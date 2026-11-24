using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Sentinel.IntegrationTests;

public class AdminApiTests : IClassFixture<ProvedorEmMemoria>
{
    private const string AdminKey = "sentinel-admin-dev-key";
    private readonly HttpClient _client;

    public AdminApiTests(ProvedorEmMemoria provedor)
    {
        _client = provedor.Fabrica.CreateClient();
    }

    private HttpRequestMessage RequisicaoAdmin(HttpMethod metodo, string url, object? corpo = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        req.Headers.Add("X-Admin-Key", AdminKey);
        if (corpo != null)
            req.Content = JsonContent.Create(corpo);
        return req;
    }

    [Fact]
    public async Task Admin_SemChave_Retorna401()
    {
        var resp = await _client.GetAsync("/api/admin/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_ComChaveInvalida_Retorna401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/workspaces");
        req.Headers.Add("X-Admin-Key", "chave-totalmente-errada");
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_CriarWorkspaceEListar_Sucesso()
    {
        var slug = $"ws-teste-{Guid.NewGuid():N}"[..20];
        var criarReq = RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new
        {
            name = "Workspace de Teste",
            slug = slug
        });

        var criarResp = await _client.SendAsync(criarReq);
        Assert.Equal(HttpStatusCode.Created, criarResp.StatusCode);

        var wsCriado = await criarResp.Content.ReadFromJsonAsync<JsonElement>();
        var wsId = wsCriado.GetProperty("workspaceId").GetGuid();
        Assert.Equal(slug, wsCriado.GetProperty("slug").GetString());

        // Listar workspaces
        var listarReq = RequisicaoAdmin(HttpMethod.Get, "/api/admin/workspaces");
        var listarResp = await _client.SendAsync(listarReq);
        Assert.Equal(HttpStatusCode.OK, listarResp.StatusCode);

        var lista = await listarResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(lista.EnumerateArray(), w => w.GetProperty("workspaceId").GetGuid() == wsId);
    }

    [Fact]
    public async Task Admin_CriarWorkspaceComSlugDuplicado_Retorna409()
    {
        var slug = $"ws-dup-{Guid.NewGuid():N}"[..20];
        var req1 = RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS 1", slug });
        var resp1 = await _client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);

        var req2 = RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS 2", slug });
        var resp2 = await _client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
    }

    [Fact]
    public async Task Admin_CriarProjetoEListar_Sucesso()
    {
        var wsSlug = $"ws-proj-{Guid.NewGuid():N}"[..20];
        var wsResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS Proj", slug = wsSlug }));
        var wsId = (await wsResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspaceId").GetGuid();

        var projSlug = $"proj-{Guid.NewGuid():N}"[..20];
        var projResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Post, $"/api/admin/workspaces/{wsId}/projects", new
        {
            name = "Meu Projeto",
            slug = projSlug
        }));
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);

        var proj = await projResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(projSlug, proj.GetProperty("slug").GetString());
        Assert.Equal(wsId, proj.GetProperty("workspaceId").GetGuid());
    }

    [Fact]
    public async Task Admin_ConfigurarVinculoEListarERemover_Sucesso()
    {
        var wsSlug = $"ws-vinc-{Guid.NewGuid():N}"[..20];
        var wsResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS Vinculo", slug = wsSlug }));
        var wsId = (await wsResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspaceId").GetGuid();

        // 1. Criar vínculo
        var vincResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Post, $"/api/admin/workspaces/{wsId}/memberships", new
        {
            issuer = "https://auth.sentinel.dev",
            subject = "novo-agente",
            role = "Contributor",
            isActive = true
        }));
        Assert.Equal(HttpStatusCode.OK, vincResp.StatusCode);

        var vinc = await vincResp.Content.ReadFromJsonAsync<JsonElement>();
        var vincId = vinc.GetProperty("membershipId").GetGuid();
        Assert.Equal("novo-agente", vinc.GetProperty("subject").GetString());
        Assert.Equal("Contributor", vinc.GetProperty("role").GetString());

        // 2. Listar vínculos
        var listResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Get, $"/api/admin/workspaces/{wsId}/memberships"));
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var lista = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(lista.EnumerateArray(), m => m.GetProperty("membershipId").GetGuid() == vincId);

        // 3. Remover vínculo
        var delResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Delete, $"/api/admin/workspaces/{wsId}/memberships/{vincId}"));
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        // 4. Listar novamente e verificar que foi removido
        var listPosResp = await _client.SendAsync(RequisicaoAdmin(HttpMethod.Get, $"/api/admin/workspaces/{wsId}/memberships"));
        var listaPos = await listPosResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(listaPos.EnumerateArray(), m => m.GetProperty("membershipId").GetGuid() == vincId);
    }
}
