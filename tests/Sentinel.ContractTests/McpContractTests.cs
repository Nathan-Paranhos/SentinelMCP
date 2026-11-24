using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sentinel.ContractTests;

public sealed class FabricaContrato : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Sentinel:StorageProvider", "InMemory");
        builder.UseSetting("Sentinel:SeedDemoData", "true");
        builder.UseSetting("Sentinel:AllowMockTokens", "true");
        builder.UseSetting("Sentinel:Retention:Enabled", "false");
        builder.UseSetting("Sentinel:Mcp:AllowedOrigins:0", "https://cliente-confiavel.example");
    }
}

// Testes de conformidade do protocolo MCP
public class McpContractTests : IClassFixture<FabricaContrato>
{
    private const string Token = "mock:agent-dev-1|read:memory,write:decision,write:incident|contract-tests";
    private readonly HttpClient _client;

    public McpContractTests(FabricaContrato factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> PostAsync(object corpo, string? token = Token, Action<HttpRequestMessage>? ajustar = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(corpo) };
        if (token != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ajustar?.Invoke(req);
        return await _client.SendAsync(req);
    }

    private async Task<JsonElement> RpcAsync(object corpo, string? token = Token)
    {
        var resp = await PostAsync(corpo, token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("1999-01-01", "2025-06-18")]
    public async Task Initialize_NegociaVersaoDoProtocolo(string pedida, string esperada)
    {
        var body = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = pedida, capabilities = new { }, clientInfo = new { name = "ContractTestRunner", version = "1.0.0" } }
        });

