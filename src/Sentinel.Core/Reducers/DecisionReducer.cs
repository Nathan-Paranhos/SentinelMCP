using Sentinel.Core.Models;

namespace Sentinel.Core.Reducers;

public static class DecisionReducer
{
    public static ReconstitutedDecision Reconstitute(
        Guid topicId,
        Guid workspaceId,
        Guid projectId,
        string topicKey,
        IEnumerable<MemoryEvent> events,
        Func<Guid, MemoryPayload?> payloadResolver,
        int? atVersion = null)
    {
        var filteredEvents = events
            .Where(e => !atVersion.HasValue || e.StreamVersion <= atVersion.Value)
            .OrderBy(e => e.StreamVersion);

        int currentStreamVersion = 0;
        DecisionStatus status = DecisionStatus.Proposed;
        Guid? activeRecordId = null;
        int? activeRecordVersion = null;
        Guid? pendingProposalId = null;
        MemoryPayload? activePayload = null;
        MemoryPayload? latestPayload = null;

        foreach (var ev in filteredEvents)
        {
            currentStreamVersion = ev.StreamVersion;
            var payload = ev.PayloadId.HasValue ? payloadResolver(ev.PayloadId.Value) : null;
            if (payload != null)
            {
                latestPayload = payload;
            }

            switch (ev.Type)
            {
                case "DecisionProposed":
                    pendingProposalId = ev.PayloadId;
                    if (activeRecordId == null)
                    {
                        status = DecisionStatus.Proposed;
                    }
                    break;

                case "DecisionApproved":
                    activeRecordId = ev.PayloadId ?? pendingProposalId;
                    activeRecordVersion = ev.StreamVersion;
                    activePayload = payload ?? (activeRecordId.HasValue ? payloadResolver(activeRecordId.Value) : null);
                    pendingProposalId = null;
                    status = DecisionStatus.Active;
                    break;

                case "DecisionRejected":
                    pendingProposalId = null;
                    if (activeRecordId == null)
                    {
                        status = DecisionStatus.Rejected;
                    }
                    break;

                case "DecisionSuperseded":
                    status = DecisionStatus.Superseded;
                    break;
            }
        }

        var displayPayload = activePayload ?? latestPayload;

        return new ReconstitutedDecision(
            TopicId: topicId,
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            TopicKey: topicKey,
            StreamVersion: currentStreamVersion,
            Status: status,
            ActiveRecordId: activeRecordId,
            ActiveRecordVersion: activeRecordVersion,
            PendingProposalId: pendingProposalId,
            Title: displayPayload?.Title ?? string.Empty,
            Summary: displayPayload?.Summary ?? string.Empty,
            Rationale: displayPayload?.Rationale,
            TechnicalKeys: displayPayload?.TechnicalKeys ?? [],
            RelatedIncidentIds: displayPayload?.RelatedIncidentIds ?? [],
            Evidence: displayPayload?.Evidence ?? []
        );
    }
}
