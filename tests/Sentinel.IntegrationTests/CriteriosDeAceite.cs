using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Core.Contratos;
using Xunit;
using static Sentinel.IntegrationTests.DadosDemo;

namespace Sentinel.IntegrationTests;

// Critérios de aceite AC-01 a AC-08
public abstract class CriteriosDeAceiteBase
{
    private readonly IProvedorDeTeste _provedor;

    protected CriteriosDeAceiteBase(IProvedorDeTeste provedor) => _provedor = provedor;

    protected FabricaSentinel Fabrica
    {
        get
        {
            Skip.IfNot(_provedor.Disponivel, _provedor.MotivoIndisponivel);
            return _provedor.Fabrica;
        }
    }

    protected ClienteMcp Mcp() => new(Fabrica.CreateClient());

    protected static string NovaChave(string prefixo) => $"{prefixo}-{Guid.NewGuid():N}"[..Math.Min(prefixo.Length + 13, 80)];

    protected static object ArgsProposta(string topicKey, int expectedVersion, string title, string idempotencyKey, string[]? keys = null, string url = "https://example.com/pull/1") => new
    {
        workspaceId = WsA,
        projectId = ProjA,
        topicKey,
        expectedVersion,
        title,
        summary = $"Resumo de {title}",
        rationale = "Justificativa técnica.",
        technicalKeys = keys ?? new[] { "BillingService" },
        evidence = new[] { new { type = "pull_request", label = "PR", url } },
        idempotencyKey
    };

    protected async Task<JsonElement> ProporAsync(ClienteMcp mcp, string topicKey, int expectedVersion, string title, string[]? keys = null) =>
        (await mcp.ToolAsync("propose_decision", ArgsProposta(topicKey, expectedVersion, title, Guid.NewGuid().ToString(), keys))).Sucesso();

    protected async Task<JsonElement> ConsultarAsync(ClienteMcp mcp, string query, string kind = "decision", string token = TokenAgente) =>
        (await mcp.ToolAsync("query_context", new { workspaceId = WsA, projectId = ProjA, query, kind, limit = 10 }, token)).Sucesso();

    protected async Task<JsonElement> TimelineAsync(ClienteMcp mcp, Guid streamId, int? atVersion = null, string kind = "decision") =>
        (await mcp.ToolAsync("get_timeline", new { workspaceId = WsA, projectId = ProjA, kind, streamId, atVersion, limit = 50 })).Sucesso();

