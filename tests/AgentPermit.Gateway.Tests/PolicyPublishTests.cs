using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The policy publisher is the moat bridge: an authored policy is validated, signed, frozen, and
/// enforced by the runtime with NO redeploy. These prove publish → immediate enforcement change.
/// </summary>
public sealed class PolicyPublishTests
{
    private sealed class StubSigner : IReceiptSigner
    {
        public bool Enabled => true;
        public Task<ReceiptSignature?> SignAsync(string h, CancellationToken ct)
            => Task.FromResult<ReceiptSignature?>(new ReceiptSignature("RS256", "test-key", "c2ln"));
        public Task<bool> VerifyAsync(string h, string s, CancellationToken ct) => Task.FromResult(true);
    }

    // Author a snapshot whose auto-approve limit is `allowBelow`; everything else denies by default.
    private static object Snapshot(string version, decimal allowBelow) => new
    {
        policyCode = "Refund.Issue.AmountThreshold",
        version,
        defaultDecision = "DENY",
        rules = new object[]
        {
            new
            {
                id = "allow", name = "auto-approve", priority = 10, actionId = "Refund.Issue",
                conditions = new object[] { new { field = "amount", @operator = "lessThan", value = allowBelow } },
                decision = "ALLOW"
            }
        }
    };

    private static async Task<string> Decision(HttpClient c, decimal amount)
    {
        var body = await (await c.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(amount)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        return body!.Decision;
    }

    // THE killer test: publishing a new policy changes enforcement instantly, no redeploy.
    [Fact]
    public async Task PublishedPolicy_IsEnforced_Immediately()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        // Baseline (seed v2.0.0): 500 is in the approval band.
        Assert.Equal("REQUIRE_APPROVAL", await Decision(client, 500m));

        // Publish a new policy that auto-approves anything under 1000.
        var pub = await client.PostAsJsonAsync("/api/policies/publish", Snapshot("3.0.0", 1000m));
        pub.EnsureSuccessStatusCode();

        // Same request, now ALLOW — enforced from the freshly published snapshot, no redeploy.
        Assert.Equal("ALLOW", await Decision(client, 500m));

        var active = JsonDocument.Parse(await (await client.GetAsync("/api/policies/active")).Content.ReadAsStringAsync());
        Assert.Equal("3.0.0", active.RootElement.GetProperty("version").GetString());
    }

    // When an admin key is configured, publishing without it is forbidden; with it, allowed.
    [Fact]
    public async Task Publish_RequiresAdminKey_WhenConfigured()
    {
        using var factory = new GatewayFactory { AdminApiKey = "s3cret-admin" };
        var client = factory.CreateClient();

        var noKey = await client.PostAsJsonAsync("/api/policies/publish", Snapshot("5.0.0", 100m));
        Assert.Equal(HttpStatusCode.Forbidden, noKey.StatusCode);

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/policies/publish")
        {
            Content = JsonContent.Create(Snapshot("5.0.0", 100m))
        };
        msg.Headers.Add("X-Admin-Key", "s3cret-admin");
        var withKey = await client.SendAsync(msg);
        withKey.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Publish_InvalidDecision_Rejected()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var bad = new
        {
            policyCode = "X", version = "9.9",
            rules = new object[] { new { id = "r", name = "r", priority = 1, decision = "MAYBE", conditions = new object[0] } }
        };
        var resp = await client.PostAsJsonAsync("/api/policies/publish", bad);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Publish_Signs_AndListsVersion()
    {
        using var factory = new GatewayFactory { SignerOverride = new StubSigner() };
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/policies/publish", Snapshot("4.1.0", 300m));

        var active = JsonDocument.Parse(await (await client.GetAsync("/api/policies/active")).Content.ReadAsStringAsync());
        Assert.True(active.RootElement.GetProperty("signed").GetBoolean());
        Assert.Equal("RS256", active.RootElement.GetProperty("signatureAlgorithm").GetString());

        var versions = JsonDocument.Parse(await (await client.GetAsync("/api/policies/versions")).Content.ReadAsStringAsync());
        Assert.Contains("4.1.0", versions.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()));
    }
}
