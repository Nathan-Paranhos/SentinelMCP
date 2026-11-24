using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Sentinel.Api.Mcp;
using Xunit;

namespace Sentinel.ContractTests;

public class McpContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public McpContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndCapabilities()
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "ContractTestRunner", version = "1.0.0" }
            }
        };

        var response = await _client.PostAsJsonAsync("/mcp", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonRpcResponse>();
        Assert.NotNull(body);
        Assert.Null(body.Error);

        var resultJson = JsonSerializer.Serialize(body.Result);
        var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        Assert.Equal("2024-11-05", root.GetProperty("protocolVersion").GetString());
        Assert.Equal("Sentinel-MCP", root.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(root.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task ToolsList_ReturnsAllFiveOfficialTools()
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        };

        var response = await _client.PostAsJsonAsync("/mcp", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonRpcResponse>();
        Assert.NotNull(body);
        Assert.Null(body.Error);

        var resultJson = JsonSerializer.Serialize(body.Result);
        var doc = JsonDocument.Parse(resultJson);
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Contains("propose_decision", tools);
        Assert.Contains("log_incident", tools);
        Assert.Contains("query_context", tools);
        Assert.Contains("get_timeline", tools);
        Assert.Contains("get_context_receipt", tools);
        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsNotFound()
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new
            {
                name = "unknown_tool",
                arguments = new { }
            }
        };

        var response = await _client.PostAsJsonAsync("/mcp", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonRpcResponse>();
        Assert.NotNull(body);

        var resultJson = JsonSerializer.Serialize(body.Result);
        var doc = JsonDocument.Parse(resultJson);
        var isError = doc.RootElement.TryGetProperty("isError", out var isErr) && isErr.GetBoolean();
        Assert.True(isError);
    }
}