    [SkippableFact]
    public async Task AC01_Interoperabilidade_HandshakeListaEChamadaDeTool()
    {
        var mcp = Mcp();
        var init = await mcp.RpcAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "Claude Code", version = "1.0" }
        }, TokenAgente);
        Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());

        var notificacao = await mcp.EnviarAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, TokenAgente);
        Assert.Equal(HttpStatusCode.Accepted, notificacao.StatusCode);
        Assert.Equal(string.Empty, await notificacao.Content.ReadAsStringAsync());

        var lista = await mcp.RpcAsync("tools/list", null, TokenAgente);
        Assert.Equal(5, lista.GetProperty("result").GetProperty("tools").GetArrayLength());

        var consulta = await ConsultarAsync(mcp, "idempotência");
        Assert.Equal("found", consulta.GetProperty("matchStatus").GetString());
        Assert.True(consulta.GetProperty("queryId").GetGuid() != Guid.Empty);
    }

    [SkippableFact]
    public async Task AC02_ConcorrenciaNoMesmoTopico_UmSucessoENoveConflitos()
    {
        var mcp = Mcp();
        var topico = NovaChave("concorrencia");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            mcp.ToolAsync("propose_decision", ArgsProposta(topico, 0, $"Variante {i}", Guid.NewGuid().ToString()))));

        Assert.Equal(1, resultados.Count(r => !r.EhErro()));
        Assert.Equal(9, resultados.Count(r => r.CodigoErro() == "VERSION_CONFLICT"));

        var topicId = resultados.Single(r => !r.EhErro()).Dados().GetProperty("topicId").GetGuid();
        var timeline = await TimelineAsync(mcp, topicId);
        Assert.Equal(1, timeline.GetProperty("currentStreamVersion").GetInt32());
        Assert.Equal(1, timeline.GetProperty("events").GetArrayLength());
    }

    [SkippableFact]
    public async Task AC03_IndependenciaEntreTopicos_DezTopicosEmParalelo()
    {
        var mcp = Mcp();
        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            mcp.ToolAsync("propose_decision", ArgsProposta(NovaChave($"indep{i}"), 0, $"Tópico {i}", Guid.NewGuid().ToString()))));

        Assert.All(resultados, r => Assert.Equal(1, r.Sucesso().GetProperty("streamVersion").GetInt32()));
        Assert.Equal(10, resultados.Select(r => r.Dados().GetProperty("topicId").GetGuid()).Distinct().Count());
    }

    [SkippableFact]
    public async Task AC04_Idempotencia_RetentativasSequenciaisEConcorrentes()
    {
        var mcp = Mcp();
        var topico = NovaChave("idem");
        var chave = Guid.NewGuid().ToString();
        var args = ArgsProposta(topico, 0, "Original", chave);

        // Retentativas concorrentes com a mesma chave
        var concorrentes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => mcp.ToolAsync("propose_decision", args)));
        var propostas = concorrentes.Select(r => r.Sucesso().GetProperty("proposalId").GetGuid()).Distinct().ToList();
        Assert.Single(propostas);

        var repeticao = (await mcp.ToolAsync("propose_decision", args)).Sucesso();
        Assert.Equal(propostas[0], repeticao.GetProperty("proposalId").GetGuid());

        var adulterado = await mcp.ToolAsync("propose_decision", ArgsProposta(topico, 0, "Título alterado", chave));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", adulterado.CodigoErro());

        var timeline = await TimelineAsync(mcp, repeticao.GetProperty("topicId").GetGuid());
        Assert.Equal(1, timeline.GetProperty("events").GetArrayLength());
    }

    [SkippableFact]
    public async Task AC05_IsolamentoEntreWorkspaces_RespondeNotFound()
    {
        var mcp = Mcp();
        var cruzado = await mcp.ToolAsync("query_context", new { workspaceId = WsB, projectId = ProjB, query = "audit" });
        Assert.Equal("NOT_FOUND", cruzado.CodigoErro());

        var semEscopo = await mcp.ToolAsync("propose_decision", ArgsProposta(NovaChave("escopo"), 0, "X", Guid.NewGuid().ToString()), TokenAgenteSomenteLeitura);
        Assert.Equal("FORBIDDEN", semEscopo.CodigoErro());
    }

    [SkippableFact]
    public async Task AC05_RevisaoWeb_NaoAceitaTokenMcpNemSessaoForjada()
    {
        var mcp = Mcp();
        var proposta = await ProporAsync(mcp, NovaChave("revisao"), 0, "Proposta para revisão");
        var corpo = new
        {
            workspaceId = WsA,
            projectId = ProjA,
            topicId = proposta.GetProperty("topicId").GetGuid(),
            proposalId = proposta.GetProperty("proposalId").GetGuid(),
            expectedVersion = 1,
            idempotencyKey = Guid.NewGuid().ToString()
        };
        var anonimo = Fabrica.CreateClient();

        // Token Bearer não é aceito na revisão
        var comBearer = new HttpRequestMessage(HttpMethod.Post, "/api/review/approve") { Content = JsonContent.Create(corpo) };
        comBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenMantenedor);
        comBearer.Headers.Add("X-CSRF-Token", "qualquer");
        Assert.Equal(HttpStatusCode.Forbidden, (await anonimo.SendAsync(comBearer)).StatusCode);

        // Sessão forjada é recusada
        var forjada = new HttpRequestMessage(HttpMethod.Post, "/api/review/approve") { Content = JsonContent.Create(corpo) };
        forjada.Headers.Add("X-Sentinel-Web-Session", "maintainer-alice:https://auth.sentinel.dev");
        forjada.Headers.Add("X-CSRF-Token", "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.SendAsync(forjada)).StatusCode);

        // Acesso anônimo retorna 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/review/state")).StatusCode);
        var semCookies = Fabrica.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var pagina = await semCookies.GetAsync("/review");
        Assert.Equal(HttpStatusCode.Redirect, pagina.StatusCode);
        Assert.StartsWith("/auth/login", pagina.Headers.Location!.OriginalString);

        // Falha de antiforgery sem CSRF válido
        var alice = await SessaoWeb.EntrarAsync(Fabrica, "maintainer-alice");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostarAsync("/api/review/approve", corpo, comCsrf: false)).StatusCode);

        // Colaborador não pode aprovar na revisão
        var agenteWeb = await SessaoWeb.EntrarAsync(Fabrica, "agent-dev-1");
        Assert.False(agenteWeb.Estado.GetProperty("usuario").GetProperty("podeRevisar").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await agenteWeb.PostarAsync("/api/review/approve", corpo)).StatusCode);

        // Mantenedor com CSRF aprova decisão
        var aprovado = await alice.AprovarAsync(corpo.topicId, corpo.proposalId, 1);
        Assert.Equal("active", aprovado.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task AC05_PainelWeb_MostraSomenteWorkspacesDoUsuario()
    {
        var alice = await SessaoWeb.EntrarAsync(Fabrica, "maintainer-alice");
        var workspaces = alice.Estado.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([WsA], workspaces);

        var outro = await alice.Http.GetAsync($"/api/review/state?workspaceId={WsB}");
        Assert.Equal(HttpStatusCode.NotFound, outro.StatusCode);
    }

    [SkippableFact]
    public async Task AC05_EvidenciaComEsquemaPerigoso_EhRecusadaNaEntrada()
    {
        var mcp = Mcp();
        var resultado = await mcp.ToolAsync("propose_decision",
            ArgsProposta(NovaChave("xss"), 0, "XSS", Guid.NewGuid().ToString(), url: "javascript:alert(1)"));
        Assert.Equal("VALIDATION_ERROR", resultado.CodigoErro());
    }

    [SkippableFact]
    public async Task AC06_VigenciaEHistorico_SubstituicaoTimelineEComprovante()
    {
        var mcp = Mcp();
        var alice = await SessaoWeb.EntrarAsync(Fabrica, "maintainer-alice");
        var topico = NovaChave("vigencia");
        var chaveV1 = $"LIFECYCLE_V1_{Guid.NewGuid():N}";
        var chaveV2 = $"LIFECYCLE_V2_{Guid.NewGuid():N}";

        var p1 = await ProporAsync(mcp, topico, 0, "Decisão versão 1", [chaveV1]);
        var topicId = p1.GetProperty("topicId").GetGuid();
        await alice.AprovarAsync(topicId, p1.GetProperty("proposalId").GetGuid(), 1);

        var consulta1 = await ConsultarAsync(mcp, chaveV1);
        var item1 = consulta1.GetProperty("items")[0];
        Assert.Equal("Decisão versão 1", item1.GetProperty("title").GetString());
        Assert.Equal(1, item1.GetProperty("recordVersion").GetInt32());
        Assert.Equal(2, item1.GetProperty("streamVersion").GetInt32());

        // Nova proposta para decisão vigente
        var p2 = await ProporAsync(mcp, topico, 2, "Decisão versão 2", [chaveV2]);
        await alice.AtualizarEstadoAsync();
        var pendente = alice.Estado.GetProperty("propostas").EnumerateArray().Single(p => p.GetProperty("topicoId").GetGuid() == topicId);
        Assert.Equal("Decisão versão 2", pendente.GetProperty("titulo").GetString());
        Assert.Equal("Decisão versão 1", pendente.GetProperty("decisaoVigente").GetProperty("titulo").GetString());

        // Decisão vigente permanece até aprovação
        Assert.Equal("Decisão versão 1", (await ConsultarAsync(mcp, chaveV1)).GetProperty("items")[0].GetProperty("title").GetString());

        await alice.AprovarAsync(topicId, p2.GetProperty("proposalId").GetGuid(), 3);
        var consulta2 = await ConsultarAsync(mcp, chaveV2);
        var item2 = consulta2.GetProperty("items")[0];
        Assert.Equal("Decisão versão 2", item2.GetProperty("title").GetString());
        Assert.Equal(2, item2.GetProperty("recordVersion").GetInt32());
        Assert.Equal(4, item2.GetProperty("streamVersion").GetInt32());

        var naV2 = (await TimelineAsync(mcp, topicId, atVersion: 2)).GetProperty("reconstitutedState");
        Assert.Equal("Decisão versão 1", naV2.GetProperty("title").GetString());
        Assert.Equal("active", naV2.GetProperty("status").GetString());

        // Comprovante preserva versões originais
        var comprovante = (await mcp.ToolAsync("get_context_receipt", new { workspaceId = WsA, projectId = ProjA, queryId = consulta1.GetProperty("queryId").GetGuid() })).Sucesso();
        var itemComprovante = comprovante.GetProperty("items")[0];
        Assert.Equal(1, itemComprovante.GetProperty("recordVersion").GetInt32());
        Assert.Equal(2, itemComprovante.GetProperty("streamVersion").GetInt32());
        Assert.True(comprovante.GetProperty("integrityVerified").GetBoolean());

        // Bloqueia leitura de comprovante por terceiros
        var alheio = await mcp.ToolAsync("get_context_receipt", new { workspaceId = WsA, projectId = ProjA, queryId = consulta1.GetProperty("queryId").GetGuid() }, TokenLeitor);
        Assert.Equal("NOT_FOUND", alheio.CodigoErro());
    }

    [SkippableFact]
    public async Task AC07_QualidadeELatencia_ChaveTecnicaExataPrimeiro()
    {
        var mcp = Mcp();
        var alice = await SessaoWeb.EntrarAsync(Fabrica, "maintainer-alice");
        var chave = $"TARGET_{Guid.NewGuid():N}".ToUpperInvariant();

        var porTexto = await ProporAsync(mcp, NovaChave("texto"), 0, $"Discussão que menciona {chave} no texto", ["OUTRA_CHAVE"]);
        await alice.AprovarAsync(porTexto.GetProperty("topicId").GetGuid(), porTexto.GetProperty("proposalId").GetGuid(), 1);
        var porChave = await ProporAsync(mcp, NovaChave("chave"), 0, "Título genérico", [chave]);
        await alice.AprovarAsync(porChave.GetProperty("topicId").GetGuid(), porChave.GetProperty("proposalId").GetGuid(), 1);

        var resultado = await ConsultarAsync(mcp, chave.ToLowerInvariant());
        var itens = resultado.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, itens.Count);
        Assert.Equal(porChave.GetProperty("topicId").GetGuid(), itens[0].GetProperty("topicId").GetGuid());

        // Medição de latência da consulta
        var tempos = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var cronometro = Stopwatch.StartNew();
            await ConsultarAsync(mcp, chave);
            tempos.Add(cronometro.Elapsed.TotalMilliseconds);
        }
        var p95 = tempos.OrderBy(t => t).ElementAt((int)Math.Ceiling(0.95 * tempos.Count) - 1);
        Assert.True(p95 < 500, $"p95 = {p95:F1} ms");
    }

    [SkippableFact]
    public async Task AC08_RecuperacaoEReproducao_RebuildReproduzOMesmoEstado()
    {
        var mcp = Mcp();
        var alice = await SessaoWeb.EntrarAsync(Fabrica, "maintainer-alice");

        // Cenário com múltiplos tipos de eventos
        var aprovada = await ProporAsync(mcp, NovaChave("rebuild-a"), 0, "Aprovada");
        await alice.AprovarAsync(aprovada.GetProperty("topicId").GetGuid(), aprovada.GetProperty("proposalId").GetGuid(), 1);
        var rejeitada = await ProporAsync(mcp, NovaChave("rebuild-r"), 0, "Rejeitada");
        var rejeicao = await alice.PostarAsync("/api/review/reject", new
        {
            workspaceId = WsA,
            projectId = ProjA,
            topicId = rejeitada.GetProperty("topicId").GetGuid(),
            proposalId = rejeitada.GetProperty("proposalId").GetGuid(),
            expectedVersion = 1,
            reason = "Sem evidência de carga.",
            idempotencyKey = Guid.NewGuid().ToString()
        });
        rejeicao.EnsureSuccessStatusCode();
        await ProporAsync(mcp, NovaChave("rebuild-p"), 0, "Pendente");
        (await mcp.ToolAsync("log_incident", new
        {
            workspaceId = WsA,
            projectId = ProjA,
            title = "Incidente para rebuild",
            summary = "Timeout no gateway.",
            occurredAt = "2026-10-01T12:00:00Z",
            technicalKeys = new[] { "Gateway" },
            evidence = new[] { new { type = "incident_rca", label = "RCA", url = "https://example.com/rca/1" } },
            idempotencyKey = Guid.NewGuid().ToString()
        })).Sucesso();

        var antes = await CapturarProjecoesAsync();
        await Fabrica.Services.GetRequiredService<IReconstrutorProjecoes>().ReconstruirProjecoesAsync(WsA);
        var depois = await CapturarProjecoesAsync();

        Assert.Equal(antes, depois);
        Assert.Contains("\"OcorridoEm\":\"2026-10-01T12:00:00+00:00\"", depois);
    }

    private async Task<string> CapturarProjecoesAsync()
    {
        var decisoes = Fabrica.Services.GetRequiredService<IRepositorioDecisao>();
        var incidentes = Fabrica.Services.GetRequiredService<IRepositorioIncidente>();
        return JsonSerializer.Serialize(new
        {
            decisoes = await decisoes.ObterDecisoesAsync(WsA),
            pendentes = await decisoes.ObterPropostasPendentesAsync(WsA),
            incidentes = await incidentes.ObterIncidentesAsync(WsA)
        });
    }
}

