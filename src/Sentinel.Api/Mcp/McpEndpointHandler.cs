using System.Text.Json;
using Sentinel.Api.Auth;
using Sentinel.Core.Excecoes;

namespace Sentinel.Api.Mcp;

// Endpoint HTTP para MCP JSON-RPC
public sealed class McpEndpointHandler
{
    public static readonly string[] VersoesSuportadas = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly McpDispatcher _despachante;
    private readonly IServicoIdentidadeAgente _identidade;
    private readonly IConfiguration _config;
    private readonly ILogger<McpEndpointHandler> _log;

    public McpEndpointHandler(McpDispatcher despachante, IServicoIdentidadeAgente identidade, IConfiguration config, ILogger<McpEndpointHandler> log)
    {
        _despachante = despachante;
        _identidade = identidade;
        _config = config;
        _log = log;
    }

    public async Task HandleAsync(HttpContext contexto)
    {
        // Valida origem da requisição
        if (!OrigemPermitida(contexto))
        {
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await EscreverAsync(contexto, JsonRpcResponse.CreateError(null, -32600, "Origin não permitida."));
            return;
        }

        if (!HttpMethods.IsPost(contexto.Request.Method))
        {
            // Método HTTP não permitido
            contexto.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            contexto.Response.Headers.Allow = "POST";
            return;
        }

        var versaoCabecalho = contexto.Request.Headers["MCP-Protocol-Version"].ToString();
        if (!string.IsNullOrEmpty(versaoCabecalho) && !VersoesSuportadas.Contains(versaoCabecalho))
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await EscreverAsync(contexto, JsonRpcResponse.CreateError(null, -32600, $"MCP-Protocol-Version não suportada: {versaoCabecalho}."));
            return;
        }

        JsonDocument documento;
        try
        {
            documento = await JsonDocument.ParseAsync(contexto.Request.Body, cancellationToken: contexto.RequestAborted);
        }
        catch (JsonException)
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await EscreverAsync(contexto, JsonRpcResponse.CreateError(null, -32700, "Erro ao analisar JSON."));
            return;
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
                await EscreverAsync(contexto, JsonRpcResponse.CreateError(null, -32600, "Requisição inválida: envie um único objeto JSON-RPC (lotes não são suportados)."));
                return;
            }

            var temId = raiz.TryGetProperty("id", out var idElem) && idElem.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            object? id = temId ? idElem.Clone() : null;
            var metodo = raiz.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

            // Notificações não geram resposta de corpo
            if (metodo == null || !temId)
            {
                if (metodo == null && !raiz.TryGetProperty("result", out _) && !raiz.TryGetProperty("error", out _))
                {
                    contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await EscreverAsync(contexto, JsonRpcResponse.CreateError(id, -32600, "Requisição inválida: 'method' ausente."));
                    return;
                }
                contexto.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            // Autenticação do agente
            IdentidadeAgente identidade;
            try
            {
                identidade = await _identidade.AutenticarAsync(contexto);
            }
            catch (ExcecaoDeNaoAutorizado ex)
            {
                contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
                contexto.Response.Headers.WWWAuthenticate = CabecalhoDesafio(contexto);
                await EscreverAsync(contexto, JsonRpcResponse.CreateError(id, -32001, ex.Message));
                return;
            }

            var parametros = raiz.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

            switch (metodo)
            {
                case "initialize":
                {
                    var solicitada = parametros.ValueKind == JsonValueKind.Object && parametros.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String
                        ? pv.GetString()
                        : null;
                    // Negociação da versão do protocolo
                    var versao = solicitada != null && VersoesSuportadas.Contains(solicitada) ? solicitada : VersoesSuportadas[0];

                    await EscreverAsync(contexto, JsonRpcResponse.Success(id, new
                    {
                        protocolVersion = versao,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "Sentinel-MCP", version = "0.2.0" },
                        instructions = "Consulte decisões vigentes com query_context antes de propor mudanças. Propostas exigem aprovação humana na página de revisão."
                    }));
                    break;
                }

                case "ping":
                    await EscreverAsync(contexto, JsonRpcResponse.Success(id, new { }));
                    break;

                case "tools/list":
                    await EscreverAsync(contexto, JsonRpcResponse.Success(id, new { tools = McpToolsRegistry.AllTools }));
                    break;

                case "tools/call":
                {
                    if (parametros.ValueKind != JsonValueKind.Object || !parametros.TryGetProperty("name", out var nomeElem) || nomeElem.ValueKind != JsonValueKind.String)
                    {
                        await EscreverAsync(contexto, JsonRpcResponse.CreateError(id, -32602, "Parâmetros inválidos: 'name' da ferramenta é obrigatório."));
                        return;
                    }

                    var nome = nomeElem.GetString()!;
                    if (!McpDispatcher.Existe(nome))
                    {
                        await EscreverAsync(contexto, JsonRpcResponse.CreateError(id, -32602, $"Ferramenta desconhecida: {nome}."));
                        return;
                    }

                    var argumentos = parametros.TryGetProperty("arguments", out var a) ? a : default;
                    var resultado = await _despachante.CallToolAsync(nome, argumentos, identidade, contexto.TraceIdentifier, contexto.RequestAborted);
                    await EscreverAsync(contexto, JsonRpcResponse.Success(id, resultado));
                    break;
                }

                default:
                    await EscreverAsync(contexto, JsonRpcResponse.CreateError(id, -32601, $"Método não encontrado: {metodo}"));
                    break;
            }
        }
    }

    private bool OrigemPermitida(HttpContext contexto)
    {
        var origem = contexto.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origem))
            return true;

        var propria = $"{contexto.Request.Scheme}://{contexto.Request.Host}";
        if (string.Equals(origem, propria, StringComparison.OrdinalIgnoreCase))
            return true;

        var permitidas = _config.GetSection("Sentinel:Mcp:AllowedOrigins").Get<string[]>() ?? [];
        var ok = permitidas.Any(o => string.Equals(o.TrimEnd('/'), origem, StringComparison.OrdinalIgnoreCase));
        if (!ok)
            _log.LogWarning("Requisição MCP recusada por Origin não permitida: {Origem}", origem);
        return ok;
    }

    private string CabecalhoDesafio(HttpContext contexto)
    {
        if (!ServicoIdentidadeAgente.JwtConfigurado(_config))
            return "Bearer realm=\"sentinel-mcp\"";
        var metadados = $"{contexto.Request.Scheme}://{contexto.Request.Host}/.well-known/oauth-protected-resource";
        return $"Bearer realm=\"sentinel-mcp\", resource_metadata=\"{metadados}\"";
    }

    private static async Task EscreverAsync(HttpContext contexto, JsonRpcResponse resposta)
    {
        var accept = contexto.Request.Headers.Accept.ToString();
        var querSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
                      && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);

        if (querSse)
        {
            contexto.Response.ContentType = "text/event-stream";
            contexto.Response.Headers.CacheControl = "no-cache";
            var json = JsonSerializer.Serialize(resposta, OpcoesJson);
            await contexto.Response.WriteAsync($"event: message\ndata: {json}\n\n", contexto.RequestAborted);
        }
        else
        {
            await contexto.Response.WriteAsJsonAsync(resposta, OpcoesJson, contexto.RequestAborted);
        }
    }
}
