using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Policies;

namespace AgentPermit.Gateway.Services;

/// <summary>Two-person control for rule changes (the Policy section of configuration).</summary>
public sealed class PolicyChangeOptions
{
    /// <summary>
    /// When true, publishing rules does not make them live. It records a proposed change, and a DIFFERENT
    /// administrator, signed in with their own Entra ID account, must approve it before it is signed and
    /// published. Off by default so a single-admin development environment keeps working; turn it on in
    /// production. The freeze switch is not affected: one administrator can always freeze every agent.
    /// </summary>
    public bool RequireSecondApprover { get; set; } = false;

    /// <summary>Days a proposed change stays open for approval.</summary>
    public int ChangeExpiryDays { get; set; } = 7;
}

/// <summary>Who is acting on a rule change. <see cref="IsPerson"/> is false for the shared admin key and dev-open mode.</summary>
public sealed record PolicyChangeActor(string Name, string? Email, bool IsPerson)
{
    public string Display =>
        string.IsNullOrEmpty(Email) ? Name
        : string.IsNullOrEmpty(Name) || string.Equals(Name, Email, StringComparison.OrdinalIgnoreCase) ? Email!
        : $"{Name} <{Email}>";
}

public static class PolicyChangeStatus
{
    public const string Pending = "Pending";
    public const string Published = "Published";
    public const string Rejected = "Rejected";
    public const string Superseded = "Superseded";
    public const string Expired = "Expired";
}

/// <summary>A proposed rule change waiting for (or decided by) a second administrator. Stored in the config store.</summary>
public sealed class PolicyChangeRecord
{
    [JsonPropertyName("changeId")] public string ChangeId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = PolicyChangeStatus.Pending;
    [JsonPropertyName("policyCode")] public string PolicyCode { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("ruleCount")] public int RuleCount { get; set; }

    /// <summary>The live rules the proposal was made against. If they change first, the proposal is superseded.</summary>
    [JsonPropertyName("basedOnVersion")] public string? BasedOnVersion { get; set; }
    [JsonPropertyName("basedOnHash")] public string? BasedOnHash { get; set; }

    [JsonPropertyName("proposedBy")] public string ProposedBy { get; set; } = string.Empty;
    [JsonPropertyName("proposedByEmail")] public string? ProposedByEmail { get; set; }
    [JsonPropertyName("proposedAtUtc")] public DateTimeOffset ProposedAtUtc { get; set; }
    [JsonPropertyName("expiresAtUtc")] public DateTimeOffset ExpiresAtUtc { get; set; }

    [JsonPropertyName("decidedBy")] public string? DecidedBy { get; set; }
    [JsonPropertyName("decidedAtUtc")] public DateTimeOffset? DecidedAtUtc { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }

    /// <summary>Hash of the snapshot as published, once approved.</summary>
    [JsonPropertyName("publishedHash")] public string? PublishedHash { get; set; }

    /// <summary>The complete proposed rule set, exactly as it will be published.</summary>
    [JsonPropertyName("snapshot")] public PolicySnapshot Snapshot { get; set; } = new();
}