public sealed class CriteriosDeAceiteEmMemoria : CriteriosDeAceiteBase, IClassFixture<ProvedorEmMemoria>
{
    public CriteriosDeAceiteEmMemoria(ProvedorEmMemoria provedor) : base(provedor) { }
}

public sealed class CriteriosDeAceitePostgreSql : CriteriosDeAceiteBase, IClassFixture<ProvedorPostgreSql>
{
    private readonly ProvedorPostgreSql _pg;

    public CriteriosDeAceitePostgreSql(ProvedorPostgreSql provedor) : base(provedor) => _pg = provedor;

    [SkippableFact]
    public async Task RLS_UsuarioDaAplicacaoSoEnxergaOWorkspaceDaTransacao()
    {
        Skip.IfNot(_pg.Disponivel, _pg.MotivoIndisponivel);
        await using var conexao = new Npgsql.NpgsqlConnection(_pg.StringConexaoApp);
        await conexao.OpenAsync();

        async Task<long> ContarAsync(string? workspace)
        {
            await using var tx = await conexao.BeginTransactionAsync();
            if (workspace != null)
            {
                await using var set = new Npgsql.NpgsqlCommand("SELECT set_config('app.current_workspace_id', @ws, true)", conexao, tx);
                set.Parameters.AddWithValue("ws", workspace);
                await set.ExecuteNonQueryAsync();
            }
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM memberships", conexao, tx);
            var total = (long)(await cmd.ExecuteScalarAsync())!;
            await tx.RollbackAsync();
            return total;
        }

        Assert.Equal(0, await ContarAsync(null));
        Assert.Equal(3, await ContarAsync(WsA.ToString()));
        Assert.Equal(2, await ContarAsync(WsB.ToString()));
        // Garante isolamento fora da transação
        Assert.Equal(0, await ContarAsync(null));
    }

    [SkippableFact]
    public async Task LogDeEventos_EhImutavel()
    {
        Skip.IfNot(_pg.Disponivel, _pg.MotivoIndisponivel);

        await using var app = new Npgsql.NpgsqlConnection(_pg.StringConexaoApp);
        await app.OpenAsync();
        await using var cmdApp = new Npgsql.NpgsqlCommand("UPDATE memory_events SET type = 'X'", app);
        var erroApp = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmdApp.ExecuteNonQueryAsync());
        Assert.Equal("42501", erroApp.SqlState); // sem privilégio de UPDATE

        await using var admin = new Npgsql.NpgsqlConnection(_pg.StringConexaoAdmin);
        await admin.OpenAsync();
        await using var cmdAdmin = new Npgsql.NpgsqlCommand("DELETE FROM memory_events", admin);
        var erroAdmin = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmdAdmin.ExecuteNonQueryAsync());
        Assert.Contains("append-only", erroAdmin.MessageText);
    }
}
