using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Api.Mcp;
using Sentinel.Core.Commands;
using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Core.Queries;
using Sentinel.Core.Reducers;
using Sentinel.Persistence.Contracts;
using Sentinel.Persistence.InMemory;
using Xunit;

namespace Sentinel.IntegrationTests;

public class AcceptanceCriteriaTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly InMemorySentinelRepository _repo;

    private readonly Guid _wsA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _wsB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly Guid _projA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly Guid _projB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public AcceptanceCriteriaTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _repo = factory.Services.GetRequiredService<InMemorySentinelRepository>();
    }

    [Fact]
    public async Task AC01_Interoperability_HandshakeAndToolCallsWork()
    {
        // 1. initialize
        var initResp = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "Claude Code", version = "1.0" } }
        });
        initResp.EnsureSuccessStatusCode();

        // 2. tools/list
        var listResp = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        });
        listResp.EnsureSuccessStatusCode();

        // 3. tools/call query_context
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
                var cmd = new ProposeDecisionCommand(
                    WorkspaceId: _wsA,
                    ProjectId: _projA,
                    TopicKey: topicKey,
                    ExpectedVersion: 0,
                    Title: $"Decision proposal variant {idx}",
                    Summary: "Summary for concurrency test",
                    Rationale: "Testing AC-02 version conflict",
                    TechnicalKeys: ["BillingService"],
                    RelatedIncidentIds: [],
                    Evidence: [new Evidence("pull_request", "PR 42", "https://example.com/pr/42")],
                    IdempotencyKey: $"key-{Guid.NewGuid()}",
                    AuthorSubject: "agent-dev-1",
                    AuthorIssuer: "https://auth.sentinel.dev",
                    ClientId: "claude-code"
                );

                try
                {
                    var res = await _repo.ProposeDecisionAsync(cmd);
                    return McpToolResult.Success(res, "Success");
                }
                catch (VersionConflictException ex)
                {
                    return McpToolResult.Error(ex.Code, ex.Message, new { topicId = ex.TopicId, expectedVersion = ex.ExpectedVersion, actualVersion = ex.ActualVersion });
                }
            }));
        }

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => !r.IsError);
        var conflictCount = results.Count(r => r.IsError &&
            JsonSerializer.Serialize(r.StructuredContent).Contains("VERSION_CONFLICT"));

        Assert.Equal(1, successCount);
        Assert.Equal(9, conflictCount);

        var topic = await _repo.GetTopicByKeyAsync(_wsA, _projA, topicKey);
        Assert.NotNull(topic);
        Assert.Equal(1, topic.CurrentVersion);

        var events = await _repo.GetEventsByStreamAsync(_wsA, topic.TopicId);
        Assert.Single(events);
    }

    [Fact]
    public async Task AC03_IndependenceBetweenTopics_TenTopicsSucceedInParallel()
    {
        var tasks = new List<Task<ProposeDecisionResult>>();

        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var topicKey = $"independent-topic-{Guid.NewGuid():N}";
                var cmd = new ProposeDecisionCommand(
                    WorkspaceId: _wsA,
                    ProjectId: _projA,
                    TopicKey: topicKey,
                    ExpectedVersion: 0,
                    Title: $"Independent Topic {idx}",
                    Summary: "Independent topic summary",
                    Rationale: "AC-03 test",
                    TechnicalKeys: [$"tech-{idx}"],
                    RelatedIncidentIds: [],
                    Evidence: [new Evidence("pull_request", "PR 1", "https://example.com/pr/1")],
                    IdempotencyKey: $"key-{Guid.NewGuid()}",
                    AuthorSubject: "agent-dev-1",
                    AuthorIssuer: "https://auth.sentinel.dev",
                    ClientId: "claude-code"
                );

                return await _repo.ProposeDecisionAsync(cmd);
            }));
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.Equal(1, r.StreamVersion));
    }

    [Fact]
    public async Task AC04_IdempotencyAndUncertainCommit_HandlesRetriesAndReusedKeys()
    {
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";
        var topicKey = $"idem-topic-{Guid.NewGuid():N}";

        var cmd = new ProposeDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicKey: topicKey,
            ExpectedVersion: 0,
            Title: "Original Proposal",
            Summary: "Original Summary",
            Rationale: "AC-04 test",
            TechnicalKeys: ["BillingService"],
            RelatedIncidentIds: [],
            Evidence: [new Evidence("pull_request", "PR 1", "https://example.com/pr/1")],
            IdempotencyKey: idempotencyKey,
            AuthorSubject: "agent-dev-1",
            AuthorIssuer: "https://auth.sentinel.dev",
            ClientId: "claude-code"
        );

        // 1. Initial execution
        var result1 = await _repo.ProposeDecisionAsync(cmd);
        Assert.Equal(1, result1.StreamVersion);

        // 2. Retry with same arguments returns identical result
        var result2 = await _repo.ProposeDecisionAsync(cmd);
        Assert.Equal(result1.ProposalId, result2.ProposalId);
        Assert.Equal(result1.StreamVersion, result2.StreamVersion);

        // 3. Reusing same idempotencyKey with different arguments throws IDEMPOTENCY_KEY_REUSED
        var tamperedCmd = cmd with { Title = "Modified Title" };
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => _repo.ProposeDecisionAsync(tamperedCmd));

        // 4. Verify only 1 event was recorded
        var events = await _repo.GetEventsByStreamAsync(_wsA, result1.TopicId);
        Assert.Single(events);
    }

    [Fact]
    public async Task AC05_IsolationAndWebReviewSeparation_CannotCrossWorkspacesOrApproveWithMcpToken()
    {
        // 1. Querying Workspace B using Workspace A token
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
                        workspaceId = _wsB, // Workspace B
                        projectId = _projB,
                        query = "audit"
                    }
                }
            })
        };
        // Authenticated as agent in Workspace A only
        crossReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mock:https://auth.sentinel.dev:agent-dev-1:Contributor:read:memory:claude-code");

        var crossResp = await _client.SendAsync(crossReq);
        crossResp.EnsureSuccessStatusCode();

        var body = await crossResp.Content.ReadFromJsonAsync<JsonRpcResponse>();
        Assert.NotNull(body);
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(body.Result));
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
        Assert.Equal("UNAUTHORIZED", doc.RootElement.GetProperty("structuredContent").GetProperty("code").GetString());

        // 2. MCP Bearer token cannot approve on web review route (ADR-004)
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

        // 1. Propose V1
        var prop1 = await _repo.ProposeDecisionAsync(new ProposeDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicKey: topicKey,
            ExpectedVersion: 0,
            Title: "Decision Version 1",
            Summary: "Summary V1",
            Rationale: "Rationale V1",
            TechnicalKeys: ["LifecycleV1"],
            RelatedIncidentIds: [],
            Evidence: [new Evidence("pull_request", "PR 1", "https://example.com/pr/1")],
            IdempotencyKey: $"prop1-{Guid.NewGuid()}",
            AuthorSubject: "agent-dev-1",
            AuthorIssuer: "https://auth.sentinel.dev",
            ClientId: "claude-code"
        ));
        Assert.Equal(1, prop1.StreamVersion);

        // 2. Approve V1
        var app1 = await _repo.ApproveDecisionAsync(new ApproveDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicId: prop1.TopicId,
            ProposalId: prop1.ProposalId,
            ExpectedVersion: 1,
            IdempotencyKey: $"app1-{Guid.NewGuid()}",
            ReviewerSubject: "maintainer-alice",
            ReviewerIssuer: "https://auth.sentinel.dev"
        ));
        Assert.Equal(2, app1.StreamVersion);

        // 3. Query Context returns V1
        var query1 = await _repo.QueryContextAsync(new QueryContextQuery(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            Query: "LifecycleV1",
            Kind: "decision",
            PrincipalId: "agent-dev-1"
        ));
        Assert.Single(query1.Items);
        Assert.Equal("Decision Version 1", query1.Items[0].Title);
        Assert.Equal(2, query1.Items[0].StreamVersion);

        // 4. Propose V2
        var prop2 = await _repo.ProposeDecisionAsync(new ProposeDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicKey: topicKey,
            ExpectedVersion: 2,
            Title: "Decision Version 2 Superseding V1",
            Summary: "Summary V2",
            Rationale: "Rationale V2",
            TechnicalKeys: ["LifecycleV2"],
            RelatedIncidentIds: [],
            Evidence: [new Evidence("pull_request", "PR 2", "https://example.com/pr/2")],
            IdempotencyKey: $"prop2-{Guid.NewGuid()}",
            AuthorSubject: "agent-dev-1",
            AuthorIssuer: "https://auth.sentinel.dev",
            ClientId: "claude-code"
        ));
        Assert.Equal(3, prop2.StreamVersion);

        // 5. Approve V2
        var app2 = await _repo.ApproveDecisionAsync(new ApproveDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicId: prop1.TopicId,
            ProposalId: prop2.ProposalId,
            ExpectedVersion: 3,
            IdempotencyKey: $"app2-{Guid.NewGuid()}",
            ReviewerSubject: "maintainer-alice",
            ReviewerIssuer: "https://auth.sentinel.dev"
        ));
        Assert.Equal(4, app2.StreamVersion);

        // 6. Query Context now returns V2 as active
        var query2 = await _repo.QueryContextAsync(new QueryContextQuery(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            Query: "LifecycleV2",
            Kind: "decision",
            PrincipalId: "agent-dev-1"
        ));
        Assert.Single(query2.Items);
        Assert.Equal("Decision Version 2 Superseding V1", query2.Items[0].Title);
        Assert.Equal(4, query2.Items[0].StreamVersion);

        // 7. Timeline at version 2 reconstructs V1 as it was when active
        var events = await _repo.GetEventsByStreamAsync(_wsA, prop1.TopicId);
        var stateAtV2 = DecisionReducer.Reconstitute(
            prop1.TopicId, _wsA, _projA, topicKey, events,
            pid => _repo.GetPayloadAsync(_wsA, pid).GetAwaiter().GetResult(),
            atVersion: 2
        );
        Assert.Equal("Decision Version 1", stateAtV2.Title);
        Assert.Equal(2, stateAtV2.StreamVersion);
        Assert.Equal(DecisionStatus.Active, stateAtV2.Status);

        // 8. Old context receipt preserved
        var pastReceipt = await _repo.GetContextReceiptAsync(_wsA, _projA, query1.QueryId, "agent-dev-1");
        Assert.Single(pastReceipt.Items);
        Assert.Equal(2, pastReceipt.Items[0].RecordVersion);
        Assert.Equal(2, pastReceipt.Items[0].StreamVersion);
    }

    [Fact]
    public async Task AC07_QualityAndLatency_ExactTechnicalKeyMatchPrioritized()
    {
        // Topic with exact technical key match vs topic with general text match
        var keyTopic = "key-match-" + Guid.NewGuid().ToString("N")[..8];
        var textTopic = "text-match-" + Guid.NewGuid().ToString("N")[..8];

        var prop1 = await _repo.ProposeDecisionAsync(new ProposeDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicKey: keyTopic,
            ExpectedVersion: 0,
            Title: "Generic Title",
            Summary: "Short summary",
            Rationale: null,
            TechnicalKeys: ["TARGET_CONFIG_PARAM"],
            RelatedIncidentIds: [],
            Evidence: [new Evidence("pull_request", "PR 1", "https://example.com/pr/1")],
            IdempotencyKey: $"k-{Guid.NewGuid()}",
            AuthorSubject: "agent-dev-1",
            AuthorIssuer: "https://auth.sentinel.dev",
            ClientId: "claude-code"
        ));
        await _repo.ApproveDecisionAsync(new ApproveDecisionCommand(
            _wsA, _projA, prop1.TopicId, prop1.ProposalId, 1, $"app-{Guid.NewGuid()}", "maintainer-alice", "https://auth.sentinel.dev"));

        var prop2 = await _repo.ProposeDecisionAsync(new ProposeDecisionCommand(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            TopicKey: textTopic,
            ExpectedVersion: 0,
            Title: "Discussion mentioning TARGET_CONFIG_PARAM in prose",
            Summary: "Longer text talking about TARGET_CONFIG_PARAM repeatedly",
            Rationale: "Additional text",
            TechnicalKeys: ["OTHER_KEY"],
            RelatedIncidentIds: [],
            Evidence: [new Evidence("pull_request", "PR 2", "https://example.com/pr/2")],
            IdempotencyKey: $"t-{Guid.NewGuid()}",
            AuthorSubject: "agent-dev-1",
            AuthorIssuer: "https://auth.sentinel.dev",
            ClientId: "claude-code"
        ));
        await _repo.ApproveDecisionAsync(new ApproveDecisionCommand(
            _wsA, _projA, prop2.TopicId, prop2.ProposalId, 1, $"app2-{Guid.NewGuid()}", "maintainer-alice", "https://auth.sentinel.dev"));

        var result = await _repo.QueryContextAsync(new QueryContextQuery(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            Query: "TARGET_CONFIG_PARAM",
            Kind: "decision",
            PrincipalId: "agent-dev-1"
        ));

        Assert.NotEmpty(result.Items);
        // The topic with exact technical key match must be ranked 1st!
        Assert.Equal(prop1.TopicId, result.Items[0].TopicId);
    }

    [Fact]
    public async Task AC08_RecoveryAndReproduction_RebuildProjectionsRestoresConsistentState()
    {
        // 1. Rebuild projections from events
        await _repo.RebuildProjectionsAsync(_wsA);

        // 2. Query idempotency decision from seeded demo
        var query = await _repo.QueryContextAsync(new QueryContextQuery(
            WorkspaceId: _wsA,
            ProjectId: _projA,
            Query: "Idempotência",
            Kind: "decision",
            PrincipalId: "agent-dev-1"
        ));

        Assert.NotEmpty(query.Items);
        var activeDec = query.Items.First(i => i.Title.Contains("Idempotência Obrigatória"));
        Assert.Equal("active", activeDec.Status);
        Assert.NotEmpty(activeDec.Evidence);
    }
}
