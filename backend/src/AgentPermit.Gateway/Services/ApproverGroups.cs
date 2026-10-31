using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// A named approver group — the people who may sign off when a rule says <c>approverRoles: ["FinanceController"]</c>.
/// The generic <c>approver</c> persona says "this person may approve things"; an approver group says
/// "this person may approve <em>this kind</em> of thing". Rules name the group; the group lists the people.
/// </summary>
public sealed class ApproverGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("members")] public List<ApproverGroupMember> Members { get; set; } = new();
    [JsonPropertyName("updatedUtc")] public DateTimeOffset? UpdatedUtc { get; set; }
}

public sealed class ApproverGroupMember
{
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

/// <summary>
/// Store-backed directory of approver groups (config entity type <c>approver-group</c>, id = group name,
/// case-insensitive), cached briefly like the access directory. Membership is matched on e-mail.
/// </summary>
public sealed class ApproverGroupDirectory(IConfigStore store, ILogger<ApproverGroupDirectory> logger)
{
    public const string EntityType = "approver-group";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile Dictionary<string, ApproverGroup>? _cache;
    private DateTimeOffset _loadedUtc = DateTimeOffset.MinValue;

    public void Invalidate() => _loadedUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<ApproverGroup>> ListAsync(CancellationToken ct)
        => (await LoadAsync(ct)).Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<ApproverGroup?> GetAsync(string name, CancellationToken ct)
        => string.IsNullOrWhiteSpace(name) ? null
         : (await LoadAsync(ct)).TryGetValue(name.Trim(), out var g) ? g : null;

    /// <summary>
    /// Is <paramref name="approverIdentity"/> a member of the group? The identity may be a bare e-mail or the
    /// console's "Display Name &lt;email&gt;" form; the e-mail is what is compared.
    /// </summary>
    public async Task<bool> IsMemberAsync(string groupName, string? approverIdentity, CancellationToken ct)
    {
        var group = await GetAsync(groupName, ct);
        if (group is null) return false;
        var email = ExtractEmail(approverIdentity);
        if (email is null) return false;
        return group.Members.Any(m => string.Equals(m.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ApproverGroup> UpsertAsync(string name, ApproverGroup group, CancellationToken ct)
    {
        group.Name = name.Trim();
        group.UpdatedUtc = DateTimeOffset.UtcNow;
        group.Members = (group.Members ?? new())
            .Where(m => !string.IsNullOrWhiteSpace(m.Email))
            .Select(m => new ApproverGroupMember { Email = m.Email.Trim().ToLowerInvariant(), DisplayName = m.DisplayName?.Trim() })
            .GroupBy(m => m.Email).Select(g => g.First())
            .ToList();
        await store.UpsertAsync(EntityType, group.Name, JsonSerializer.Serialize(group), ct);
        Invalidate();
        return group;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        // Resolve the stored (canonical-case) name first so "financecontroller" deletes "FinanceController".
        var existing = await GetAsync(name, ct);
        var removed = await store.DeleteAsync(EntityType, existing?.Name ?? name.Trim(), ct);
        Invalidate();
        return removed;
    }

    /// <summary>Pull the e-mail out of "Name &lt;email&gt;" or return the trimmed, lower-cased value if it looks like one.</summary>
    public static string? ExtractEmail(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var s = identity.Trim();
        var lt = s.LastIndexOf('<');
        var gt = s.LastIndexOf('>');
        if (lt >= 0 && gt > lt) s = s[(lt + 1)..gt].Trim();
        return s.Length == 0 ? null : s.ToLowerInvariant();
    }

    private async Task<Dictionary<string, ApproverGroup>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _loadedUtc <= CacheTtl) return _cache;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _loadedUtc <= CacheTtl) return _cache;
            var map = new Dictionary<string, ApproverGroup>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var rec in await store.ListAsync(EntityType, ct))
                {
                    var g = JsonSerializer.Deserialize<ApproverGroup>(rec.Json);
                    if (g is null) continue;
                    if (string.IsNullOrWhiteSpace(g.Name)) g.Name = rec.EntityId;
                    map[g.Name] = g;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load approver groups; treating as none defined.");
            }
            _cache = map;
            _loadedUtc = DateTimeOffset.UtcNow;
            return map;
        }
        finally { _lock.Release(); }
    }
}
