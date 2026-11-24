using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sentinel.Api.Auth;
using Sentinel.Api.Operacao;
using Sentinel.Core.CasosDeUso;
using Sentinel.Core.Consultas;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;

namespace Sentinel.Api.Mcp;

// Despachante de ferramentas, recursos e prompts MCP
public sealed class McpDispatcher
{
    private static readonly HashSet<string> Ferramentas =
        ["propose_decision", "log_incident", "query_context", "get_timeline", "get_context_receipt", "list_workspaces"];

    private static readonly Regex PadraoUriDecisoes = new(
        @"^sentinel://workspaces/(?<ws>[0-9a-fA-F-]{36})/projects/(?<proj>[0-9a-fA-F-]{36})/decisions$",
        RegexOptions.Compiled);

    private static readonly Regex PadraoUriIncidentes = new(
        @"^sentinel://workspaces/(?<ws>[0-9a-fA-F-]{36})/projects/(?<proj>[0-9a-fA-F-]{36})/incidents$",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static readonly List<McpResourceDefinition> AllResources =
    [
        new McpResourceDefinition(
            "sentinel://workspaces",
            "workspaces",
            "Workspaces e Projetos Autorizados",
            "Lista os workspaces, papéis e projetos acessíveis à identidade autenticada no formato JSON.",
            "application/json"),
        new McpResourceDefinition(
            "sentinel://guide",
            "governance-guide",
            "Guia de Governança Sentinel-MCP",
            "Regras operacionais de consulta, concorrência otimista, evidências HTTPS e revisão humana.",
            "text/markdown")
    ];

    public static readonly List<McpResourceTemplateDefinition> AllResourceTemplates =
    [
        new McpResourceTemplateDefinition(
            "sentinel://workspaces/{workspaceId}/projects/{projectId}/decisions",
            "project-decisions",
            "Decisões Vigentes do Projeto",
            "Lista todas as decisões arquiteturais ativas de um projeto específico.",
            "application/json"),
        new McpResourceTemplateDefinition(
            "sentinel://workspaces/{workspaceId}/projects/{projectId}/incidents",
            "project-incidents",
            "Incidentes do Projeto",
            "Lista todos os incidentes reportados de um projeto específico.",
            "application/json")
    ];

    public static readonly List<McpPromptDefinition> AllPrompts =
    [
        new McpPromptDefinition(
            "consult_context_before_change",
            "Consultar Governança Antes de Alterar Código",
            "Guia o agente a descobrir workspaces/projetos e consultar decisões vigentes e incidentes antes de propor mudanças.",
            [new McpPromptArgument("topic", "Tema, serviço ou chave técnica sendo alterada", false)]),
        new McpPromptDefinition(
            "propose_architecture_decision",
            "Propor Decisão Arquitetural",
            "Fluxo guiado para propor ou substituir uma decisão arquitetural com controle de versão, evidências HTTPS e idempotência.",
            [new McpPromptArgument("topicKey", "Slug do tópico (ex.: payment-idempotency)", false)]),
        new McpPromptDefinition(
            "report_incident_rca",
            "Registrar Incidente e Mitigação",
            "Fluxo guiado para registrar um incidente técnico com chaves técnicas e links HTTPS de RCA/PR.",
            [new McpPromptArgument("service", "Nome do serviço ou componente afetado", false)])
    ];

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
            if (argumentos.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
                throw new ExcecaoDeValidacao("'arguments' deve ser um objeto JSON.");

            var args = new LeitorDeArgumentos(argumentos.ValueKind == JsonValueKind.Object ? argumentos : default);
            var resultado = nome switch
            {
                "propose_decision" => await ProporDecisaoAsync(args, identidade, ct),
                "log_incident" => await RegistrarIncidenteAsync(args, identidade, ct),
                "query_context" => await ConsultarContextoAsync(args, identidade, ct),
                "get_timeline" => await ObterLinhaDoTempoAsync(args, identidade, ct),
                "get_context_receipt" => await ObterComprovanteAsync(args, identidade, ct),
                "list_workspaces" => await ListarEspacosAsync(args, identidade, ct),
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
            _log.LogError(ex, "Falha inesperada na tool MCP {Ferramenta} (correlação {Correlacao})", nome, idCorrelacao);
            return McpToolResult.Error("INTERNAL_ERROR", $"Erro interno. ID de correlação: {idCorrelacao}.");
        }
    }

    public async Task<(string MimeType, string Content)> ReadResourceAsync(string uri, IdentidadeAgente identidade, CancellationToken ct = default)
    {
        if (string.Equals(uri, "sentinel://guide", StringComparison.Ordinal))
        {
            return ("text/markdown", """
                # Guia Operacional Sentinel-MCP

                1. **Descoberta**: Chame `list_workspaces` (ou leia `sentinel://workspaces`) para obter `workspaceId` e `projectId`.
                2. **Consulta prévia**: Antes de propor mudanças arquiteturais, chame `query_context` para verificar decisões `active` e incidentes `reported`.
                3. **Concorrência**: Use `expectedVersion: 0` para tópico inédito ou a `streamVersion` atual do tópico em `propose_decision`.
                4. **Evidências**: Toda proposta e incidente exige ao menos uma evidência com URL `https://` sem credenciais ou query params secretos.
                5. **Aprovação humana**: Nenhuma ferramenta MCP aprova decisões. Um humano com papel Maintainer revisa e aprova em `/review`.
                """);
        }

        if (string.Equals(uri, "sentinel://workspaces", StringComparison.Ordinal))
        {
            identidade.ExigirEscopo(Escopos.LerMemoria);
            var lista = await _servico.ObterEspacosEProjetosDaIdentidadeAsync(identidade.Ator, ct);
            var payload = new
            {
                actorSubject = identidade.Ator.Sujeito,
                actorIssuer = identidade.Ator.Emissor,
                workspaces = lista.Select(x => new
                {
                    workspaceId = x.EspacoTrabalho.EspacoTrabalhoId,
                    name = x.EspacoTrabalho.Nome,
                    slug = x.EspacoTrabalho.IdentificadorSlug,
                    role = x.Papel.ParaTexto(),
                    projects = x.Projetos.Select(p => new
                    {
                        projectId = p.ProjetoId,
                        name = p.Nome,
                        slug = p.IdentificadorSlug
                    }).ToList()
                }).ToList()
            };
            return ("application/json", JsonSerializer.Serialize(payload, OpcoesJson));
        }

        var matchDec = PadraoUriDecisoes.Match(uri);
        if (matchDec.Success)
        {
            identidade.ExigirEscopo(Escopos.LerMemoria);
            var ws = Guid.Parse(matchDec.Groups["ws"].Value);
            var proj = Guid.Parse(matchDec.Groups["proj"].Value);
            var decisoes = await _servico.ListarDecisoesVigentesDoProjetoAsync(identidade.Ator, ws, proj, ct);
            var payload = decisoes.Select(d => new
            {
                topicId = d.TopicoId,
                topicKey = d.ChaveTopico,
                activeRecordId = d.RegistroAtivoId,
                recordVersion = d.VersaoRegistroAtivo,
                streamVersion = d.VersaoFluxo,
                status = d.Situacao.ParaTexto(),
                title = d.Titulo,
                summary = d.Resumo,
                rationale = d.Justificativa,
                technicalKeys = d.ChavesTecnicas,
                evidence = Evidencias(d.Evidencias),
                updatedAt = d.AtualizadoEm
            }).ToList();
            return ("application/json", JsonSerializer.Serialize(payload, OpcoesJson));
        }

        var matchInc = PadraoUriIncidentes.Match(uri);
        if (matchInc.Success)
        {
            identidade.ExigirEscopo(Escopos.LerMemoria);
            var ws = Guid.Parse(matchInc.Groups["ws"].Value);
            var proj = Guid.Parse(matchInc.Groups["proj"].Value);
            var incidentes = await _servico.ListarIncidentesDoProjetoAsync(identidade.Ator, ws, proj, ct);
            var payload = incidentes.Select(i => new
            {
                incidentId = i.IncidenteId,
                recordVersion = i.VersaoRegistro,
                streamVersion = i.VersaoFluxo,
                status = i.Situacao.ParaTexto(),
                title = i.Titulo,
                summary = i.Resumo,
                occurredAt = i.OcorridoEm,
                technicalKeys = i.ChavesTecnicas,
                evidence = Evidencias(i.Evidencias)
            }).ToList();
            return ("application/json", JsonSerializer.Serialize(payload, OpcoesJson));
        }

        throw new ExcecaoDeNaoEncontrado($"Recurso MCP não encontrado: '{uri}'.");
    }

    public static (string Description, string UserMessage) GetPromptContent(string nome, IReadOnlyDictionary<string, string?>? args)
    {
        string Arg(string k, string padrao) =>
            args != null && args.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v! : padrao;

        return nome switch
        {
            "consult_context_before_change" => (
                "Consulta decisões vigentes e incidentes antes de propor mudanças técnicas.",
                $"Antes de alterar '{Arg("topic", "o código alvo")}', siga estes passos:\n" +
                "1. Chame `list_workspaces` para obter o `workspaceId` e `projectId` corretos.\n" +
                $"2. Chame `query_context` com `kind=\"both\"` buscando por '{Arg("topic", "termos relevantes")}'.\n" +
                "3. Se encontrar decisões vigentes ou incidentes relacionados, cite seus IDs, versões e `queryId` do comprovante.\n" +
                "4. Respeite as decisões vigentes ou proponha substituição usando `propose_decision` com o `streamVersion` atual."),
            "propose_architecture_decision" => (
                "Submete uma proposta arquitetural para revisão humana.",
                $"Para registrar ou atualizar a decisão do tópico '{Arg("topicKey", "seu-topico-slug")}':\n" +
                "1. Chame `list_workspaces` para identificar `workspaceId` e `projectId`.\n" +
                "2. Consulte `query_context` para verificar se já existe decisão vigente ou incidente relacionado.\n" +
                "3. Se já existir tópico, chame `get_timeline` para obter `currentStreamVersion` (senão use `expectedVersion=0`).\n" +
                "4. Envie `propose_decision` com `evidence` em URL `https://` válida e `idempotencyKey` única.\n" +
                "5. Informe ao usuário que a proposta aguarda aprovação de um Maintainer em `/review`."),
            "report_incident_rca" => (
                "Registra relato técnico de incidente e mitigação.",
                $"Para registrar o incidente envolvendo '{Arg("service", "o serviço afetado")}':\n" +
                "1. Chame `list_workspaces` para obter `workspaceId` e `projectId`.\n" +
                "2. Chame `log_incident` com título objetivo, resumo da causa raiz/mitigação, `technicalKeys`, `occurredAt` ISO-8601 e evidências HTTPS (RCA/PR).\n" +
                "3. Guarde o `incidentId` retornado para referenciar em futuras decisões via `relatedIncidentIds`."),
            _ => throw new ExcecaoDeNaoEncontrado($"Prompt '{nome}' não encontrado.")
        };
    }

    private static object Evidencias(IEnumerable<Evidencia> evidencias) =>
        evidencias.Select(e => new { type = e.Tipo, label = e.Rotulo, url = e.Url }).ToList();

    private async Task<McpToolResult> ListarEspacosAsync(LeitorDeArgumentos a, IdentidadeAgente identidade, CancellationToken ct)
    {
        identidade.ExigirEscopo(Escopos.LerMemoria);
        var incluirProjetos = a.BooleanoOpcional("includeProjects") ?? true;
        var lista = await _servico.ObterEspacosEProjetosDaIdentidadeAsync(identidade.Ator, ct);

        var dados = new
        {
            actorSubject = identidade.Ator.Sujeito,
            actorIssuer = identidade.Ator.Emissor,
            workspaces = lista.Select(w => new
            {
                workspaceId = w.EspacoTrabalho.EspacoTrabalhoId,
                name = w.EspacoTrabalho.Nome,
                slug = w.EspacoTrabalho.IdentificadorSlug,
                role = w.Papel.ParaTexto(),
                projects = incluirProjetos
                    ? w.Projetos.Select(p => new
                    {
                        projectId = p.ProjetoId,
                        name = p.Nome,
                        slug = p.IdentificadorSlug
                    }).ToList()
                    : []
            }).ToList()
        };

        return McpToolResult.Success(dados,
            $"Encontrados {lista.Count} workspace(s) acessível(is) para '{identidade.Ator.Sujeito}'.");
    }

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
        }, $"Proposta registrada no tópico '{entrada.ChaveTopico}' (tópico {r.TopicoId}, proposta {r.PropostaId}, versão {r.VersaoFluxo}). Aguarda aprovação humana em /review.");
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
            ? new
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
                ? new
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
                : new { };

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
        _raiz.ValueKind == JsonValueKind.Object && _raiz.TryGetProperty(nome, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static ExcecaoDeValidacao Erro(string nome, string esperado) =>
        new($"Argumento '{nome}' inválido: esperado {esperado}.");

    public Guid Guid(string nome)
    {
        var v = Obter(nome) ?? throw new ExcecaoDeValidacao($"Argumento obrigatório ausente: '{nome}'.");
        if (v.ValueKind != JsonValueKind.String || !System.Guid.TryParse(v.GetString(), out var g))
            throw Erro(nome, "UUID");
        return g;
    }

    public string? Texto(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind != JsonValueKind.String) throw Erro(nome, "string");
        return LerTexto(v.Value, nome);
    }

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

    public bool? BooleanoOpcional(string nome)
    {
        var v = Obter(nome);
        if (v == null) return null;
        if (v.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.Value.GetBoolean();
        throw Erro(nome, "booleano");
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
