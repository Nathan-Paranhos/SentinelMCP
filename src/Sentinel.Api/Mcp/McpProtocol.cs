using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Api.Mcp;

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(object? id, object result) => new()
    {
        Id = id,
        Result = result
    };

    public static JsonRpcResponse CreateError(object? id, int code, string message, object? data = null) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            Code = code,
            Message = message,
            Data = data
        }
    };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}

public sealed class McpToolAnnotations
{
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("readOnlyHint")]
    public bool ReadOnlyHint { get; set; }

    [JsonPropertyName("destructiveHint")]
    public bool DestructiveHint { get; set; }

    [JsonPropertyName("idempotentHint")]
    public bool IdempotentHint { get; set; }

    [JsonPropertyName("openWorldHint")]
    public bool OpenWorldHint { get; set; }
}

public sealed class McpToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; set; } = new();

    [JsonPropertyName("outputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? OutputSchema { get; set; }

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpToolAnnotations? Annotations { get; set; }
}

public sealed class McpToolResult
{
    private static readonly JsonSerializerOptions OpcoesTextoJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [JsonPropertyName("content")]
    public List<McpContentItem> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }

    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StructuredContent { get; set; }

    // Inclui resumo legível e JSON serializado no texto para compatibilidade com todos os clientes MCP
    public static McpToolResult Success(object structuredData, string textSummary)
    {
        var json = JsonSerializer.Serialize(structuredData, OpcoesTextoJson);
        return new McpToolResult
        {
            IsError = false,
            StructuredContent = structuredData,
            Content =
            [
                new McpContentItem
                {
                    Type = "text",
                    Text = $"{textSummary}\n\n{json}"
                }
            ]
        };
    }

    public static McpToolResult Error(string code, string message, object? details = null)
    {
        var errorObj = new
        {
            code,
            message,
            details
        };
        var texto = details == null
            ? $"Error [{code}]: {message}"
            : $"Error [{code}]: {message}\n{JsonSerializer.Serialize(details, OpcoesTextoJson)}";

        return new McpToolResult
        {
            IsError = true,
            StructuredContent = errorObj,
            Content =
            [
                new McpContentItem
                {
                    Type = "text",
                    Text = texto
                }
            ]
        };
    }
}

public sealed class McpContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed record McpPromptDefinition(
    string Name,
    string Title,
    string Description,
    List<McpPromptArgument> Arguments);

public sealed record McpPromptArgument(
    string Name,
    string Description,
    bool Required);

public sealed record McpResourceDefinition(
    string Uri,
    string Name,
    string Title,
    string Description,
    string MimeType);

public sealed record McpResourceTemplateDefinition(
    string UriTemplate,
    string Name,
    string Title,
    string Description,
    string MimeType);