        var resultado = body.GetProperty("result");
        Assert.Equal(esperada, resultado.GetProperty("protocolVersion").GetString());
        Assert.Equal("Sentinel-MCP", resultado.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(resultado.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Id_EhDevolvidoNoMesmoTipo()
    {
        var comTexto = await RpcAsync(new { jsonrpc = "2.0", id = "abc-1", method = "ping" });
        Assert.Equal(JsonValueKind.String, comTexto.GetProperty("id").ValueKind);
        Assert.Equal("abc-1", comTexto.GetProperty("id").GetString());

        var comNumero = await RpcAsync(new { jsonrpc = "2.0", id = 42, method = "ping" });
        Assert.Equal(42, comNumero.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ToolsList_RetornaAsSeisFerramentas()
    {
        var body = await RpcAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var tools = body.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Equal(["propose_decision", "log_incident", "query_context", "get_timeline", "get_context_receipt", "list_workspaces"], tools);
    }

    [Fact]
    public async Task ToolsList_PublicaOutputSchemaEAnotacoesEmTodasAsFerramentas()
    {
        var body = await RpcAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var tools = body.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

        Assert.Equal(6, tools.Count);
        foreach (var tool in tools)
        {
            Assert.True(tool.TryGetProperty("annotations", out var annotations));
            Assert.True(annotations.TryGetProperty("readOnlyHint", out _));
            Assert.True(tool.TryGetProperty("outputSchema", out var outputSchema), $"outputSchema ausente na tool {tool.GetProperty("name").GetString()}");
            Assert.Equal("object", outputSchema.GetProperty("type").GetString());
            Assert.True(outputSchema.TryGetProperty("properties", out var props));
            Assert.True(props.EnumerateObject().Any());
            Assert.True(outputSchema.TryGetProperty("required", out var req));
            Assert.True(req.EnumerateArray().Any());
        }
    }

    [Theory]
    [InlineData("notifications/initialized")]
    [InlineData("notifications/cancelled")]
    [InlineData("notifications/qualquer-outra")]
    public async Task Notificacoes_Recebem202SemCorpo(string metodo)
    {
        var resp = await PostAsync(new { jsonrpc = "2.0", method = metodo });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(string.Empty, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SemCredencial_Retorna401ComDesafioBearer()
    {
        var resp = await PostAsync(new { jsonrpc = "2.0", id = 3, method = "tools/list" }, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.StartsWith("Bearer", resp.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task TokenInvalido_NaoEcoaOTokenNaResposta()
    {
        const string segredo = "token-secreto-que-nao-pode-vazar";
        var resp = await PostAsync(new { jsonrpc = "2.0", id = 4, method = "tools/list" }, token: segredo);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain(segredo, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_Retorna405PoisNaoHaStreamSse()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Theory]
    [InlineData("https://site-malicioso.example", HttpStatusCode.Forbidden)]
    [InlineData("https://cliente-confiavel.example", HttpStatusCode.OK)]
    public async Task Origin_EhValidada(string origem, HttpStatusCode esperado)
    {
        var resp = await PostAsync(new { jsonrpc = "2.0", id = 5, method = "ping" }, ajustar: r => r.Headers.Add("Origin", origem));
        Assert.Equal(esperado, resp.StatusCode);
    }

    [Fact]
    public async Task VersaoDeProtocoloNoCabecalhoNaoSuportada_Retorna400()
    {
        var resp = await PostAsync(new { jsonrpc = "2.0", id = 6, method = "ping" }, ajustar: r => r.Headers.Add("MCP-Protocol-Version", "1999-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task JsonInvalido_RetornaParseError()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{nao-e-json", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(-32700, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task FerramentaDesconhecida_RetornaErroDeParametros()
    {
        var body = await RpcAsync(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "unknown_tool", arguments = new { } } });
        Assert.Equal(-32602, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ArgumentoAusente_RetornaValidationErrorENaoInternalError()
    {
        var body = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 8,
            method = "tools/call",
            @params = new { name = "query_context", arguments = new { workspaceId = "11111111-1111-1111-1111-111111111111" } }
        });

        var resultado = body.GetProperty("result");
        Assert.True(resultado.GetProperty("isError").GetBoolean());
        var erro = resultado.GetProperty("structuredContent");
        Assert.Equal("VALIDATION_ERROR", erro.GetProperty("code").GetString());
        Assert.Contains("projectId", erro.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Utf8InvalidoEmArgumento_RetornaValidationError()
    {
        var prefixo = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"query_context\",\"arguments\":{\"workspaceId\":\"11111111-1111-1111-1111-111111111111\",\"projectId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"query\":\"idempot");
        var sufixo = Encoding.UTF8.GetBytes("ncia\"}}}");
        byte[] latin1 = [0xEA]; // "ê" em Latin-1: byte inválido em UTF-8
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new ByteArrayContent([.. prefixo, .. latin1, .. sufixo]) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var resp = await _client.SendAsync(req);
        var corpo = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // Valida erro esperado
        var codigo = corpo.TryGetProperty("error", out var erro)
            ? erro.GetProperty("code").GetInt32().ToString()
            : corpo.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString();
        Assert.Contains(codigo, new[] { "-32700", "VALIDATION_ERROR" });
    }

    [Fact]
    public async Task LimiteForaDoIntervalo_RetornaValidationError()
    {
        var body = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 9,
            method = "tools/call",
            @params = new
            {
                name = "query_context",
                arguments = new
                {
                    workspaceId = "11111111-1111-1111-1111-111111111111",
                    projectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    query = "billing",
                    limit = 500
                }
            }
        });

        Assert.Equal("VALIDATION_ERROR", body.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString());
    }

    [Fact]
    public async Task QueryContext_RetornaEvidenciasNoFormatoDeEntrada()
    {
        var body = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 10,
            method = "tools/call",
            @params = new
            {
                name = "query_context",
                arguments = new { workspaceId = "11111111-1111-1111-1111-111111111111", projectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", query = "BillingService" }
            }
        });

        var item = body.GetProperty("result").GetProperty("structuredContent").GetProperty("items")[0];
        var evidencia = item.GetProperty("evidence")[0];
        Assert.True(evidencia.TryGetProperty("type", out _));
        Assert.True(evidencia.TryGetProperty("label", out _));
        Assert.StartsWith("https://", evidencia.GetProperty("url").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListWorkspaces_SemArguments_RetornaEspacosEProjetosComTextoEstruturado()
    {
        var body = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 12,
            method = "tools/call",
            @params = new { name = "list_workspaces" }
        });

        var result = body.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var errFlag) && errFlag.GetBoolean());
        var texto = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("workspaces", texto);
        var workspaces = result.GetProperty("structuredContent").GetProperty("workspaces").EnumerateArray().ToList();
        Assert.Single(workspaces);
        Assert.Equal("11111111-1111-1111-1111-111111111111", workspaces[0].GetProperty("workspaceId").GetString());
        Assert.True(workspaces[0].GetProperty("projects").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task ResourcesListTemplatesERead_RetornamCatalogoEDados()
    {
        var list = await RpcAsync(new { jsonrpc = "2.0", id = 13, method = "resources/list" });
        var resources = list.GetProperty("result").GetProperty("resources").EnumerateArray().ToList();
        Assert.Contains(resources, r => r.GetProperty("uri").GetString() == "sentinel://workspaces");

        var templates = await RpcAsync(new { jsonrpc = "2.0", id = 14, method = "resources/templates/list" });
        var listaTemplates = templates.GetProperty("result").GetProperty("resourceTemplates").EnumerateArray().ToList();
        Assert.True(listaTemplates.Count >= 2);

        var read = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 15,
            method = "resources/read",
            @params = new { uri = "sentinel://workspaces" }
        });
        var conteudo = read.GetProperty("result").GetProperty("contents")[0];
        Assert.Equal("application/json", conteudo.GetProperty("mimeType").GetString());
        Assert.Contains("11111111-1111-1111-1111-111111111111", conteudo.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PromptsListEGet_RetornamFluxoDeGovernanca()
    {
        var list = await RpcAsync(new { jsonrpc = "2.0", id = 16, method = "prompts/list" });
        var prompts = list.GetProperty("result").GetProperty("prompts").EnumerateArray().ToList();
        Assert.Contains(prompts, p => p.GetProperty("name").GetString() == "consult_context_before_change");

        var get = await RpcAsync(new
        {
            jsonrpc = "2.0",
            id = 17,
            method = "prompts/get",
            @params = new
            {
                name = "consult_context_before_change",
                arguments = new { topic = "BillingService" }
            }
        });
        var msgs = get.GetProperty("result").GetProperty("messages").EnumerateArray().ToList();
        Assert.NotEmpty(msgs);
        Assert.Contains("query_context", msgs[0].GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public async Task McpSse_SemToken_Retorna401ComWwwAuthenticate()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/sse")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 18, method = "initialize" })
        };
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.True(resp.Headers.Contains("WWW-Authenticate"));
    }

    [Fact]
    public async Task PaginasHtml_TemCspEAntiFraming()
    {
        var resp = await _client.GetAsync("/");
        resp.EnsureSuccessStatusCode();
        Assert.Contains("script-src 'self'", resp.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
    }
}
