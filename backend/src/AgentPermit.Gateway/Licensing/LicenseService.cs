using System.Security.Cryptography;
using System.Text.Json;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Licensing;

/// <summary>Configuration for tier/entitlement enforcement.</summary>
public sealed class LicenseOptions
{
    /// <summary>When false (default: dev/tests), licensing is not enforced and everything is enabled.</summary>
    public bool Enforce { get; set; } = false;
    /// <summary>The vendor's RSA <b>public</b> key (PEM), baked into the deployment, used to verify licences.</summary>
    public string? PublicKeyPem { get; set; }
    /// <summary>Bootstrap licence token from config; a hot-applied licence in the config store overrides it.</summary>
    public string? Token { get; set; }
}

/// <summary>
/// Resolves entitlements from a signed licence. Customer-hosted, so tiers are enforced by a licence the
/// gateway verifies against the vendor's public key. A licence can be <b>hot-applied</b> at runtime
/// (persisted in the config store, activated in memory — no restart). Missing/invalid/expired → a restricted
/// mode that keeps enforcing existing policy but locks management, so a lapse never leaves agents ungoverned.
/// </summary>
public sealed class LicenseService : IEntitlementsProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string CfgType = "license";
    private const string CfgId = "current";

    private readonly LicenseOptions _o;
    private readonly IConfigStore _store;
    private readonly ILogger<LicenseService> _log;
    private volatile Entitlements _current;

    public LicenseService(LicenseOptions options, IConfigStore store, ILogger<LicenseService> log)
    {
        _o = options; _store = store; _log = log;
        _current = Compute(options.Token);   // bootstrap from config; InitAsync may override from the store
    }

    public Entitlements Current => _current;

    /// <summary>Startup: override the bootstrap licence with any hot-applied token persisted in the config store.</summary>
    public async Task InitAsync(CancellationToken ct)
    {
        if (!_o.Enforce) return;
        try
        {
            var rec = await _store.GetAsync(CfgType, CfgId, ct);
            var stored = rec is null ? null : JsonSerializer.Deserialize<StoredLicense>(rec.Json, Json);
            if (!string.IsNullOrWhiteSpace(stored?.Token))
            {
                _current = Compute(stored!.Token);
                _log.LogInformation("Licence loaded from store: edition={E} locked={L}", _current.Edition, _current.ManagementLocked);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Licence init from store failed."); }
    }

    /// <summary>Hot-apply a new licence token: verify → persist → activate. No restart.</summary>
    public async Task<Entitlements> ApplyAsync(string token, CancellationToken ct)
    {
        var v = VerifyToken(token, _o.PublicKeyPem);
        if (v.ManagementLocked)
            throw new InvalidOperationException(v.Expired ? "Licence has expired." : "Licence token is invalid or not signed by this vendor key.");
        await _store.UpsertAsync(CfgType, CfgId, JsonSerializer.Serialize(new StoredLicense { Token = token }, Json), ct);
        _current = _o.Enforce ? v : Entitlements.Unlimited;
        _log.LogInformation("Licence applied: edition={E} maxAgents={M}", _current.Edition, _current.MaxAgents);
        return _current;
    }

    /// <summary>Remove the hot-applied licence and revert to the bootstrap/config licence.</summary>
    public async Task<Entitlements> ClearAsync(CancellationToken ct)
    {
        await _store.DeleteAsync(CfgType, CfgId, ct);
        _current = Compute(_o.Token);
        _log.LogInformation("Licence cleared; reverted to bootstrap: edition={E}", _current.Edition);
        return _current;
    }

    private Entitlements Compute(string? token) =>
        !_o.Enforce ? Entitlements.Unlimited : VerifyOrRestricted(token, _o.PublicKeyPem);

    // ---- static verify / issue (used by controllers, tools, tests) --------------------------------------

    /// <summary>Verify a token against a public key. Returns a Restricted entitlement (ManagementLocked) when invalid.</summary>
    public static Entitlements VerifyToken(string? token, string? publicKeyPem) => VerifyOrRestricted(token, publicKeyPem);

    private static Entitlements VerifyOrRestricted(string? token, string? pub)
    {
        try { return Verify(token, pub) ?? Entitlements.Restricted("no licence configured"); }
        catch { return Entitlements.Restricted("invalid licence"); }
    }

    private static Entitlements? Verify(string? token, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(publicKeyPem)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        var payload = B64UrlDecode(parts[0]);
        var signature = B64UrlDecode(parts[1]);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            return null; // tampered or wrong key
        var ent = JsonSerializer.Deserialize<LicensePayload>(payload, Json)?.ToEntitlements();
        if (ent is null) return null;
        return ent.Expired ? ent with { ManagementLocked = true } : ent;
    }

    /// <summary>Sign a licence payload with the vendor private key (PEM) — used by the issuing tool / tests.</summary>
    public static string Issue(string privateKeyPem, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return B64Url(json) + "." + B64Url(sig);
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] B64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }

    private sealed class StoredLicense { public string? Token { get; set; } }

    private sealed class LicensePayload
    {
        public string? CustomerId { get; set; }
        public string? Edition { get; set; }
        public int? MaxAgents { get; set; }
        public int? MaxEnvironments { get; set; }
        public string[]? Features { get; set; }
        public DateTimeOffset? Exp { get; set; }

        public Entitlements ToEntitlements() => new()
        {
            Edition = Edition ?? "Standard",
            MaxAgents = MaxAgents ?? 25,
            MaxEnvironments = MaxEnvironments ?? 1,
            FeatureSet = new HashSet<string>(Features ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            ExpiresUtc = Exp,
            CustomerId = CustomerId,
        };
    }
}
