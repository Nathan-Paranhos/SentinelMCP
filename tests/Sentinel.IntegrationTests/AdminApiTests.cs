using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using static Sentinel.IntegrationTests.DadosDemo;

namespace Sentinel.IntegrationTests;

public abstract class AdminApiBase
{
    private const string AdminKey = "sentinel-admin-dev-key";
    private readonly IProvedorDeTeste _provedor;

    protected AdminApiBase(IProvedorDeTeste provedor) => _provedor = provedor;

    protected FabricaSentinel Fabrica
    {
        get
        {
            Skip.IfNot(_provedor.Disponivel, _provedor.MotivoIndisponivel);
            return _provedor.Fabrica;
        }
    }

    protected HttpClient Client => Fabrica.CreateClient();

    private static HttpRequestMessage RequisicaoAdmin(HttpMethod metodo, string url, object? corpo = null)
    {
        var req = new HttpRequestMessage(metodo, url);
        req.Headers.Add("X-Admin-Key", AdminKey);
        if (corpo != null)
            req.Content = JsonContent.Create(corpo);
        return req;
    }

    [SkippableFact]
    public async Task Admin_SemChave_Retorna401()
    {
        var resp = await Client.GetAsync("/api/admin/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_ComChaveInvalida_Retorna401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/workspaces");
        req.Headers.Add("X-Admin-Key", "chave-totalmente-errada");
        var resp = await Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_CriarWorkspaceObterDetalhesEListar_Sucesso()
    {
        var slug = $"ws-teste-{Guid.NewGuid():N}"[..20];
        var criarResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new
        {
            name = "Workspace de Teste",
            slug
        }));
        Assert.Equal(HttpStatusCode.Created, criarResp.StatusCode);

        var wsCriado = await criarResp.Content.ReadFromJsonAsync<JsonElement>();
        var wsId = wsCriado.GetProperty("workspaceId").GetGuid();
        Assert.Equal(slug, wsCriado.GetProperty("slug").GetString());

