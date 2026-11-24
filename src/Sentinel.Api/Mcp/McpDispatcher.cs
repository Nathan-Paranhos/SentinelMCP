using System.Text.Json;
using Sentinel.Api.Auth;
using Sentinel.Core.Comandos;
using Sentinel.Core.Consultas;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Persistence.Contratos;

namespace Sentinel.Api.Mcp;

public sealed class McpDispatcher
{
    private readonly IServicoIdentidade _servicoIdentidade;
    private readonly IRepositorioEspacoTrabalho _repositorioEspacoTrabalho;
    private readonly IRepositorioDecisao _repositorioDecisao;
    private readonly IRepositorioIncidente _repositorioIncidente;
    private readonly IRepositorioContexto _repositorioContexto;

    public McpDispatcher(
        IServicoIdentidade servicoIdentidade,
        IRepositorioEspacoTrabalho repositorioEspacoTrabalho,
        IRepositorioDecisao repositorioDecisao,
        IRepositorioIncidente repositorioIncidente,
        IRepositorioContexto repositorioContexto)
    {
        _servicoIdentidade = servicoIdentidade;
        _repositorioEspacoTrabalho = repositorioEspacoTrabalho;
        _repositorioDecisao = repositorioDecisao;
        _repositorioIncidente = repositorioIncidente;
        _repositorioContexto = repositorioContexto;
    }

