using System.Text.Json;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services.PowerPlatform;

/// <summary>
/// Persists tenant connections an admin adds at runtime (via the console) in the config store, so a
/// newly-connected tenant works immediately without a redeploy. Config-file connections
/// (<see cref="PowerPlatformConnectionSet"/>) remain the baseline and are never mutated here.
///
/// The client secret is stored alongside the connection in the customer's own config store (Azure SQL,
/// encrypted at rest by TDE) and is NEVER returned by any read API. For higher assurance a customer can
/// instead keep the connection in config with the secret as a Key Vault <c>secretref</c>.
/// </summary>
public sealed class TenantConnectionStore(IConfigStore store)
{
    private const string Type = "onboarding-tenant";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<IReadOnlyList<PowerPlatformConnection>> ListAsync(CancellationToken ct)
    {
        var rows = await store.ListAsync(Type, ct);
        var list = new List<PowerPlatformConnection>();
        foreach (var r in rows)
        {
            try
            {
                var c = JsonSerializer.Deserialize<PowerPlatformConnection>(r.Json, Json);
                if (c is not null) list.Add(c);
            }
            catch { /* skip a corrupt row rather than break onboarding */ }
        }
        return list;
    }

    public Task SaveAsync(PowerPlatformConnection c, CancellationToken ct)
        => store.UpsertAsync(Type, c.ResolvedKey, JsonSerializer.Serialize(c, Json), ct);

    public Task<bool> DeleteAsync(string key, CancellationToken ct)
        => store.DeleteAsync(Type, PowerPlatformConnection.Slug(key), ct);
}

/// <summary>A resolved tenant connection plus whether it can be removed from the console (stored ones can; config ones can't).</summary>
public readonly record struct ResolvedTenant(PowerPlatformConnection Connection, bool Removable);

/// <summary>
/// Merges config-file tenant connections with admin-added (stored) ones, resolving by key. Stored
/// connections override a config connection with the same key. This is the single source of truth
/// discovery uses at call time.
/// </summary>
public sealed class TenantConnectionResolver(PowerPlatformConnectionSet config, TenantConnectionStore store)
{
    public async Task<IReadOnlyList<ResolvedTenant>> AllAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, ResolvedTenant>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in config.Connections) map[c.ResolvedKey] = new ResolvedTenant(c, Removable: false);
        foreach (var c in await store.ListAsync(ct)) map[c.ResolvedKey] = new ResolvedTenant(c, Removable: true);
        return map.Values.OrderBy(t => t.Connection.ResolvedLabel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<PowerPlatformConnection?> ByKeyAsync(string? key, CancellationToken ct)
    {
        var all = await AllAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) return all.Count > 0 ? all[0].Connection : null;
        var slug = PowerPlatformConnection.Slug(key);
        foreach (var t in all)
            if (string.Equals(t.Connection.ResolvedKey, slug, StringComparison.OrdinalIgnoreCase)) return t.Connection;
        return null;
    }

    /// <summary>True if the key belongs to an admin-added (removable) connection.</summary>
    public async Task<bool> IsRemovableAsync(string key, CancellationToken ct)
    {
        var slug = PowerPlatformConnection.Slug(key);
        foreach (var t in await AllAsync(ct))
            if (string.Equals(t.Connection.ResolvedKey, slug, StringComparison.OrdinalIgnoreCase)) return t.Removable;
        return false;
    }
}