        var listarResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Get, "/api/admin/workspaces"));
        Assert.Equal(HttpStatusCode.OK, listarResp.StatusCode);
        var lista = await listarResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(lista.EnumerateArray(), w => w.GetProperty("workspaceId").GetGuid() == wsId);

        var detResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Get, $"/api/admin/workspaces/{wsId}"));
        Assert.Equal(HttpStatusCode.OK, detResp.StatusCode);
        var detalhes = await detResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wsId, detalhes.GetProperty("workspace").GetProperty("workspaceId").GetGuid());
        Assert.Equal(JsonValueKind.Array, detalhes.GetProperty("projects").ValueKind);
        Assert.Equal(JsonValueKind.Array, detalhes.GetProperty("memberships").ValueKind);
    }

    [SkippableFact]
    public async Task Admin_CriarWorkspaceComSlugDuplicado_Retorna409()
    {
        var slug = $"ws-dup-{Guid.NewGuid():N}"[..20];
        var resp1 = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS 1", slug }));
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);

        var resp2 = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS 2", slug }));
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_CriarProjetoEListar_Sucesso()
    {
        var wsSlug = $"ws-proj-{Guid.NewGuid():N}"[..20];
        var wsResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS Proj", slug = wsSlug }));
        var wsId = (await wsResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspaceId").GetGuid();

        var projSlug = $"proj-{Guid.NewGuid():N}"[..20];
        var projResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, $"/api/admin/workspaces/{wsId}/projects", new
        {
            name = "Meu Projeto",
            slug = projSlug
        }));
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);

        var proj = await projResp.Content.ReadFromJsonAsync<JsonElement>();
        var projId = proj.GetProperty("projectId").GetGuid();
        Assert.Equal(projSlug, proj.GetProperty("slug").GetString());
        Assert.Equal(wsId, proj.GetProperty("workspaceId").GetGuid());

        var listProjResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Get, $"/api/admin/workspaces/{wsId}/projects"));
        Assert.Equal(HttpStatusCode.OK, listProjResp.StatusCode);
        var projetos = await listProjResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(projetos.EnumerateArray(), p => p.GetProperty("projectId").GetGuid() == projId);
    }

    [SkippableFact]
    public async Task Admin_ConfigurarVinculoEListarERemover_Sucesso()
    {
        var wsSlug = $"ws-vinc-{Guid.NewGuid():N}"[..20];
        var wsResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces", new { name = "WS Vinculo", slug = wsSlug }));
        var wsId = (await wsResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workspaceId").GetGuid();

        var vincResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, $"/api/admin/workspaces/{wsId}/memberships", new
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

        var listResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Get, $"/api/admin/workspaces/{wsId}/memberships"));
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var lista = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(lista.EnumerateArray(), m => m.GetProperty("membershipId").GetGuid() == vincId);

        var delResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Delete, $"/api/admin/workspaces/{wsId}/memberships/{vincId}"));
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var listPosResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Get, $"/api/admin/workspaces/{wsId}/memberships"));
        var listaPos = await listPosResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(listaPos.EnumerateArray(), m => m.GetProperty("membershipId").GetGuid() == vincId);
    }

    [SkippableFact]
    public async Task Admin_SessaoWebMantenedor_ExigeCsrfEBloqueiaLeitorETerceiros()
    {
        var leitor = await SessaoWeb.EntrarAsync(Fabrica, "reader-bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await leitor.Http.GetAsync($"/api/admin/workspaces/{WsA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await leitor.PostarAsync($"/api/admin/workspaces/{WsA}/projects", new { name = "X", slug = "x-1" })).StatusCode);

        var alice = await SessaoWeb.EntrarAsync(Fabrica, "maintainer-alice");

        // Mantenedor não cria workspaces globais (reservado a X-Admin-Key)
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostarAsync("/api/admin/workspaces", new { name = "Global", slug = $"ws-g-{Guid.NewGuid():N}"[..16] })).StatusCode);

        // Mutação sem CSRF falha; com CSRF cria projeto no próprio workspace
        var semCsrf = await alice.PostarAsync($"/api/admin/workspaces/{WsA}/projects", new { name = "Sem CSRF", slug = "sem-csrf" }, comCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, semCsrf.StatusCode);

        var projSlug = $"proj-web-{Guid.NewGuid():N}"[..20];
        var comCsrf = await alice.PostarAsync($"/api/admin/workspaces/{WsA}/projects", new { name = "Projeto Web", slug = projSlug }, comCsrf: true);
        Assert.Equal(HttpStatusCode.Created, comCsrf.StatusCode);
    }

    [SkippableFact]
    public async Task Admin_JsonMalformadoOuPapelInvalido_Retorna400()
    {
        var reqJson = RequisicaoAdmin(HttpMethod.Post, "/api/admin/workspaces");
        reqJson.Content = new StringContent("{invalido", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.SendAsync(reqJson)).StatusCode);

        var papelResp = await Client.SendAsync(RequisicaoAdmin(HttpMethod.Post, $"/api/admin/workspaces/{WsA}/memberships", new
        {
            issuer = "https://auth.sentinel.dev",
            subject = "codex-agent",
            role = "SuperAdmin",
            isActive = true
        }));
        Assert.Equal(HttpStatusCode.BadRequest, papelResp.StatusCode);
    }
}

public sealed class AdminApiEmMemoriaTests : AdminApiBase, IClassFixture<ProvedorEmMemoria>
{
    public AdminApiEmMemoriaTests(ProvedorEmMemoria provedor) : base(provedor) { }
}

public sealed class AdminApiPostgreSqlTests : AdminApiBase, IClassFixture<ProvedorPostgreSql>
{
    public AdminApiPostgreSqlTests(ProvedorPostgreSql provedor) : base(provedor) { }
}
