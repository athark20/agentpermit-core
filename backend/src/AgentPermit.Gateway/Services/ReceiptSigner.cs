using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace AgentPermit.Gateway.Services;

public sealed class ReceiptSigningOptions
{
    /// <summary>
    /// Full Key Vault key identifier used to sign receipts, e.g.
    /// https://kv-....vault.azure.net/keys/receipt-signing-key. Empty = signing disabled.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;
}

/// <summary>A detached signature over a receipt's content hash.</summary>
public sealed record ReceiptSignature(string Algorithm, string KeyId, string Value);

/// <summary>
/// Signs and verifies a SHA-256 hash (hex) with a Key Vault key. Used for both action receipts and
/// published policy snapshots — the private key never leaves Key Vault.
/// </summary>
public interface IReceiptSigner
{
    bool Enabled { get; }

    /// <summary>Sign a SHA-256 hash (hex). Returns null when signing is disabled.</summary>
    Task<ReceiptSignature?> SignAsync(string receiptHashHex, CancellationToken ct);

    /// <summary>Verify a signature over a SHA-256 hash (hex). Returns true when signing is disabled.</summary>
    Task<bool> VerifyAsync(string hashHex, string signatureBase64, CancellationToken ct);
}

/// <summary>No-op signer for local/dev/tests — receipts/snapshots remain unsigned.</summary>
public sealed class NullReceiptSigner : IReceiptSigner
{
    public bool Enabled => false;
    public Task<ReceiptSignature?> SignAsync(string receiptHashHex, CancellationToken ct)
        => Task.FromResult<ReceiptSignature?>(null);
    public Task<bool> VerifyAsync(string hashHex, string signatureBase64, CancellationToken ct)
        => Task.FromResult(true);
}

/// <summary>
/// Signs the receipt content hash with an RSA key in Key Vault (RS256). The gateway's managed
/// identity needs "Key Vault Crypto User". Verifiers use the exported public key — the private
/// key is never exposed. This makes each receipt independently, cryptographically verifiable.
/// </summary>
public sealed class KeyVaultReceiptSigner : IReceiptSigner
{
    private readonly CryptographyClient _crypto;

    public bool Enabled => true;

    public KeyVaultReceiptSigner(string keyId)
        => _crypto = new CryptographyClient(new Uri(keyId), new DefaultAzureCredential());

    public async Task<ReceiptSignature?> SignAsync(string receiptHashHex, CancellationToken ct)
    {
        // The hash IS the SHA-256 digest; sign it directly with RS256.
        var digest = Convert.FromHexString(receiptHashHex);
        var result = await _crypto.SignAsync(SignatureAlgorithm.RS256, digest, ct);
        return new ReceiptSignature("RS256", result.KeyId, Convert.ToBase64String(result.Signature));
    }

    public async Task<bool> VerifyAsync(string hashHex, string signatureBase64, CancellationToken ct)
    {
        var digest = Convert.FromHexString(hashHex);
        var result = await _crypto.VerifyAsync(SignatureAlgorithm.RS256, digest, Convert.FromBase64String(signatureBase64), ct);
        return result.IsValid;
    }
}
