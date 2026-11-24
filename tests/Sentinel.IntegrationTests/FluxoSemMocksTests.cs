using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sentinel.IntegrationTests;

// Valida o fluxo completo sem tokens mock, sem SeedDemoData e sem EnableDevLogin
public sealed class FluxoSemMocksTests : IDisposable
{
    private readonly FabricaSentinel _fabrica = new(new Dictionary<string, string?>
    {
        ["Sentinel:StorageProvider"] = "InMemory",
        ["Sentinel:SeedDemoData"] = "false",
        ["Sentinel:AllowMockTokens"] = "false",
        ["Sentinel:Web:EnableDevLogin"] = "false"
    });

    public void Dispose() => _fabrica.Dispose();

    [Fact]
    public async Task SetupInicial_LoginReal_ChaveApiReal_PropostaEAprovacaoSemMocks()
    {
        var http = _fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // 1. Token mock é recusado
        var mcp = new ClienteMcp(http);
        var mockResp = await mcp.EnviarAsync(new { jsonrpc = "2.0", id = 1, method = "tools/list" }, "mock:agent-dev-1|read:memory|codex");
        Assert.Equal(HttpStatusCode.Unauthorized, mockResp.StatusCode);

        // 2. /auth/login redireciona para /auth/setup quando não há workspaces
        var loginRedirect = await http.GetAsync("/auth/login?returnUrl=%2Freview");
        Assert.Equal(HttpStatusCode.Redirect, loginRedirect.StatusCode);
        Assert.Contains("/auth/setup", loginRedirect.Headers.Location!.ToString());

        // 3. Executa /auth/setup validando confirmação de senha, olho de visualização e criando workspace/conta PBKDF2
        var setupHtml = await http.GetStringAsync("/auth/setup?returnUrl=%2Freview");
        Assert.Contains("name=\"confirmPassword\"", setupHtml);
        Assert.Contains("data-toggle-password=\"setup-password\"", setupHtml);
        Assert.Contains("data-toggle-password=\"setup-confirm-password\"", setupHtml);
        var csrfSetup = WebUtility.HtmlDecode(Regex.Match(setupHtml, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(csrfSetup));

        var setupDivergente = await http.PostAsync("/auth/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["workspaceName"] = "Empresa Real",
            ["workspaceSlug"] = "empresa-real",
            ["projectName"] = "Core API",
            ["projectSlug"] = "core-api",
            ["username"] = "nathan.adm",
            ["displayName"] = "Nathan Admin",
            ["password"] = "SenhaSegura#2026",
            ["confirmPassword"] = "OutraSenha#2026",
            ["__RequestVerificationToken"] = csrfSetup,
            ["returnUrl"] = "/review"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, setupDivergente.StatusCode);
        var setupDivergenteHtml = await setupDivergente.Content.ReadAsStringAsync();
        Assert.Contains("não conferem", WebUtility.HtmlDecode(setupDivergenteHtml));

        var csrfSetupAtualizado = WebUtility.HtmlDecode(Regex.Match(setupDivergenteHtml, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        var setupPost = await http.PostAsync("/auth/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["workspaceName"] = "Empresa Real",
            ["workspaceSlug"] = "empresa-real",
            ["projectName"] = "Core API",
            ["projectSlug"] = "core-api",
            ["username"] = "nathan.adm",
            ["displayName"] = "Nathan Admin",
            ["password"] = "SenhaSegura#2026",
            ["confirmPassword"] = "SenhaSegura#2026",
            ["__RequestVerificationToken"] = csrfSetupAtualizado,
            ["returnUrl"] = "/review"
        }));
        Assert.Equal(HttpStatusCode.Redirect, setupPost.StatusCode);

        // 4. Encerra sessão e faz login real via POST /auth/login
        var stateAntes = await http.GetFromJsonAsync<JsonElement>("/api/review/state");
        var csrfAntes = stateAntes.GetProperty("csrf").GetProperty("token").GetString()!;
        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logoutReq.Headers.Add("X-CSRF-Token", csrfAntes);
        (await http.SendAsync(logoutReq)).EnsureSuccessStatusCode();

        var loginHtml = await http.GetStringAsync("/auth/login");
        Assert.Contains("data-toggle-password=\"login-password\"", loginHtml);
        var csrfLogin = WebUtility.HtmlDecode(Regex.Match(loginHtml, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        var loginPost = await http.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "nathan.adm",
            ["password"] = "SenhaSegura#2026",
            ["__RequestVerificationToken"] = csrfLogin,
            ["returnUrl"] = "/review"
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginPost.StatusCode);

        // 5. Lê estado real (sem propostas/decisões/incidentes mock) e gera chave de API snt_...
        var estado = await http.GetFromJsonAsync<JsonElement>("/api/review/state");
        Assert.Empty(estado.GetProperty("propostas").EnumerateArray());
        Assert.Empty(estado.GetProperty("decisoes").EnumerateArray());
        Assert.Empty(estado.GetProperty("incidentes").EnumerateArray());

        var wsId = estado.GetProperty("workspace").GetProperty("id").GetGuid();
        var projId = estado.GetProperty("projetos")[0].GetProperty("id").GetGuid();
        var csrf = estado.GetProperty("csrf").GetProperty("token").GetString()!;

        var reqKey = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/workspaces/{wsId}/api-keys")
        {
            Content = JsonContent.Create(new
            {
                name = "Codex Local",
                subject = "codex-nathan",
                clientId = "codex",
                role = "Contributor",
                scopes = new[] { "read:memory", "write:decision", "write:incident" }
            })
        };
        reqKey.Headers.Add("X-CSRF-Token", csrf);
        var keyResp = await http.SendAsync(reqKey);
        Assert.Equal(HttpStatusCode.Created, keyResp.StatusCode);
        var tokenApi = (await keyResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        // 6. Agente conecta no /mcp com a chave snt_..., propõe decisão e mantenedor aprova
        var descoberta = (await mcp.ToolAsync("list_workspaces", new { }, tokenApi)).Sucesso();
        Assert.Equal(wsId, descoberta.GetProperty("workspaces")[0].GetProperty("workspaceId").GetGuid());

        var proposta = (await mcp.ToolAsync("propose_decision", new
        {
            workspaceId = wsId,
            projectId = projId,
            topicKey = "auth-strategy",
            expectedVersion = 0,
            title = "Chaves de API SHA-256 e PBKDF2",
            summary = "Autenticação de agentes sem tokens mock.",
            technicalKeys = new[] { "AuthService", "PBKDF2" },
            evidence = new[] { new { type = "adr", label = "ADR Auth", url = "https://github.com/Nathan-Paranhos/SentinelMCP" } },
            idempotencyKey = Guid.NewGuid().ToString()
        }, tokenApi)).Sucesso();

        var aprovarReq = new HttpRequestMessage(HttpMethod.Post, "/api/review/approve")
        {
            Content = JsonContent.Create(new
            {
                workspaceId = wsId,
                projectId = projId,
                topicId = proposta.GetProperty("topicId").GetGuid(),
                proposalId = proposta.GetProperty("proposalId").GetGuid(),
                expectedVersion = 1,
                idempotencyKey = Guid.NewGuid().ToString()
            })
        };
        aprovarReq.Headers.Add("X-CSRF-Token", csrf);
        (await http.SendAsync(aprovarReq)).EnsureSuccessStatusCode();

        // 7. Agente consulta contexto real e verifica recibo SHA-256
        var consulta = (await mcp.ToolAsync("query_context", new
        {
            workspaceId = wsId,
            projectId = projId,
            query = "PBKDF2",
            kind = "decision"
        }, tokenApi)).Sucesso();
        Assert.Equal("found", consulta.GetProperty("matchStatus").GetString());
        var queryId = consulta.GetProperty("queryId").GetGuid();

        var recibo = await http.GetFromJsonAsync<JsonElement>($"/api/review/receipt?workspaceId={wsId}&projectId={projId}&queryId={queryId}");
        Assert.True(recibo.GetProperty("integrityVerified").GetBoolean());
    }
}