/// <summary>
/// Maker-checker for rules: one administrator proposes a rule set, a different signed-in administrator approves
/// it, and only then is it signed and published. The proposer can't approve their own change, the shared admin
/// key can't approve at all (it is not a person), and a proposal made against rules that have since changed is
/// superseded rather than published over the newer rules.
/// </summary>
public sealed class PolicyChangeService(
    IConfigStore store,
    IPolicyPublisher publisher,
    IPolicySnapshotProvider snapshots,
    PolicyChangeOptions options,
    ILogger<PolicyChangeService> logger)
{
    public const string ConfigType = "policy-change";

    // One decision at a time in this process, so a double click can't publish twice.
    private static readonly SemaphoreSlim DecisionGate = new(1, 1);

    public bool Required => options.RequireSecondApprover;

    public async Task<PolicyChangeRecord> ProposeAsync(PolicySnapshot authored, PolicyChangeActor proposer, CancellationToken ct)
    {
        // Check it now, not when the second administrator approves it.
        PolicyPublisher.Validate(authored);

        var now = DateTimeOffset.UtcNow;
        var change = new PolicyChangeRecord
        {
            ChangeId = Guid.NewGuid().ToString("N"),
            Status = PolicyChangeStatus.Pending,
            PolicyCode = authored.PolicyCode,
            Version = authored.Version,
            RuleCount = authored.Rules.Count,
            BasedOnVersion = snapshots.Current.Version,
            BasedOnHash = snapshots.ActiveHash,
            ProposedBy = proposer.Display,
            ProposedByEmail = proposer.Email?.Trim().ToLowerInvariant(),
            ProposedAtUtc = now,
            ExpiresAtUtc = now.AddDays(Math.Max(1, options.ChangeExpiryDays)),
            Snapshot = authored
        };
        await SaveAsync(change, ct);
        logger.LogInformation("Rule change {ChangeId} ({Code} v{Version}) proposed by {Proposer}; waiting for a second administrator.",
            change.ChangeId, change.PolicyCode, change.Version, change.ProposedBy);
        return change;
    }

    /// <summary>Recent proposals, newest first, with stale pending ones shown as expired.</summary>
    public async Task<IReadOnlyList<PolicyChangeRecord>> ListAsync(CancellationToken ct)
    {
        var rows = await store.ListAsync(ConfigType, ct);
        var now = DateTimeOffset.UtcNow;
        return rows.Select(r => Read(r.Json))
            .OfType<PolicyChangeRecord>()
            .Select(c => { if (c.Status == PolicyChangeStatus.Pending && c.ExpiresAtUtc < now) c.Status = PolicyChangeStatus.Expired; return c; })
            .OrderByDescending(c => c.ProposedAtUtc)
            .Take(50)
            .ToList();
    }

    public async Task<PolicyChangeRecord?> GetAsync(string changeId, CancellationToken ct)
    {
        var row = await store.GetAsync(ConfigType, changeId, ct);
        return row is null ? null : Read(row.Json);
    }

    /// <summary>A second administrator approves: the proposed rules are signed and published.</summary>
    public async Task<(PolicyChangeRecord Change, PublishResult Result)> ApproveAsync(
        string changeId, PolicyChangeActor approver, CancellationToken ct)
    {
        if (!approver.IsPerson)
            throw new ForbiddenActionException(
                "A rule change must be approved by an administrator signed in with their own work account.")
            { Title = "Sign in to approve" };

        await DecisionGate.WaitAsync(ct);
        try
        {
            var change = await RequirePendingAsync(changeId, ct);

            if (IsProposer(change, approver))
                throw new ForbiddenActionException(
                    "You proposed this change, so a different administrator must approve it.")
                { Title = "Second administrator needed" };

            if (!string.Equals(snapshots.ActiveHash, change.BasedOnHash, StringComparison.OrdinalIgnoreCase))
            {
                change.Status = PolicyChangeStatus.Superseded;
                change.DecidedAtUtc = DateTimeOffset.UtcNow;
                await SaveAsync(change, ct);
                throw new ConflictException(
                    $"The live rules changed after this was proposed (it was based on version {change.BasedOnVersion}, live is now {snapshots.Current.Version}). Propose the change again from the current rules.")
                { Title = "Rules changed since" };
            }

            var result = await publisher.PublishAsync(change.Snapshot, $"{change.ProposedBy}, approved by {approver.Display}", ct);

            change.Status = PolicyChangeStatus.Published;
            change.DecidedBy = approver.Display;
            change.DecidedAtUtc = DateTimeOffset.UtcNow;
            change.PublishedHash = result.Hash;
            await SaveAsync(change, ct);
            logger.LogInformation("Rule change {ChangeId} approved by {Approver} and published as v{Version}.",
                change.ChangeId, approver.Display, result.Version);
            return (change, result);
        }
        finally
        {
            DecisionGate.Release();
        }
    }

    /// <summary>Any administrator, including the proposer, can turn a proposal down.</summary>
    public async Task<PolicyChangeRecord> RejectAsync(string changeId, PolicyChangeActor actor, string? comment, CancellationToken ct)
    {
        await DecisionGate.WaitAsync(ct);
        try
        {
            var change = await RequirePendingAsync(changeId, ct);
            change.Status = PolicyChangeStatus.Rejected;
            change.DecidedBy = actor.Display;
            change.DecidedAtUtc = DateTimeOffset.UtcNow;
            change.Comment = comment is { Length: > 2000 } ? comment[..2000] : comment;
            await SaveAsync(change, ct);
            return change;
        }
        finally
        {
            DecisionGate.Release();
        }
    }

    private async Task<PolicyChangeRecord> RequirePendingAsync(string changeId, CancellationToken ct)
    {
        var change = await GetAsync(changeId, ct)
            ?? throw new NotFoundException("No proposed rule change exists with that id.") { Title = "Change not found" };

        if (change.Status != PolicyChangeStatus.Pending)
            throw new ConflictException($"This change is already {change.Status.ToLowerInvariant()}.") { Title = "Already decided" };

        if (change.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            change.Status = PolicyChangeStatus.Expired;
            await SaveAsync(change, ct);
            throw new ConflictException("This change expired before anyone approved it. Propose it again.") { Title = "Change expired" };
        }
        return change;
    }

    private static bool IsProposer(PolicyChangeRecord change, PolicyChangeActor actor)
    {
        if (!string.IsNullOrEmpty(change.ProposedByEmail) && !string.IsNullOrEmpty(actor.Email))
            return string.Equals(change.ProposedByEmail, actor.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        return string.Equals(change.ProposedBy, actor.Display, StringComparison.OrdinalIgnoreCase);
    }

    private Task SaveAsync(PolicyChangeRecord change, CancellationToken ct)
        => store.UpsertAsync(ConfigType, change.ChangeId, JsonSerializer.Serialize(change), ct);

    private PolicyChangeRecord? Read(string json)
    {
        try { return JsonSerializer.Deserialize<PolicyChangeRecord>(json); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping an unreadable rule-change record.");
            return null;
        }
    }
}
