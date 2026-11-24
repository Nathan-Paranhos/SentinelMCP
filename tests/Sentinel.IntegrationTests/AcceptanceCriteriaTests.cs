using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Api.Mcp;
using Sentinel.Core.Comandos;
using Sentinel.Core.Consultas;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Persistence.EmMemoria;
using Xunit;

namespace Sentinel.IntegrationTests;

public class AcceptanceCriteriaTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly RepositorioSentinelEmMemoria _repo;

    private readonly Guid _wsA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _wsB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly Guid _projA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _projB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public AcceptanceCriteriaTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _repo = factory.Services.GetRequiredService<RepositorioSentinelEmMemoria>();
        _repo.PopularDadosDemonstracao();
    }

    [Fact]
    public async Task AC01_Interoperability_HandshakeAndToolCallsWork()
    {
        var initResp = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "Claude Code", version = "1.0" } }
        });
        initResp.EnsureSuccessStatusCode();

        var listResp = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        });
        listResp.EnsureSuccessStatusCode();

        var callReq = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "query_context",
                    arguments = new
                    {
                        workspaceId = _wsA,
                        projectId = _projA,
                        query = "idempotência",
                        kind = "decision"
                    }
                }
            })
        };
        callReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mock:https://auth.sentinel.dev:agent-dev-1:Contributor:read:memory,write:decision:claude-code");

        var callResp = await _client.SendAsync(callReq);
        callResp.EnsureSuccessStatusCode();

        var body = await callResp.Content.ReadFromJsonAsync<JsonRpcResponse>();
        Assert.NotNull(body);
        Assert.Null(body.Error);

        var doc = JsonDocument.Parse(JsonSerializer.Serialize(body.Result));
        var isError = doc.RootElement.TryGetProperty("isError", out var isErr) && isErr.GetBoolean();
        Assert.False(isError);
        Assert.True(doc.RootElement.TryGetProperty("structuredContent", out _));
    }

    [Fact]
    public async Task AC02_ConcurrencySameTopic_TenCommandsOneSucceedsNineConflict()
    {
        var topicKey = "concurrency-test-" + Guid.NewGuid().ToString("N")[..8];
        var tasks = new List<Task<McpToolResult>>();

        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var cmd = new ComandoProporDecisao(
                    EspacoTrabalhoId: _wsA,
                    ProjetoId: _projA,
                    ChaveTopico: topicKey,
                    VersaoEsperada: 0,
                    Titulo: $"Decision proposal variant {idx}",
                    Resumo: "Summary for concurrency test",
                    Justificativa: "Testing AC-02 version conflict",
                    ChavesTecnicas: ["BillingService"],
                    IncidentesRelacionadosIds: [],
                    Evidencias: [new Evidencia("pull_request", "PR 42", "https://example.com/pr/42")],
                    ChaveIdempotencia: $"key-{Guid.NewGuid()}",
                    AutorSujeito: "agent-dev-1",
                    AutorEmissor: "https://auth.sentinel.dev",
                    ClienteId: "claude-code"
                );

                try
                {
                    var res = await _repo.ProporDecisaoAsync(cmd);
                    return McpToolResult.Success(res, "Success");
                }
                catch (ExcecaoDeConflitoDeVersao ex)
                {
                    return McpToolResult.Error(ex.Codigo, ex.Message, new { topicId = ex.TopicoId, expectedVersion = ex.VersaoEsperada, actualVersion = ex.VersaoAtual });
                }
            }));
        }

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => !r.IsError);
        var conflictCount = results.Count(r => r.IsError &&
            JsonSerializer.Serialize(r.StructuredContent).Contains("VERSION_CONFLICT"));

        Assert.Equal(1, successCount);
        Assert.Equal(9, conflictCount);

        var topic = await _repo.ObterTopicoPorChaveAsync(_wsA, _projA, topicKey);
        Assert.NotNull(topic);
        Assert.Equal(1, topic.VersaoAtual);

        var events = await _repo.ObterEventosPorFluxoAsync(_wsA, topic.TopicoId);
        Assert.Single(events);
    }

    [Fact]
    public async Task AC03_IndependenceBetweenTopics_TenTopicsSucceedInParallel()
    {
        var tasks = new List<Task<ResultadoProporDecisao>>();

        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var topicKey = $"independent-topic-{Guid.NewGuid():N}";
                var cmd = new ComandoProporDecisao(
                    EspacoTrabalhoId: _wsA,
                    ProjetoId: _projA,
                    ChaveTopico: topicKey,
                    VersaoEsperada: 0,
                    Titulo: $"Independent Topic {idx}",
                    Resumo: "Independent topic summary",
                    Justificativa: "AC-03 test",
                    ChavesTecnicas: [$"tech-{idx}"],
                    IncidentesRelacionadosIds: [],
                    Evidencias: [new Evidencia("pull_request", "PR 1", "https://example.com/pr/1")],
                    ChaveIdempotencia: $"key-{Guid.NewGuid()}",
                    AutorSujeito: "agent-dev-1",
                    AutorEmissor: "https://auth.sentinel.dev",
                    ClienteId: "claude-code"
                );

                return await _repo.ProporDecisaoAsync(cmd);
            }));
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.Equal(1, r.VersaoFluxo));
    }

    [Fact]
    public async Task AC04_IdempotencyAndUncertainCommit_HandlesRetriesAndReusedKeys()
    {
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";
        var topicKey = $"idem-topic-{Guid.NewGuid():N}";

        var cmd = new ComandoProporDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            ChaveTopico: topicKey,
            VersaoEsperada: 0,
            Titulo: "Original Proposal",
            Resumo: "Original Summary",
            Justificativa: "AC-04 test",
            ChavesTecnicas: ["BillingService"],
            IncidentesRelacionadosIds: [],
            Evidencias: [new Evidencia("pull_request", "PR 1", "https://example.com/pr/1")],
            ChaveIdempotencia: idempotencyKey,
            AutorSujeito: "agent-dev-1",
            AutorEmissor: "https://auth.sentinel.dev",
            ClienteId: "claude-code"
        );

        var result1 = await _repo.ProporDecisaoAsync(cmd);
        Assert.Equal(1, result1.VersaoFluxo);

        var result2 = await _repo.ProporDecisaoAsync(cmd);
        Assert.Equal(result1.PropostaId, result2.PropostaId);
        Assert.Equal(result1.VersaoFluxo, result2.VersaoFluxo);

        var tamperedCmd = cmd with { Titulo = "Modified Title" };
        await Assert.ThrowsAsync<ExcecaoDeChaveIdempotenciaReutilizada>(() => _repo.ProporDecisaoAsync(tamperedCmd));

        var events = await _repo.ObterEventosPorFluxoAsync(_wsA, result1.TopicoId);
        Assert.Single(events);
    }

    [Fact]
    public async Task AC05_IsolationAndWebReviewSeparation_CannotCrossWorkspacesOrApproveWithMcpToken()
    {
        var crossReq = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 10,
                method = "tools/call",
                @params = new
                {
                    name = "query_context",
                    arguments = new
                    {
                        workspaceId = _wsB,
                        projectId = _projB,
                        query = "audit"
                    }
                }
            })
        };
        crossReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mock:https://auth.sentinel.dev:agent-dev-1:Contributor:read:memory:claude-code");

        var crossResp = await _client.SendAsync(crossReq);
        crossResp.EnsureSuccessStatusCode();

        var body = await crossResp.Content.ReadFromJsonAsync<JsonRpcResponse>();
        Assert.NotNull(body);
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(body.Result));
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
        Assert.Equal("UNAUTHORIZED", doc.RootElement.GetProperty("structuredContent").GetProperty("code").GetString());

        var approveReq = new HttpRequestMessage(HttpMethod.Post, "/api/review/approve")
        {
            Content = JsonContent.Create(new
            {
                workspaceId = _wsA,
                projectId = _projA,
                topicId = Guid.NewGuid(),
                proposalId = Guid.NewGuid(),
                expectedVersion = 1,
                idempotencyKey = "key"
            })
        };
        approveReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mock:https://auth.sentinel.dev:agent-dev-1:Maintainer:write:decision:claude-code");
        approveReq.Headers.Add("X-CSRF-Token", "valid_csrf");

        var approveResp = await _client.SendAsync(approveReq);
        Assert.Equal(HttpStatusCode.Forbidden, approveResp.StatusCode);
    }

    [Fact]
    public async Task AC06_ValidityAndHistory_ProposeApproveSupersedeTimelineAndReceipts()
    {
        var topicKey = "lifecycle-" + Guid.NewGuid().ToString("N")[..8];

        var prop1 = await _repo.ProporDecisaoAsync(new ComandoProporDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            ChaveTopico: topicKey,
            VersaoEsperada: 0,
            Titulo: "Decision Version 1",
            Resumo: "Summary V1",
            Justificativa: "Rationale V1",
            ChavesTecnicas: ["LifecycleV1"],
            IncidentesRelacionadosIds: [],
            Evidencias: [new Evidencia("pull_request", "PR 1", "https://example.com/pr/1")],
            ChaveIdempotencia: $"prop1-{Guid.NewGuid()}",
            AutorSujeito: "agent-dev-1",
            AutorEmissor: "https://auth.sentinel.dev",
            ClienteId: "claude-code"
        ));
        Assert.Equal(1, prop1.VersaoFluxo);

        var app1 = await _repo.AprovarDecisaoAsync(new ComandoAprovarDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            TopicoId: prop1.TopicoId,
            PropostaId: prop1.PropostaId,
            VersaoEsperada: 1,
            ChaveIdempotencia: $"app1-{Guid.NewGuid()}",
            RevisorSujeito: "maintainer-alice",
            RevisorEmissor: "https://auth.sentinel.dev"
        ));
        Assert.Equal(2, app1.VersaoFluxo);

        var query1 = await _repo.ConsultarContextoAsync(new ConsultaContexto(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            TermoBusca: "LifecycleV1",
            TipoRegistro: "decision",
            PrincipalId: "agent-dev-1"
        ));
        Assert.Single(query1.Itens);
        Assert.Equal("Decision Version 1", query1.Itens[0].Titulo);
        Assert.Equal(2, query1.Itens[0].VersaoFluxo);

        var prop2 = await _repo.ProporDecisaoAsync(new ComandoProporDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            ChaveTopico: topicKey,
            VersaoEsperada: 2,
            Titulo: "Decision Version 2 Superseding V1",
            Resumo: "Summary V2",
            Justificativa: "Rationale V2",
            ChavesTecnicas: ["LifecycleV2"],
            IncidentesRelacionadosIds: [],
            Evidencias: [new Evidencia("pull_request", "PR 2", "https://example.com/pr/2")],
            ChaveIdempotencia: $"prop2-{Guid.NewGuid()}",
            AutorSujeito: "agent-dev-1",
            AutorEmissor: "https://auth.sentinel.dev",
            ClienteId: "claude-code"
        ));
        Assert.Equal(3, prop2.VersaoFluxo);

        var app2 = await _repo.AprovarDecisaoAsync(new ComandoAprovarDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            TopicoId: prop1.TopicoId,
            PropostaId: prop2.PropostaId,
            VersaoEsperada: 3,
            ChaveIdempotencia: $"app2-{Guid.NewGuid()}",
            RevisorSujeito: "maintainer-alice",
            RevisorEmissor: "https://auth.sentinel.dev"
        ));
        Assert.Equal(4, app2.VersaoFluxo);

        var query2 = await _repo.ConsultarContextoAsync(new ConsultaContexto(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            TermoBusca: "LifecycleV2",
            TipoRegistro: "decision",
            PrincipalId: "agent-dev-1"
        ));
        Assert.Single(query2.Itens);
        Assert.Equal("Decision Version 2 Superseding V1", query2.Itens[0].Titulo);
        Assert.Equal(4, query2.Itens[0].VersaoFluxo);

        var events = await _repo.ObterEventosPorFluxoAsync(_wsA, prop1.TopicoId);
        var stateAtV2 = RedutorDeDecisao.Reconstituir(
            prop1.TopicoId, _wsA, _projA, topicKey, events,
            pid => _repo.ObterCargaAsync(_wsA, pid).GetAwaiter().GetResult(),
            naVersao: 2
        );
        Assert.Equal("Decision Version 1", stateAtV2.Titulo);
        Assert.Equal(2, stateAtV2.VersaoFluxo);
        Assert.Equal(StatusDecisao.Active, stateAtV2.Situacao);

        var pastReceipt = await _repo.ObterComprovanteContextoAsync(_wsA, _projA, query1.ConsultaId, "agent-dev-1");
        Assert.Single(pastReceipt.Itens);
        Assert.Equal(1, pastReceipt.Itens[0].VersaoRegistro);
        Assert.Equal(2, pastReceipt.Itens[0].VersaoFluxo);
    }

    [Fact]
    public async Task AC07_QualityAndLatency_ExactTechnicalKeyMatchPrioritized()
    {
        var keyTopic = "key-match-" + Guid.NewGuid().ToString("N")[..8];
        var textTopic = "text-match-" + Guid.NewGuid().ToString("N")[..8];

        var prop1 = await _repo.ProporDecisaoAsync(new ComandoProporDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            ChaveTopico: keyTopic,
            VersaoEsperada: 0,
            Titulo: "Generic Title",
            Resumo: "Short summary",
            Justificativa: null,
            ChavesTecnicas: ["TARGET_CONFIG_PARAM"],
            IncidentesRelacionadosIds: [],
            Evidencias: [new Evidencia("pull_request", "PR 1", "https://example.com/pr/1")],
            ChaveIdempotencia: $"k-{Guid.NewGuid()}",
            AutorSujeito: "agent-dev-1",
            AutorEmissor: "https://auth.sentinel.dev",
            ClienteId: "claude-code"
        ));
        await _repo.AprovarDecisaoAsync(new ComandoAprovarDecisao(
            _wsA, _projA, prop1.TopicoId, prop1.PropostaId, 1, $"app-{Guid.NewGuid()}", "maintainer-alice", "https://auth.sentinel.dev"));

        var prop2 = await _repo.ProporDecisaoAsync(new ComandoProporDecisao(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            ChaveTopico: textTopic,
            VersaoEsperada: 0,
            Titulo: "Discussion mentioning TARGET_CONFIG_PARAM in prose",
            Resumo: "Longer text talking about TARGET_CONFIG_PARAM repeatedly",
            Justificativa: "Additional text",
            ChavesTecnicas: ["OTHER_KEY"],
            IncidentesRelacionadosIds: [],
            Evidencias: [new Evidencia("pull_request", "PR 2", "https://example.com/pr/2")],
            ChaveIdempotencia: $"t-{Guid.NewGuid()}",
            AutorSujeito: "agent-dev-1",
            AutorEmissor: "https://auth.sentinel.dev",
            ClienteId: "claude-code"
        ));
        await _repo.AprovarDecisaoAsync(new ComandoAprovarDecisao(
            _wsA, _projA, prop2.TopicoId, prop2.PropostaId, 1, $"app2-{Guid.NewGuid()}", "maintainer-alice", "https://auth.sentinel.dev"));

        var result = await _repo.ConsultarContextoAsync(new ConsultaContexto(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            TermoBusca: "TARGET_CONFIG_PARAM",
            TipoRegistro: "decision",
            PrincipalId: "agent-dev-1"
        ));

        Assert.NotEmpty(result.Itens);
        Assert.Equal(prop1.TopicoId, result.Itens[0].TopicoId);
    }

    [Fact]
    public async Task AC08_RecoveryAndReproduction_RebuildProjectionsRestoresConsistentState()
    {
        await _repo.ReconstruirProjecoesAsync(_wsA);

        var query = await _repo.ConsultarContextoAsync(new ConsultaContexto(
            EspacoTrabalhoId: _wsA,
            ProjetoId: _projA,
            TermoBusca: "Idempotência",
            TipoRegistro: "decision",
            PrincipalId: "agent-dev-1"
        ));

        Assert.NotEmpty(query.Itens);
        var activeDec = query.Itens.First(i => i.Titulo.Contains("Idempotência Obrigatória"));
        Assert.Equal("active", activeDec.Situacao);
        Assert.NotEmpty(activeDec.Evidencias);
    }
}
