namespace Sentinel.Core.Models;

public sealed record Workspace(
    Guid WorkspaceId,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt
);

public sealed record Project(
    Guid ProjectId,
    Guid WorkspaceId,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt
);

public sealed record Membership(
    Guid MembershipId,
    Guid WorkspaceId,
    string Issuer,
    string Subject,
    UserRole Role,
    bool IsActive,
    DateTimeOffset CreatedAt
);

public sealed record DecisionTopic(
    Guid TopicId,
    Guid WorkspaceId,
    Guid ProjectId,
    string TopicKey,
    int CurrentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record IncidentStream(
    Guid IncidentId,
    Guid WorkspaceId,
    Guid ProjectId,
    int CurrentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record MemoryEvent(
    Guid EventId,
    Guid WorkspaceId,
    Guid ProjectId,
    Guid StreamId,
    int StreamVersion,
    string Type,
    int SchemaVersion,
    DateTimeOffset RecordedAt,
    string AuthorSubject,
    string AuthorIssuer,
    string? ClientId,
    Guid? PayloadId,
    Guid? RefEventId
);

public sealed record MemoryPayload(
    Guid PayloadId,
    Guid WorkspaceId,
    Guid ProjectId,
    string Title,
    string Summary,
    string? Rationale,
    List<string> TechnicalKeys,
    List<Guid> RelatedIncidentIds,
    List<Evidence> Evidence,
    DateTimeOffset CreatedAt
);

public sealed record DecisionCurrent(
    Guid TopicId,
    Guid WorkspaceId,
    Guid ProjectId,
    string TopicKey,
    Guid? ActiveRecordId,
    int? ActiveRecordVersion,
    int StreamVersion,
    DecisionStatus Status,
    Guid? PendingProposalId,
    string Title,
    string Summary,
    string? Rationale,
    List<string> TechnicalKeys,
    List<Guid> RelatedIncidentIds,
    List<Evidence> Evidence,
    DateTimeOffset UpdatedAt
);

public sealed record IncidentCurrent(
    Guid IncidentId,
    Guid WorkspaceId,
    Guid ProjectId,
    int RecordVersion,
    int StreamVersion,
    IncidentStatus Status,
    string Title,
    string Summary,
    DateTimeOffset OccurredAt,
    List<string> TechnicalKeys,
    List<Evidence> Evidence,
    DateTimeOffset CreatedAt
);

public sealed record CommandReceipt(
    Guid ReceiptId,
    Guid WorkspaceId,
    string PrincipalId,
    string Operation,
    string IdempotencyKey,
    string ArgsHash,
    string ResultJson,
    DateTimeOffset CreatedAt
);

public sealed record ContextReceipt(
    Guid QueryId,
    Guid WorkspaceId,
    Guid ProjectId,
    string PrincipalId,
    string? ClientId,
    string SearchMode,
    DateTimeOffset CreatedAt,
    List<ContextReceiptItem> Items
);

public sealed record ContextReceiptItem(
    Guid ReceiptItemId,
    Guid QueryId,
    Guid WorkspaceId,
    int Position,
    string Kind,
    Guid StreamId,
    Guid RecordId,
    int RecordVersion,
    int StreamVersion,
    string Status
);

public sealed record ReconstitutedDecision(
    Guid TopicId,
    Guid WorkspaceId,
    Guid ProjectId,
    string TopicKey,
    int StreamVersion,
    DecisionStatus Status,
    Guid? ActiveRecordId,
    int? ActiveRecordVersion,
    Guid? PendingProposalId,
    string Title,
    string Summary,
    string? Rationale,
    List<string> TechnicalKeys,
    List<Guid> RelatedIncidentIds,
    List<Evidence> Evidence
);

public sealed record ReconstitutedIncident(
    Guid IncidentId,
    Guid WorkspaceId,
    Guid ProjectId,
    int StreamVersion,
    int RecordVersion,
    IncidentStatus Status,
    string Title,
    string Summary,
    DateTimeOffset OccurredAt,
    List<string> TechnicalKeys,
    List<Evidence> Evidence
);
