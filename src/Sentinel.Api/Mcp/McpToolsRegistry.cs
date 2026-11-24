namespace Sentinel.Api.Mcp;

public static class McpToolsRegistry
{
    public static readonly List<McpToolDefinition> AllTools =
    [
        new McpToolDefinition
        {
            Name = "propose_decision",
            Description = "Submits an architecture decision proposal for human maintainer review (agents cannot approve). Only one pending proposal per topic: returns PROPOSAL_PENDING while one awaits review. Requires write:decision scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    topicKey = new { type = "string", minLength = 1, maxLength = 80, pattern = @"^[a-zA-Z0-9_\-\.]{1,80}$", description = "Topic key slug (1-80 chars ASCII, case-insensitive)" },
                    expectedVersion = new { type = "integer", minimum = 0, description = "Expected version of the topic (0 for new)" },
                    title = new { type = "string", minLength = 1, maxLength = 160, description = "Decision title" },
                    summary = new { type = "string", minLength = 1, maxLength = 2000, description = "Executive summary of the decision" },
                    rationale = new { type = "string", maxLength = 4000, description = "Technical rationale and context" },
                    technicalKeys = new { type = "array", maxItems = 20, items = new { type = "string", minLength = 1, maxLength = 100 }, description = "Associated technical keys (max 20)" },
                    relatedIncidentIds = new { type = "array", maxItems = 10, items = new { type = "string", format = "uuid" }, description = "IDs of related incidents (max 10)" },
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
                                url = new { type = "string", format = "uri", pattern = "^https://", maxLength = 2048, description = "HTTPS reference URL (no credentials or secret query parameters)" }
                            },
                            required = new[] { "type", "label", "url" }
                        },
                        description = "Evidence supporting this proposal"
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
                    topicId = new { type = "string", format = "uuid", description = "ID do tópico de decisão" },
                    proposalId = new { type = "string", format = "uuid", description = "ID da proposta submetida" },
                    streamVersion = new { type = "integer", minimum = 1, description = "Versão do fluxo da decisão" },
                    status = new { type = "string", description = "Situação da proposta (ex.: pending)" }
                },
                required = new[] { "topicId", "proposalId", "streamVersion", "status" }
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
                    title = new { type = "string", minLength = 1, maxLength = 160, description = "Incident title" },
                    summary = new { type = "string", minLength = 1, maxLength = 2000, description = "Summary of the incident" },
                    occurredAt = new { type = "string", format = "date-time", description = "When incident occurred (ISO-8601)" },
                    technicalKeys = new { type = "array", maxItems = 20, items = new { type = "string", minLength = 1, maxLength = 100 }, description = "Associated technical keys (max 20)" },
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
                                url = new { type = "string", format = "uri", pattern = "^https://", maxLength = 2048, description = "HTTPS reference URL (no credentials or secret query parameters)" }
                            },
                            required = new[] { "type", "label", "url" }
                        },
                        description = "Evidences for this incident"
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
                    incidentId = new { type = "string", format = "uuid", description = "ID do incidente registrado" },
                    streamVersion = new { type = "integer", minimum = 1, description = "Versão do fluxo do incidente" },
                    status = new { type = "string", description = "Situação do incidente (ex.: active)" }
                },
                required = new[] { "incidentId", "streamVersion", "status" }
            }
        },
        new McpToolDefinition
        {
            Name = "query_context",
            Description = "Queries active decisions or reported incidents in the workspace and saves a context receipt (versions returned + SHA-256 digest). Exact technical-key matches rank first. Requires read:memory scope.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    workspaceId = new { type = "string", format = "uuid", description = "Workspace ID" },
                    projectId = new { type = "string", format = "uuid", description = "Project ID" },
                    query = new { type = "string", minLength = 1, maxLength = 300, description = "Search query or technical key" },
                    kind = new { type = "string", @enum = new[] { "decision", "incident", "both" }, @default = "decision", description = "Kind of records to query" },
                    limit = new { type = "integer", minimum = 1, maximum = 20, @default = 10, description = "Max results to return" }
                },
                required = new[] { "workspaceId", "projectId", "query" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    queryId = new { type = "string", format = "uuid", description = "ID único do comprovante de consulta gerado" },
                    matchStatus = new { type = "string", description = "Status de correspondência (exact, partial, semantic, not_found)" },
                    searchMode = new { type = "string", description = "Modo de busca utilizado (technical_exact, text_search)" },
                    items = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                recordType = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Tipo do registro" },
                                topicId = new { type = "string", format = "uuid", description = "ID do tópico (se decisão)" },
                                incidentId = new { type = "string", format = "uuid", description = "ID do incidente (se incidente)" },
                                recordId = new { type = "string", format = "uuid", description = "ID da versão ativa do registro" },
                                recordVersion = new { type = "integer", minimum = 1, description = "Número da versão do registro" },
                                streamVersion = new { type = "integer", minimum = 1, description = "Número da versão do fluxo de eventos" },
                                status = new { type = "string", description = "Situação atual (active, deprecated, etc.)" },
                                title = new { type = "string", description = "Título do registro" },
                                summary = new { type = "string", description = "Resumo executivo do registro" },
                                evidence = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            type = new { type = "string", description = "Tipo de evidência" },
                                            label = new { type = "string", description = "Rótulo descritivo" },
                                            url = new { type = "string", format = "uri", description = "URL HTTPS da evidência" }
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
                    beforeVersion = new { type = "integer", minimum = 1, description = "Return events older than this stream version (use nextBeforeVersion from the previous page)" },
                    limit = new { type = "integer", minimum = 1, maximum = 50, @default = 20, description = "Number of events to retrieve" }
                },
                required = new[] { "workspaceId", "projectId", "kind", "streamId" }
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Tipo do fluxo" },
                    streamId = new { type = "string", format = "uuid", description = "ID do fluxo" },
                    currentStreamVersion = new { type = "integer", minimum = 1, description = "Versão mais recente do fluxo" },
                    targetVersion = new { type = "integer", minimum = 1, description = "Versão reconstituída solicitada" },
                    reconstitutedState = new { type = "object", description = "Estado completo reconstituído na versão solicitada" },
                    events = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                eventId = new { type = "string", format = "uuid", description = "ID do evento no log imutável" },
                                streamVersion = new { type = "integer", minimum = 1, description = "Versão do fluxo neste evento" },
                                type = new { type = "string", description = "Tipo do evento de domínio" },
                                recordedAt = new { type = "string", format = "date-time", description = "Timestamp do registro" },
                                authorSubject = new { type = "string", description = "Sujeito que originou a ação" },
                                clientId = new { type = "string", description = "ID do cliente" },
                                payloadId = new { type = "string", format = "uuid", description = "ID do payload referenciado, se houver" },
                                refEventId = new { type = "string", format = "uuid", description = "ID de evento referenciado, se houver" },
                                reason = new { type = "string", description = "Motivo registrado no evento, se houver" }
                            },
                            required = new[] { "eventId", "streamVersion", "type", "recordedAt", "authorSubject", "clientId" }
                        }
                    },
                    nextBeforeVersion = new { type = "integer", description = "Cursor para buscar eventos anteriores na paginação reversa" }
                },
                required = new[] { "kind", "streamId", "currentStreamVersion", "targetVersion", "reconstitutedState", "events" }
            }
        },
        new McpToolDefinition
        {
            Name = "get_context_receipt",
            Description = "Retrieves a context query receipt with the exact versions returned and an integrity check of its digest. Visible to the receipt author and workspace maintainers. Requires read:memory scope.",
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
            },
            OutputSchema = new
            {
                type = "object",
                properties = new
                {
                    queryId = new { type = "string", format = "uuid", description = "ID do comprovante de consulta" },
                    workspaceId = new { type = "string", format = "uuid", description = "ID do workspace" },
                    projectId = new { type = "string", format = "uuid", description = "ID do projeto" },
                    principalId = new { type = "string", description = "Sujeito que executou a consulta" },
                    principalIssuer = new { type = "string", description = "Emissor da credencial do sujeito" },
                    clientId = new { type = "string", description = "Identificador do cliente" },
                    searchMode = new { type = "string", description = "Modo de busca (technical_exact, text_search)" },
                    createdAt = new { type = "string", format = "date-time", description = "Data/hora em que a consulta foi realizada" },
                    digest = new { type = "string", description = "Digest SHA-256 dos itens retornados" },
                    digestAlgorithm = new { type = "string", description = "Algoritmo de digest criptográfico utilizado" },
                    integrityVerified = new { type = "boolean", description = "Booleano indicando se o hash confere com o conteúdo recalculado" },
                    items = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                position = new { type = "integer", minimum = 0, description = "Posição ordinal no resultado da consulta" },
                                recordType = new { type = "string", @enum = new[] { "decision", "incident" }, description = "Tipo do registro retornado" },
                                streamId = new { type = "string", format = "uuid", description = "ID do fluxo do registro" },
                                recordId = new { type = "string", format = "uuid", description = "ID da versão do registro" },
                                recordVersion = new { type = "integer", minimum = 1, description = "Versão do registro no momento da consulta" },
                                streamVersion = new { type = "integer", minimum = 1, description = "Versão do fluxo no momento da consulta" },
                                status = new { type = "string", description = "Situação do registro (active, etc.)" }
                            },
                            required = new[] { "position", "recordType", "streamId", "recordId", "recordVersion", "streamVersion", "status" }
                        }
                    }
                },
                required = new[] { "queryId", "workspaceId", "projectId", "principalId", "principalIssuer", "clientId", "searchMode", "createdAt", "digest", "digestAlgorithm", "integrityVerified", "items" }
            }
        }
    ];
}
