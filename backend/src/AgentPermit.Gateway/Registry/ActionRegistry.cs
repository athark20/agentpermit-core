using System.Text.Json;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Registry;

/// <summary>A registered business action and its request parameter schema.</summary>
public sealed class ActionDefinition
{
    public string ActionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Risk { get; init; } = string.Empty;
    public string TargetSystem { get; init; } = string.Empty;

    /// <summary>
    /// Consequence class (LOW | MEDIUM | HIGH | CRITICAL) — how much damage this action can do. HIGH and
    /// CRITICAL actions are always enforced and never eligible for a warm-up downgrade. Declared explicitly
    /// via <c>consequenceTier</c> in the action definition, else derived from <see cref="Risk"/>; an
    /// unclassified action defaults to HIGH (secure by default). See <see cref="Domain.ConsequenceTier"/>.
    /// </summary>
    // Fully qualified: the enclosing type has both a "Domain" and a "ConsequenceTier" property, so the
    // Domain.ConsequenceTier static class must be named via its global path here.
    public string ConsequenceTier { get; init; } = global::AgentPermit.Gateway.Domain.ConsequenceTier.High;

    public JsonElement RequestSchema { get; init; }
}

public interface IActionRegistry
{
    ActionDefinition? Find(string? actionId);

    /// <summary>All registered business actions (for the admin catalog).</summary>
    IReadOnlyList<ActionDefinition> All();

    /// <summary>Validate a parameters payload against the action's request schema.</summary>
    IReadOnlyList<string> ValidateParameters(ActionDefinition action, JsonElement parameters);
}

/// <summary>Options bound from "Registry:ActionDefinitionFiles" (paths to *.json action definitions).</summary>
public sealed class ActionRegistryOptions
{
    /// <summary>Paths (relative to content root or absolute) to action definition JSON files.</summary>
    public List<string> ActionDefinitionFiles { get; set; } = new();

    /// <summary>
    /// Directories (relative to content root or absolute) scanned recursively for <c>*.json</c> action
    /// definitions. Lets a whole go-to-market catalog be dropped in without listing every file. Missing
    /// directories are ignored; a duplicate actionId keeps the first-loaded definition.
    /// </summary>
    public List<string> ActionDefinitionDirectories { get; set; } = new();
}

/// <summary>
/// Loads action definitions from JSON files at startup. The seed file mirrors the
/// single source of truth at config/first-action.json (linked into the build output).
/// </summary>
public sealed class FileActionRegistry : IActionRegistry
{
    private readonly Dictionary<string, ActionDefinition> _actions;

    public FileActionRegistry(ActionRegistryOptions options, string contentRoot)
    {
        _actions = new(StringComparer.OrdinalIgnoreCase);

        // 1) Explicitly listed files must exist (fail fast on a typo).
        foreach (var relativeOrAbsolute in options.ActionDefinitionFiles)
        {
            var path = Path.IsPathRooted(relativeOrAbsolute) ? relativeOrAbsolute : Path.Combine(contentRoot, relativeOrAbsolute);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Action definition file not found: {path}");
            LoadFile(path);
        }

        // 2) Directory catalogs — scanned recursively; missing dirs ignored; first definition of an id wins.
        foreach (var dir in options.ActionDefinitionDirectories)
        {
            var full = Path.IsPathRooted(dir) ? dir : Path.Combine(contentRoot, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var path in Directory.EnumerateFiles(full, "*.json", SearchOption.AllDirectories).OrderBy(p => p))
                LoadFile(path);
        }
    }

    private void LoadFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var risk = root.TryGetProperty("risk", out var rk) ? rk.GetString() ?? "" : "";
        // Prefer an explicit consequenceTier; otherwise derive it from the risk label. An action that
        // declares neither is treated as HIGH consequence so it is never silently warmed-up open.
        var consequenceTier = root.TryGetProperty("consequenceTier", out var ctv) && ctv.ValueKind == JsonValueKind.String
            ? Domain.ConsequenceTier.Normalize(ctv.GetString())
            : Domain.ConsequenceTier.FromRiskLabel(risk);
        var def = new ActionDefinition
        {
            ActionId = root.GetProperty("actionId").GetString() ?? throw new InvalidOperationException("actionId missing"),
            DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            Domain = root.TryGetProperty("domain", out var dm) ? dm.GetString() ?? "" : "",
            Risk = risk,
            ConsequenceTier = consequenceTier,
            TargetSystem = root.TryGetProperty("targetSystem", out var ts) ? ts.GetString() ?? "" : "",
            // Clone the schema element so it outlives the JsonDocument.
            RequestSchema = root.TryGetProperty("requestSchema", out var rs) ? rs.Clone() : default
        };
        _actions.TryAdd(def.ActionId, def);   // first definition of an id wins (explicit files take precedence)
    }

    public ActionDefinition? Find(string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return null;
        return _actions.TryGetValue(actionId, out var def) ? def : null;
    }

    public IReadOnlyList<ActionDefinition> All() => _actions.Values.OrderBy(a => a.ActionId).ToList();

    public IReadOnlyList<string> ValidateParameters(ActionDefinition action, JsonElement parameters)
    {
        if (action.RequestSchema.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        return SimpleSchemaValidator.Validate(action.RequestSchema, parameters);
    }
}
