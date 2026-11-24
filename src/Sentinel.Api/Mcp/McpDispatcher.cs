using System.Text.Json;
using Sentinel.Api.Auth;
using Sentinel.Core.Commands;
using Sentinel.Core.Exceptions;
using Sentinel.Core.Models;
using Sentinel.Core.Queries;
using Sentinel.Core.Reducers;
using Sentinel.Persistence.Contracts;

namespace Sentinel.Api.Mcp;

public sealed class McpDispatcher
{
    private readonly IIdentityService _identityService;
    private readonly IWorkspaceRepository _workspaceRepo;
    private readonly IDecisionRepository _decisionRepo;
    private readonly IIncidentRepository _incidentRepo;
    private readonly IContextRepository _contextRepo;

    public McpDispatcher(
        IIdentityService identityService,
        IWorkspaceRepository workspaceRepo,
        IDecisionRepository decisionRepo,
        IIncidentRepository incidentRepo,
        IContextRepository contextRepo)
    {
        _identityService = identityService;
        _workspaceRepo = workspaceRepo;
        _decisionRepo = decisionRepo;
        _incidentRepo = incidentRepo;
        _contextRepo = contextRepo;
    }

    public async Task<McpToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        try
        {
            return toolName switch
            {
                "propose_decision" => await HandleProposeDecisionAsync(arguments, httpContext, ct),
                "log_incident" => await HandleLogIncidentAsync(arguments, httpContext, ct),
                "query_context" => await HandleQueryContextAsync(arguments, httpContext, ct),
                "get_timeline" => await HandleGetTimelineAsync(arguments, httpContext, ct),
                "get_context_receipt" => await HandleGetContextReceiptAsync(arguments, httpContext, ct),
                _ => McpToolResult.Error("NOT_FOUND", $"Tool '{toolName}' is not recognized.")
            };
        }
        catch (VersionConflictException ex)
        {
            return McpToolResult.Error(ex.Code, ex.Message, new
            {
                topicId = ex.TopicId,
                expectedVersion = ex.ExpectedVersion,
                actualVersion = ex.ActualVersion
            });
        }
        catch (DomainException ex)
        {
            return McpToolResult.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return McpToolResult.Error("INTERNAL_ERROR", ex.Message);
        }
    }

    private async Task<McpToolResult> HandleProposeDecisionAsync(JsonElement args, HttpContext httpContext, CancellationToken ct)
    {
        var workspaceId = args.GetProperty("workspaceId").GetGuid();
        var projectId = args.GetProperty("projectId").GetGuid();

        var membership = await _identityService.AuthenticateAndAuthorizeAsync(
            httpContext, workspaceId, "write:decision", UserRole.Contributor, ct);
        var identity = _identityService.ResolveIdentity(httpContext);

        var project = await _workspaceRepo.GetProjectAsync(workspaceId, projectId, ct);
        if (project == null)
            throw new NotFoundException($"Project '{projectId}' not found in workspace.");

        var topicKey = args.GetProperty("topicKey").GetString() ?? string.Empty;
        var expectedVersion = args.GetProperty("expectedVersion").GetInt32();
        var title = args.GetProperty("title").GetString() ?? string.Empty;
        var summary = args.GetProperty("summary").GetString() ?? string.Empty;
        var rationale = args.TryGetProperty("rationale", out var ratElem) ? ratElem.GetString() : null;

        List<string>? technicalKeys = null;
        if (args.TryGetProperty("technicalKeys", out var tkElem) && tkElem.ValueKind == JsonValueKind.Array)
        {
            technicalKeys = tkElem.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        List<Guid>? relatedIncidents = null;
        if (args.TryGetProperty("relatedIncidentIds", out var incElem) && incElem.ValueKind == JsonValueKind.Array)
        {
            relatedIncidents = incElem.EnumerateArray().Select(x => x.GetGuid()).ToList();
        }

        var evidence = new List<Evidence>();
        if (args.TryGetProperty("evidence", out var evElem) && evElem.ValueKind == JsonValueKind.Array)
        {
            evidence = evElem.EnumerateArray().Select(e => new Evidence(
                e.GetProperty("type").GetString() ?? string.Empty,
                e.GetProperty("label").GetString() ?? string.Empty,
                e.GetProperty("url").GetString() ?? string.Empty
            )).ToList();
        }

        var idempotencyKey = args.GetProperty("idempotencyKey").GetString() ?? string.Empty;

        var command = new ProposeDecisionCommand(
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            TopicKey: topicKey,
            ExpectedVersion: expectedVersion,
            Title: title,
            Summary: summary,
            Rationale: rationale,
            TechnicalKeys: technicalKeys,
            RelatedIncidentIds: relatedIncidents,
            Evidence: evidence,
            IdempotencyKey: idempotencyKey,
            AuthorSubject: identity.Subject,
            AuthorIssuer: identity.Issuer,
            ClientId: identity.ClientId
        );

        var result = await _decisionRepo.ProposeDecisionAsync(command, ct);
        return McpToolResult.Success(result, $"Decision proposal created for topic '{topicKey}'. Proposal ID: {result.ProposalId}, stream version: {result.StreamVersion}.");
    }

    private async Task<McpToolResult> HandleLogIncidentAsync(JsonElement args, HttpContext httpContext, CancellationToken ct)
    {
        var workspaceId = args.GetProperty("workspaceId").GetGuid();
        var projectId = args.GetProperty("projectId").GetGuid();

        var membership = await _identityService.AuthenticateAndAuthorizeAsync(
            httpContext, workspaceId, "write:incident", UserRole.Contributor, ct);
        var identity = _identityService.ResolveIdentity(httpContext);

        var project = await _workspaceRepo.GetProjectAsync(workspaceId, projectId, ct);
        if (project == null)
            throw new NotFoundException($"Project '{projectId}' not found in workspace.");

        var title = args.GetProperty("title").GetString() ?? string.Empty;
        var summary = args.GetProperty("summary").GetString() ?? string.Empty;
        DateTimeOffset? occurredAt = args.TryGetProperty("occurredAt", out var occElem) && occElem.ValueKind == JsonValueKind.String
            ? occElem.GetDateTimeOffset()
            : null;

        List<string>? technicalKeys = null;
        if (args.TryGetProperty("technicalKeys", out var tkElem) && tkElem.ValueKind == JsonValueKind.Array)
        {
            technicalKeys = tkElem.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        var evidence = new List<Evidence>();
        if (args.TryGetProperty("evidence", out var evElem) && evElem.ValueKind == JsonValueKind.Array)
        {
            evidence = evElem.EnumerateArray().Select(e => new Evidence(
                e.GetProperty("type").GetString() ?? string.Empty,
                e.GetProperty("label").GetString() ?? string.Empty,
                e.GetProperty("url").GetString() ?? string.Empty
            )).ToList();
        }

        var idempotencyKey = args.GetProperty("idempotencyKey").GetString() ?? string.Empty;

        var command = new LogIncidentCommand(
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            Title: title,
            Summary: summary,
            OccurredAt: occurredAt,
            TechnicalKeys: technicalKeys,
            Evidence: evidence,
            IdempotencyKey: idempotencyKey,
            AuthorSubject: identity.Subject,
            AuthorIssuer: identity.Issuer,
            ClientId: identity.ClientId
        );

        var result = await _incidentRepo.LogIncidentAsync(command, ct);
        return McpToolResult.Success(result, $"Incident logged successfully with ID {result.IncidentId}, version {result.StreamVersion}.");
    }

    private async Task<McpToolResult> HandleQueryContextAsync(JsonElement args, HttpContext httpContext, CancellationToken ct)
    {
        var workspaceId = args.GetProperty("workspaceId").GetGuid();
        var projectId = args.GetProperty("projectId").GetGuid();

        var membership = await _identityService.AuthenticateAndAuthorizeAsync(
            httpContext, workspaceId, "read:memory", UserRole.Reader, ct);
        var identity = _identityService.ResolveIdentity(httpContext);

        var project = await _workspaceRepo.GetProjectAsync(workspaceId, projectId, ct);
        if (project == null)
            throw new NotFoundException($"Project '{projectId}' not found in workspace.");

        var queryStr = args.GetProperty("query").GetString() ?? string.Empty;
        var kind = args.TryGetProperty("kind", out var kindElem) ? (kindElem.GetString() ?? "decision") : "decision";
        var limit = args.TryGetProperty("limit", out var limElem) ? limElem.GetInt32() : 10;

        var query = new QueryContextQuery(
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            Query: queryStr,
            Kind: kind,
            Limit: limit,
            PrincipalId: identity.Subject,
            ClientId: identity.ClientId
        );

        var result = await _contextRepo.QueryContextAsync(query, ct);

        var summaryText = result.Items.Count == 0
            ? $"No matching {kind} records found for query '{queryStr}'."
            : $"Found {result.Items.Count} matching {kind}(s). Query ID receipt: {result.QueryId}.";

        return McpToolResult.Success(result, summaryText);
    }

    private async Task<McpToolResult> HandleGetTimelineAsync(JsonElement args, HttpContext httpContext, CancellationToken ct)
    {
        var workspaceId = args.GetProperty("workspaceId").GetGuid();
        var projectId = args.GetProperty("projectId").GetGuid();

        var membership = await _identityService.AuthenticateAndAuthorizeAsync(
            httpContext, workspaceId, "read:memory", UserRole.Reader, ct);

        var project = await _workspaceRepo.GetProjectAsync(workspaceId, projectId, ct);
        if (project == null)
            throw new NotFoundException($"Project '{projectId}' not found in workspace.");

        var kind = args.GetProperty("kind").GetString() ?? "decision";
        var streamId = args.GetProperty("streamId").GetGuid();
        int? atVersion = args.TryGetProperty("atVersion", out var atElem) && atElem.ValueKind == JsonValueKind.Number ? atElem.GetInt32() : null;
        int? beforeVersion = args.TryGetProperty("beforeVersion", out var bElem) && bElem.ValueKind == JsonValueKind.Number ? bElem.GetInt32() : null;
        int limit = args.TryGetProperty("limit", out var lElem) && lElem.ValueKind == JsonValueKind.Number ? lElem.GetInt32() : 20;

        if (string.Equals(kind, "decision", StringComparison.OrdinalIgnoreCase))
        {
            var topic = await _decisionRepo.GetTopicByIdAsync(workspaceId, projectId, streamId, ct);
            if (topic == null)
                throw new NotFoundException($"Decision topic '{streamId}' not found in workspace.");

            var events = await _decisionRepo.GetEventsByStreamAsync(workspaceId, streamId, atVersion, beforeVersion, limit, ct);
            var reconstituted = DecisionReducer.Reconstitute(
                topic.TopicId, workspaceId, projectId, topic.TopicKey, events,
                pid => _decisionRepo.GetPayloadAsync(workspaceId, pid, ct).GetAwaiter().GetResult(),
                atVersion
            );

            var eventItems = events.Select(e => new TimelineEventItem(
                e.EventId, e.StreamVersion, e.Type, e.RecordedAt, e.AuthorSubject, e.ClientId, e.PayloadId
            )).ToList();

            var result = new GetTimelineResult(
                StreamId: streamId,
                CurrentStreamVersion: topic.CurrentVersion,
                TargetVersion: atVersion ?? topic.CurrentVersion,
                ReconstitutedState: reconstituted,
                Events: eventItems,
                NextBeforeVersion: events.Count == limit ? events.Last().StreamVersion : null
            );

            return McpToolResult.Success(result, $"Timeline for decision '{topic.TopicKey}' at version {result.TargetVersion}. Status: {reconstituted.Status}.");
        }
        else if (string.Equals(kind, "incident", StringComparison.OrdinalIgnoreCase))
        {
            var incident = await _incidentRepo.GetIncidentByIdAsync(workspaceId, projectId, streamId, ct);
            if (incident == null)
                throw new NotFoundException($"Incident stream '{streamId}' not found in workspace.");

            var events = await _incidentRepo.GetEventsByStreamAsync(workspaceId, streamId, atVersion, beforeVersion, limit, ct);
            var reconstituted = IncidentReducer.Reconstitute(
                incident.IncidentId, workspaceId, projectId, events,
                pid => _incidentRepo.GetPayloadAsync(workspaceId, pid, ct).GetAwaiter().GetResult(),
                atVersion
            );

            var eventItems = events.Select(e => new TimelineEventItem(
                e.EventId, e.StreamVersion, e.Type, e.RecordedAt, e.AuthorSubject, e.ClientId, e.PayloadId
            )).ToList();

            var result = new GetTimelineResult(
                StreamId: streamId,
                CurrentStreamVersion: incident.CurrentVersion,
                TargetVersion: atVersion ?? incident.CurrentVersion,
                ReconstitutedState: reconstituted,
                Events: eventItems,
                NextBeforeVersion: events.Count == limit ? events.Last().StreamVersion : null
            );

            return McpToolResult.Success(result, $"Timeline for incident '{incident.IncidentId}'. Status: {reconstituted.Status}.");
        }

        throw new ValidationException($"Unsupported kind '{kind}'. Expected 'decision' or 'incident'.");
    }

    private async Task<McpToolResult> HandleGetContextReceiptAsync(JsonElement args, HttpContext httpContext, CancellationToken ct)
    {
        var workspaceId = args.GetProperty("workspaceId").GetGuid();
        var projectId = args.GetProperty("projectId").GetGuid();
        var queryId = args.GetProperty("queryId").GetGuid();

        var membership = await _identityService.AuthenticateAndAuthorizeAsync(
            httpContext, workspaceId, "read:memory", UserRole.Reader, ct);
        var identity = _identityService.ResolveIdentity(httpContext);

        var project = await _workspaceRepo.GetProjectAsync(workspaceId, projectId, ct);
        if (project == null)
            throw new NotFoundException($"Project '{projectId}' not found in workspace.");

        var receipt = await _contextRepo.GetContextReceiptAsync(workspaceId, projectId, queryId, identity.Subject, ct);
        return McpToolResult.Success(receipt, $"Receipt {receipt.QueryId} contains {receipt.Items.Count} verified reference(s).");
    }
}
