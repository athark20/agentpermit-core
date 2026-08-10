using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.ControlPlane;

namespace AgentPermit.Gateway.Adapters;

/// <summary>
/// Shared HTTP execution for target adapters whose connection details come from the Target System
/// Registry: POST with a per-target timeout, bounded retries on transient failures (5xx/408/network),
/// no retry on 4xx, and a stable target-idempotency header.
/// </summary>
public abstract class HttpTargetAdapterBase(HttpClient http, ITargetSystemRegistry targets, ILogger logger)
    : ActionAdapterBase
{
    protected TargetSystemRecord? Target(string code) => targets.Find(code);

    protected async Task<AdapterResult> InvokeAsync(
        string url, object body, int timeoutSeconds, int maxRetries, Guid executionId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 30 : timeoutSeconds));

                using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                message.Headers.Add("X-Execution-Id", executionId.ToString()); // target-system idempotency

                using var resp = await http.SendAsync(message, cts.Token);
                var content = await resp.Content.ReadAsStringAsync(cts.Token);

                if (resp.IsSuccessStatusCode) return AdapterResult.Ok(ExtractTargetId(content, resp), content);

                if ((int)resp.StatusCode is >= 400 and < 500 && resp.StatusCode != HttpStatusCode.RequestTimeout)
                    return AdapterResult.Fail($"HTTP_{(int)resp.StatusCode}", Truncate(content));

                if (attempt > maxRetries)
                    return AdapterResult.Fail($"HTTP_{(int)resp.StatusCode}", $"Exhausted retries: {Truncate(content)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (attempt > maxRetries) return AdapterResult.Fail("TARGET_UNAVAILABLE", ex.Message);
                logger.LogWarning(ex, "{Adapter} attempt {Attempt} failed; retrying.", AdapterCode, attempt);
            }

            await Task.Delay(200 * attempt, ct);
        }
    }

    protected static string? ExtractTargetId(string content, HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-Resource-Id", out var vals)) return vals.FirstOrDefault();
        try
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var name in new[] { "id", "resourceId", "referenceId", "targetResourceId" })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { /* non-JSON body */ }
        return null;
    }

    protected static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}

/// <summary>
/// Generic REST/OData target adapter. Reads the target's endpoint, timeout and retries from the
/// registry — so any REST/OData system is onboarded as a registry entry, no per-target code.
/// </summary>
public sealed class RestActionAdapter(HttpClient http, ITargetSystemRegistry targets, ILogger<RestActionAdapter> logger)
    : HttpTargetAdapterBase(http, targets, logger)
{
    public const string Code = "REST";
    public override string AdapterCode => Code;

    public override Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct)
    {
        var t = Target(request.TargetSystem);
        if (t is null || string.IsNullOrWhiteSpace(t.BaseUrl))
            return Task.FromResult(AdapterResult.Fail("TARGET_NOT_CONFIGURED", $"REST target '{request.TargetSystem}' has no base URL."));

        var url = $"{t.BaseUrl!.TrimEnd('/')}/{(t.ExecutePath ?? string.Empty).TrimStart('/')}";
        var body = new { executionId = request.ExecutionId, actionRequestId = request.ActionRequestId, actionId = request.ActionId, parameters = request.Parameters };
        return InvokeAsync(url, body, t.TimeoutSeconds, t.MaxRetries, request.ExecutionId, ct);
    }
}

/// <summary>
/// Delegated-execution adapter: hands the governed action to a Power Automate flow / Logic App
/// (the target's BaseUrl is the flow trigger URL). That flow uses Microsoft's prebuilt connector
/// to reach the real system — so a target with adapterType DELEGATED can reach ANY of the 1,400+
/// connectors with zero per-app code. AgentPermit governs + signs; Microsoft connects.
/// </summary>
public sealed class DelegatedActionAdapter(HttpClient http, ITargetSystemRegistry targets, ILogger<DelegatedActionAdapter> logger)
    : HttpTargetAdapterBase(http, targets, logger)
{
    public const string Code = "DELEGATED";
    public override string AdapterCode => Code;

    public override async Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct)
    {
        var t = Target(request.TargetSystem);
        if (t is null || string.IsNullOrWhiteSpace(t.BaseUrl))
            return AdapterResult.Fail("TARGET_NOT_CONFIGURED", $"DELEGATED target '{request.TargetSystem}' has no flow URL.");

        var body = new
        {
            executionId = request.ExecutionId,
            actionRequestId = request.ActionRequestId,
            actionId = request.ActionId,
            targetSystem = request.TargetSystem,
            parameters = request.Parameters
        };
        var timeout = t.TimeoutSeconds <= 0 ? 90 : t.TimeoutSeconds; // flows can take longer than a direct call
        var result = await InvokeAsync(t.BaseUrl!, body, timeout, t.MaxRetries, request.ExecutionId, ct);

        // A flow may return 200 with a business-failure status; honour it.
        if (result.Status == AdapterExecutionStatus.Succeeded && result.ResultJson is { } json && ReportsFailure(json))
            return AdapterResult.Fail("TARGET_REPORTED_FAILURE", Truncate(json));
        return result;
    }

    private static bool ReportsFailure(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                   && string.Equals(s.GetString(), "Failed", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
