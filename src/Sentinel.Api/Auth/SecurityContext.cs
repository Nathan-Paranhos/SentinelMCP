using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Sentinel.Core.CasosDeUso;
using Sentinel.Core.Excecoes;

namespace Sentinel.Api.Auth;

public static class EsquemasDeAutenticacao
{
    public const string JwtMcp = "SentinelMcpJwt";
    public const string CookieWeb = "SentinelWeb";
    public const string OidcWeb = "SentinelOidc";
}

public static class Escopos
{
    public const string LerMemoria = "read:memory";
    public const string EscreverDecisao = "write:decision";
    public const string EscreverIncidente = "write:incident";
    public static readonly string[] Todos = [LerMemoria, EscreverDecisao, EscreverIncidente];
}

public sealed record IdentidadeAgente(Ator Ator, IReadOnlySet<string> Escopos, string Metodo)
{
    public void ExigirEscopo(string escopo)
    {
        if (!Escopos.Contains(escopo))
            throw new ExcecaoDeAcessoNegado($"Escopo insuficiente. Necessário: '{escopo}'.");
    }
}

// Configuração de chave de API para agentes
public sealed class ConfiguracaoChaveApi
{
    public string Nome { get; set; } = string.Empty;
    public string KeySha256 { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public List<string> Scopes { get; set; } = [];
}

public interface IServicoIdentidadeAgente
{
    // Autentica o agente MCP
    Task<IdentidadeAgente> AutenticarAsync(HttpContext contexto);
}

public sealed class ServicoIdentidadeAgente : IServicoIdentidadeAgente
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _ambiente;
    private readonly ILogger<ServicoIdentidadeAgente> _log;
    private readonly List<(byte[] Hash, ConfiguracaoChaveApi Config)> _chaves;

    public ServicoIdentidadeAgente(IConfiguration config, IWebHostEnvironment ambiente, ILogger<ServicoIdentidadeAgente> log)
    {
        _config = config;
        _ambiente = ambiente;
        _log = log;
        _chaves = (config.GetSection("Sentinel:ApiKeys").Get<List<ConfiguracaoChaveApi>>() ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.KeySha256) && !string.IsNullOrWhiteSpace(k.Subject) && !string.IsNullOrWhiteSpace(k.Issuer))
            .Select(k => (Convert.FromHexString(k.KeySha256.Trim()), k))
            .ToList();
    }

    public static bool JwtConfigurado(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Authentication:Mcp:Authority"]);

    // Verifica se tokens mock estão habilitados
    public static bool MockPermitido(IConfiguration config, IHostEnvironment ambiente) =>
        !ambiente.IsProduction() && config.GetValue<bool>("Sentinel:AllowMockTokens");

    public async Task<IdentidadeAgente> AutenticarAsync(HttpContext contexto)
    {
        var cabecalho = contexto.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(cabecalho) || !cabecalho.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ExcecaoDeNaoAutorizado("Cabeçalho Authorization ausente. Esperado token Bearer.");

        var token = cabecalho["Bearer ".Length..].Trim();
        if (token.Length == 0)
            throw new ExcecaoDeNaoAutorizado("Token Bearer vazio.");

        // Autenticação por chave de API
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var (hashConfigurado, chave) in _chaves)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, hashConfigurado))
            {
                return new IdentidadeAgente(
                    new Ator(chave.Issuer, chave.Subject, chave.ClientId, CanalDeAcesso.Mcp),
                    chave.Scopes.ToHashSet(StringComparer.Ordinal), "api-key");
            }
        }

        // Autenticação por token mock
        if (token.StartsWith("mock:", StringComparison.Ordinal))
        {
            if (!MockPermitido(_config, _ambiente))
                throw new ExcecaoDeNaoAutorizado("Token inválido.");
            return LerTokenMock(token["mock:".Length..]);
        }

        // Autenticação por token JWT
        if (JwtConfigurado(_config))
        {
            var resultado = await contexto.AuthenticateAsync(EsquemasDeAutenticacao.JwtMcp);
            if (resultado.Succeeded && resultado.Principal != null)
                return LerJwt(resultado.Principal);

            _log.LogInformation("Falha de autenticação JWT MCP: {Motivo}", resultado.Failure?.GetType().Name ?? "sem credencial");
        }

        throw new ExcecaoDeNaoAutorizado("Token inválido ou expirado.");
    }

    private static IdentidadeAgente LerJwt(ClaimsPrincipal principal)
    {
        var emissor = principal.FindFirstValue("iss");
        var sujeito = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(emissor) || string.IsNullOrWhiteSpace(sujeito))
            throw new ExcecaoDeNaoAutorizado("Token sem as claims 'iss' e 'sub'.");

        var clienteId = principal.FindFirstValue("client_id") ?? principal.FindFirstValue("azp");
        var escopos = principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        return new IdentidadeAgente(new Ator(emissor, sujeito, clienteId, CanalDeAcesso.Mcp), escopos, "jwt");
    }

    // Lê token mock no formato: mock:sujeito|escopos|clientId|emissor
    private static IdentidadeAgente LerTokenMock(string dados)
    {
        var partes = dados.Split('|');
        var sujeito = partes.ElementAtOrDefault(0);
        if (string.IsNullOrWhiteSpace(sujeito))
            throw new ExcecaoDeNaoAutorizado("Token mock sem sujeito.");

        var escopos = (partes.ElementAtOrDefault(1) is { Length: > 0 } texto ? texto : string.Join(',', Escopos.Todos))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var clienteId = partes.ElementAtOrDefault(2) is { Length: > 0 } c ? c : "dev-client";
        var emissor = partes.ElementAtOrDefault(3) is { Length: > 0 } e ? e : "https://auth.sentinel.dev";

        return new IdentidadeAgente(new Ator(emissor, sujeito.Trim(), clienteId, CanalDeAcesso.Mcp), escopos, "mock");
    }
}
