using System.Security.Claims;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Writes audit events for administrative changes, resolving the actor from the Entra token
/// (falling back to the admin-key path). Best-effort: an audit-store failure never blocks the action.
/// </summary>
public sealed class AuditService(IAuditStore store, ILogger<AuditService> logger)
{
    public async Task WriteAsync(HttpContext ctx, string action, string targetType, string? targetId, string? summary)
    {
        try
        {
            var (actor, actorId) = ResolveActor(ctx);
            await store.AppendAsync(new AuditEventRecord
            {
                AuditEventId = Guid.NewGuid(),
                OccurredUtc = DateTimeOffset.UtcNow,
                Actor = actor,
                ActorId = actorId,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Summary = summary,
            }, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit write failed for {Action} {TargetType}/{TargetId}", action, targetType, targetId);
        }
    }

    /// <summary>Audit an event performed by the platform itself (a background job), not an admin.</summary>
    public async Task WriteSystemAsync(string action, string targetType, string? targetId, string? summary, CancellationToken ct)
    {
        try
        {
            await store.AppendAsync(new AuditEventRecord
            {
                AuditEventId = Guid.NewGuid(),
                OccurredUtc = DateTimeOffset.UtcNow,
                Actor = "system (auto)",
                ActorId = null,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Summary = summary,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System audit write failed for {Action} {TargetType}/{TargetId}", action, targetType, targetId);
        }
    }

    private static (string actor, string? actorId) ResolveActor(HttpContext ctx)
    {
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var name = user.FindFirstValue("name")
                ?? user.FindFirstValue("preferred_username")
                ?? user.FindFirstValue(ClaimTypes.Name)
                ?? "admin";
            var oid = user.FindFirstValue("oid") ?? user.FindFirstValue("sub");
            return (name, oid);
        }
        // Admin-key path (no Entra identity): attribute to the service key.
        return ("admin (api key)", null);
    }
}
