using System.Security.Cryptography;
using System.Text;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Gate for admin operations (e.g. publishing policy). Interim admin-key gate: a shared secret
/// only administrators hold. When no key is configured (local/dev/tests), admin operations are
/// allowed. In production the key is a Container App secret. (Entra app-role admin is the next
/// refinement, aligned with the Entra-groups-as-roles direction.)
/// </summary>
public sealed class AdminOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string HeaderName { get; set; } = "X-Admin-Key";

    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    /// <summary>True if the provided key matches (constant-time), or if no key is configured.</summary>
    public bool Matches(string? provided)
    {
        if (!IsConfigured) return true;
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(ApiKey));
    }
}
