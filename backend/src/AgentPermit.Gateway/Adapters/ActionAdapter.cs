using System.Text.Json;

namespace AgentPermit.Gateway.Adapters;

public enum AdapterExecutionStatus { Succeeded, Failed, Pending }

/// <summary>What the execution service hands an adapter to run against a target system.</summary>
public sealed class AdapterRequest
{
    public required Guid ExecutionId { get; init; }
    public required Guid ActionRequestId { get; init; }
    public required string ActionId { get; init; }
    public required string TargetSystem { get; init; }
    public required JsonElement Parameters { get; init; }
    public Guid CorrelationId { get; init; }
}

/// <summary>The normalized result every adapter returns (no exceptions for expected target failures).</summary>
public sealed class AdapterResult
{
    public required AdapterExecutionStatus Status { get; init; }
    public string? TargetResourceId { get; init; }
    public string? ResultJson { get; init; }
    public string? ProviderReference { get; init; }  // for async status polling
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static AdapterResult Ok(string? targetResourceId, string? resultJson) =>
        new() { Status = AdapterExecutionStatus.Succeeded, TargetResourceId = targetResourceId, ResultJson = resultJson };

    public static AdapterResult Fail(string code, string message) =>
        new() { Status = AdapterExecutionStatus.Failed, ErrorCode = code, ErrorMessage = message };

    public static AdapterResult Async(string providerReference) =>
        new() { Status = AdapterExecutionStatus.Pending, ProviderReference = providerReference };
}

public sealed class AdapterHealth
{
    public required bool Healthy { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The standard contract for executing a business action against a target system. Adapters are
/// the only components that hold downstream credentials; the gateway resolves an adapter by the
/// action's target system and never talks to a target directly.
/// </summary>
public interface IActionAdapter
{
    string AdapterCode { get; }
    Task<AdapterHealth> CheckHealthAsync(CancellationToken ct);
    Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct);
    Task<AdapterResult> GetStatusAsync(string providerReference, CancellationToken ct);
    Task<AdapterResult> CompensateAsync(AdapterRequest request, CancellationToken ct);
}

/// <summary>Base class supplying defaults for the optional operations.</summary>
public abstract class ActionAdapterBase : IActionAdapter
{
    public abstract string AdapterCode { get; }
    public abstract Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct);

    public virtual Task<AdapterHealth> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(new AdapterHealth { Healthy = true, Message = "OK" });

    public virtual Task<AdapterResult> GetStatusAsync(string providerReference, CancellationToken ct)
        => throw new NotSupportedException($"Adapter '{AdapterCode}' does not support status polling.");

    public virtual Task<AdapterResult> CompensateAsync(AdapterRequest request, CancellationToken ct)
        => throw new NotSupportedException($"Adapter '{AdapterCode}' does not support compensation.");
}

/// <summary>Resolves the adapter registered for a target system.</summary>
public interface IActionAdapterResolver
{
    bool TryResolve(string targetSystem, out IActionAdapter adapter);
    IActionAdapter Resolve(string targetSystem);
    IReadOnlyCollection<string> RegisteredTargets { get; }
}

public sealed class ActionAdapterResolver : IActionAdapterResolver
{
    private readonly Dictionary<string, IActionAdapter> _byCode;

    public ActionAdapterResolver(IEnumerable<IActionAdapter> adapters)
        => _byCode = adapters.ToDictionary(a => a.AdapterCode, a => a, StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(string targetSystem, out IActionAdapter adapter)
        => _byCode.TryGetValue(targetSystem, out adapter!);

    public IActionAdapter Resolve(string targetSystem)
        => _byCode.TryGetValue(targetSystem, out var a)
            ? a
            : throw new InvalidOperationException($"No adapter is registered for target system '{targetSystem}'.");

    public IReadOnlyCollection<string> RegisteredTargets => _byCode.Keys;
}
