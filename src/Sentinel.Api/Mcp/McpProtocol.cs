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

public sealed class McpToolDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; set; } = new();

    [JsonPropertyName("outputSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? OutputSchema { get; set; }
}

public sealed class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContentItem> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }

    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StructuredContent { get; set; }

    public static McpToolResult Success(object structuredData, string textSummary)
    {
        return new McpToolResult
        {
            IsError = false,
            StructuredContent = structuredData,
            Content =
            [
                new McpContentItem
                {
                    Type = "text",
                    Text = textSummary
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

        return new McpToolResult
        {
            IsError = true,
            StructuredContent = errorObj,
            Content =
            [
                new McpContentItem
                {
                    Type = "text",
                    Text = $"Error [{code}]: {message}"
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
