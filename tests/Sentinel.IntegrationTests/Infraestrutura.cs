using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sentinel.Persistence.PostgreSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Sentinel.IntegrationTests;

public static class DadosDemo
{
    public static readonly Guid WsA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid WsB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ProjA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid ProjB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // Token mock para testes
    public const string TokenAgente = "mock:agent-dev-1|read:memory,write:decision,write:incident|claude-code";
    public const string TokenAgenteSomenteLeitura = "mock:agent-dev-1|read:memory|claude-code";
    public const string TokenLeitor = "mock:reader-bob|read:memory|cursor";
    public const string TokenMantenedor = "mock:maintainer-alice|read:memory,write:decision,write:incident|claude-code";
}

// Fábrica de teste da aplicação web
public sealed class FabricaSentinel : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _configuracao;

    public FabricaSentinel(Dictionary<string, string?> configuracao) => _configuracao = configuracao;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var padrao = new Dictionary<string, string?>
        {
            ["Sentinel:AllowMockTokens"] = "true",
            ["Sentinel:Web:EnableDevLogin"] = "true",
            ["Sentinel:Retention:Enabled"] = "false",
            ["Sentinel:AutoMigrate"] = "false",
            ["Sentinel:RateLimit:McpPerMinute"] = "100000",
            ["Sentinel:RateLimit:WebPerMinute"] = "100000",
            ["Sentinel:RateLimit:LoginPerMinute"] = "100000"
        };
        foreach (var (chave, valor) in padrao.Concat(_configuracao))
            builder.UseSetting(chave, valor);
    }
}

public interface IProvedorDeTeste
{
    FabricaSentinel Fabrica { get; }
    bool Disponivel { get; }
    string? MotivoIndisponivel { get; }
}

public sealed class ProvedorEmMemoria : IProvedorDeTeste, IDisposable
{
    public FabricaSentinel Fabrica { get; } = new(new Dictionary<string, string?>
    {
        ["Sentinel:StorageProvider"] = "InMemory",
        ["Sentinel:SeedDemoData"] = "true"
    });

    public bool Disponivel => true;
    public string? MotivoIndisponivel => null;
    public void Dispose() => Fabrica.Dispose();
}

// Provedor PostgreSQL para testes de integração
public sealed class ProvedorPostgreSql : IProvedorDeTeste, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private FabricaSentinel? _fabrica;

    public FabricaSentinel Fabrica => _fabrica ?? throw new InvalidOperationException(MotivoIndisponivel);
    public bool Disponivel { get; private set; }
    public string? MotivoIndisponivel { get; private set; }
    public string StringConexaoAdmin { get; private set; } = string.Empty;
    public string StringConexaoApp { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("SENTINEL_REQUIRE_DOCKER") != "1")
        {
            MotivoIndisponivel = $"Docker indisponível para Testcontainers: {ex.GetType().Name}";
            return;
        }

        StringConexaoAdmin = _container.GetConnectionString();
        var diretorio = Path.Combine(AppContext.BaseDirectory, "db");
        var migrador = new MigradorBancoDados(StringConexaoAdmin);
        await migrador.MigrarAsync(Path.Combine(diretorio, "migrations"));

        var senha = Convert.ToHexString(Guid.NewGuid().ToByteArray()); // gerada por execução, só para o container efêmero
        await using (var conexao = new NpgsqlConnection(StringConexaoAdmin))
        {
            await conexao.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE ROLE sentinel_app_login LOGIN PASSWORD '{senha}' IN ROLE sentinel_app", conexao);
            await cmd.ExecuteNonQueryAsync();
        }

        await migrador.AplicarFixturesAsync(Path.Combine(diretorio, "fixtures"));
        await using (var fonteAdmin = NpgsqlDataSource.Create(StringConexaoAdmin))
            await new RepositorioSentinelPostgreSql(fonteAdmin).ReconstruirProjecoesAsync();

        StringConexaoApp = new NpgsqlConnectionStringBuilder(StringConexaoAdmin) { Username = "sentinel_app_login", Password = senha }.ConnectionString;
        _fabrica = new FabricaSentinel(new Dictionary<string, string?>
        {
            ["Sentinel:StorageProvider"] = "PostgreSQL",
            ["ConnectionStrings:Database"] = StringConexaoApp
        });
        Disponivel = true;
    }

    public async Task DisposeAsync()
    {
        _fabrica?.Dispose();
        if (_container != null)
            await _container.DisposeAsync();
    }
}

