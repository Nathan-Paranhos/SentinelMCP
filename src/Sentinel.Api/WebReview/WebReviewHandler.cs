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

    private static string? UrlLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : null;

    // Renderização de páginas HTML
    public async Task RenderReviewPageAsync(HttpContext contexto)
    {
        Ator? ator;
        try
        {
            ator = await ObterAtorAsync(contexto);
        }
        catch (ExcecaoDeAcessoNegado ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await contexto.Response.WriteAsync(ex.Message);
            return;
        }

        if (ator == null)
        {
            var retorno = contexto.Request.Path + contexto.Request.QueryString;
            contexto.Response.Redirect("/auth/login?returnUrl=" + Uri.EscapeDataString(retorno));
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

        if (!LoginDevPermitido(_config, _ambiente))
        {
            contexto.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await contexto.Response.WriteAsync("Login web não configurado. Defina Authentication:Web (OIDC).");
            return;
        }

        var tokens = _antiforgery.GetAndStoreTokens(contexto);
        var enc = HtmlEncoder.Default;
        contexto.Response.ContentType = "text/html; charset=utf-8";
        contexto.Response.Headers.CacheControl = "no-store";
        await contexto.Response.WriteAsync($@"<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
<meta charset=""UTF-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Sentinel — Login de desenvolvimento</title>
<link rel=""stylesheet"" href=""/css/tokens.css""><link rel=""stylesheet"" href=""/css/components.css"">
</head>
<body style=""font-family:var(--font-sans,system-ui);display:flex;min-height:100vh;align-items:center;justify-content:center;background:var(--bg-window,#0b1008);color:var(--text-primary,#eee);"">
<form method=""post"" action=""/auth/dev-login"" style=""display:flex;flex-direction:column;gap:12px;min-width:320px;padding:2rem;border:1px solid var(--border-subtle,#333);border-radius:12px;"">
  <h1 style=""font-size:1.2rem;margin:0;"">Login de desenvolvimento</h1>
  <p style=""font-size:0.85rem;margin:0;opacity:.8;"">Disponível apenas fora de Production. Em ambientes reais o login é feito pelo provedor OIDC.</p>
  <label>Sujeito (sub)<br><input name=""subject"" required maxlength=""200"" value=""maintainer-alice"" style=""width:100%;""></label>
  <input type=""hidden"" name=""{enc.Encode(tokens.FormFieldName)}"" value=""{enc.Encode(tokens.RequestToken ?? string.Empty)}"">
  <input type=""hidden"" name=""returnUrl"" value=""{enc.Encode(retorno)}"">
  <button type=""submit"" class=""btn-macos btn-macos-primary"">Entrar</button>
</form>
</body>
</html>");
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

        return new
        {
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
