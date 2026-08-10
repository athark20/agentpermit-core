using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The Entra-secured admin control-plane surface our own admin UI consumes: registered agents and
/// the signed evidence trail. Also proves independent re-verification of a receipt (recompute the
/// canonical hash + verify the signature) — the evidence guarantee at the heart of the product.
/// </summary>
public sealed class AdminEndpointsTests
{
    private sealed class RsaSigner : IReceiptSigner
    {
        private readonly System.Security.Cryptography.RSA _rsa = System.Security.Cryptography.RSA.Create(2048);
        public bool Enabled => true;
        public Task<ReceiptSignature?> SignAsync(string hashHex, CancellationToken ct)
        {
            var sig = _rsa.SignHash(Convert.FromHexString(hashHex),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            return Task.FromResult<ReceiptSignature?>(new ReceiptSignature("RS256", "test-key", Convert.ToBase64String(sig)));
        }
        public Task<bool> VerifyAsync(string hashHex, string signatureBase64, CancellationToken ct)
            => Task.FromResult(_rsa.VerifyHash(Convert.FromHexString(hashHex), Convert.FromBase64String(signatureBase64),
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task Agents_ListsRegisteredAgents()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/agents")).Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(a => a.GetProperty("agentId").GetString());
        Assert.Contains("copilot-refund-demo", ids);
    }

    [Fact]
    public async Task Admin_RequiresKey_WhenConfigured()
    {
        using var factory = new GatewayFactory { AdminApiKey = "s3cret-admin" };
        var client = factory.CreateClient();

        var noKey = await client.GetAsync("/api/admin/agents");
        Assert.Equal(HttpStatusCode.Forbidden, noKey.StatusCode);

        var msg = new HttpRequestMessage(HttpMethod.Get, "/api/admin/agents");
        msg.Headers.Add("X-Admin-Key", "s3cret-admin");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(msg)).StatusCode);
    }

    // Execute a refund → a signed receipt exists → the admin can re-verify it end to end.
    [Fact]
    public async Task Evidence_ReceiptListsAndReVerifies()
    {
        using var factory = new GatewayFactory { SignerOverride = new RsaSigner() };
        var client = factory.CreateClient();

        var resp = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", resp!.Decision);
        var actionRequestId = resp.ActionRequestId!.Value;

        var list = JsonDocument.Parse(await (await client.GetAsync("/api/admin/receipts")).Content.ReadAsStringAsync());
        Assert.Contains(list.RootElement.EnumerateArray(),
            r => r.GetProperty("actionRequestId").GetGuid() == actionRequestId);

        var verify = JsonDocument.Parse(
            await (await client.GetAsync($"/api/admin/receipts/{actionRequestId}/verify")).Content.ReadAsStringAsync());
        Assert.True(verify.RootElement.GetProperty("integrityOk").GetBoolean());
        Assert.True(verify.RootElement.GetProperty("signatureOk").GetBoolean());
        Assert.True(verify.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task Verify_UnknownReceipt_Returns404()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var resp = await client.GetAsync($"/api/admin/receipts/{Guid.NewGuid()}/verify");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
