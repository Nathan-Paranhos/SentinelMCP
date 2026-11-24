using Sentinel.Core.Models;

namespace Sentinel.Core.Reducers;

public static class IncidentReducer
{
    public static ReconstitutedIncident Reconstitute(
        Guid incidentId,
        Guid workspaceId,
        Guid projectId,
        IEnumerable<MemoryEvent> events,
        Func<Guid, MemoryPayload?> payloadResolver,
        int? atVersion = null)
    {
        var filteredEvents = events
            .Where(e => !atVersion.HasValue || e.StreamVersion <= atVersion.Value)
            .OrderBy(e => e.StreamVersion);

        int currentStreamVersion = 0;
        int recordVersion = 1;
        var status = IncidentStatus.Reported;
        MemoryPayload? latestPayload = null;

        foreach (var ev in filteredEvents)
        {
            currentStreamVersion = ev.StreamVersion;
            recordVersion = ev.StreamVersion;
            var payload = ev.PayloadId.HasValue ? payloadResolver(ev.PayloadId.Value) : null;
            if (payload != null)
            {
                latestPayload = payload;
            }

            if (ev.Type == "IncidentReported")
            {
                status = IncidentStatus.Reported;
            }
        }

        return new ReconstitutedIncident(
            IncidentId: incidentId,
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            StreamVersion: currentStreamVersion,
            RecordVersion: recordVersion,
            Status: status,
            Title: latestPayload?.Title ?? string.Empty,
            Summary: latestPayload?.Summary ?? string.Empty,
            OccurredAt: latestPayload?.CreatedAt ?? DateTimeOffset.UtcNow,
            TechnicalKeys: latestPayload?.TechnicalKeys ?? [],
            Evidence: latestPayload?.Evidence ?? []
        );
    }
}
