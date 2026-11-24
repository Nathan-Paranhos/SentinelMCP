using Npgsql;

namespace Sentinel.Persistence.PostgreSql;

// Aplicador de migrações e fixtures SQL
public sealed class MigradorBancoDados
{
    // Chave do lock consultivo de migração
    private const long ChaveLockMigracao = 0x53454E54494E454CL;

    private readonly string _stringConexao;

    public MigradorBancoDados(string stringConexao)
    {
        _stringConexao = stringConexao;
    }

    // Aplica as migrações SQL pendentes
    public async Task<IReadOnlyList<string>> MigrarAsync(string diretorioMigracoes, CancellationToken ct = default)
    {
        if (!Directory.Exists(diretorioMigracoes))
            throw new DirectoryNotFoundException($"Diretório de migrações não encontrado: '{diretorioMigracoes}'.");

        await using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);

        await using (var cmdLock = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conexao))
        {
            cmdLock.Parameters.AddWithValue("k", ChaveLockMigracao);
            cmdLock.CommandTimeout = 0;
            await cmdLock.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using (var cmdInicial = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS _schema_migrations (
                    version TEXT PRIMARY KEY,
                    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );", conexao))
            {
                await cmdInicial.ExecuteNonQueryAsync(ct);
            }

            var aplicadas = new List<string>();
            foreach (var arquivo in Directory.GetFiles(diretorioMigracoes, "*.sql").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var versao = Path.GetFileName(arquivo);

                await using (var cmdVerificacao = new NpgsqlCommand("SELECT 1 FROM _schema_migrations WHERE version = @ver", conexao))
                {
                    cmdVerificacao.Parameters.AddWithValue("ver", versao);
                    if (await cmdVerificacao.ExecuteScalarAsync(ct) != null)
                        continue;
                }

                var sql = await File.ReadAllTextAsync(arquivo, ct);
                await using var transacao = await conexao.BeginTransactionAsync(ct);
                await using (var cmdExec = new NpgsqlCommand(sql, conexao, transacao))
                {
                    cmdExec.CommandTimeout = 0;
                    await cmdExec.ExecuteNonQueryAsync(ct);
                }
                await using (var cmdRegistro = new NpgsqlCommand("INSERT INTO _schema_migrations (version) VALUES (@ver)", conexao, transacao))
                {
                    cmdRegistro.Parameters.AddWithValue("ver", versao);
                    await cmdRegistro.ExecuteNonQueryAsync(ct);
                }
                await transacao.CommitAsync(ct);
                aplicadas.Add(versao);
            }

            return aplicadas;
        }
        finally
        {
            await using var cmdUnlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conexao);
            cmdUnlock.Parameters.AddWithValue("k", ChaveLockMigracao);
            await cmdUnlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    // Aplica fixtures SQL de demonstração
    public async Task AplicarFixturesAsync(string diretorioFixtures, CancellationToken ct = default)
    {
        if (!Directory.Exists(diretorioFixtures))
            throw new DirectoryNotFoundException($"Diretório de fixtures não encontrado: '{diretorioFixtures}'.");

        await using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);
        foreach (var arquivo in Directory.GetFiles(diretorioFixtures, "*.sql").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            await using var transacao = await conexao.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(arquivo, ct), conexao, transacao))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await transacao.CommitAsync(ct);
        }
    }
}
