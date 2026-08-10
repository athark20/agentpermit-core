using System.Text.Json;

namespace AgentPermit.Gateway.Registry;

/// <summary>
/// A small, dependency-free validator covering the JSON Schema keywords used by
/// the action request schemas (config/first-action.json): type, required,
/// additionalProperties:false, const, min/maxLength, minimum/maximum,
/// exclusiveMinimum. It is deliberately domain-neutral — it validates whatever
/// schema the action definition carries, not refund-specific rules.
/// </summary>
public static class SimpleSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement instance)
    {
        var errors = new List<string>();
        ValidateNode(schema, instance, "$", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            if (!MatchesType(typeEl.GetString()!, instance))
            {
                errors.Add($"{path}: expected type '{typeEl.GetString()}' but got '{instance.ValueKind}'.");
                return; // further checks are unreliable once the type is wrong
            }
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, instance, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(schema, instance, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, instance, path, errors);
                break;
        }

        if (schema.TryGetProperty("const", out var constEl) && !JsonEquals(constEl, instance))
            errors.Add($"{path}: value must equal const {constEl.GetRawText()}.");
    }

    private static void ValidateObject(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        var props = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : default;

        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in req.EnumerateArray())
            {
                var name = r.GetString();
                if (name is not null && !instance.TryGetProperty(name, out _))
                    errors.Add($"{path}: missing required property '{name}'.");
            }
        }

        var additionalAllowed = !(schema.TryGetProperty("additionalProperties", out var ap)
                                   && ap.ValueKind == JsonValueKind.False);

        foreach (var member in instance.EnumerateObject())
        {
            if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(member.Name, out var propSchema))
                ValidateNode(propSchema, member.Value, $"{path}.{member.Name}", errors);
            else if (!additionalAllowed)
                errors.Add($"{path}: additional property '{member.Name}' is not allowed.");
        }
    }

    private static void ValidateString(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        var s = instance.GetString() ?? string.Empty;
        if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var mn) && s.Length < mn)
            errors.Add($"{path}: string shorter than minLength {mn}.");
        if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var mx) && s.Length > mx)
            errors.Add($"{path}: string longer than maxLength {mx}.");
    }

    private static void ValidateNumber(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        var v = instance.GetDecimal();
        if (schema.TryGetProperty("minimum", out var min) && min.TryGetDecimal(out var mn) && v < mn)
            errors.Add($"{path}: number below minimum {mn}.");
        if (schema.TryGetProperty("maximum", out var max) && max.TryGetDecimal(out var mx) && v > mx)
            errors.Add($"{path}: number above maximum {mx}.");
        if (schema.TryGetProperty("exclusiveMinimum", out var xmin) && xmin.TryGetDecimal(out var xmn) && v <= xmn)
            errors.Add($"{path}: number must be greater than {xmn}.");
    }

    private static bool MatchesType(string type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            _ => a.GetRawText() == b.GetRawText()
        };
    }
}
