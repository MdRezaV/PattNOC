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

    public BulkObservableCollection<OpenCodeModelRow> ModelRows { get; } = [];
    private List<OpenCodeModelRow> _allModelRows = [];

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

        foreach (var cmd in new[]
                 {
                     SaveCmd, RefreshModelsCmd, TestConnectionCmd, SaveApiKeyCmd, ClearApiKeyCmd,
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

                PersistSettings();
                SyncSelectionFromDefaultModel();
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

        this.WhenAnyValue(x => x.SelectedModelRow)
            .Where(r => r is not null)
            .Subscribe(r =>
            {
                if (_syncingSelection)
                {
                    return;
                }

                _syncingSelection = true;
                try
                {
                    if (!DefaultModel.Equals(r!.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        DefaultModel = r.Id;
                    }

                    foreach (var row in _allModelRows)
                    {
                        row.IsSelected = row.Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase);
                    }

                    UpdateSelectedModelDisplay();
                }
                finally
                {
                    _syncingSelection = false;
                }
            });

        this.WhenAnyValue(x => x.ModelFilter)
            .Subscribe(_ => ApplyModelFilterAndSort());
        this.WhenAnyValue(x => x.FilterFreeOnly)
            .Subscribe(_ => ApplyModelFilterAndSort());
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
        DefaultTarget = item.DefaultTarget;
        DefaultModel = item.DefaultModel;
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
            $"Base URL: http://{item.GatewayHost}:{item.GatewayPort}/v1\n" +
            $"Model: {item.DefaultModel}";
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
            item.DefaultTarget = DefaultTarget;
            item.DefaultModel = DefaultModel;
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
            OpenCodeManager.Instance.SaveSettings(item);
            _ = ConfigHandler.SaveConfig(_config);

            ClientExampleText =
                $"Base URL: http://{item.GatewayHost}:{item.GatewayPort}/v1\n" +
                $"Model: {item.DefaultModel}";
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

            _syncingSelection = true;
            try
            {
                foreach (var row in _allModelRows)
                {
                    row.IsSelected = row.Id.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase);
                }

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

    private void SyncSelectionFromDefaultModel()
    {
        _syncingSelection = true;
        try
        {
            foreach (var row in _allModelRows)
            {
                row.IsSelected = row.Id.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase);
            }

            SelectedModelRow = _allModelRows.FirstOrDefault(r => r.IsSelected);
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateSelectedModelDisplay();
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
}
