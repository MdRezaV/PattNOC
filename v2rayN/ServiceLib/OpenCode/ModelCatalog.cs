namespace ServiceLib.OpenCode;

public sealed class ModelCatalog
{
    private const string Tag = "OpenCode.Catalog";

    private readonly string _cachePath;
    private readonly IActiveProxyProvider _proxyProvider;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly object _lock = new();
    private OpenCodeCatalogCache _cache = new();

    public ModelCatalog(IActiveProxyProvider proxyProvider, string? cachePath = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _proxyProvider = proxyProvider;
        _cachePath = cachePath ?? Utils.GetConfigPath("opencode_catalog_cache.json");
        _handlerFactory = handlerFactory;
        LoadCache();
    }

    public IReadOnlyList<OpenCodeModel> GetModels(string targetId)
    {
        lock (_lock)
        {
            if (_cache.ModelsByTarget.TryGetValue(targetId, out var cached) && cached.Count > 0)
            {
                return cached;
            }
        }

        return [];
    }

    public OpenCodeModel? Resolve(string? modelRef, string defaultTarget)
    {
        return ResolveTargetModel(modelRef, defaultTarget, null).Model;
    }

    public (OpenCodeTargetItem? Target, OpenCodeModel? Model) ResolveTargetModel(
        string? modelRef,
        OpenCodeItem settings)
    {
        return ResolveTargetModel(modelRef, settings.DefaultTarget, settings.Targets);
    }

    public (OpenCodeTargetItem? Target, OpenCodeModel? Model) ResolveTargetModel(
        string? modelRef,
        string defaultTarget,
        List<OpenCodeTargetItem>? targets)
    {
        if (modelRef.IsNullOrEmpty())
        {
            return (null, null);
        }

        string targetId;
        string modelId;
        var slash = modelRef.IndexOf('/');
        if (slash > 0 && slash < modelRef.Length - 1)
        {
            targetId = modelRef[..slash];
            modelId = modelRef[(slash + 1)..];
        }
        else
        {
            targetId = defaultTarget;
            modelId = modelRef;
        }

        if (targetId.IsNullOrEmpty() || modelId.IsNullOrEmpty())
        {
            return (null, null);
        }

        var target = targets?.FirstOrDefault(t => t.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase) && t.Enabled);
        var models = GetModels(targetId);
        var model = models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return (target, model);
    }

    public async Task<bool> RefreshAsync(OpenCodeTargetItem target, CancellationToken ct = default)
    {
        if (target.CatalogUrl.IsNullOrEmpty())
        {
            MarkRefreshed(target.Id);
            return true;
        }

        try
        {
            var snapshot = await _proxyProvider.TryGetSnapshotAsync(ct);
            using var client = CreateClient(snapshot?.WebProxy);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await client.GetAsync(target.CatalogUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Logging.SaveLog($"{Tag} refresh failed status={(int)response.StatusCode} target={target.Id}");
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var remoteIds = ParseModelIds(body);
            if (remoteIds.Count == 0)
            {
                Logging.SaveLog($"{Tag} refresh returned no models target={target.Id}");
                return false;
            }

            var models = new List<OpenCodeModel>(remoteIds.Count);
            foreach (var id in remoteIds)
            {
                var isFree = id.EndsWith("-free", StringComparison.OrdinalIgnoreCase);
                models.Add(new OpenCodeModel(
                    id, id,
                    ParseApiStyle(target.DefaultApiStyle),
                    null, true, true, true, false, "remote", isFree));
            }

            lock (_lock)
            {
                _cache.ModelsByTarget[target.Id] = models;
                _cache.UpdatedAt = DateTime.UtcNow;
            }

            SaveCache();
            MarkRefreshed(target.Id);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return false;
        }
    }

    private static EOpenCodeApiStyle ParseApiStyle(string? style)
    {
        return style?.Equals(nameof(EOpenCodeApiStyle.Responses), StringComparison.OrdinalIgnoreCase) == true
            ? EOpenCodeApiStyle.Responses
            : EOpenCodeApiStyle.ChatCompletions;
    }

    private HttpClient CreateClient(IWebProxy? proxy)
    {
        if (_handlerFactory is null)
        {
            return ProxyHttpClientFactory.Create(proxy, connectTimeoutSeconds: 10);
        }

        var handler = _handlerFactory();
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static List<string> ParseModelIds(string body)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ids;
            }

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (id.IsNotEmpty())
                        {
                            ids.Add(id);
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        var id = item.GetString();
                        if (id.IsNotEmpty())
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }

        return ids;
    }

    private void MarkRefreshed(string targetId)
    {
        lock (_lock)
        {
            _cache.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            var config = AppManager.Instance.Config?.OpenCodeItem;
            var target = config?.Targets?.FirstOrDefault(t => t.Id == targetId);
            if (target is not null)
            {
                target.LastCatalogRefresh = DateTime.UtcNow;
            }
        }
        catch
        {
            // Display-only metadata; never fail refresh over it.
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var content = File.ReadAllText(_cachePath);
            var cache = JsonUtils.Deserialize<OpenCodeCatalogCache>(content);
            if (cache is not null)
            {
                // Remove any legacy "default" source entries; remote is the single source of truth.
                foreach (var kvp in cache.ModelsByTarget)
                {
                    kvp.Value.RemoveAll(m => m.Source.Equals("default", StringComparison.OrdinalIgnoreCase));
                }

                lock (_lock)
                {
                    _cache = cache;
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    private void SaveCache()
    {
        try
        {
            OpenCodeCatalogCache snapshot;
            lock (_lock)
            {
                snapshot = _cache;
            }

            var content = JsonUtils.Serialize(snapshot, true);
            File.WriteAllText(_cachePath, content);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }
}
