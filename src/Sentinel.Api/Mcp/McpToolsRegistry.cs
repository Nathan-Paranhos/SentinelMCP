namespace Sentinel.Api.Mcp;

public static class McpToolsRegistry
{
    public static readonly List<McpToolDefinition> AllTools =
    [
        new McpToolDefinition
        {
            Name = "propose_decision",
            Description = "Submits a new architecture decision proposal for maintainer review. Requires write:decision scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    topicKey = new { type = "string", description = "Topic key slug (1-80 chars ASCII)" },
                    expectedVersion = new { type = "integer", minimum = 0, description = "Expected version of the topic (0 for new)" },
                    title = new { type = "string", maxLength = 160, description = "Decision title" },
                    summary = new { type = "string", maxLength = 2000, description = "Executive summary of the decision" },
                    rationale = new { type = "string", maxLength = 4000, description = "Technical rationale and context" },
                    technicalKeys = new { type = "array", items = new { type = "string" }, description = "Associated technical keys (max 20)" },
                    relatedIncidentIds = new { type = "array", items = new { type = "string", format = "uuid" }, description = "IDs of related incidents (max 10)" },
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
                                type = new { type = "string", description = "Evidence type (pull_request, incident_rca, adr, etc.)" },
                                label = new { type = "string", description = "Human readable label" },
                                url = new { type = "string", format = "uri", description = "HTTPS reference URL" }
                            },
                            required = new[] { "type", "label", "url" }
                        },
                        description = "Evidence supporting this proposal"
                    },
                    idempotencyKey = new { type = "string", maxLength = 128, description = "Unique client-generated idempotency key" }
                },
                required = new[] { "workspaceId", "projectId", "topicKey", "expectedVersion", "title", "summary", "evidence", "idempotencyKey" }
            }
        },
        new McpToolDefinition
        {
            Name = "log_incident",
            Description = "Records an incident report with technical evidence. Requires write:incident scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    title = new { type = "string", maxLength = 160, description = "Incident title" },
                    summary = new { type = "string", maxLength = 2000, description = "Summary of the incident" },
                    occurredAt = new { type = "string", format = "date-time", description = "When incident occurred (ISO-8601)" },
                    technicalKeys = new { type = "array", items = new { type = "string" }, description = "Associated technical keys (max 20)" },
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
                                type = new { type = "string", description = "Evidence type (incident_rca, pull_request, etc.)" },
                                label = new { type = "string", description = "Label" },
                                url = new { type = "string", format = "uri", description = "HTTPS reference URL" }
                            },
                            required = new[] { "type", "label", "url" }
                        },
                        description = "Evidences for this incident"
                    },
                    idempotencyKey = new { type = "string", maxLength = 128, description = "Unique idempotency key" }
                },
                required = new[] { "workspaceId", "projectId", "title", "summary", "evidence", "idempotencyKey" }
            }
        },
        new McpToolDefinition
        {
            Name = "query_context",
            Description = "Queries active decisions or reported incidents in the workspace and saves an immutable context receipt. Requires read:memory scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    query = new { type = "string", maxLength = 300, description = "Search query or technical key" },
                    kind = new { type = "string", @enum = new[] { "decision", "incident" }, @default = "decision", description = "Kind of records to query" },
                    limit = new { type = "integer", minimum = 1, maximum = 20, @default = 10, description = "Max results to return" }
                },
                required = new[] { "workspaceId", "projectId", "query" }
            }
        },
        new McpToolDefinition
        {
            Name = "get_timeline",
            Description = "Returns the event stream and reconstituted state of a decision topic or incident. Requires read:memory scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    kind = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Stream kind" },
                    streamId = new { type = "string", format = "uuid", description = "Topic ID or Incident ID" },
                    atVersion = new { type = "integer", minimum = 1, description = "Reconstitute state at specific stream version" },
                    beforeVersion = new { type = "integer", minimum = 1, description = "Filter events before stream version (pagination)" },
                    limit = new { type = "integer", minimum = 1, maximum = 50, @default = 20, description = "Number of events to retrieve" }
                },
                required = new[] { "workspaceId", "projectId", "kind", "streamId" }
            }
        },
        new McpToolDefinition
        {
            Name = "get_context_receipt",
            Description = "Retrieves an immutable context query receipt containing the exact versions returned. Requires read:memory scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    queryId = new { type = "string", format = "uuid", description = "Query receipt ID" }
                },
                required = new[] { "workspaceId", "projectId", "queryId" }
            }
        }
    ];
}
