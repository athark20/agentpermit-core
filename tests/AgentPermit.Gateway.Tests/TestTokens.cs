using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Mints agent access tokens for the tests. The gateway runs its real JwtBearer handler; only the key
/// material is local (see <see cref="GatewayFactory"/>), so signature, issuer, audience and lifetime are all
/// genuinely validated.
/// </summary>
public static class TestTokens
{
    public const string TenantId = "22222222-2222-2222-2222-222222222222";
    public static readonly string Authority = $"https://login.microsoftonline.com/{TenantId}/v2.0";
    public static readonly string Issuer = Authority;
    public const string Audience = "api://agentpermit-gateway";

    public static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("agentpermit-test-signing-key-which-is-long-enough-for-hs256"));

    /// <summary>An app-only token for a workload identity: azp = the agent's Entra client id, no user.</summary>
    public static string AppOnly(string clientId, string? audience = null, string? issuer = null, TimeSpan? lifetime = null)
        => Build(new[]
        {
            new Claim("azp", clientId),
            new Claim("oid", "33333333-3333-3333-3333-333333333333"), // the service principal's own object id
            new Claim("idtyp", "app"),
            new Claim("roles", "AgentPermit.Act")
        }, audience, issuer, lifetime);

    /// <summary>A delegated token: the agent acts on behalf of a signed-in human.</summary>
    public static string OnBehalfOf(string clientId, string userPrincipalName)
        => Build(new[]
        {
            new Claim("azp", clientId),
            new Claim("preferred_username", userPrincipalName),
            new Claim("oid", "44444444-4444-4444-4444-444444444444"),
            new Claim("scp", "Actions.Request")
        }, null, null, null);

    /// <summary>A delegated token that names the user only by object id (no UPN claim).</summary>
    public static string OnBehalfOfObjectId(string clientId, string objectId)
        => Build(new[] { new Claim("azp", clientId), new Claim("oid", objectId), new Claim("scp", "Actions.Request") },
                 null, null, null);

    /// <summary>A token signed with the wrong key — must be rejected.</summary>
    public static string WrongKey(string clientId)
    {
        var bad = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-signing-key-of-sufficient-length"));
        return Write(new[] { new Claim("azp", clientId) }, Issuer, Audience, DateTime.UtcNow.AddMinutes(10), bad);
    }

    private static string Build(Claim[] claims, string? audience, string? issuer, TimeSpan? lifetime)
        => Write(claims, issuer ?? Issuer, audience ?? Audience,
                 DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)), SigningKey);

    private static string Write(Claim[] claims, string issuer, string audience, DateTime expires, SecurityKey key)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow.AddMinutes(-2),
            IssuedAt = DateTime.UtcNow.AddMinutes(-2),
            Expires = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
