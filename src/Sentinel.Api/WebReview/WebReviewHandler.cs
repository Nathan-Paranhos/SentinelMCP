using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

using Microsoft.AspNetCore.Authentication.Cookies;
using Sentinel.Api.Auth;
using Sentinel.Core.CasosDeUso;

using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;

namespace Sentinel.Api.WebReview;

// Manipulador da página e API de revisão humana.
public sealed class WebReviewHandler
{
    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ServicoDeMemoria _servico;
    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _ambiente;
    private readonly ILogger<WebReviewHandler> _log;
    private readonly Lazy<string> _paginaRevisao;

    public WebReviewHandler(ServicoDeMemoria servico, IAntiforgery antiforgery, IConfiguration config, IWebHostEnvironment ambiente, ILogger<WebReviewHandler> log)
    {
        _servico = servico;
        _antiforgery = antiforgery;
        _config = config;
        _ambiente = ambiente;
        _log = log;
        _paginaRevisao = new Lazy<string>(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WebReview", "review.html")));
    }

    public static bool OidcConfigurado(IConfiguration config) => !string.IsNullOrWhiteSpace(config["Authentication:Web:Authority"]);

    public static bool LoginDevPermitido(IConfiguration config, IHostEnvironment ambiente) =>
        !ambiente.IsProduction() && config.GetValue<bool>("Sentinel:Web:EnableDevLogin");

    // Sessão web
    private static async Task<Ator?> ObterAtorAsync(HttpContext contexto)
    {
        // Bloqueia credenciais Bearer em rotas de revisão
        if (contexto.Request.Headers.Authorization.Count > 0)
            throw new ExcecaoDeAcessoNegado("Credenciais Bearer não são aceitas nas rotas de revisão humana. Use a sessão web.");

        var resultado = await contexto.AuthenticateAsync(EsquemasDeAutenticacao.CookieWeb);
        if (!resultado.Succeeded || resultado.Principal == null)
            return null;

        var emissor = resultado.Principal.FindFirstValue("iss");
        var sujeito = resultado.Principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(emissor) || string.IsNullOrWhiteSpace(sujeito))
            return null;

        contexto.User = resultado.Principal;
        return new Ator(emissor, sujeito, "web-session", CanalDeAcesso.Web);
    }

    private static async Task<(bool Permitido, Ator? Ator)> TentarObterAtorWebAsync(HttpContext contexto)
    {
        try
        {
            return (true, await ObterAtorAsync(contexto));
        }
        catch (ExcecaoDeAcessoNegado ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await contexto.Response.WriteAsync(ex.Message);
            return (false, null);
        }
    }

    private static string? UrlLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : null;

    // Renderização de páginas HTML
    public async Task RenderReviewPageAsync(HttpContext contexto)
    {
        var (permitido, ator) = await TentarObterAtorWebAsync(contexto);
        if (!permitido)
            return;

        if (ator == null)
        {
            var retorno = contexto.Request.Path + contexto.Request.QueryString;
            contexto.Response.Redirect("/auth/login?returnUrl=" + Uri.EscapeDataString(retorno));
            return;
        }

        // Descarta cookie obsoleto caso os dados locais/banco tenham sido reiniciados ou o vínculo removido
        var espacosAtivos = await _servico.ObterEspacosEProjetosDaIdentidadeAsync(ator, contexto.RequestAborted);
        if (espacosAtivos.Count == 0)
        {
            await contexto.SignOutAsync(EsquemasDeAutenticacao.CookieWeb);
            var retorno = contexto.Request.Path + contexto.Request.QueryString;
            if (!OidcConfigurado(_config) && await _servico.ConfiguracaoInicialPendenteAsync(contexto.RequestAborted))
            {
                contexto.Response.Redirect("/auth/setup?returnUrl=" + Uri.EscapeDataString(retorno));
                return;
            }
            contexto.Response.Redirect("/auth/login?erro=sem_vinculo&returnUrl=" + Uri.EscapeDataString(retorno));
            return;
        }

        contexto.Response.ContentType = "text/html; charset=utf-8";
        contexto.Response.Headers.CacheControl = "no-store";
        await contexto.Response.WriteAsync(_paginaRevisao.Value);
    }