// Cliente MCP para testes
public sealed class ClienteMcp
{
    private readonly HttpClient _http;
    private int _id;

    public ClienteMcp(HttpClient http) => _http = http;

    public async Task<HttpResponseMessage> EnviarAsync(object corpo, string? token, Action<HttpRequestMessage>? ajustar = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(corpo) };
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ajustar?.Invoke(req);
        return await _http.SendAsync(req);
    }

    public async Task<JsonElement> RpcAsync(string metodo, object? parametros, string? token)
    {
        var resp = await EnviarAsync(new { jsonrpc = "2.0", id = Interlocked.Increment(ref _id), method = metodo, @params = parametros }, token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Executa chamada de ferramenta MCP
    public async Task<JsonElement> ToolAsync(string nome, object argumentos, string token = DadosDemo.TokenAgente)
    {
        var resposta = await RpcAsync("tools/call", new { name = nome, arguments = argumentos }, token);
        Assert.False(resposta.TryGetProperty("error", out var erro), $"Erro JSON-RPC inesperado: {erro}");
        return resposta.GetProperty("result");
    }
}

public static class ExtensoesResultado
{
    public static bool EhErro(this JsonElement resultado) =>
        resultado.TryGetProperty("isError", out var e) && e.GetBoolean();

    public static JsonElement Dados(this JsonElement resultado) => resultado.GetProperty("structuredContent");

    public static string? CodigoErro(this JsonElement resultado) =>
        resultado.EhErro() ? resultado.Dados().GetProperty("code").GetString() : null;

    public static JsonElement Sucesso(this JsonElement resultado)
    {
        Assert.False(resultado.EhErro(), $"Tool retornou erro: {resultado}");
        return resultado.Dados();
    }
}

// Sessão web de teste
public sealed class SessaoWeb
{
    public HttpClient Http { get; }
    public string TokenCsrf { get; private set; } = string.Empty;
    public JsonElement Estado { get; private set; }

    private SessaoWeb(HttpClient http) => Http = http;

    public static async Task<SessaoWeb> EntrarAsync(FabricaSentinel fabrica, string sujeito)
    {
        var http = fabrica.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var pagina = await http.GetStringAsync("/auth/login?returnUrl=%2Freview");
        var token = WebUtility.HtmlDecode(Regex.Match(pagina, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(token), "Formulário de login sem token antiforgery.");

        var login = await http.PostAsync("/auth/dev-login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["subject"] = sujeito,
            ["__RequestVerificationToken"] = token,
            ["returnUrl"] = "/review"
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var sessao = new SessaoWeb(http);
        await sessao.AtualizarEstadoAsync();
        return sessao;
    }

    public async Task AtualizarEstadoAsync(Guid? workspaceId = null)
    {
        var resp = await Http.GetAsync("/api/review/state" + (workspaceId.HasValue ? $"?workspaceId={workspaceId}" : string.Empty));
        resp.EnsureSuccessStatusCode();
        Estado = await resp.Content.ReadFromJsonAsync<JsonElement>();
        TokenCsrf = Estado.GetProperty("csrf").GetProperty("token").GetString()!;
    }

    public Task<HttpResponseMessage> PostarAsync(string caminho, object corpo, bool comCsrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, caminho) { Content = JsonContent.Create(corpo) };
        if (comCsrf)
            req.Headers.Add("X-CSRF-Token", TokenCsrf);
        return Http.SendAsync(req);
    }

    public async Task<JsonElement> AprovarAsync(Guid topicId, Guid proposalId, int expectedVersion)
    {
        var resp = await PostarAsync("/api/review/approve", new
        {
            workspaceId = DadosDemo.WsA,
            projectId = DadosDemo.ProjA,
            topicId,
            proposalId,
            expectedVersion,
            idempotencyKey = Guid.NewGuid().ToString()
        });
        Assert.True(resp.IsSuccessStatusCode, $"Aprovação falhou: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
