using System.Security.Claims;
using System.Text.Json;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Human-in-the-loop personas. AgentPermit's stored role membership (Roles &amp; access) decides who
/// can view, approve, manage agents or administer — and the approver identity recorded on a decision
/// is DERIVED from the authenticated caller, never trusted from the body (except the service-key
/// path used by the Teams workflow).
/// </summary>
public sealed class PersonaTests
{
    private static readonly AdminAuthOptions Entra = new()
    {
        Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0",
        Audience = "api://test",
    };

    private static InMemoryConfigStore StoreWith(params (string role, string[] emails)[] roles)
    {
        var store = new InMemoryConfigStore();
        foreach (var (role, emails) in roles)
        {
            var json = JsonSerializer.Serialize(new { members = emails.Select(e => new { email = e, displayName = e }).ToArray() });
            store.UpsertAsync("access-role", role, json, CancellationToken.None).GetAwaiter().GetResult();
        }
        return store;
    }

    private static AccessDirectoryService Directory(IConfigStore store, AdminOptions? key = null, AdminAuthOptions? entra = null)
        => new(store, key ?? new AdminOptions(), entra ?? Entra, NullLogger<AccessDirectoryService>.Instance);

    private static DefaultHttpContext UserContext(string email, params (string type, string value)[] extra)
    {
        var claims = new List<Claim> { new("preferred_username", email), new("name", "Test User") };
        claims.AddRange(extra.Select(c => new Claim(c.type, c.value)));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return ctx;
    }

    [Fact]
    public async Task DevOpen_IsAdmin()
    {
        var dir = Directory(StoreWith(), new AdminOptions(), new AdminAuthOptions());   // nothing configured
        var roles = await dir.RolesForAsync(new DefaultHttpContext());
        Assert.Contains("admin", roles);
    }

    [Fact]
    public async Task ServiceKey_IsAdmin()
    {
        var key = new AdminOptions { ApiKey = "super-secret-key" };
        var dir = Directory(StoreWith(), key);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[key.HeaderName] = "super-secret-key";
        Assert.True(dir.IsServiceCaller(ctx));
        Assert.Contains("admin", await dir.RolesForAsync(ctx));
    }

    [Fact]
    public async Task StoredMembership_GrantsRoles_CaseInsensitive()
    {
        var dir = Directory(StoreWith(("approver", new[] { "Fatima.Hassan@bank.ae" }), ("viewer", new[] { "omar@bank.ae" })));
        var approver = await dir.RolesForAsync(UserContext("fatima.hassan@BANK.AE"));
        Assert.Equal(new[] { "approver" }, approver);
        var viewer = await dir.RolesForAsync(UserContext("omar@bank.ae"));
        Assert.Equal(new[] { "viewer" }, viewer);
    }

    [Fact]
    public async Task EntraAdminClaim_GrantsAdmin()
    {
        var dir = Directory(StoreWith());
        var roles = await dir.RolesForAsync(UserContext("boss@bank.ae", ("roles", "Admin.Access")));
        Assert.Contains("admin", roles);
    }

    [Fact]
    public async Task NoMembership_NoRoles()
    {
        var dir = Directory(StoreWith(("viewer", new[] { "someone-else@bank.ae" })));
        Assert.Empty(await dir.RolesForAsync(UserContext("stranger@bank.ae")));
    }

    [Fact]
    public void ApproverIdentity_DerivedFromToken_NotBody()
    {
        var dir = Directory(StoreWith(("approver", new[] { "fatima@bank.ae" })));
        var identity = dir.ResolveApproverIdentity(UserContext("fatima@bank.ae"), bodyApproverId: "spoofed-somebody-else");
        Assert.Equal("Test User <fatima@bank.ae>", identity);   // body value ignored on the Entra path
    }

    [Fact]
    public void ApproverIdentity_ServiceKey_PassesThroughTeamsResponder()
    {
        var key = new AdminOptions { ApiKey = "super-secret-key" };
        var dir = Directory(StoreWith(), key);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[key.HeaderName] = "super-secret-key";
        Assert.Equal("responder@bank.ae", dir.ResolveApproverIdentity(ctx, "responder@bank.ae"));
        Assert.Equal("teams-workflow", dir.ResolveApproverIdentity(ctx, null));
    }

    [Fact]
    public async Task StoredAdminRole_GrantsIsAuthorized()
    {
        var store = StoreWith(("admin", new[] { "ops-lead@bank.ae" }));
        var dir = Directory(store);
        var auth = new AdminAuthorizationService(new AdminOptions(), Entra, dir, NullLogger<AdminAuthorizationService>.Instance);
        var ctx = UserContext("ops-lead@bank.ae");
        await dir.RolesForAsync(ctx);   // warm the cache so the sync path can see membership
        Assert.True(auth.IsAuthorized(ctx));
    }

    [Fact]
    public async Task Viewer_IsMember_ButNotAdmin()
    {
        var dir = Directory(StoreWith(("viewer", new[] { "watcher@bank.ae" })));
        var auth = new AdminAuthorizationService(new AdminOptions(), Entra, dir, NullLogger<AdminAuthorizationService>.Instance);
        var ctx = UserContext("watcher@bank.ae");
        Assert.True(await auth.IsMemberAsync(ctx));
        Assert.False(auth.IsAuthorized(ctx));
    }

    [Fact]
    public async Task RolelessUser_IsNotMember()
    {
        var dir = Directory(StoreWith(("viewer", new[] { "watcher@bank.ae" })));
        var auth = new AdminAuthorizationService(new AdminOptions(), Entra, dir, NullLogger<AdminAuthorizationService>.Instance);
        Assert.False(await auth.IsMemberAsync(UserContext("stranger@bank.ae")));
    }

    [Fact]
    public async Task Invalidate_PicksUpMembershipChanges()
    {
        var store = StoreWith(("approver", new[] { "old@bank.ae" }));
        var dir = Directory(store);
        Assert.Empty(await dir.RolesForAsync(UserContext("new@bank.ae")));

        var json = JsonSerializer.Serialize(new { members = new[] { new { email = "new@bank.ae", displayName = "New" } } });
        await store.UpsertAsync("access-role", "approver", json, CancellationToken.None);
        dir.Invalidate();
        Assert.Contains("approver", await dir.RolesForAsync(UserContext("new@bank.ae")));
    }
}
