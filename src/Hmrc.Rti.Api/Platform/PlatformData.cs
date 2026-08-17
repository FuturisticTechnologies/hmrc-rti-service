// ---------------------------------------------------------------------------
// Data-layer platform concerns shared by the .NET services:
//
//   SqlMigrator      - applies ordered .sql files, the same way the Python
//                      service runs `alembic upgrade head` on container start.
//                      SQL-first because row-level security policies belong in
//                      the migration, not in a runbook step (see FPP M2-10).
//   TenantSession    - sets app.tenant_id / app.is_superuser so the RLS
//                      policies written by those migrations actually engage.
//   IdempotencyStore - Idempotency-Key replay with the same semantics as
//                      app/core/idempotency.py: same key + same body replays the
//                      original response, same key + different body is a 409.
//
// Deliberately uses System.Data.Common rather than Npgsql types directly, so
// there is no second provider package to keep version-aligned with EF Core.
// ---------------------------------------------------------------------------
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Payroll.Platform;

public static class SqlMigrator
{
    /// <summary>
    /// Applies every *.sql file in <paramref name="directory"/> in filename order,
    /// recording each in {schema}.schema_migrations. Applying twice is a no-op.
    /// </summary>
    public static async Task ApplyAsync(
        DbContext db,
        string directory,
        string schema,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogWarning("migrations.directory_missing {Directory}", directory);
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {schema}", ct);
        await ExecuteAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS {schema}.schema_migrations (
                version     text PRIMARY KEY,
                applied_at  timestamptz NOT NULL DEFAULT now()
            )
            """, ct);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = $"SELECT version FROM {schema}.schema_migrations";
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                applied.Add(reader.GetString(0));
            }
        }

        var files = Directory.GetFiles(directory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            var version = Path.GetFileNameWithoutExtension(file);
            if (applied.Contains(version))
            {
                continue;
            }

            var sql = await File.ReadAllTextAsync(file, ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            try
            {
                await ExecuteAsync(connection, sql, ct, tx);

                await using var record = connection.CreateCommand();
                record.Transaction = tx;
                record.CommandText =
                    $"INSERT INTO {schema}.schema_migrations (version) VALUES (@v)";
                record.Parameters.Add(Param(record, "@v", version));
                await record.ExecuteNonQueryAsync(ct);

                await tx.CommitAsync(ct);
                logger.LogInformation("migrations.applied {Version}", version);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                logger.LogError("migrations.failed {Version}", version);
                throw;
            }
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection, string sql, CancellationToken ct, DbTransaction? tx = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static DbParameter Param(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        return p;
    }
}

public static class TenantSession
{
    /// <summary>
    /// Sets the Postgres session variables the RLS policies read. Session-scoped
    /// (not transaction-scoped) so it survives across the EF queries in a request;
    /// Npgsql issues DISCARD ALL when the connection returns to the pool, and
    /// <see cref="ClearAsync"/> clears it explicitly as well.
    /// </summary>
    public static async Task ApplyAsync(
        DbContext db, Guid tenantId, bool isSuperuser = false, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT set_config('app.tenant_id', @tenant, false), " +
            "       set_config('app.is_superuser', @superuser, false)";
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@tenant", tenantId.ToString()));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@superuser", isSuperuser ? "true" : "false"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task ClearAsync(DbContext db, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT set_config('app.tenant_id', '', false), " +
            "       set_config('app.is_superuser', 'false', false)";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record IdempotentReplay(int StatusCode, string Body);

public sealed class IdempotencyStore(string schema)
{
    private const int MaxKeyLength = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns a previously stored response when this key has already been used
    /// with an identical request body. Returns null when the caller should go on
    /// and do the work. Throws 409 when the key is reused with a different body.
    /// </summary>
    public async Task<IdempotentReplay?> TryReplayAsync(
        DbContext db, HttpRequest request, Guid tenantId, object payload, CancellationToken ct)
    {
        var key = Normalise(request);
        if (key is null)
        {
            return null;
        }

        var hash = RequestHash(request, payload);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT request_hash, status_code, response_body::text
            FROM {schema}.idempotency_keys
            WHERE key = @key AND method = @method AND path = @path
            """;
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@key", key));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@method", request.Method));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@path", request.Path.Value ?? string.Empty));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var storedHash = reader.GetString(0);
        var status = reader.GetInt32(1);
        var body = reader.IsDBNull(2) ? "{}" : reader.GetString(2);

        if (!string.Equals(storedHash, hash, StringComparison.Ordinal))
        {
            throw ApiException.Conflict(
                "Idempotency-Key has already been used with a different request body");
        }

        if (status == 0)
        {
            throw ApiException.Conflict(
                "Idempotent request is still being finalised; retry shortly");
        }

        return new IdempotentReplay(status, body);
    }

    /// <summary>Records the response so a retry of the same key replays it.</summary>
    public async Task RecordAsync(
        DbContext db, HttpRequest request, Guid tenantId, object payload,
        int statusCode, object responseBody, CancellationToken ct)
    {
        var key = Normalise(request);
        if (key is null)
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {schema}.idempotency_keys
                (key, method, path, request_hash, status_code, response_body, tenant_id)
            VALUES (@key, @method, @path, @hash, @status, @body::jsonb, @tenant)
            ON CONFLICT (key, method, path) DO UPDATE
                SET status_code = EXCLUDED.status_code,
                    response_body = EXCLUDED.response_body
            """;
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@key", key));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@method", request.Method));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@path", request.Path.Value ?? string.Empty));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@hash", RequestHash(request, payload)));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@status", statusCode));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@body", JsonSerializer.Serialize(responseBody, Json)));
        cmd.Parameters.Add(SqlMigrator.Param(cmd, "@tenant", tenantId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Normalise(HttpRequest request)
    {
        var raw = request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var key = raw.Trim();
        if (key.Length > MaxKeyLength)
        {
            throw ApiException.BadRequest(
                $"Idempotency-Key must be {MaxKeyLength} characters or fewer");
        }

        return key;
    }

    private static string RequestHash(HttpRequest request, object payload)
    {
        var canonical =
            $"{request.Method}:{request.Path.Value}:{JsonSerializer.Serialize(payload, Json)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
