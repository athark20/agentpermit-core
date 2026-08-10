using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Signed receipts must be independently verifiable: a verifier with only the public key can
/// confirm the signature over the receipt's content hash. Uses an in-memory RSA signer that
/// mirrors what KeyVaultReceiptSigner does (RS256 over the SHA-256 content hash).
/// </summary>
public sealed class ReceiptSigningTests
{
    private sealed class RsaSigner : IReceiptSigner
    {
        public readonly RSA Rsa = RSA.Create(2048);
        public bool Enabled => true;
        public Task<ReceiptSignature?> SignAsync(string receiptHashHex, CancellationToken ct)
        {
            var digest = Convert.FromHexString(receiptHashHex);
            var sig = Rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Task.FromResult<ReceiptSignature?>(new ReceiptSignature("RS256", "test-key", Convert.ToBase64String(sig)));
        }
        public Task<bool> VerifyAsync(string hashHex, string signatureBase64, CancellationToken ct)
            => Task.FromResult(Rsa.VerifyHash(Convert.FromHexString(hashHex), Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task Receipt_IsSigned_AndVerifiesWithPublicKey()
    {
        var signer = new RsaSigner();
        using var factory = new GatewayFactory { SignerOverride = signer };
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        var receipt = body!.Receipt;

        Assert.NotNull(receipt);
        Assert.False(string.IsNullOrEmpty(receipt!.Signature));
        Assert.Equal("RS256", receipt.SignatureAlgorithm);

        // A verifier with only the public key confirms the signature over the receipt hash.
        using var publicOnly = RSA.Create();
        publicOnly.ImportParameters(signer.Rsa.ExportParameters(false)); // public params only
        var verified = publicOnly.VerifyHash(
            Convert.FromHexString(receipt.ReceiptHash),
            Convert.FromBase64String(receipt.Signature!),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(verified);

        // Tampering the hash breaks verification.
        var tampered = receipt.ReceiptHash[..^2] + (receipt.ReceiptHash.EndsWith("00") ? "11" : "00");
        Assert.False(publicOnly.VerifyHash(
            Convert.FromHexString(tampered), Convert.FromBase64String(receipt.Signature!),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task Receipt_IsUnsigned_WhenSignerDisabled()
    {
        using var factory = new GatewayFactory(); // default: NullReceiptSigner
        var client = factory.CreateClient();

        var body = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.NotNull(body!.Receipt);
        Assert.Null(body.Receipt!.Signature);
    }
}
