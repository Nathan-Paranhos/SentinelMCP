using Sentinel.Core.Models;

namespace Sentinel.Core.Commands;

public sealed record ProposeDecisionCommand(
    Guid WorkspaceId,
    Guid ProjectId,
    string TopicKey,
    int ExpectedVersion,
    string Title,
    string Summary,
    string? Rationale,
    List<string>? TechnicalKeys,
    List<Guid>? RelatedIncidentIds,
    List<Evidence> Evidence,
    string IdempotencyKey,
    string AuthorSubject,
    string AuthorIssuer,
    string? ClientId
);

public sealed record ProposeDecisionResult(
    Guid TopicId,
    Guid ProposalId,
    int StreamVersion,
    string Status = "proposed"
);

public sealed record ApproveDecisionCommand(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TopicId,
    Guid ProposalId,
    int ExpectedVersion,
    string IdempotencyKey,
    string ReviewerSubject,
    string ReviewerIssuer
);

public sealed record ApproveDecisionResult(
    Guid TopicId,
    Guid ActiveRecordId,
    int StreamVersion,
    string Status = "active"
);

public sealed record RejectDecisionCommand(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TopicId,
    Guid ProposalId,
    int ExpectedVersion,
    string Reason,
    string IdempotencyKey,
    string ReviewerSubject,
    string ReviewerIssuer
);

public sealed record RejectDecisionResult(
    Guid TopicId,
    int StreamVersion,
    string Status
);

public sealed record RestoreDecisionCommand(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid TopicId,
    int VersionToRestore,
    int ExpectedVersion,
    string IdempotencyKey,
    string ReviewerSubject,
    string ReviewerIssuer
);

public sealed record RestoreDecisionResult(
    Guid TopicId,
    Guid ProposalId,
    int StreamVersion,
    string Status = "proposed"
);

public sealed record LogIncidentCommand(
    Guid WorkspaceId,
    Guid ProjectId,
    string Title,
    string Summary,
    DateTimeOffset? OccurredAt,
    List<string>? TechnicalKeys,
    List<Evidence> Evidence,
    string IdempotencyKey,
    string AuthorSubject,
    string AuthorIssuer,
    string? ClientId
);

public sealed record LogIncidentResult(
    Guid IncidentId,
    int StreamVersion,
    string Status = "reported"
);
