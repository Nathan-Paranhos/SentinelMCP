using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Sentinel.Core.Comandos;
using Sentinel.Core.Comum;
using Sentinel.Core.Consultas;
using Sentinel.Core.Contratos;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Core.Validacao;
using Sentinel.Persistence.Comum;

namespace Sentinel.Persistence.PostgreSql;

// Repositório PostgreSQL com isolamento RLS e redutores
public sealed class RepositorioSentinelPostgreSql :
    IRepositorioEspacoTrabalho,
    IRepositorioDecisao,
    IRepositorioIncidente,
    IRepositorioContexto,
    IRepositorioHistorico,
    IReconstrutorProjecoes,
    IManutencaoRetencao,
    IVerificadorProntidao,
    IRepositorioUsuariosLocais,
    IRepositorioChavesApi
{
    private const string ColunasEvento =
        "event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id, ref_event_id, reason";

    private const string ColunasCarga =
        "payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, created_at, occurred_at";

    private const string ColunasDecisao =
        "topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version, stream_version, status, pending_proposal_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, updated_at";

    private const string ColunasProposta =
        "topic_id, workspace_id, project_id, topic_key, stream_version, proposal_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, author_subject, author_issuer, client_id, proposed_at";

    private const string ColunasIncidente =
        "incident_id, workspace_id, project_id, record_version, stream_version, status, title, summary, occurred_at, technical_keys, evidence, created_at";

    private const string ColunasTopico =
        "topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at";

    private readonly NpgsqlDataSource _fonte;

    public RepositorioSentinelPostgreSql(NpgsqlDataSource fonte)
    {
        _fonte = fonte;
    }

    private async Task<T> EmTransacaoAsync<T>(
        Guid? espacoTrabalhoId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> acao,
        CancellationToken ct,
        bool somenteLeitura = false,
        (string Emissor, string Sujeito)? identidade = null,
        string? hashChave = null)
    {
        await using var conexao = await _fonte.OpenConnectionAsync(ct);
        await using var transacao = await conexao.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (somenteLeitura)
            {
                await using var cmdLeitura = new NpgsqlCommand("SET TRANSACTION READ ONLY", conexao, transacao);
                await cmdLeitura.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdContexto = new NpgsqlCommand(
                "SELECT set_config('app.current_workspace_id', @ws, true), set_config('app.current_issuer', @iss, true), set_config('app.current_subject', @sub, true), set_config('app.current_key_hash', @kh, true)",
                conexao, transacao))
            {
                cmdContexto.Parameters.AddWithValue("ws", espacoTrabalhoId?.ToString() ?? string.Empty);
                cmdContexto.Parameters.AddWithValue("iss", identidade?.Emissor ?? string.Empty);
                cmdContexto.Parameters.AddWithValue("sub", identidade?.Sujeito ?? string.Empty);
                cmdContexto.Parameters.AddWithValue("kh", hashChave ?? string.Empty);
                await cmdContexto.ExecuteNonQueryAsync(ct);
            }

            var resultado = await acao(conexao, transacao);
            await transacao.CommitAsync(ct);
            return resultado;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Trata violação de chave única como conflito de versão
            throw new ExcecaoDeConflitoDeVersao(null, null, null,
                "Escrita concorrente detectada no mesmo registro. Releia a versão atual e tente novamente.");
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable)
        {
            throw new ExcecaoDeTemporariamenteIndisponivel();
        }
    }

    private Task<List<T>> ListarAsync<T>(Guid? espacoTrabalhoId, string sql, Action<NpgsqlCommand> parametros, Func<NpgsqlDataReader, T> mapear, CancellationToken ct, (string, string)? identidade = null, string? hashChave = null) =>
        EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            parametros(cmd);
            return await LerListaAsync(cmd, mapear, ct);
        }, ct, somenteLeitura: true, identidade: identidade, hashChave: hashChave);

    private async Task<T?> PrimeiroAsync<T>(Guid espacoTrabalhoId, string sql, Action<NpgsqlCommand> parametros, Func<NpgsqlDataReader, T> mapear, CancellationToken ct) where T : class
    {
        var lista = await ListarAsync(espacoTrabalhoId, sql, parametros, mapear, ct);
        return lista.FirstOrDefault();
    }

    private static async Task<List<T>> LerListaAsync<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> mapear, CancellationToken ct)
    {
        var lista = new List<T>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
            lista.Add(mapear(leitor));
        return lista;
    }

    private static async Task<T?> LerPrimeiroAsync<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> mapear, CancellationToken ct) where T : class
    {
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
            return mapear(leitor);
        return null;
    }

    private static async Task ExecutarAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, Action<NpgsqlCommand> parametros, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        parametros(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Json(NpgsqlCommand cmd, string nome, string json) =>
        cmd.Parameters.Add(new NpgsqlParameter(nome, NpgsqlDbType.Jsonb) { Value = json });

    private static void Nulavel(NpgsqlCommand cmd, string nome, object? valor) =>
        cmd.Parameters.AddWithValue(nome, valor ?? DBNull.Value);

    // Serialização de colunas jsonb

    private sealed record EvidenciaArmazenada(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("url")] string Url);

    private static string EvidenciasParaJson(IEnumerable<Evidencia>? evidencias) =>
        JsonSerializer.Serialize((evidencias ?? []).Select(e => new EvidenciaArmazenada(e.Tipo, e.Rotulo, e.Url)));

    private static List<Evidencia> EvidenciasDeJson(string json) =>
        (JsonSerializer.Deserialize<List<EvidenciaArmazenada>>(json) ?? [])
        .Select(e => new Evidencia(e.Type ?? string.Empty, e.Label ?? string.Empty, e.Url ?? string.Empty))
        .ToList();

    private static string ListaParaJson<T>(IEnumerable<T>? itens) => JsonSerializer.Serialize(itens ?? []);
    private static List<string> TextosDeJson(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];
    private static List<Guid> GuidsDeJson(string json) => JsonSerializer.Deserialize<List<Guid>>(json) ?? [];

    private static string? TextoOuNulo(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? GuidOuNulo(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset Instante(NpgsqlDataReader r, int i) => r.GetFieldValue<DateTimeOffset>(i);

    private static EventoMemoria MapearEvento(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6),
        Instante(r, 7), r.GetString(8), r.GetString(9), TextoOuNulo(r, 10), GuidOuNulo(r, 11), GuidOuNulo(r, 12))
    {
        Motivo = TextoOuNulo(r, 13)
    };

    private static CargaMemoria MapearCarga(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), TextoOuNulo(r, 5),
        TextosDeJson(r.GetString(6)), GuidsDeJson(r.GetString(7)), EvidenciasDeJson(r.GetString(8)), Instante(r, 9))
    {
        OcorridoEm = r.IsDBNull(10) ? null : Instante(r, 10)
    };

    private static DecisaoAtual MapearDecisao(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), GuidOuNulo(r, 4), r.IsDBNull(5) ? null : r.GetInt32(5),
        r.GetInt32(6), ConversorDeEnumeradores.ParaStatusDecisao(r.GetString(7)), GuidOuNulo(r, 8),
        r.GetString(9), r.GetString(10), TextoOuNulo(r, 11),
        TextosDeJson(r.GetString(12)), GuidsDeJson(r.GetString(13)), EvidenciasDeJson(r.GetString(14)), Instante(r, 15));

    private static PropostaPendente MapearProposta(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetGuid(5),
        r.GetString(6), r.GetString(7), TextoOuNulo(r, 8),
        TextosDeJson(r.GetString(9)), GuidsDeJson(r.GetString(10)), EvidenciasDeJson(r.GetString(11)),
        r.GetString(12), r.GetString(13), TextoOuNulo(r, 14), Instante(r, 15));

    private static IncidenteAtual MapearIncidente(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetInt32(4),
        ConversorDeEnumeradores.ParaStatusIncidente(r.GetString(5)), r.GetString(6), r.GetString(7), Instante(r, 8),
        TextosDeJson(r.GetString(9)), EvidenciasDeJson(r.GetString(10)), Instante(r, 11));

    private static TopicoDecisao MapearTopico(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), Instante(r, 5), Instante(r, 6));

    public async Task VerificarAsync(CancellationToken ct = default)
    {
        await using var cmd = _fonte.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
    }

    // Verifica se usuário ignora RLS
    public async Task<(string Papel, bool IgnoraRls)> VerificarPrivilegiosAsync(CancellationToken ct = default)
    {
        await using var cmd = _fonte.CreateCommand("SELECT current_user, rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user");
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        await leitor.ReadAsync(ct);
        return (leitor.GetString(0), leitor.GetBoolean(1));
    }

    public async Task<EspacoTrabalho?> ObterEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        var lista = await ListarAsync(espacoTrabalhoId,
            "SELECT workspace_id, name, slug, created_at FROM workspaces WHERE workspace_id = @id",
            cmd => cmd.Parameters.AddWithValue("id", espacoTrabalhoId),
            r => new EspacoTrabalho(r.GetGuid(0), r.GetString(1), r.GetString(2), Instante(r, 3)), ct);
        return lista.FirstOrDefault();
    }

    // Consulta vínculos da identidade
    public Task<List<EspacoTrabalho>> ObterEspacosDaIdentidadeAsync(string emissor, string sujeito, CancellationToken ct = default) =>
        ListarAsync(null,
            @"SELECT w.workspace_id, w.name, w.slug, w.created_at
              FROM workspaces w
              JOIN memberships m ON m.workspace_id = w.workspace_id
              WHERE m.issuer = @iss AND m.subject = @sub AND m.is_active
              ORDER BY w.created_at, w.workspace_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("iss", emissor);
                cmd.Parameters.AddWithValue("sub", sujeito);
            },
            r => new EspacoTrabalho(r.GetGuid(0), r.GetString(1), r.GetString(2), Instante(r, 3)), ct,
            identidade: (emissor, sujeito));

    public Task<VinculoMembro?> ObterVinculoMembroAsync(Guid espacoTrabalhoId, string emissor, string sujeito, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            @"SELECT membership_id, workspace_id, issuer, subject, role, is_active, created_at
              FROM memberships WHERE workspace_id = @ws AND issuer = @iss AND subject = @sub",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("iss", emissor);
                cmd.Parameters.AddWithValue("sub", sujeito);
            },
            r => new VinculoMembro(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3),
                ConversorDeEnumeradores.ParaPapel(r.GetString(4)), r.GetBoolean(5), Instante(r, 6)), ct);

    public Task<Projeto?> ObterProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            "SELECT project_id, workspace_id, name, slug, created_at FROM projects WHERE workspace_id = @ws AND project_id = @proj",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
            },
            r => new Projeto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), Instante(r, 4)), ct);

    public Task<List<Projeto>> ObterProjetosPorEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            "SELECT project_id, workspace_id, name, slug, created_at FROM projects WHERE workspace_id = @ws ORDER BY name, project_id",
            cmd => cmd.Parameters.AddWithValue("ws", espacoTrabalhoId),
            r => new Projeto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), Instante(r, 4)), ct);

    public async Task<bool> PossuiAlgumEspacoAsync(CancellationToken ct = default)
    {
        await using var cmd = _fonte.CreateCommand("SELECT EXISTS (SELECT 1 FROM workspaces)");
        var res = await cmd.ExecuteScalarAsync(ct);
        return res is true;
    }

    public async Task<EspacoTrabalho> CriarEspacoTrabalhoAsync(string nome, string slug, Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        var id = espacoTrabalhoId ?? Guid.NewGuid();
        try
        {
            await using var cmd = _fonte.CreateCommand(
                @"INSERT INTO workspaces (workspace_id, name, slug, created_at)
                  VALUES (@id, @name, @slug, now())
                  RETURNING workspace_id, name, slug, created_at");
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("name", nome);
            cmd.Parameters.AddWithValue("slug", slug);
            var ws = await LerPrimeiroAsync(cmd, r => new EspacoTrabalho(r.GetGuid(0), r.GetString(1), r.GetString(2), Instante(r, 3)), ct);
            return ws!;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new ExcecaoDeConflito($"Workspace com slug '{slug}' já existe.");
        }
    }

    public async Task<List<EspacoTrabalho>> ListarTodosEspacosAsync(CancellationToken ct = default)
    {
        await using var cmd = _fonte.CreateCommand("SELECT workspace_id, name, slug, created_at FROM workspaces ORDER BY created_at, workspace_id");
        return await LerListaAsync(cmd, r => new EspacoTrabalho(r.GetGuid(0), r.GetString(1), r.GetString(2), Instante(r, 3)), ct);
    }

    public async Task<Projeto> CriarProjetoAsync(Guid espacoTrabalhoId, string nome, string slug, Guid? projetoId = null, CancellationToken ct = default)
    {
        var id = projetoId ?? Guid.NewGuid();
        return await EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            try
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO projects (project_id, workspace_id, name, slug, created_at)
                                    VALUES (@id, @ws, @name, @slug, now())
                                    RETURNING project_id, workspace_id, name, slug, created_at";
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("name", nome);
                cmd.Parameters.AddWithValue("slug", slug);
                var proj = await LerPrimeiroAsync(cmd, r => new Projeto(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), Instante(r, 4)), ct);
                return proj!;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                throw new ExcecaoDeConflito($"Projeto com slug '{slug}' já existe no workspace.");
            }
        }, ct);
    }

    public Task<bool> ProjetoPossuiHistoricoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default) =>
        EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                @"SELECT
                    EXISTS (SELECT 1 FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj) OR
                    EXISTS (SELECT 1 FROM incident_streams WHERE workspace_id = @ws AND project_id = @proj) OR
                    EXISTS (SELECT 1 FROM memory_events WHERE workspace_id = @ws AND project_id = @proj)", c, tx);
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("proj", projetoId);
            return (await cmd.ExecuteScalarAsync(ct)) is true;
        }, ct, somenteLeitura: true);

    public Task<bool> RemoverProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default) =>
        EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = new NpgsqlCommand("DELETE FROM projects WHERE workspace_id = @ws AND project_id = @proj", c, tx);
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("proj", projetoId);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }, ct);

    public async Task<VinculoMembro> CriarOuAtualizarVinculoAsync(Guid espacoTrabalhoId, string emissor, string sujeito, PapelUsuario papel, bool ativo = true, CancellationToken ct = default)
    {
        return await EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO memberships (membership_id, workspace_id, issuer, subject, role, is_active, created_at)
                                VALUES (@id, @ws, @iss, @sub, @role, @active, now())
                                ON CONFLICT (workspace_id, issuer, subject) DO UPDATE
                                SET role = EXCLUDED.role, is_active = EXCLUDED.is_active
                                RETURNING membership_id, workspace_id, issuer, subject, role, is_active, created_at";
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("iss", emissor);
            cmd.Parameters.AddWithValue("sub", sujeito);
            cmd.Parameters.AddWithValue("role", papel.ParaTexto());
            cmd.Parameters.AddWithValue("active", ativo);
            var vinculo = await LerPrimeiroAsync(cmd, r => new VinculoMembro(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3),
                ConversorDeEnumeradores.ParaPapel(r.GetString(4)), r.GetBoolean(5), Instante(r, 6)), ct);
            return vinculo!;
        }, ct);
    }

    public Task<List<VinculoMembro>> ListarVinculosDoEspacoAsync(Guid espacoTrabalhoId, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            @"SELECT membership_id, workspace_id, issuer, subject, role, is_active, created_at
              FROM memberships WHERE workspace_id = @ws ORDER BY created_at",
            cmd => cmd.Parameters.AddWithValue("ws", espacoTrabalhoId),
            r => new VinculoMembro(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3),
                ConversorDeEnumeradores.ParaPapel(r.GetString(4)), r.GetBoolean(5), Instante(r, 6)), ct);

    public async Task<bool> RemoverVinculoAsync(Guid espacoTrabalhoId, Guid vinculoId, CancellationToken ct = default) =>
        await EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM memberships WHERE workspace_id = @ws AND membership_id = @id";
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("id", vinculoId);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }, ct);

    public Task<TopicoDecisao?> ObterTopicoPorChaveAsync(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            $"SELECT {ColunasTopico} FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_key = @key",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
                cmd.Parameters.AddWithValue("key", ValidadorDeInvariantes.NormalizarChaveDoTopico(chaveTopico));
            },
            MapearTopico, ct);

    public Task<TopicoDecisao?> ObterTopicoPorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            $"SELECT {ColunasTopico} FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
                cmd.Parameters.AddWithValue("id", topicoId);
            },
            MapearTopico, ct);

    public Task<DecisaoAtual?> ObterDecisaoAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            $"SELECT {ColunasDecisao} FROM decision_current WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
                cmd.Parameters.AddWithValue("id", topicoId);
            },
            MapearDecisao, ct);

    public Task<List<PropostaPendente>> ObterPropostasPendentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            $@"SELECT {ColunasProposta} FROM decision_pending_proposals
               WHERE workspace_id = @ws AND (@proj::uuid IS NULL OR project_id = @proj)
               ORDER BY proposed_at DESC, topic_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.Add(new NpgsqlParameter("proj", NpgsqlDbType.Uuid) { Value = (object?)projetoId ?? DBNull.Value });
            },
            MapearProposta, ct);

    public Task<List<DecisaoAtual>> ObterDecisoesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            $@"SELECT {ColunasDecisao} FROM decision_current
               WHERE workspace_id = @ws AND (@proj::uuid IS NULL OR project_id = @proj)
               ORDER BY updated_at DESC, topic_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.Add(new NpgsqlParameter("proj", NpgsqlDbType.Uuid) { Value = (object?)projetoId ?? DBNull.Value });
            },
            MapearDecisao, ct);

    public Task<List<DecisaoAtual>> ObterDecisoesVigentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            $@"SELECT {ColunasDecisao} FROM decision_current
               WHERE workspace_id = @ws AND status = 'active' AND active_record_id IS NOT NULL
                 AND (@proj::uuid IS NULL OR project_id = @proj)
               ORDER BY updated_at DESC, topic_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.Add(new NpgsqlParameter("proj", NpgsqlDbType.Uuid) { Value = (object?)projetoId ?? DBNull.Value });
            },
            MapearDecisao, ct);

    public Task<List<EventoMemoria>> ObterEventosRecentesAsync(Guid espacoTrabalhoId, int limite = 50, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            $"SELECT {ColunasEvento} FROM memory_events WHERE workspace_id = @ws ORDER BY recorded_at DESC, stream_version DESC, event_id LIMIT @lim",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("lim", limite);
            },
            MapearEvento, ct);

    public Task<HistoricoDoFluxo> ObterHistoricoDoFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, CancellationToken ct = default) =>
        EmTransacaoAsync(espacoTrabalhoId, (c, tx) => CarregarHistoricoAsync(c, tx, espacoTrabalhoId, fluxoId, ct), ct, somenteLeitura: true);

    private static async Task<HistoricoDoFluxo> CarregarHistoricoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, Guid fluxoId, CancellationToken ct)
    {
        List<EventoMemoria> eventos;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {ColunasEvento} FROM memory_events WHERE workspace_id = @ws AND stream_id = @s ORDER BY stream_version", c, tx))
        {
            cmd.Parameters.AddWithValue("ws", ws);
            cmd.Parameters.AddWithValue("s", fluxoId);
            eventos = await LerListaAsync(cmd, MapearEvento, ct);
        }

        List<CargaMemoria> cargas;
        await using (var cmd = new NpgsqlCommand(
            $@"SELECT {ColunasCarga} FROM memory_payloads
               WHERE workspace_id = @ws AND payload_id IN (
                   SELECT payload_id FROM memory_events WHERE workspace_id = @ws AND stream_id = @s AND payload_id IS NOT NULL)", c, tx))
        {
            cmd.Parameters.AddWithValue("ws", ws);
            cmd.Parameters.AddWithValue("s", fluxoId);
            cargas = await LerListaAsync(cmd, MapearCarga, ct);
        }

        return new HistoricoDoFluxo(eventos, cargas.ToDictionary(x => x.CargaId));
    }

    // Controle de concorrência e idempotência por chave
    private static async Task<T?> IniciarIdempotenciaAsync<T>(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, string principal, string operacao, string chave, string hash, CancellationToken ct) where T : class
    {
        await ExecutarAsync(c, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))",
            cmd => cmd.Parameters.AddWithValue("k", $"{ws}|{principal}|{operacao}|{chave}"), ct);

        await using var cmd = new NpgsqlCommand(
            @"SELECT args_hash, result_json FROM command_receipts
              WHERE workspace_id = @ws AND principal_id = @p AND operation = @op AND idempotency_key = @k", c, tx);
        cmd.Parameters.AddWithValue("ws", ws);
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("op", operacao);
        cmd.Parameters.AddWithValue("k", chave);
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (!await leitor.ReadAsync(ct))
            return null;

        if (leitor.GetString(0) != hash)
            throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");
        return JsonSerializer.Deserialize<T>(leitor.GetString(1))!;
    }

    private static Task SalvarReciboAsync<T>(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, string principal, string operacao, string chave, string hash, T resultado, DateTimeOffset agora, CancellationToken ct) =>
        ExecutarAsync(c, tx,
            @"INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
              VALUES (@id, @ws, @p, @op, @k, @h, @r, @now)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("ws", ws);
                cmd.Parameters.AddWithValue("p", principal);
                cmd.Parameters.AddWithValue("op", operacao);
                cmd.Parameters.AddWithValue("k", chave);
                cmd.Parameters.AddWithValue("h", hash);
                Json(cmd, "r", JsonSerializer.Serialize(resultado));
                cmd.Parameters.AddWithValue("now", agora);
            }, ct);

    private static async Task<TopicoDecisao?> TravarTopicoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, Guid proj, Guid? topicoId, string? chave, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $@"SELECT {ColunasTopico} FROM decision_topics
               WHERE workspace_id = @ws AND project_id = @proj AND {(topicoId.HasValue ? "topic_id = @id" : "topic_key = @key")}
               FOR UPDATE", c, tx);
        cmd.Parameters.AddWithValue("ws", ws);
        cmd.Parameters.AddWithValue("proj", proj);
        if (topicoId.HasValue) cmd.Parameters.AddWithValue("id", topicoId.Value);
        else cmd.Parameters.AddWithValue("key", chave!);
        return (await LerListaAsync(cmd, MapearTopico, ct)).FirstOrDefault();
    }

    private static async Task<Guid?> ObterPropostaPendenteIdAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, Guid topicoId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT proposal_id FROM decision_pending_proposals WHERE workspace_id = @ws AND topic_id = @id", c, tx);
        cmd.Parameters.AddWithValue("ws", ws);
        cmd.Parameters.AddWithValue("id", topicoId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<Guid?> ObterEventoPropostaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, Guid topicoId, Guid propostaId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT event_id FROM memory_events
              WHERE workspace_id = @ws AND stream_id = @s AND payload_id = @p AND type = @t
              ORDER BY stream_version DESC LIMIT 1", c, tx);
        cmd.Parameters.AddWithValue("ws", ws);
        cmd.Parameters.AddWithValue("s", topicoId);
        cmd.Parameters.AddWithValue("p", propostaId);
        cmd.Parameters.AddWithValue("t", TiposDeEvento.DecisaoProposta);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static Task InserirCargaAsync(NpgsqlConnection c, NpgsqlTransaction tx, CargaMemoria carga, CancellationToken ct) =>
        ExecutarAsync(c, tx,
            $@"INSERT INTO memory_payloads ({ColunasCarga})
               VALUES (@id, @ws, @proj, @title, @summary, @rationale, @keys, @incidents, @evidence, @created, @occurred)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", carga.CargaId);
                cmd.Parameters.AddWithValue("ws", carga.EspacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", carga.ProjetoId);
                cmd.Parameters.AddWithValue("title", carga.Titulo);
                cmd.Parameters.AddWithValue("summary", carga.Resumo);
                Nulavel(cmd, "rationale", carga.Justificativa);
                Json(cmd, "keys", ListaParaJson(carga.ChavesTecnicas));
                Json(cmd, "incidents", ListaParaJson(carga.IncidentesRelacionadosIds));
                Json(cmd, "evidence", EvidenciasParaJson(carga.Evidencias));
                cmd.Parameters.AddWithValue("created", carga.CriadoEm);
                Nulavel(cmd, "occurred", carga.OcorridoEm);
            }, ct);

    private static Task InserirEventoAsync(NpgsqlConnection c, NpgsqlTransaction tx, EventoMemoria ev, CancellationToken ct) =>
        ExecutarAsync(c, tx,
            $@"INSERT INTO memory_events ({ColunasEvento})
               VALUES (@id, @ws, @proj, @s, @v, @type, @schema, @at, @sub, @iss, @client, @payload, @ref, @reason)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", ev.EventoId);
                cmd.Parameters.AddWithValue("ws", ev.EspacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", ev.ProjetoId);
                cmd.Parameters.AddWithValue("s", ev.FluxoId);
                cmd.Parameters.AddWithValue("v", ev.VersaoFluxo);
                cmd.Parameters.AddWithValue("type", ev.Tipo);
                cmd.Parameters.AddWithValue("schema", ev.VersaoEsquema);
                cmd.Parameters.AddWithValue("at", ev.RegistradoEm);
                cmd.Parameters.AddWithValue("sub", ev.AutorSujeito);
                cmd.Parameters.AddWithValue("iss", ev.AutorEmissor);
                Nulavel(cmd, "client", ev.ClienteId);
                Nulavel(cmd, "payload", ev.CargaId);
                Nulavel(cmd, "ref", ev.EventoReferenciaId);
                Nulavel(cmd, "reason", ev.Motivo);
            }, ct);

    private static Task AtualizarVersaoTopicoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, Guid topicoId, int versao, DateTimeOffset agora, CancellationToken ct) =>
        ExecutarAsync(c, tx,
            "UPDATE decision_topics SET current_version = @v, updated_at = @now WHERE workspace_id = @ws AND topic_id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("v", versao);
                cmd.Parameters.AddWithValue("now", agora);
                cmd.Parameters.AddWithValue("ws", ws);
                cmd.Parameters.AddWithValue("id", topicoId);
            }, ct);

    private static async Task<DecisaoAtual?> AtualizarProjecaoDecisaoAsync(NpgsqlConnection c, NpgsqlTransaction tx, TopicoDecisao topico, CancellationToken ct)
    {
        var historico = await CarregarHistoricoAsync(c, tx, topico.EspacoTrabalhoId, topico.TopicoId, ct);
        if (historico.Eventos.Count == 0)
        {
            await ExecutarAsync(c, tx, "DELETE FROM decision_pending_proposals WHERE workspace_id = @ws AND topic_id = @id",
                cmd => { cmd.Parameters.AddWithValue("ws", topico.EspacoTrabalhoId); cmd.Parameters.AddWithValue("id", topico.TopicoId); }, ct);
            await ExecutarAsync(c, tx, "DELETE FROM decision_current WHERE workspace_id = @ws AND topic_id = @id",
                cmd => { cmd.Parameters.AddWithValue("ws", topico.EspacoTrabalhoId); cmd.Parameters.AddWithValue("id", topico.TopicoId); }, ct);
            return null;
        }

        var estado = RedutorDeDecisao.Reconstituir(topico.TopicoId, topico.EspacoTrabalhoId, topico.ProjetoId, topico.ChaveTopico,
            historico.Eventos, id => historico.Cargas.GetValueOrDefault(id));
        var (atual, pendente) = RedutorDeDecisao.Projetar(estado, historico.Eventos[^1].RegistradoEm);

        await ExecutarAsync(c, tx,
            $@"INSERT INTO decision_current ({ColunasDecisao})
               VALUES (@id, @ws, @proj, @key, @actId, @actVer, @sv, @status, @pend, @title, @summary, @rationale, @keys, @incidents, @evidence, @updated)
               ON CONFLICT (topic_id) DO UPDATE SET
                   topic_key = EXCLUDED.topic_key,
                   active_record_id = EXCLUDED.active_record_id,
                   active_record_version = EXCLUDED.active_record_version,
                   stream_version = EXCLUDED.stream_version,
                   status = EXCLUDED.status,
                   pending_proposal_id = EXCLUDED.pending_proposal_id,
                   title = EXCLUDED.title,
                   summary = EXCLUDED.summary,
                   rationale = EXCLUDED.rationale,
                   technical_keys = EXCLUDED.technical_keys,
                   related_incident_ids = EXCLUDED.related_incident_ids,
                   evidence = EXCLUDED.evidence,
                   updated_at = EXCLUDED.updated_at",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", atual.TopicoId);
                cmd.Parameters.AddWithValue("ws", atual.EspacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", atual.ProjetoId);
                cmd.Parameters.AddWithValue("key", atual.ChaveTopico);
                Nulavel(cmd, "actId", atual.RegistroAtivoId);
                Nulavel(cmd, "actVer", atual.VersaoRegistroAtivo);
                cmd.Parameters.AddWithValue("sv", atual.VersaoFluxo);
                cmd.Parameters.AddWithValue("status", atual.Situacao.ParaTexto());
                Nulavel(cmd, "pend", atual.PropostaPendenteId);
                cmd.Parameters.AddWithValue("title", atual.Titulo);
                cmd.Parameters.AddWithValue("summary", atual.Resumo);
                Nulavel(cmd, "rationale", atual.Justificativa);
                Json(cmd, "keys", ListaParaJson(atual.ChavesTecnicas));
                Json(cmd, "incidents", ListaParaJson(atual.IncidentesRelacionadosIds));
                Json(cmd, "evidence", EvidenciasParaJson(atual.Evidencias));
                cmd.Parameters.AddWithValue("updated", atual.AtualizadoEm);
            }, ct);

        if (pendente == null)
        {
            await ExecutarAsync(c, tx, "DELETE FROM decision_pending_proposals WHERE workspace_id = @ws AND topic_id = @id",
                cmd => { cmd.Parameters.AddWithValue("ws", topico.EspacoTrabalhoId); cmd.Parameters.AddWithValue("id", topico.TopicoId); }, ct);
        }
        else
        {
            await ExecutarAsync(c, tx,
                $@"INSERT INTO decision_pending_proposals ({ColunasProposta})
                   VALUES (@id, @ws, @proj, @key, @sv, @prop, @title, @summary, @rationale, @keys, @incidents, @evidence, @sub, @iss, @client, @at)
                   ON CONFLICT (topic_id) DO UPDATE SET
                       topic_key = EXCLUDED.topic_key,
                       stream_version = EXCLUDED.stream_version,
                       proposal_id = EXCLUDED.proposal_id,
                       title = EXCLUDED.title,
                       summary = EXCLUDED.summary,
                       rationale = EXCLUDED.rationale,
                       technical_keys = EXCLUDED.technical_keys,
                       related_incident_ids = EXCLUDED.related_incident_ids,
                       evidence = EXCLUDED.evidence,
                       author_subject = EXCLUDED.author_subject,
                       author_issuer = EXCLUDED.author_issuer,
                       client_id = EXCLUDED.client_id,
                       proposed_at = EXCLUDED.proposed_at",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("id", pendente.TopicoId);
                    cmd.Parameters.AddWithValue("ws", pendente.EspacoTrabalhoId);
                    cmd.Parameters.AddWithValue("proj", pendente.ProjetoId);
                    cmd.Parameters.AddWithValue("key", pendente.ChaveTopico);
                    cmd.Parameters.AddWithValue("sv", pendente.VersaoFluxo);
                    cmd.Parameters.AddWithValue("prop", pendente.PropostaId);
                    cmd.Parameters.AddWithValue("title", pendente.Titulo);
                    cmd.Parameters.AddWithValue("summary", pendente.Resumo);
                    Nulavel(cmd, "rationale", pendente.Justificativa);
                    Json(cmd, "keys", ListaParaJson(pendente.ChavesTecnicas));
                    Json(cmd, "incidents", ListaParaJson(pendente.IncidentesRelacionadosIds));
                    Json(cmd, "evidence", EvidenciasParaJson(pendente.Evidencias));
                    cmd.Parameters.AddWithValue("sub", pendente.AutorSujeito);
                    cmd.Parameters.AddWithValue("iss", pendente.AutorEmissor);
                    Nulavel(cmd, "client", pendente.ClienteId);
                    cmd.Parameters.AddWithValue("at", pendente.PropostaEm);
                }, ct);
        }

        return atual;
    }

    private static async Task AtualizarProjecaoIncidenteAsync(NpgsqlConnection c, NpgsqlTransaction tx, FluxoIncidente fluxo, CancellationToken ct)
    {
        var historico = await CarregarHistoricoAsync(c, tx, fluxo.EspacoTrabalhoId, fluxo.IncidenteId, ct);
        if (historico.Eventos.Count == 0)
            return;

        var estado = RedutorDeIncidente.Reconstituir(fluxo.IncidenteId, fluxo.EspacoTrabalhoId, fluxo.ProjetoId,
            historico.Eventos, id => historico.Cargas.GetValueOrDefault(id));
        var atual = RedutorDeIncidente.Projetar(estado, fluxo.CriadoEm);

        await ExecutarAsync(c, tx,
            $@"INSERT INTO incident_current ({ColunasIncidente})
               VALUES (@id, @ws, @proj, @rv, @sv, @status, @title, @summary, @occurred, @keys, @evidence, @created)
               ON CONFLICT (incident_id) DO UPDATE SET
                   record_version = EXCLUDED.record_version,
                   stream_version = EXCLUDED.stream_version,
                   status = EXCLUDED.status,
                   title = EXCLUDED.title,
                   summary = EXCLUDED.summary,
                   occurred_at = EXCLUDED.occurred_at,
                   technical_keys = EXCLUDED.technical_keys,
                   evidence = EXCLUDED.evidence",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", atual.IncidenteId);
                cmd.Parameters.AddWithValue("ws", atual.EspacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", atual.ProjetoId);
                cmd.Parameters.AddWithValue("rv", atual.VersaoRegistro);
                cmd.Parameters.AddWithValue("sv", atual.VersaoFluxo);
                cmd.Parameters.AddWithValue("status", atual.Situacao.ParaTexto());
                cmd.Parameters.AddWithValue("title", atual.Titulo);
                cmd.Parameters.AddWithValue("summary", atual.Resumo);
                cmd.Parameters.AddWithValue("occurred", atual.OcorridoEm);
                Json(cmd, "keys", ListaParaJson(atual.ChavesTecnicas));
                Json(cmd, "evidence", EvidenciasParaJson(atual.Evidencias));
                cmd.Parameters.AddWithValue("created", atual.CriadoEm);
            }, ct);
    }

    public Task<ResultadoProporDecisao> ProporDecisaoAsync(ComandoProporDecisao comando, CancellationToken ct = default)
    {
        const string operacao = "propose_decision";
        var hash = AjudanteIdempotencia.HashProporDecisao(comando);
        var ws = comando.EspacoTrabalhoId;

        return EmTransacaoAsync(ws, async (c, tx) =>
        {
            var existente = await IniciarIdempotenciaAsync<ResultadoProporDecisao>(c, tx, ws, comando.AutorSujeito, operacao, comando.ChaveIdempotencia, hash, ct);
            if (existente != null)
                return existente;

            var agora = Relogio.AgoraUtc();

            // Inicializa tópico para primeira proposta
            if (comando.VersaoEsperada == 0)
            {
                await ExecutarAsync(c, tx,
                    @"INSERT INTO decision_topics (topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at)
                      VALUES (@id, @ws, @proj, @key, 0, @now, @now)
                      ON CONFLICT (workspace_id, project_id, topic_key) DO NOTHING",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                        cmd.Parameters.AddWithValue("ws", ws);
                        cmd.Parameters.AddWithValue("proj", comando.ProjetoId);
                        cmd.Parameters.AddWithValue("key", comando.ChaveTopico);
                        cmd.Parameters.AddWithValue("now", agora);
                    }, ct);
            }

            var topico = await TravarTopicoAsync(c, tx, ws, comando.ProjetoId, null, comando.ChaveTopico, ct);
            var versaoAtual = topico?.VersaoAtual ?? 0;
            if (topico == null || versaoAtual != comando.VersaoEsperada)
                throw new ExcecaoDeConflitoDeVersao(topico?.TopicoId, comando.VersaoEsperada, versaoAtual);

            var pendente = await ObterPropostaPendenteIdAsync(c, tx, ws, topico.TopicoId, ct);
            if (pendente.HasValue)
                throw new ExcecaoDePropostaPendente(topico.TopicoId, pendente.Value);

            var carga = new CargaMemoria(Guid.NewGuid(), ws, comando.ProjetoId, comando.Titulo, comando.Resumo, comando.Justificativa,
                comando.ChavesTecnicas ?? [], comando.IncidentesRelacionadosIds ?? [], comando.Evidencias, agora);
            await InserirCargaAsync(c, tx, carga, ct);

            var novaVersao = versaoAtual + 1;
            await InserirEventoAsync(c, tx, new EventoMemoria(Guid.NewGuid(), ws, comando.ProjetoId, topico.TopicoId, novaVersao,
                TiposDeEvento.DecisaoProposta, 1, agora, comando.AutorSujeito, comando.AutorEmissor, comando.ClienteId, carga.CargaId, null), ct);
            await AtualizarVersaoTopicoAsync(c, tx, ws, topico.TopicoId, novaVersao, agora, ct);
            await AtualizarProjecaoDecisaoAsync(c, tx, topico with { VersaoAtual = novaVersao }, ct);

            var resultado = new ResultadoProporDecisao(topico.TopicoId, carga.CargaId, novaVersao, "proposed");
            await SalvarReciboAsync(c, tx, ws, comando.AutorSujeito, operacao, comando.ChaveIdempotencia, hash, resultado, agora, ct);
            return resultado;
        }, ct);
    }

    private static async Task<TopicoDecisao> TravarTopicoParaRevisaoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid ws, Guid proj, Guid topicoId, int versaoEsperada, CancellationToken ct)
    {
        var topico = await TravarTopicoAsync(c, tx, ws, proj, topicoId, null, ct)
            ?? throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");
        if (topico.VersaoAtual != versaoEsperada)
            throw new ExcecaoDeConflitoDeVersao(topicoId, versaoEsperada, topico.VersaoAtual);
        return topico;
    }

    public Task<ResultadoAprovarDecisao> AprovarDecisaoAsync(ComandoAprovarDecisao comando, CancellationToken ct = default)
    {
        const string operacao = "approve_decision";
        var hash = AjudanteIdempotencia.HashAprovarDecisao(comando);
        var ws = comando.EspacoTrabalhoId;

        return EmTransacaoAsync(ws, async (c, tx) =>
        {
            var existente = await IniciarIdempotenciaAsync<ResultadoAprovarDecisao>(c, tx, ws, comando.RevisorSujeito, operacao, comando.ChaveIdempotencia, hash, ct);
            if (existente != null)
                return existente;

            var topico = await TravarTopicoParaRevisaoAsync(c, tx, ws, comando.ProjetoId, comando.TopicoId, comando.VersaoEsperada, ct);
            if (await ObterPropostaPendenteIdAsync(c, tx, ws, topico.TopicoId, ct) != comando.PropostaId)
                throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para aprovação.");

            var eventoProposta = await ObterEventoPropostaAsync(c, tx, ws, topico.TopicoId, comando.PropostaId, ct);
            var agora = Relogio.AgoraUtc();
            var novaVersao = topico.VersaoAtual + 1;

            await InserirEventoAsync(c, tx, new EventoMemoria(Guid.NewGuid(), ws, comando.ProjetoId, topico.TopicoId, novaVersao,
                TiposDeEvento.DecisaoAprovada, 1, agora, comando.RevisorSujeito, comando.RevisorEmissor, "web-session", comando.PropostaId, eventoProposta), ct);
            await AtualizarVersaoTopicoAsync(c, tx, ws, topico.TopicoId, novaVersao, agora, ct);
            await AtualizarProjecaoDecisaoAsync(c, tx, topico with { VersaoAtual = novaVersao }, ct);

            var resultado = new ResultadoAprovarDecisao(topico.TopicoId, comando.PropostaId, novaVersao, "active");
            await SalvarReciboAsync(c, tx, ws, comando.RevisorSujeito, operacao, comando.ChaveIdempotencia, hash, resultado, agora, ct);
            return resultado;
        }, ct);
    }

    public Task<ResultadoRejeitarDecisao> RejeitarDecisaoAsync(ComandoRejeitarDecisao comando, CancellationToken ct = default)
    {
        const string operacao = "reject_decision";
        var hash = AjudanteIdempotencia.HashRejeitarDecisao(comando);
        var ws = comando.EspacoTrabalhoId;

        return EmTransacaoAsync(ws, async (c, tx) =>
        {
            var existente = await IniciarIdempotenciaAsync<ResultadoRejeitarDecisao>(c, tx, ws, comando.RevisorSujeito, operacao, comando.ChaveIdempotencia, hash, ct);
            if (existente != null)
                return existente;

            var topico = await TravarTopicoParaRevisaoAsync(c, tx, ws, comando.ProjetoId, comando.TopicoId, comando.VersaoEsperada, ct);
            if (await ObterPropostaPendenteIdAsync(c, tx, ws, topico.TopicoId, ct) != comando.PropostaId)
                throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para rejeição.");

            var eventoProposta = await ObterEventoPropostaAsync(c, tx, ws, topico.TopicoId, comando.PropostaId, ct);
            var agora = Relogio.AgoraUtc();
            var novaVersao = topico.VersaoAtual + 1;

            await InserirEventoAsync(c, tx, new EventoMemoria(Guid.NewGuid(), ws, comando.ProjetoId, topico.TopicoId, novaVersao,
                TiposDeEvento.DecisaoRejeitada, 1, agora, comando.RevisorSujeito, comando.RevisorEmissor, "web-session", comando.PropostaId, eventoProposta)
            { Motivo = comando.Motivo }, ct);
            await AtualizarVersaoTopicoAsync(c, tx, ws, topico.TopicoId, novaVersao, agora, ct);
            var atual = await AtualizarProjecaoDecisaoAsync(c, tx, topico with { VersaoAtual = novaVersao }, ct);

            var resultado = new ResultadoRejeitarDecisao(topico.TopicoId, novaVersao, atual!.Situacao.ParaTexto());
            await SalvarReciboAsync(c, tx, ws, comando.RevisorSujeito, operacao, comando.ChaveIdempotencia, hash, resultado, agora, ct);
            return resultado;
        }, ct);
    }

    public Task<ResultadoRestaurarDecisao> RestaurarDecisaoAsync(ComandoRestaurarDecisao comando, CancellationToken ct = default)
    {
        const string operacao = "restore_decision";
        var hash = AjudanteIdempotencia.HashRestaurarDecisao(comando);
        var ws = comando.EspacoTrabalhoId;

        return EmTransacaoAsync(ws, async (c, tx) =>
        {
            var existente = await IniciarIdempotenciaAsync<ResultadoRestaurarDecisao>(c, tx, ws, comando.RevisorSujeito, operacao, comando.ChaveIdempotencia, hash, ct);
            if (existente != null)
                return existente;

            var topico = await TravarTopicoParaRevisaoAsync(c, tx, ws, comando.ProjetoId, comando.TopicoId, comando.VersaoEsperada, ct);
            var pendente = await ObterPropostaPendenteIdAsync(c, tx, ws, topico.TopicoId, ct);
            if (pendente.HasValue)
                throw new ExcecaoDePropostaPendente(topico.TopicoId, pendente.Value);
            if (comando.VersaoParaRestaurar > topico.VersaoAtual)
                throw new ExcecaoDeValidacao("A versão a restaurar não existe no histórico do tópico.");

            var historico = await CarregarHistoricoAsync(c, tx, ws, topico.TopicoId, ct);
            var snapshot = RedutorDeDecisao.Reconstituir(topico.TopicoId, ws, topico.ProjetoId, topico.ChaveTopico,
                historico.Eventos, id => historico.Cargas.GetValueOrDefault(id), comando.VersaoParaRestaurar);
            if (snapshot.RegistroAtivoId == null)
                throw new ExcecaoDeValidacao("A versão indicada não possui decisão ativa para restauração.");
            var original = historico.Cargas.GetValueOrDefault(snapshot.RegistroAtivoId.Value)
                ?? throw new ExcecaoDeNaoEncontrado("Carga histórica da decisão não encontrada.");

            var agora = Relogio.AgoraUtc();
            var novaCarga = original with { CargaId = Guid.NewGuid(), CriadoEm = agora };
            await InserirCargaAsync(c, tx, novaCarga, ct);

            var novaVersao = topico.VersaoAtual + 1;
            await InserirEventoAsync(c, tx, new EventoMemoria(Guid.NewGuid(), ws, comando.ProjetoId, topico.TopicoId, novaVersao,
                TiposDeEvento.DecisaoProposta, 1, agora, comando.RevisorSujeito, comando.RevisorEmissor, "web-session", novaCarga.CargaId, null), ct);
            await AtualizarVersaoTopicoAsync(c, tx, ws, topico.TopicoId, novaVersao, agora, ct);
            await AtualizarProjecaoDecisaoAsync(c, tx, topico with { VersaoAtual = novaVersao }, ct);

            var resultado = new ResultadoRestaurarDecisao(topico.TopicoId, novaCarga.CargaId, novaVersao, "proposed");
            await SalvarReciboAsync(c, tx, ws, comando.RevisorSujeito, operacao, comando.ChaveIdempotencia, hash, resultado, agora, ct);
            return resultado;
        }, ct);
    }

    public Task<FluxoIncidente?> ObterIncidentePorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            @"SELECT incident_id, workspace_id, project_id, current_version, created_at, updated_at
              FROM incident_streams WHERE workspace_id = @ws AND project_id = @proj AND incident_id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
                cmd.Parameters.AddWithValue("id", incidenteId);
            },
            r => new FluxoIncidente(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), Instante(r, 4), Instante(r, 5)), ct);

    public Task<IncidenteAtual?> ObterIncidenteAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default) =>
        PrimeiroAsync(espacoTrabalhoId,
            $"SELECT {ColunasIncidente} FROM incident_current WHERE workspace_id = @ws AND project_id = @proj AND incident_id = @id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
                cmd.Parameters.AddWithValue("id", incidenteId);
            },
            MapearIncidente, ct);

    public Task<List<IncidenteAtual>> ObterIncidentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            $@"SELECT {ColunasIncidente} FROM incident_current
               WHERE workspace_id = @ws AND (@proj::uuid IS NULL OR project_id = @proj)
               ORDER BY occurred_at DESC, incident_id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.Add(new NpgsqlParameter("proj", NpgsqlDbType.Uuid) { Value = (object?)projetoId ?? DBNull.Value });
            },
            MapearIncidente, ct);

    public Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(ComandoRegistrarIncidente comando, CancellationToken ct = default)
    {
        const string operacao = "log_incident";
        var hash = AjudanteIdempotencia.HashRegistrarIncidente(comando);
        var ws = comando.EspacoTrabalhoId;

        return EmTransacaoAsync(ws, async (c, tx) =>
        {
            var existente = await IniciarIdempotenciaAsync<ResultadoRegistrarIncidente>(c, tx, ws, comando.AutorSujeito, operacao, comando.ChaveIdempotencia, hash, ct);
            if (existente != null)
                return existente;

            var agora = Relogio.AgoraUtc();
            var fluxo = new FluxoIncidente(Guid.NewGuid(), ws, comando.ProjetoId, 1, agora, agora);
            await ExecutarAsync(c, tx,
                @"INSERT INTO incident_streams (incident_id, workspace_id, project_id, current_version, created_at, updated_at)
                  VALUES (@id, @ws, @proj, 1, @now, @now)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("id", fluxo.IncidenteId);
                    cmd.Parameters.AddWithValue("ws", ws);
                    cmd.Parameters.AddWithValue("proj", comando.ProjetoId);
                    cmd.Parameters.AddWithValue("now", agora);
                }, ct);

            var carga = new CargaMemoria(Guid.NewGuid(), ws, comando.ProjetoId, comando.Titulo, comando.Resumo, null,
                comando.ChavesTecnicas ?? [], [], comando.Evidencias, agora)
            { OcorridoEm = Relogio.Truncar(comando.OcorridoEm ?? agora) };
            await InserirCargaAsync(c, tx, carga, ct);
            await InserirEventoAsync(c, tx, new EventoMemoria(Guid.NewGuid(), ws, comando.ProjetoId, fluxo.IncidenteId, 1,
                TiposDeEvento.IncidenteReportado, 1, agora, comando.AutorSujeito, comando.AutorEmissor, comando.ClienteId, carga.CargaId, null), ct);
            await AtualizarProjecaoIncidenteAsync(c, tx, fluxo, ct);

            var resultado = new ResultadoRegistrarIncidente(fluxo.IncidenteId, 1, "reported");
            await SalvarReciboAsync(c, tx, ws, comando.AutorSujeito, operacao, comando.ChaveIdempotencia, hash, resultado, agora, ct);
            return resultado;
        }, ct);
    }

    private static string EscaparLike(string texto) =>
        texto.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public Task<ResultadoConsultaContexto> ConsultarContextoAsync(ConsultaContexto consulta, CancellationToken ct = default)
    {
        var ws = consulta.EspacoTrabalhoId;
        var termos = consulta.TermoBusca
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(consulta.TermoBusca.Trim())
            .Select(ValidadorDeInvariantes.NormalizarChaveTecnica)
            .Distinct()
            .ToArray();
        var padraoLike = "%" + EscaparLike(consulta.TermoBusca.Trim()) + "%";

        return EmTransacaoAsync(ws, async (c, tx) =>
        {
            var candidatos = new List<(ItemContexto Item, bool Exata, float Rank, DateTimeOffset Atualizado)>();

            void Parametros(NpgsqlCommand cmd)
            {
                cmd.Parameters.AddWithValue("ws", ws);
                cmd.Parameters.AddWithValue("proj", consulta.ProjetoId);
                cmd.Parameters.AddWithValue("q", consulta.TermoBusca);
                cmd.Parameters.Add(new NpgsqlParameter("termos", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = termos });
                cmd.Parameters.AddWithValue("like", padraoLike);
                cmd.Parameters.AddWithValue("lim", consulta.Limite);
            }

            // Busca textual e ordenação determinística
            if (consulta.TipoRegistro is "decision" or "both")
            {
                await using var cmd = new NpgsqlCommand(
                    @"WITH q AS (SELECT websearch_to_tsquery('portuguese', @q) || websearch_to_tsquery('simple', @q) AS tsq)
                      SELECT d.topic_id, d.active_record_id, d.active_record_version, d.stream_version, d.title, d.summary, d.evidence,
                             (d.technical_keys_norm && @termos) AS exata,
                             ts_rank_cd(d.search_vector, q.tsq) AS rank,
                             d.updated_at
                      FROM decision_current d, q
                      WHERE d.workspace_id = @ws AND d.project_id = @proj
                        AND d.status = 'active' AND d.active_record_id IS NOT NULL
                        AND (d.search_vector @@ q.tsq
                             OR d.technical_keys_norm && @termos
                             OR d.title ILIKE @like ESCAPE '\'
                             OR d.summary ILIKE @like ESCAPE '\')
                      ORDER BY exata DESC, rank DESC, d.updated_at DESC, d.topic_id
                      LIMIT @lim", c, tx);
                Parametros(cmd);
                candidatos.AddRange(await LerListaAsync(cmd, r => (
                    new ItemContexto("decision", r.GetGuid(0), null, r.GetGuid(1), r.GetInt32(2), r.GetInt32(3), "active",
                        r.GetString(4), r.GetString(5), EvidenciasDeJson(r.GetString(6))),
                    r.GetBoolean(7), r.GetFloat(8), Instante(r, 9)), ct));
            }

            if (consulta.TipoRegistro is "incident" or "both")
            {
                await using var cmd = new NpgsqlCommand(
                    @"WITH q AS (SELECT websearch_to_tsquery('portuguese', @q) || websearch_to_tsquery('simple', @q) AS tsq)
                      SELECT i.incident_id, i.record_version, i.stream_version, i.title, i.summary, i.evidence,
                             (i.technical_keys_norm && @termos) AS exata,
                             ts_rank_cd(i.search_vector, q.tsq) AS rank,
                             i.created_at
                      FROM incident_current i, q
                      WHERE i.workspace_id = @ws AND i.project_id = @proj AND i.status = 'reported'
                        AND (i.search_vector @@ q.tsq
                             OR i.technical_keys_norm && @termos
                             OR i.title ILIKE @like ESCAPE '\'
                             OR i.summary ILIKE @like ESCAPE '\')
                      ORDER BY exata DESC, rank DESC, i.created_at DESC, i.incident_id
                      LIMIT @lim", c, tx);
                Parametros(cmd);
                candidatos.AddRange(await LerListaAsync(cmd, r => (
                    new ItemContexto("incident", null, r.GetGuid(0), r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), "reported",
                        r.GetString(3), r.GetString(4), EvidenciasDeJson(r.GetString(5))),
                    r.GetBoolean(6), r.GetFloat(7), Instante(r, 8)), ct));
            }

            var itens = candidatos
                .OrderByDescending(x => x.Exata)
                .ThenByDescending(x => x.Rank)
                .ThenByDescending(x => x.Atualizado)
                .ThenBy(x => x.Item.RegistroId)
                .Take(consulta.Limite)
                .Select(x => x.Item)
                .ToList();

            var consultaId = Guid.NewGuid();
            var agora = Relogio.AgoraUtc();
            var itensRecibo = itens.Select((item, idx) => new ItemReciboConsulta(
                Guid.NewGuid(), consultaId, ws, idx + 1, item.TipoRegistro, item.TopicoId ?? item.IncidenteId!.Value,
                item.RegistroId, item.VersaoRegistro, item.VersaoFluxo, item.Situacao)).ToList();
            var digest = DigestDeComprovante.Calcular(consultaId, ws, consulta.ProjetoId, consulta.PrincipalId,
                consulta.PrincipalEmissor, consulta.ClienteId, "textual", agora, itensRecibo);

            await ExecutarAsync(c, tx,
                @"INSERT INTO context_receipts (query_id, workspace_id, project_id, principal_id, principal_issuer, client_id, search_mode, created_at, digest)
                  VALUES (@id, @ws, @proj, @p, @pi, @client, 'textual', @now, @digest)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("id", consultaId);
                    cmd.Parameters.AddWithValue("ws", ws);
                    cmd.Parameters.AddWithValue("proj", consulta.ProjetoId);
                    cmd.Parameters.AddWithValue("p", consulta.PrincipalId);
                    Nulavel(cmd, "pi", consulta.PrincipalEmissor);
                    Nulavel(cmd, "client", consulta.ClienteId);
                    cmd.Parameters.AddWithValue("now", agora);
                    cmd.Parameters.AddWithValue("digest", digest);
                }, ct);

            foreach (var item in itensRecibo)
            {
                await ExecutarAsync(c, tx,
                    @"INSERT INTO context_receipt_items (receipt_item_id, query_id, workspace_id, position, kind, stream_id, record_id, record_version, stream_version, status)
                      VALUES (@id, @q, @ws, @pos, @kind, @s, @rec, @rv, @sv, @status)",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("id", item.ItemReciboId);
                        cmd.Parameters.AddWithValue("q", consultaId);
                        cmd.Parameters.AddWithValue("ws", ws);
                        cmd.Parameters.AddWithValue("pos", item.Posicao);
                        cmd.Parameters.AddWithValue("kind", item.TipoRegistro);
                        cmd.Parameters.AddWithValue("s", item.FluxoId);
                        cmd.Parameters.AddWithValue("rec", item.RegistroId);
                        cmd.Parameters.AddWithValue("rv", item.VersaoRegistro);
                        cmd.Parameters.AddWithValue("sv", item.VersaoFluxo);
                        cmd.Parameters.AddWithValue("status", item.Situacao);
                    }, ct);
            }

            return new ResultadoConsultaContexto(consultaId, itens.Count > 0 ? "found" : "none", "textual", itens);
        }, ct);
    }

    public Task<ResultadoComprovanteContexto?> ObterComprovanteContextoAsync(Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, CancellationToken ct = default) =>
        EmTransacaoAsync<ResultadoComprovanteContexto?>(espacoTrabalhoId, async (c, tx) =>
        {
            string principal;
            string? principalEmissor, cliente, digest;
            string modo;
            DateTimeOffset criadoEm;

            await using (var cmd = new NpgsqlCommand(
                @"SELECT principal_id, principal_issuer, client_id, search_mode, created_at, digest
                  FROM context_receipts WHERE workspace_id = @ws AND project_id = @proj AND query_id = @id", c, tx))
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("proj", projetoId);
                cmd.Parameters.AddWithValue("id", consultaId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return null;
                principal = r.GetString(0);
                principalEmissor = TextoOuNulo(r, 1);
                cliente = TextoOuNulo(r, 2);
                modo = r.GetString(3);
                criadoEm = Instante(r, 4);
                digest = TextoOuNulo(r, 5);
            }

            List<ItemReciboConsulta> itens;
            await using (var cmd = new NpgsqlCommand(
                @"SELECT receipt_item_id, query_id, workspace_id, position, kind, stream_id, record_id, record_version, stream_version, status
                  FROM context_receipt_items WHERE workspace_id = @ws AND query_id = @id ORDER BY position", c, tx))
            {
                cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
                cmd.Parameters.AddWithValue("id", consultaId);
                itens = await LerListaAsync(cmd, r => new ItemReciboConsulta(
                    r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4),
                    r.GetGuid(5), r.GetGuid(6), r.GetInt32(7), r.GetInt32(8), r.GetString(9)), ct);
            }

            var recalculado = DigestDeComprovante.Calcular(consultaId, espacoTrabalhoId, projetoId, principal, principalEmissor, cliente, modo, criadoEm, itens);
            return new ResultadoComprovanteContexto(consultaId, espacoTrabalhoId, projetoId, principal, principalEmissor, cliente,
                modo, criadoEm, itens, digest ?? string.Empty, digest != null && digest == recalculado);
        }, ct, somenteLeitura: true);

    // Rebuild e retenção

    private async Task<List<Guid>> ListarEspacosAsync(Guid? filtro, CancellationToken ct)
    {
        if (filtro.HasValue)
            return [filtro.Value];
        await using var cmd = _fonte.CreateCommand("SELECT workspace_id FROM workspaces ORDER BY created_at, workspace_id");
        return await LerListaAsync(cmd, r => r.GetGuid(0), ct);
    }

    public async Task ReconstruirProjecoesAsync(Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        foreach (var ws in await ListarEspacosAsync(espacoTrabalhoId, ct))
        {
            // Reconstrução de projeções do workspace
            await EmTransacaoAsync(ws, async (c, tx) =>
            {
                foreach (var tabela in new[] { "decision_pending_proposals", "decision_current", "incident_current" })
                    await ExecutarAsync(c, tx, $"DELETE FROM {tabela} WHERE workspace_id = @ws", cmd => cmd.Parameters.AddWithValue("ws", ws), ct);

                // Atualiza versão do tópico com base nos eventos
                await ExecutarAsync(c, tx,
                    @"UPDATE decision_topics t SET current_version = coalesce(
                          (SELECT max(e.stream_version) FROM memory_events e WHERE e.workspace_id = t.workspace_id AND e.stream_id = t.topic_id), 0)
                      WHERE t.workspace_id = @ws",
                    cmd => cmd.Parameters.AddWithValue("ws", ws), ct);

                List<TopicoDecisao> topicos;
                await using (var cmd = new NpgsqlCommand($"SELECT {ColunasTopico} FROM decision_topics WHERE workspace_id = @ws ORDER BY topic_id", c, tx))
                {
                    cmd.Parameters.AddWithValue("ws", ws);
                    topicos = await LerListaAsync(cmd, MapearTopico, ct);
                }
                foreach (var topico in topicos)
                    await AtualizarProjecaoDecisaoAsync(c, tx, topico, ct);

                List<FluxoIncidente> incidentes;
                await using (var cmd = new NpgsqlCommand(
                    "SELECT incident_id, workspace_id, project_id, current_version, created_at, updated_at FROM incident_streams WHERE workspace_id = @ws ORDER BY incident_id", c, tx))
                {
                    cmd.Parameters.AddWithValue("ws", ws);
                    incidentes = await LerListaAsync(cmd, r => new FluxoIncidente(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), Instante(r, 4), Instante(r, 5)), ct);
                }
                foreach (var incidente in incidentes)
                    await AtualizarProjecaoIncidenteAsync(c, tx, incidente, ct);

                return 0;
            }, ct);
        }
    }

    public async Task<int> ExpurgarRecibosAsync(DateTimeOffset criadosAntesDe, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var ws in await ListarEspacosAsync(null, ct))
        {
            total += await EmTransacaoAsync(ws, async (c, tx) =>
            {
                var removidos = 0;
                foreach (var tabela in new[] { "context_receipts", "command_receipts" })
                {
                    await using var cmd = new NpgsqlCommand($"DELETE FROM {tabela} WHERE workspace_id = @ws AND created_at < @lim", c, tx);
                    cmd.Parameters.AddWithValue("ws", ws);
                    cmd.Parameters.AddWithValue("lim", criadosAntesDe);
                    removidos += await cmd.ExecuteNonQueryAsync(ct);
                }
                return removidos;
            }, ct);
        }
        return total;
    }

    // Usuários locais
    public async Task<bool> PossuiAlgumUsuarioAsync(CancellationToken ct = default)
    {
        await using var cmd = _fonte.CreateCommand("SELECT EXISTS (SELECT 1 FROM local_users)");
        return (await cmd.ExecuteScalarAsync(ct)) is true;
    }

    public async Task<UsuarioLocal?> ObterPorNomeUsuarioAsync(string nomeUsuario, CancellationToken ct = default)
    {
        await using var cmd = _fonte.CreateCommand(
            @"SELECT user_id, username, issuer, subject, display_name, password_hash, is_active, created_at
              FROM local_users WHERE username = @u");
        cmd.Parameters.AddWithValue("u", nomeUsuario.Trim().ToLowerInvariant());
        return await LerPrimeiroAsync(cmd, MapearUsuarioLocal, ct);
    }

    public Task<List<UsuarioLocal>> ListarUsuariosDoEspacoAsync(Guid espacoTrabalhoId, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            @"SELECT u.user_id, u.username, u.issuer, u.subject, u.display_name, u.password_hash, u.is_active, u.created_at
              FROM local_users u
              JOIN memberships m ON m.workspace_id = @ws AND m.issuer = u.issuer AND m.subject = u.subject
              ORDER BY u.created_at",
            cmd => cmd.Parameters.AddWithValue("ws", espacoTrabalhoId),
            MapearUsuarioLocal, ct);

    public async Task<UsuarioLocal> CriarUsuarioLocalAsync(UsuarioLocal usuario, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = _fonte.CreateCommand(
                @"INSERT INTO local_users (user_id, username, issuer, subject, display_name, password_hash, is_active, created_at)
                  VALUES (@id, @u, @iss, @sub, @dn, @hash, @act, @cr)
                  RETURNING user_id, username, issuer, subject, display_name, password_hash, is_active, created_at");
            cmd.Parameters.AddWithValue("id", usuario.UsuarioId);
            cmd.Parameters.AddWithValue("u", usuario.NomeUsuario);
            cmd.Parameters.AddWithValue("iss", usuario.Emissor);
            cmd.Parameters.AddWithValue("sub", usuario.Sujeito);
            Nulavel(cmd, "dn", usuario.NomeExibicao);
            cmd.Parameters.AddWithValue("hash", usuario.HashSenha);
            cmd.Parameters.AddWithValue("act", usuario.Ativo);
            cmd.Parameters.AddWithValue("cr", usuario.CriadoEm);
            return (await LerPrimeiroAsync(cmd, MapearUsuarioLocal, ct))!;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new ExcecaoDeConflito($"Usuário local '{usuario.NomeUsuario}' já existe.");
        }
    }

    // Chaves de API para agentes MCP
    public Task<ChaveApiAgente> CriarChaveApiAsync(ChaveApiAgente chave, CancellationToken ct = default) =>
        EmTransacaoAsync(chave.EspacoTrabalhoId, async (c, tx) =>
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    @"INSERT INTO agent_api_keys (key_id, workspace_id, name, key_prefix, key_hash, issuer, subject, client_id, scopes, is_active, created_by_subject, created_at, expires_at, last_used_at)
                      VALUES (@id, @ws, @nm, @pref, @hash, @iss, @sub, @cid, @scopes, @act, @by, @cr, @exp, @lu)
                      RETURNING key_id, workspace_id, name, key_prefix, key_hash, issuer, subject, client_id, scopes, is_active, created_by_subject, created_at, expires_at, last_used_at",
                    c, tx);
                cmd.Parameters.AddWithValue("id", chave.ChaveId);
                cmd.Parameters.AddWithValue("ws", chave.EspacoTrabalhoId);
                cmd.Parameters.AddWithValue("nm", chave.Nome);
                cmd.Parameters.AddWithValue("pref", chave.PrefixoChave);
                cmd.Parameters.AddWithValue("hash", chave.HashChave);
                cmd.Parameters.AddWithValue("iss", chave.Emissor);
                cmd.Parameters.AddWithValue("sub", chave.Sujeito);
                Nulavel(cmd, "cid", chave.ClienteId);
                Json(cmd, "scopes", ListaParaJson(chave.Escopos));
                cmd.Parameters.AddWithValue("act", chave.Ativo);
                cmd.Parameters.AddWithValue("by", chave.CriadoPorSujeito);
                cmd.Parameters.AddWithValue("cr", chave.CriadoEm);
                Nulavel(cmd, "exp", chave.ExpiraEm);
                Nulavel(cmd, "lu", chave.UltimoUsoEm);
                return (await LerPrimeiroAsync(cmd, MapearChaveApi, ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                throw new ExcecaoDeConflito("Conflito ao registrar chave de API.");
            }
        }, ct);

    public Task<List<ChaveApiAgente>> ListarChavesApiAsync(Guid espacoTrabalhoId, CancellationToken ct = default) =>
        ListarAsync(espacoTrabalhoId,
            @"SELECT key_id, workspace_id, name, key_prefix, key_hash, issuer, subject, client_id, scopes, is_active, created_by_subject, created_at, expires_at, last_used_at
              FROM agent_api_keys WHERE workspace_id = @ws ORDER BY created_at DESC",
            cmd => cmd.Parameters.AddWithValue("ws", espacoTrabalhoId),
            MapearChaveApi, ct);

    public async Task<ChaveApiAgente?> ObterChaveApiPorHashAsync(string hashSha256Hex, CancellationToken ct = default)
    {
        var lista = await ListarAsync(null,
            @"SELECT key_id, workspace_id, name, key_prefix, key_hash, issuer, subject, client_id, scopes, is_active, created_by_subject, created_at, expires_at, last_used_at
              FROM agent_api_keys WHERE key_hash = @hash",
            cmd => cmd.Parameters.AddWithValue("hash", hashSha256Hex),
            MapearChaveApi, ct, hashChave: hashSha256Hex);
        return lista.FirstOrDefault();
    }

    public Task RegistrarUsoChaveApiAsync(Guid espacoTrabalhoId, Guid chaveId, DateTimeOffset usadoEm, CancellationToken ct = default) =>
        EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE agent_api_keys SET last_used_at = @lu WHERE workspace_id = @ws AND key_id = @id", c, tx);
            cmd.Parameters.AddWithValue("lu", usadoEm);
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("id", chaveId);
            await cmd.ExecuteNonQueryAsync(ct);
            return 0;
        }, ct);

    public Task<bool> RevogarChaveApiAsync(Guid espacoTrabalhoId, Guid chaveId, CancellationToken ct = default) =>
        EmTransacaoAsync(espacoTrabalhoId, async (c, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM agent_api_keys WHERE workspace_id = @ws AND key_id = @id", c, tx);
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("id", chaveId);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }, ct);

    private static UsuarioLocal MapearUsuarioLocal(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        TextoOuNulo(r, 4), r.GetString(5), r.GetBoolean(6), Instante(r, 7));

    private static ChaveApiAgente MapearChaveApi(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), TextoOuNulo(r, 7), TextosDeJson(r.GetString(8)),
        r.GetBoolean(9), r.GetString(10), Instante(r, 11),
        r.IsDBNull(12) ? null : Instante(r, 12),
        r.IsDBNull(13) ? null : Instante(r, 13));
}
