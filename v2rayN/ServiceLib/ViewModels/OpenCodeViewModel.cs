namespace ServiceLib.ViewModels;

public partial class OpenCodeViewModel : MyReactiveObject, ICloseable
{
    private const string Tag = "OpenCodeViewModel";

    public event EventHandler? RequestClose;

    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RefreshModelsCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> TestConnectionCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveApiKeyCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearApiKeyCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> MovePriorityUpCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> MovePriorityDownCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemovePriorityCmd { get; }

    public BulkObservableCollection<OpenCodeModelRow> ModelRows { get; } = [];
    public BulkObservableCollection<OpenCodeModelRow> PriorityRows { get; } = [];
    private List<OpenCodeModelRow> _allModelRows = [];
    private readonly List<string> _modelOrder = [];

    private string _originalApiKey = "";
    private bool _syncingSelection;

    [Reactive] public partial bool Enabled { get; set; }
    [Reactive] public partial bool GatewayEnabled { get; set; }
    [Reactive] public partial string DefaultTarget { get; set; }
    [Reactive] public partial string DefaultModel { get; set; }
    [Reactive] public partial string GatewayHost { get; set; }
    [Reactive] public partial int GatewayPort { get; set; }
    [Reactive] public partial int ConnectTimeoutSeconds { get; set; }
    [Reactive] public partial int RequestTimeoutSeconds { get; set; }
    [Reactive] public partial int MaxRetry { get; set; }
    [Reactive] public partial int MaxConcurrentRequests { get; set; }

    [Reactive] public partial string TargetName { get; set; }
    [Reactive] public partial string TargetBaseUrl { get; set; }
    [Reactive] public partial string TargetCatalogUrl { get; set; }
    [Reactive] public partial string ApiKeyInput { get; set; }
    [Reactive] public partial bool ApiKeySet { get; set; }

    [Reactive] public partial string GatewayStatusText { get; set; }
    [Reactive] public partial string EndpointText { get; set; }
    [Reactive] public partial string ProfileStatusText { get; set; }
    [Reactive] public partial string ProxyStatusText { get; set; }
    [Reactive] public partial string EgressIpText { get; set; }
    [Reactive] public partial string TestResultText { get; set; }
    [Reactive] public partial string TelemetryText { get; set; }
    [Reactive] public partial string ClientExampleText { get; set; }

    [Reactive] public partial string ModelFilter { get; set; }
    [Reactive] public partial bool FilterFreeOnly { get; set; }
    [Reactive] public partial int ModelApiStyleFilterIndex { get; set; }
    [Reactive] public partial int ModelSortIndex { get; set; }
    [Reactive] public partial OpenCodeModelRow? SelectedModelRow { get; set; }
    [Reactive] public partial OpenCodeModelRow? SelectedPriorityRow { get; set; }
    [Reactive] public partial bool HasPriorityRows { get; set; }
    [Reactive] public partial string SelectedModelDisplay { get; set; }
    [Reactive] public partial string ModelCountDisplay { get; set; }