    public async Task RenderLoginAsync(HttpContext contexto)
    {
        var retorno = UrlLocal(contexto.Request.Query["returnUrl"]) ?? "/review";

        if (OidcConfigurado(_config))
        {
            await contexto.ChallengeAsync(EsquemasDeAutenticacao.OidcWeb, new AuthenticationProperties { RedirectUri = retorno });
            return;
        }

        if (LoginDevPermitido(_config, _ambiente))
        {
            var tokensDev = _antiforgery.GetAndStoreTokens(contexto);
            var encDev = HtmlEncoder.Default;
            contexto.Response.ContentType = "text/html; charset=utf-8";
            contexto.Response.Headers.CacheControl = "no-store";
            await contexto.Response.WriteAsync($@"<!DOCTYPE html>
<html lang=""pt-BR"" data-theme=""dark"">
<head>
<meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Sentinel-MCP — Acesso Web de Teste</title>
<link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
<link rel=""stylesheet"" href=""/css/tokens.css""><link rel=""stylesheet"" href=""/css/components.css""><link rel=""stylesheet"" href=""/css/views.css"">
<script src=""/js/theme-boot.js""></script>
</head>
<body class=""auth-shell"">
<form method=""post"" action=""/auth/dev-login"" class=""auth-card"">
  <div class=""auth-brand""><img src=""/sentinel-verde.svg"" alt=""Sentinel""><span>Sentinel-MCP</span></div>
  <h1 class=""auth-title"">Acesso Rápido (Modo Teste)</h1>
  <p class=""auth-desc"">Habilitado apenas via Sentinel:Web:EnableDevLogin em suítes de teste.</p>
  <label class=""form-field""><span>Sujeito (sub)</span><input class=""form-input"" name=""subject"" required maxlength=""200""></label>
  <input type=""hidden"" name=""{encDev.Encode(tokensDev.FormFieldName)}"" value=""{encDev.Encode(tokensDev.RequestToken ?? string.Empty)}"">
  <input type=""hidden"" name=""returnUrl"" value=""{encDev.Encode(retorno)}"">
  <button type=""submit"" class=""btn-macos btn-macos-primary"">Entrar</button>
</form>
</body>
</html>");
            return;
        }

        if (await _servico.ConfiguracaoInicialPendenteAsync(contexto.RequestAborted))
        {
            contexto.Response.Redirect("/auth/setup?returnUrl=" + Uri.EscapeDataString(retorno));
            return;
        }

        var erroCodigo = contexto.Request.Query["erro"].ToString();
        var erroMsg = erroCodigo switch
        {
            "1" => "Usuário ou senha inválidos.",
            "sem_vinculo" => "Sua sessão não possui vínculo ativo com nenhum workspace neste ambiente. Entre novamente.",
            _ => null
        };
        await RenderFormularioLoginLocalAsync(contexto, retorno, erroMsg);
    }

    private static string BotaoOlhoSenha(string inputId) =>
        $@"<button type=""button"" class=""password-toggle-btn"" data-toggle-password=""{inputId}"" aria-label=""Mostrar senha"" title=""Mostrar senha"" aria-pressed=""false""><svg width=""15"" height=""15"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z""/><circle cx=""12"" cy=""12"" r=""3""/></svg></button>";

    private async Task RenderFormularioLoginLocalAsync(HttpContext contexto, string retorno, string? erro)
    {
        var (permitido, atorAtual) = await TentarObterAtorWebAsync(contexto);
        if (!permitido)
            return;

        var tokens = _antiforgery.GetAndStoreTokens(contexto);
        var enc = HtmlEncoder.Default;
        var blocoErro = string.IsNullOrWhiteSpace(erro)
            ? string.Empty
            : $@"<div class=""review-permission-notice"" role=""alert"">{enc.Encode(erro)}</div>";

        var espacosAtivos = atorAtual != null
            ? await _servico.ObterEspacosEProjetosDaIdentidadeAsync(atorAtual, contexto.RequestAborted)
            : [];
        var blocoSessaoAtiva = espacosAtivos.Count > 0
            ? $@"<div class=""callout-guide"" style=""margin-bottom:0;""><span>Sessão ativa como <strong>{enc.Encode(atorAtual!.Sujeito)}</strong> ({espacosAtivos.Count} workspace(s)).</span><a href=""{enc.Encode(retorno)}"" class=""btn-macos btn-macos-secondary btn-macos-sm"">Ir para Console</a></div>"
            : string.Empty;

        contexto.Response.ContentType = "text/html; charset=utf-8";
        contexto.Response.Headers.CacheControl = "no-store";
        await contexto.Response.WriteAsync($@"<!DOCTYPE html>
<html lang=""pt-BR"" data-theme=""dark"">
<head>
<meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Sentinel-MCP — Entrar no Console</title>
<link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
<link rel=""stylesheet"" href=""/css/tokens.css""><link rel=""stylesheet"" href=""/css/components.css""><link rel=""stylesheet"" href=""/css/views.css"">
<script src=""/js/theme-boot.js""></script>
<script src=""/js/auth-setup.js"" defer></script>
</head>
<body class=""auth-shell"">
<form method=""post"" action=""/auth/login"" class=""auth-card"">
  <div class=""auth-brand""><img src=""/sentinel-verde.svg"" alt=""Sentinel""><span>Sentinel-MCP</span></div>
  <h1 class=""auth-title"">Entrar no Console de Governança</h1>
  <p class=""auth-desc"">Informe o <strong>usuário</strong> cadastrado na configuração inicial ou em Administração (não o nome de exibição) e sua senha.</p>
  {blocoSessaoAtiva}
  {blocoErro}
  <label class=""form-field""><span>Usuário</span><input class=""form-input"" name=""username"" required maxlength=""80"" autocomplete=""username"" placeholder=""seu.usuario""></label>
  <label class=""form-field"" for=""login-password""><span>Senha</span><div class=""password-input-wrap""><input id=""login-password"" class=""form-input"" name=""password"" type=""password"" required maxlength=""128"" autocomplete=""current-password"">{BotaoOlhoSenha("login-password")}</div></label>
  <input type=""hidden"" name=""{enc.Encode(tokens.FormFieldName)}"" value=""{enc.Encode(tokens.RequestToken ?? string.Empty)}"">
  <input type=""hidden"" name=""returnUrl"" value=""{enc.Encode(retorno)}"">
  <div class=""auth-actions"">
    <a href=""/"" class=""btn-macos btn-macos-secondary"">Voltar</a>
    <button type=""submit"" class=""btn-macos btn-macos-primary"">Entrar</button>
  </div>
</form>
</body>
</html>");
    }

    public async Task HandleLocalLoginAsync(HttpContext contexto)
    {
        if (OidcConfigurado(_config))
        {
            contexto.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!await AntiforgeryValidoAsync(contexto))
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsync("Token CSRF inválido.");
            return;
        }

        var formulario = await contexto.Request.ReadFormAsync(contexto.RequestAborted);
        var usuarioNome = formulario["username"].ToString();
        var senha = formulario["password"].ToString();
        var retorno = UrlLocal(formulario["returnUrl"]) ?? "/review";

        try
        {
            var usuario = await _servico.AutenticarUsuarioLocalAsync(usuarioNome, senha, contexto.RequestAborted);
            var identidade = new ClaimsIdentity(
                [new Claim("sub", usuario.Sujeito), new Claim("iss", usuario.Emissor), new Claim("name", usuario.NomeExibicao ?? usuario.Sujeito)],
                EsquemasDeAutenticacao.CookieWeb, "name", "role");
            await contexto.SignInAsync(EsquemasDeAutenticacao.CookieWeb, new ClaimsPrincipal(identidade));
            _log.LogInformation("Login web realizado para {Sujeito}", usuario.Sujeito);
            contexto.Response.Redirect(retorno);
        }
        catch (ExcecaoDeNaoAutorizado)
        {
            contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await RenderFormularioLoginLocalAsync(contexto, retorno, "Usuário ou senha inválidos.");
        }
    }

    public async Task RenderSetupAsync(HttpContext contexto)
    {
        var (permitido, ator) = await TentarObterAtorWebAsync(contexto);
        if (!permitido)
            return;

        var retorno = UrlLocal(contexto.Request.Query["returnUrl"]) ?? "/review";
        if (OidcConfigurado(_config) || !await _servico.ConfiguracaoInicialPendenteAsync(contexto.RequestAborted))
        {
            if (ator != null && (await _servico.ObterEspacosEProjetosDaIdentidadeAsync(ator, contexto.RequestAborted)).Count > 0)
            {
                contexto.Response.Redirect(retorno);
                return;
            }
            contexto.Response.Redirect("/auth/login?returnUrl=" + Uri.EscapeDataString(retorno));
            return;
        }

        await RenderFormularioSetupAsync(contexto, retorno, null);
    }

    private string? ObterAdminKeyConfigurada() =>
        _config["Sentinel:AdminApiKey"] ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY");

    private async Task RenderFormularioSetupAsync(HttpContext contexto, string retorno, string? erro, IFormCollection? valores = null)
    {
        var tokens = _antiforgery.GetAndStoreTokens(contexto);
        var enc = HtmlEncoder.Default;
        var chaveConfigurada = ObterAdminKeyConfigurada();
        var exigirAdminKey = _ambiente.IsProduction();
        var erroEfetivo = erro;
        if (exigirAdminKey && string.IsNullOrWhiteSpace(chaveConfigurada) && string.IsNullOrWhiteSpace(erroEfetivo))
            erroEfetivo = "Em Production, defina ADMIN_API_KEY (ou Sentinel:AdminApiKey) no servidor para habilitar a configuração inicial.";

        var blocoErro = string.IsNullOrWhiteSpace(erroEfetivo)
            ? string.Empty
            : $@"<div class=""review-permission-notice"" role=""alert"">{enc.Encode(erroEfetivo)}</div>";
        var campoAdminKey = exigirAdminKey
            ? $@"<label class=""form-field"" for=""setup-admin-key""><span>Chave de Administração (ADMIN_API_KEY)</span><div class=""password-input-wrap""><input id=""setup-admin-key"" class=""form-input"" name=""adminKey"" type=""password"" required>{BotaoOlhoSenha("setup-admin-key")}</div></label>"
            : string.Empty;

        string Valor(string campo) => enc.Encode(valores?[campo].ToString() ?? string.Empty);

        contexto.Response.ContentType = "text/html; charset=utf-8";
        contexto.Response.Headers.CacheControl = "no-store";
        await contexto.Response.WriteAsync($@"<!DOCTYPE html>
<html lang=""pt-BR"" data-theme=""dark"">
<head>
<meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Sentinel-MCP — Configuração Inicial</title>
<link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
<link rel=""stylesheet"" href=""/css/tokens.css""><link rel=""stylesheet"" href=""/css/components.css""><link rel=""stylesheet"" href=""/css/views.css"">
<script src=""/js/theme-boot.js""></script>
<script src=""/js/auth-setup.js"" defer></script>
</head>
<body class=""auth-shell"">
<form method=""post"" action=""/auth/setup"" class=""auth-card auth-card-wide"">
  <div class=""auth-brand""><img src=""/sentinel-verde.svg"" alt=""Sentinel""><span>Sentinel-MCP</span></div>
  <h1 class=""auth-title"">Configuração Inicial do Workspace</h1>
  <p class=""auth-desc"">Ambiente limpo e sem dados fictícios. Crie seu primeiro espaço de trabalho, projeto inicial e usuário administrador (Maintainer).</p>
  {blocoErro}
  <div class=""form-grid"">
    <label class=""form-field""><span>Nome do Workspace</span><input id=""setup-ws-name"" class=""form-input"" name=""workspaceName"" value=""{Valor("workspaceName")}"" required maxlength=""120"" placeholder=""Ex.: Engenharia da Empresa""></label>
    <label class=""form-field""><span>Slug do Workspace</span><input id=""setup-ws-slug"" class=""form-input"" name=""workspaceSlug"" value=""{Valor("workspaceSlug")}"" required maxlength=""80"" placeholder=""engenharia-empresa""></label>
  </div>
  <div class=""form-grid"">
    <label class=""form-field""><span>Primeiro Projeto</span><input id=""setup-proj-name"" class=""form-input"" name=""projectName"" value=""{Valor("projectName")}"" required maxlength=""120"" placeholder=""Ex.: Plataforma Core""></label>
    <label class=""form-field""><span>Slug do Projeto</span><input id=""setup-proj-slug"" class=""form-input"" name=""projectSlug"" value=""{Valor("projectSlug")}"" required maxlength=""80"" placeholder=""plataforma-core""></label>
  </div>
  <div class=""form-grid"">
    <label class=""form-field""><span>Usuário Administrador</span><input class=""form-input"" name=""username"" value=""{Valor("username")}"" required maxlength=""80"" autocomplete=""username"" placeholder=""seu.usuario""></label>
    <label class=""form-field""><span>Nome de Exibição</span><input class=""form-input"" name=""displayName"" value=""{Valor("displayName")}"" maxlength=""120"" placeholder=""Seu Nome""></label>
  </div>
  <div class=""form-grid"">
    <label class=""form-field"" for=""setup-password""><span>Senha (mínimo 8 caracteres)</span><div class=""password-input-wrap""><input id=""setup-password"" class=""form-input"" name=""password"" type=""password"" required minlength=""8"" maxlength=""128"" autocomplete=""new-password"">{BotaoOlhoSenha("setup-password")}</div></label>
    <label class=""form-field"" for=""setup-confirm-password""><span>Confirmar Senha</span><div class=""password-input-wrap""><input id=""setup-confirm-password"" class=""form-input"" name=""confirmPassword"" type=""password"" required minlength=""8"" maxlength=""128"" autocomplete=""new-password"">{BotaoOlhoSenha("setup-confirm-password")}</div></label>
  </div>
  <div id=""setup-password-match"" class=""password-match-hint"" aria-live=""polite""></div>
  {campoAdminKey}
  <input type=""hidden"" name=""{enc.Encode(tokens.FormFieldName)}"" value=""{enc.Encode(tokens.RequestToken ?? string.Empty)}"">
  <input type=""hidden"" name=""returnUrl"" value=""{enc.Encode(retorno)}"">
  <div class=""auth-actions"">
    <a href=""/"" class=""btn-macos btn-macos-secondary"">Voltar</a>
    <button type=""submit"" class=""btn-macos btn-macos-primary"">Criar Workspace e Entrar</button>
  </div>
</form>
</body>
</html>");
    }

    public async Task HandleSetupAsync(HttpContext contexto)
    {
        if (OidcConfigurado(_config))
        {
            contexto.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!await AntiforgeryValidoAsync(contexto))
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsync("Token CSRF inválido.");
            return;
        }

        var formulario = await contexto.Request.ReadFormAsync(contexto.RequestAborted);
        var retorno = UrlLocal(formulario["returnUrl"]) ?? "/review";

        var chaveConfigurada = ObterAdminKeyConfigurada();
        if (_ambiente.IsProduction())
        {
            if (string.IsNullOrWhiteSpace(chaveConfigurada))
            {
                contexto.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await RenderFormularioSetupAsync(contexto, retorno, "Em Production, configure ADMIN_API_KEY (ou Sentinel:AdminApiKey) no servidor antes de concluir a configuração inicial.", formulario);
                return;
            }

            var recebida = formulario["adminKey"].ToString();
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(recebida),
                    System.Text.Encoding.UTF8.GetBytes(chaveConfigurada)))
            {
                contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
                await RenderFormularioSetupAsync(contexto, retorno, "ADMIN_API_KEY inválida para configuração inicial em Production.", formulario);
                return;
            }
        }

