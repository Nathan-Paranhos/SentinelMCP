using System.Globalization;
using System.Text.Json;
using Sentinel.Api.Auth;
using Sentinel.Api.Operacao;
using Sentinel.Core.CasosDeUso;
using Sentinel.Core.Consultas;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;

namespace Sentinel.Api.Mcp;

// Despachante de ferramentas MCP
public sealed class McpDispatcher
{
    private static readonly HashSet<string> Ferramentas =
        ["propose_decision", "log_incident", "query_context", "get_timeline", "get_context_receipt"];

    private readonly ServicoDeMemoria _servico;
    private readonly ILogger<McpDispatcher> _log;

    public McpDispatcher(ServicoDeMemoria servico, ILogger<McpDispatcher> log)
    {
        _servico = servico;
        _log = log;
    }

    public static bool Existe(string nome) => Ferramentas.Contains(nome);

    public async Task<McpToolResult> CallToolAsync(string nome, JsonElement argumentos, IdentidadeAgente identidade, string idCorrelacao, CancellationToken ct = default)
    {
        using var atividade = SentinelDiagnostics.ActivitySource.StartActivity($"mcp.tool.{nome}");
        atividade?.SetTag("mcp.tool.name", nome);
        atividade?.SetTag("mcp.actor.subject", identidade.Ator.Sujeito);
        atividade?.SetTag("mcp.actor.client_id", identidade.Ator.ClienteId);

        try
        {
            if (argumentos.ValueKind != JsonValueKind.Object)
                throw new ExcecaoDeValidacao("'arguments' deve ser um objeto JSON.");

            var args = new LeitorDeArgumentos(argumentos);
            var resultado = nome switch
            {
                "propose_decision" => await ProporDecisaoAsync(args, identidade, ct),
                "log_incident" => await RegistrarIncidenteAsync(args, identidade, ct),
                "query_context" => await ConsultarContextoAsync(args, identidade, ct),
                "get_timeline" => await ObterLinhaDoTempoAsync(args, identidade, ct),
                "get_context_receipt" => await ObterComprovanteAsync(args, identidade, ct),
                _ => throw new ExcecaoDeNaoEncontrado($"Ferramenta '{nome}' não reconhecida.")
            };

            SentinelDiagnostics.McpToolExecutions.Add(1,
                new KeyValuePair<string, object?>("tool", nome),
                new KeyValuePair<string, object?>("status", resultado.IsError ? "error" : "success"));

            _log.LogInformation("Tool MCP {Ferramenta} executada por {Sujeito} ({Cliente})", nome, identidade.Ator.Sujeito, identidade.Ator.ClienteId);
            return resultado;
        }
        catch (ExcecaoDeConflitoDeVersao ex)
        {
            SentinelDiagnostics.McpToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", nome),
                new KeyValuePair<string, object?>("code", ex.Codigo));
            return McpToolResult.Error(ex.Codigo, ex.Message, new
            {
                topicId = ex.TopicoId,
                expectedVersion = ex.VersaoEsperada,
                currentVersion = ex.VersaoAtual
            });
        }
        catch (ExcecaoDePropostaPendente ex)
        {
            SentinelDiagnostics.McpToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", nome),
                new KeyValuePair<string, object?>("code", ex.Codigo));
            return McpToolResult.Error(ex.Codigo, ex.Message, new { topicId = ex.TopicoId, pendingProposalId = ex.PropostaPendenteId });
        }
        catch (ExcecaoDeDominio ex)
        {
            SentinelDiagnostics.McpToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", nome),
                new KeyValuePair<string, object?>("code", ex.Codigo));
            _log.LogInformation("Tool MCP {Ferramenta} recusada para {Sujeito}: {Codigo}", nome, identidade.Ator.Sujeito, ex.Codigo);
            return McpToolResult.Error(ex.Codigo, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SentinelDiagnostics.McpToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", nome),
                new KeyValuePair<string, object?>("code", "INTERNAL_ERROR"));
            // Registra erro interno com ID de correlação
            _log.LogError(ex, "Falha inesperada na tool MCP {Ferramenta} (correlação {Correlacao})", nome, idCorrelacao);
            return McpToolResult.Error("INTERNAL_ERROR", $"Erro interno. ID de correlação: {idCorrelacao}.");
        }
    }

    private static object Evidencias(IEnumerable<Evidencia> evidencias) =>
        evidencias.Select(e => new { type = e.Tipo, label = e.Rotulo, url = e.Url }).ToList();

    private async Task<McpToolResult> ProporDecisaoAsync(LeitorDeArgumentos a, IdentidadeAgente identidade, CancellationToken ct)
    {
        identidade.ExigirEscopo(Escopos.EscreverDecisao);
        var entrada = new EntradaProporDecisao(
            a.Guid("workspaceId"), a.Guid("projectId"), a.Texto("topicKey"), a.Inteiro("expectedVersion"),
            a.Texto("title"), a.Texto("summary"), a.TextoOpcional("rationale"),
            a.ListaTextos("technicalKeys"), a.ListaGuids("relatedIncidentIds"), a.Evidencias("evidence"),
            a.Texto("idempotencyKey"));

        var r = await _servico.ProporDecisaoAsync(identidade.Ator, entrada, ct);
        return McpToolResult.Success(new
        {
            topicId = r.TopicoId,
            proposalId = r.PropostaId,
            streamVersion = r.VersaoFluxo,
            status = r.Situacao
        }, $"Proposta registrada no tópico '{entrada.ChaveTopico}' (proposta {r.PropostaId}, versão {r.VersaoFluxo}). Aguarda aprovação humana.");
    }

    private async Task<McpToolResult> RegistrarIncidenteAsync(LeitorDeArgumentos a, IdentidadeAgente identidade, CancellationToken ct)
    {
        identidade.ExigirEscopo(Escopos.EscreverIncidente);
        var entrada = new EntradaRegistrarIncidente(
            a.Guid("workspaceId"), a.Guid("projectId"), a.Texto("title"), a.Texto("summary"),
            a.DataOpcional("occurredAt"), a.ListaTextos("technicalKeys"), a.Evidencias("evidence"), a.Texto("idempotencyKey"));

        var r = await _servico.RegistrarIncidenteAsync(identidade.Ator, entrada, ct);
        return McpToolResult.Success(new
        {
            incidentId = r.IncidenteId,
            streamVersion = r.VersaoFluxo,
            status = r.Situacao
        }, $"Incidente {r.IncidenteId} registrado (versão {r.VersaoFluxo}).");
    }

    private async Task<McpToolResult> ConsultarContextoAsync(LeitorDeArgumentos a, IdentidadeAgente identidade, CancellationToken ct)
    {
        identidade.ExigirEscopo(Escopos.LerMemoria);
        var entrada = new EntradaConsultarContexto(
            a.Guid("workspaceId"), a.Guid("projectId"), a.Texto("query"),
            a.TextoOpcional("kind") ?? "decision", a.InteiroOpcional("limit") ?? 10);

        var r = await _servico.ConsultarContextoAsync(identidade.Ator, entrada, ct);
        var resumo = r.Itens.Count == 0
            ? $"Nenhum registro encontrado para '{entrada.TermoBusca}'. Comprovante: {r.ConsultaId}."
            : $"{r.Itens.Count} registro(s) encontrado(s). Comprovante: {r.ConsultaId}.";

        return McpToolResult.Success(new
        {
            queryId = r.ConsultaId,
            matchStatus = r.StatusCorrespondencia,
            searchMode = r.ModoBusca,
            items = r.Itens.Select(i => new
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
                evidence = Evidencias(i.Evidencias)
            }).ToList()
        }, resumo);
    }

    private async Task<McpToolResult> ObterLinhaDoTempoAsync(LeitorDeArgumentos a, IdentidadeAgente identidade, CancellationToken ct)
    {
        identidade.ExigirEscopo(Escopos.LerMemoria);
        var consulta = new ConsultaLinhaDoTempo(
            a.Guid("workspaceId"), a.Guid("projectId"), a.Texto("kind") ?? string.Empty, a.Guid("streamId"),
            a.InteiroOpcional("atVersion"), a.InteiroOpcional("beforeVersion"), a.InteiroOpcional("limit") ?? 20);

        var r = await _servico.ObterLinhaDoTempoAsync(identidade.Ator, consulta, ct);

        object estado = r.EstadoDecisao is { } d
            ? (object)new
            {
                topicId = d.TopicoId,
                workspaceId = d.EspacoTrabalhoId,
                projectId = d.ProjetoId,
                topicKey = d.ChaveTopico,
                streamVersion = d.VersaoFluxo,
                activeRecordId = d.RegistroAtivoId,
                activeRecordVersion = d.VersaoRegistroAtivo,
                status = d.Situacao.ParaTexto(),
                pendingProposalId = d.PropostaPendenteId,
                title = d.Titulo,
                summary = d.Resumo,
                rationale = d.Justificativa,
                technicalKeys = d.ChavesTecnicas,
                relatedIncidentIds = d.IncidentesRelacionadosIds,
                evidence = Evidencias(d.Evidencias)
            }
            : r.EstadoIncidente is { } i
                ? (object)new
                {
                    incidentId = i.IncidenteId,
                    workspaceId = i.EspacoTrabalhoId,
                    projectId = i.ProjetoId,
                    streamVersion = i.VersaoFluxo,
                    recordVersion = i.VersaoRegistro,
                    status = i.Situacao.ParaTexto(),
                    title = i.Titulo,
                    summary = i.Resumo,
                    occurredAt = i.OcorridoEm,
                    technicalKeys = i.ChavesTecnicas,
                    evidence = Evidencias(i.Evidencias)
                }
                : (object)new { };

        var situacao = r.EstadoDecisao?.Situacao.ParaTexto() ?? r.EstadoIncidente?.Situacao.ParaTexto();
        return McpToolResult.Success(new
        {
            kind = r.TipoRegistro,
            streamId = r.FluxoId,
            currentStreamVersion = r.VersaoFluxoAtual,
            targetVersion = r.VersaoAlvo,
            reconstitutedState = estado,
            events = r.Eventos.Select(e => new
            {
                eventId = e.EventoId,
                streamVersion = e.VersaoFluxo,
                type = e.Tipo,
                recordedAt = e.RegistradoEm,
                authorSubject = e.AutorSujeito,
                clientId = e.ClienteId,
                payloadId = e.CargaId,
                refEventId = e.EventoReferenciaId,
                reason = e.Motivo
            }).ToList(),
            nextBeforeVersion = r.ProximaVersaoAnterior
        }, $"Linha do tempo de {r.TipoRegistro} {r.FluxoId} na versão {r.VersaoAlvo} (atual {r.VersaoFluxoAtual}). Situação: {situacao}.");
    }

    private async Task<McpToolResult> ObterComprovanteAsync(LeitorDeArgumentos a, IdentidadeAgente identidade, CancellationToken ct)
    {
        identidade.ExigirEscopo(Escopos.LerMemoria);
        var c = await _servico.ObterComprovanteAsync(identidade.Ator, a.Guid("workspaceId"), a.Guid("projectId"), a.Guid("queryId"), ct);

        return McpToolResult.Success(new
        {
            queryId = c.ConsultaId,
            workspaceId = c.EspacoTrabalhoId,
            projectId = c.ProjetoId,
            principalId = c.PrincipalId,
            principalIssuer = c.PrincipalEmissor,
            clientId = c.ClienteId,
            searchMode = c.ModoBusca,
            createdAt = c.CriadoEm,
            digest = c.Digest,
            digestAlgorithm = "sha256",
            integrityVerified = c.IntegridadeVerificada,
            items = c.Itens.Select(i => new
            {
                position = i.Posicao,
                recordType = i.TipoRegistro,
                streamId = i.FluxoId,
                recordId = i.RegistroId,
                recordVersion = i.VersaoRegistro,
                streamVersion = i.VersaoFluxo,
                status = i.Situacao
            }).ToList()
        }, $"Comprovante {c.ConsultaId}: {c.Itens.Count} referência(s); integridade {(c.IntegridadeVerificada ? "verificada" : "NÃO verificada")}.");
    }
}

