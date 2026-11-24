using System.Text.Json;
using Npgsql;
using Sentinel.Core.Comandos;
using Sentinel.Core.Consultas;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Persistence.Comum;
using Sentinel.Persistence.Contratos;

namespace Sentinel.Persistence.PostgreSql;

public sealed class RepositorioSentinelPostgreSql :
    IRepositorioEspacoTrabalho,
    IRepositorioDecisao,
    IRepositorioIncidente,
    IRepositorioContexto,
    IRepositorioRecibo,
    IReconstrutorProjecoes
{
    private readonly string _stringConexao;

    public RepositorioSentinelPostgreSql(string stringConexao)
    {
        _stringConexao = stringConexao;
    }

    private async Task<NpgsqlConnection> AbrirConexaoComRlsAsync(Guid espacoTrabalhoId, CancellationToken ct)
    {
        var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{espacoTrabalhoId}'", conexao);
        await cmd.ExecuteNonQueryAsync(ct);
        return conexao;
    }

    public async Task<EspacoTrabalho?> ObterEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        await using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT workspace_id, name, slug, created_at FROM workspaces WHERE workspace_id = @id", conexao);
        cmd.Parameters.AddWithValue("id", espacoTrabalhoId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new EspacoTrabalho(
                leitor.GetGuid(0),
                leitor.GetString(1),
                leitor.GetString(2),
                leitor.GetFieldValue<DateTimeOffset>(3)
            );
        }
        return null;
    }

    public async Task<List<EspacoTrabalho>> ObterTodosEspacosTrabalhoAsync(CancellationToken ct = default)
    {
        await using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT workspace_id, name, slug, created_at FROM workspaces ORDER BY created_at ASC", conexao);

        var resultado = new List<EspacoTrabalho>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            resultado.Add(new EspacoTrabalho(
                leitor.GetGuid(0),
                leitor.GetString(1),
                leitor.GetString(2),
                leitor.GetFieldValue<DateTimeOffset>(3)
            ));
        }
        return resultado;
    }

    public async Task<VinculoMembro?> ObterVinculoMembroAsync(Guid espacoTrabalhoId, string emissor, string sujeito, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT membership_id, workspace_id, issuer, subject, role, is_active, created_at 
              FROM memberships 
              WHERE workspace_id = @ws AND issuer = @iss AND subject = @sub", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("iss", emissor);
        cmd.Parameters.AddWithValue("sub", sujeito);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            var papelStr = leitor.GetString(4);
            Enum.TryParse<PapelUsuario>(papelStr, true, out var papel);
            return new VinculoMembro(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetString(2),
                leitor.GetString(3),
                papel,
                leitor.GetBoolean(5),
                leitor.GetFieldValue<DateTimeOffset>(6)
            );
        }
        return null;
    }

    public async Task<Projeto?> ObterProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT project_id, workspace_id, name, slug, created_at FROM projects WHERE workspace_id = @ws AND project_id = @proj", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("proj", projetoId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new Projeto(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetString(2),
                leitor.GetString(3),
                leitor.GetFieldValue<DateTimeOffset>(4)
            );
        }
        return null;
    }

    public async Task<List<Projeto>> ObterProjetosPorEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT project_id, workspace_id, name, slug, created_at FROM projects WHERE workspace_id = @ws ORDER BY name", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);

        var projetos = new List<Projeto>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            projetos.Add(new Projeto(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetString(2),
                leitor.GetString(3),
                leitor.GetFieldValue<DateTimeOffset>(4)
            ));
        }
        return projetos;
    }

    public async Task<TopicoDecisao?> ObterTopicoPorChaveAsync(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at
              FROM decision_topics
              WHERE workspace_id = @ws AND project_id = @proj AND lower(topic_key) = lower(@key)", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("proj", projetoId);
        cmd.Parameters.AddWithValue("key", chaveTopico);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new TopicoDecisao(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetString(3),
                leitor.GetInt32(4),
                leitor.GetFieldValue<DateTimeOffset>(5),
                leitor.GetFieldValue<DateTimeOffset>(6)
            );
        }
        return null;
    }

    public async Task<TopicoDecisao?> ObterTopicoPorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at
              FROM decision_topics
              WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("proj", projetoId);
        cmd.Parameters.AddWithValue("id", topicoId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new TopicoDecisao(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetString(3),
                leitor.GetInt32(4),
                leitor.GetFieldValue<DateTimeOffset>(5),
                leitor.GetFieldValue<DateTimeOffset>(6)
            );
        }
        return null;
    }

    public async Task<DecisaoAtual?> ObterDecisaoAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version,
                     stream_version, status, pending_proposal_id, title, summary, justification,
                     technical_keys, related_incident_ids, evidence, updated_at
              FROM decision_current
              WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("proj", projetoId);
        cmd.Parameters.AddWithValue("id", topicoId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return MapearDecisaoAtual(leitor);
        }
        return null;
    }

    public async Task<List<DecisaoAtual>> ObterPropostasPendentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        var sql = @"SELECT topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version,
                           stream_version, status, pending_proposal_id, title, summary, justification,
                           technical_keys, related_incident_ids, evidence, updated_at
                    FROM decision_current
                    WHERE workspace_id = @ws AND pending_proposal_id IS NOT NULL";
        if (projetoId.HasValue) sql += " AND project_id = @proj";
        sql += " ORDER BY updated_at DESC";

        await using var cmd = new NpgsqlCommand(sql, conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        if (projetoId.HasValue) cmd.Parameters.AddWithValue("proj", projetoId.Value);

        var lista = new List<DecisaoAtual>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            lista.Add(MapearDecisaoAtual(leitor));
        }
        return lista;
    }

    public async Task<List<DecisaoAtual>> ObterDecisoesVigentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        var sql = @"SELECT topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version,
                           stream_version, status, pending_proposal_id, title, summary, justification,
                           technical_keys, related_incident_ids, evidence, updated_at
                    FROM decision_current
                    WHERE workspace_id = @ws AND status = 'active' AND active_record_id IS NOT NULL";
        if (projetoId.HasValue) sql += " AND project_id = @proj";
        sql += " ORDER BY updated_at DESC";

        await using var cmd = new NpgsqlCommand(sql, conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        if (projetoId.HasValue) cmd.Parameters.AddWithValue("proj", projetoId.Value);

        var lista = new List<DecisaoAtual>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            lista.Add(MapearDecisaoAtual(leitor));
        }
        return lista;
    }

    private static DecisaoAtual MapearDecisaoAtual(NpgsqlDataReader leitor)
    {
        var statusStr = leitor.GetString(7);
        Enum.TryParse<StatusDecisao>(statusStr, true, out var status);
        var chavesTecnicasJson = leitor.GetString(12);
        var incidentesJson = leitor.GetString(13);
        var evidenciasJson = leitor.GetString(14);

        return new DecisaoAtual(
            leitor.GetGuid(0),
            leitor.GetGuid(1),
            leitor.GetGuid(2),
            leitor.GetString(3),
            leitor.IsDBNull(4) ? null : leitor.GetGuid(4),
            leitor.IsDBNull(5) ? null : leitor.GetInt32(5),
            leitor.GetInt32(6),
            status,
            leitor.IsDBNull(8) ? null : leitor.GetGuid(8),
            leitor.GetString(9),
            leitor.GetString(10),
            leitor.IsDBNull(11) ? null : leitor.GetString(11),
            JsonSerializer.Deserialize<List<string>>(chavesTecnicasJson) ?? [],
            JsonSerializer.Deserialize<List<Guid>>(incidentesJson) ?? [],
            JsonSerializer.Deserialize<List<Evidencia>>(evidenciasJson) ?? [],
            leitor.GetFieldValue<DateTimeOffset>(15)
        );
    }

    public async Task<List<EventoMemoria>> ObterEventosPorFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, int? naVersao = null, int? antesDaVersao = null, int limite = 50, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        var sql = @"SELECT event_id, workspace_id, project_id, stream_id, stream_version, type,
                           schema_version, recorded_at, author_subject, author_issuer, client_id,
                           payload_id, ref_event_id
                    FROM memory_events
                    WHERE workspace_id = @ws AND stream_id = @stream";
        if (naVersao.HasValue) sql += " AND stream_version <= @atVer";
        if (antesDaVersao.HasValue) sql += " AND stream_version < @beforeVer";
        sql += " ORDER BY stream_version ASC LIMIT @lim";

        await using var cmd = new NpgsqlCommand(sql, conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("stream", fluxoId);
        cmd.Parameters.AddWithValue("lim", limite);
        if (naVersao.HasValue) cmd.Parameters.AddWithValue("atVer", naVersao.Value);
        if (antesDaVersao.HasValue) cmd.Parameters.AddWithValue("beforeVer", antesDaVersao.Value);

        var eventos = new List<EventoMemoria>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            eventos.Add(new EventoMemoria(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetGuid(3),
                leitor.GetInt32(4),
                leitor.GetString(5),
                leitor.GetInt32(6),
                leitor.GetFieldValue<DateTimeOffset>(7),
                leitor.GetString(8),
                leitor.GetString(9),
                leitor.IsDBNull(10) ? null : leitor.GetString(10),
                leitor.IsDBNull(11) ? null : leitor.GetGuid(11),
                leitor.IsDBNull(12) ? null : leitor.GetGuid(12)
            ));
        }
        return eventos;
    }

    public async Task<CargaMemoria?> ObterCargaAsync(Guid espacoTrabalhoId, Guid cargaId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT payload_id, workspace_id, project_id, title, summary, justification,
                     technical_keys, related_incident_ids, evidence, created_at
              FROM memory_payloads
              WHERE workspace_id = @ws AND payload_id = @id", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("id", cargaId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new CargaMemoria(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetString(3),
                leitor.GetString(4),
                leitor.IsDBNull(5) ? null : leitor.GetString(5),
                JsonSerializer.Deserialize<List<string>>(leitor.GetString(6)) ?? [],
                JsonSerializer.Deserialize<List<Guid>>(leitor.GetString(7)) ?? [],
                JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(8)) ?? [],
                leitor.GetFieldValue<DateTimeOffset>(9)
            );
        }
        return null;
    }

    public async Task<ResultadoProporDecisao> ProporDecisaoAsync(ComandoProporDecisao comando, CancellationToken ct = default)
    {
        var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
        {
            comando.EspacoTrabalhoId,
            comando.ProjetoId,
            comando.ChaveTopico,
            comando.VersaoEsperada,
            comando.Titulo,
            comando.Resumo,
            comando.Justificativa,
            comando.ChavesTecnicas,
            comando.IncidentesRelacionadosIds,
            comando.Evidencias
        });

        await using var conexao = await AbrirConexaoComRlsAsync(comando.EspacoTrabalhoId, ct);
        await using var transacao = await conexao.BeginTransactionAsync(ct);

        try
        {
            var reciboExistente = await ObterReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.AutorSujeito, "propose_decision", comando.ChaveIdempotencia, ct);
            if (reciboExistente != null)
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoProporDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            int versaoAtual = 0;
            Guid topicoId = Guid.Empty;
            bool topicoExiste = false;

            await using (var cmdTopico = new NpgsqlCommand(
                "SELECT topic_id, current_version FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND lower(topic_key) = lower(@key) FOR UPDATE",
                conexao, transacao))
            {
                cmdTopico.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdTopico.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdTopico.Parameters.AddWithValue("key", comando.ChaveTopico);

                await using var leitor = await cmdTopico.ExecuteReaderAsync(ct);
                if (await leitor.ReadAsync(ct))
                {
                    topicoId = leitor.GetGuid(0);
                    versaoAtual = leitor.GetInt32(1);
                    topicoExiste = true;
                }
            }

            if (comando.VersaoEsperada != versaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(topicoId != Guid.Empty ? topicoId : (Guid?)null, comando.VersaoEsperada, versaoAtual);
            }

            var novaVersao = versaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;
            if (!topicoExiste)
            {
                topicoId = Guid.NewGuid();
                await using var cmdInsereTopico = new NpgsqlCommand(
                    @"INSERT INTO decision_topics (topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at)
                      VALUES (@id, @ws, @proj, @key, @ver, @now, @now)", conexao, transacao);
                cmdInsereTopico.Parameters.AddWithValue("id", topicoId);
                cmdInsereTopico.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdInsereTopico.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdInsereTopico.Parameters.AddWithValue("key", comando.ChaveTopico);
                cmdInsereTopico.Parameters.AddWithValue("ver", novaVersao);
                cmdInsereTopico.Parameters.AddWithValue("now", agora);
                await cmdInsereTopico.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var cmdAtualizaTopico = new NpgsqlCommand(
                    "UPDATE decision_topics SET current_version = @ver, updated_at = @now WHERE topic_id = @id", conexao, transacao);
                cmdAtualizaTopico.Parameters.AddWithValue("ver", novaVersao);
                cmdAtualizaTopico.Parameters.AddWithValue("now", agora);
                cmdAtualizaTopico.Parameters.AddWithValue("id", topicoId);
                await cmdAtualizaTopico.ExecuteNonQueryAsync(ct);
            }

            var cargaId = Guid.NewGuid();
            await using (var cmdCarga = new NpgsqlCommand(
                @"INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, justification, technical_keys, related_incident_ids, evidence, created_at)
                  VALUES (@id, @ws, @proj, @title, @summary, @just, @keys::jsonb, @incidents::jsonb, @evidence::jsonb, @now)", conexao, transacao))
            {
                cmdCarga.Parameters.AddWithValue("id", cargaId);
                cmdCarga.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdCarga.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdCarga.Parameters.AddWithValue("title", comando.Titulo);
                cmdCarga.Parameters.AddWithValue("summary", comando.Resumo);
                cmdCarga.Parameters.AddWithValue("just", (object?)comando.Justificativa ?? DBNull.Value);
                cmdCarga.Parameters.AddWithValue("keys", JsonSerializer.Serialize(comando.ChavesTecnicas ?? []));
                cmdCarga.Parameters.AddWithValue("incidents", JsonSerializer.Serialize(comando.IncidentesRelacionadosIds ?? []));
                cmdCarga.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(comando.Evidencias));
                cmdCarga.Parameters.AddWithValue("now", agora);
                await cmdCarga.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdEvento = new NpgsqlCommand(
                @"INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
                  VALUES (@id, @ws, @proj, @stream, @ver, 'DecisionProposed', 1, @now, @author, @issuer, @client, @payload)", conexao, transacao))
            {
                cmdEvento.Parameters.AddWithValue("id", Guid.NewGuid());
                cmdEvento.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvento.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdEvento.Parameters.AddWithValue("stream", topicoId);
                cmdEvento.Parameters.AddWithValue("ver", novaVersao);
                cmdEvento.Parameters.AddWithValue("now", agora);
                cmdEvento.Parameters.AddWithValue("author", comando.AutorSujeito);
                cmdEvento.Parameters.AddWithValue("issuer", comando.AutorEmissor);
                cmdEvento.Parameters.AddWithValue("client", (object?)comando.ClienteId ?? DBNull.Value);
                cmdEvento.Parameters.AddWithValue("payload", cargaId);
                await cmdEvento.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdProjecao = new NpgsqlCommand(
                @"INSERT INTO decision_current (topic_id, workspace_id, project_id, topic_key, stream_version, status, pending_proposal_id, title, summary, justification, technical_keys, related_incident_ids, evidence, updated_at)
                  VALUES (@id, @ws, @proj, @key, @ver, 'proposed', @prop, @title, @summary, @just, @keys::jsonb, @incidents::jsonb, @evidence::jsonb, @now)
                  ON CONFLICT (workspace_id, project_id, topic_id) DO UPDATE SET
                    stream_version = @ver,
                    pending_proposal_id = @prop,
                    updated_at = @now", conexao, transacao))
            {
                cmdProjecao.Parameters.AddWithValue("id", topicoId);
                cmdProjecao.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProjecao.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProjecao.Parameters.AddWithValue("key", comando.ChaveTopico);
                cmdProjecao.Parameters.AddWithValue("ver", novaVersao);
                cmdProjecao.Parameters.AddWithValue("prop", cargaId);
                cmdProjecao.Parameters.AddWithValue("title", comando.Titulo);
                cmdProjecao.Parameters.AddWithValue("summary", comando.Resumo);
                cmdProjecao.Parameters.AddWithValue("just", (object?)comando.Justificativa ?? DBNull.Value);
                cmdProjecao.Parameters.AddWithValue("keys", JsonSerializer.Serialize(comando.ChavesTecnicas ?? []));
                cmdProjecao.Parameters.AddWithValue("incidents", JsonSerializer.Serialize(comando.IncidentesRelacionadosIds ?? []));
                cmdProjecao.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(comando.Evidencias));
                cmdProjecao.Parameters.AddWithValue("now", agora);
                await cmdProjecao.ExecuteNonQueryAsync(ct);
            }

            var resultado = new ResultadoProporDecisao(topicoId, cargaId, novaVersao, "proposed");
            await SalvarReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.AutorSujeito, "propose_decision", comando.ChaveIdempotencia, hashArgumentos, JsonSerializer.Serialize(resultado), agora, ct);

            await transacao.CommitAsync(ct);
            return resultado;
        }
        catch
        {
            await transacao.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<ResultadoAprovarDecisao> AprovarDecisaoAsync(ComandoAprovarDecisao comando, CancellationToken ct = default)
    {
        var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
        {
            comando.EspacoTrabalhoId,
            comando.ProjetoId,
            comando.TopicoId,
            comando.PropostaId,
            comando.VersaoEsperada
        });

        await using var conexao = await AbrirConexaoComRlsAsync(comando.EspacoTrabalhoId, ct);
        await using var transacao = await conexao.BeginTransactionAsync(ct);

        try
        {
            var reciboExistente = await ObterReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.RevisorSujeito, "approve_decision", comando.ChaveIdempotencia, ct);
            if (reciboExistente != null)
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoAprovarDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            int versaoAtual = 0;
            string chaveTopico = "";
            await using (var cmdTopico = new NpgsqlCommand(
                "SELECT topic_key, current_version FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id FOR UPDATE",
                conexao, transacao))
            {
                cmdTopico.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdTopico.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdTopico.Parameters.AddWithValue("id", comando.TopicoId);

                await using var leitor = await cmdTopico.ExecuteReaderAsync(ct);
                if (!await leitor.ReadAsync(ct))
                    throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");

                chaveTopico = leitor.GetString(0);
                versaoAtual = leitor.GetInt32(1);
            }

            if (comando.VersaoEsperada != versaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(comando.TopicoId, comando.VersaoEsperada, versaoAtual);
            }

            Guid? propostaPendente = null;
            int versaoRegistroAtual = 0;
            await using (var cmdProj = new NpgsqlCommand(
                "SELECT pending_proposal_id, active_record_version FROM decision_current WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id",
                conexao, transacao))
            {
                cmdProj.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProj.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProj.Parameters.AddWithValue("id", comando.TopicoId);

                await using var leitor = await cmdProj.ExecuteReaderAsync(ct);
                if (await leitor.ReadAsync(ct))
                {
                    propostaPendente = leitor.IsDBNull(0) ? null : leitor.GetGuid(0);
                    versaoRegistroAtual = leitor.IsDBNull(1) ? 0 : leitor.GetInt32(1);
                }
            }

            if (propostaPendente != comando.PropostaId)
                throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para aprovação.");

            CargaMemoria cargaProposta;
            await using (var cmdCarga = new NpgsqlCommand(
                "SELECT title, summary, justification, technical_keys, related_incident_ids, evidence FROM memory_payloads WHERE workspace_id = @ws AND payload_id = @id",
                conexao, transacao))
            {
                cmdCarga.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdCarga.Parameters.AddWithValue("id", comando.PropostaId);

                await using var leitor = await cmdCarga.ExecuteReaderAsync(ct);
                if (!await leitor.ReadAsync(ct))
                    throw new ExcecaoDeNaoEncontrado("Carga da proposta não encontrada.");

                cargaProposta = new CargaMemoria(
                    comando.PropostaId, comando.EspacoTrabalhoId, comando.ProjetoId,
                    leitor.GetString(0), leitor.GetString(1),
                    leitor.IsDBNull(2) ? null : leitor.GetString(2),
                    JsonSerializer.Deserialize<List<string>>(leitor.GetString(3)) ?? [],
                    JsonSerializer.Deserialize<List<Guid>>(leitor.GetString(4)) ?? [],
                    JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(5)) ?? [],
                    DateTimeOffset.UtcNow
                );
            }

            Guid eventoRefId = Guid.Empty;
            await using (var cmdEvRef = new NpgsqlCommand(
                "SELECT event_id FROM memory_events WHERE workspace_id = @ws AND stream_id = @stream AND payload_id = @payload",
                conexao, transacao))
            {
                cmdEvRef.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvRef.Parameters.AddWithValue("stream", comando.TopicoId);
                cmdEvRef.Parameters.AddWithValue("payload", comando.PropostaId);
                var res = await cmdEvRef.ExecuteScalarAsync(ct);
                if (res != null) eventoRefId = (Guid)res;
            }

            var novaVersao = versaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;
            await using (var cmdAtualizaTopico = new NpgsqlCommand(
                "UPDATE decision_topics SET current_version = @ver, updated_at = @now WHERE topic_id = @id", conexao, transacao))
            {
                cmdAtualizaTopico.Parameters.AddWithValue("ver", novaVersao);
                cmdAtualizaTopico.Parameters.AddWithValue("now", agora);
                cmdAtualizaTopico.Parameters.AddWithValue("id", comando.TopicoId);
                await cmdAtualizaTopico.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdEvento = new NpgsqlCommand(
                @"INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, ref_event_id)
                  VALUES (@id, @ws, @proj, @stream, @ver, 'DecisionApproved', 1, @now, @author, @issuer, @ref)", conexao, transacao))
            {
                cmdEvento.Parameters.AddWithValue("id", Guid.NewGuid());
                cmdEvento.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvento.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdEvento.Parameters.AddWithValue("stream", comando.TopicoId);
                cmdEvento.Parameters.AddWithValue("ver", novaVersao);
                cmdEvento.Parameters.AddWithValue("now", agora);
                cmdEvento.Parameters.AddWithValue("author", comando.RevisorSujeito);
                cmdEvento.Parameters.AddWithValue("issuer", comando.RevisorEmissor);
                cmdEvento.Parameters.AddWithValue("ref", (object?)eventoRefId != null && eventoRefId != Guid.Empty ? eventoRefId : DBNull.Value);
                await cmdEvento.ExecuteNonQueryAsync(ct);
            }

            var novaVersaoRegistro = versaoRegistroAtual + 1;
            await using (var cmdProjecao = new NpgsqlCommand(
                @"UPDATE decision_current SET
                    active_record_id = @recId,
                    active_record_version = @recVer,
                    stream_version = @ver,
                    status = 'active',
                    pending_proposal_id = NULL,
                    title = @title,
                    summary = @summary,
                    justification = @just,
                    technical_keys = @keys::jsonb,
                    related_incident_ids = @incidents::jsonb,
                    evidence = @evidence::jsonb,
                    updated_at = @now
                  WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conexao, transacao))
            {
                cmdProjecao.Parameters.AddWithValue("recId", comando.PropostaId);
                cmdProjecao.Parameters.AddWithValue("recVer", novaVersaoRegistro);
                cmdProjecao.Parameters.AddWithValue("ver", novaVersao);
                cmdProjecao.Parameters.AddWithValue("title", cargaProposta.Titulo);
                cmdProjecao.Parameters.AddWithValue("summary", cargaProposta.Resumo);
                cmdProjecao.Parameters.AddWithValue("just", (object?)cargaProposta.Justificativa ?? DBNull.Value);
                cmdProjecao.Parameters.AddWithValue("keys", JsonSerializer.Serialize(cargaProposta.ChavesTecnicas));
                cmdProjecao.Parameters.AddWithValue("incidents", JsonSerializer.Serialize(cargaProposta.IncidentesRelacionadosIds));
                cmdProjecao.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(cargaProposta.Evidencias));
                cmdProjecao.Parameters.AddWithValue("now", agora);
                cmdProjecao.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProjecao.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProjecao.Parameters.AddWithValue("id", comando.TopicoId);
                await cmdProjecao.ExecuteNonQueryAsync(ct);
            }

            var resultado = new ResultadoAprovarDecisao(comando.TopicoId, comando.PropostaId, novaVersao, "active");
            await SalvarReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.RevisorSujeito, "approve_decision", comando.ChaveIdempotencia, hashArgumentos, JsonSerializer.Serialize(resultado), agora, ct);

            await transacao.CommitAsync(ct);
            return resultado;
        }
        catch
        {
            await transacao.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<ResultadoRejeitarDecisao> RejeitarDecisaoAsync(ComandoRejeitarDecisao comando, CancellationToken ct = default)
    {
        var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
        {
            comando.EspacoTrabalhoId,
            comando.ProjetoId,
            comando.TopicoId,
            comando.PropostaId,
            comando.VersaoEsperada,
            comando.Motivo
        });

        await using var conexao = await AbrirConexaoComRlsAsync(comando.EspacoTrabalhoId, ct);
        await using var transacao = await conexao.BeginTransactionAsync(ct);

        try
        {
            var reciboExistente = await ObterReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.RevisorSujeito, "reject_decision", comando.ChaveIdempotencia, ct);
            if (reciboExistente != null)
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoRejeitarDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            int versaoAtual = 0;
            await using (var cmdTopico = new NpgsqlCommand(
                "SELECT current_version FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id FOR UPDATE",
                conexao, transacao))
            {
                cmdTopico.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdTopico.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdTopico.Parameters.AddWithValue("id", comando.TopicoId);

                await using var leitor = await cmdTopico.ExecuteReaderAsync(ct);
                if (!await leitor.ReadAsync(ct))
                    throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");

                versaoAtual = leitor.GetInt32(0);
            }

            if (comando.VersaoEsperada != versaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(comando.TopicoId, comando.VersaoEsperada, versaoAtual);
            }

            Guid? propostaPendente = null;
            Guid? registroAtivoId = null;
            await using (var cmdProj = new NpgsqlCommand(
                "SELECT pending_proposal_id, active_record_id FROM decision_current WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id",
                conexao, transacao))
            {
                cmdProj.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProj.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProj.Parameters.AddWithValue("id", comando.TopicoId);

                await using var leitor = await cmdProj.ExecuteReaderAsync(ct);
                if (await leitor.ReadAsync(ct))
                {
                    propostaPendente = leitor.IsDBNull(0) ? null : leitor.GetGuid(0);
                    registroAtivoId = leitor.IsDBNull(1) ? null : leitor.GetGuid(1);
                }
            }

            if (propostaPendente != comando.PropostaId)
                throw new ExcecaoDeValidacao("Não existe proposta pendente com o ID informado para rejeição.");

            Guid eventoRefId = Guid.Empty;
            await using (var cmdEvRef = new NpgsqlCommand(
                "SELECT event_id FROM memory_events WHERE workspace_id = @ws AND stream_id = @stream AND payload_id = @payload",
                conexao, transacao))
            {
                cmdEvRef.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvRef.Parameters.AddWithValue("stream", comando.TopicoId);
                cmdEvRef.Parameters.AddWithValue("payload", comando.PropostaId);
                var res = await cmdEvRef.ExecuteScalarAsync(ct);
                if (res != null) eventoRefId = (Guid)res;
            }

            var novaVersao = versaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;
            await using (var cmdAtualizaTopico = new NpgsqlCommand(
                "UPDATE decision_topics SET current_version = @ver, updated_at = @now WHERE topic_id = @id", conexao, transacao))
            {
                cmdAtualizaTopico.Parameters.AddWithValue("ver", novaVersao);
                cmdAtualizaTopico.Parameters.AddWithValue("now", agora);
                cmdAtualizaTopico.Parameters.AddWithValue("id", comando.TopicoId);
                await cmdAtualizaTopico.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdEvento = new NpgsqlCommand(
                @"INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, ref_event_id)
                  VALUES (@id, @ws, @proj, @stream, @ver, 'DecisionRejected', 1, @now, @author, @issuer, @ref)", conexao, transacao))
            {
                cmdEvento.Parameters.AddWithValue("id", Guid.NewGuid());
                cmdEvento.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvento.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdEvento.Parameters.AddWithValue("stream", comando.TopicoId);
                cmdEvento.Parameters.AddWithValue("ver", novaVersao);
                cmdEvento.Parameters.AddWithValue("now", agora);
                cmdEvento.Parameters.AddWithValue("author", comando.RevisorSujeito);
                cmdEvento.Parameters.AddWithValue("issuer", comando.RevisorEmissor);
                cmdEvento.Parameters.AddWithValue("ref", (object?)eventoRefId != null && eventoRefId != Guid.Empty ? eventoRefId : DBNull.Value);
                await cmdEvento.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdProjecao = new NpgsqlCommand(
                @"UPDATE decision_current SET
                    stream_version = @ver,
                    pending_proposal_id = NULL,
                    updated_at = @now
                  WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conexao, transacao))
            {
                cmdProjecao.Parameters.AddWithValue("ver", novaVersao);
                cmdProjecao.Parameters.AddWithValue("now", agora);
                cmdProjecao.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProjecao.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProjecao.Parameters.AddWithValue("id", comando.TopicoId);
                await cmdProjecao.ExecuteNonQueryAsync(ct);
            }

            var statusResultado = registroAtivoId.HasValue ? "active" : "superseded";
            var resultado = new ResultadoRejeitarDecisao(comando.TopicoId, novaVersao, statusResultado);
            await SalvarReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.RevisorSujeito, "reject_decision", comando.ChaveIdempotencia, hashArgumentos, JsonSerializer.Serialize(resultado), agora, ct);

            await transacao.CommitAsync(ct);
            return resultado;
        }
        catch
        {
            await transacao.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<ResultadoRestaurarDecisao> RestaurarDecisaoAsync(ComandoRestaurarDecisao comando, CancellationToken ct = default)
    {
        var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
        {
            comando.EspacoTrabalhoId,
            comando.ProjetoId,
            comando.TopicoId,
            comando.VersaoParaRestaurar,
            comando.VersaoEsperada
        });

        await using var conexao = await AbrirConexaoComRlsAsync(comando.EspacoTrabalhoId, ct);
        await using var transacao = await conexao.BeginTransactionAsync(ct);

        try
        {
            var reciboExistente = await ObterReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.RevisorSujeito, "restore_decision", comando.ChaveIdempotencia, ct);
            if (reciboExistente != null)
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoRestaurarDecisao>(reciboExistente.ResultadoSerializado)!;
            }

            int versaoAtual = 0;
            string chaveTopico = "";
            DateTimeOffset criadoEm;
            await using (var cmdTopico = new NpgsqlCommand(
                "SELECT topic_key, current_version, created_at FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id FOR UPDATE",
                conexao, transacao))
            {
                cmdTopico.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdTopico.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdTopico.Parameters.AddWithValue("id", comando.TopicoId);

                await using var leitor = await cmdTopico.ExecuteReaderAsync(ct);
                if (!await leitor.ReadAsync(ct))
                    throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");

                chaveTopico = leitor.GetString(0);
                versaoAtual = leitor.GetInt32(1);
                criadoEm = leitor.GetFieldValue<DateTimeOffset>(2);
            }

            if (comando.VersaoEsperada != versaoAtual)
            {
                throw new ExcecaoDeConflitoDeVersao(comando.TopicoId, comando.VersaoEsperada, versaoAtual);
            }

            var eventos = await ObterEventosPorFluxoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.TopicoId, comando.VersaoParaRestaurar, ct);
            if (eventos.Count == 0)
                throw new ExcecaoDeNaoEncontrado("Nenhum histórico encontrado para a versão solicitada.");

            var cargas = new Dictionary<Guid, CargaMemoria>();
            foreach (var ev in eventos)
            {
                if (ev.CargaId.HasValue && !cargas.ContainsKey(ev.CargaId.Value))
                {
                    var c = await ObterCargaInternaAsync(conexao, transacao, comando.EspacoTrabalhoId, ev.CargaId.Value, ct);
                    if (c != null) cargas[ev.CargaId.Value] = c;
                }
            }

            var snapshot = RedutorDeDecisao.Reconstituir(comando.TopicoId, comando.EspacoTrabalhoId, comando.ProjetoId, chaveTopico, eventos, id => cargas.TryGetValue(id, out var c) ? c : null, comando.VersaoParaRestaurar);

            if (snapshot.RegistroAtivoId == null)
                throw new ExcecaoDeValidacao("A versão indicada não possui decisão ativa para restauração.");

            if (!cargas.TryGetValue(snapshot.RegistroAtivoId.Value, out var cargaOriginal))
                throw new ExcecaoDeNaoEncontrado("Carga histórica da decisão não encontrada.");

            var novaVersao = versaoAtual + 1;
            var agora = DateTimeOffset.UtcNow;
            await using (var cmdAtualizaTopico = new NpgsqlCommand(
                "UPDATE decision_topics SET current_version = @ver, updated_at = @now WHERE topic_id = @id", conexao, transacao))
            {
                cmdAtualizaTopico.Parameters.AddWithValue("ver", novaVersao);
                cmdAtualizaTopico.Parameters.AddWithValue("now", agora);
                cmdAtualizaTopico.Parameters.AddWithValue("id", comando.TopicoId);
                await cmdAtualizaTopico.ExecuteNonQueryAsync(ct);
            }

            var novaCargaId = Guid.NewGuid();
            await using (var cmdCarga = new NpgsqlCommand(
                @"INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, justification, technical_keys, related_incident_ids, evidence, created_at)
                  VALUES (@id, @ws, @proj, @title, @summary, @just, @keys::jsonb, @incidents::jsonb, @evidence::jsonb, @now)", conexao, transacao))
            {
                cmdCarga.Parameters.AddWithValue("id", novaCargaId);
                cmdCarga.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdCarga.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdCarga.Parameters.AddWithValue("title", cargaOriginal.Titulo);
                cmdCarga.Parameters.AddWithValue("summary", cargaOriginal.Resumo);
                cmdCarga.Parameters.AddWithValue("just", (object?)cargaOriginal.Justificativa ?? DBNull.Value);
                cmdCarga.Parameters.AddWithValue("keys", JsonSerializer.Serialize(cargaOriginal.ChavesTecnicas));
                cmdCarga.Parameters.AddWithValue("incidents", JsonSerializer.Serialize(cargaOriginal.IncidentesRelacionadosIds));
                cmdCarga.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(cargaOriginal.Evidencias));
                cmdCarga.Parameters.AddWithValue("now", agora);
                await cmdCarga.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdEvento = new NpgsqlCommand(
                @"INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, payload_id)
                  VALUES (@id, @ws, @proj, @stream, @ver, 'DecisionProposed', 1, @now, @author, @issuer, @payload)", conexao, transacao))
            {
                cmdEvento.Parameters.AddWithValue("id", Guid.NewGuid());
                cmdEvento.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvento.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdEvento.Parameters.AddWithValue("stream", comando.TopicoId);
                cmdEvento.Parameters.AddWithValue("ver", novaVersao);
                cmdEvento.Parameters.AddWithValue("now", agora);
                cmdEvento.Parameters.AddWithValue("author", comando.RevisorSujeito);
                cmdEvento.Parameters.AddWithValue("issuer", comando.RevisorEmissor);
                cmdEvento.Parameters.AddWithValue("payload", novaCargaId);
                await cmdEvento.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdProjecao = new NpgsqlCommand(
                @"UPDATE decision_current SET
                    stream_version = @ver,
                    pending_proposal_id = @prop,
                    updated_at = @now
                  WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conexao, transacao))
            {
                cmdProjecao.Parameters.AddWithValue("ver", novaVersao);
                cmdProjecao.Parameters.AddWithValue("prop", novaCargaId);
                cmdProjecao.Parameters.AddWithValue("now", agora);
                cmdProjecao.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProjecao.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProjecao.Parameters.AddWithValue("id", comando.TopicoId);
                await cmdProjecao.ExecuteNonQueryAsync(ct);
            }

            var resultado = new ResultadoRestaurarDecisao(comando.TopicoId, novaCargaId, novaVersao, "proposed");
            await SalvarReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.RevisorSujeito, "restore_decision", comando.ChaveIdempotencia, hashArgumentos, JsonSerializer.Serialize(resultado), agora, ct);

            await transacao.CommitAsync(ct);
            return resultado;
        }
        catch
        {
            await transacao.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<FluxoIncidente?> ObterIncidentePorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT incident_id, workspace_id, project_id, current_version, created_at, updated_at
              FROM incident_streams
              WHERE workspace_id = @ws AND project_id = @proj AND incident_id = @id", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("proj", projetoId);
        cmd.Parameters.AddWithValue("id", incidenteId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new FluxoIncidente(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetInt32(3),
                leitor.GetFieldValue<DateTimeOffset>(4),
                leitor.GetFieldValue<DateTimeOffset>(5)
            );
        }
        return null;
    }

    public async Task<IncidenteAtual?> ObterIncidenteAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT incident_id, workspace_id, project_id, record_version, stream_version,
                     status, title, summary, occurred_at, technical_keys, evidence, created_at
              FROM incident_current
              WHERE workspace_id = @ws AND project_id = @proj AND incident_id = @id", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("proj", projetoId);
        cmd.Parameters.AddWithValue("id", incidenteId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            var statusStr = leitor.GetString(5);
            Enum.TryParse<StatusIncidente>(statusStr, true, out var status);

            return new IncidenteAtual(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetInt32(3),
                leitor.GetInt32(4),
                status,
                leitor.GetString(6),
                leitor.GetString(7),
                leitor.GetFieldValue<DateTimeOffset>(8),
                JsonSerializer.Deserialize<List<string>>(leitor.GetString(9)) ?? [],
                JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(10)) ?? [],
                leitor.GetFieldValue<DateTimeOffset>(11)
            );
        }
        return null;
    }

    public async Task<List<IncidenteAtual>> ObterIncidentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        var sql = @"SELECT incident_id, workspace_id, project_id, record_version, stream_version,
                           status, title, summary, occurred_at, technical_keys, evidence, created_at
                    FROM incident_current
                    WHERE workspace_id = @ws";
        if (projetoId.HasValue) sql += " AND project_id = @proj";
        sql += " ORDER BY occurred_at DESC";

        await using var cmd = new NpgsqlCommand(sql, conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        if (projetoId.HasValue) cmd.Parameters.AddWithValue("proj", projetoId.Value);

        var lista = new List<IncidenteAtual>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            var statusStr = leitor.GetString(5);
            Enum.TryParse<StatusIncidente>(statusStr, true, out var status);

            lista.Add(new IncidenteAtual(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetInt32(3),
                leitor.GetInt32(4),
                status,
                leitor.GetString(6),
                leitor.GetString(7),
                leitor.GetFieldValue<DateTimeOffset>(8),
                JsonSerializer.Deserialize<List<string>>(leitor.GetString(9)) ?? [],
                JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(10)) ?? [],
                leitor.GetFieldValue<DateTimeOffset>(11)
            ));
        }
        return lista;
    }

    public async Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(ComandoRegistrarIncidente comando, CancellationToken ct = default)
    {
        var hashArgumentos = AjudanteIdempotencia.CalcularImpressaoDigital(new
        {
            comando.EspacoTrabalhoId,
            comando.ProjetoId,
            comando.Titulo,
            comando.Resumo,
            comando.OcorridoEm,
            comando.ChavesTecnicas,
            comando.Evidencias
        });

        await using var conexao = await AbrirConexaoComRlsAsync(comando.EspacoTrabalhoId, ct);
        await using var transacao = await conexao.BeginTransactionAsync(ct);

        try
        {
            var reciboExistente = await ObterReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.AutorSujeito, "log_incident", comando.ChaveIdempotencia, ct);
            if (reciboExistente != null)
            {
                if (reciboExistente.HashArgumentos != hashArgumentos)
                    throw new ExcecaoDeChaveIdempotenciaReutilizada("A chave de idempotência foi reutilizada com argumentos distintos.");

                return JsonSerializer.Deserialize<ResultadoRegistrarIncidente>(reciboExistente.ResultadoSerializado)!;
            }

            var incidenteId = Guid.NewGuid();
            var cargaId = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;
            var ocorridoEm = comando.OcorridoEm ?? agora;

            await using (var cmdFluxo = new NpgsqlCommand(
                @"INSERT INTO incident_streams (incident_id, workspace_id, project_id, current_version, created_at, updated_at)
                  VALUES (@id, @ws, @proj, 1, @now, @now)", conexao, transacao))
            {
                cmdFluxo.Parameters.AddWithValue("id", incidenteId);
                cmdFluxo.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdFluxo.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdFluxo.Parameters.AddWithValue("now", agora);
                await cmdFluxo.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdCarga = new NpgsqlCommand(
                @"INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, technical_keys, related_incident_ids, evidence, created_at)
                  VALUES (@id, @ws, @proj, @title, @summary, @keys::jsonb, '[]'::jsonb, @evidence::jsonb, @now)", conexao, transacao))
            {
                cmdCarga.Parameters.AddWithValue("id", cargaId);
                cmdCarga.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdCarga.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdCarga.Parameters.AddWithValue("title", comando.Titulo);
                cmdCarga.Parameters.AddWithValue("summary", comando.Resumo);
                cmdCarga.Parameters.AddWithValue("keys", JsonSerializer.Serialize(comando.ChavesTecnicas ?? []));
                cmdCarga.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(comando.Evidencias));
                cmdCarga.Parameters.AddWithValue("now", agora);
                await cmdCarga.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdEvento = new NpgsqlCommand(
                @"INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
                  VALUES (@id, @ws, @proj, @stream, 1, 'IncidentReported', 1, @now, @author, @issuer, @client, @payload)", conexao, transacao))
            {
                cmdEvento.Parameters.AddWithValue("id", Guid.NewGuid());
                cmdEvento.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdEvento.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdEvento.Parameters.AddWithValue("stream", incidenteId);
                cmdEvento.Parameters.AddWithValue("now", agora);
                cmdEvento.Parameters.AddWithValue("author", comando.AutorSujeito);
                cmdEvento.Parameters.AddWithValue("issuer", comando.AutorEmissor);
                cmdEvento.Parameters.AddWithValue("client", (object?)comando.ClienteId ?? DBNull.Value);
                cmdEvento.Parameters.AddWithValue("payload", cargaId);
                await cmdEvento.ExecuteNonQueryAsync(ct);
            }

            await using (var cmdProjecao = new NpgsqlCommand(
                @"INSERT INTO incident_current (incident_id, workspace_id, project_id, record_version, stream_version, status, title, summary, occurred_at, technical_keys, evidence, created_at)
                  VALUES (@id, @ws, @proj, 1, 1, 'reported', @title, @summary, @occurred, @keys::jsonb, @evidence::jsonb, @now)", conexao, transacao))
            {
                cmdProjecao.Parameters.AddWithValue("id", incidenteId);
                cmdProjecao.Parameters.AddWithValue("ws", comando.EspacoTrabalhoId);
                cmdProjecao.Parameters.AddWithValue("proj", comando.ProjetoId);
                cmdProjecao.Parameters.AddWithValue("title", comando.Titulo);
                cmdProjecao.Parameters.AddWithValue("summary", comando.Resumo);
                cmdProjecao.Parameters.AddWithValue("occurred", ocorridoEm);
                cmdProjecao.Parameters.AddWithValue("keys", JsonSerializer.Serialize(comando.ChavesTecnicas ?? []));
                cmdProjecao.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(comando.Evidencias));
                cmdProjecao.Parameters.AddWithValue("now", agora);
                await cmdProjecao.ExecuteNonQueryAsync(ct);
            }

            var resultado = new ResultadoRegistrarIncidente(incidenteId, 1, "reported");
            await SalvarReciboComandoInternoAsync(conexao, transacao, comando.EspacoTrabalhoId, comando.AutorSujeito, "log_incident", comando.ChaveIdempotencia, hashArgumentos, JsonSerializer.Serialize(resultado), agora, ct);

            await transacao.CommitAsync(ct);
            return resultado;
        }
        catch
        {
            await transacao.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<ResultadoConsultaContexto> ConsultarContextoAsync(ConsultaContexto consulta, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(consulta.EspacoTrabalhoId, ct);
        await using var transacao = await conexao.BeginTransactionAsync(ct);

        try
        {
            var consultaId = Guid.NewGuid();
            var agora = DateTimeOffset.UtcNow;
            var itens = new List<ItemContexto>();

            if (consulta.TipoRegistro is "decision" or "both")
            {
                await using var cmdDecisoes = new NpgsqlCommand(
                    @"SELECT topic_id, active_record_id, active_record_version, stream_version, status, title, summary, evidence
                      FROM decision_current
                      WHERE workspace_id = @ws AND project_id = @proj AND status = 'active'
                        AND (search_vector @@ websearch_to_tsquery('portuguese', @q)
                             OR search_vector @@ websearch_to_tsquery('simple', @q)
                             OR title ILIKE '%' || @q || '%'
                             OR summary ILIKE '%' || @q || '%')
                      ORDER BY ts_rank_cd(search_vector, websearch_to_tsquery('simple', @q)) DESC
                      LIMIT @lim", conexao, transacao);

                cmdDecisoes.Parameters.AddWithValue("ws", consulta.EspacoTrabalhoId);
                cmdDecisoes.Parameters.AddWithValue("proj", consulta.ProjetoId);
                cmdDecisoes.Parameters.AddWithValue("q", consulta.TermoBusca);
                cmdDecisoes.Parameters.AddWithValue("lim", consulta.Limite);

                await using var leitor = await cmdDecisoes.ExecuteReaderAsync(ct);
                while (await leitor.ReadAsync(ct))
                {
                    itens.Add(new ItemContexto(
                        "decision",
                        leitor.GetGuid(0),
                        null,
                        leitor.GetGuid(1),
                        leitor.GetInt32(2),
                        leitor.GetInt32(3),
                        leitor.GetString(4),
                        leitor.GetString(5),
                        leitor.GetString(6),
                        JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(7)) ?? []
                    ));
                }
            }

            if (consulta.TipoRegistro is "incident" or "both")
            {
                await using var cmdIncidentes = new NpgsqlCommand(
                    @"SELECT incident_id, record_version, stream_version, status, title, summary, evidence
                      FROM incident_current
                      WHERE workspace_id = @ws AND project_id = @proj
                        AND (search_vector @@ websearch_to_tsquery('portuguese', @q)
                             OR search_vector @@ websearch_to_tsquery('simple', @q)
                             OR title ILIKE '%' || @q || '%'
                             OR summary ILIKE '%' || @q || '%')
                      ORDER BY ts_rank_cd(search_vector, websearch_to_tsquery('simple', @q)) DESC
                      LIMIT @lim", conexao, transacao);

                cmdIncidentes.Parameters.AddWithValue("ws", consulta.EspacoTrabalhoId);
                cmdIncidentes.Parameters.AddWithValue("proj", consulta.ProjetoId);
                cmdIncidentes.Parameters.AddWithValue("q", consulta.TermoBusca);
                cmdIncidentes.Parameters.AddWithValue("lim", consulta.Limite);

                await using var leitor = await cmdIncidentes.ExecuteReaderAsync(ct);
                while (await leitor.ReadAsync(ct))
                {
                    itens.Add(new ItemContexto(
                        "incident",
                        null,
                        leitor.GetGuid(0),
                        leitor.GetGuid(0),
                        leitor.GetInt32(1),
                        leitor.GetInt32(2),
                        leitor.GetString(3),
                        leitor.GetString(4),
                        leitor.GetString(5),
                        JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(6)) ?? []
                    ));
                }
            }

            var itensLimitados = itens.Take(consulta.Limite).ToList();

            await using (var cmdRecibo = new NpgsqlCommand(
                @"INSERT INTO context_receipts (query_id, workspace_id, project_id, principal_id, client_id, search_mode, created_at)
                  VALUES (@id, @ws, @proj, @principal, @client, 'textual', @now)", conexao, transacao))
            {
                cmdRecibo.Parameters.AddWithValue("id", consultaId);
                cmdRecibo.Parameters.AddWithValue("ws", consulta.EspacoTrabalhoId);
                cmdRecibo.Parameters.AddWithValue("proj", consulta.ProjetoId);
                cmdRecibo.Parameters.AddWithValue("principal", consulta.PrincipalId);
                cmdRecibo.Parameters.AddWithValue("client", (object?)consulta.ClienteId ?? DBNull.Value);
                cmdRecibo.Parameters.AddWithValue("now", agora);
                await cmdRecibo.ExecuteNonQueryAsync(ct);
            }

            int posicao = 1;
            foreach (var item in itensLimitados)
            {
                await using var cmdItem = new NpgsqlCommand(
                    @"INSERT INTO context_receipt_items (receipt_item_id, query_id, workspace_id, position, record_type, stream_id, record_id, record_version, stream_version, status)
                      VALUES (@id, @query, @ws, @pos, @type, @stream, @recId, @recVer, @streamVer, @status)", conexao, transacao);

                cmdItem.Parameters.AddWithValue("id", Guid.NewGuid());
                cmdItem.Parameters.AddWithValue("query", consultaId);
                cmdItem.Parameters.AddWithValue("ws", consulta.EspacoTrabalhoId);
                cmdItem.Parameters.AddWithValue("pos", posicao++);
                cmdItem.Parameters.AddWithValue("type", item.TipoRegistro);
                cmdItem.Parameters.AddWithValue("stream", item.TopicoId ?? item.IncidenteId!.Value);
                cmdItem.Parameters.AddWithValue("recId", item.RegistroId);
                cmdItem.Parameters.AddWithValue("recVer", item.VersaoRegistro);
                cmdItem.Parameters.AddWithValue("streamVer", item.VersaoFluxo);
                cmdItem.Parameters.AddWithValue("status", item.Situacao);
                await cmdItem.ExecuteNonQueryAsync(ct);
            }

            await transacao.CommitAsync(ct);

            var statusCorrespondencia = itensLimitados.Count > 0 ? "found" : "none";
            return new ResultadoConsultaContexto(consultaId, statusCorrespondencia, "textual", itensLimitados);
        }
        catch
        {
            await transacao.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<ResultadoComprovanteContexto> ObterComprovanteContextoAsync(Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, string principalId, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);

        DateTimeOffset criadoEm;
        string modoBusca;
        string? clienteId;
        string principal;

        await using (var cmd = new NpgsqlCommand(
            "SELECT principal_id, client_id, search_mode, created_at FROM context_receipts WHERE workspace_id = @ws AND project_id = @proj AND query_id = @id", conexao))
        {
            cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmd.Parameters.AddWithValue("proj", projetoId);
            cmd.Parameters.AddWithValue("id", consultaId);

            await using var leitor = await cmd.ExecuteReaderAsync(ct);
            if (!await leitor.ReadAsync(ct))
                throw new ExcecaoDeNaoEncontrado("Comprovante de consulta não encontrado.");

            principal = leitor.GetString(0);
            clienteId = leitor.IsDBNull(1) ? null : leitor.GetString(1);
            modoBusca = leitor.GetString(2);
            criadoEm = leitor.GetFieldValue<DateTimeOffset>(3);
        }

        var itens = new List<ItemReciboConsulta>();
        await using (var cmdItens = new NpgsqlCommand(
            @"SELECT receipt_item_id, query_id, workspace_id, position, record_type, stream_id, record_id, record_version, stream_version, status
              FROM context_receipt_items
              WHERE workspace_id = @ws AND query_id = @id
              ORDER BY position ASC", conexao))
        {
            cmdItens.Parameters.AddWithValue("ws", espacoTrabalhoId);
            cmdItens.Parameters.AddWithValue("id", consultaId);

            await using var leitor = await cmdItens.ExecuteReaderAsync(ct);
            while (await leitor.ReadAsync(ct))
            {
                itens.Add(new ItemReciboConsulta(
                    leitor.GetGuid(0),
                    leitor.GetGuid(1),
                    leitor.GetGuid(2),
                    leitor.GetInt32(3),
                    leitor.GetString(4),
                    leitor.GetGuid(5),
                    leitor.GetGuid(6),
                    leitor.GetInt32(7),
                    leitor.GetInt32(8),
                    leitor.GetString(9)
                ));
            }
        }

        return new ResultadoComprovanteContexto(
            consultaId, espacoTrabalhoId, projetoId, principal, clienteId, modoBusca, criadoEm, itens
        );
    }

    public async Task<ReciboComando?> ObterReciboComandoAsync(Guid espacoTrabalhoId, string principalId, string operacao, string chaveIdempotencia, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(espacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at
              FROM command_receipts
              WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conexao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("prin", principalId);
        cmd.Parameters.AddWithValue("op", operacao);
        cmd.Parameters.AddWithValue("key", chaveIdempotencia);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new ReciboComando(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetString(2),
                leitor.GetString(3),
                leitor.GetString(4),
                leitor.GetString(5),
                leitor.GetString(6),
                leitor.GetFieldValue<DateTimeOffset>(7)
            );
        }
        return null;
    }

    public async Task SalvarReciboComandoAsync(ReciboComando recibo, CancellationToken ct = default)
    {
        await using var conexao = await AbrirConexaoComRlsAsync(recibo.EspacoTrabalhoId, ct);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
              VALUES (@id, @ws, @prin, @op, @key, @hash, @res::jsonb, @now)
              ON CONFLICT (workspace_id, principal_id, operation, idempotency_key) DO NOTHING", conexao);
        cmd.Parameters.AddWithValue("id", recibo.ReciboId);
        cmd.Parameters.AddWithValue("ws", recibo.EspacoTrabalhoId);
        cmd.Parameters.AddWithValue("prin", recibo.PrincipalId);
        cmd.Parameters.AddWithValue("op", recibo.Operacao);
        cmd.Parameters.AddWithValue("key", recibo.ChaveIdempotencia);
        cmd.Parameters.AddWithValue("hash", recibo.HashArgumentos);
        cmd.Parameters.AddWithValue("res", recibo.ResultadoSerializado);
        cmd.Parameters.AddWithValue("now", recibo.CriadoEm);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ReciboComando?> ObterReciboComandoInternoAsync(NpgsqlConnection conexao, NpgsqlTransaction transacao, Guid espacoTrabalhoId, string principalId, string operacao, string chaveIdempotencia, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at
              FROM command_receipts
              WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conexao, transacao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("prin", principalId);
        cmd.Parameters.AddWithValue("op", operacao);
        cmd.Parameters.AddWithValue("key", chaveIdempotencia);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new ReciboComando(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetString(2),
                leitor.GetString(3),
                leitor.GetString(4),
                leitor.GetString(5),
                leitor.GetString(6),
                leitor.GetFieldValue<DateTimeOffset>(7)
            );
        }
        return null;
    }

    private static async Task SalvarReciboComandoInternoAsync(NpgsqlConnection conexao, NpgsqlTransaction transacao, Guid espacoTrabalhoId, string principalId, string operacao, string chaveIdempotencia, string hashArgumentos, string resultadoSerializado, DateTimeOffset agora, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
              VALUES (@id, @ws, @prin, @op, @key, @hash, @res::jsonb, @now)", conexao, transacao);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("prin", principalId);
        cmd.Parameters.AddWithValue("op", operacao);
        cmd.Parameters.AddWithValue("key", chaveIdempotencia);
        cmd.Parameters.AddWithValue("hash", hashArgumentos);
        cmd.Parameters.AddWithValue("res", resultadoSerializado);
        cmd.Parameters.AddWithValue("now", agora);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<EventoMemoria>> ObterEventosPorFluxoInternoAsync(NpgsqlConnection conexao, NpgsqlTransaction transacao, Guid espacoTrabalhoId, Guid fluxoId, int ateVersao, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id, ref_event_id
              FROM memory_events
              WHERE workspace_id = @ws AND stream_id = @stream AND stream_version <= @ver
              ORDER BY stream_version ASC", conexao, transacao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("stream", fluxoId);
        cmd.Parameters.AddWithValue("ver", ateVersao);

        var eventos = new List<EventoMemoria>();
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            eventos.Add(new EventoMemoria(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetGuid(3),
                leitor.GetInt32(4),
                leitor.GetString(5),
                leitor.GetInt32(6),
                leitor.GetFieldValue<DateTimeOffset>(7),
                leitor.GetString(8),
                leitor.GetString(9),
                leitor.IsDBNull(10) ? null : leitor.GetString(10),
                leitor.IsDBNull(11) ? null : leitor.GetGuid(11),
                leitor.IsDBNull(12) ? null : leitor.GetGuid(12)
            ));
        }
        return eventos;
    }

    private static async Task<CargaMemoria?> ObterCargaInternaAsync(NpgsqlConnection conexao, NpgsqlTransaction transacao, Guid espacoTrabalhoId, Guid cargaId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            @"SELECT payload_id, workspace_id, project_id, title, summary, justification, technical_keys, related_incident_ids, evidence, created_at
              FROM memory_payloads
              WHERE workspace_id = @ws AND payload_id = @id", conexao, transacao);
        cmd.Parameters.AddWithValue("ws", espacoTrabalhoId);
        cmd.Parameters.AddWithValue("id", cargaId);

        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        if (await leitor.ReadAsync(ct))
        {
            return new CargaMemoria(
                leitor.GetGuid(0),
                leitor.GetGuid(1),
                leitor.GetGuid(2),
                leitor.GetString(3),
                leitor.GetString(4),
                leitor.IsDBNull(5) ? null : leitor.GetString(5),
                JsonSerializer.Deserialize<List<string>>(leitor.GetString(6)) ?? [],
                JsonSerializer.Deserialize<List<Guid>>(leitor.GetString(7)) ?? [],
                JsonSerializer.Deserialize<List<Evidencia>>(leitor.GetString(8)) ?? [],
                leitor.GetFieldValue<DateTimeOffset>(9)
            );
        }
        return null;
    }

    public async Task ReconstruirProjecoesAsync(Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        await using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(ct);

        var sqlTopicos = "SELECT topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at FROM decision_topics";
        if (espacoTrabalhoId.HasValue) sqlTopicos += $" WHERE workspace_id = '{espacoTrabalhoId.Value}'";

        var topicos = new List<TopicoDecisao>();
        await using (var cmd = new NpgsqlCommand(sqlTopicos, conexao))
        {
            await using var leitor = await cmd.ExecuteReaderAsync(ct);
            while (await leitor.ReadAsync(ct))
            {
                topicos.Add(new TopicoDecisao(
                    leitor.GetGuid(0),
                    leitor.GetGuid(1),
                    leitor.GetGuid(2),
                    leitor.GetString(3),
                    leitor.GetInt32(4),
                    leitor.GetFieldValue<DateTimeOffset>(5),
                    leitor.GetFieldValue<DateTimeOffset>(6)
                ));
            }
        }

        foreach (var t in topicos)
        {
            var eventos = await ObterEventosPorFluxoAsync(t.EspacoTrabalhoId, t.TopicoId, limite: 1000, ct: ct);
            var cargas = new Dictionary<Guid, CargaMemoria>();
            foreach (var ev in eventos)
            {
                if (ev.CargaId.HasValue && !cargas.ContainsKey(ev.CargaId.Value))
                {
                    var c = await ObterCargaAsync(t.EspacoTrabalhoId, ev.CargaId.Value, ct);
                    if (c != null) cargas[ev.CargaId.Value] = c;
                }
            }

            var reconstituido = RedutorDeDecisao.Reconstituir(t.TopicoId, t.EspacoTrabalhoId, t.ProjetoId, t.ChaveTopico, eventos, id => cargas.TryGetValue(id, out var c) ? c : null);

            CargaMemoria? carga = null;
            if (reconstituido.RegistroAtivoId.HasValue)
                cargas.TryGetValue(reconstituido.RegistroAtivoId.Value, out carga);
            else if (reconstituido.PropostaPendenteId.HasValue)
                cargas.TryGetValue(reconstituido.PropostaPendenteId.Value, out carga);

            await using var cmdProj = new NpgsqlCommand(
                @"INSERT INTO decision_current (topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version, stream_version, status, pending_proposal_id, title, summary, justification, technical_keys, related_incident_ids, evidence, updated_at)
                  VALUES (@id, @ws, @proj, @key, @actId, @actVer, @streamVer, @status, @propId, @title, @summary, @just, @keys::jsonb, @incidents::jsonb, @evidence::jsonb, @updated)
                  ON CONFLICT (workspace_id, project_id, topic_id) DO UPDATE SET
                    active_record_id = @actId,
                    active_record_version = @actVer,
                    stream_version = @streamVer,
                    status = @status,
                    pending_proposal_id = @propId,
                    title = @title,
                    summary = @summary,
                    justification = @just,
                    technical_keys = @keys::jsonb,
                    related_incident_ids = @incidents::jsonb,
                    evidence = @evidence::jsonb,
                    updated_at = @updated", conexao);

            cmdProj.Parameters.AddWithValue("id", t.TopicoId);
            cmdProj.Parameters.AddWithValue("ws", t.EspacoTrabalhoId);
            cmdProj.Parameters.AddWithValue("proj", t.ProjetoId);
            cmdProj.Parameters.AddWithValue("key", t.ChaveTopico);
            cmdProj.Parameters.AddWithValue("actId", (object?)reconstituido.RegistroAtivoId ?? DBNull.Value);
            cmdProj.Parameters.AddWithValue("actVer", (object?)reconstituido.VersaoRegistroAtivo ?? DBNull.Value);
            cmdProj.Parameters.AddWithValue("streamVer", reconstituido.VersaoFluxo);
            cmdProj.Parameters.AddWithValue("status", reconstituido.Situacao.ToString().ToLowerInvariant());
            cmdProj.Parameters.AddWithValue("propId", (object?)reconstituido.PropostaPendenteId ?? DBNull.Value);
            cmdProj.Parameters.AddWithValue("title", carga?.Titulo ?? string.Empty);
            cmdProj.Parameters.AddWithValue("summary", carga?.Resumo ?? string.Empty);
            cmdProj.Parameters.AddWithValue("just", (object?)carga?.Justificativa ?? DBNull.Value);
            cmdProj.Parameters.AddWithValue("keys", JsonSerializer.Serialize(carga?.ChavesTecnicas ?? []));
            cmdProj.Parameters.AddWithValue("incidents", JsonSerializer.Serialize(carga?.IncidentesRelacionadosIds ?? []));
            cmdProj.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(carga?.Evidencias ?? []));
            cmdProj.Parameters.AddWithValue("updated", t.AtualizadoEm);
            await cmdProj.ExecuteNonQueryAsync(ct);
        }
    }
}
