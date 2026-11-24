namespace Sentinel.Api.Mcp;

public static class McpToolsRegistry
{
    public static readonly List<McpToolDefinition> AllTools =
    [
        new McpToolDefinition
        {
            Name = "propose_decision",
            Title = "Propose Architecture Decision",
            Description = "Submits an architecture decision proposal for human maintainer review (agents cannot approve). Only one pending proposal per topic: returns PROPOSAL_PENDING while one awaits review. Use list_workspaces first if workspaceId/projectId is unknown. Requires write:decision scope.",
            Annotations = new McpToolAnnotations
            {
                Title = "Propose Architecture Decision",
                ReadOnlyHint = false,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            },
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace UUID (discover via list_workspaces)" },
                    projectId = new { type = "string", format = "uuid", description = "Project UUID (discover via list_workspaces)" },
                    topicKey = new { type = "string", minLength = 1, maxLength = 80, pattern = @"^[a-zA-Z0-9_\-\.]{1,80}$", description = "Topic key slug (1-80 chars ASCII, case-insensitive)" },
                    expectedVersion = new { type = "integer", minimum = 0, description = "Expected stream version of the topic (0 for new topic)" },
                    title = new { type = "string", minLength = 1, maxLength = 160, description = "Decision title" },
                    summary = new { type = "string", minLength = 1, maxLength = 2000, description = "Executive summary of the decision" },
                    rationale = new { type = "string", maxLength = 4000, description = "Technical rationale and architectural trade-offs" },
                    technicalKeys = new { type = "array", maxItems = 20, items = new { type = "string", minLength = 1, maxLength = 100 }, description = "Associated technical identifiers (max 20)" },
                    relatedIncidentIds = new { type = "array", maxItems = 10, items = new { type = "string", format = "uuid" }, description = "UUIDs of related incidents (max 10)" },
                    evidence = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 10,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                type = new { type = "string", description = "Evidence type (pull_request, incident_rca, adr, doc, etc.)" },
                                label = new { type = "string", description = "Human-readable label" },
                                url = new { type = "string", format = "uri", pattern = "^https://", maxLength = 2048, description = "HTTPS reference URL (no embedded credentials or secret query params)" }
                            },
                            required = new[] { "type", "label", "url" }
                        },
                        description = "HTTPS evidence supporting this proposal"
                    },
                    idempotencyKey = new { type = "string", minLength = 1, maxLength = 128, description = "Unique client-generated idempotency key" }
                },
                required = new[] { "workspaceId", "projectId", "topicKey", "expectedVersion", "title", "summary", "evidence", "idempotencyKey" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    topicId = new { type = "string", format = "uuid", description = "Decision topic ID" },
                    proposalId = new { type = "string", format = "uuid", description = "Submitted proposal ID" },
                    streamVersion = new { type = "integer", minimum = 1, description = "Decision stream version" },
                    status = new { type = "string", description = "Proposal status (proposed)" }
                },
                required = new[] { "topicId", "proposalId", "streamVersion", "status" }
            }
        },
        new McpToolDefinition
        {
            Name = "log_incident",
            Title = "Log Engineering Incident",
            Description = "Records an incident report with technical keys and HTTPS evidence. Use list_workspaces first if workspaceId/projectId is unknown. Requires write:incident scope.",
            Annotations = new McpToolAnnotations
            {
                Title = "Log Engineering Incident",
                ReadOnlyHint = false,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            },
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace UUID" },
                    projectId = new { type = "string", format = "uuid", description = "Project UUID" },
                    title = new { type = "string", minLength = 1, maxLength = 160, description = "Incident title" },
                    summary = new { type = "string", minLength = 1, maxLength = 2000, description = "Executive summary of the incident and mitigation" },
                    occurredAt = new { type = "string", format = "date-time", description = "When the incident occurred (ISO-8601 UTC)" },
                    technicalKeys = new { type = "array", maxItems = 20, items = new { type = "string", minLength = 1, maxLength = 100 }, description = "Associated technical identifiers (max 20)" },
                    evidence = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 10,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                type = new { type = "string", description = "Evidence type (incident_rca, pull_request, postmortem, etc.)" },
                                label = new { type = "string", description = "Human-readable label" },
                                url = new { type = "string", format = "uri", pattern = "^https://", maxLength = 2048, description = "HTTPS reference URL (no credentials or secret query params)" }
                            },
                            required = new[] { "type", "label", "url" }
                        },
                        description = "HTTPS evidence supporting this incident"
                    },
                    idempotencyKey = new { type = "string", minLength = 1, maxLength = 128, description = "Unique idempotency key" }
                },
                required = new[] { "workspaceId", "projectId", "title", "summary", "evidence", "idempotencyKey" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    incidentId = new { type = "string", format = "uuid", description = "Recorded incident ID" },
                    streamVersion = new { type = "integer", minimum = 1, description = "Incident stream version" },
                    status = new { type = "string", description = "Incident status (reported)" }
                },
                required = new[] { "incidentId", "streamVersion", "status" }
            }
        },
        new McpToolDefinition
        {
            Name = "query_context",
            Title = "Query Engineering Context",
            Description = "Queries active decisions and/or reported incidents in the workspace/project and issues an auditable context receipt with a SHA-256 digest. Exact technical-key matches rank first. Call list_workspaces first if workspaceId/projectId is unknown. Requires read:memory scope.",
            Annotations = new McpToolAnnotations
            {
                Title = "Query Engineering Context",
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = false,
                OpenWorldHint = false
            },
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace UUID" },
                    projectId = new { type = "string", format = "uuid", description = "Project UUID" },
                    query = new { type = "string", minLength = 1, maxLength = 300, description = "Search query terms or exact technical key" },
                    kind = new { type = "string", @enum = new[] { "decision", "incident", "both" }, @default = "decision", description = "Record types to search" },
                    limit = new { type = "integer", minimum = 1, maximum = 20, @default = 10, description = "Maximum items to return (1-20)" }
                },
                required = new[] { "workspaceId", "projectId", "query" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    queryId = new { type = "string", format = "uuid", description = "Generated context receipt ID" },
                    matchStatus = new { type = "string", description = "Match status (found, none)" },
                    searchMode = new { type = "string", description = "Search mode applied (textual)" },
                    items = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                recordType = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Record type" },
                                topicId = new { type = "string", format = "uuid", description = "Topic ID (for decisions)" },
                                incidentId = new { type = "string", format = "uuid", description = "Incident ID (for incidents)" },
                                recordId = new { type = "string", format = "uuid", description = "Active version payload ID" },
                                recordVersion = new { type = "integer", minimum = 1, description = "Record version number" },
                                streamVersion = new { type = "integer", minimum = 1, description = "Event stream version number" },
                                status = new { type = "string", description = "Current status (active, reported)" },
                                title = new { type = "string", description = "Record title" },
                                summary = new { type = "string", description = "Record summary" },
                                evidence = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            type = new { type = "string", description = "Evidence type" },
                                            label = new { type = "string", description = "Evidence label" },
                                            url = new { type = "string", format = "uri", description = "HTTPS URL" }
                                        },
                                        required = new[] { "type", "label", "url" }
                                    }
                                }
                            },
                            required = new[] { "recordType", "recordId", "recordVersion", "streamVersion", "status", "title", "summary", "evidence" }
                        }
                    }
                },
                required = new[] { "queryId", "matchStatus", "searchMode", "items" }
            }
        },
        new McpToolDefinition
        {
            Name = "get_timeline",
            Title = "Get Stream Timeline & State",
            Description = "Returns the immutable event stream and deterministic reconstituted state of a decision topic or incident at current or historical version. Requires read:memory scope.",
            Annotations = new McpToolAnnotations
            {
                Title = "Get Stream Timeline & State",
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            },
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace UUID" },
                    projectId = new { type = "string", format = "uuid", description = "Project UUID" },
                    kind = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Stream kind" },
                    streamId = new { type = "string", format = "uuid", description = "Topic ID or Incident ID" },
                    atVersion = new { type = "integer", minimum = 1, description = "Reconstitute state at a specific stream version" },
                    beforeVersion = new { type = "integer", minimum = 1, description = "Reverse pagination cursor (use nextBeforeVersion)" },
                    limit = new { type = "integer", minimum = 1, maximum = 50, @default = 20, description = "Number of events to retrieve (1-50)" }
                },
                required = new[] { "workspaceId", "projectId", "kind", "streamId" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Stream kind" },
                    streamId = new { type = "string", format = "uuid", description = "Stream ID" },
                    currentStreamVersion = new { type = "integer", minimum = 1, description = "Latest stream version" },
                    targetVersion = new { type = "integer", minimum = 1, description = "Reconstituted target version" },
                    reconstitutedState = new { type = "object", description = "Full reconstituted state at targetVersion" },
                    events = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                eventId = new { type = "string", format = "uuid", description = "Immutable event ID" },
                                streamVersion = new { type = "integer", minimum = 1, description = "Stream version at this event" },
                                type = new { type = "string", description = "Domain event type" },
                                recordedAt = new { type = "string", format = "date-time", description = "UTC timestamp" },
                                authorSubject = new { type = "string", description = "Actor subject" },
                                clientId = new { type = "string", description = "Client ID" },
                                payloadId = new { type = "string", format = "uuid", description = "Referenced payload ID" },
                                refEventId = new { type = "string", format = "uuid", description = "Referenced event ID" },
                                reason = new { type = "string", description = "Rejection or audit reason" }
                            },
                            required = new[] { "eventId", "streamVersion", "type", "recordedAt", "authorSubject", "clientId" }
                        }
                    },
                    nextBeforeVersion = new { type = "integer", description = "Cursor for older events" }
                },
                required = new[] { "kind", "streamId", "currentStreamVersion", "targetVersion", "reconstitutedState", "events" }
            }
        },
        new McpToolDefinition
        {
            Name = "get_context_receipt",
            Title = "Verify Context Receipt",
            Description = "Retrieves a context query receipt with exact ordered item versions and live SHA-256 integrity verification. Visible to the receipt author and workspace maintainers. Requires read:memory scope.",
            Annotations = new McpToolAnnotations
            {
                Title = "Verify Context Receipt",
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            },
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace UUID" },
                    projectId = new { type = "string", format = "uuid", description = "Project UUID" },
                    queryId = new { type = "string", format = "uuid", description = "Context query receipt ID" }
                },
                required = new[] { "workspaceId", "projectId", "queryId" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    queryId = new { type = "string", format = "uuid", description = "Receipt query ID" },
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    principalId = new { type = "string", description = "Subject who executed the query" },
                    principalIssuer = new { type = "string", description = "Subject credential issuer" },
                    clientId = new { type = "string", description = "Client identifier" },
                    searchMode = new { type = "string", description = "Search mode" },
                    createdAt = new { type = "string", format = "date-time", description = "Query UTC timestamp" },
                    digest = new { type = "string", description = "Canonical SHA-256 hex digest" },
                    digestAlgorithm = new { type = "string", description = "Digest algorithm (sha256)" },
                    integrityVerified = new { type = "boolean", description = "True when recalculated digest matches stored digest" },
                    items = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                position = new { type = "integer", minimum = 0, description = "Result position" },
                                recordType = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Record type" },
                                streamId = new { type = "string", format = "uuid", description = "Stream ID" },
                                recordId = new { type = "string", format = "uuid", description = "Record payload ID" },
                                recordVersion = new { type = "integer", minimum = 1, description = "Record version at query time" },
                                streamVersion = new { type = "integer", minimum = 1, description = "Stream version at query time" },
                                status = new { type = "string", description = "Status at query time" }
                            },
                            required = new[] { "position", "recordType", "streamId", "recordId", "recordVersion", "streamVersion", "status" }
                        }
                    }
                },
                required = new[] { "queryId", "workspaceId", "projectId", "principalId", "principalIssuer", "clientId", "searchMode", "createdAt", "digest", "digestAlgorithm", "integrityVerified", "items" }
            }
        },
        new McpToolDefinition
        {
            Name = "list_workspaces",
            Title = "List Workspaces & Projects",
            Description = "Lists workspaces, roles, and projects accessible to the authenticated agent identity. Call this tool first when workspaceId or projectId is not provided in the prompt. Requires read:memory scope.",
            Annotations = new McpToolAnnotations
            {
                Title = "List Workspaces & Projects",
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            },
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    includeProjects = new { type = "boolean", @default = true, description = "Include projects inside each workspace" }
                },
                required = Array.Empty<string>()
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    actorSubject = new { type = "string", description = "Authenticated agent subject" },
                    actorIssuer = new { type = "string", description = "Credential issuer" },
                    workspaces = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                workspaceId = new { type = "string", format = "uuid", description = "Workspace UUID" },
                                name = new { type = "string", description = "Workspace display name" },
                                slug = new { type = "string", description = "Workspace slug" },
                                role = new { type = "string", description = "Agent role in workspace (Reader, Contributor, Maintainer)" },
                                projects = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            projectId = new { type = "string", format = "uuid", description = "Project UUID" },
                                            name = new { type = "string", description = "Project name" },
                                            slug = new { type = "string", description = "Project slug" }
                                        },
                                        required = new[] { "projectId", "name", "slug" }
                                    }
                                }
                            },
                            required = new[] { "workspaceId", "name", "slug", "role", "projects" }
                        }
                    }
                },
                required = new[] { "actorSubject", "actorIssuer", "workspaces" }
            }
        }
    ];
}
