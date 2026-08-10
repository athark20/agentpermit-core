using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Services;
using AgentPermit.Gateway.Services.PowerPlatform;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Roles &amp; access: the four fixed roles, and the app→Entra path. AgentPermit is the source of truth —
/// members are saved in-app and pushed OUT to Entra. Until the tenant is connected and consented, saving
/// still works and applying reports that approval is needed (no implicit writes).
/// </summary>
public sealed class AccessRolesTests
{
    [Fact]
    public void FourFixedRoles_AreDefined_WithStableKeys()
    {
        var keys = AccessRoles.All.Select(r => r.Key).ToArray();
        Assert.Equal(new[] { "admin", "approver", "agent-owner", "viewer" }, keys);
    }

    [Theory]
    [InlineData("Admin", "admin")]
    [InlineData("APPROVER", "approver")]
    [InlineData("agent-owner", "agent-owner")]
    public void Find_IsCaseInsensitive(string input, string expectedKey)
        => Assert.Equal(expectedKey, AccessRoles.Find(input)!.Key);

    [Fact]
    public void Find_UnknownRole_IsNull() => Assert.Null(AccessRoles.Find("superuser"));

    [Fact]
    public void GroupNaming_IsDeterministic_AndNamespaced()
    {
        var approver = AccessRoles.Find("approver")!;
        Assert.Equal("AgentPermit Approver", AccessRoles.GroupName(approver));
        Assert.Equal("agentpermit-approver", AccessRoles.GroupMailNickname(approver));
    }

    [Fact]
    public async Task List_ReturnsAllFourRoles()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/access")).Content.ReadAsStringAsync());
        var keys = doc.RootElement.EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToArray();
        Assert.Equal(new[] { "admin", "approver", "agent-owner", "viewer" }, keys);
    }

    [Fact]
    public async Task Save_ThenList_ReflectsMembers()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var save = await client.PutAsJsonAsync("/api/admin/access/approver",
            new { members = new[] { new { email = "sara@contoso.com", displayName = "Sara" } } });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/access")).Content.ReadAsStringAsync());
        var approver = doc.RootElement.EnumerateArray().First(r => r.GetProperty("key").GetString() == "approver");
        var member = approver.GetProperty("members").EnumerateArray().Single();
        Assert.Equal("sara@contoso.com", member.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Save_UnknownRole_Is404()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var res = await client.PutAsJsonAsync("/api/admin/access/superuser", new { members = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Status_WhenTenantNotConnected_ReportsNotConfigured()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/access/status")).Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("configured").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("consented").GetBoolean());
    }

    // Secure-by-default: with no connected tenant, applying to Entra must fail loudly, not silently no-op.
    [Fact]
    public async Task Sync_WhenNotConnected_ReturnsApprovalNeeded()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var res = await client.PostAsync("/api/admin/access/viewer/sync", content: null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    // The consent link must carry an explicit HTTPS return URL so Microsoft doesn't fall back to the app's
    // default (possibly non-browser) reply address and break the flow with AADSTS500119.
    [Fact]
    public void ConsentUrl_CarriesHttpsReturnUrl_WhenConfigured()
    {
        const string ret = "https://host.example/api/admin/access/consent-complete";
        var svc = new EntraProvisioningService(
            new PowerPlatformOptions { TenantId = "tenant-1", ClientId = "client-1" },
            new EntraProvisioningOptions { ConsentReturnUrl = ret },
            new StubHttpClientFactory(), NullLogger<EntraProvisioningService>.Instance);

        Assert.Contains("/tenant-1/adminconsent?client_id=client-1", svc.ConsentUrl);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(ret), svc.ConsentUrl);
    }

    [Fact]
    public void ConsentUrl_OmitsReturnUrl_WhenNotConfigured()
    {
        var svc = new EntraProvisioningService(
            new PowerPlatformOptions { TenantId = "tenant-1", ClientId = "client-1" },
            new EntraProvisioningOptions(), new StubHttpClientFactory(), NullLogger<EntraProvisioningService>.Instance);

        Assert.DoesNotContain("redirect_uri", svc.ConsentUrl);
    }

    // The return page is hit by the admin's browser straight from Microsoft with no app token → must be
    // anonymous, plain-language, and safe (reflected error text HTML-encoded).
    [Fact]
    public async Task ConsentComplete_Approved_ShowsPlainSuccess_Anonymous()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/admin/access/consent-complete?admin_consent=True");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/html", res.Content.Headers.ContentType!.ToString());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("approved to manage roles in Microsoft Entra", html);
    }

    [Fact]
    public async Task ConsentComplete_Error_ShowsPlainFailure_AndEncodesReflectedDetails()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/admin/access/consent-complete?error=access_denied&error_description=%3Cscript%3Ex%3C/script%3E");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("was not approved", html);
        Assert.Contains("access_denied", html);
        Assert.DoesNotContain("<script>x</script>", html); // reflected text must be encoded
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
