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
        DefaultModel = "",
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
            item.DefaultModel = "";
        }

        item.TestModelOrder ??= [];
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        item.TestModelOrder = item.TestModelOrder
            .Where(id => id.IsNotEmpty() && seenModels.Add(id))
            .ToList();
        if (item.TestModelOrder.Count > 0)
        {
            item.DefaultModel = item.TestModelOrder[0];
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
