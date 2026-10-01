namespace ServiceLib.OpenCode;

public static class TargetCatalogDefaults
{
    public const string OpenCodeFreeTargetId = "opencode-free";
    public const string OpenCodeFreeName = "OpenCode Free";
    public const string OpenCodeFreeBaseUrl = "https://opencode.ai/zen/v1";
    public const string OpenCodeFreeCatalogUrl = "https://opencode.ai/zen/v1/models";

    public static List<OpenCodeTargetItem> CreateDefaultTargets() =>
    [
        new()
        {
            Id = OpenCodeFreeTargetId,
            Name = OpenCodeFreeName,
            BaseUrl = OpenCodeFreeBaseUrl,
            CatalogUrl = OpenCodeFreeCatalogUrl,
            KeyOptional = true,
            Enabled = true,
        },
    ];

    // Free-priced OpenCode Zen models. ApiStyle is per-model metadata (Chat Completions for these).
    public static List<OpenCodeModel> CreateFreeModels() =>
    [
        new("big-pickle", "Big Pickle", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("mimo-v2.5-free", "MiMo V2.5 Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("mimo-v2.6-flash-free", "MiMo V2.6 Flash Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("space-bunny-free", "Space Bunny Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("longcat-2.5-preview-free", "LongCat 2.5 Preview Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("ling-3.0-flash-fin-free", "Ling 3.0 Flash Fin Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("nemotron-3-ultra-free", "Nemotron 3 Ultra Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("nemotron-3.5-lightning-free", "Nemotron 3.5 Lightning Free", EOpenCodeApiStyle.ChatCompletions, null, true, true, true, false, "default", true),
        new("muse-spark-1.3-contributor-free", "Muse Spark 1.3 Contributor Free", EOpenCodeApiStyle.Responses, null, true, true, true, false, "default", true),
    ];
}

public static class OpenCodeConfigDefaults
{
    public const int DefaultGatewayPort = 10651;
    public const int MinGatewayPort = 1;
    public const int MaxGatewayPort = 65535;

    public static OpenCodeItem Create() => new()
    {
        Enabled = false,
        GatewayEnabled = true,
        DefaultTarget = TargetCatalogDefaults.OpenCodeFreeTargetId,
        DefaultModel = "big-pickle",
        GatewayHost = Global.Loopback,
        GatewayPort = DefaultGatewayPort,
        ConnectTimeoutSeconds = 10,
        RequestTimeoutSeconds = 120,
        MaxRetry = 1,
        MaxConcurrentRequests = 8,
        Targets = TargetCatalogDefaults.CreateDefaultTargets(),
    };

    public static void Normalize(OpenCodeItem item)
    {
        item.Targets ??= [];
        if (item.Targets.Count == 0)
        {
            item.Targets = TargetCatalogDefaults.CreateDefaultTargets();
        }

        foreach (var target in item.Targets)
        {
            target.Enabled = true;
            target.KeyOptional = true;
        }

        if (item.Targets.All(t => !t.Id.Equals(item.DefaultTarget, StringComparison.OrdinalIgnoreCase)))
        {
            item.DefaultTarget = item.Targets.First().Id;
        }

        if (item.DefaultModel.IsNullOrEmpty())
        {
            item.DefaultModel = "big-pickle";
        }

        if (item.GatewayHost.IsNullOrEmpty())
        {
            item.GatewayHost = Global.Loopback;
        }

        if (item.GatewayPort < MinGatewayPort || item.GatewayPort > MaxGatewayPort)
        {
            item.GatewayPort = DefaultGatewayPort;
        }

        if (item.ConnectTimeoutSeconds < 1)
        {
            item.ConnectTimeoutSeconds = 10;
        }

        if (item.RequestTimeoutSeconds < 5)
        {
            item.RequestTimeoutSeconds = 120;
        }

        if (item.MaxRetry < 0)
        {
            item.MaxRetry = 0;
        }
        else if (item.MaxRetry > 3)
        {
            item.MaxRetry = 3;
        }

        if (item.MaxConcurrentRequests < 1)
        {
            item.MaxConcurrentRequests = 8;
        }
        else if (item.MaxConcurrentRequests > 64)
        {
            item.MaxConcurrentRequests = 64;
        }
    }
}

public static class OpenCodeUrl
{
    public static string Combine(string? baseUrl, string path)
    {
        var baseTrimmed = (baseUrl ?? string.Empty).TrimEnd('/');
        var pathNormalized = path.StartsWith('/') ? path : "/" + path;
        return baseTrimmed + pathNormalized;
    }
}