// Leitor tipado de argumentos JSON
internal sealed class LeitorDeArgumentos
{
    private readonly JsonElement _raiz;

    public LeitorDeArgumentos(JsonElement raiz) => _raiz = raiz;

    private JsonElement? Obter(string nome) =>
        _raiz.TryGetProperty(nome, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static ExcecaoDeValidacao Erro(string nome, string esperado) =>
        new($"Argumento '{nome}' inválido: esperado {esperado}.");

    public Guid Guid(string nome)
    {
        var v = Obter(nome) ?? throw new ExcecaoDeValidacao($"Argumento obrigatório ausente: '{nome}'.");
        if (v.ValueKind != JsonValueKind.String || !System.Guid.TryParse(v.GetString(), out var g))
            throw Erro(nome, "UUID");
        return g;
    }

    // Lê campo de texto opcional
    public string? Texto(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.String) throw Erro(nome, "string");
        return LerTexto(v.Value, nome);
    }

    // Lê texto validando UTF-8
    private static string LerTexto(JsonElement elemento, string nome)
    {
        try
        {
            return elemento.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            throw Erro(nome, "texto UTF-8 válido");
        }
    }

    public string? TextoOpcional(string nome) => Texto(nome);

    public int Inteiro(string nome) =>
        InteiroOpcional(nome) ?? throw new ExcecaoDeValidacao($"Argumento obrigatório ausente: '{nome}'.");

