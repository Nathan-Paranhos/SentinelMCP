using Sentinel.Core.Models;

namespace Sentinel.Core.Queries;

public sealed record QueryContextQuery(
    Guid WorkspaceId,
    Guid ProjectId,
    string Query,
    string Kind = "decision",
    int Limit = 10,
    string PrincipalId = "",
    string? ClientId = null
);

public sealed record ContextItem(
    string Kind,
    Guid? TopicId,
    Guid? IncidentId,
    Guid RecordId,
    int RecordVersion,
    int StreamVersion,
    string Status,
    string Title,
    string Summary,
    List<Evidence> Evidence
);

public sealed record QueryContextResult(
    Guid QueryId,
    string MatchStatus,
    string SearchMode,
    List<ContextItem> Items
);

public sealed record GetTimelineQuery(
    Guid WorkspaceId,
    Guid ProjectId,
    string Kind,
    Guid StreamId,
    int? AtVersion = null,
    int? BeforeVersion = null,
    int Limit = 20
);

public sealed record TimelineEventItem(
    Guid EventId,
    int StreamVersion,
    string Type,
    DateTimeOffset RecordedAt,
    string AuthorSubject,
    string? ClientId,
    Guid? PayloadId
);

public sealed record GetTimelineResult(
    Guid StreamId,
    int CurrentStreamVersion,
    int TargetVersion,
    object ReconstitutedState,
    List<TimelineEventItem> Events,
    int? NextBeforeVersion
);

public sealed record GetContextReceiptQuery(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid QueryId,
    string PrincipalId
);

public sealed record GetContextReceiptResult(
    Guid QueryId,
    Guid WorkspaceId,
    Guid ProjectId,
    string PrincipalId,
    string? ClientId,
    string SearchMode,
    DateTimeOffset CreatedAt,
    List<ContextReceiptItem> Items
);
