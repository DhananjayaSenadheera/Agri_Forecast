using System.Data;
using AgriForecast.Application.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.IngestionControl;

// Cross-process single-flight for an ingestion pass, arbitrated by SQL Server's sp_getapplock.
//
// The database is the only thing every ingestion host shares (API pod, Docker/CronJob worker, a dev
// machine), so it is the only place a lock can actually be mutual. Design points, each deliberate:
//
//  * @LockOwner = 'Session'. A pass runs for minutes; a Transaction-owned lock would force an open
//    transaction for that whole time, holding row locks and a version-store tail hostage to an HTTP
//    call. Session ownership needs only its own dedicated, open SqlConnection, which the lease holds.
//  * @LockTimeout = 0. Never queue: a held lock means "a pass is already running", which the caller
//    reports as 409 immediately. Waiting would silently stack a second pass behind the first.
//  * Dedicated connection, NOT the EF DbContext's. The lock must outlive any DbContext scope and must
//    not be affected by EF opening/closing its connection between saves.
//  * The lock dies with the session. If the API pod is killed mid-pass, SQL Server drops the connection
//    and releases the lock automatically — there is no stuck-lock recovery chore.
public class SqlIngestionPassLock : IIngestionPassLock
{
    // Application-lock resource name. Shared verbatim by every host that may run a pass; changing it
    // silently disables mutual exclusion between old and new deployments, so treat it as a contract.
    public const string ResourceName = "AgriForecast:IngestionPass";

    private readonly string _connectionString;
    private readonly ILogger<SqlIngestionPassLock> _logger;

    public SqlIngestionPassLock(IConfiguration configuration, ILogger<SqlIngestionPassLock> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Missing ConnectionStrings:DefaultConnection for the ingestion pass lock.");
        _logger = logger;
    }

    public async Task<IIngestionPassLease?> TryAcquireAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);

            await using var cmd = connection.CreateCommand();
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandText = "sp_getapplock";
            cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ResourceName });
            cmd.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.NVarChar, 32) { Value = "Exclusive" });
            cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
            cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = 0 });

            var returnValue = new SqlParameter
            {
                ParameterName = "@Result",
                SqlDbType = SqlDbType.Int,
                Direction = ParameterDirection.ReturnValue
            };
            cmd.Parameters.Add(returnValue);

            await cmd.ExecuteNonQueryAsync(ct);

            // >= 0 granted (0 immediately, 1 after waiting); < 0 not granted (-1 timeout with @LockTimeout
            // 0 = already held elsewhere, -3 deadlock victim, -999 parameter error).
            var result = returnValue.Value is int code ? code : -1;
            if (result >= 0)
                return new SqlIngestionPassLease(connection, _logger);

            _logger.LogInformation(
                "Ingestion pass lock is already held (sp_getapplock returned {Result}); not starting a second pass.",
                result);
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            // Never leak the connection when acquisition throws. A genuine DB fault propagates: the caller
            // must not be told "not running" because the lock could not be consulted.
            await connection.DisposeAsync();
            throw;
        }
    }

    // Holds the SQL session that owns the lock. Releasing explicitly (rather than relying on the
    // connection close) keeps the release deterministic even when the connection is pooled and reused.
    private sealed class SqlIngestionPassLease : IIngestionPassLease
    {
        private readonly SqlConnection _connection;
        private readonly ILogger _logger;
        private bool _disposed;

        public SqlIngestionPassLease(SqlConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "sp_releaseapplock";
                cmd.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ResourceName });
                cmd.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.NVarChar, 32) { Value = "Session" });
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Closing the connection below drops the session, which releases a session-owned lock
                // anyway, so a failed explicit release is a warning and never a thrown error out of a
                // finally block.
                _logger.LogWarning(ex,
                    "Failed to release the ingestion pass application lock explicitly; closing the session releases it.");
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }
}
