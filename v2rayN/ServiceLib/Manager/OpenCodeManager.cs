namespace ServiceLib.OpenCode;

public sealed class OpenCodeManager
{
    private const string Tag = "OpenCode";

    private static readonly Lazy<OpenCodeManager> _instance = new(() => new OpenCodeManager());
    public static OpenCodeManager Instance => _instance.Value;

    private readonly object _lock = new();
    private OpenCodeItem _settings = OpenCodeConfigDefaults.Create();
    private ModelCatalog? _catalog;
    private RequestExecutor? _executor;
    private ConnectivityTester? _tester;
    private GatewayService? _gateway;
    private OpenCodeTelemetry _telemetry = new();

    public OpenCodeTelemetry Telemetry
    {
        get
        {
            lock (_lock)
            {
                return _telemetry;
            }
        }
    }

    public bool IsGatewayRunning => _gateway?.IsRunning == true;

    public string? GatewayLastError => _gateway?.LastError;

    public bool GatewayLastErrorIsPortInUse => _gateway?.LastErrorIsPortInUse == true;

    public void Init()
    {
        lock (_lock)
        {
            try
            {
                _settings = AppManager.Instance.Config?.OpenCodeItem ?? OpenCodeConfigDefaults.Create();
                OpenCodeConfigDefaults.Normalize(_settings);

                _telemetry = new OpenCodeTelemetry();
                var proxyProvider = new ActiveProxyProvider();
                _catalog = new ModelCatalog(proxyProvider);
                _executor = new RequestExecutor(proxyProvider, _catalog, _telemetry);
                _tester = new ConnectivityTester(_executor);
                _gateway = new GatewayService(_executor, _catalog, _telemetry, GetSettingsSnapshot);
            }
            catch (Exception ex)
            {
                Logging.SaveLog(Tag, ex);
            }
        }
    }

    public async Task StartGatewayAsync()
    {
        try
        {
            EnsureInitialized();
            var settings = GetSettingsSnapshot();
            if (!settings.Enabled || !settings.GatewayEnabled)
            {
                return;
            }

            GatewayService? gateway;
            lock (_lock)
            {
                gateway = _gateway;
            }

            if (gateway is null)
            {
                return;
            }

            if (!await gateway.StartAsync())
            {
                Logging.SaveLog($"{Tag} gateway start failed: {gateway.LastError}");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        try
        {
            GatewayService? gateway;
            lock (_lock)
            {
                gateway = _gateway;
            }

            if (gateway is not null)
            {
                await gateway.StopAsync(timeout);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    public OpenCodeGatewayStatus GetStatus()
    {
        try
        {
            EnsureInitialized();
            var settings = GetSettingsSnapshot();
            var gateway = _gateway;
            var running = gateway?.IsRunning == true;
            var port = gateway?.BoundPort is > 0 ? gateway!.BoundPort : settings.GatewayPort;
            var endpoint = $"http://{(settings.GatewayHost.IsNullOrEmpty() ? Global.Loopback : settings.GatewayHost)}:{port}/v1";
            string? error = null;
            if (gateway?.LastError is not null)
            {
                error = gateway.LastErrorIsPortInUse
                    ? ResUI.OpenCodeGatewayPortInUse
                    : $"{ResUI.OpenCodeGatewayStartFailed}: {gateway.LastError}";
            }

            return new OpenCodeGatewayStatus(
                running,
                settings.GatewayHost.IsNullOrEmpty() ? Global.Loopback : settings.GatewayHost,
                port,
                endpoint,
                error);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return new OpenCodeGatewayStatus(false, Global.Loopback, 0, "", ex.Message);
        }
    }

    public async Task<ConnectivityTestResult> TestConnectionAsync(
        string? targetId = null,
        string? modelRef = null,
        CancellationToken ct = default)
    {
        try
        {
            EnsureInitialized();
            var settings = GetSettingsSnapshot();
            var tester = _tester;
            if (tester is null)
            {
                return new ConnectivityTestResult(
                    EOpenCodeConnectivityState.Unknown, "OpenCode is not initialized.");
            }

            var result = await tester.TestAsync(settings, targetId, modelRef, ct);
            await StoreTestResultAsync(targetId ?? settings.DefaultTarget, result);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return new ConnectivityTestResult(EOpenCodeConnectivityState.Unknown, ex.Message);
        }
    }

    public async Task<bool> RefreshModelsAsync(string? targetId = null, CancellationToken ct = default)
    {
        try
        {
            EnsureInitialized();
            var settings = GetSettingsSnapshot();
            var id = targetId ?? settings.DefaultTarget;
            var target = settings.Targets?.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }

            var catalog = _catalog;
            if (catalog is null)
            {
                return false;
            }

            return await catalog.RefreshAsync(target, ct);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return false;
        }
    }

    public IReadOnlyList<OpenCodeModel> GetModels(string? targetId = null)
    {
        try
        {
            EnsureInitialized();
            var settings = GetSettingsSnapshot();
            var catalog = _catalog;
            if (catalog is null)
            {
                return [];
            }

            return catalog.GetModels(targetId ?? settings.DefaultTarget);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return [];
        }
    }

    public OpenCodeTelemetrySnapshot GetTelemetrySnapshot()
    {
        try
        {
            return Telemetry.GetSnapshot();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return new OpenCodeTelemetrySnapshot(0, 0, 0, 0, 0, 0, null, null, null);
        }
    }

    public void SaveSettings(OpenCodeItem settings)
    {
        try
        {
            OpenCodeConfigDefaults.Normalize(settings);
            lock (_lock)
            {
                _settings = settings;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    private OpenCodeItem GetSettingsSnapshot()
    {
        lock (_lock)
        {
            return _settings;
        }
    }

    private void EnsureInitialized()
    {
        if (_executor is null || _catalog is null || _gateway is null)
        {
            Init();
        }
    }

    private async Task StoreTestResultAsync(string targetId, ConnectivityTestResult result)
    {
        try
        {
            var settings = AppManager.Instance.Config?.OpenCodeItem;
            var target = settings?.Targets?.FirstOrDefault(t => t.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return;
            }

            target.LastTestState = result.State.ToString();
            target.LastError = result.State == EOpenCodeConnectivityState.OpenCodeAccepted
                ? null
                : result.Detail;
            await ConfigHandler.SaveConfig(AppManager.Instance.Config);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }
}
