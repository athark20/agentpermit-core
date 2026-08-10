using AgentPermit.Gateway.ControlPlane;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Import-time target risk classification and the severity combiner. These prove "danger is known up
/// front" — a money/PII target is HIGH the moment it is registered, an unrecognized target fails closed to
/// HIGH, and an action can never under-declare its way below the target it hits.
/// </summary>
public sealed class RiskClassifierTests
{
    private readonly RiskClassifier _classifier = new();

    private static TargetSystemRecord Target(string code, string adapter = "REST", string? name = null)
        => new() { TargetSystemCode = code, AdapterType = adapter, DisplayName = name ?? code };

    [Theory]
    [InlineData("REFUND_SIMULATOR")]
    [InlineData("STRIPE_PAYMENTS")]
    [InlineData("bank-wire-gateway")]
    [InlineData("payroll-system")]
    public void MoneyOrSensitiveTargets_ClassifyHigh(string code)
    {
        var a = _classifier.ClassifyTarget(Target(code));
        Assert.Equal(ConsequenceTier.High, a.Tier);
        Assert.NotEmpty(a.Reasons);
    }

    [Fact]
    public void CrmWriteTarget_ClassifiesMedium()
        => Assert.Equal(ConsequenceTier.Medium, _classifier.ClassifyTarget(Target("crm-case-updater")).Tier);

    [Fact]
    public void ReadOnlyLookupTarget_ClassifiesLow()
        => Assert.Equal(ConsequenceTier.Low, _classifier.ClassifyTarget(Target("catalog-lookup", name: "Product Catalog Search")).Tier);

    [Fact]
    public void UnrecognizedTarget_FailsClosed_ToHigh()
    {
        var a = _classifier.ClassifyTarget(Target("xyzzy", adapter: "REST", name: "Zork"));
        Assert.Equal(ConsequenceTier.High, a.Tier);
        Assert.Contains("secure by default", string.Join(" ", a.Reasons));
    }

    // ---- Severity combiner: an action's effective consequence is the severest of action + target ----

    [Theory]
    [InlineData(ConsequenceTier.Low, ConsequenceTier.High, ConsequenceTier.High)]   // target pulls it up
    [InlineData(ConsequenceTier.Medium, ConsequenceTier.Low, ConsequenceTier.Medium)]
    [InlineData(ConsequenceTier.High, ConsequenceTier.Critical, ConsequenceTier.Critical)]
    public void Severest_TakesTheHigherTier(string a, string b, string expected)
        => Assert.Equal(expected, ConsequenceTier.Severest(a, b));

    [Fact]
    public void Severest_IgnoresBlanks_ButFailsClosedWhenNothingKnown()
    {
        Assert.Equal(ConsequenceTier.Low, ConsequenceTier.Severest(ConsequenceTier.Low, null));   // null target ignored
        Assert.Equal(ConsequenceTier.High, ConsequenceTier.Severest(null, "  "));                 // nothing known => HIGH
    }
}
