using Npgsql;

namespace Sentinel.Persistence.PostgreSql;

public sealed class DatabaseMigrator
{
    private readonly string _connectionString;

    public DatabaseMigrator(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task MigrateAsync(string migrationsDirectory, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var initCmd = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS _schema_migrations (
                version TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );", conn))
        {
            await initCmd.ExecuteNonQueryAsync(ct);
        }

        if (!Directory.Exists(migrationsDirectory))
            return;

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        foreach (var file in migrationFiles)
        {
            var version = Path.GetFileName(file);
            bool alreadyApplied;

            await using (var checkCmd = new NpgsqlCommand("SELECT 1 FROM _schema_migrations WHERE version = @ver", conn))
            {
                checkCmd.Parameters.AddWithValue("ver", version);
                var result = await checkCmd.ExecuteScalarAsync(ct);
                alreadyApplied = result != null;
            }

            if (!alreadyApplied)
            {
                var sql = await File.ReadAllTextAsync(file, ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                try
                {
                    await using (var execCmd = new NpgsqlCommand(sql, conn, tx))
                    {
                        await execCmd.ExecuteNonQueryAsync(ct);
                    }

                    await using (var recordCmd = new NpgsqlCommand("INSERT INTO _schema_migrations (version) VALUES (@ver)", conn, tx))
                    {
                        recordCmd.Parameters.AddWithValue("ver", version);
                        await recordCmd.ExecuteNonQueryAsync(ct);
                    }

                    await tx.CommitAsync(ct);
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }
        }
    }
}
