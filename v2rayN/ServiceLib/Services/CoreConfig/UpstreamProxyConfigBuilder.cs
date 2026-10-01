using System.Text.Json.Nodes;

namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Builds the app-upstream outbound for Xray/sing-box generated configs and
/// applies dialerProxy/detour on non-private proxy outbounds.
/// PattN global first-hop proxy — unrelated to FullConfigTemplateItem.ProxyDetour.
/// </summary>
internal static class UpstreamProxyConfigBuilder
{
    public static string Tag => Global.UpstreamProxyTag;

    public static Outbounds4Ray? BuildXrayOutbound(UpstreamProxyItem item)
    {
        if (item is null || !item.IsUsable())
        {
            return null;
        }
        // Xray http outbound has no TLS-to-proxy field; Https falls back to plain http.
        var protocol = item.ProxyType == EUpstreamProxyType.Socks5 ? "socks" : "http";
        var hasCredentials = !item.Username.IsNullOrEmpty();
        return new Outbounds4Ray
        {
            tag = Tag,
            protocol = protocol,
            settings = new Outboundsettings4Ray
            {
                address = item.Server,
                port = item.Port,
                user = hasCredentials ? item.Username : null,
                pass = hasCredentials ? item.Password : null,
                email = hasCredentials ? Global.UserEMail : null,
            },
            streamSettings = new StreamSettings4Ray(),
        };
    }

    public static Outbound4Sbox? BuildSingboxOutbound(UpstreamProxyItem item)
    {
        if (item is null || !item.IsUsable())
        {
            return null;
        }
        var outbound = new Outbound4Sbox
        {
            tag = Tag,
            type = item.ProxyType == EUpstreamProxyType.Socks5 ? "socks" : "http",
            server = item.Server,
            server_port = item.Port,
        };
        if (item.ProxyType == EUpstreamProxyType.Socks5)
        {
            outbound.version = "5";
        }
        if (!item.Username.IsNullOrEmpty())
        {
            outbound.username = item.Username;
        }
        if (!item.Password.IsNullOrEmpty())
        {
            outbound.password = item.Password;
        }
        if (item.ProxyType == EUpstreamProxyType.Https)
        {
            outbound.tls = new Tls4Sbox { enabled = true };
        }
        return outbound;
    }