    public int? InteiroOpcional(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.Number || !v.Value.TryGetInt32(out var i)) throw Erro(nome, "inteiro");
        return i;
    }

    public DateTimeOffset? DataOpcional(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(v.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            throw Erro(nome, "data ISO-8601");
        return d;
    }

    public List<string>? ListaTextos(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.Array) throw Erro(nome, "array de strings");
        return v.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? LerTexto(x, nome) : throw Erro(nome, "array de strings")).ToList();
    }

    public List<Guid>? ListaGuids(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.Array) throw Erro(nome, "array de UUIDs");
        return v.Value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String && System.Guid.TryParse(x.GetString(), out var g) ? g : throw Erro(nome, "array de UUIDs"))
            .ToList();
    }

    public List<Evidencia>? Evidencias(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.Array) throw Erro(nome, "array de evidências");
        return v.Value.EnumerateArray().Select(e =>
        {
            if (e.ValueKind != JsonValueKind.Object) throw Erro(nome, "objetos {type, label, url}");
            string Campo(string c) => e.TryGetProperty(c, out var x) && x.ValueKind == JsonValueKind.String ? LerTexto(x, $"{nome}.{c}") : string.Empty;
            return new Evidencia(Campo("type"), Campo("label"), Campo("url"));
        }).ToList();
    }
}
