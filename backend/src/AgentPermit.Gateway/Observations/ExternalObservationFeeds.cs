using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPermit.Gateway.Observations;

/// <summary>An observation representing UNGOVERNED activity seen by an external feed (an agent using a
/// connector directly, not through AgentPermit). Distinct from a gateway decision — there is no
/// computed/effective decision because AgentPermit never evaluated it; the value is surfacing it for review.</summary>
public static class ExternalObservation
{
    public const string Ungoverned = "UNGOVERNED";

    public static ObservationRecord Ungoverned_(string source, string agentId, string operation,
        string? connector, DateTimeOffset when, string? correlationId) => new()
    {
        OccurredUtc = when,
        Source = source,
        AgentId = agentId,
        ActionId = operation,
        TargetSystem = connector,
        ComputedDecision = string.Empty,
        EffectiveDecision = Ungoverned,
        EnforcementMode = Ungoverned,
        ConsequenceTier = string.Empty,
        WouldHaveBlocked = false,
        CorrelationId = correlationId
    };
}

/// <summary>
/// Pulls Power Platform / Copilot connector activity from the Microsoft 365 unified audit log via the
/// Office 365 Management Activity API (free with M365 audit — Standard tier is enough). OFF by default;
/// enabled per customer once their tenant + an app registration are configured, so no external cost is
/// incurred implicitly. Emits UNGOVERNED observations = connector use AgentPermit did not mediate.
/// </summary>
public sealed class PurviewAuditFeed(PurviewFeedOptions options, IHttpClientFactory http) : IObservationFeed
{
    public string Name => "purview-audit";
    public string Source => ObservationSource.PurviewAudit;
    public bool Enabled => options.Enabled;

    /// <summary>Map one audit record (Audit.General shape) to an UNGOVERNED observation, or null to skip.</summary>
    public static ObservationRecord? MapAuditRecord(JsonElement e)
    {
        string? S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var op = S("Operation");
        var actor = S("UserId") ?? S("UserKey");
        if (string.IsNullOrWhiteSpace(op) || string.IsNullOrWhiteSpace(actor)) return null;

        // Connector name lives under a Power Platform / connector-specific property when present.
        var connector = S("ConnectorId") ?? S("ConnectorName") ?? S("Workload");
        var whenStr = S("CreationTime");
        var when = DateTimeOffset.TryParse(whenStr, out var dto) ? dto : DateTimeOffset.UtcNow;
        return ExternalObservation.Ungoverned_(ObservationSource.PurviewAudit, actor!, op!, connector, when, S("Id"));
    }

    public async Task<FeedPullResult> PullAsync(string? cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new InvalidOperationException(
                "Purview audit feed is enabled but not configured (TenantId/ClientId/ClientSecret). " +
                "Provision these during onboarding; the feed stays inert until then.");

        var client = http.CreateClient();
        var token = await AcquireTokenAsync(client, ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Content is listed in blobs since the cursor (last CreationTime processed).
        var start = cursor ?? DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss");
        var listUrl = $"https://manage.office.com/api/v1.0/{options.TenantId}/activity/feed/subscriptions/content" +
                      $"?contentType={Uri.EscapeDataString(options.ContentType)}&startTime={Uri.EscapeDataString(start)}";
        var blobs = await client.GetFromJsonAsync<List<ContentBlob>>(listUrl, ct) ?? new();

        var observations = new List<ObservationRecord>();
        DateTimeOffset newest = cursor is not null && DateTimeOffset.TryParse(cursor, out var c) ? c : DateTimeOffset.MinValue;
        foreach (var blob in blobs.Where(b => !string.IsNullOrWhiteSpace(b.ContentUri)))
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync(blob.ContentUri!, ct));
            foreach (var rec in doc.RootElement.EnumerateArray())
            {
                var obs = MapAuditRecord(rec);
                if (obs is null) continue;
                observations.Add(obs);
                if (obs.OccurredUtc > newest) newest = obs.OccurredUtc;
            }
        }
        return new FeedPullResult
        {
            Observations = observations,
            NextCursor = newest == DateTimeOffset.MinValue ? cursor : newest.ToString("yyyy-MM-ddTHH:mm:ss")
        };
    }

    private async Task<string> AcquireTokenAsync(HttpClient client, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.ClientId!,
            ["client_secret"] = options.ClientSecret!,
            ["resource"] = "https://manage.office.com"
        });
        var resp = await client.PostAsync($"https://login.microsoftonline.com/{options.TenantId}/oauth2/token", form, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private sealed class ContentBlob { public string? ContentUri { get; set; } }
}

/// <summary>
/// Pulls Power Automate cloud-flow run history (free with Power Platform). OFF by default; enabled per
/// customer with an environment + app registration. Emits UNGOVERNED observations for connector calls made
/// by flows outside AgentPermit. The run→observation mapper is the tested core; the transport is provisioned
/// at onboarding.
/// </summary>
public sealed class FlowRunFeed(FlowRunFeedOptions options) : IObservationFeed
{
    public string Name => "flow-run";
    public string Source => ObservationSource.FlowRun;
    public bool Enabled => options.Enabled;

    /// <summary>Map one flow-run record to an UNGOVERNED observation, or null to skip.</summary>
    public static ObservationRecord? MapFlowRun(JsonElement e)
    {
        string? S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var flow = S("flowName") ?? S("name");
        var connector = S("connector") ?? S("trigger");
        var whenStr = S("startTime");
        if (string.IsNullOrWhiteSpace(flow)) return null;
        var when = DateTimeOffset.TryParse(whenStr, out var dto) ? dto : DateTimeOffset.UtcNow;
        return ExternalObservation.Ungoverned_(ObservationSource.FlowRun, flow!, S("status") ?? "run", connector, when, S("runId"));
    }

    public Task<FeedPullResult> PullAsync(string? cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.EnvironmentUrl) || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new InvalidOperationException(
                "Flow-run feed is enabled but not configured (EnvironmentUrl/ClientId/ClientSecret). " +
                "Provision these during onboarding; the feed stays inert until then.");

        // Live transport (Dataverse flowruns / Power Automate management API) is provisioned per tenant at
        // onboarding; the run->observation mapping above is the tested, tenant-independent core.
        throw new NotImplementedException("Flow-run transport is provisioned during onboarding.");
    }
}
