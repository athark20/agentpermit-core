using System.Security.Claims;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>The admin gate: dev-open when nothing configured, else admin key OR Entra group/app-role.</summary>
public sealed class AdminAuthorizationServiceTests
{
    private static AdminAuthorizationService Svc(AdminOptions key, AdminAuthOptions entra)
        => new(key, entra,
            new AccessDirectoryService(new AgentPermit.Gateway.Persistence.InMemoryConfigStore(), key, entra,
                NullLogger<AccessDirectoryService>.Instance),
            NullLogger<AdminAuthorizationService>.Instance);

    private static HttpContext Ctx(ClaimsPrincipal? user = null, (string, string)? header = null)
    {
        var ctx = new DefaultHttpContext();
        if (user is not null) ctx.User = user;
        if (header is { } h) ctx.Request.Headers[h.Item1] = h.Item2;
        return ctx;
    }

    private static ClaimsPrincipal With(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test")); // authenticated (has an auth type)

    [Fact]
    public void DevOpen_WhenNothingConfigured()
        => Assert.True(Svc(new AdminOptions(), new AdminAuthOptions()).IsAuthorized(Ctx()));

    [Fact]
    public void Key_GatesWhenConfigured()
    {
        var svc = Svc(new AdminOptions { ApiKey = "s3cret" }, new AdminAuthOptions());
        Assert.False(svc.IsAuthorized(Ctx()));
        Assert.True(svc.IsAuthorized(Ctx(header: ("X-Admin-Key", "s3cret"))));
    }

    [Fact]
    public void Entra_AppRole_Authorizes()
    {
        var entra = new AdminAuthOptions { Authority = "https://login.microsoftonline.com/t/v2.0", Audience = "api://x" };
        var svc = Svc(new AdminOptions(), entra);
        Assert.True(svc.IsAuthorized(Ctx(With(new Claim("roles", "Admin.Access")))));
        Assert.False(svc.IsAuthorized(Ctx(With(new Claim("roles", "Some.Other")))));
        Assert.False(svc.IsAuthorized(Ctx())); // no token, key not configured
    }

    [Fact]
    public void Entra_Group_Authorizes()
    {
        var entra = new AdminAuthOptions
        {
            Authority = "https://login.microsoftonline.com/t/v2.0", Audience = "api://x",
            AdminGroupId = "group-123", AdminAppRole = "Admin.Access",
        };
        var svc = Svc(new AdminOptions(), entra);
        Assert.True(svc.IsAuthorized(Ctx(With(new Claim("groups", "group-123")))));
        Assert.False(svc.IsAuthorized(Ctx(With(new Claim("groups", "group-999")))));
    }

    [Fact]
    public void ValidIssuers_DerivesBothV1AndV2()
    {
        var entra = new AdminAuthOptions { Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0" };
        var issuers = entra.ValidIssuers();
        Assert.Contains("https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0", issuers);
        Assert.Contains("https://sts.windows.net/00000000-0000-0000-0000-000000000000/", issuers);
    }
}
