using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// Computes the windowed aggregates the active snapshot's rules reference for one action, BEFORE the
/// synchronous policy engine runs. Each distinct aggregate spec is queried once. When the snapshot has no
/// aggregate conditions for the action, no store call is made at all — the engine's "no per-request DB call"
/// property is preserved for everyone who doesn't use the feature.
///
/// Every aggregate INCLUDES the current request's own contribution (its amount, itself as one more, its own
/// distinct value), so "sum &gt; 5000" means "with this request, the window would exceed 5000".
///
/// Failure mode is closed: a store error propagates and the request fails, rather than a rule silently
/// evaluating against a missing value.
///
/// Concurrency: <see cref="ReservationKeys"/> names the locks (see <see cref="IAggregateLock"/>) a request holds
/// from before these totals are computed until it is recorded, so two requests in the same group are decided one
/// after the other and the second one's total includes the first.
/// </summary>
public sealed partial class PolicyAggregateProvider(IAgentPermitStore store, IPolicySnapshotProvider snapshots)
{
    /// <summary>Statuses that represent real or pending exposure (scope = committed).</summary>
    public static readonly string[] CommittedStatuses =
    {
        RequestState.Succeeded, RequestState.Executing, RequestState.ExecutionPending,
        RequestState.Approved, RequestState.AwaitingApproval
    };

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex SafeName();

