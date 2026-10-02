using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ServiceLib.OpenCode;

public sealed class GatewayService
{
    private const string Tag = "OpenCode.Gateway";

    private readonly RequestExecutor _executor;
    private readonly ModelCatalog _catalog;
    private readonly OpenCodeTelemetry _telemetry;
    private readonly Func<OpenCodeItem> _getSettings;

    private WebApplication? _app;
    private SemaphoreSlim? _concurrency;
    private int _boundPort;

    public GatewayService(
        RequestExecutor executor,
        ModelCatalog catalog,
        OpenCodeTelemetry telemetry,
        Func<OpenCodeItem> getSettings)
    {
        _executor = executor;
        _catalog = catalog;
        _telemetry = telemetry;
        _getSettings = getSettings;
    }

    public bool IsRunning => _app is not null;

    public int BoundPort => _boundPort;

    public string? LastError { get; private set; }

    public bool LastErrorIsPortInUse { get; private set; }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            return true;
        }

        var settings = _getSettings();
        var host = settings.GatewayHost.IsNullOrEmpty() ? Global.Loopback : settings.GatewayHost;
        var port = settings.GatewayPort;
        _boundPort = port;
        LastError = null;
        LastErrorIsPortInUse = false;

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Parse(host), port);
                options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
            });

            var app = builder.Build();
            _concurrency = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentRequests));

            app.MapGet("/v1/models", ctx => HandleListModels(ctx, claudeAlias: false));
            app.MapGet("/claude/v1/models", ctx => HandleListModels(ctx, claudeAlias: true));
            app.MapGet("/v1/models/{modelId}", (HttpContext ctx, string modelId) => HandleGetModel(ctx, modelId, claudeAlias: false));
            app.MapGet("/claude/v1/models/{modelId}", (HttpContext ctx, string modelId) => HandleGetModel(ctx, modelId, claudeAlias: true));
            app.MapPost("/v1/chat/completions", ctx => HandleCompletion(ctx, "chat", claudeAlias: false));
            app.MapPost("/claude/v1/chat/completions", ctx => HandleCompletion(ctx, "chat", claudeAlias: true));
            app.MapPost("/v1/responses", ctx => HandleCompletion(ctx, "responses", claudeAlias: false));
            app.MapPost("/claude/v1/responses", ctx => HandleCompletion(ctx, "responses", claudeAlias: true));
            // Anthropic Messages API — what Claude Code actually calls (ANTHROPIC_BASE_URL + /v1/messages).
            app.MapPost("/v1/messages", ctx => HandleCompletion(ctx, "anthropic", claudeAlias: false));
            app.MapPost("/claude/v1/messages", ctx => HandleCompletion(ctx, "anthropic", claudeAlias: true));

            await app.StartAsync(ct);

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            var bound = addresses?.Addresses?.FirstOrDefault();
            if (bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                _boundPort = uri.Port;
            }

            _app = app;
            Logging.SaveLog($"{Tag} started on http://{host}:{_boundPort}/v1 (claude alias: /claude/v1, /claude/v1/messages)");
            return true;
        }
        catch (Exception ex)
        {
            LastErrorIsPortInUse = IsPortInUse(ex);
            LastError = LastErrorIsPortInUse ? "port_in_use" : ex.Message;
            Logging.SaveLog($"{Tag} failed to start on {host}:{port}: {ex.Message}");
            _app = null;
            return false;
        }
    }

    private static bool IsPortInUse(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        var message = ex.Message;
        return message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
            || message.Contains("address in use", StringComparison.OrdinalIgnoreCase);
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        var app = _app;
        if (app is null)
        {
            return;
        }

        _app = null;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await app.StopAsync(cts.Token);
            await app.DisposeAsync();
            Logging.SaveLog($"{Tag} stopped");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
        finally
        {
            _concurrency?.Dispose();
            _concurrency = null;
        }
    }

    private const string ClaudeModelPrefix = "claude-";

    private static string ApplyClaudePrefix(string modelId) =>
        modelId.StartsWith(ClaudeModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId
            : ClaudeModelPrefix + modelId;

    private static string StripClaudePrefix(string modelRef)
    {
        if (modelRef.IsNullOrEmpty())
        {
            return modelRef;
        }

        // modelRef may be "model" or "target/model"; strip only from the model part.
        var slash = modelRef.IndexOf('/');
        if (slash > 0 && slash < modelRef.Length - 1)
        {
            var targetPart = modelRef[..(slash + 1)];
            var modelPart = modelRef[(slash + 1)..];
            return modelPart.StartsWith(ClaudeModelPrefix, StringComparison.OrdinalIgnoreCase)
                ? targetPart + modelPart[ClaudeModelPrefix.Length..]
                : modelRef;
        }

        return modelRef.StartsWith(ClaudeModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelRef[ClaudeModelPrefix.Length..]
            : modelRef;
    }

    private async Task HandleListModels(HttpContext context, bool claudeAlias)
    {
        var settings = _getSettings();
        var targetId = settings.DefaultTarget;
        var models = _catalog.GetModels(targetId);
        if (settings.FreeOnly)
        {
            models = models.Where(m => m.IsFree).ToList();
        }

        if (claudeAlias)
        {
            models = models.Select(m => m with { Id = ApplyClaudePrefix(m.Id) }).ToList();
        }

        await WriteJson(context, 200, ClientFormat.WriteModels(models, targetId));
    }

    private async Task HandleGetModel(HttpContext context, string modelId, bool claudeAlias)
    {
        var settings = _getSettings();
        var lookupId = claudeAlias ? StripClaudePrefix(modelId) : modelId;
        var (target, model) = _catalog.ResolveTargetModel(lookupId, settings);
        if (model is null || target is null || (settings.FreeOnly && !model.IsFree))
        {
            await WriteError(context, 404, new OpenCodeError(
                "not_found_error", $"Model '{modelId}' not found.", 404, null, "model_not_found"));
            return;
        }

        if (claudeAlias)
        {
            model = model with { Id = ApplyClaudePrefix(model.Id) };
        }

        await WriteJson(context, 200, ClientFormat.WriteModel(model, target.Id));
    }

    private async Task HandleCompletion(HttpContext context, string clientFormat, bool claudeAlias)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        context.Response.Headers["x-opencode-request-id"] = requestId;

        var semaphore = _concurrency;
        if (semaphore is null || !await semaphore.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted))
        {
            await WriteError(context, 503, new OpenCodeError(
                "server_error", "Gateway is overloaded. Try again shortly.", 503, null, "overloaded_error"));
            return;
        }

        try
        {
            var settings = _getSettings();
            if (!settings.Enabled || !settings.GatewayEnabled)
            {
                await WriteError(context, 503, new OpenCodeError(
                    "server_error", "OpenCode gateway is disabled.", 503, null, "gateway_disabled"));
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.Body))
            {
                body = await reader.ReadToEndAsync(context.RequestAborted);
            }

            if (body.IsNullOrEmpty())
            {
                await WriteError(context, 400, new OpenCodeError(
                    "invalid_request_error", "Request body is required.", 400));
                return;
            }

            var request = clientFormat switch
            {
                "chat" => ClientFormat.ParseChatRequest(body),
                "anthropic" => ClientFormat.ParseAnthropicRequest(body),
                _ => ClientFormat.ParseResponsesRequest(body),
            };

            if (request is null)
            {
                await WriteError(context, 400, new OpenCodeError(
                    "invalid_request_error", "Malformed request body.", 400), clientFormat);
                return;
            }

            // Clients may send either the bare catalog id or a claude- alias id
            // (e.g. a model name copied from the /claude/v1 model list into a
            // non-alias client, or Claude Code itself). Resolve against the bare id;
            // echo the name the client used (or the claude-prefixed default on the
            // alias routes) so responses stay consistent with the model list.
            var rawModel = request.Model.IsNullOrEmpty() ? settings.DefaultModel : request.Model;
            var bareModel = StripClaudePrefix(rawModel);
            request = request with { Model = bareModel };
            var modelRef = claudeAlias ? ApplyClaudePrefix(bareModel) : rawModel;

            if (settings.FreeOnly)
            {
                var (_, resolvedModel) = _catalog.ResolveTargetModel(bareModel, settings);
                if (resolvedModel is null || !resolvedModel.IsFree)
                {
                    await WriteError(context, 404, new OpenCodeError(
                        "not_found_error", $"Model '{modelRef}' not found.", 404, null, "model_not_found"), clientFormat);
                    return;
                }
            }

            if (settings.DebugLogRequests)
            {
                Logging.SaveLog($"{Tag} <- client={clientFormat} model={modelRef} " +
                    $"stream={request.Stream} stop={request.Stop?.Count ?? 0} " +
                    $"messages={request.Messages.Count} tools={request.Tools?.Count ?? 0}");
            }

            var result = await _executor.ExecuteAsync(request, settings, context.RequestAborted);

            if (!result.Success)
            {
                var status = result.HttpStatus ?? 500;
                if (result.RetryAfter.IsNotEmpty()
                    && (status == 429 || result.State is EOpenCodeConnectivityState.RateLimited
                        or EOpenCodeConnectivityState.FreeUsageLimit))
                {
                    context.Response.Headers["Retry-After"] = result.RetryAfter;
                }

                await WriteError(context, status, result.Error ?? new OpenCodeError(
                    "server_error", "Request failed.", status), clientFormat);
                return;
            }

            if (request.Stream && result.Events is not null)
            {
                await WriteStream(context, clientFormat, modelRef, result.Events);
                return;
            }

            if (result.Response is null)
            {
                await WriteError(context, 502, new OpenCodeError(
                    "server_error", "Upstream returned no content.", 502), clientFormat);
                return;
            }

            var json = clientFormat switch
            {
                "chat" => ClientFormat.WriteChatCompletion(result.Response, modelRef),
                "anthropic" => ClientFormat.WriteAnthropicMessage(result.Response, modelRef),
                _ => ClientFormat.WriteResponses(result.Response, modelRef),
            };
            await WriteJson(context, 200, json);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; upstream cancellation is handled by the executor.
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            try
            {
                if (!context.Response.HasStarted)
                {
                    await WriteError(context, 500, new OpenCodeError(
                        "server_error", "Internal gateway error.", 500), clientFormat);
                }
            }
            catch
            {
                // Response already started or client gone.
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task WriteStream(
        HttpContext context,
        string clientFormat,
        string modelRef,
        IAsyncEnumerable<NormalizedStreamEvent> events)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (clientFormat == "anthropic")
        {
            var writer = new AnthropicStreamWriter(modelRef);
            await foreach (var evt in events.WithCancellation(context.RequestAborted))
            {
                foreach (var (eventName, json) in writer.Write(evt))
                {
                    await WriteSse(context, eventName, json);
                    if (eventName is "message_stop" or "error")
                    {
                        return;
                    }
                }
            }

            return;
        }

        var responseId = clientFormat == "chat"
            ? $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}"
            : $"resp_{Guid.NewGuid().ToString("N")[..12]}";

        await foreach (var evt in events.WithCancellation(context.RequestAborted))
        {
            if (evt.Type == NormalizedStreamEventType.Error)
            {
                var errorJson = ClientFormat.WriteError(new OpenCodeError(
                    evt.ErrorType ?? "server_error",
                    evt.ErrorMessage ?? "Upstream error."));
                await WriteSse(context, "error", errorJson);
                return;
            }

            if (clientFormat == "chat")
            {
                if (evt.Type == NormalizedStreamEventType.Usage)
                {
                    continue;
                }

                var chunk = ClientFormat.WriteChatChunk(responseId, modelRef, evt, created);
                await context.Response.WriteAsync($"data: {chunk}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);

                if (evt.Type == NormalizedStreamEventType.Done)
                {
                    await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    return;
                }
            }
            else
            {
                if (evt.Type == NormalizedStreamEventType.Usage)
                {
                    continue;
                }

                var (eventName, json) = ClientFormat.WriteResponsesEvent(responseId, modelRef, evt);
                await WriteSse(context, eventName, json);

                if (evt.Type == NormalizedStreamEventType.Done)
                {
                    return;
                }
            }
        }

        if (clientFormat == "chat")
        {
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static async Task WriteSse(HttpContext context, string eventName, string json)
    {
        await context.Response.WriteAsync($"event: {eventName}\n", context.RequestAborted);
        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task WriteJson(HttpContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json, context.RequestAborted);
    }

    private static async Task WriteError(HttpContext context, int status, OpenCodeError error, string clientFormat = "chat")
    {
        var json = clientFormat == "anthropic"
            ? ClientFormat.WriteAnthropicError(error)
            : ClientFormat.WriteError(error);
        await WriteJson(context, status, json);
    }
}
