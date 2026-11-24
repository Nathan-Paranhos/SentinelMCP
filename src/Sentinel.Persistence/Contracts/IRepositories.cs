using Sentinel.Core.Commands;
using Sentinel.Core.Models;
using Sentinel.Core.Queries;

namespace Sentinel.Persistence.Contracts;

public interface IWorkspaceRepository
{
    Task<Workspace?> GetWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task<Membership?> GetMembershipAsync(Guid workspaceId, string issuer, string subject, CancellationToken ct = default);
    Task<Project?> GetProjectAsync(Guid workspaceId, Guid projectId, CancellationToken ct = default);
    Task<List<Project>> GetProjectsByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}

public interface IDecisionRepository
{
    Task<DecisionTopic?> GetTopicByKeyAsync(Guid workspaceId, Guid projectId, string topicKey, CancellationToken ct = default);
    Task<DecisionTopic?> GetTopicByIdAsync(Guid workspaceId, Guid projectId, Guid topicId, CancellationToken ct = default);
    Task<DecisionCurrent?> GetDecisionCurrentAsync(Guid workspaceId, Guid projectId, Guid topicId, CancellationToken ct = default);
    Task<List<DecisionCurrent>> GetPendingProposalsAsync(Guid workspaceId, Guid? projectId = null, CancellationToken ct = default);
    Task<List<MemoryEvent>> GetEventsByStreamAsync(Guid workspaceId, Guid streamId, int? atVersion = null, int? beforeVersion = null, int limit = 50, CancellationToken ct = default);
    Task<MemoryPayload?> GetPayloadAsync(Guid workspaceId, Guid payloadId, CancellationToken ct = default);
    Task<ProposeDecisionResult> ProposeDecisionAsync(ProposeDecisionCommand command, CancellationToken ct = default);
    Task<ApproveDecisionResult> ApproveDecisionAsync(ApproveDecisionCommand command, CancellationToken ct = default);
    Task<RejectDecisionResult> RejectDecisionAsync(RejectDecisionCommand command, CancellationToken ct = default);
    Task<RestoreDecisionResult> RestoreDecisionAsync(RestoreDecisionCommand command, CancellationToken ct = default);
}

public interface IIncidentRepository
{
    Task<IncidentStream?> GetIncidentByIdAsync(Guid workspaceId, Guid projectId, Guid incidentId, CancellationToken ct = default);
    Task<IncidentCurrent?> GetIncidentCurrentAsync(Guid workspaceId, Guid projectId, Guid incidentId, CancellationToken ct = default);
    Task<List<MemoryEvent>> GetEventsByStreamAsync(Guid workspaceId, Guid incidentId, int? atVersion = null, int? beforeVersion = null, int limit = 50, CancellationToken ct = default);
    Task<MemoryPayload?> GetPayloadAsync(Guid workspaceId, Guid payloadId, CancellationToken ct = default);
    Task<LogIncidentResult> LogIncidentAsync(LogIncidentCommand command, CancellationToken ct = default);
}

public interface IContextRepository
{
    Task<QueryContextResult> QueryContextAsync(QueryContextQuery query, CancellationToken ct = default);
    Task<GetContextReceiptResult> GetContextReceiptAsync(Guid workspaceId, Guid projectId, Guid queryId, string principalId, CancellationToken ct = default);
}

public interface IReceiptRepository
{
    Task<CommandReceipt?> GetCommandReceiptAsync(Guid workspaceId, string principalId, string operation, string idempotencyKey, CancellationToken ct = default);
    Task SaveCommandReceiptAsync(CommandReceipt receipt, CancellationToken ct = default);
}

public interface IProjectionRebuilder
{
    Task RebuildProjectionsAsync(Guid? workspaceId = null, CancellationToken ct = default);
}
