using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentPermit.Gateway.Common;

/// <summary>
/// Deterministic ("canonical") JSON serialization plus SHA-256 hashing.
///
/// The payload hash is the tamper-evidence primitive for the whole platform:
/// a request is hashed at intake, the hash is frozen into the approval and the
/// receipt, and re-verified before execution. Because a hash is only meaningful
/// if identical logical payloads always produce identical bytes, we canonicalize
/// first: object keys are sorted, whitespace is removed, and numbers keep their
/// original textual form.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Serialize a JsonElement to canonical form (sorted keys, no whitespace).</summary>
    public static string Serialize(JsonElement element)
    {
        var sb = new StringBuilder();
        WriteCanonical(element, sb);
        return sb.ToString();
    }

    /// <summary>Compute the uppercase hex SHA-256 of the canonical form (matches ^[A-Fa-f0-9]{64}$).</summary>
    public static string Hash(JsonElement element) => HashRaw(Serialize(element));

    /// <summary>Compute the uppercase hex SHA-256 of an arbitrary string.</summary>
    public static string HashRaw(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes); // uppercase hex, length 64
    }

    private static void WriteCanonical(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonString(prop.Name, sb);
                    sb.Append(':');
                    WriteCanonical(prop.Value, sb);
                }
                sb.Append('}');
                break;

            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    WriteCanonical(item, sb);
                }
                sb.Append(']');
                break;

            case JsonValueKind.String:
                WriteJsonString(element.GetString() ?? string.Empty, sb);
                break;

            case JsonValueKind.Number:
                // Preserve the raw numeric text so 750 and 750.0 hash to their as-sent form.
                sb.Append(element.GetRawText());
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                sb.Append("null");
                break;
        }
    }

    private static void WriteJsonString(string value, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
