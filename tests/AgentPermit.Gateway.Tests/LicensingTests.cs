using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentPermit.Gateway.Licensing;
using AgentPermit.Gateway.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Tier enforcement via signed licence. Entitlements gate management & advanced features by edition; a
/// missing/expired/tampered licence drops to a restricted mode that KEEPS enforcing existing policy but
/// locks management. A licence can be hot-applied at runtime (persisted + activated, no restart).
/// </summary>
public sealed class LicensingTests
{
    private static (string priv, string pub) Keys()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }
    private static LicenseService Svc(LicenseOptions o, IConfigStore? store = null)
        => new(o, store ?? new InMemoryConfigStore(), NullLogger<LicenseService>.Instance);

    [Fact]
    public void Presets_GateFeaturesByEdition()
    {
        Assert.True(Entitlements.Enterprise.Has(Features.ConnectorGovernance));
        Assert.False(Entitlements.Standard.Has(Features.ConnectorGovernance));
        Assert.Equal(25, Entitlements.Standard.MaxAgents);
        Assert.True(Entitlements.Unlimited.Has("anything"));
        Assert.False(Entitlements.Unlimited.ManagementLocked);
    }

    [Fact]
    public void EnforceOff_IsUnlimited()
    {
        var svc = Svc(new LicenseOptions { Enforce = false });
        Assert.Equal("Unlicensed (enforcement off)", svc.Current.Edition);
        Assert.True(svc.Current.Has(Features.Sso));
        Assert.False(svc.Current.ManagementLocked);
    }

    [Fact]
    public void EnforceOn_NoLicence_IsRestricted_ButProtectionStays()
    {
        var svc = Svc(new LicenseOptions { Enforce = true });
        Assert.True(svc.Current.ManagementLocked);
        Assert.Equal(0, svc.Current.MaxAgents);
        Assert.False(svc.Current.Has(Features.ConnectorGovernance));
    }

    [Fact]
    public void ValidSignedLicence_VerifiesToEntitlements()
    {
        var (priv, pub) = Keys();
        var token = LicenseService.Issue(priv, new { edition = "Enterprise", features = new[] { "*" }, maxAgents = 500, exp = DateTimeOffset.UtcNow.AddYears(1) });
        var e = LicenseService.VerifyToken(token, pub);
        Assert.Equal("Enterprise", e.Edition);
        Assert.True(e.Has(Features.ConnectorGovernance));
        Assert.Equal(500, e.MaxAgents);
        Assert.False(e.ManagementLocked);
    }

    [Fact]
    public void StandardLicence_GatesEnterpriseFeatures()
    {
        var (priv, pub) = Keys();
        var token = LicenseService.Issue(priv, new { edition = "Standard", features = Array.Empty<string>(), maxAgents = 25, exp = DateTimeOffset.UtcNow.AddYears(1) });
        var e = LicenseService.VerifyToken(token, pub);
        Assert.Equal("Standard", e.Edition);
        Assert.False(e.Has(Features.ConnectorGovernance));
    }

    [Fact]
    public void ExpiredLicence_LocksManagement()
    {
        var (priv, pub) = Keys();
        var token = LicenseService.Issue(priv, new { edition = "Enterprise", features = new[] { "*" }, exp = DateTimeOffset.UtcNow.AddDays(-1) });
        var e = LicenseService.VerifyToken(token, pub);
        Assert.True(e.Expired);
        Assert.True(e.ManagementLocked);
    }

    [Fact]
    public void TamperedLicence_IsRejected()
    {
        var (priv, pub) = Keys();
        var token = LicenseService.Issue(priv, new { edition = "Enterprise", features = new[] { "*" }, exp = DateTimeOffset.UtcNow.AddYears(1) });
        var tampered = ("A" == token[..1] ? "B" : "A") + token[1..];
        var e = LicenseService.VerifyToken(tampered, pub);
        Assert.True(e.ManagementLocked);
        Assert.Equal("Unlicensed", e.Edition);
    }

    [Fact]
    public async Task HotApply_ActivatesLive_AndPersists()
    {
        var (priv, pub) = Keys();
        var store = new InMemoryConfigStore();
        var svc = Svc(new LicenseOptions { Enforce = true, PublicKeyPem = pub }, store);
        Assert.True(svc.Current.ManagementLocked);   // starts restricted (no licence)

        var token = LicenseService.Issue(priv, new { edition = "Enterprise", features = new[] { "*" }, maxAgents = 100, exp = DateTimeOffset.UtcNow.AddYears(1) });
        await svc.ApplyAsync(token, CancellationToken.None);
        Assert.Equal("Enterprise", svc.Current.Edition);   // activated live
        Assert.False(svc.Current.ManagementLocked);

        // Persisted: a fresh service on the same store picks it up at init (survives restart).
        var svc2 = Svc(new LicenseOptions { Enforce = true, PublicKeyPem = pub }, store);
        await svc2.InitAsync(CancellationToken.None);
        Assert.Equal("Enterprise", svc2.Current.Edition);

        await svc.ClearAsync(CancellationToken.None);
        Assert.True(svc.Current.ManagementLocked);          // reverts to restricted
    }

    [Fact]
    public async Task HotApply_InvalidToken_IsRejected()
    {
        var (_, pub) = Keys();
        var svc = Svc(new LicenseOptions { Enforce = true, PublicKeyPem = pub });
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("not.a-real-token", CancellationToken.None));
    }

    [Fact]
    public async Task LicenseStatus_Endpoint_ReportsEdition()
    {
        using var f = new GatewayFactory();
        var res = await f.CreateClient().GetAsync("/api/admin/license");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(d.RootElement.GetProperty("edition").GetString()));
    }
}
