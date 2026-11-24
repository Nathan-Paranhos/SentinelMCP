using System.Text.Json;
using Npgsql;
using Sentinel.Core.Commands;
using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Core.Queries;
using Sentinel.Core.Reducers;
using Sentinel.Core.Validation;
using Sentinel.Persistence.Common;
using Sentinel.Persistence.Contracts;

namespace Sentinel.Persistence.PostgreSql;

public sealed class PostgreSqlSentinelRepository :
    IWorkspaceRepository,
    IDecisionRepository,
    IIncidentRepository,
    IContextRepository,
    IReceiptRepository,
    IProjectionRebuilder
{
    private readonly string _connectionString;

    public PostgreSqlSentinelRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<NpgsqlConnection> OpenConnectionWithRlsAsync(Guid workspaceId, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{workspaceId}'", conn);
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    #region IWorkspaceRepository

    public async Task<Workspace?> GetWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT workspace_id, name, slug, created_at FROM workspaces WHERE workspace_id = @id", conn);
        cmd.Parameters.AddWithValue("id", workspaceId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new Workspace(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)
            );
        }
        return null;
    }

    public async Task<Membership?> GetMembershipAsync(Guid workspaceId, string issuer, string subject, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT membership_id, workspace_id, issuer, subject, role, is_active, created_at 
              FROM memberships 
              WHERE workspace_id = @ws AND issuer = @iss AND subject = @sub", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("iss", issuer);
        cmd.Parameters.AddWithValue("sub", subject);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var roleStr = reader.GetString(4);
            Enum.TryParse<UserRole>(roleStr, true, out var role);
            return new Membership(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                role,
                reader.GetBoolean(5),
                reader.GetFieldValue<DateTimeOffset>(6)
            );
        }
        return null;
    }

    public async Task<Project?> GetProjectAsync(Guid workspaceId, Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT project_id, workspace_id, name, slug, created_at FROM projects WHERE workspace_id = @ws AND project_id = @proj", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("proj", projectId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new Project(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)
            );
        }
        return null;
    }

    public async Task<List<Project>> GetProjectsByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT project_id, workspace_id, name, slug, created_at FROM projects WHERE workspace_id = @ws", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);

        var list = new List<Project>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Project(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)
            ));
        }
        return list;
    }

    #endregion

    #region IDecisionRepository

    public async Task<DecisionTopic?> GetTopicByKeyAsync(Guid workspaceId, Guid projectId, string topicKey, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_key = @key", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("proj", projectId);
        cmd.Parameters.AddWithValue("key", topicKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new DecisionTopic(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)
            );
        }
        return null;
    }

    public async Task<DecisionTopic?> GetTopicByIdAsync(Guid workspaceId, Guid projectId, Guid topicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("proj", projectId);
        cmd.Parameters.AddWithValue("id", topicId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new DecisionTopic(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)
            );
        }
        return null;
    }

    public async Task<DecisionCurrent?> GetDecisionCurrentAsync(Guid workspaceId, Guid projectId, Guid topicId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version, 
                     stream_version, status, pending_proposal_id, title, summary, rationale, 
                     technical_keys::text, related_incident_ids::text, evidence::text, updated_at 
              FROM decision_current 
              WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("proj", projectId);
        cmd.Parameters.AddWithValue("id", topicId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            Enum.TryParse<DecisionStatus>(reader.GetString(7), true, out var status);
            return new DecisionCurrent(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6), status,
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(12)) ?? [],
                JsonSerializer.Deserialize<List<Guid>>(reader.GetString(13)) ?? [],
                JsonSerializer.Deserialize<List<Evidence>>(reader.GetString(14)) ?? [],
                reader.GetFieldValue<DateTimeOffset>(15)
            );
        }
        return null;
    }

    public async Task<List<DecisionCurrent>> GetPendingProposalsAsync(Guid workspaceId, Guid? projectId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        var sql = @"SELECT topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version, 
                           stream_version, status, pending_proposal_id, title, summary, rationale, 
                           technical_keys::text, related_incident_ids::text, evidence::text, updated_at 
                    FROM decision_current 
                    WHERE workspace_id = @ws AND (status = 'proposed' OR pending_proposal_id IS NOT NULL)";
        if (projectId.HasValue)
            sql += " AND project_id = @proj";
        sql += " ORDER BY updated_at DESC";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("proj", projectId.Value);

        var list = new List<DecisionCurrent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            Enum.TryParse<DecisionStatus>(reader.GetString(7), true, out var status);
            list.Add(new DecisionCurrent(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6), status,
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(12)) ?? [],
                JsonSerializer.Deserialize<List<Guid>>(reader.GetString(13)) ?? [],
                JsonSerializer.Deserialize<List<Evidence>>(reader.GetString(14)) ?? [],
                reader.GetFieldValue<DateTimeOffset>(15)
            ));
        }
        return list;
    }

    public async Task<List<MemoryEvent>> GetEventsByStreamAsync(Guid workspaceId, Guid streamId, int? atVersion = null, int? beforeVersion = null, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        var sql = @"SELECT event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, 
                           recorded_at, author_subject, author_issuer, client_id, payload_id, ref_event_id 
                    FROM memory_events 
                    WHERE workspace_id = @ws AND stream_id = @stream";
        if (atVersion.HasValue) sql += " AND stream_version <= @atVer";
        if (beforeVersion.HasValue) sql += " AND stream_version < @beforeVer";
        sql += " ORDER BY stream_version DESC LIMIT @lim";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("stream", streamId);
        if (atVersion.HasValue) cmd.Parameters.AddWithValue("atVer", atVersion.Value);
        if (beforeVersion.HasValue) cmd.Parameters.AddWithValue("beforeVer", beforeVersion.Value);
        cmd.Parameters.AddWithValue("lim", limit);

        var list = new List<MemoryEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new MemoryEvent(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12)
            ));
        }
        return list;
    }

    public async Task<MemoryPayload?> GetPayloadAsync(Guid workspaceId, Guid payloadId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT payload_id, workspace_id, project_id, title, summary, rationale, 
                     technical_keys::text, related_incident_ids::text, evidence::text, created_at 
              FROM memory_payloads 
              WHERE workspace_id = @ws AND payload_id = @id", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("id", payloadId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new MemoryPayload(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
                JsonSerializer.Deserialize<List<Guid>>(reader.GetString(7)) ?? [],
                JsonSerializer.Deserialize<List<Evidence>>(reader.GetString(8)) ?? [],
                reader.GetFieldValue<DateTimeOffset>(9)
            );
        }
        return null;
    }

    public async Task<ProposeDecisionResult> ProposeDecisionAsync(ProposeDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateTopicKey(command.TopicKey);
        InvariantsValidator.ValidateTitle(command.Title);
        InvariantsValidator.ValidateSummary(command.Summary);
        InvariantsValidator.ValidateRationale(command.Rationale);
        InvariantsValidator.ValidateTechnicalKeys(command.TechnicalKeys);
        InvariantsValidator.ValidateRelatedIncidentIds(command.RelatedIncidentIds);
        InvariantsValidator.ValidateEvidence(command.Evidence, required: true);
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var rlsCmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{command.WorkspaceId}'", conn, tx))
        {
            await rlsCmd.ExecuteNonQueryAsync(ct);
        }

        var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
        {
            command.WorkspaceId, command.ProjectId, command.TopicKey, command.ExpectedVersion,
            command.Title, command.Summary, command.Rationale, command.TechnicalKeys,
            command.RelatedIncidentIds, command.Evidence
        });

        // 1. Idempotency Check
        await using (var chkReceipt = new NpgsqlCommand(
            "SELECT args_hash, result_json::text FROM command_receipts WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conn, tx))
        {
            chkReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            chkReceipt.Parameters.AddWithValue("prin", command.AuthorSubject);
            chkReceipt.Parameters.AddWithValue("op", "propose_decision");
            chkReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);

            await using var reader = await chkReceipt.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var storedHash = reader.GetString(0);
                var storedJson = reader.GetString(1);
                if (storedHash == currentArgsFingerprint)
                {
                    return JsonSerializer.Deserialize<ProposeDecisionResult>(storedJson)!;
                }
                throw new IdempotencyKeyReusedException();
            }
        }

        // 2. Lock topic and check version
        Guid topicId;
        int currentVersion = 0;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT topic_id, current_version FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_key = @key FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("ws", command.WorkspaceId);
            lockCmd.Parameters.AddWithValue("proj", command.ProjectId);
            lockCmd.Parameters.AddWithValue("key", command.TopicKey);

            await using var reader = await lockCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                topicId = reader.GetGuid(0);
                currentVersion = reader.GetInt32(1);
            }
            else
            {
                topicId = Guid.NewGuid();
                currentVersion = 0;
            }
        }

        if (currentVersion != command.ExpectedVersion)
        {
            await tx.RollbackAsync(ct);
            throw new VersionConflictException(topicId, command.ExpectedVersion, currentVersion);
        }

        int newVersion = currentVersion + 1;
        var proposalId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Insert/Update topic
        await using (var upsertTopic = new NpgsqlCommand(@"
            INSERT INTO decision_topics (topic_id, workspace_id, project_id, topic_key, current_version, created_at, updated_at)
            VALUES (@id, @ws, @proj, @key, @ver, @now, @now)
            ON CONFLICT (workspace_id, project_id, topic_key)
            DO UPDATE SET current_version = @ver, updated_at = @now", conn, tx))
        {
            upsertTopic.Parameters.AddWithValue("id", topicId);
            upsertTopic.Parameters.AddWithValue("ws", command.WorkspaceId);
            upsertTopic.Parameters.AddWithValue("proj", command.ProjectId);
            upsertTopic.Parameters.AddWithValue("key", command.TopicKey);
            upsertTopic.Parameters.AddWithValue("ver", newVersion);
            upsertTopic.Parameters.AddWithValue("now", now);
            await upsertTopic.ExecuteNonQueryAsync(ct);
        }

        // Insert Payload
        await using (var insPayload = new NpgsqlCommand(@"
            INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, created_at)
            VALUES (@id, @ws, @proj, @title, @sum, @rat, @keys::jsonb, @inc::jsonb, @evi::jsonb, @now)", conn, tx))
        {
            insPayload.Parameters.AddWithValue("id", proposalId);
            insPayload.Parameters.AddWithValue("ws", command.WorkspaceId);
            insPayload.Parameters.AddWithValue("proj", command.ProjectId);
            insPayload.Parameters.AddWithValue("title", command.Title);
            insPayload.Parameters.AddWithValue("sum", command.Summary);
            insPayload.Parameters.AddWithValue("rat", (object?)command.Rationale ?? DBNull.Value);
            insPayload.Parameters.AddWithValue("keys", JsonSerializer.Serialize(command.TechnicalKeys ?? []));
            insPayload.Parameters.AddWithValue("inc", JsonSerializer.Serialize(command.RelatedIncidentIds ?? []));
            insPayload.Parameters.AddWithValue("evi", JsonSerializer.Serialize(command.Evidence));
            insPayload.Parameters.AddWithValue("now", now);
            await insPayload.ExecuteNonQueryAsync(ct);
        }

        // Insert Event
        await using (var insEvent = new NpgsqlCommand(@"
            INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
            VALUES (@id, @ws, @proj, @stream, @ver, @type, 1, @now, @sub, @iss, @cli, @pay)", conn, tx))
        {
            insEvent.Parameters.AddWithValue("id", Guid.NewGuid());
            insEvent.Parameters.AddWithValue("ws", command.WorkspaceId);
            insEvent.Parameters.AddWithValue("proj", command.ProjectId);
            insEvent.Parameters.AddWithValue("stream", topicId);
            insEvent.Parameters.AddWithValue("ver", newVersion);
            insEvent.Parameters.AddWithValue("type", "DecisionProposed");
            insEvent.Parameters.AddWithValue("now", now);
            insEvent.Parameters.AddWithValue("sub", command.AuthorSubject);
            insEvent.Parameters.AddWithValue("iss", command.AuthorIssuer);
            insEvent.Parameters.AddWithValue("cli", (object?)command.ClientId ?? DBNull.Value);
            insEvent.Parameters.AddWithValue("pay", proposalId);
            await insEvent.ExecuteNonQueryAsync(ct);
        }

        // Update Projection
        await using (var upsertProj = new NpgsqlCommand(@"
            INSERT INTO decision_current (topic_id, workspace_id, project_id, topic_key, stream_version, status, pending_proposal_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, search_vector, updated_at)
            VALUES (@id, @ws, @proj, @key, @ver, 'proposed', @prop, @title, @sum, @rat, @keys::jsonb, @inc::jsonb, @evi::jsonb, to_tsvector('portuguese', @search), @now)
            ON CONFLICT (topic_id)
            DO UPDATE SET stream_version = @ver, pending_proposal_id = @prop, updated_at = @now", conn, tx))
        {
            upsertProj.Parameters.AddWithValue("id", topicId);
            upsertProj.Parameters.AddWithValue("ws", command.WorkspaceId);
            upsertProj.Parameters.AddWithValue("proj", command.ProjectId);
            upsertProj.Parameters.AddWithValue("key", command.TopicKey);
            upsertProj.Parameters.AddWithValue("ver", newVersion);
            upsertProj.Parameters.AddWithValue("prop", proposalId);
            upsertProj.Parameters.AddWithValue("title", command.Title);
            upsertProj.Parameters.AddWithValue("sum", command.Summary);
            upsertProj.Parameters.AddWithValue("rat", (object?)command.Rationale ?? DBNull.Value);
            upsertProj.Parameters.AddWithValue("keys", JsonSerializer.Serialize(command.TechnicalKeys ?? []));
            upsertProj.Parameters.AddWithValue("inc", JsonSerializer.Serialize(command.RelatedIncidentIds ?? []));
            upsertProj.Parameters.AddWithValue("evi", JsonSerializer.Serialize(command.Evidence));
            upsertProj.Parameters.AddWithValue("search", $"{command.Title} {command.Summary} {command.Rationale ?? ""} {command.TopicKey}");
            upsertProj.Parameters.AddWithValue("now", now);
            await upsertProj.ExecuteNonQueryAsync(ct);
        }

        var result = new ProposeDecisionResult(topicId, proposalId, newVersion, "proposed");
        var resultJson = JsonSerializer.Serialize(result);

        // Insert Receipt
        await using (var insReceipt = new NpgsqlCommand(@"
            INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
            VALUES (@id, @ws, @prin, 'propose_decision', @key, @hash, @res::jsonb, @now)", conn, tx))
        {
            insReceipt.Parameters.AddWithValue("id", Guid.NewGuid());
            insReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            insReceipt.Parameters.AddWithValue("prin", command.AuthorSubject);
            insReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);
            insReceipt.Parameters.AddWithValue("hash", currentArgsFingerprint);
            insReceipt.Parameters.AddWithValue("res", resultJson);
            insReceipt.Parameters.AddWithValue("now", now);
            await insReceipt.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<ApproveDecisionResult> ApproveDecisionAsync(ApproveDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var rlsCmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{command.WorkspaceId}'", conn, tx))
        {
            await rlsCmd.ExecuteNonQueryAsync(ct);
        }

        var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
        {
            command.WorkspaceId, command.ProjectId, command.TopicId, command.ProposalId, command.ExpectedVersion
        });

        // 1. Idempotency Check
        await using (var chkReceipt = new NpgsqlCommand(
            "SELECT args_hash, result_json::text FROM command_receipts WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conn, tx))
        {
            chkReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            chkReceipt.Parameters.AddWithValue("prin", command.ReviewerSubject);
            chkReceipt.Parameters.AddWithValue("op", "approve_decision");
            chkReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);

            await using var reader = await chkReceipt.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var storedHash = reader.GetString(0);
                var storedJson = reader.GetString(1);
                if (storedHash == currentArgsFingerprint)
                {
                    return JsonSerializer.Deserialize<ApproveDecisionResult>(storedJson)!;
                }
                throw new IdempotencyKeyReusedException();
            }
        }

        // 2. Lock topic and check version
        int currentVersion;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT current_version FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("ws", command.WorkspaceId);
            lockCmd.Parameters.AddWithValue("proj", command.ProjectId);
            lockCmd.Parameters.AddWithValue("id", command.TopicId);

            var verObj = await lockCmd.ExecuteScalarAsync(ct);
            if (verObj == null)
            {
                throw new NotFoundException($"Decision topic {command.TopicId} not found.");
            }
            currentVersion = Convert.ToInt32(verObj);
        }

        if (currentVersion != command.ExpectedVersion)
        {
            await tx.RollbackAsync(ct);
            throw new VersionConflictException(command.TopicId, command.ExpectedVersion, currentVersion);
        }

        // Fetch proposal payload
        string title, summary;
        string? rationale;
        string techKeys, relIncidents, evidence;
        await using (var payCmd = new NpgsqlCommand(
            "SELECT title, summary, rationale, technical_keys::text, related_incident_ids::text, evidence::text FROM memory_payloads WHERE workspace_id = @ws AND payload_id = @id", conn, tx))
        {
            payCmd.Parameters.AddWithValue("ws", command.WorkspaceId);
            payCmd.Parameters.AddWithValue("id", command.ProposalId);
            await using var reader = await payCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new NotFoundException($"Proposal payload {command.ProposalId} not found.");

            title = reader.GetString(0);
            summary = reader.GetString(1);
            rationale = reader.IsDBNull(2) ? null : reader.GetString(2);
            techKeys = reader.GetString(3);
            relIncidents = reader.GetString(4);
            evidence = reader.GetString(5);
        }

        int newVersion = currentVersion + 1;
        var now = DateTimeOffset.UtcNow;

        // Update topic
        await using (var updTopic = new NpgsqlCommand(
            "UPDATE decision_topics SET current_version = @ver, updated_at = @now WHERE topic_id = @id", conn, tx))
        {
            updTopic.Parameters.AddWithValue("ver", newVersion);
            updTopic.Parameters.AddWithValue("now", now);
            updTopic.Parameters.AddWithValue("id", command.TopicId);
            await updTopic.ExecuteNonQueryAsync(ct);
        }

        // Insert event
        await using (var insEvent = new NpgsqlCommand(@"
            INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
            VALUES (@id, @ws, @proj, @stream, @ver, 'DecisionApproved', 1, @now, @sub, @iss, 'web-session', @pay)", conn, tx))
        {
            insEvent.Parameters.AddWithValue("id", Guid.NewGuid());
            insEvent.Parameters.AddWithValue("ws", command.WorkspaceId);
            insEvent.Parameters.AddWithValue("proj", command.ProjectId);
            insEvent.Parameters.AddWithValue("stream", command.TopicId);
            insEvent.Parameters.AddWithValue("ver", newVersion);
            insEvent.Parameters.AddWithValue("now", now);
            insEvent.Parameters.AddWithValue("sub", command.ReviewerSubject);
            insEvent.Parameters.AddWithValue("iss", command.ReviewerIssuer);
            insEvent.Parameters.AddWithValue("pay", command.ProposalId);
            await insEvent.ExecuteNonQueryAsync(ct);
        }

        // Update current projection
        await using (var updProj = new NpgsqlCommand(@"
            UPDATE decision_current 
            SET active_record_id = @actId, active_record_version = @ver, stream_version = @ver,
                status = 'active', pending_proposal_id = NULL, title = @title, summary = @sum, rationale = @rat,
                technical_keys = @keys::jsonb, related_incident_ids = @inc::jsonb, evidence = @evi::jsonb,
                search_vector = to_tsvector('portuguese', @search), updated_at = @now
            WHERE topic_id = @id", conn, tx))
        {
            updProj.Parameters.AddWithValue("actId", command.ProposalId);
            updProj.Parameters.AddWithValue("ver", newVersion);
            updProj.Parameters.AddWithValue("title", title);
            updProj.Parameters.AddWithValue("sum", summary);
            updProj.Parameters.AddWithValue("rat", (object?)rationale ?? DBNull.Value);
            updProj.Parameters.AddWithValue("keys", techKeys);
            updProj.Parameters.AddWithValue("inc", relIncidents);
            updProj.Parameters.AddWithValue("evi", evidence);
            updProj.Parameters.AddWithValue("search", $"{title} {summary} {rationale ?? ""}");
            updProj.Parameters.AddWithValue("now", now);
            updProj.Parameters.AddWithValue("id", command.TopicId);
            await updProj.ExecuteNonQueryAsync(ct);
        }

        var result = new ApproveDecisionResult(command.TopicId, command.ProposalId, newVersion, "active");
        var resultJson = JsonSerializer.Serialize(result);

        await using (var insReceipt = new NpgsqlCommand(@"
            INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
            VALUES (@id, @ws, @prin, 'approve_decision', @key, @hash, @res::jsonb, @now)", conn, tx))
        {
            insReceipt.Parameters.AddWithValue("id", Guid.NewGuid());
            insReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            insReceipt.Parameters.AddWithValue("prin", command.ReviewerSubject);
            insReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);
            insReceipt.Parameters.AddWithValue("hash", currentArgsFingerprint);
            insReceipt.Parameters.AddWithValue("res", resultJson);
            insReceipt.Parameters.AddWithValue("now", now);
            await insReceipt.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RejectDecisionResult> RejectDecisionAsync(RejectDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var rlsCmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{command.WorkspaceId}'", conn, tx))
        {
            await rlsCmd.ExecuteNonQueryAsync(ct);
        }

        var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
        {
            command.WorkspaceId, command.ProjectId, command.TopicId, command.ProposalId, command.ExpectedVersion, command.Reason
        });

        // 1. Idempotency Check
        await using (var chkReceipt = new NpgsqlCommand(
            "SELECT args_hash, result_json::text FROM command_receipts WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conn, tx))
        {
            chkReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            chkReceipt.Parameters.AddWithValue("prin", command.ReviewerSubject);
            chkReceipt.Parameters.AddWithValue("op", "reject_decision");
            chkReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);

            await using var reader = await chkReceipt.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var storedHash = reader.GetString(0);
                var storedJson = reader.GetString(1);
                if (storedHash == currentArgsFingerprint)
                {
                    return JsonSerializer.Deserialize<RejectDecisionResult>(storedJson)!;
                }
                throw new IdempotencyKeyReusedException();
            }
        }

        // 2. Lock topic and check version
        int currentVersion;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT current_version FROM decision_topics WHERE workspace_id = @ws AND project_id = @proj AND topic_id = @id FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("ws", command.WorkspaceId);
            lockCmd.Parameters.AddWithValue("proj", command.ProjectId);
            lockCmd.Parameters.AddWithValue("id", command.TopicId);

            var verObj = await lockCmd.ExecuteScalarAsync(ct);
            if (verObj == null)
                throw new NotFoundException($"Decision topic {command.TopicId} not found.");
            currentVersion = Convert.ToInt32(verObj);
        }

        if (currentVersion != command.ExpectedVersion)
        {
            await tx.RollbackAsync(ct);
            throw new VersionConflictException(command.TopicId, command.ExpectedVersion, currentVersion);
        }

        int newVersion = currentVersion + 1;
        var now = DateTimeOffset.UtcNow;

        await using (var updTopic = new NpgsqlCommand(
            "UPDATE decision_topics SET current_version = @ver, updated_at = @now WHERE topic_id = @id", conn, tx))
        {
            updTopic.Parameters.AddWithValue("ver", newVersion);
            updTopic.Parameters.AddWithValue("now", now);
            updTopic.Parameters.AddWithValue("id", command.TopicId);
            await updTopic.ExecuteNonQueryAsync(ct);
        }

        await using (var insEvent = new NpgsqlCommand(@"
            INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
            VALUES (@id, @ws, @proj, @stream, @ver, 'DecisionRejected', 1, @now, @sub, @iss, 'web-session', @pay)", conn, tx))
        {
            insEvent.Parameters.AddWithValue("id", Guid.NewGuid());
            insEvent.Parameters.AddWithValue("ws", command.WorkspaceId);
            insEvent.Parameters.AddWithValue("proj", command.ProjectId);
            insEvent.Parameters.AddWithValue("stream", command.TopicId);
            insEvent.Parameters.AddWithValue("ver", newVersion);
            insEvent.Parameters.AddWithValue("now", now);
            insEvent.Parameters.AddWithValue("sub", command.ReviewerSubject);
            insEvent.Parameters.AddWithValue("iss", command.ReviewerIssuer);
            insEvent.Parameters.AddWithValue("pay", command.ProposalId);
            await insEvent.ExecuteNonQueryAsync(ct);
        }

        // Projection update: clear pending_proposal_id; if had active_record_id keep active, else rejected
        string newStatus;
        await using (var updProj = new NpgsqlCommand(@"
            UPDATE decision_current 
            SET pending_proposal_id = NULL,
                stream_version = @ver,
                status = CASE WHEN active_record_id IS NOT NULL THEN 'active' ELSE 'rejected' END,
                updated_at = @now
            WHERE topic_id = @id
            RETURNING status", conn, tx))
        {
            updProj.Parameters.AddWithValue("ver", newVersion);
            updProj.Parameters.AddWithValue("now", now);
            updProj.Parameters.AddWithValue("id", command.TopicId);
            var statusObj = await updProj.ExecuteScalarAsync(ct);
            newStatus = statusObj?.ToString() ?? "rejected";
        }

        var result = new RejectDecisionResult(command.TopicId, newVersion, newStatus);
        var resultJson = JsonSerializer.Serialize(result);

        await using (var insReceipt = new NpgsqlCommand(@"
            INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
            VALUES (@id, @ws, @prin, 'reject_decision', @key, @hash, @res::jsonb, @now)", conn, tx))
        {
            insReceipt.Parameters.AddWithValue("id", Guid.NewGuid());
            insReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            insReceipt.Parameters.AddWithValue("prin", command.ReviewerSubject);
            insReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);
            insReceipt.Parameters.AddWithValue("hash", currentArgsFingerprint);
            insReceipt.Parameters.AddWithValue("res", resultJson);
            insReceipt.Parameters.AddWithValue("now", now);
            await insReceipt.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RestoreDecisionResult> RestoreDecisionAsync(RestoreDecisionCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        InvariantsValidator.ValidateExpectedVersion(command.ExpectedVersion);

        var topic = await GetTopicByIdAsync(command.WorkspaceId, command.ProjectId, command.TopicId, ct);
        if (topic == null)
            throw new NotFoundException($"Decision topic {command.TopicId} not found.");

        var events = await GetEventsByStreamAsync(command.WorkspaceId, command.TopicId, atVersion: command.VersionToRestore, limit: 100, ct: ct);
        var pastDecision = DecisionReducer.Reconstitute(
            topic.TopicId, topic.WorkspaceId, topic.ProjectId, topic.TopicKey,
            events,
            pid => GetPayloadAsync(command.WorkspaceId, pid, ct).GetAwaiter().GetResult(),
            atVersion: command.VersionToRestore
        );

        if (string.IsNullOrEmpty(pastDecision.Title))
            throw new NotFoundException($"Version {command.VersionToRestore} not found.");

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

    public async Task<IncidentStream?> GetIncidentByIdAsync(Guid workspaceId, Guid projectId, Guid incidentId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT incident_id, workspace_id, project_id, current_version, created_at, updated_at FROM incident_streams WHERE workspace_id = @ws AND project_id = @proj AND incident_id = @id", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("proj", projectId);
        cmd.Parameters.AddWithValue("id", incidentId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new IncidentStream(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5)
            );
        }
        return null;
    }

    public async Task<IncidentCurrent?> GetIncidentCurrentAsync(Guid workspaceId, Guid projectId, Guid incidentId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            @"SELECT incident_id, workspace_id, project_id, record_version, stream_version, status, 
                     title, summary, occurred_at, technical_keys::text, evidence::text, created_at 
              FROM incident_current 
              WHERE workspace_id = @ws AND project_id = @proj AND incident_id = @id", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("proj", projectId);
        cmd.Parameters.AddWithValue("id", incidentId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new IncidentCurrent(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetInt32(3), reader.GetInt32(4), IncidentStatus.Reported,
                reader.GetString(6), reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? [],
                JsonSerializer.Deserialize<List<Evidence>>(reader.GetString(10)) ?? [],
                reader.GetFieldValue<DateTimeOffset>(11)
            );
        }
        return null;
    }

    public async Task<LogIncidentResult> LogIncidentAsync(LogIncidentCommand command, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateTitle(command.Title);
        InvariantsValidator.ValidateSummary(command.Summary);
        InvariantsValidator.ValidateTechnicalKeys(command.TechnicalKeys);
        InvariantsValidator.ValidateEvidence(command.Evidence, required: true);
        InvariantsValidator.ValidateIdempotencyKey(command.IdempotencyKey);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var rlsCmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{command.WorkspaceId}'", conn, tx))
        {
            await rlsCmd.ExecuteNonQueryAsync(ct);
        }

        var currentArgsFingerprint = IdempotencyHelper.ComputeFingerprint(new
        {
            command.WorkspaceId, command.ProjectId, command.Title, command.Summary, command.OccurredAt, command.TechnicalKeys, command.Evidence
        });

        // 1. Idempotency Check
        await using (var chkReceipt = new NpgsqlCommand(
            "SELECT args_hash, result_json::text FROM command_receipts WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conn, tx))
        {
            chkReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            chkReceipt.Parameters.AddWithValue("prin", command.AuthorSubject);
            chkReceipt.Parameters.AddWithValue("op", "log_incident");
            chkReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);

            await using var reader = await chkReceipt.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var storedHash = reader.GetString(0);
                var storedJson = reader.GetString(1);
                if (storedHash == currentArgsFingerprint)
                {
                    return JsonSerializer.Deserialize<LogIncidentResult>(storedJson)!;
                }
                throw new IdempotencyKeyReusedException();
            }
        }

        var incidentId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var occurredAt = command.OccurredAt ?? now;

        await using (var insStream = new NpgsqlCommand(@"
            INSERT INTO incident_streams (incident_id, workspace_id, project_id, current_version, created_at, updated_at)
            VALUES (@id, @ws, @proj, 1, @now, @now)", conn, tx))
        {
            insStream.Parameters.AddWithValue("id", incidentId);
            insStream.Parameters.AddWithValue("ws", command.WorkspaceId);
            insStream.Parameters.AddWithValue("proj", command.ProjectId);
            insStream.Parameters.AddWithValue("now", now);
            await insStream.ExecuteNonQueryAsync(ct);
        }

        await using (var insPayload = new NpgsqlCommand(@"
            INSERT INTO memory_payloads (payload_id, workspace_id, project_id, title, summary, technical_keys, evidence, created_at)
            VALUES (@id, @ws, @proj, @title, @sum, @keys::jsonb, @evi::jsonb, @now)", conn, tx))
        {
            insPayload.Parameters.AddWithValue("id", payloadId);
            insPayload.Parameters.AddWithValue("ws", command.WorkspaceId);
            insPayload.Parameters.AddWithValue("proj", command.ProjectId);
            insPayload.Parameters.AddWithValue("title", command.Title);
            insPayload.Parameters.AddWithValue("sum", command.Summary);
            insPayload.Parameters.AddWithValue("keys", JsonSerializer.Serialize(command.TechnicalKeys ?? []));
            insPayload.Parameters.AddWithValue("evi", JsonSerializer.Serialize(command.Evidence));
            insPayload.Parameters.AddWithValue("now", now);
            await insPayload.ExecuteNonQueryAsync(ct);
        }

        await using (var insEvent = new NpgsqlCommand(@"
            INSERT INTO memory_events (event_id, workspace_id, project_id, stream_id, stream_version, type, schema_version, recorded_at, author_subject, author_issuer, client_id, payload_id)
            VALUES (@id, @ws, @proj, @stream, 1, 'IncidentReported', 1, @now, @sub, @iss, @cli, @pay)", conn, tx))
        {
            insEvent.Parameters.AddWithValue("id", Guid.NewGuid());
            insEvent.Parameters.AddWithValue("ws", command.WorkspaceId);
            insEvent.Parameters.AddWithValue("proj", command.ProjectId);
            insEvent.Parameters.AddWithValue("stream", incidentId);
            insEvent.Parameters.AddWithValue("now", now);
            insEvent.Parameters.AddWithValue("sub", command.AuthorSubject);
            insEvent.Parameters.AddWithValue("iss", command.AuthorIssuer);
            insEvent.Parameters.AddWithValue("cli", (object?)command.ClientId ?? DBNull.Value);
            insEvent.Parameters.AddWithValue("pay", payloadId);
            await insEvent.ExecuteNonQueryAsync(ct);
        }

        await using (var insCurrent = new NpgsqlCommand(@"
            INSERT INTO incident_current (incident_id, workspace_id, project_id, record_version, stream_version, status, title, summary, occurred_at, technical_keys, evidence, search_vector, created_at)
            VALUES (@id, @ws, @proj, 1, 1, 'reported', @title, @sum, @occ, @keys::jsonb, @evi::jsonb, to_tsvector('portuguese', @search), @now)", conn, tx))
        {
            insCurrent.Parameters.AddWithValue("id", incidentId);
            insCurrent.Parameters.AddWithValue("ws", command.WorkspaceId);
            insCurrent.Parameters.AddWithValue("proj", command.ProjectId);
            insCurrent.Parameters.AddWithValue("title", command.Title);
            insCurrent.Parameters.AddWithValue("sum", command.Summary);
            insCurrent.Parameters.AddWithValue("occ", occurredAt);
            insCurrent.Parameters.AddWithValue("keys", JsonSerializer.Serialize(command.TechnicalKeys ?? []));
            insCurrent.Parameters.AddWithValue("evi", JsonSerializer.Serialize(command.Evidence));
            insCurrent.Parameters.AddWithValue("search", $"{command.Title} {command.Summary}");
            insCurrent.Parameters.AddWithValue("now", now);
            await insCurrent.ExecuteNonQueryAsync(ct);
        }

        var result = new LogIncidentResult(incidentId, 1, "reported");
        var resultJson = JsonSerializer.Serialize(result);

        await using (var insReceipt = new NpgsqlCommand(@"
            INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
            VALUES (@id, @ws, @prin, 'log_incident', @key, @hash, @res::jsonb, @now)", conn, tx))
        {
            insReceipt.Parameters.AddWithValue("id", Guid.NewGuid());
            insReceipt.Parameters.AddWithValue("ws", command.WorkspaceId);
            insReceipt.Parameters.AddWithValue("prin", command.AuthorSubject);
            insReceipt.Parameters.AddWithValue("key", command.IdempotencyKey);
            insReceipt.Parameters.AddWithValue("hash", currentArgsFingerprint);
            insReceipt.Parameters.AddWithValue("res", resultJson);
            insReceipt.Parameters.AddWithValue("now", now);
            await insReceipt.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return result;
    }

    #endregion

    #region IContextRepository

    public async Task<QueryContextResult> QueryContextAsync(QueryContextQuery query, CancellationToken ct = default)
    {
        InvariantsValidator.ValidateQuery(query.Query);
        InvariantsValidator.ValidateLimit(query.Limit, min: 1, max: 20);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var rlsCmd = new NpgsqlCommand($"SET LOCAL app.current_workspace_id = '{query.WorkspaceId}'", conn, tx))
        {
            await rlsCmd.ExecuteNonQueryAsync(ct);
        }

        var items = new List<ContextItem>();
        if (string.Equals(query.Kind, "decision", StringComparison.OrdinalIgnoreCase))
        {
            var sql = @"
                SELECT topic_id, active_record_id, active_record_version, stream_version, status, title, summary, evidence::text,
                       (technical_keys @> @exactKey::jsonb) as has_exact,
                       ts_rank_cd(search_vector, plainto_tsquery('portuguese', @q)) as rank
                FROM decision_current
                WHERE workspace_id = @ws AND project_id = @proj AND status = 'active'
                  AND (search_vector @@ plainto_tsquery('portuguese', @q) OR technical_keys @> @exactKey::jsonb)
                ORDER BY has_exact DESC, rank DESC, updated_at DESC, topic_id ASC
                LIMIT @lim";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("ws", query.WorkspaceId);
            cmd.Parameters.AddWithValue("proj", query.ProjectId);
            cmd.Parameters.AddWithValue("q", query.Query);
            cmd.Parameters.AddWithValue("exactKey", JsonSerializer.Serialize(new[] { query.Query.Trim() }));
            cmd.Parameters.AddWithValue("lim", query.Limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var topicId = reader.GetGuid(0);
                var recId = reader.IsDBNull(1) ? topicId : reader.GetGuid(1);
                var recVer = reader.IsDBNull(2) ? reader.GetInt32(3) : reader.GetInt32(2);
                items.Add(new ContextItem(
                    Kind: "decision",
                    TopicId: topicId,
                    IncidentId: null,
                    RecordId: recId,
                    RecordVersion: recVer,
                    StreamVersion: reader.GetInt32(3),
                    Status: reader.GetString(4),
                    Title: reader.GetString(5),
                    Summary: reader.GetString(6),
                    Evidence: JsonSerializer.Deserialize<List<Evidence>>(reader.GetString(7)) ?? []
                ));
            }
        }
        else if (string.Equals(query.Kind, "incident", StringComparison.OrdinalIgnoreCase))
        {
            var sql = @"
                SELECT incident_id, record_version, stream_version, status, title, summary, evidence::text,
                       (technical_keys @> @exactKey::jsonb) as has_exact,
                       ts_rank_cd(search_vector, plainto_tsquery('portuguese', @q)) as rank
                FROM incident_current
                WHERE workspace_id = @ws AND project_id = @proj AND status = 'reported'
                  AND (search_vector @@ plainto_tsquery('portuguese', @q) OR technical_keys @> @exactKey::jsonb)
                ORDER BY has_exact DESC, rank DESC, created_at DESC, incident_id ASC
                LIMIT @lim";

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("ws", query.WorkspaceId);
            cmd.Parameters.AddWithValue("proj", query.ProjectId);
            cmd.Parameters.AddWithValue("q", query.Query);
            cmd.Parameters.AddWithValue("exactKey", JsonSerializer.Serialize(new[] { query.Query.Trim() }));
            cmd.Parameters.AddWithValue("lim", query.Limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var incId = reader.GetGuid(0);
                items.Add(new ContextItem(
                    Kind: "incident",
                    TopicId: null,
                    IncidentId: incId,
                    RecordId: incId,
                    RecordVersion: reader.GetInt32(1),
                    StreamVersion: reader.GetInt32(2),
                    Status: reader.GetString(3),
                    Title: reader.GetString(4),
                    Summary: reader.GetString(5),
                    Evidence: JsonSerializer.Deserialize<List<Evidence>>(reader.GetString(6)) ?? []
                ));
            }
        }

        var queryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var searchMode = "lexical";
        var matchStatus = items.Count > 0 ? "matched" : "no_match";

        // Insert context receipt
        await using (var insRec = new NpgsqlCommand(@"
            INSERT INTO context_receipts (query_id, workspace_id, project_id, principal_id, client_id, search_mode, created_at)
            VALUES (@id, @ws, @proj, @prin, @cli, @mode, @now)", conn, tx))
        {
            insRec.Parameters.AddWithValue("id", queryId);
            insRec.Parameters.AddWithValue("ws", query.WorkspaceId);
            insRec.Parameters.AddWithValue("proj", query.ProjectId);
            insRec.Parameters.AddWithValue("prin", query.PrincipalId);
            insRec.Parameters.AddWithValue("cli", (object?)query.ClientId ?? DBNull.Value);
            insRec.Parameters.AddWithValue("mode", searchMode);
            insRec.Parameters.AddWithValue("now", now);
            await insRec.ExecuteNonQueryAsync(ct);
        }

        // Insert receipt items
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            await using var insItem = new NpgsqlCommand(@"
                INSERT INTO context_receipt_items (receipt_item_id, query_id, workspace_id, position, kind, stream_id, record_id, record_version, stream_version, status)
                VALUES (@id, @qid, @ws, @pos, @kind, @stream, @rec, @recVer, @strVer, @status)", conn, tx);
            insItem.Parameters.AddWithValue("id", Guid.NewGuid());
            insItem.Parameters.AddWithValue("qid", queryId);
            insItem.Parameters.AddWithValue("ws", query.WorkspaceId);
            insItem.Parameters.AddWithValue("pos", i + 1);
            insItem.Parameters.AddWithValue("kind", item.Kind);
            insItem.Parameters.AddWithValue("stream", item.TopicId ?? item.IncidentId ?? item.RecordId);
            insItem.Parameters.AddWithValue("rec", item.RecordId);
            insItem.Parameters.AddWithValue("recVer", item.RecordVersion);
            insItem.Parameters.AddWithValue("strVer", item.StreamVersion);
            insItem.Parameters.AddWithValue("status", item.Status);
            await insItem.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new QueryContextResult(queryId, matchStatus, searchMode, items);
    }

    public async Task<GetContextReceiptResult> GetContextReceiptAsync(Guid workspaceId, Guid projectId, Guid queryId, string principalId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        DateTimeOffset createdAt;
        string? clientId;
        string searchMode;

        await using (var cmd = new NpgsqlCommand(
            "SELECT principal_id, client_id, search_mode, created_at FROM context_receipts WHERE workspace_id = @ws AND project_id = @proj AND query_id = @id", conn))
        {
            cmd.Parameters.AddWithValue("ws", workspaceId);
            cmd.Parameters.AddWithValue("proj", projectId);
            cmd.Parameters.AddWithValue("id", queryId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new NotFoundException($"Context receipt {queryId} not found in workspace/project.");
            }
            clientId = reader.IsDBNull(1) ? null : reader.GetString(1);
            searchMode = reader.GetString(2);
            createdAt = reader.GetFieldValue<DateTimeOffset>(3);
        }

        var items = new List<ContextReceiptItem>();
        await using (var cmdItems = new NpgsqlCommand(
            @"SELECT receipt_item_id, query_id, workspace_id, position, kind, stream_id, record_id, record_version, stream_version, status 
              FROM context_receipt_items 
              WHERE workspace_id = @ws AND query_id = @id 
              ORDER BY position ASC", conn))
        {
            cmdItems.Parameters.AddWithValue("ws", workspaceId);
            cmdItems.Parameters.AddWithValue("id", queryId);

            await using var reader = await cmdItems.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new ContextReceiptItem(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.GetInt32(3), reader.GetString(4), reader.GetGuid(5),
                    reader.GetGuid(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetString(9)
                ));
            }
        }

        return new GetContextReceiptResult(queryId, workspaceId, projectId, principalId, clientId, searchMode, createdAt, items);
    }

    #endregion

    #region IReceiptRepository

    public async Task<CommandReceipt?> GetCommandReceiptAsync(Guid workspaceId, string principalId, string operation, string idempotencyKey, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(workspaceId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json::text, created_at FROM command_receipts WHERE workspace_id = @ws AND principal_id = @prin AND operation = @op AND idempotency_key = @key", conn);
        cmd.Parameters.AddWithValue("ws", workspaceId);
        cmd.Parameters.AddWithValue("prin", principalId);
        cmd.Parameters.AddWithValue("op", operation);
        cmd.Parameters.AddWithValue("key", idempotencyKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new CommandReceipt(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)
            );
        }
        return null;
    }

    public async Task SaveCommandReceiptAsync(CommandReceipt receipt, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionWithRlsAsync(receipt.WorkspaceId, ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO command_receipts (receipt_id, workspace_id, principal_id, operation, idempotency_key, args_hash, result_json, created_at)
            VALUES (@id, @ws, @prin, @op, @key, @hash, @res::jsonb, @now)
            ON CONFLICT (workspace_id, principal_id, operation, idempotency_key) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("id", receipt.ReceiptId);
        cmd.Parameters.AddWithValue("ws", receipt.WorkspaceId);
        cmd.Parameters.AddWithValue("prin", receipt.PrincipalId);
        cmd.Parameters.AddWithValue("op", receipt.Operation);
        cmd.Parameters.AddWithValue("key", receipt.IdempotencyKey);
        cmd.Parameters.AddWithValue("hash", receipt.ArgsHash);
        cmd.Parameters.AddWithValue("res", receipt.ResultJson);
        cmd.Parameters.AddWithValue("now", receipt.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region IProjectionRebuilder

    public async Task RebuildProjectionsAsync(Guid? workspaceId = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Fetch distinct topic streams
        var topicIds = new List<(Guid WsId, Guid ProjId, Guid TopicId, string TopicKey)>();
        var sqlTopics = "SELECT workspace_id, project_id, topic_id, topic_key FROM decision_topics";
        if (workspaceId.HasValue) sqlTopics += " WHERE workspace_id = @ws";

        await using (var cmd = new NpgsqlCommand(sqlTopics, conn))
        {
            if (workspaceId.HasValue) cmd.Parameters.AddWithValue("ws", workspaceId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                topicIds.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3)));
            }
        }

        foreach (var (ws, proj, topicId, topicKey) in topicIds)
        {
            var events = await GetEventsByStreamAsync(ws, topicId, limit: 1000, ct: ct);
            events = events.OrderBy(e => e.StreamVersion).ToList();

            var reconstituted = DecisionReducer.Reconstitute(
                topicId, ws, proj, topicKey, events,
                pid => GetPayloadAsync(ws, pid, ct).GetAwaiter().GetResult()
            );

            await using var cmdUpd = new NpgsqlCommand(@"
                INSERT INTO decision_current (topic_id, workspace_id, project_id, topic_key, active_record_id, active_record_version, stream_version, status, pending_proposal_id, title, summary, rationale, technical_keys, related_incident_ids, evidence, search_vector, updated_at)
                VALUES (@id, @ws, @proj, @key, @actId, @actVer, @strVer, @status, @propId, @title, @sum, @rat, @keys::jsonb, @inc::jsonb, @evi::jsonb, to_tsvector('portuguese', @search), now())
                ON CONFLICT (topic_id)
                DO UPDATE SET active_record_id = @actId, active_record_version = @actVer, stream_version = @strVer, status = @status, pending_proposal_id = @propId, title = @title, summary = @sum, rationale = @rat, technical_keys = @keys::jsonb, related_incident_ids = @inc::jsonb, evidence = @evi::jsonb, search_vector = to_tsvector('portuguese', @search), updated_at = now()", conn);

            cmdUpd.Parameters.AddWithValue("id", topicId);
            cmdUpd.Parameters.AddWithValue("ws", ws);
            cmdUpd.Parameters.AddWithValue("proj", proj);
            cmdUpd.Parameters.AddWithValue("key", topicKey);
            cmdUpd.Parameters.AddWithValue("actId", (object?)reconstituted.ActiveRecordId ?? DBNull.Value);
            cmdUpd.Parameters.AddWithValue("actVer", (object?)reconstituted.ActiveRecordVersion ?? DBNull.Value);
            cmdUpd.Parameters.AddWithValue("strVer", reconstituted.StreamVersion);
            cmdUpd.Parameters.AddWithValue("status", reconstituted.Status.ToString().ToLowerInvariant());
            cmdUpd.Parameters.AddWithValue("propId", (object?)reconstituted.PendingProposalId ?? DBNull.Value);
            cmdUpd.Parameters.AddWithValue("title", reconstituted.Title);
            cmdUpd.Parameters.AddWithValue("sum", reconstituted.Summary);
            cmdUpd.Parameters.AddWithValue("rat", (object?)reconstituted.Rationale ?? DBNull.Value);
            cmdUpd.Parameters.AddWithValue("keys", JsonSerializer.Serialize(reconstituted.TechnicalKeys));
            cmdUpd.Parameters.AddWithValue("inc", JsonSerializer.Serialize(reconstituted.RelatedIncidentIds));
            cmdUpd.Parameters.AddWithValue("evi", JsonSerializer.Serialize(reconstituted.Evidence));
            cmdUpd.Parameters.AddWithValue("search", $"{reconstituted.Title} {reconstituted.Summary} {reconstituted.Rationale ?? ""} {reconstituted.TopicKey}");
            await cmdUpd.ExecuteNonQueryAsync(ct);
        }
    }

    #endregion
}