    public OpenCodeViewModel()
    {
        _config = AppManager.Instance.Config;
        _config.OpenCodeItem ??= OpenCodeConfigDefaults.Create();
        OpenCodeConfigDefaults.Normalize(_config.OpenCodeItem);

        SaveCmd = ReactiveCommand.CreateFromTask(SaveAndCloseAsync);
        RefreshModelsCmd = ReactiveCommand.CreateFromTask(RefreshModelsAsync);
        TestConnectionCmd = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveApiKeyCmd = ReactiveCommand.CreateFromTask(SaveApiKeyAsync);
        ClearApiKeyCmd = ReactiveCommand.CreateFromTask(ClearApiKeyAsync);
        MovePriorityUpCmd = ReactiveCommand.Create(() => MovePriority(-1));
        MovePriorityDownCmd = ReactiveCommand.Create(() => MovePriority(1));
        RemovePriorityCmd = ReactiveCommand.Create(RemovePriority);

        foreach (var cmd in new[]
                 {
                     SaveCmd, RefreshModelsCmd, TestConnectionCmd, SaveApiKeyCmd, ClearApiKeyCmd,
                     MovePriorityUpCmd, MovePriorityDownCmd, RemovePriorityCmd,
                 })
        {
            cmd.ThrownExceptions.Subscribe(ex =>
            {
                Logging.SaveLog(Tag, ex);
                NoticeManager.Instance.Enqueue(ex.Message);
            });
        }

        LoadFromConfig();

        this.WhenAnyValue(x => x.Enabled)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.GatewayEnabled)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.DefaultModel)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (_syncingSelection)
                {
                    return;
                }

                MoveDefaultModelToFront();
            });
        this.WhenAnyValue(x => x.GatewayHost)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.GatewayPort)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.ConnectTimeoutSeconds)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.RequestTimeoutSeconds)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.MaxRetry)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.MaxConcurrentRequests)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.TargetName)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.TargetBaseUrl)
            .Skip(1)
            .Subscribe(_ => PersistSettings());
        this.WhenAnyValue(x => x.TargetCatalogUrl)
            .Skip(1)
            .Subscribe(_ => PersistSettings());

        this.WhenAnyValue(x => x.DefaultTarget)
            .Skip(1)
            .Subscribe(x => { _ = LoadModelsAsync(); });

        this.WhenAnyValue(x => x.ModelFilter)
            .Subscribe(_ => ApplyModelFilterAndSort());
        this.WhenAnyValue(x => x.FilterFreeOnly)
            .Subscribe(_ =>
            {
                PersistSettings();
                ApplyModelFilterAndSort();
            });
        this.WhenAnyValue(x => x.ModelApiStyleFilterIndex)
            .Subscribe(_ => ApplyModelFilterAndSort());
        this.WhenAnyValue(x => x.ModelSortIndex)
            .Subscribe(_ => ApplyModelFilterAndSort());

        _ = LoadStatusAsync();
        _ = LoadModelsAsync();
    }

    private void LoadFromConfig()
    {
        var item = _config.OpenCodeItem!;
        Enabled = item.Enabled;
        GatewayEnabled = item.GatewayEnabled;
        FilterFreeOnly = item.FreeOnly;
        DefaultTarget = item.DefaultTarget;
        DefaultModel = item.DefaultModel;
        _modelOrder.Clear();
        _modelOrder.AddRange(item.TestModelOrder.Count == 0 && item.DefaultModel.IsNotEmpty()
            ? [item.DefaultModel]
            : item.TestModelOrder);
        GatewayHost = item.GatewayHost;
        GatewayPort = item.GatewayPort;
        ConnectTimeoutSeconds = item.ConnectTimeoutSeconds;
        RequestTimeoutSeconds = item.RequestTimeoutSeconds;
        MaxRetry = item.MaxRetry;
        MaxConcurrentRequests = item.MaxConcurrentRequests;

        var target = GetTarget(item);
        TargetName = target?.Name ?? "";
        TargetBaseUrl = target?.BaseUrl ?? "";
        TargetCatalogUrl = target?.CatalogUrl ?? "";
        _originalApiKey = target?.ApiKey ?? "";
        ApiKeyInput = "";
        ApiKeySet = _originalApiKey.IsNotEmpty();

        ClientExampleText =
            $"OpenAI base URL: http://{item.GatewayHost}:{item.GatewayPort}/v1\n" +
            $"Claude Code ANTHROPIC_BASE_URL: http://{item.GatewayHost}:{item.GatewayPort}/claude\n" +
            $"Model: claude-{item.DefaultModel}";
    }

    private OpenCodeTargetItem? GetTarget(OpenCodeItem item)
    {
        return item.Targets?.FirstOrDefault(t =>
                   t.Id.Equals(item.DefaultTarget, StringComparison.OrdinalIgnoreCase))
               ?? item.Targets?.FirstOrDefault();
    }

    private void PersistSettings()
    {
        try
        {
            var item = _config.OpenCodeItem!;
            item.Enabled = Enabled;
            item.GatewayEnabled = GatewayEnabled;
            item.FreeOnly = FilterFreeOnly;
            item.DefaultTarget = DefaultTarget;
            item.DefaultModel = DefaultModel;
            item.TestModelOrder = [.. _modelOrder];
            item.GatewayHost = GatewayHost;
            item.GatewayPort = GatewayPort;
            item.ConnectTimeoutSeconds = ConnectTimeoutSeconds;
            item.RequestTimeoutSeconds = RequestTimeoutSeconds;
            item.MaxRetry = MaxRetry;
            item.MaxConcurrentRequests = MaxConcurrentRequests;

            var target = GetTarget(item);
            if (target is not null)
            {
                target.Name = TargetName;
                target.BaseUrl = TargetBaseUrl;
                target.CatalogUrl = TargetCatalogUrl.IsNullOrEmpty() ? null : TargetCatalogUrl;
            }

            OpenCodeConfigDefaults.Normalize(item);
            if (!DefaultModel.Equals(item.DefaultModel))
            {
                _syncingSelection = true;
                try
                {
                    DefaultModel = item.DefaultModel;
                }
                finally
                {
                    _syncingSelection = false;
                }
            }

            OpenCodeManager.Instance.SaveSettings(item);
            _ = ConfigHandler.SaveConfig(_config);

            ClientExampleText =
                $"OpenAI base URL: http://{item.GatewayHost}:{item.GatewayPort}/v1\n" +
                $"Claude Code ANTHROPIC_BASE_URL: http://{item.GatewayHost}:{item.GatewayPort}/claude\n" +
                $"Model: claude-{item.DefaultModel}";
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    private async Task SaveAndCloseAsync()
    {
        PersistSettings();
        if (Enabled && GatewayEnabled)
        {
            await OpenCodeManager.Instance.StopAsync(TimeSpan.FromSeconds(2));
            await OpenCodeManager.Instance.StartGatewayAsync();
        }
        else
        {
            await OpenCodeManager.Instance.StopAsync(TimeSpan.FromSeconds(2));
        }

        if (await ConfigHandler.SaveConfig(_config) == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OpenCodeSaved);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    private async Task RefreshModelsAsync()
    {
        PersistSettings();
        var ok = await OpenCodeManager.Instance.RefreshModelsAsync(DefaultTarget);
        await LoadModelsAsync();
        NoticeManager.Instance.Enqueue(ok ? ResUI.OpenCodeRefreshDone : ResUI.OpenCodeRefreshFailed);
    }

    private async Task TestConnectionAsync()
    {
        PersistSettings();
        TestResultText = ResUI.OpenCodeStateTesting;
        var result = await OpenCodeManager.Instance.TestConnectionAsync(DefaultTarget, DefaultModel);
        TestResultText = $"{GetStateText(result.State)} | {result.Detail}";
        await LoadStatusAsync();
    }

    private async Task SaveApiKeyAsync()
    {
        if (ApiKeyInput.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.OpenCodeApiKeyOptionalHint);
            return;
        }

        var item = _config.OpenCodeItem!;
        var target = GetTarget(item);
        if (target is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }

        target.ApiKey = ApiKeyInput;
        _originalApiKey = ApiKeyInput;
        ApiKeyInput = "";
        ApiKeySet = true;
        await ConfigHandler.SaveConfig(_config);
        NoticeManager.Instance.Enqueue(ResUI.OpenCodeSaved);
    }

    private async Task ClearApiKeyAsync()
    {
        var item = _config.OpenCodeItem!;
        var target = GetTarget(item);
        if (target is not null)
        {
            target.ApiKey = null;
        }

        _originalApiKey = "";
        ApiKeyInput = "";
        ApiKeySet = false;
        await ConfigHandler.SaveConfig(_config);
        NoticeManager.Instance.Enqueue(ResUI.OpenCodeSaved);
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var models = OpenCodeManager.Instance.GetModels(DefaultTarget);
            _allModelRows = models.Select(m => new OpenCodeModelRow(m)).ToList();
            foreach (var row in _allModelRows)
            {
                row.WhenAnyValue(r => r.IsSelected)
                    .Skip(1)
                    .Subscribe(_ => OnRowIsSelectedChanged(row));
            }

            _syncingSelection = true;
            try
            {
                if (_allModelRows.Count > 0)
                {
                    // Models that left the catalog can no longer be tested. Never prune on an
                    // empty catalog — that would silently drop a saved order.
                    _modelOrder.RemoveAll(id =>
                        _allModelRows.All(r => !r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
                }

                RebuildSelectionState();
                SelectedModelRow = _allModelRows.FirstOrDefault(r => r.IsSelected);
            }
            finally
            {
                _syncingSelection = false;
            }

            ApplyModelFilterAndSort();
            UpdateSelectedModelDisplay();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }

        await Task.CompletedTask;
    }

    private void RebuildSelectionState()
    {
        foreach (var row in _allModelRows)
        {
            var index = _modelOrder.FindIndex(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
            row.IsSelected = index >= 0;
            row.Priority = index >= 0 ? index + 1 : 0;
        }

        var previousId = SelectedPriorityRow?.Id;
        PriorityRows.ReplaceRange(_modelOrder
            .Select(id => _allModelRows.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<OpenCodeModelRow>());
        HasPriorityRows = PriorityRows.Count > 0;
        SelectedPriorityRow = previousId is null
            ? PriorityRows.FirstOrDefault()
            : PriorityRows.FirstOrDefault(r => r.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase))
              ?? PriorityRows.FirstOrDefault();
    }

    private void OnRowIsSelectedChanged(OpenCodeModelRow row)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (row.IsSelected)
        {
            if (!_modelOrder.Any(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)))
            {
                _modelOrder.Add(row.Id);
            }
        }
        else
        {
            _modelOrder.RemoveAll(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase));
        }

        CommitModelOrder();
    }

    private void CommitModelOrder()
    {
        _syncingSelection = true;
        try
        {
            foreach (var row in _allModelRows)
            {
                row.Priority = 0;
                row.IsSelected = false;
            }

            RebuildSelectionState();
            DefaultModel = _modelOrder.Count > 0 ? _modelOrder[0] : string.Empty;
        }
        finally
        {
            _syncingSelection = false;
        }

        PersistSettings();
        UpdateSelectedModelDisplay();
    }

    private void MovePriority(int offset)
    {
        var id = SelectedPriorityRow?.Id;
        var index = id is null ? -1 : _modelOrder.FindIndex(m => m.Equals(id, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _modelOrder.Count)
        {
            return;
        }

        (_modelOrder[index], _modelOrder[target]) = (_modelOrder[target], _modelOrder[index]);
        CommitModelOrder();
    }

    private void RemovePriority()
    {
        var id = SelectedPriorityRow?.Id;
        if (id is null)
        {
            return;
        }

        if (_modelOrder.RemoveAll(m => m.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        CommitModelOrder();
    }

    private void MoveDefaultModelToFront()
    {
        if (DefaultModel.IsNullOrEmpty())
        {
            if (_modelOrder.Count > 0)
            {
                _modelOrder.Clear();
                CommitModelOrder();
            }

            return;
        }

        // Only models from the catalog can carry a priority; partially typed ids are ignored.
        if (_allModelRows.All(r => !r.Id.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _modelOrder.RemoveAll(m => m.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase));
        _modelOrder.Insert(0, DefaultModel);
        CommitModelOrder();
    }

    private void ApplyModelFilterAndSort()
    {
        try
        {
            IEnumerable<OpenCodeModelRow> rows = _allModelRows;

            if (FilterFreeOnly)
            {
                rows = rows.Where(r => r.IsFree);
            }

            var filter = ModelFilter;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                rows = rows.Where(r =>
                    r.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    r.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            rows = ModelApiStyleFilterIndex switch
            {
                1 => rows.Where(r => r.ApiStyle == nameof(EOpenCodeApiStyle.ChatCompletions)),
                2 => rows.Where(r => r.ApiStyle == nameof(EOpenCodeApiStyle.Responses)),
                _ => rows,
            };

            rows = ModelSortIndex switch
            {
                1 => rows.OrderByDescending(r => r.DisplayName),
                2 => rows.OrderBy(r => r.ApiStyle).ThenBy(r => r.DisplayName),
                3 => rows.OrderByDescending(r => r.IsFree).ThenBy(r => r.DisplayName),
                4 => rows.OrderBy(r => r.Source).ThenBy(r => r.DisplayName),
                _ => rows.OrderBy(r => r.DisplayName),
            };

            var list = rows.ToList();
            ModelRows.ReplaceRange(list);
            ModelCountDisplay = string.Format(ResUI.OpenCodeModelCount, list.Count, _allModelRows.Count);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    private void UpdateSelectedModelDisplay()
    {
        var row = _allModelRows.FirstOrDefault(r => r.Id.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase));
        SelectedModelDisplay = row is not null
            ? $"{row.Id} — {row.DisplayName}"
            : DefaultModel;
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            var status = OpenCodeManager.Instance.GetStatus();
            GatewayStatusText = status.IsRunning
                ? ResUI.OpenCodeGatewayRunning
                : (status.Error ?? ResUI.OpenCodeGatewayStopped);
            EndpointText = status.Endpoint;

            var provider = new ActiveProxyProvider();
            var snapshot = await provider.TryGetSnapshotAsync();
            ProfileStatusText = snapshot?.ProfileRemark ?? ResUI.OpenCodeProfileNone;
            ProxyStatusText = snapshot is null
                ? ResUI.OpenCodeProxyUnavailable
                : string.Format(ResUI.OpenCodeProxyPort, snapshot.SocksPort);

            // Configuration egress IP via local core only — never the first-hop proxy.
            var localProxy = await AppProxyResolver.Instance.ResolveLocalCoreOnlyAsync();
            if (localProxy is not null)
            {
                var ipInfo = await ConnectionHandler.GetIPInfo(localProxy);
                EgressIpText = ipInfo is { } info
                    ? $"{info.Ip} {info.Country}"
                    : Global.None;
            }
            else
            {
                EgressIpText = Global.None;
            }

            var telemetry = OpenCodeManager.Instance.GetTelemetrySnapshot();
            TelemetryText =
                $"total={telemetry.TotalRequests} ok={telemetry.Success} fail={telemetry.Failed} " +
                $"retry={telemetry.Retries} stream={telemetry.StreamingRequests} tools={telemetry.ToolCallRequests}" +
                (telemetry.LastFailureSummary.IsNullOrEmpty()
                    ? ""
                    : $"\nlast failure: {telemetry.LastFailureSummary}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    private static string GetStateText(EOpenCodeConnectivityState state)
    {
        return state switch
        {
            EOpenCodeConnectivityState.NotTested => ResUI.OpenCodeStateNotTested,
            EOpenCodeConnectivityState.Testing => ResUI.OpenCodeStateTesting,
            EOpenCodeConnectivityState.OpenCodeAccepted => ResUI.OpenCodeStateAccepted,
            EOpenCodeConnectivityState.RateLimited => ResUI.OpenCodeStateRateLimited,
            EOpenCodeConnectivityState.FreeUsageLimit => ResUI.OpenCodeStateFreeUsageLimit,
            EOpenCodeConnectivityState.AuthenticationFailed => ResUI.OpenCodeStateAuthenticationFailed,
            EOpenCodeConnectivityState.AuthorizationFailed => ResUI.OpenCodeStateAuthorizationFailed,
            EOpenCodeConnectivityState.ModelNotFound => ResUI.OpenCodeStateModelNotFound,
            EOpenCodeConnectivityState.ProviderError => ResUI.OpenCodeStateProviderError,
            EOpenCodeConnectivityState.NetworkError => ResUI.OpenCodeStateNetworkError,
            EOpenCodeConnectivityState.Timeout => ResUI.OpenCodeStateTimeout,
            EOpenCodeConnectivityState.ClientRestricted => ResUI.OpenCodeStateClientRestricted,
            EOpenCodeConnectivityState.UnsupportedRequest => ResUI.OpenCodeStateUnsupportedRequest,
            _ => ResUI.OpenCodeStateUnknown,
        };
    }
}

public sealed partial class OpenCodeModelRow : MyReactiveObject
{
    public OpenCodeModelRow(OpenCodeModel model)
    {
        Model = model;
    }

    public OpenCodeModel Model { get; }

    public string Id => Model.Id;
    public string DisplayName => Model.DisplayName;
    public string ApiStyle => Model.ApiStyle.ToString();
    public bool IsFree => Model.IsFree;
    public string Source => Model.Source;
    public bool SupportsTools => Model.SupportsTools;
    public bool SupportsStreaming => Model.SupportsStreaming;

    [Reactive] public partial bool IsSelected { get; set; }
    [Reactive] public partial int Priority { get; set; }
}
