using System.Collections.Concurrent;
using System.Text.Json;
using Sentinel.Core.Commands;
using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Core.Queries;
using Sentinel.Core.Reducers;
using Sentinel.Core.Validation;
using Sentinel.Persistence.Common;
using Sentinel.Persistence.Contracts;

namespace Sentinel.Persistence.InMemory;

public sealed class InMemorySentinelRepository :
    IWorkspaceRepository,
    IDecisionRepository,
    IIncidentRepository,
    IContextRepository,
    IReceiptRepository,
    IProjectionRebuilder
{
    private readonly ConcurrentDictionary<Guid, Workspace> _workspaces = new();
    private readonly ConcurrentDictionary<Guid, Project> _projects = new();
    private readonly ConcurrentDictionary<string, Membership> _memberships = new(); // key: wsId:issuer:subject
    private readonly ConcurrentDictionary<Guid, DecisionTopic> _decisionTopics = new();
    private readonly ConcurrentDictionary<Guid, IncidentStream> _incidentStreams = new();
    private readonly List<MemoryEvent> _memoryEvents = [];
    private readonly ConcurrentDictionary<Guid, MemoryPayload> _memoryPayloads = new();
    private readonly ConcurrentDictionary<Guid, DecisionCurrent> _decisionCurrent = new();
    private readonly ConcurrentDictionary<Guid, IncidentCurrent> _incidentCurrent = new();
    private readonly ConcurrentDictionary<string, CommandReceipt> _commandReceipts = new(); // key: wsId:principalId:op:key
    private readonly ConcurrentDictionary<Guid, ContextReceipt> _contextReceipts = new();
    private readonly ConcurrentDictionary<Guid, List<ContextReceiptItem>> _contextReceiptItems = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _topicLocks = new();
    private readonly object _globalLock = new();

    public InMemorySentinelRepository()
    {
    }

    private SemaphoreSlim GetTopicLock(Guid workspaceId, Guid projectId, string topicKey)
    {
        var key = $"{workspaceId}:{projectId}:{topicKey.ToLowerInvariant()}";
        return _topicLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    #region Seeding & Setup

    public void SeedWorkspace(Workspace ws) => _workspaces[ws.WorkspaceId] = ws;
    public void SeedProject(Project p) => _projects[p.ProjectId] = p;
    public void SeedMembership(Membership m)
    {
        var key = $"{m.WorkspaceId}:{m.Issuer}:{m.Subject}";
        _memberships[key] = m;
    }

    public void SeedDemo()
    {
        var ws1 = new Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Engineering Core", "eng-core", DateTimeOffset.UtcNow);
        var ws2 = new Workspace(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Security Isolated", "sec-isolated", DateTimeOffset.UtcNow);
        SeedWorkspace(ws1);
        SeedWorkspace(ws2);

        var p1 = new Project(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ws1.WorkspaceId, "Payments & Billing", "payments-billing", DateTimeOffset.UtcNow);
        var p2 = new Project(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ws2.WorkspaceId, "Audit Logs", "audit-logs", DateTimeOffset.UtcNow);
        SeedProject(p1);
        SeedProject(p2);

        SeedMembership(new Membership(Guid.NewGuid(), ws1.WorkspaceId, "https://auth.sentinel.dev", "agent-dev-1", UserRole.Contributor, true, DateTimeOffset.UtcNow));
        SeedMembership(new Membership(Guid.NewGuid(), ws1.WorkspaceId, "https://auth.sentinel.dev", "maintainer-alice", UserRole.Maintainer, true, DateTimeOffset.UtcNow));
        SeedMembership(new Membership(Guid.NewGuid(), ws1.WorkspaceId, "https://auth.sentinel.dev", "reader-bob", UserRole.Reader, true, DateTimeOffset.UtcNow));
        SeedMembership(new Membership(Guid.NewGuid(), ws2.WorkspaceId, "https://auth.sentinel.dev", "sec-agent-1", UserRole.Contributor, true, DateTimeOffset.UtcNow));
        SeedMembership(new Membership(Guid.NewGuid(), ws2.WorkspaceId, "https://auth.sentinel.dev", "sec-maintainer", UserRole.Maintainer, true, DateTimeOffset.UtcNow));

        // Seed Incident: duplicate billing
        var incidentId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var incidentPayloadId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        _incidentStreams[incidentId] = new IncidentStream(incidentId, ws1.WorkspaceId, p1.ProjectId, 1, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(-7));
        
        var incidentPayload = new MemoryPayload(
            incidentPayloadId, ws1.WorkspaceId, p1.ProjectId,
            "Duplicação de cobrança em retentativas no gateway de pagamento",
            "Clientes sofreram cobrança duplicada após timeouts parciais no webhook do gateway externo.",
            "O webhook do gateway externo falhou com timeout de rede e realizou nova tentativa de entrega sem que a transação anterior tivesse sido reconciliada com chave única no banco.",
            ["BillingService", "WebhookController", "charge_attempts", "ERR_DUPLICATE_CHARGE"],
            [],
            [
                new Evidence("incident_rca", "RCA Cobrança Duplicada 2026-10", "https://example.com/rca/billing-dup-202610"),
                new Evidence("pull_request", "PR Correção da duplicação", "https://example.com/pull/42")
            ],
            DateTimeOffset.UtcNow.AddDays(-7)
        );
        _memoryPayloads[incidentPayloadId] = incidentPayload;

        var incidentEvent = new MemoryEvent(
            Guid.NewGuid(), ws1.WorkspaceId, p1.ProjectId, incidentId, 1, "IncidentReported", 1,
            DateTimeOffset.UtcNow.AddDays(-7), "agent-dev-1", "https://auth.sentinel.dev", "claude-code", incidentPayloadId, null
        );
        _memoryEvents.Add(incidentEvent);

        _incidentCurrent[incidentId] = new IncidentCurrent(
            incidentId, ws1.WorkspaceId, p1.ProjectId, 1, 1, IncidentStatus.Reported,
            incidentPayload.Title, incidentPayload.Summary, incidentPayload.CreatedAt,
            incidentPayload.TechnicalKeys, incidentPayload.Evidence, incidentPayload.CreatedAt
        );

        // Seed Decision: payment-idempotency (Active, stream version 2)
        var topicId = Guid.Parse("c0c58a62-e095-4baf-9b56-69de5fa339ab");
        var decisionPayloadId = Guid.Parse("ed377b6a-cfdb-4c94-a51a-d0a0f66a247a");
        _decisionTopics[topicId] = new DecisionTopic(topicId, ws1.WorkspaceId, p1.ProjectId, "payment-idempotency", 2, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(-4));

        var decisionPayload = new MemoryPayload(
            decisionPayloadId, ws1.WorkspaceId, p1.ProjectId,
            "Idempotência Obrigatória para Cobranças",
            "Usar uma chave de idempotência por tentativa lógica de cobrança com persistência transacional.",
            "Evitar cobranças duplicadas em timeouts do gateway e garantir retentativas seguras por agentes e serviços.",
            ["BillingService", "payment-idempotency", "IdempotencyKey", "charge_attempts"],
            [incidentId],
            [
                new Evidence("pull_request", "Correção da cobrança duplicada", "https://example.com/pull/42"),
                new Evidence("adr", "ADR 042 Idempotência de Pagamento", "https://example.com/adr/042-payment-idempotency")
            ],
            DateTimeOffset.UtcNow.AddDays(-5)
        );
        _memoryPayloads[decisionPayloadId] = decisionPayload;

        _memoryEvents.Add(new MemoryEvent(
            Guid.NewGuid(), ws1.WorkspaceId, p1.ProjectId, topicId, 1, "DecisionProposed", 1,
            DateTimeOffset.UtcNow.AddDays(-5), "agent-dev-1", "https://auth.sentinel.dev", "claude-code", decisionPayloadId, null
        ));
        _memoryEvents.Add(new MemoryEvent(
            Guid.NewGuid(), ws1.WorkspaceId, p1.ProjectId, topicId, 2, "DecisionApproved", 1,
            DateTimeOffset.UtcNow.AddDays(-4), "maintainer-alice", "https://auth.sentinel.dev", "web-session", decisionPayloadId, null
        ));

        _decisionCurrent[topicId] = new DecisionCurrent(
            topicId, ws1.WorkspaceId, p1.ProjectId, "payment-idempotency",
            decisionPayloadId, 2, 2, DecisionStatus.Active, null,
            decisionPayload.Title, decisionPayload.Summary, decisionPayload.Rationale,
            decisionPayload.TechnicalKeys, decisionPayload.RelatedIncidentIds, decisionPayload.Evidence,
            DateTimeOffset.UtcNow.AddDays(-4)
        );
    }

    #endregion

    #region IWorkspaceRepository

    public Task<Workspace?> GetWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        _workspaces.TryGetValue(workspaceId, out var ws);
        return Task.FromResult(ws);
    }

    public Task<Membership?> GetMembershipAsync(Guid workspaceId, string issuer, string subject, CancellationToken ct = default)
    {
        var key = $"{workspaceId}:{issuer}:{subject}";
        _memberships.TryGetValue(key, out var m);
        return Task.FromResult(m);
    }

    public Task<Project?> GetProjectAsync(Guid workspaceId, Guid projectId, CancellationToken ct = default)
    {
        if (_projects.TryGetValue(projectId, out var p) && p.WorkspaceId == workspaceId)
            return Task.FromResult<Project?>(p);
        return Task.FromResult<Project?>(null);
    }

    public Task<List<Project>> GetProjectsByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var list = _projects.Values.Where(p => p.WorkspaceId == workspaceId).ToList();
        return Task.FromResult(list);
    }

    #endregion

    #region IDecisionRepository

    public Task<DecisionTopic?> GetTopicByKeyAsync(Guid workspaceId, Guid projectId, string topicKey, CancellationToken ct = default)
    {
        var topic = _decisionTopics.Values.FirstOrDefault(t =>
            t.WorkspaceId == workspaceId &&
            t.ProjectId == projectId &&
            string.Equals(t.TopicKey, topicKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(topic);
    }

    public Task<DecisionTopic?> GetTopicByIdAsync(Guid workspaceId, Guid projectId, Guid topicId, CancellationToken ct = default)
    {
        if (_decisionTopics.TryGetValue(topicId, out var topic) &&
            topic.WorkspaceId == workspaceId &&
            topic.ProjectId == projectId)
        {
            return Task.FromResult<DecisionTopic?>(topic);
        }
        return Task.FromResult<DecisionTopic?>(null);
    }

    public Task<DecisionCurrent?> GetDecisionCurrentAsync(Guid workspaceId, Guid projectId, Guid topicId, CancellationToken ct = default)
    {
        if (_decisionCurrent.TryGetValue(topicId, out var current) &&
            current.WorkspaceId == workspaceId &&
            current.ProjectId == projectId)
        {
            return Task.FromResult<DecisionCurrent?>(current);
        }
        return Task.FromResult<DecisionCurrent?>(null);
    }

    public Task<List<DecisionCurrent>> GetPendingProposalsAsync(Guid workspaceId, Guid? projectId = null, CancellationToken ct = default)
    {
        var list = _decisionCurrent.Values
            .Where(d => d.WorkspaceId == workspaceId &&
                        (!projectId.HasValue || d.ProjectId == projectId.Value) &&
                        (d.Status == DecisionStatus.Proposed || d.PendingProposalId.HasValue))
            .OrderByDescending(d => d.UpdatedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<List<MemoryEvent>> GetEventsByStreamAsync(Guid workspaceId, Guid streamId, int? atVersion = null, int? beforeVersion = null, int limit = 50, CancellationToken ct = default)
    {
        lock (_globalLock)
        {
            var query = _memoryEvents.Where(e => e.WorkspaceId == workspaceId && e.StreamId == streamId);
            if (atVersion.HasValue)
                query = query.Where(e => e.StreamVersion <= atVersion.Value);
            if (beforeVersion.HasValue)
                query = query.Where(e => e.StreamVersion < beforeVersion.Value);

            var list = query
                .OrderByDescending(e => e.StreamVersion)
                .Take(limit)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<MemoryPayload?> GetPayloadAsync(Guid workspaceId, Guid payloadId, CancellationToken ct = default)
    {
        if (_memoryPayloads.TryGetValue(payloadId, out var payload) && payload.WorkspaceId == workspaceId)
            return Task.FromResult<MemoryPayload?>(payload);
        return Task.FromResult<MemoryPayload?>(null);
    }

    public async Task<ProposeDecisionResult> ProposeDecisionAsync(ProposeDecisionCommand command, CancellationToken ct = default)
    {
        // 1. Invariants validation
        InvariantsValidator.ValidateTopicKey(command.TopicKey);
        InvariantsValidator.ValidateTitle(command.Title);
        InvariantsValidator.ValidateSummary(command.Summary);
        InvariantsValidator.ValidateRationale(command.Rationale);
        InvariantsValidator.ValidateTechnicalKeys(command.TechnicalKeys);
        InvariantsValidator.ValidateRelatedIncidentIds(command.RelatedIncidentIds);
        InvariantsValidator.ValidateEvidence(command.Evidence, required: true);
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        var topicLock = GetTopicLock(command.WorkspaceId, command.ProjectId, command.TopicKey);
        await topicLock.WaitAsync(ct);
        try
        {
            // 2. Check Idempotency
            var receiptKey = $"{command.WorkspaceId}:{command.AuthorSubject}:propose_decision:{command.IdempotencyKey}";
            var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
            {
                command.WorkspaceId,
                command.ProjectId,
                command.TopicKey,
                command.ExpectedVersion,
                command.Title,
                command.Summary,
                command.Rationale,
                command.TechnicalKeys,
                command.RelatedIncidentIds,
                command.Evidence
            });

            if (_commandReceipts.TryGetValue(receiptKey, out var existingReceipt))
            {
                if (existingReceipt.ArgsHash == currentArgsFingerprint)
                {
                    var cached = JsonSerializer.Deserialize<ProposeDecisionResult>(existingReceipt.ResultJson);
                    return cached!;
                }
                throw new IdempotencyKeyReusedException();
            }

            // 3. Concurrency & ExpectedVersion check
            var existingTopic = _decisionTopics.Values.FirstOrDefault(t =>
                t.WorkspaceId == command.WorkspaceId &&
                t.ProjectId == command.ProjectId &&
                string.Equals(t.TopicKey, command.TopicKey, StringComparison.OrdinalIgnoreCase));

            int currentVersion = existingTopic?.CurrentVersion ?? 0;
            if (currentVersion != command.ExpectedVersion)
            {
                throw new VersionConflictException(existingTopic?.TopicId, command.ExpectedVersion, currentVersion);
            }

            var topicId = existingTopic?.TopicId ?? Guid.NewGuid();
            int newVersion = currentVersion + 1;
            var now = DateTimeOffset.UtcNow;

            var newTopic = new DecisionTopic(
                topicId, command.WorkspaceId, command.ProjectId, command.TopicKey,
                newVersion, existingTopic?.CreatedAt ?? now, now
            );

            var proposalId = Guid.NewGuid();
            var payload = new MemoryPayload(
                proposalId, command.WorkspaceId, command.ProjectId,
                command.Title, command.Summary, command.Rationale,
                command.TechnicalKeys ?? [], command.RelatedIncidentIds ?? [], command.Evidence,
                now
            );

            var memoryEvent = new MemoryEvent(
                Guid.NewGuid(), command.WorkspaceId, command.ProjectId, topicId, newVersion,
                "DecisionProposed", 1, now, command.AuthorSubject, command.AuthorIssuer, command.ClientId,
                proposalId, null
            );

            // Update current projection
            _decisionCurrent.TryGetValue(topicId, out var existingCurrent);
            DecisionStatus newStatus = existingCurrent != null && existingCurrent.Status == DecisionStatus.Active
                ? DecisionStatus.Active
                : DecisionStatus.Proposed;

            var updatedCurrent = new DecisionCurrent(
                topicId, command.WorkspaceId, command.ProjectId, command.TopicKey,
                existingCurrent?.ActiveRecordId, existingCurrent?.ActiveRecordVersion,
                newVersion, newStatus, proposalId,
                existingCurrent != null && existingCurrent.Status == DecisionStatus.Active ? existingCurrent.Title : command.Title,
                existingCurrent != null && existingCurrent.Status == DecisionStatus.Active ? existingCurrent.Summary : command.Summary,
                existingCurrent != null && existingCurrent.Status == DecisionStatus.Active ? existingCurrent.Rationale : command.Rationale,
                command.TechnicalKeys ?? existingCurrent?.TechnicalKeys ?? [],
                command.RelatedIncidentIds ?? existingCurrent?.RelatedIncidentIds ?? [],
                command.Evidence,
                now
            );

            var result = new ProposeDecisionResult(topicId, proposalId, newVersion, "proposed");
            var resultJson = JsonSerializer.Serialize(result);

            var receipt = new CommandReceipt(
                Guid.NewGuid(), command.WorkspaceId, command.AuthorSubject, "propose_decision",
                command.IdempotencyKey, currentArgsFingerprint, resultJson, now
            );

            // Commit atomically
            lock (_globalLock)
            {
                _decisionTopics[topicId] = newTopic;
                _memoryPayloads[proposalId] = payload;
                _memoryEvents.Add(memoryEvent);
                _decisionCurrent[topicId] = updatedCurrent;
                _commandReceipts[receiptKey] = receipt;
            }

            return result;
        }
        finally
        {
            topicLock.Release();
        }
    }

    public async Task<ApproveDecisionResult> ApproveDecisionAsync(ApproveDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        if (!_decisionTopics.TryGetValue(command.TopicId, out var topic) ||
            topic.WorkspaceId != command.WorkspaceId ||
            topic.ProjectId != command.ProjectId)
        {
            throw new NotFoundException($"Decision topic {command.TopicId} not found in workspace/project.");
        }

        var topicLock = GetTopicLock(command.WorkspaceId, command.ProjectId, topic.TopicKey);
        await topicLock.WaitAsync(ct);
        try
        {
            var receiptKey = $"{command.WorkspaceId}:{command.ReviewerSubject}:approve_decision:{command.IdempotencyKey}";
            var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
            {
                command.WorkspaceId,
                command.ProjectId,
                command.TopicId,
                command.ProposalId,
                command.ExpectedVersion
            });

            if (_commandReceipts.TryGetValue(receiptKey, out var existingReceipt))
            {
                if (existingReceipt.ArgsHash == currentArgsFingerprint)
                {
                    return JsonSerializer.Deserialize<ApproveDecisionResult>(existingReceipt.ResultJson)!;
                }
                throw new IdempotencyKeyReusedException();
            }

            if (topic.CurrentVersion != command.ExpectedVersion)
            {
                throw new VersionConflictException(topic.TopicId, command.ExpectedVersion, topic.CurrentVersion);
            }

            if (!_memoryPayloads.TryGetValue(command.ProposalId, out var proposalPayload) ||
                proposalPayload.WorkspaceId != command.WorkspaceId)
            {
                throw new NotFoundException($"Proposal payload {command.ProposalId} not found.");
            }

            int newVersion = topic.CurrentVersion + 1;
            var now = DateTimeOffset.UtcNow;

            var updatedTopic = topic with { CurrentVersion = newVersion, UpdatedAt = now };
            var memoryEvent = new MemoryEvent(
                Guid.NewGuid(), command.WorkspaceId, command.ProjectId, topic.TopicId, newVersion,
                "DecisionApproved", 1, now, command.ReviewerSubject, command.ReviewerIssuer, "web-session",
                command.ProposalId, null
            );

            var updatedCurrent = new DecisionCurrent(
                topic.TopicId, command.WorkspaceId, command.ProjectId, topic.TopicKey,
                command.ProposalId, newVersion, newVersion, DecisionStatus.Active, null,
                proposalPayload.Title, proposalPayload.Summary, proposalPayload.Rationale,
                proposalPayload.TechnicalKeys, proposalPayload.RelatedIncidentIds, proposalPayload.Evidence,
                now
            );

            var result = new ApproveDecisionResult(topic.TopicId, command.ProposalId, newVersion, "active");
            var receipt = new CommandReceipt(
                Guid.NewGuid(), command.WorkspaceId, command.ReviewerSubject, "approve_decision",
                command.IdempotencyKey, currentArgsFingerprint, JsonSerializer.Serialize(result), now
            );

            lock (_globalLock)
            {
                _decisionTopics[topic.TopicId] = updatedTopic;
                _memoryEvents.Add(memoryEvent);
                _decisionCurrent[topic.TopicId] = updatedCurrent;
                _commandReceipts[receiptKey] = receipt;
            }

            return result;
        }
        finally
        {
            topicLock.Release();
        }
    }

    public async Task<RejectDecisionResult> RejectDecisionAsync(RejectDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        if (!_decisionTopics.TryGetValue(command.TopicId, out var topic) ||
            topic.WorkspaceId != command.WorkspaceId ||
            topic.ProjectId != command.ProjectId)
        {
            throw new NotFoundException($"Decision topic {command.TopicId} not found.");
        }

        var topicLock = GetTopicLock(command.WorkspaceId, command.ProjectId, topic.TopicKey);
        await topicLock.WaitAsync(ct);
        try
        {
            var receiptKey = $"{command.WorkspaceId}:{command.ReviewerSubject}:reject_decision:{command.IdempotencyKey}";
            var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
            {
                command.WorkspaceId,
                command.ProjectId,
                command.TopicId,
                command.ProposalId,
                command.ExpectedVersion,
                command.Reason
            });

            if (_commandReceipts.TryGetValue(receiptKey, out var existingReceipt))
            {
                if (existingReceipt.ArgsHash == currentArgsFingerprint)
                {
                    return JsonSerializer.Deserialize<RejectDecisionResult>(existingReceipt.ResultJson)!;
                }
                throw new IdempotencyKeyReusedException();
            }

            if (topic.CurrentVersion != command.ExpectedVersion)
            {
                throw new VersionConflictException(topic.TopicId, command.ExpectedVersion, topic.CurrentVersion);
            }

            int newVersion = topic.CurrentVersion + 1;
            var now = DateTimeOffset.UtcNow;

            var updatedTopic = topic with { CurrentVersion = newVersion, UpdatedAt = now };
            var memoryEvent = new MemoryEvent(
                Guid.NewGuid(), command.WorkspaceId, command.ProjectId, topic.TopicId, newVersion,
                "DecisionRejected", 1, now, command.ReviewerSubject, command.ReviewerIssuer, "web-session",
                command.ProposalId, null
            );

            _decisionCurrent.TryGetValue(topic.TopicId, out var current);
            var newStatus = current != null && current.ActiveRecordId.HasValue
                ? DecisionStatus.Active
                : DecisionStatus.Rejected;

            var updatedCurrent = current != null
                ? current with { PendingProposalId = null, Status = newStatus, StreamVersion = newVersion, UpdatedAt = now }
                : new DecisionCurrent(
                    topic.TopicId, command.WorkspaceId, command.ProjectId, topic.TopicKey,
                    null, null, newVersion, DecisionStatus.Rejected, null,
                    string.Empty, string.Empty, command.Reason, [], [], [], now
                );

            var result = new RejectDecisionResult(topic.TopicId, newVersion, newStatus.ToString().ToLowerInvariant());
            var receipt = new CommandReceipt(
                Guid.NewGuid(), command.WorkspaceId, command.ReviewerSubject, "reject_decision",
                command.IdempotencyKey, currentArgsFingerprint, JsonSerializer.Serialize(result), now
            );

            lock (_globalLock)
            {
                _decisionTopics[topic.TopicId] = updatedTopic;
                _memoryEvents.Add(memoryEvent);
                _decisionCurrent[topic.TopicId] = updatedCurrent;
                _commandReceipts[receiptKey] = receipt;
            }

            return result;
        }
        finally
        {
            topicLock.Release();
        }
    }

    public async Task<RestoreDecisionResult> RestoreDecisionAsync(RestoreDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        if (!_decisionTopics.TryGetValue(command.TopicId, out var topic) ||
            topic.WorkspaceId != command.WorkspaceId ||
            topic.ProjectId != command.ProjectId)
        {
            throw new NotFoundException($"Decision topic {command.TopicId} not found.");
        }

        // Reconstitute state at version to restore
        var pastDecision = DecisionReducer.Reconstitute(
            topic.TopicId, topic.WorkspaceId, topic.ProjectId, topic.TopicKey,
            _memoryEvents.Where(e => e.StreamId == topic.TopicId),
            pid => _memoryPayloads.TryGetValue(pid, out var p) ? p : null,
            atVersion: command.VersionToRestore
        );

        if (string.IsNullOrEmpty(pastDecision.Title))
        {
            throw new NotFoundException($"Version {command.VersionToRestore} not found for restoration.");
        }

        // Submit as a proposal referencing past version
        var proposeCmd = new ProposeDecisionCommand(
            WorkspaceId: command.WorkspaceId,
            ProjectId: command.ProjectId,
            TopicKey: topic.TopicKey,
            ExpectedVersion: command.ExpectedVersion,
            Title: pastDecision.Title,
            Summary: pastDecision.Summary,
            Rationale: $"Restoration of version {command.VersionToRestore}. {pastDecision.Rationale}",
            TechnicalKeys: pastDecision.TechnicalKeys,
            RelatedIncidentIds: pastDecision.RelatedIncidentIds,
            Evidence: pastDecision.Evidence,
            IdempotencyKey: command.IdempotencyKey,
            AuthorSubject: command.ReviewerSubject,
            AuthorIssuer: command.ReviewerIssuer,
            ClientId: "web-session"
        );

        var proposeResult = await ProposeDecisionAsync(proposeCmd, ct);
        return new RestoreDecisionResult(proposeResult.TopicId, proposeResult.ProposalId, proposeResult.StreamVersion, "proposed");
    }

    #endregion

    #region IIncidentRepository

    public Task<IncidentStream?> GetIncidentByIdAsync(Guid workspaceId, Guid projectId, Guid incidentId, CancellationToken ct = default)
    {
        if (_incidentStreams.TryGetValue(incidentId, out var stream) &&
            stream.WorkspaceId == workspaceId &&
            stream.ProjectId == projectId)
        {
            return Task.FromResult<IncidentStream?>(stream);
        }
        return Task.FromResult<IncidentStream?>(null);
    }

    public Task<IncidentCurrent?> GetIncidentCurrentAsync(Guid workspaceId, Guid projectId, Guid incidentId, CancellationToken ct = default)
    {
        if (_incidentCurrent.TryGetValue(incidentId, out var current) &&
            current.WorkspaceId == workspaceId &&
            current.ProjectId == projectId)
        {
            return Task.FromResult<IncidentCurrent?>(current);
        }
        return Task.FromResult<IncidentCurrent?>(null);
    }

    public Task<LogIncidentResult> LogIncidentAsync(LogIncidentCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateTitle(command.Title);
        InvariantsValidator.ValidateSummary(command.Summary);
        InvariantsValidator.ValidateTechnicalKeys(command.TechnicalKeys);
        InvariantsValidator.ValidateEvidence(command.Evidence, required: true);
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);

        var receiptKey = $"{command.WorkspaceId}:{command.AuthorSubject}:log_incident:{command.IdempotencyKey}";
        var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
        {
            command.WorkspaceId,
            command.ProjectId,
            command.Title,
            command.Summary,
            command.OccurredAt,
            command.TechnicalKeys,
            command.Evidence
        });

        if (_commandReceipts.TryGetValue(receiptKey, out var existingReceipt))
        {
            if (existingReceipt.ArgsHash == currentArgsFingerprint)
            {
                return Task.FromResult(JsonSerializer.Deserialize<LogIncidentResult>(existingReceipt.ResultJson)!);
            }
            throw new IdempotencyKeyReusedException();
        }

        var incidentId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var occurredAt = command.OccurredAt ?? now;

        var stream = new IncidentStream(incidentId, command.WorkspaceId, command.ProjectId, 1, now, now);
        var payload = new MemoryPayload(
            payloadId, command.WorkspaceId, command.ProjectId,
            command.Title, command.Summary, null,
            command.TechnicalKeys ?? [], [], command.Evidence,
            now
        );
        var memoryEvent = new MemoryEvent(
            Guid.NewGuid(), command.WorkspaceId, command.ProjectId, incidentId, 1,
            "IncidentReported", 1, now, command.AuthorSubject, command.AuthorIssuer, command.ClientId,
            payloadId, null
        );
        var current = new IncidentCurrent(
            incidentId, command.WorkspaceId, command.ProjectId, 1, 1, IncidentStatus.Reported,
            command.Title, command.Summary, occurredAt, command.TechnicalKeys ?? [], command.Evidence, now
        );

        var result = new LogIncidentResult(incidentId, 1, "reported");
        var receipt = new CommandReceipt(
            Guid.NewGuid(), command.WorkspaceId, command.AuthorSubject, "log_incident",
            command.IdempotencyKey, currentArgsFingerprint, JsonSerializer.Serialize(result), now
        );

        lock (_globalLock)
        {
            _incidentStreams[incidentId] = stream;
            _memoryPayloads[payloadId] = payload;
            _memoryEvents.Add(memoryEvent);
            _incidentCurrent[incidentId] = current;
            _commandReceipts[receiptKey] = receipt;
        }

        return Task.FromResult(result);
    }

    #endregion

    #region IContextRepository

    public Task<QueryContextResult> QueryContextAsync(QueryContextQuery query, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateQuery(query.Query);
        InvariantsValidator.ValidateLimit(query.Limit, min: 1, max: 20);

        var queryTerms = query.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToList();

        var candidateItems = new List<(ContextItem Item, bool HasExactTechKeyMatch, int LexicalScore, DateTimeOffset Timestamp)>();

        if (string.Equals(query.Kind, "decision", StringComparison.OrdinalIgnoreCase))
        {
            var activeDecisions = _decisionCurrent.Values
                .Where(d => d.WorkspaceId == query.WorkspaceId &&
                            d.ProjectId == query.ProjectId &&
                            d.Status == DecisionStatus.Active);

            foreach (var d in activeDecisions)
            {
                bool hasExactTechMatch = d.TechnicalKeys.Any(k =>
                    queryTerms.Any(qt => string.Equals(k, qt, StringComparison.OrdinalIgnoreCase)) ||
                    query.Query.Contains(k, StringComparison.OrdinalIgnoreCase));

                int score = 0;
                var fullText = $"{d.Title} {d.Summary} {d.Rationale ?? ""} {d.TopicKey}".ToLowerInvariant();
                foreach (var term in queryTerms)
                {
                    if (fullText.Contains(term))
                        score += 10;
                }
                if (hasExactTechMatch) score += 100;

                if (score > 0 || queryTerms.Count == 0)
                {
                    var item = new ContextItem(
                        Kind: "decision",
                        TopicId: d.TopicId,
                        IncidentId: null,
                        RecordId: d.ActiveRecordId ?? d.TopicId,
                        RecordVersion: d.ActiveRecordVersion ?? d.StreamVersion,
                        StreamVersion: d.StreamVersion,
                        Status: "active",
                        Title: d.Title,
                        Summary: d.Summary,
                        Evidence: d.Evidence
                    );
                    candidateItems.Add((item, hasExactTechMatch, score, d.UpdatedAt));
                }
            }
        }
        else if (string.Equals(query.Kind, "incident", StringComparison.OrdinalIgnoreCase))
        {
            var reportedIncidents = _incidentCurrent.Values
                .Where(i => i.WorkspaceId == query.WorkspaceId &&
                            i.ProjectId == query.ProjectId &&
                            i.Status == IncidentStatus.Reported);

            foreach (var inc in reportedIncidents)
            {
                bool hasExactTechMatch = inc.TechnicalKeys.Any(k =>
                    queryTerms.Any(qt => string.Equals(k, qt, StringComparison.OrdinalIgnoreCase)) ||
                    query.Query.Contains(k, StringComparison.OrdinalIgnoreCase));

                int score = 0;
                var fullText = $"{inc.Title} {inc.Summary}".ToLowerInvariant();
                foreach (var term in queryTerms)
                {
                    if (fullText.Contains(term))
                        score += 10;
                }
                if (hasExactTechMatch) score += 100;

                if (score > 0 || queryTerms.Count == 0)
                {
                    var item = new ContextItem(
                        Kind: "incident",
                        TopicId: null,
                        IncidentId: inc.IncidentId,
                        RecordId: inc.IncidentId,
                        RecordVersion: inc.RecordVersion,
                        StreamVersion: inc.StreamVersion,
                        Status: "reported",
                        Title: inc.Title,
                        Summary: inc.Summary,
                        Evidence: inc.Evidence
                    );
                    candidateItems.Add((item, hasExactTechMatch, score, inc.OccurredAt));
                }
            }
        }

        // Deterministic sorting: Exact technical key match DESC, Lexical score DESC, Timestamp DESC, RecordId ASC
        var orderedItems = candidateItems
            .OrderByDescending(c => c.HasExactTechKeyMatch)
            .ThenByDescending(c => c.LexicalScore)
            .ThenByDescending(c => c.Timestamp)
            .ThenBy(c => c.Item.RecordId)
            .Select(c => c.Item)
            .Take(query.Limit)
            .ToList();

        var queryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var searchMode = "lexical";
        var matchStatus = orderedItems.Count > 0 ? "matched" : "no_match";

        // Save context receipt and items in same atomic operation
        var receiptItems = orderedItems.Select((item, idx) => new ContextReceiptItem(
            ReceiptItemId: Guid.NewGuid(),
            QueryId: queryId,
            WorkspaceId: query.WorkspaceId,
            Position: idx + 1,
            Kind: item.Kind,
            StreamId: item.TopicId ?? item.IncidentId ?? item.RecordId,
            RecordId: item.RecordId,
            RecordVersion: item.RecordVersion,
            StreamVersion: item.StreamVersion,
            Status: item.Status
        )).ToList();

        var contextReceipt = new ContextReceipt(
            QueryId: queryId,
            WorkspaceId: query.WorkspaceId,
            ProjectId: query.ProjectId,
            PrincipalId: query.PrincipalId,
            ClientId: query.ClientId,
            SearchMode: searchMode,
            CreatedAt: now,
            Items: receiptItems
        );

        lock (_globalLock)
        {
            _contextReceipts[queryId] = contextReceipt;
            _contextReceiptItems[queryId] = receiptItems;
        }

        return Task.FromResult(new QueryContextResult(
            QueryId: queryId,
            MatchStatus: matchStatus,
            SearchMode: searchMode,
            Items: orderedItems
        ));
    }

    public Task<GetContextReceiptResult> GetContextReceiptAsync(Guid workspaceId, Guid projectId, Guid queryId, string principalId, CancellationToken ct = default)
    {
        if (!_contextReceipts.TryGetValue(queryId, out var receipt) ||
            receipt.WorkspaceId != workspaceId ||
            receipt.ProjectId != projectId)
        {
            throw new NotFoundException($"Context receipt {queryId} not found in this workspace/project.");
        }

        _contextReceiptItems.TryGetValue(queryId, out var items);

        var result = new GetContextReceiptResult(
            QueryId: receipt.QueryId,
            WorkspaceId: receipt.WorkspaceId,
            ProjectId: receipt.ProjectId,
            PrincipalId: receipt.PrincipalId,
            ClientId: receipt.ClientId,
            SearchMode: receipt.SearchMode,
            CreatedAt: receipt.CreatedAt,
            Items: (items ?? []).OrderBy(i => i.Position).ToList()
        );
        return Task.FromResult(result);
    }

    #endregion

    #region IReceiptRepository

    public Task<CommandReceipt?> GetCommandReceiptAsync(Guid workspaceId, string principalId, string operation, string idempotencyKey, CancellationToken ct = default)
    {
        var key = $"{workspaceId}:{principalId}:{operation}:{idempotencyKey}";
        _commandReceipts.TryGetValue(key, out var receipt);
        return Task.FromResult(receipt);
    }

    public Task SaveCommandReceiptAsync(CommandReceipt receipt, CancellationToken ct = default)
    {
        var key = $"{receipt.WorkspaceId}:{receipt.PrincipalId}:{receipt.Operation}:{receipt.IdempotencyKey}";
        _commandReceipts[key] = receipt;
        return Task.CompletedTask;
    }

    #endregion

    #region IProjectionRebuilder

    public Task RebuildProjectionsAsync(Guid? workspaceId = null, CancellationToken ct = default)
    {
        lock (_globalLock)
        {
            // Rebuild Decision Current
            var decisionStreams = _decisionTopics.Values
                .Where(t => !workspaceId.HasValue || t.WorkspaceId == workspaceId.Value)
                .ToList();

            foreach (var topic in decisionStreams)
            {
                var events = _memoryEvents
                    .Where(e => e.StreamId == topic.TopicId)
                    .OrderBy(e => e.StreamVersion);

                var reconstituted = DecisionReducer.Reconstitute(
                    topic.TopicId, topic.WorkspaceId, topic.ProjectId, topic.TopicKey,
                    events,
                    pid => _memoryPayloads.TryGetValue(pid, out var p) ? p : null
                );

                var current = new DecisionCurrent(
                    topic.TopicId, topic.WorkspaceId, topic.ProjectId, topic.TopicKey,
                    reconstituted.ActiveRecordId, reconstituted.ActiveRecordVersion,
                    reconstituted.StreamVersion, reconstituted.Status, reconstituted.PendingProposalId,
                    reconstituted.Title, reconstituted.Summary, reconstituted.Rationale,
                    reconstituted.TechnicalKeys, reconstituted.RelatedIncidentIds, reconstituted.Evidence,
                    DateTimeOffset.UtcNow
                );

                _decisionCurrent[topic.TopicId] = current;
            }

            // Rebuild Incident Current
            var incidentStreams = _incidentStreams.Values
                .Where(i => !workspaceId.HasValue || i.WorkspaceId == workspaceId.Value)
                .ToList();

            foreach (var inc in incidentStreams)
            {
                var events = _memoryEvents
                    .Where(e => e.StreamId == inc.IncidentId)
                    .OrderBy(e => e.StreamVersion);

                var reconstituted = IncidentReducer.Reconstitute(
                    inc.IncidentId, inc.WorkspaceId, inc.ProjectId,
                    events,
                    pid => _memoryPayloads.TryGetValue(pid, out var p) ? p : null
                );

                var current = new IncidentCurrent(
                    inc.IncidentId, inc.WorkspaceId, inc.ProjectId,
                    reconstituted.RecordVersion, reconstituted.StreamVersion,
                    reconstituted.Status, reconstituted.Title, reconstituted.Summary,
                    reconstituted.OccurredAt, reconstituted.TechnicalKeys, reconstituted.Evidence,
                    DateTimeOffset.UtcNow
                );

                _incidentCurrent[inc.IncidentId] = current;
            }

            return Task.CompletedTask;
        }
    }

    #endregion
}