    public static string ApplyXrayGlobalFirstHop(string coreConfigContent, UpstreamProxyItem? item)
    {
        if (item is null || !item.IsUsable())
        {
            return coreConfigContent;
        }
        if (JsonUtils.ParseJson(coreConfigContent) is not JsonObject cfg)
        {
            return coreConfigContent;
        }
        if (cfg["outbounds"] is not JsonArray outbounds)
        {
            return coreConfigContent;
        }

        var tag = Tag;
        var takenTags = outbounds.OfType<JsonObject>()
            .Select(o => o["tag"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (!takenTags.Contains(tag))
        {
            var upstream = BuildXrayOutbound(item);
            if (upstream is null)
            {
                return coreConfigContent;
            }
            var upstreamNode = JsonUtils.ParseJson(JsonUtils.Serialize(upstream));
            if (upstreamNode is JsonObject upstreamObj)
            {
                outbounds.Insert(0, upstreamObj);
            }
        }

        // Outbounds referenced as dialerProxy targets must not themselves be detoured
        var detourTargets = outbounds.OfType<JsonObject>()
            .Select(o => o["streamSettings"]?["sockopt"]?["dialerProxy"]?.ToString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var outbound in outbounds.OfType<JsonObject>())
        {
            var t = outbound["tag"]?.ToString();
            if (t is null || t == tag)
            {
                continue;
            }
            var protocol = outbound["protocol"]?.ToString()?.ToLowerInvariant();
            if (protocol is "freedom" or "dns" or "blackhole" or "block" or "loopback")
            {
                continue;
            }
            if (detourTargets.Contains(t))
            {
                continue;
            }
            var address = ExtractXrayServerAddress(outbound, protocol);
            if (address.IsNullOrEmpty() || Utils.IsPrivateNetwork(address))
            {
                continue;
            }

            var current = outbound["streamSettings"]?["sockopt"]?["dialerProxy"]?.ToString();
            if (!current.IsNullOrEmpty())
            {
                continue; // ProxyChain / ProxyDetour already set
            }
            outbound["streamSettings"] ??= new JsonObject();
            outbound["streamSettings"]["sockopt"] ??= new JsonObject();
            outbound["streamSettings"]["sockopt"]["dialerProxy"] = tag;

            // xhttp downloadSettings, mirroring ApplyFullConfigTemplate
            if (outbound["streamSettings"]?["xhttpSettings"]?["extra"]?["downloadSettings"] is JsonObject downloadSettings)
            {
                downloadSettings["sockopt"] ??= new JsonObject();
                if (downloadSettings["sockopt"]["dialerProxy"] is null)
                {
                    downloadSettings["sockopt"]["dialerProxy"] = tag;
                }
            }
        }

        return JsonUtils.Serialize(cfg);
    }

    /// <summary>
    /// Xray WireGuard keeps the real server in settings.peers[].endpoint;
    /// settings.address is the local interface address and must not be used
    /// to decide whether to detour.
    /// </summary>
    private static string ExtractXrayServerAddress(JsonObject outbound, string? protocol)
    {
        if (protocol == "wireguard")
        {
            var endpoint = outbound["settings"]?["peers"]?.AsArray()?.FirstOrDefault()?["endpoint"]?.ToString();
            return StripHostPort(endpoint);
        }
        var address = outbound["settings"]?["servers"]?.AsArray()?.FirstOrDefault()?["address"]?.ToString()
            ?? outbound["settings"]?["vnext"]?.AsArray()?.FirstOrDefault()?["address"]?.ToString()
            ?? outbound["settings"]?["address"]?.ToString()
            ?? string.Empty;
        return address;
    }

    private static string StripHostPort(string? endpoint)
    {
        if (endpoint.IsNullOrEmpty())
        {
            return string.Empty;
        }
        if (endpoint.StartsWith('['))
        {
            var end = endpoint.IndexOf(']');
            return end > 0 ? endpoint[1..end] : endpoint;
        }
        var idx = endpoint.LastIndexOf(':');
        return idx > 0 ? endpoint[..idx] : endpoint;
    }

    public static string ApplySingboxGlobalFirstHop(string coreConfigContent, UpstreamProxyItem? item)
    {
        if (item is null || !item.IsUsable())
        {
            return coreConfigContent;
        }
        if (JsonUtils.ParseJson(coreConfigContent) is not JsonObject cfg)
        {
            return coreConfigContent;
        }

        var tag = Tag;

        if (cfg["outbounds"] is JsonArray outbounds)
        {
            var takenTags = outbounds.OfType<JsonObject>()
                .Select(o => o["tag"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (!takenTags.Contains(tag))
            {
                var upstream = BuildSingboxOutbound(item);
                if (upstream is not null)
                {
                    var upstreamNode = JsonUtils.ParseJson(JsonUtils.Serialize(upstream));
                    if (upstreamNode is JsonObject upstreamObj)
                    {
                        outbounds.Insert(0, upstreamObj);
                    }
                }
            }

            var detourTargets = outbounds.OfType<JsonObject>()
                .Select(o => o["detour"]?.ToString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            foreach (var outbound in outbounds.OfType<JsonObject>())
            {
                var t = outbound["tag"]?.ToString();
                if (t is null || t == tag)
                {
                    continue;
                }
                var type = outbound["type"]?.ToString()?.ToLowerInvariant();
                if (type is "direct" or "block" or "dns" or "selector" or "urltest")
                {
                    continue;
                }
                if (detourTargets.Contains(t))
                {
                    continue;
                }
                var server = outbound["server"]?.ToString() ?? string.Empty;
                if (!server.IsNullOrEmpty() && Utils.IsPrivateNetwork(server))
                {
                    continue;
                }
                var current = outbound["detour"]?.ToString();
                if (!current.IsNullOrEmpty())
                {
                    continue;
                }
                outbound["detour"] = tag;
            }
        }

        // WireGuard endpoints
        if (cfg["endpoints"] is JsonArray endpoints && endpoints.Count > 0)
        {
            foreach (var endpoint in endpoints.OfType<JsonObject>())
            {
                if (endpoint["detour"]?.ToString() is { } existing && !existing.IsNullOrEmpty())
                {
                    continue;
                }
                endpoint["detour"] = tag;
            }
        }

        return JsonUtils.Serialize(cfg);
    }
}
