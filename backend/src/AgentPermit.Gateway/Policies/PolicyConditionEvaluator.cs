using System.Globalization;
using System.Text.Json;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// Deterministic operator evaluation for a single condition. Supports the operators in
/// schemas/Policy.schema.json: equals, notEquals, greaterThan(OrEqual), lessThan(OrEqual),
/// in, notIn, exists. Comparisons are numeric when both sides parse as numbers, else
/// date-time when both parse as timestamps, else ordinal string.
/// </summary>
public static class PolicyConditionEvaluator
{
    public static bool Matches(JsonElement? field, string op, JsonElement value)
    {
        var present = field is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

        switch (op)
        {
            case "exists":
                var want = value.ValueKind != JsonValueKind.False; // exists:true (default) vs exists:false
                return present == want;
            case "in":
                return present && InList(field!.Value, value);
            case "notIn":
                return !present || !InList(field!.Value, value);
        }

        if (!present) return false; // ordering/equality against a missing field never matches
        var f = field!.Value;

        return op switch
        {
            "equals" => AreEqual(f, value),
            "notEquals" => !AreEqual(f, value),
            "greaterThan" => Compare(f, value) is { } c1 && c1 > 0,
            "greaterThanOrEqual" => Compare(f, value) is { } c2 && c2 >= 0,
            "lessThan" => Compare(f, value) is { } c3 && c3 < 0,
            "lessThanOrEqual" => Compare(f, value) is { } c4 && c4 <= 0,
            _ => false
        };
    }

    private static bool InList(JsonElement field, JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in list.EnumerateArray())
            if (AreEqual(field, item)) return true;
        return false;
    }

    private static bool AreEqual(JsonElement a, JsonElement b)
    {
        if (TryDecimal(a, out var da) && TryDecimal(b, out var db)) return da == db;
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return string.Equals(a.GetString(), b.GetString(), StringComparison.OrdinalIgnoreCase);
        if ((a.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
            (b.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return a.ValueKind == b.ValueKind;
        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns sign of (a - b), or null if the two aren't comparably ordered.</summary>
    private static int? Compare(JsonElement a, JsonElement b)
    {
        if (TryDecimal(a, out var da) && TryDecimal(b, out var db)) return decimal.Compare(da, db);
        if (TryDate(a, out var ta) && TryDate(b, out var tb)) return DateTimeOffset.Compare(ta, tb);
        var sa = a.ValueKind == JsonValueKind.String ? a.GetString() : a.ToString();
        var sb = b.ValueKind == JsonValueKind.String ? b.GetString() : b.ToString();
        return string.Compare(sa, sb, StringComparison.Ordinal);
    }

    private static bool TryDecimal(JsonElement e, out decimal d)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetDecimal(out d);
        if (e.ValueKind == JsonValueKind.String)
            return decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out d);
        d = 0m;
        return false;
    }

    private static bool TryDate(JsonElement e, out DateTimeOffset t)
    {
        if (e.ValueKind == JsonValueKind.String)
            return DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);
        t = default;
        return false;
    }
}