        var senha = formulario["password"].ToString();
        var confirmacao = formulario["confirmPassword"].ToString();
        if (!string.Equals(senha, confirmacao, StringComparison.Ordinal))
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await RenderFormularioSetupAsync(contexto, retorno, "As senhas informadas não conferem.", formulario);
            return;
        }

        var emissorLocal = _config["Sentinel:Web:LocalIssuer"] ?? "https://auth.sentinel.dev";
        try
        {
            var r = await _servico.ExecutarConfiguracaoInicialAsync(new EntradaConfiguracaoInicial(
                formulario["workspaceName"].ToString(),
                formulario["workspaceSlug"].ToString(),
                formulario["projectName"].ToString(),
                formulario["projectSlug"].ToString(),
                formulario["username"].ToString(),
                formulario["displayName"].ToString(),
                senha), emissorLocal, contexto.RequestAborted);

            var identidade = new ClaimsIdentity(
                [new Claim("sub", r.Usuario.Sujeito), new Claim("iss", r.Usuario.Emissor), new Claim("name", r.Usuario.NomeExibicao ?? r.Usuario.Sujeito)],
                EsquemasDeAutenticacao.CookieWeb, "name", "role");
            await contexto.SignInAsync(EsquemasDeAutenticacao.CookieWeb, new ClaimsPrincipal(identidade));
            _log.LogInformation("Configuração inicial concluída: workspace {WorkspaceId}, administrador {Sujeito}", r.EspacoTrabalho.EspacoTrabalhoId, r.Usuario.Sujeito);
            contexto.Response.Redirect(retorno);
        }
        catch (ExcecaoDeDominio ex)
        {
            contexto.Response.StatusCode = ex is ExcecaoDeConflito ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            await RenderFormularioSetupAsync(contexto, retorno, ex.Message, formulario);
        }
    }

    public async Task HandleDevLoginAsync(HttpContext contexto)
    {
        if (!LoginDevPermitido(_config, _ambiente))
        {
            contexto.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!await AntiforgeryValidoAsync(contexto))
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsync("Token CSRF inválido.");
            return;
        }

        var formulario = await contexto.Request.ReadFormAsync(contexto.RequestAborted);
        var sujeito = formulario["subject"].ToString().Trim();
        if (sujeito.Length is 0 or > 200)
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsync("Sujeito inválido.");
            return;
        }

        var emissor = _config["Sentinel:Web:DevLoginIssuer"] ?? "https://auth.sentinel.dev";
        var identidade = new ClaimsIdentity(
            [new Claim("sub", sujeito), new Claim("iss", emissor), new Claim("name", sujeito)],
            EsquemasDeAutenticacao.CookieWeb, "name", "role");
        await contexto.SignInAsync(EsquemasDeAutenticacao.CookieWeb, new ClaimsPrincipal(identidade));
        _log.LogInformation("Login de desenvolvimento para {Sujeito}", sujeito);

        contexto.Response.Redirect(UrlLocal(formulario["returnUrl"]) ?? "/review");
    }

    public async Task HandleLogoutAsync(HttpContext contexto)
    {
        if (await ObterAtorAsync(contexto) != null && !await AntiforgeryValidoAsync(contexto))
        {
            await EscreverErroAsync(contexto, StatusCodes.Status400BadRequest, "CSRF_INVALID", "Token CSRF inválido.");
            return;
        }

        await contexto.SignOutAsync(EsquemasDeAutenticacao.CookieWeb);
        contexto.Response.StatusCode = StatusCodes.Status200OK;
        await contexto.Response.WriteAsJsonAsync(new { status = "signed_out" }, OpcoesJson);
    }

    // Endpoints da API de revisão
    public Task HandleStateAsync(HttpContext contexto) => ExecutarAsync(contexto, exigirCsrf: false, async ator =>
    {
        Guid? espacoId = Guid.TryParse(contexto.Request.Query["workspaceId"], out var g) ? g : null;
        var painel = await _servico.ObterPainelAsync(ator, espacoId, contexto.RequestAborted);
        var tokens = _antiforgery.GetAndStoreTokens(contexto);
        contexto.Response.Headers.CacheControl = "no-store";

        object Espaco(EspacoTrabalho e) => new { id = e.EspacoTrabalhoId, nome = e.Nome, slug = e.IdentificadorSlug };
        object Evidencias(IEnumerable<Evidencia> ev) => ev.Select(e => new { tipo = e.Tipo, rotulo = e.Rotulo, url = e.Url }).ToList();

        var provedorArmazenamento = _config["Sentinel:StorageProvider"] ?? "InMemory";
        var arquivoLocal = _config["Sentinel:LocalDataFile"];
        var resumoPersistencia = string.Equals(provedorArmazenamento, "PostgreSQL", StringComparison.OrdinalIgnoreCase)
            ? "PostgreSQL com RLS forçada"
            : (!string.IsNullOrWhiteSpace(arquivoLocal)
                ? $"Arquivo local persistido ({arquivoLocal})"
                : "Em memória (sem arquivo persistido)");

        return new
        {
            sistema = new
            {
                armazenamento = provedorArmazenamento,
                persistenciaLocal = resumoPersistencia,
                autenticacaoWeb = OidcConfigurado(_config) ? "OIDC PKCE" : (LoginDevPermitido(_config, _ambiente) ? "Dev" : "Local PBKDF2"),
                tokensMockPermitidos = ServicoIdentidadeAgente.MockPermitido(_config, _ambiente),
                ambiente = _ambiente.EnvironmentName
            },
            usuario = new
            {
                sujeito = ator.Sujeito,
                emissor = ator.Emissor,
                papel = painel.Vinculo.Papel.ParaTexto(),
                podeRevisar = painel.Vinculo.Papel == PapelUsuario.Mantenedor
            },
            csrf = new { headerName = tokens.HeaderName, token = tokens.RequestToken },
            workspace = Espaco(painel.EspacoTrabalho),
            workspaces = painel.EspacosDisponiveis.Select(Espaco).ToList(),
            projetos = painel.Projetos.Select(pr => new
            {
                id = pr.ProjetoId,
                nome = pr.Nome,
                slug = pr.IdentificadorSlug,
                criadoEm = pr.CriadoEm
            }).ToList(),
            propostas = painel.Propostas.Select(p =>
            {
                var vigente = painel.DecisoesPorTopico.GetValueOrDefault(p.TopicoId);
                return new
                {
                    espacoTrabalhoId = p.EspacoTrabalhoId,
                    projetoId = p.ProjetoId,
                    topicoId = p.TopicoId,
                    chaveTopico = p.ChaveTopico,
                    versaoFluxo = p.VersaoFluxo,
                    propostaPendenteId = p.PropostaId,
                    titulo = p.Titulo,
                    resumo = p.Resumo,
                    justificativa = p.Justificativa,
                    chavesTecnicas = p.ChavesTecnicas,
                    incidentesRelacionadosIds = p.IncidentesRelacionadosIds,
                    evidencias = Evidencias(p.Evidencias),
                    autorSujeito = p.AutorSujeito,
                    clienteId = p.ClienteId,
                    propostaEm = p.PropostaEm,
                    decisaoVigente = vigente is { RegistroAtivoId: not null }
                        ? new
                        {
                            registroAtivoId = vigente.RegistroAtivoId,
                            versaoRegistroAtivo = vigente.VersaoRegistroAtivo,
                            titulo = vigente.Titulo,
                            resumo = vigente.Resumo,
                            justificativa = vigente.Justificativa,
                            chavesTecnicas = vigente.ChavesTecnicas,
                            evidencias = Evidencias(vigente.Evidencias)
                        }
                        : null
                };
            }).ToList(),
            decisoes = painel.DecisoesVigentes.Select(d => new
            {
                topicoId = d.TopicoId,
                espacoTrabalhoId = d.EspacoTrabalhoId,
                projetoId = d.ProjetoId,
                chaveTopico = d.ChaveTopico,
                registroAtivoId = d.RegistroAtivoId,
                versaoRegistroAtivo = d.VersaoRegistroAtivo,
                versaoFluxo = d.VersaoFluxo,
                situacao = d.Situacao.ParaTexto(),
                propostaPendenteId = d.PropostaPendenteId,
                titulo = d.Titulo,
                resumo = d.Resumo,
                justificativa = d.Justificativa,
                chavesTecnicas = d.ChavesTecnicas,
                incidentesRelacionadosIds = d.IncidentesRelacionadosIds,
                evidencias = Evidencias(d.Evidencias),
                atualizadoEm = d.AtualizadoEm
            }).ToList(),
            incidentes = painel.Incidentes.Select(i => new
            {
                incidenteId = i.IncidenteId,
                espacoTrabalhoId = i.EspacoTrabalhoId,
                projetoId = i.ProjetoId,
                versaoRegistro = i.VersaoRegistro,
                versaoFluxo = i.VersaoFluxo,
                situacao = i.Situacao.ParaTexto(),
                titulo = i.Titulo,
                resumo = i.Resumo,
                ocorridoEm = i.OcorridoEm,
                chavesTecnicas = i.ChavesTecnicas,
                evidencias = Evidencias(i.Evidencias),
                criadoEm = i.CriadoEm
            }).ToList(),
            eventos = painel.Eventos.Select(e => new
            {
                eventoId = e.EventoId,
                espacoTrabalhoId = e.EspacoTrabalhoId,
                projetoId = e.ProjetoId,
                fluxoId = e.FluxoId,
                versaoFluxo = e.VersaoFluxo,
                tipo = e.Tipo,
                versaoEsquema = e.VersaoEsquema,
                registradoEm = e.RegistradoEm,
                autorSujeito = e.AutorSujeito,
                autorEmissor = e.AutorEmissor,
                clienteId = e.ClienteId,
                cargaId = e.CargaId,
                eventoReferenciaId = e.EventoReferenciaId
            }).ToList()
        };
    });

    public Task HandleApproveAsync(HttpContext contexto) => ExecutarAsync(contexto, exigirCsrf: true, async ator =>
    {
        var c = await LerCorpoAsync(contexto);
        var r = await _servico.AprovarDecisaoAsync(ator, new EntradaRevisao(
            c.Guid("workspaceId"), c.Guid("projectId"), c.Guid("topicId"), c.Guid("proposalId"),
            c.Inteiro("expectedVersion"), c.Texto("idempotencyKey")), contexto.RequestAborted);
        _log.LogInformation("Proposta {Proposta} aprovada por {Sujeito}", r.RegistroAtivoId, ator.Sujeito);
        return new { topicId = r.TopicoId, activeRecordId = r.RegistroAtivoId, streamVersion = r.VersaoFluxo, status = r.Situacao };
    });

    public Task HandleRejectAsync(HttpContext contexto) => ExecutarAsync(contexto, exigirCsrf: true, async ator =>
    {
        var c = await LerCorpoAsync(contexto);
        var r = await _servico.RejeitarDecisaoAsync(ator, new EntradaRevisao(
            c.Guid("workspaceId"), c.Guid("projectId"), c.Guid("topicId"), c.Guid("proposalId"),
            c.Inteiro("expectedVersion"), c.Texto("idempotencyKey"), c.Texto("reason")), contexto.RequestAborted);
        _log.LogInformation("Proposta no tópico {Topico} rejeitada por {Sujeito}", r.TopicoId, ator.Sujeito);
        return new { topicId = r.TopicoId, streamVersion = r.VersaoFluxo, status = r.Situacao };
    });

    public Task HandleRestoreAsync(HttpContext contexto) => ExecutarAsync(contexto, exigirCsrf: true, async ator =>
    {
        var c = await LerCorpoAsync(contexto);
        var r = await _servico.RestaurarDecisaoAsync(ator, new EntradaRestaurar(
            c.Guid("workspaceId"), c.Guid("projectId"), c.Guid("topicId"),
            c.Inteiro("versionToRestore"), c.Inteiro("expectedVersion"), c.Texto("idempotencyKey")), contexto.RequestAborted);
        return new { topicId = r.TopicoId, proposalId = r.PropostaId, streamVersion = r.VersaoFluxo, status = r.Situacao };
    });

    public Task HandleTimelineAsync(HttpContext contexto) => ExecutarAsync(contexto, exigirCsrf: false, async ator =>
    {
        if (!Guid.TryParse(contexto.Request.Query["workspaceId"], out var ws)
            || !Guid.TryParse(contexto.Request.Query["projectId"], out var proj)
            || !Guid.TryParse(contexto.Request.Query["streamId"], out var streamId))
            throw new ExcecaoDeValidacao("Parâmetros 'workspaceId', 'projectId' e 'streamId' são obrigatórios (UUID).");

        var kind = contexto.Request.Query["kind"].ToString();
        if (string.IsNullOrWhiteSpace(kind))
            kind = "decision";
        int? atVersion = int.TryParse(contexto.Request.Query["atVersion"], out var av) ? av : null;
        var limit = int.TryParse(contexto.Request.Query["limit"], out var lim) ? lim : 50;

        var r = await _servico.ObterLinhaDoTempoAsync(ator, new Core.Consultas.ConsultaLinhaDoTempo(ws, proj, kind, streamId, atVersion, null, limit), contexto.RequestAborted);
        contexto.Response.Headers.CacheControl = "no-store";

        return new
        {
            kind = r.TipoRegistro,
            streamId = r.FluxoId,
            currentStreamVersion = r.VersaoFluxoAtual,
            targetVersion = r.VersaoAlvo,
            events = r.Eventos.Select(e => new
            {
                eventId = e.EventoId,
                streamVersion = e.VersaoFluxo,
                type = e.Tipo,
                recordedAt = e.RegistradoEm,
                authorSubject = e.AutorSujeito,
                clientId = e.ClienteId,
                payloadId = e.CargaId,
                refEventId = e.EventoReferenciaId,
                reason = e.Motivo
            }).ToList(),
            nextBeforeVersion = r.ProximaVersaoAnterior
        };
    });

    public Task HandleReceiptAsync(HttpContext contexto) => ExecutarAsync(contexto, exigirCsrf: false, async ator =>
    {
        if (!Guid.TryParse(contexto.Request.Query["workspaceId"], out var ws)
            || !Guid.TryParse(contexto.Request.Query["projectId"], out var proj)
            || !Guid.TryParse(contexto.Request.Query["queryId"], out var queryId))
            throw new ExcecaoDeValidacao("Parâmetros 'workspaceId', 'projectId' e 'queryId' são obrigatórios (UUID).");

        var c = await _servico.ObterComprovanteAsync(ator, ws, proj, queryId, contexto.RequestAborted);
        contexto.Response.Headers.CacheControl = "no-store";

        return new
        {
            queryId = c.ConsultaId,
            workspaceId = c.EspacoTrabalhoId,
            projectId = c.ProjetoId,
            principalId = c.PrincipalId,
            principalIssuer = c.PrincipalEmissor,
            clientId = c.ClienteId,
            searchMode = c.ModoBusca,
            createdAt = c.CriadoEm,
            digest = c.Digest,
            digestAlgorithm = "sha256",
            integrityVerified = c.IntegridadeVerificada,
            items = c.Itens.Select(i => new
            {
                position = i.Posicao,
                recordType = i.TipoRegistro,
                streamId = i.FluxoId,
                recordId = i.RegistroId,
                recordVersion = i.VersaoRegistro,
                streamVersion = i.VersaoFluxo,
                status = i.Situacao
            }).ToList()
        };
    });

    // Métodos auxiliares
    private async Task<bool> AntiforgeryValidoAsync(HttpContext contexto)
    {
        try
        {
            await _antiforgery.ValidateRequestAsync(contexto);
            return true;
        }
        catch (AntiforgeryValidationException ex)
        {
            _log.LogWarning("Validação CSRF falhou: {Motivo}", ex.Message);
            return false;
        }
    }

    private async Task ExecutarAsync(HttpContext contexto, bool exigirCsrf, Func<Ator, Task<object>> acao)
    {
        try
        {
            var ator = await ObterAtorAsync(contexto);
            if (ator == null)
            {
                await EscreverErroAsync(contexto, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Sessão web necessária.");
                return;
            }

            if (exigirCsrf && !await AntiforgeryValidoAsync(contexto))
            {
                await EscreverErroAsync(contexto, StatusCodes.Status400BadRequest, "CSRF_INVALID", "Token CSRF ausente ou inválido.");
                return;
            }

            var resultado = await acao(ator);
            await contexto.Response.WriteAsJsonAsync(resultado, OpcoesJson, contexto.RequestAborted);
        }
        catch (ExcecaoDeDominio ex)
        {
            var status = ex switch
            {
                ExcecaoDeNaoAutorizado => StatusCodes.Status401Unauthorized,
                ExcecaoDeAcessoNegado => StatusCodes.Status403Forbidden,
                ExcecaoDeNaoEncontrado => StatusCodes.Status404NotFound,
                ExcecaoDeConflitoDeVersao or ExcecaoDePropostaPendente or ExcecaoDeChaveIdempotenciaReutilizada => StatusCodes.Status409Conflict,
                ExcecaoDeTemporariamenteIndisponivel => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest
            };
            await EscreverErroAsync(contexto, status, ex.Codigo, ex.Message);
        }
        catch (JsonException)
        {
            await EscreverErroAsync(contexto, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Corpo JSON inválido.");
        }
    }

    private static Task EscreverErroAsync(HttpContext contexto, int status, string codigo, string mensagem)
    {
        contexto.Response.StatusCode = status;
        return contexto.Response.WriteAsJsonAsync(new { code = codigo, message = mensagem }, OpcoesJson, contexto.RequestAborted);
    }

    private static async Task<CorpoJson> LerCorpoAsync(HttpContext contexto)
    {
        var documento = await JsonDocument.ParseAsync(contexto.Request.Body, cancellationToken: contexto.RequestAborted);
        if (documento.RootElement.ValueKind != JsonValueKind.Object)
            throw new ExcecaoDeValidacao("O corpo deve ser um objeto JSON.");
        return new CorpoJson(documento.RootElement.Clone());
    }

    private sealed class CorpoJson(JsonElement raiz)
    {
        public Guid Guid(string nome) =>
            raiz.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String && System.Guid.TryParse(v.GetString(), out var g)
                ? g
                : throw new ExcecaoDeValidacao($"Campo '{nome}' ausente ou inválido (UUID).");

        public int Inteiro(string nome) =>
            raiz.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i
                : throw new ExcecaoDeValidacao($"Campo '{nome}' ausente ou inválido (inteiro).");

        public string? Texto(string nome) =>
            raiz.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
