using System.Text.Json;

namespace Sentinel.Api.Mcp;

public sealed class McpEndpointHandler
{
    private readonly McpDispatcher _despachante;
    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public McpEndpointHandler(McpDispatcher despachante)
    {
        _despachante = despachante;
    }

    public async Task HandleAsync(HttpContext contexto)
    {
        if (HttpMethods.IsGet(contexto.Request.Method))
        {
            contexto.Response.ContentType = "text/event-stream";
            contexto.Response.Headers.CacheControl = "no-cache";
            contexto.Response.Headers.Connection = "keep-alive";

            var mensagemEndpoint = "event: endpoint\ndata: /mcp\n\n";
            await contexto.Response.WriteAsync(mensagemEndpoint);
            await contexto.Response.Body.FlushAsync();
            return;
        }

        if (!HttpMethods.IsPost(contexto.Request.Method))
        {
            contexto.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        JsonRpcRequest? requisicao;
        try
        {
            requisicao = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(contexto.Request.Body, OpcoesJson);
        }
        catch (JsonException ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsJsonAsync(JsonRpcResponse.CreateError(null, -32700, "Erro ao analisar JSON", ex.Message));
            return;
        }

        if (requisicao == null || string.IsNullOrWhiteSpace(requisicao.Method))
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsJsonAsync(JsonRpcResponse.CreateError(null, -32600, "Requisição inválida"));
            return;
        }

        switch (requisicao.Method)
        {
            case "initialize":
            {
                var versaoProtocolo = "2024-11-05";
                if (requisicao.Params.HasValue && requisicao.Params.Value.TryGetProperty("protocolVersion", out var pvElem))
                {
                    versaoProtocolo = pvElem.GetString() ?? "2024-11-05";
                }

                var resultadoInicializacao = new
                {
                    protocolVersion = versaoProtocolo,
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

                await contexto.Response.WriteAsJsonAsync(JsonRpcResponse.Success(requisicao.Id, resultadoInicializacao), OpcoesJson);
                break;
            }

            case "notifications/initialized":
            {
                contexto.Response.StatusCode = StatusCodes.Status202Accepted;
                break;
            }

            case "ping":
            {
                await contexto.Response.WriteAsJsonAsync(JsonRpcResponse.Success(requisicao.Id, new { }), OpcoesJson);
                break;
            }

            case "tools/list":
            {
                var resultadoLista = new
                {
                    tools = McpToolsRegistry.AllTools
                };
                await contexto.Response.WriteAsJsonAsync(JsonRpcResponse.Success(requisicao.Id, resultadoLista), OpcoesJson);
                break;
            }

            case "tools/call":
            {
                if (!requisicao.Params.HasValue)
                {
                    await contexto.Response.WriteAsJsonAsync(
                        JsonRpcResponse.CreateError(requisicao.Id, -32602, "Parâmetros inválidos: esperado nome da ferramenta e argumentos"), OpcoesJson);
                    return;
                }

                var parametros = requisicao.Params.Value;
                if (!parametros.TryGetProperty("name", out var nomeElem))
                {
                    await contexto.Response.WriteAsJsonAsync(
                        JsonRpcResponse.CreateError(requisicao.Id, -32602, "Propriedade 'name' obrigatória ausente"), OpcoesJson);
                    return;
                }

                var nomeFerramenta = nomeElem.GetString() ?? string.Empty;
                var argumentos = parametros.TryGetProperty("arguments", out var argsElem) ? argsElem : default;

                var resultadoFerramenta = await _despachante.CallToolAsync(nomeFerramenta, argumentos, contexto, contexto.RequestAborted);
                await contexto.Response.WriteAsJsonAsync(JsonRpcResponse.Success(requisicao.Id, resultadoFerramenta), OpcoesJson);
                break;
            }

            default:
            {
                await contexto.Response.WriteAsJsonAsync(
                    JsonRpcResponse.CreateError(requisicao.Id, -32601, $"Método não encontrado: {requisicao.Method}"), OpcoesJson);
                break;
            }
        }
    }
}
