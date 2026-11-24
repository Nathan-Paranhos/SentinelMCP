using System.Text.Json;

namespace Sentinel.Api.Mcp;

public sealed class McpEndpointHandler
{
    private readonly McpDispatcher _dispatcher;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public McpEndpointHandler(McpDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            // Streamable HTTP / SSE handshake endpoint
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var endpointMessage = "event: endpoint\ndata: /mcp\n\n";
            await context.Response.WriteAsync(endpointMessage);
            await context.Response.Body.FlushAsync();
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        JsonRpcRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(context.Request.Body, JsonOptions);
        }
        catch (JsonException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(JsonRpcResponse.CreateError(null, -32700, "Parse error", ex.Message));
            return;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(JsonRpcResponse.CreateError(null, -32600, "Invalid Request"));
            return;
        }

        switch (request.Method)
        {
            case "initialize":
            {
                var protocolVersion = "2024-11-05";
                if (request.Params.HasValue && request.Params.Value.TryGetProperty("protocolVersion", out var pvElem))
                {
                    protocolVersion = pvElem.GetString() ?? "2024-11-05";
                }

                var initResult = new
                {
                    protocolVersion,
                    capabilities = new
                    {
                        tools = new { listChanged = false }
                    },
                    serverInfo = new
                    {
                        name = "Sentinel-MCP",
                        version = "0.1.0"
                    }
                };

                await context.Response.WriteAsJsonAsync(JsonRpcResponse.Success(request.Id, initResult), JsonOptions);
                break;
            }

            case "notifications/initialized":
            {
                // Notifications do not return a response
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                break;
            }

            case "ping":
            {
                await context.Response.WriteAsJsonAsync(JsonRpcResponse.Success(request.Id, new { }), JsonOptions);
                break;
            }

            case "tools/list":
            {
                var listResult = new
                {
                    tools = McpToolsRegistry.AllTools
                };
                await context.Response.WriteAsJsonAsync(JsonRpcResponse.Success(request.Id, listResult), JsonOptions);
                break;
            }

            case "tools/call":
            {
                if (!request.Params.HasValue)
                {
                    await context.Response.WriteAsJsonAsync(
                        JsonRpcResponse.CreateError(request.Id, -32602, "Invalid params: expected tool name and arguments"), JsonOptions);
                    return;
                }

                var paramsObj = request.Params.Value;
                if (!paramsObj.TryGetProperty("name", out var nameElem))
                {
                    await context.Response.WriteAsJsonAsync(
                        JsonRpcResponse.CreateError(request.Id, -32602, "Missing tool 'name'"), JsonOptions);
                    return;
                }

                var toolName = nameElem.GetString() ?? string.Empty;
                var arguments = paramsObj.TryGetProperty("arguments", out var argsElem) ? argsElem : default;

                var toolResult = await _dispatcher.CallToolAsync(toolName, arguments, context, context.RequestAborted);
                await context.Response.WriteAsJsonAsync(JsonRpcResponse.Success(request.Id, toolResult), JsonOptions);
                break;
            }

            default:
            {
                await context.Response.WriteAsJsonAsync(
                    JsonRpcResponse.CreateError(request.Id, -32601, $"Method not found: {request.Method}"), JsonOptions);
                break;
            }
        }
    }
}
