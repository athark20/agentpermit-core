using System.Globalization;
using System.Text.Json;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Tests the data-driven policy engine against versioned snapshots — including proving that
/// changing a threshold is a snapshot change, not a code change (Week 5/6 exit criterion).
/// </summary>
public sealed class PolicyEngineTests
{
    private const string BaselineSnapshot = """
    {
      "policyCode": "Refund.Issue.AmountThreshold",
      "version": "2.0.0",
      "defaultDecision": "DENY",
      "rules": [
        { "id": "allow", "name": "allow", "priority": 10, "actionId": "Refund.Issue",
          "conditions": [ { "field": "amount", "operator": "lessThan", "value": 250 } ], "decision": "ALLOW" },
        { "id": "approve", "name": "approve", "priority": 20, "actionId": "Refund.Issue",
          "conditions": [ { "field": "amount", "operator": "greaterThanOrEqual", "value": 250 },
                          { "field": "amount", "operator": "lessThanOrEqual", "value": 2000 } ],
          "decision": "REQUIRE_APPROVAL", "approverRoles": [ "RefundApprover" ] },
        { "id": "deny", "name": "deny", "priority": 30, "actionId": "Refund.Issue",
          "conditions": [ { "field": "amount", "operator": "greaterThan", "value": 2000 } ], "decision": "DENY" }
      ]
    }
    """;

    private sealed class StubProvider(PolicySnapshot snapshot) : IPolicySnapshotProvider
    {
        public PolicySnapshot Current { get; } = snapshot;
        public string ActiveHash => "test";
    }

    private static IPolicyEngine Engine(string snapshotJson) =>
        new ConfigurablePolicyEngine(new StubProvider(JsonSerializer.Deserialize<PolicySnapshot>(snapshotJson)!));

    private static PolicyEvaluationContext Ctx(decimal amount, string actionId = "Refund.Issue", string currency = "AED")
    {
        var json = $"{{\"amount\":{amount.ToString(CultureInfo.InvariantCulture)},\"currency\":\"{currency}\"}}";
        using var doc = JsonDocument.Parse(json);
        return new PolicyEvaluationContext
        {
            Action = new ActionDefinition { ActionId = actionId },
            Parameters = doc.RootElement.Clone(),
            AgentId = "copilot-refund-demo"
        };
    }

    [Theory]
    [InlineData(0.01, "ALLOW")]
    [InlineData(100, "ALLOW")]
    [InlineData(249.99, "ALLOW")]
    [InlineData(250, "REQUIRE_APPROVAL")]
    [InlineData(750, "REQUIRE_APPROVAL")]
    [InlineData(2000, "REQUIRE_APPROVAL")]
    [InlineData(2000.01, "DENY")]
    [InlineData(3000, "DENY")]
    public void Baseline_Bands_FromSnapshot(decimal amount, string expected)
    {
        var result = Engine(BaselineSnapshot).Evaluate(Ctx(amount));
        Assert.Equal(expected, result.Decision);
        Assert.Equal("2.0.0", result.PolicyVersion);
    }

    // The whole point: change the threshold in data -> different decision, zero code change.
    [Fact]
    public void ChangingThreshold_IsData_NotCode()
    {
        const string raised = """
        {
          "policyCode": "Refund.Issue.AmountThreshold", "version": "3.0.0", "defaultDecision": "DENY",
          "rules": [
            { "id": "allow", "name": "allow", "priority": 10, "actionId": "Refund.Issue",
              "conditions": [ { "field": "amount", "operator": "lessThan", "value": 500 } ], "decision": "ALLOW" }
          ]
        }
        """;
        // 300 is REQUIRE_APPROVAL under the baseline, but ALLOW once the auto-approve limit is raised to 500.
        Assert.Equal("REQUIRE_APPROVAL", Engine(BaselineSnapshot).Evaluate(Ctx(300)).Decision);
        var r = Engine(raised).Evaluate(Ctx(300));
        Assert.Equal("ALLOW", r.Decision);
        Assert.Equal("3.0.0", r.PolicyVersion);
    }

    [Fact]
    public void NoMatchingRule_DefaultsToDeny()
    {
        Assert.Equal("DENY", Engine(BaselineSnapshot).Evaluate(Ctx(100, actionId: "Other.Action")).Decision);
    }

    [Fact]
    public void MatchedRuleId_IsReported()
    {
        Assert.Equal("allow", Engine(BaselineSnapshot).Evaluate(Ctx(100)).MatchedRuleId);
    }

    [Fact]
    public void Evaluation_IsDeterministic()
    {
        var a = Engine(BaselineSnapshot).Evaluate(Ctx(750));
        var b = Engine(BaselineSnapshot).Evaluate(Ctx(750));
        Assert.Equal(a.Decision, b.Decision);
        Assert.Equal(a.MatchedRuleId, b.MatchedRuleId);
    }

    // String + enumeration operators (e.g. currency allow-list).
    [Fact]
    public void InOperator_MatchesEnumeration()
    {
        const string snap = """
        {
          "policyCode": "x", "version": "1", "defaultDecision": "DENY",
          "rules": [ { "id": "c", "name": "c", "priority": 10, "actionId": "Refund.Issue",
            "conditions": [ { "field": "currency", "operator": "in", "value": [ "AED", "USD" ] } ], "decision": "ALLOW" } ]
        }
        """;
        Assert.Equal("ALLOW", Engine(snap).Evaluate(Ctx(100, currency: "AED")).Decision);
        Assert.Equal("DENY", Engine(snap).Evaluate(Ctx(100, currency: "EUR")).Decision);
    }
}
