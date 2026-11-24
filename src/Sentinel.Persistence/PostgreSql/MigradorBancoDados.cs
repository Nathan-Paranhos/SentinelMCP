using Npgsql;

namespace Sentinel.Persistence.PostgreSql;

public sealed class MigradorBancoDados
{
    private readonly string _stringConexao;

    public MigradorBancoDados(string stringConexao)
    {
        _stringConexao = stringConexao;
    }

    public async Task MigrarAsync(string diretorioMigracoes, CancellationToken ct = default)
    {
        await using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);

        await using (var cmdInicial = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS _schema_migrations (
                version TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );", conexao))
        {
            await cmdInicial.ExecuteNonQueryAsync(ct);
        }

        if (!Directory.Exists(diretorioMigracoes))
            return;

        var arquivosMigracao = Directory.GetFiles(diretorioMigracoes, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        foreach (var arquivo in arquivosMigracao)
        {
            var versao = Path.GetFileName(arquivo);
            bool jaAplicado;

            await using (var cmdVerificacao = new NpgsqlCommand("SELECT 1 FROM _schema_migrations WHERE version = @ver", conexao))
            {
                cmdVerificacao.Parameters.AddWithValue("ver", versao);
                var resultado = await cmdVerificacao.ExecuteScalarAsync(ct);
                jaAplicado = resultado != null;
            }

            if (!jaAplicado)
            {
                var sql = await File.ReadAllTextAsync(arquivo, ct);
                await using var transacao = await conexao.BeginTransactionAsync(ct);
                try
                {
                    await using (var cmdExec = new NpgsqlCommand(sql, conexao, transacao))
                    {
                        await cmdExec.ExecuteNonQueryAsync(ct);
                    }

                    await using (var cmdRegistro = new NpgsqlCommand("INSERT INTO _schema_migrations (version) VALUES (@ver)", conexao, transacao))
                    {
                        cmdRegistro.Parameters.AddWithValue("ver", versao);
                        await cmdRegistro.ExecuteNonQueryAsync(ct);
                    }

                    await transacao.CommitAsync(ct);
                }
                catch
                {
                    await transacao.RollbackAsync(ct);
                    throw;
                }
            }
        }
    }
}