    public async Task<McpToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        HttpContext contextoHttp,
        CancellationToken ct = default)
    {
        try
        {
            return toolName switch
            {
                "propose_decision" => await TratarProporDecisaoAsync(arguments, contextoHttp, ct),
                "log_incident" => await TratarRegistrarIncidenteAsync(arguments, contextoHttp, ct),
                "query_context" => await TratarConsultarContextoAsync(arguments, contextoHttp, ct),
                "get_timeline" => await TratarObterLinhaDoTempoAsync(arguments, contextoHttp, ct),
                "get_context_receipt" => await TratarObterComprovanteContextoAsync(arguments, contextoHttp, ct),
                _ => McpToolResult.Error("NOT_FOUND", $"A ferramenta '{toolName}' não foi reconhecida.")
            };
        }
        catch (ExcecaoDeConflitoDeVersao ex)
        {
            return McpToolResult.Error(ex.Codigo, ex.Message, new
            {
                topicoId = ex.TopicoId,
                versaoEsperada = ex.VersaoEsperada,
                versaoAtual = ex.VersaoAtual
            });
        }
        catch (ExcecaoDeDominio ex)
        {
            return McpToolResult.Error(ex.Codigo, ex.Message);
        }
        catch (Exception ex)
        {
            return McpToolResult.Error("INTERNAL_ERROR", ex.Message);
        }
    }

    private async Task<McpToolResult> TratarProporDecisaoAsync(JsonElement args, HttpContext contextoHttp, CancellationToken ct)
    {
        var espacoTrabalhoId = args.GetProperty("workspaceId").GetGuid();
        var projetoId = args.GetProperty("projectId").GetGuid();

        await _servicoIdentidade.AutenticarEAutorizarAsync(
            contextoHttp, espacoTrabalhoId, "write:decision", PapelUsuario.Contributor, ct);
        var identidade = _servicoIdentidade.ResolverIdentidade(contextoHttp);

        var projeto = await _repositorioEspacoTrabalho.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct);
        if (projeto == null)
            throw new ExcecaoDeNaoEncontrado($"Projeto '{projetoId}' não encontrado no espaço de trabalho.");

        var chaveTopico = args.GetProperty("topicKey").GetString() ?? string.Empty;
        var versaoEsperada = args.GetProperty("expectedVersion").GetInt32();
        var titulo = args.GetProperty("title").GetString() ?? string.Empty;
        var resumo = args.GetProperty("summary").GetString() ?? string.Empty;
        var justificativa = args.TryGetProperty("rationale", out var ratElem) ? ratElem.GetString() : null;

        List<string>? chavesTecnicas = null;
        if (args.TryGetProperty("technicalKeys", out var tkElem) && tkElem.ValueKind == JsonValueKind.Array)
        {
            chavesTecnicas = tkElem.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        List<Guid>? incidentesRelacionados = null;
        if (args.TryGetProperty("relatedIncidentIds", out var incElem) && incElem.ValueKind == JsonValueKind.Array)
        {
            incidentesRelacionados = incElem.EnumerateArray().Select(x => x.GetGuid()).ToList();
        }

        var evidencias = new List<Evidencia>();
        if (args.TryGetProperty("evidence", out var evElem) && evElem.ValueKind == JsonValueKind.Array)
        {
            evidencias = evElem.EnumerateArray().Select(e => new Evidencia(
                e.GetProperty("type").GetString() ?? string.Empty,
                e.GetProperty("label").GetString() ?? string.Empty,
                e.GetProperty("url").GetString() ?? string.Empty
            )).ToList();
        }

        var chaveIdempotencia = args.GetProperty("idempotencyKey").GetString() ?? string.Empty;

        var comando = new ComandoProporDecisao(
            EspacoTrabalhoId: espacoTrabalhoId,
            ProjetoId: projetoId,
            ChaveTopico: chaveTopico,
            VersaoEsperada: versaoEsperada,
            Titulo: titulo,
            Resumo: resumo,
            Justificativa: justificativa,
            ChavesTecnicas: chavesTecnicas,
            IncidentesRelacionadosIds: incidentesRelacionados,
            Evidencias: evidencias,
            ChaveIdempotencia: chaveIdempotencia,
            AutorSujeito: identidade.Sujeito,
            AutorEmissor: identidade.Emissor,
            ClienteId: identidade.ClienteId
        );

        var resultado = await _repositorioDecisao.ProporDecisaoAsync(comando, ct);
        return McpToolResult.Success(new
        {
            topicId = resultado.TopicoId,
            proposalId = resultado.PropostaId,
            streamVersion = resultado.VersaoFluxo,
            status = resultado.Situacao
        }, $"Proposta de decisão criada para o tópico '{chaveTopico}'. Proposta ID: {resultado.PropostaId}, versão do fluxo: {resultado.VersaoFluxo}.");
    }

    private async Task<McpToolResult> TratarRegistrarIncidenteAsync(JsonElement args, HttpContext contextoHttp, CancellationToken ct)
    {
        var espacoTrabalhoId = args.GetProperty("workspaceId").GetGuid();
        var projetoId = args.GetProperty("projectId").GetGuid();

        await _servicoIdentidade.AutenticarEAutorizarAsync(
            contextoHttp, espacoTrabalhoId, "write:incident", PapelUsuario.Contributor, ct);
        var identidade = _servicoIdentidade.ResolverIdentidade(contextoHttp);

        var projeto = await _repositorioEspacoTrabalho.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct);
        if (projeto == null)
            throw new ExcecaoDeNaoEncontrado($"Projeto '{projetoId}' não encontrado no espaço de trabalho.");

        var titulo = args.GetProperty("title").GetString() ?? string.Empty;
        var resumo = args.GetProperty("summary").GetString() ?? string.Empty;
        DateTimeOffset? ocorridoEm = args.TryGetProperty("occurredAt", out var occElem) && occElem.ValueKind == JsonValueKind.String
            ? occElem.GetDateTimeOffset()
            : null;

        List<string>? chavesTecnicas = null;
        if (args.TryGetProperty("technicalKeys", out var tkElem) && tkElem.ValueKind == JsonValueKind.Array)
        {
            chavesTecnicas = tkElem.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        var evidencias = new List<Evidencia>();
        if (args.TryGetProperty("evidence", out var evElem) && evElem.ValueKind == JsonValueKind.Array)
        {
            evidencias = evElem.EnumerateArray().Select(e => new Evidencia(
                e.GetProperty("type").GetString() ?? string.Empty,
                e.GetProperty("label").GetString() ?? string.Empty,
                e.GetProperty("url").GetString() ?? string.Empty
            )).ToList();
        }

        var chaveIdempotencia = args.GetProperty("idempotencyKey").GetString() ?? string.Empty;

        var comando = new ComandoRegistrarIncidente(
            EspacoTrabalhoId: espacoTrabalhoId,
            ProjetoId: projetoId,
            Titulo: titulo,
            Resumo: resumo,
            OcorridoEm: ocorridoEm,
            ChavesTecnicas: chavesTecnicas,
            Evidencias: evidencias,
            ChaveIdempotencia: chaveIdempotencia,
            AutorSujeito: identidade.Sujeito,
            AutorEmissor: identidade.Emissor,
            ClienteId: identidade.ClienteId
        );

        var resultado = await _repositorioIncidente.RegistrarIncidenteAsync(comando, ct);
        return McpToolResult.Success(new
        {
            incidentId = resultado.IncidenteId,
            streamVersion = resultado.VersaoFluxo,
            status = resultado.Situacao
        }, $"Incidente registrado com sucesso com o ID {resultado.IncidenteId}, versão {resultado.VersaoFluxo}.");
    }

    private async Task<McpToolResult> TratarConsultarContextoAsync(JsonElement args, HttpContext contextoHttp, CancellationToken ct)
    {
        var espacoTrabalhoId = args.GetProperty("workspaceId").GetGuid();
        var projetoId = args.GetProperty("projectId").GetGuid();

        await _servicoIdentidade.AutenticarEAutorizarAsync(
            contextoHttp, espacoTrabalhoId, "read:memory", PapelUsuario.Reader, ct);
        var identidade = _servicoIdentidade.ResolverIdentidade(contextoHttp);

        var projeto = await _repositorioEspacoTrabalho.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct);
        if (projeto == null)
            throw new ExcecaoDeNaoEncontrado($"Projeto '{projetoId}' não encontrado no espaço de trabalho.");

        var termoBusca = args.GetProperty("query").GetString() ?? string.Empty;
        var tipoRegistro = args.TryGetProperty("kind", out var kindElem) ? (kindElem.GetString() ?? "decision") : "decision";
        var limite = args.TryGetProperty("limit", out var limElem) ? limElem.GetInt32() : 10;

        var consulta = new ConsultaContexto(
            EspacoTrabalhoId: espacoTrabalhoId,
            ProjetoId: projetoId,
            TermoBusca: termoBusca,
            TipoRegistro: tipoRegistro,
            Limite: limite,
            PrincipalId: identidade.Sujeito,
            ClienteId: identidade.ClienteId
        );

        var resultado = await _repositorioContexto.ConsultarContextoAsync(consulta, ct);

        var resumoTexto = resultado.Itens.Count == 0
            ? $"Nenhum registro de {tipoRegistro} encontrado para a busca '{termoBusca}'."
            : $"Encontrado(s) {resultado.Itens.Count} registro(s) de {tipoRegistro}. Comprovante da consulta: {resultado.ConsultaId}.";

        var payloadResultado = new
        {
            queryId = resultado.ConsultaId,
            matchStatus = resultado.StatusCorrespondencia,
            searchMode = resultado.ModoBusca,
            items = resultado.Itens.Select(i => new
            {
                recordType = i.TipoRegistro,
                topicId = i.TopicoId,
                incidentId = i.IncidenteId,
                recordId = i.RegistroId,
                recordVersion = i.VersaoRegistro,
                streamVersion = i.VersaoFluxo,
                status = i.Situacao,
                title = i.Titulo,
                summary = i.Resumo,
                evidence = i.Evidencias
            }).ToList()
        };

        return McpToolResult.Success(payloadResultado, resumoTexto);
    }

    private async Task<McpToolResult> TratarObterLinhaDoTempoAsync(JsonElement args, HttpContext contextoHttp, CancellationToken ct)
    {
        var espacoTrabalhoId = args.GetProperty("workspaceId").GetGuid();
        var projetoId = args.GetProperty("projectId").GetGuid();

        await _servicoIdentidade.AutenticarEAutorizarAsync(
            contextoHttp, espacoTrabalhoId, "read:memory", PapelUsuario.Reader, ct);

        var projeto = await _repositorioEspacoTrabalho.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct);
        if (projeto == null)
            throw new ExcecaoDeNaoEncontrado($"Projeto '{projetoId}' não encontrado no espaço de trabalho.");

        var tipo = args.GetProperty("kind").GetString() ?? "decision";
        var fluxoId = args.GetProperty("streamId").GetGuid();
        int? naVersao = args.TryGetProperty("atVersion", out var atElem) && atElem.ValueKind == JsonValueKind.Number ? atElem.GetInt32() : null;
        int? antesDaVersao = args.TryGetProperty("beforeVersion", out var bElem) && bElem.ValueKind == JsonValueKind.Number ? bElem.GetInt32() : null;
        int limite = args.TryGetProperty("limit", out var lElem) && lElem.ValueKind == JsonValueKind.Number ? lElem.GetInt32() : 20;

        if (string.Equals(tipo, "decision", StringComparison.OrdinalIgnoreCase))
        {
            var topico = await _repositorioDecisao.ObterTopicoPorIdAsync(espacoTrabalhoId, projetoId, fluxoId, ct);
            if (topico == null)
                throw new ExcecaoDeNaoEncontrado($"Tópico de decisão '{fluxoId}' não encontrado no espaço de trabalho.");

            var eventos = await _repositorioDecisao.ObterEventosPorFluxoAsync(espacoTrabalhoId, fluxoId, naVersao, antesDaVersao, limite, ct);
            var reconstituido = RedutorDeDecisao.Reconstituir(
                topico.TopicoId, espacoTrabalhoId, projetoId, topico.ChaveTopico, eventos,
                pid => _repositorioDecisao.ObterCargaAsync(espacoTrabalhoId, pid, ct).GetAwaiter().GetResult(),
                naVersao
            );

            var itensEventos = eventos.Select(e => new
            {
                eventId = e.EventoId,
                streamVersion = e.VersaoFluxo,
                type = e.Tipo,
                recordedAt = e.RegistradoEm,
                authorSubject = e.AutorSujeito,
                clientId = e.ClienteId,
                payloadId = e.CargaId
            }).ToList();

            var versaoAlvo = naVersao ?? topico.VersaoAtual;
            var resultado = new
            {
                streamId = fluxoId,
                currentStreamVersion = topico.VersaoAtual,
                targetVersion = versaoAlvo,
                reconstitutedState = new
                {
                    topicId = reconstituido.TopicoId,
                    workspaceId = reconstituido.EspacoTrabalhoId,
                    projectId = reconstituido.ProjetoId,
                    topicKey = reconstituido.ChaveTopico,
                    streamVersion = reconstituido.VersaoFluxo,
                    activeRecordId = reconstituido.RegistroAtivoId,
                    activeRecordVersion = reconstituido.VersaoRegistroAtivo,
                    status = reconstituido.Situacao.ToString().ToLowerInvariant(),
                    pendingProposalId = reconstituido.PropostaPendenteId,
                    title = reconstituido.Titulo,
                    summary = reconstituido.Resumo,
                    justification = reconstituido.Justificativa,
                    technicalKeys = reconstituido.ChavesTecnicas,
                    relatedIncidentIds = reconstituido.IncidentesRelacionadosIds,
                    evidence = reconstituido.Evidencias
                },
                events = itensEventos,
                nextBeforeVersion = eventos.Count == limite ? eventos.Last().VersaoFluxo : (int?)null
            };

            return McpToolResult.Success(resultado, $"Linha do tempo da decisão '{topico.ChaveTopico}' na versão {versaoAlvo}. Situação: {reconstituido.Situacao}.");
        }
        else if (string.Equals(tipo, "incident", StringComparison.OrdinalIgnoreCase))
        {
            var incidente = await _repositorioIncidente.ObterIncidentePorIdAsync(espacoTrabalhoId, projetoId, fluxoId, ct);
            if (incidente == null)
                throw new ExcecaoDeNaoEncontrado($"Fluxo de incidente '{fluxoId}' não encontrado no espaço de trabalho.");

            var eventos = await _repositorioIncidente.ObterEventosPorFluxoAsync(espacoTrabalhoId, fluxoId, naVersao, antesDaVersao, limite, ct);
            var reconstituido = RedutorDeIncidente.Reconstituir(
                incidente.IncidenteId, espacoTrabalhoId, projetoId, eventos,
                pid => _repositorioIncidente.ObterCargaAsync(espacoTrabalhoId, pid, ct).GetAwaiter().GetResult(),
                naVersao
            );

            var itensEventos = eventos.Select(e => new
            {
                eventId = e.EventoId,
                streamVersion = e.VersaoFluxo,
                type = e.Tipo,
                recordedAt = e.RegistradoEm,
                authorSubject = e.AutorSujeito,
                clientId = e.ClienteId,
                payloadId = e.CargaId
            }).ToList();

            var versaoAlvo = naVersao ?? incidente.VersaoAtual;
            var resultado = new
            {
                streamId = fluxoId,
                currentStreamVersion = incidente.VersaoAtual,
                targetVersion = versaoAlvo,
                reconstitutedState = new
                {
                    incidentId = reconstituido.IncidenteId,
                    workspaceId = reconstituido.EspacoTrabalhoId,
                    projectId = reconstituido.ProjetoId,
                    streamVersion = reconstituido.VersaoFluxo,
                    recordVersion = reconstituido.VersaoRegistro,
                    status = reconstituido.Situacao.ToString().ToLowerInvariant(),
                    title = reconstituido.Titulo,
                    summary = reconstituido.Resumo,
                    occurredAt = reconstituido.OcorridoEm,
                    technicalKeys = reconstituido.ChavesTecnicas,
                    evidence = reconstituido.Evidencias
                },
                events = itensEventos,
                nextBeforeVersion = eventos.Count == limite ? eventos.Last().VersaoFluxo : (int?)null
            };

            return McpToolResult.Success(resultado, $"Linha do tempo do incidente '{incidente.IncidenteId}'. Situação: {reconstituido.Situacao}.");
        }

        throw new ExcecaoDeValidacao($"Tipo '{tipo}' não suportado. Esperado 'decision' ou 'incident'.");
    }

    private async Task<McpToolResult> TratarObterComprovanteContextoAsync(JsonElement args, HttpContext contextoHttp, CancellationToken ct)
    {
        var espacoTrabalhoId = args.GetProperty("workspaceId").GetGuid();
        var projetoId = args.GetProperty("projectId").GetGuid();
        var consultaId = args.GetProperty("queryId").GetGuid();

        await _servicoIdentidade.AutenticarEAutorizarAsync(
            contextoHttp, espacoTrabalhoId, "read:memory", PapelUsuario.Reader, ct);
        var identidade = _servicoIdentidade.ResolverIdentidade(contextoHttp);

        var projeto = await _repositorioEspacoTrabalho.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct);
        if (projeto == null)
            throw new ExcecaoDeNaoEncontrado($"Projeto '{projetoId}' não encontrado no espaço de trabalho.");

        var comprovante = await _repositorioContexto.ObterComprovanteContextoAsync(espacoTrabalhoId, projetoId, consultaId, identidade.Sujeito, ct);

        var payloadResultado = new
        {
            queryId = comprovante.ConsultaId,
            workspaceId = comprovante.EspacoTrabalhoId,
            projectId = comprovante.ProjetoId,
            principalId = comprovante.PrincipalId,
            clientId = comprovante.ClienteId,
            searchMode = comprovante.ModoBusca,
            createdAt = comprovante.CriadoEm,
            items = comprovante.Itens.Select(i => new
            {
                receiptItemId = i.ItemReciboId,
                queryId = i.ConsultaId,
                workspaceId = i.EspacoTrabalhoId,
                position = i.Posicao,
                recordType = i.TipoRegistro,
                streamId = i.FluxoId,
                recordId = i.RegistroId,
                recordVersion = i.VersaoRegistro,
                streamVersion = i.VersaoFluxo,
                status = i.Situacao
            }).ToList()
        };

        return McpToolResult.Success(payloadResultado, $"Comprovante {comprovante.ConsultaId} contém {comprovante.Itens.Count} referência(s) verificada(s).");
    }
}