    public async Task<IReadOnlyDictionary<string, JsonElement>?> ComputeAsync(
        ActionDefinition action, JsonElement parameters, string agentId, string? rootAgentId, string? initiatingUserId,
        CancellationToken ct)
    {
        var snapshot = snapshots.Current;
        var specs = new Dictionary<string, (PolicyCondition Condition, string RuleActionId)>(StringComparer.Ordinal);
        foreach (var rule in snapshot.Rules)
        {
            if (!string.IsNullOrEmpty(rule.ActionId) && !string.Equals(rule.ActionId, action.ActionId, StringComparison.OrdinalIgnoreCase))
                continue;
            // A rule for "any action" (no action id, or an empty one from the console) aggregates the current action.
            var ruleActionId = string.IsNullOrEmpty(rule.ActionId) ? action.ActionId : rule.ActionId;
            foreach (var c in rule.Conditions)
                if (c.Aggregate is not null)
                    specs.TryAdd(c.Aggregate.Key(c.Field, ruleActionId), (c, ruleActionId));
        }
        if (specs.Count == 0) return null;

        var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, (condition, ruleActionId)) in specs)
            results[key] = await ComputeOneAsync(condition, ruleActionId, parameters, agentId, rootAgentId, initiatingUserId, ct);
        return results;
    }

    private async Task<JsonElement> ComputeOneAsync(
        PolicyCondition condition, string ruleActionId, JsonElement parameters,
        string agentId, string? rootAgentId, string? initiatingUserId, CancellationToken ct)
    {
        var agg = condition.Aggregate!;
        var function = agg.Function.ToLowerInvariant();
        var window = ParseWindow(agg.Window);
        var since = DateTimeOffset.UtcNow - window;

        // Resolve group-by constraints from the current request. A parameter the request doesn't carry means
        // no prior row can share it: only the current request contributes.
        string? gAgent = null, gRoot = null, gUser = null;
        var paramEquals = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupable = true;
        foreach (var g in agg.GroupBy ?? Array.Empty<string>())
        {
            switch (g)
            {
                case "agentId": gAgent = agentId; break;
                case "rootAgentId": gRoot = rootAgentId ?? string.Empty; if (rootAgentId is null) groupable = false; break;
                case "initiatingUserId": gUser = initiatingUserId ?? string.Empty; if (initiatingUserId is null) groupable = false; break;
                default:
                    if (!SafeName().IsMatch(g)) throw new InvalidOperationException($"Aggregate groupBy '{g}' is not a valid field name.");
                    var v = ParamAsString(parameters, g);
                    if (v is null) groupable = false; else paramEquals[g] = v;
                    break;
            }
        }

        var valueField = function == PolicyAggregate.Count ? null : condition.Field;
        if (valueField is not null && !SafeName().IsMatch(valueField))
            throw new InvalidOperationException($"Aggregate field '{valueField}' is not a valid field name.");

        var currentValue = valueField is null ? null : ParamAsString(parameters, valueField);

        IReadOnlyList<string?> prior = Array.Empty<string?>();
        if (groupable)
        {
            prior = await store.ListParameterValuesAsync(new AggregateQuery
            {
                ActionIds = agg.Actions is { Length: > 0 } ? agg.Actions : new[] { ruleActionId },
                Since = since,
                Statuses = string.Equals(agg.Scope, PolicyAggregate.ScopeAll, StringComparison.OrdinalIgnoreCase) ? null : CommittedStatuses,
                AgentId = gAgent,
                RootAgentId = gRoot,
                InitiatingUserId = gUser,
                ParameterEquals = paramEquals,
                ValueField = valueField
            }, ct);
        }

        switch (function)
        {
            case PolicyAggregate.Count:
                return JsonSerializer.SerializeToElement(prior.Count + 1);

            case PolicyAggregate.Distinct:
            {
                var set = new HashSet<string>(prior.Where(v => v is not null).Select(v => v!), StringComparer.OrdinalIgnoreCase);
                if (currentValue is not null) set.Add(currentValue);
                return JsonSerializer.SerializeToElement(set.Count);
            }

            case PolicyAggregate.Max:
            {
                decimal? max = null;
                foreach (var v in prior.Append(currentValue))
                    if (TryDecimal(v, out var d) && (max is null || d > max)) max = d;
                return JsonSerializer.SerializeToElement(max ?? 0m);
            }

            case PolicyAggregate.Sum:
            default:
            {
                var sum = 0m;
                foreach (var v in prior.Append(currentValue))
                    if (TryDecimal(v, out var d)) sum += d;
                return JsonSerializer.SerializeToElement(sum);
            }
        }
    }

    /// <summary>
    /// The limit locks this request must hold while it is checked and recorded: one for every aggregate in the
    /// active snapshot that this request is checked against (its action's rules) or would count towards (a rule
    /// on another action whose <c>actions</c> include this one), scoped to this request's own group values. A
    /// group value the request doesn't carry means it can't share that total with anyone, so no lock is needed.
    /// Empty when the snapshot has no aggregates, so the common case takes no lock at all.
    /// </summary>
    public IReadOnlyList<string> ReservationKeys(
        ActionDefinition action, JsonElement parameters, string agentId, string? rootAgentId, string? initiatingUserId)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in snapshots.Current.Rules)
        {
            var checkedAgainst = string.IsNullOrEmpty(rule.ActionId)
                                 || string.Equals(rule.ActionId, action.ActionId, StringComparison.OrdinalIgnoreCase);
            foreach (var c in rule.Conditions)
            {
                if (c.Aggregate is not { } agg) continue;
                var ruleActionId = string.IsNullOrEmpty(rule.ActionId) ? action.ActionId : rule.ActionId;
                var countsTowards = agg.Actions is { Length: > 0 } acts
                    && acts.Contains(action.ActionId, StringComparer.OrdinalIgnoreCase);
                if (!checkedAgainst && !countsTowards) continue;

                var group = new StringBuilder(agg.Key(c.Field, ruleActionId));
                var shared = true;
                foreach (var g in agg.GroupBy ?? Array.Empty<string>())
                {
                    var value = g switch
                    {
                        "agentId" => agentId,
                        "rootAgentId" => rootAgentId,
                        "initiatingUserId" => initiatingUserId,
                        _ => ParamAsString(parameters, g)
                    };
                    if (value is null) { shared = false; break; }
                    // Totals match group values case-insensitively, so the lock must too.
                    group.Append('|').Append(g).Append('=').Append(value.ToLowerInvariant());
                }
                if (shared) keys.Add(group.ToString());
            }
        }
        return keys.ToList();
    }

    /// <summary>ISO-8601 duration (PT1H, PT24H, P7D). Throws on nonsense so a bad rule fails at publish/evaluate, not silently.</summary>
    public static TimeSpan ParseWindow(string window)
    {
        if (string.IsNullOrWhiteSpace(window)) throw new InvalidOperationException("Aggregate window is required (ISO-8601 duration, e.g. PT24H).");
        TimeSpan ts;
        try { ts = XmlConvert.ToTimeSpan(window.Trim()); }
        catch (FormatException ex) { throw new InvalidOperationException($"Aggregate window '{window}' is not an ISO-8601 duration (e.g. PT1H, PT24H, P7D).", ex); }
        if (ts <= TimeSpan.Zero || ts > TimeSpan.FromDays(366))
            throw new InvalidOperationException($"Aggregate window '{window}' must be between 1 second and 366 days.");
        return ts;
    }

    private static string? ParamAsString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText()
        };
    }

    private static bool TryDecimal(string? s, out decimal d)
    {
        d = 0m;
        return s is not null && decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out d);
    }
}
