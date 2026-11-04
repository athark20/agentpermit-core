using System.Data;
using System.Security.Cryptography;
using System.Text;
using AgentPermit.Gateway.Exceptions;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// Makes a windowed-limit check and the request it lets through one step. A request holds the locks for every
/// limit it is checked against or would count towards, from before the totals are computed until it has been
/// recorded at a state those totals count (pending approval, about to execute, or denied). A second request in
/// the same group waits, then sees the first one in its total, so two requests arriving at the same moment can't
/// both slip under a cap. The recorded request is the reservation: if it later fails, is rejected or expires, it
/// stops counting, which releases the amount.
/// </summary>
public interface IAggregateLock
{
    /// <summary>
    /// Acquire every key (deduplicated, in a fixed order so two requests can never deadlock) and hold them until
    /// the returned handle is disposed. Disposing twice is safe. No keys = no lock and no store call.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(IReadOnlyCollection<string> keys, CancellationToken ct);
}

/// <summary>A limit's lock could not be obtained in time. Retryable: nothing was recorded for the request.</summary>
public sealed class LimitBusyException(string message) : AgentPermitException(message)
{
    public override string Code => "LIMIT_BUSY";
    public override int StatusCode => StatusCodes.Status503ServiceUnavailable;
}

public sealed class AggregateLockOptions
{
    /// <summary>How long a request waits for another request in the same limit group before giving up (503).</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Single-process locks (in-memory persistence: development and tests).</summary>
public sealed class InMemoryAggregateLock(AggregateLockOptions options) : IAggregateLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>Keys currently held or waited on. Entries are removed when nobody uses them, so this does not grow.</summary>
    public int TrackedKeyCount { get { lock (_sync) return _entries.Count; } }

    public async Task<IAsyncDisposable> AcquireAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var ordered = Order(keys);
        if (ordered.Length == 0) return Released.Instance;

        var held = new List<(string Key, Entry Slot)>(ordered.Length);
        try
        {
            foreach (var key in ordered)
            {
                var entry = Rent(key);
                bool acquired;
                try { acquired = await entry.Gate.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)), ct); }
                catch { Return(key, entry); throw; }
                if (!acquired)
                {
                    Return(key, entry);
                    throw new LimitBusyException("Another request for the same limit is still being decided. Try again in a moment.")
                    { Title = "Busy, try again" };
                }
                held.Add((key, entry));
            }
            return new Handle(this, held);
        }
        catch
        {
            Release(held);
            throw;
        }
    }

    internal static string[] Order(IReadOnlyCollection<string> keys)
        => keys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private Entry Rent(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry();
            entry.Users++;
            return entry;
        }
    }

    private void Return(string key, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.Users == 0) _entries.Remove(key);
        }
    }

    private void Release(List<(string Key, Entry Slot)> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
        {
            held[i].Slot.Gate.Release();
            Return(held[i].Key, held[i].Slot);
        }
        held.Clear();
    }

    private sealed class Handle(InMemoryAggregateLock owner, List<(string Key, Entry Slot)> held) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(held);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Locks shared by every gateway replica: SQL Server application locks (<c>sp_getapplock</c>), owned by a
/// transaction on a dedicated connection. Committing (or the connection dropping, if a replica dies) releases
/// them, so a lock can never outlive the request that took it, even with connection pooling.
/// </summary>
public sealed class SqlAggregateLock(string connectionString, AggregateLockOptions options, ILogger<SqlAggregateLock> logger)
    : IAggregateLock
{
    public async Task<IAsyncDisposable> AcquireAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var ordered = InMemoryAggregateLock.Order(keys);
        if (ordered.Length == 0) return Released.Instance;

        var connection = new SqlConnection(connectionString);
        SqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(ct);
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            foreach (var key in ordered)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "sp_getapplock";
                cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ResourceName(key) });
                cmd.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.NVarChar, 32) { Value = "Exclusive" });
                cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Transaction" });
                cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = Math.Max(1, options.TimeoutSeconds) * 1000 });
                var result = cmd.Parameters.Add(new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
                await cmd.ExecuteNonQueryAsync(ct);

                // 0 = granted, 1 = granted after waiting; negative = timeout (-1), cancelled (-2), deadlock victim (-3), error (-999).
                var code = result.Value is int i ? i : -999;
                if (code < 0)
                {
                    logger.LogWarning("Limit lock not granted (sp_getapplock returned {Code}).", code);
                    throw new LimitBusyException("Another request for the same limit is still being decided. Try again in a moment.")
                    { Title = "Busy, try again" };
                }
            }
            return new Handle(connection, transaction);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>sp_getapplock resource names are at most 255 characters; a hash keeps any key short and opaque.</summary>
    private static string ResourceName(string key)
        => "agentpermit:limit:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed class Handle(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await transaction.CommitAsync(); }   // releases every lock the transaction holds
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}

internal sealed class Released : IAsyncDisposable
{
    public static readonly Released Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
