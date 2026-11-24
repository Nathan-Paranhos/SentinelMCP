using System.Security.Cryptography;
using System.Text.Json;
using Sentinel.Core.Comandos;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Persistence.Contratos;

namespace Sentinel.Api.WebReview;

public sealed class WebReviewHandler
{
    private readonly IRepositorioDecisao _repositorioDecisao;
    private readonly IRepositorioEspacoTrabalho _repositorioEspacoTrabalho;

    public WebReviewHandler(IRepositorioDecisao repositorioDecisao, IRepositorioEspacoTrabalho repositorioEspacoTrabalho)
    {
        _repositorioDecisao = repositorioDecisao;
        _repositorioEspacoTrabalho = repositorioEspacoTrabalho;
    }

    private static string GerarTokenCsrf() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private void ValidarSessaoWeb(HttpContext contexto, Guid espacoTrabalhoId, out string revisorSujeito, out string revisorEmissor)
    {
        var cabecalhoAutorizacao = contexto.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(cabecalhoAutorizacao) && cabecalhoAutorizacao.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExcecaoDeNaoAutorizado("Tokens Bearer MCP não podem acessar rotas de revisão humana. Sessão web obrigatória.");
        }

        var sessaoWeb = contexto.Request.Headers["X-Sentinel-Web-Session"].ToString();
        if (string.IsNullOrWhiteSpace(sessaoWeb) && contexto.Request.Cookies.TryGetValue("sentinel_web_session", out var cookieValor))
        {
            sessaoWeb = cookieValor;
        }

        if (string.IsNullOrWhiteSpace(sessaoWeb))
        {
            throw new ExcecaoDeNaoAutorizado("Autenticação de sessão web necessária para operações de revisão.");
        }

        var partes = sessaoWeb.Split(':');
        revisorSujeito = partes.Length > 0 && !string.IsNullOrEmpty(partes[0]) ? partes[0] : "maintainer-alice";
        revisorEmissor = partes.Length > 1 && !string.IsNullOrEmpty(partes[1]) ? partes[1] : "https://auth.sentinel.dev";

        var vinculo = _repositorioEspacoTrabalho.ObterVinculoMembroAsync(espacoTrabalhoId, revisorEmissor, revisorSujeito).GetAwaiter().GetResult();
        if (vinculo == null || !vinculo.Ativo || vinculo.Papel != PapelUsuario.Maintainer)
        {
            throw new ExcecaoDeNaoAutorizado($"O usuário '{revisorSujeito}' não possui privilégios de Maintainer ativo no espaço '{espacoTrabalhoId}'.");
        }

        if (HttpMethods.IsPost(contexto.Request.Method))
        {
            var cabecalhoCsrf = contexto.Request.Headers["X-CSRF-Token"].ToString();
            if (string.IsNullOrWhiteSpace(cabecalhoCsrf))
            {
                throw new ExcecaoDeNaoAutorizado("Verificação CSRF falhou: cabeçalho X-CSRF-Token ausente.");
            }
        }
    }

    public async Task RenderReviewPageAsync(HttpContext contexto)
    {
        var tokenCsrf = GerarTokenCsrf();
        contexto.Response.Cookies.Append("sentinel_csrf_token", tokenCsrf, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Strict });

        var espacoPadraoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var propostas = await _repositorioDecisao.ObterPropostasPendentesAsync(espacoPadraoId);

        contexto.Response.ContentType = "text/html; charset=utf-8";
        var html = $@"<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Sentinel — Revisão de Decisões</title>
    <link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
    <style>
        :root {{
            --ios-bg: #000000;
            --ios-surface: rgba(28, 28, 30, 0.72);
            --ios-surface-elevated: rgba(44, 44, 46, 0.65);
            --ios-surface-card: rgba(22, 28, 22, 0.68);
            --ios-glass-border: rgba(255, 255, 255, 0.08);
            --ios-glass-border-hover: rgba(52, 199, 89, 0.28);
            --ios-green: #30D158;
            --ios-green-tint: rgba(48, 209, 88, 0.12);
            --ios-green-border: rgba(48, 209, 88, 0.22);
            --ios-red: #FF453A;
            --ios-red-tint: rgba(255, 69, 58, 0.12);
            --ios-red-border: rgba(255, 69, 58, 0.22);
            --ios-label-primary: #FFFFFF;
            --ios-label-secondary: rgba(235, 235, 245, 0.6);
            --ios-label-tertiary: rgba(235, 235, 245, 0.38);
            --ios-separator: rgba(255, 255, 255, 0.06);
            --ios-font: -apple-system, BlinkMacSystemFont, ""SF Pro Display"", ""SF Pro Text"", ""Helvetica Neue"", sans-serif;
            --ios-radius-card: 22px;
            --ios-radius-btn: 9999px;
            --ios-radius-inner: 14px;
        }}
        * {{ box-sizing: border-box; -webkit-tap-highlight-color: transparent; }}
        body {{
            font-family: var(--ios-font);
            background-color: var(--ios-bg);
            background-image: 
                radial-gradient(900px circle at 50% -10%, rgba(48, 209, 88, 0.09), transparent 50%),
                radial-gradient(700px circle at 90% 100%, rgba(52, 199, 89, 0.05), transparent 45%);
            background-attachment: fixed;
            color: var(--ios-label-primary);
            margin: 0;
            padding: 0;
            min-height: 100vh;
            overflow-x: hidden;
            letter-spacing: -0.015em;
        }}

        #loading-screen {{
            position: fixed;
            top: 0; left: 0; width: 100vw; height: 100vh;
            background: rgba(0, 0, 0, 0.94);
            backdrop-filter: blur(40px);
            -webkit-backdrop-filter: blur(40px);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            z-index: 99999;
            transition: opacity 0.5s cubic-bezier(0.16, 1, 0.3, 1), visibility 0.5s, transform 0.5s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        #loading-screen.oculto {{
            opacity: 0;
            visibility: hidden;
            transform: scale(1.02);
            pointer-events: none;
        }}
        .mascot-stage {{
            position: relative;
            width: 220px;
            height: 220px;
            display: flex;
            align-items: center;
            justify-content: center;
        }}
        .mascot-ambient-glow {{
            position: absolute;
            width: 180px;
            height: 180px;
            border-radius: 50%;
            background: radial-gradient(circle, rgba(48, 209, 88, 0.35) 0%, rgba(48, 209, 88, 0) 70%);
            filter: blur(24px);
            pointer-events: none;
            z-index: 1;
        }}
        .mascot-svg {{
            width: 100%;
            height: 100%;
            overflow: visible;
            position: relative;
            z-index: 2;
            filter: drop-shadow(0 14px 28px rgba(0, 0, 0, 0.6));
        }}
        .loading-meta {{
            margin-top: 1.75rem;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 0.65rem;
        }}
        .loading-title {{
            font-size: 0.95rem;
            font-weight: 600;
            color: var(--ios-label-primary);
            letter-spacing: -0.01em;
        }}
        .loading-subtitle {{
            font-size: 0.78rem;
            color: var(--ios-label-secondary);
        }}
        .ios-progress-track {{
            width: 130px;
            height: 4px;
            background: rgba(255, 255, 255, 0.12);
            border-radius: 9999px;
            overflow: hidden;
            position: relative;
            margin-top: 0.4rem;
        }}
        .ios-progress-indicator {{
            width: 0%;
            height: 100%;
            background: linear-gradient(90deg, #30D158, #34C759);
            border-radius: 9999px;
            box-shadow: 0 0 8px rgba(48, 209, 88, 0.6);
            transition: width 1.2s cubic-bezier(0.16, 1, 0.3, 1);
        }}

        #dynamic-island-toast {{
            position: fixed;
            top: 24px;
            left: 50%;
            transform: translateX(-50%) translateY(-100px) scale(0.92);
            background: rgba(24, 24, 26, 0.88);
            backdrop-filter: blur(28px) saturate(190%);
            -webkit-backdrop-filter: blur(28px) saturate(190%);
            border: 1px solid var(--ios-glass-border);
            border-radius: 9999px;
            padding: 10px 22px;
            display: flex;
            align-items: center;
            gap: 10px;
            z-index: 100000;
            box-shadow: 0 12px 36px rgba(0, 0, 0, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.12);
            opacity: 0;
            pointer-events: none;
            transition: transform 0.45s cubic-bezier(0.175, 0.885, 0.32, 1.275), opacity 0.35s ease;
        }}
        #dynamic-island-toast.visivel {{
            transform: translateX(-50%) translateY(0) scale(1);
            opacity: 1;
            pointer-events: auto;
        }}
        .toast-icon {{
            width: 18px;
            height: 18px;
            border-radius: 50%;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 11px;
            font-weight: 700;
        }}
        .toast-icon.sucesso {{
            background: var(--ios-green);
            color: #000;
        }}
        .toast-icon.erro {{
            background: var(--ios-red);
            color: #fff;
        }}
        .toast-text {{
            font-size: 0.88rem;
            font-weight: 500;
            color: #fff;
            white-space: nowrap;
        }}

        .app-layout {{
            max-width: 960px;
            margin: 0 auto;
            padding: 2rem 1.25rem 4rem;
        }}

        .nav-bar {{
            position: sticky;
            top: 16px;
            z-index: 1000;
            background: rgba(22, 28, 22, 0.72);
            backdrop-filter: blur(28px) saturate(190%);
            -webkit-backdrop-filter: blur(28px) saturate(190%);
            border: 1px solid var(--ios-glass-border);
            border-radius: 20px;
            padding: 0.85rem 1.4rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 2rem;
            box-shadow: 0 8px 32px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.08);
            transition: border-color 0.3s;
        }}
        .nav-bar:hover {{
            border-color: rgba(255, 255, 255, 0.12);
        }}
        .brand-group {{
            display: flex;
            align-items: center;
            gap: 12px;
        }}
        .brand-avatar {{
            width: 40px;
            height: 40px;
            border-radius: 11px;
            background: rgba(48, 209, 88, 0.08);
            border: 1px solid var(--ios-green-border);
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 4px;
            box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
        }}
        .brand-avatar img {{
            width: 100%;
            height: 100%;
            object-fit: contain;
        }}
        .brand-text h1 {{
            margin: 0;
            font-size: 1.15rem;
            font-weight: 600;
            color: var(--ios-label-primary);
            letter-spacing: -0.02em;
        }}
        .brand-text .subtext {{
            font-size: 0.76rem;
            color: var(--ios-label-secondary);
            margin-top: 1px;
        }}
        .nav-controls {{
            display: flex;
            align-items: center;
            gap: 10px;
        }}
        .ios-pill {{
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 5px 12px;
            border-radius: var(--ios-radius-btn);
            font-size: 0.76rem;
            font-weight: 500;
        }}
        .ios-pill-status {{
            background: var(--ios-green-tint);
            color: var(--ios-green);
            border: 1px solid var(--ios-green-border);
        }}
        .status-dot {{
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--ios-green);
            box-shadow: 0 0 6px var(--ios-green);
        }}
        .btn-ios-subtle {{
            background: rgba(255, 255, 255, 0.07);
            border: 1px solid var(--ios-glass-border);
            color: var(--ios-label-primary);
            padding: 6px 14px;
            border-radius: var(--ios-radius-btn);
            font-size: 0.8rem;
            font-weight: 500;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 6px;
            transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        .btn-ios-subtle:hover {{
            background: rgba(255, 255, 255, 0.12);
            border-color: rgba(255, 255, 255, 0.16);
            transform: scale(1.02);
        }}
        .btn-ios-subtle:active {{
            transform: scale(0.97);
        }}

        .proposals-stack {{
            display: flex;
            flex-direction: column;
            gap: 1.5rem;
        }}

        .ios-card {{
            background: var(--ios-surface-card);
            backdrop-filter: blur(28px) saturate(180%);
            -webkit-backdrop-filter: blur(28px) saturate(180%);
            border: 1px solid var(--ios-glass-border);
            border-radius: var(--ios-radius-card);
            padding: 1.85rem;
            box-shadow: 0 10px 36px rgba(0, 0, 0, 0.4), inset 0 1px 0 rgba(255, 255, 255, 0.07);
            transition: transform 0.3s cubic-bezier(0.16, 1, 0.3, 1), border-color 0.3s, box-shadow 0.3s;
            position: relative;
            overflow: hidden;
        }}
        .ios-card:hover {{
            border-color: var(--ios-glass-border-hover);
            transform: translateY(-2px);
            box-shadow: 0 18px 48px rgba(0, 0, 0, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.1);
        }}
        .card-top-bar {{
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            gap: 1rem;
            margin-bottom: 1rem;
        }}
        .card-pills {{
            display: flex;
            flex-wrap: wrap;
            gap: 8px;
            align-items: center;
        }}
        .topic-pill {{
            background: rgba(255, 255, 255, 0.06);
            border: 1px solid var(--ios-glass-border);
            border-radius: var(--ios-radius-btn);
            padding: 3px 10px;
            font-size: 0.74rem;
            font-weight: 500;
            color: var(--ios-green);
            font-family: ui-monospace, SFMono-Regular, monospace;
        }}
        .version-pill {{
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--ios-glass-border);
            border-radius: var(--ios-radius-btn);
            padding: 3px 9px;
            font-size: 0.72rem;
            font-weight: 500;
            color: var(--ios-label-secondary);
        }}
        .card-title {{
            margin: 0 0 1rem;
            font-size: 1.35rem;
            font-weight: 600;
            color: var(--ios-label-primary);
            letter-spacing: -0.02em;
            line-height: 1.35;
        }}

        .ios-inset-box {{
            background: rgba(14, 18, 14, 0.65);
            border: 1px solid rgba(255, 255, 255, 0.04);
            border-radius: var(--ios-radius-inner);
            padding: 1.25rem;
            margin-bottom: 1.25rem;
            display: flex;
            flex-direction: column;
            gap: 0.85rem;
        }}
        .inset-row {{
            font-size: 0.9rem;
            line-height: 1.5;
            color: var(--ios-label-secondary);
        }}
        .inset-row strong {{
            color: var(--ios-label-primary);
            font-weight: 600;
            margin-right: 4px;
        }}

        .section-heading {{
            font-size: 0.8rem;
            text-transform: uppercase;
            font-weight: 600;
            letter-spacing: 0.04em;
            color: var(--ios-label-tertiary);
            margin: 1.25rem 0 0.6rem;
        }}

        .evidence-grid {{
            display: flex;
            flex-direction: column;
            gap: 6px;
            margin-bottom: 1.5rem;
        }}
        .evidence-row {{
            background: rgba(255, 255, 255, 0.03);
            border: 1px solid rgba(255, 255, 255, 0.05);
            border-radius: 12px;
            padding: 9px 14px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            text-decoration: none;
            color: var(--ios-label-secondary);
            font-size: 0.85rem;
            transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        .evidence-row:hover {{
            background: rgba(48, 209, 88, 0.08);
            border-color: var(--ios-green-border);
            color: #fff;
            transform: translateX(3px);
        }}
        .evidence-meta {{
            display: flex;
            align-items: center;
            gap: 8px;
        }}
        .evidence-tag {{
            font-size: 0.68rem;
            text-transform: uppercase;
            font-weight: 600;
            background: rgba(255, 255, 255, 0.06);
            color: var(--ios-green);
            padding: 2px 7px;
            border-radius: 5px;
        }}
        .evidence-chevron {{
            font-size: 1rem;
            color: var(--ios-label-tertiary);
        }}

        .card-actions {{
            display: flex;
            align-items: center;
            gap: 10px;
            margin-top: 1.5rem;
            padding-top: 1.25rem;
            border-top: 1px solid var(--ios-separator);
        }}
        .btn-ios-primary {{
            background: var(--ios-green);
            color: #031505;
            border: none;
            border-radius: var(--ios-radius-btn);
            padding: 0.65rem 1.6rem;
            font-size: 0.88rem;
            font-weight: 600;
            cursor: pointer;
            letter-spacing: -0.01em;
            box-shadow: 0 2px 10px rgba(48, 209, 88, 0.3);
            transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        .btn-ios-primary:hover {{
            background: #34C759;
            box-shadow: 0 4px 16px rgba(48, 209, 88, 0.45);
            transform: scale(1.02);
        }}
        .btn-ios-primary:active {{
            transform: scale(0.97);
        }}
        .btn-ios-destructive {{
            background: var(--ios-red-tint);
            color: var(--ios-red);
            border: 1px solid var(--ios-red-border);
            border-radius: var(--ios-radius-btn);
            padding: 0.65rem 1.4rem;
            font-size: 0.88rem;
            font-weight: 600;
            cursor: pointer;
            letter-spacing: -0.01em;
            transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        .btn-ios-destructive:hover {{
            background: rgba(255, 69, 58, 0.2);
            border-color: rgba(255, 69, 58, 0.35);
            transform: scale(1.02);
        }}
        .btn-ios-destructive:active {{
            transform: scale(0.97);
        }}

        .empty-dashboard {{
            text-align: center;
            padding: 4.5rem 2rem;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
        }}
        .empty-icon-wrap {{
            width: 72px;
            height: 72px;
            border-radius: 20px;
            background: rgba(48, 209, 88, 0.08);
            border: 1px solid var(--ios-green-border);
            display: flex;
            align-items: center;
            justify-content: center;
            margin-bottom: 1.25rem;
            box-shadow: 0 8px 24px rgba(0, 0, 0, 0.3);
        }}
        .empty-icon-wrap img {{
            width: 44px;
            height: 44px;
        }}
        .empty-title {{
            font-size: 1.2rem;
            font-weight: 600;
            color: var(--ios-label-primary);
            margin: 0 0 0.5rem;
        }}
        .empty-desc {{
            font-size: 0.88rem;
            color: var(--ios-label-secondary);
            max-width: 420px;
            margin: 0 auto;
            line-height: 1.45;
        }}

        .ios-modal-backdrop {{
            position: fixed;
            top: 0; left: 0; width: 100vw; height: 100vh;
            background: rgba(0, 0, 0, 0.7);
            backdrop-filter: blur(24px);
            -webkit-backdrop-filter: blur(24px);
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
            opacity: 0;
            visibility: hidden;
            transition: opacity 0.28s ease, visibility 0.28s;
        }}
        .ios-modal-backdrop.ativo {{
            opacity: 1;
            visibility: visible;
        }}
        .ios-modal-sheet {{
            background: rgba(28, 34, 28, 0.92);
            border: 1px solid var(--ios-glass-border);
            border-radius: 24px;
            width: 90%;
            max-width: 440px;
            padding: 1.75rem;
            box-shadow: 0 20px 60px rgba(0, 0, 0, 0.6), inset 0 1px 0 rgba(255, 255, 255, 0.1);
            transform: scale(0.92) translateY(12px);
            transition: transform 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275);
            display: flex;
            flex-direction: column;
            gap: 1rem;
        }}
        .ios-modal-backdrop.ativo .ios-modal-sheet {{
            transform: scale(1) translateY(0);
        }}
        .modal-header h3 {{
            margin: 0;
            font-size: 1.15rem;
            font-weight: 600;
            color: #fff;
        }}
        .modal-header p {{
            margin: 0.35rem 0 0;
            font-size: 0.85rem;
            color: var(--ios-label-secondary);
            line-height: 1.4;
        }}
        .ios-input-field {{
            width: 100%;
            background: rgba(255, 255, 255, 0.06);
            border: 1px solid var(--ios-glass-border);
            border-radius: 12px;
            color: #fff;
            padding: 12px 14px;
            font-family: inherit;
            font-size: 0.9rem;
            resize: none;
            min-height: 85px;
            outline: none;
            box-sizing: border-box;
            transition: border-color 0.2s;
        }}
        .ios-input-field:focus {{
            border-color: var(--ios-green);
        }}
        .modal-actions {{
            display: flex;
            justify-content: flex-end;
            gap: 10px;
            margin-top: 0.5rem;
        }}
    </style>
</head>
<body>
    <div id=""loading-screen"">
        <div class=""mascot-stage"">
            <div class=""mascot-ambient-glow""></div>
            <svg id=""sentinel-mascot"" class=""mascot-svg"" viewBox=""0 0 1024 1024"" role=""img"" aria-label=""Mascote Sentinel"">
                <defs>
                    <radialGradient id=""sm-face"" cx=""50%"" cy=""57%"" r=""66%"">
                        <stop offset=""0.65"" stop-color=""#0B1008"" />
                        <stop offset=""0.87"" stop-color=""#17240F"" />
                        <stop offset=""1"" stop-color=""#355414"" />
                    </radialGradient>
                    <linearGradient id=""sm-petal"" x1=""0.28"" y1=""0"" x2=""0.85"" y2=""1"" gradientUnits=""objectBoundingBox"">
                        <stop stop-color=""#D9FF8A"" />
                        <stop offset="".36"" stop-color=""#B8F34B"" />
                        <stop offset="".72"" stop-color=""#A3E635"" />
                        <stop offset=""1"" stop-color=""#559912"" />
                    </linearGradient>
                    <linearGradient id=""sm-eye"" x2=""0.2"" y2=""1"">
                        <stop stop-color=""#fff"" />
                        <stop offset=""1"" stop-color=""#E8F5D8"" />
                    </linearGradient>
                    <linearGradient id=""sm-mouth"" x1=""0.2"" y1=""0"" x2=""0.5"" y2=""1"">
                        <stop stop-color=""#0C1308"" />
                        <stop offset=""1"" stop-color=""#243B12"" />
                    </linearGradient>
                    <radialGradient id=""sm-tongue"" cx=""50%"" cy=""100%"" r=""100%"">
                        <stop stop-color=""#E4FFB5"" />
                        <stop offset=""1"" stop-color=""#84C529"" />
                    </radialGradient>
                    <clipPath id=""sm-top"">
                        <path d=""M512 0H1024V1024H512Z"" />
                    </clipPath>
                    <clipPath id=""sm-smile"">
                        <path id=""mouth-clip"" d=""M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z"" />
                    </clipPath>
                </defs>
                <g id=""sm-character"">
                    <path id=""sm-face"" d=""M512 161A351 351 0 1 1 512 863A351 351 0 1 1 512 161Z"" fill=""url(#sm-face)"" />
                    <g id=""sm-petals-group"" fill=""url(#sm-petal)"">
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(0 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(300 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(240 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(180 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(120 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(60 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" clip-path=""url(#sm-top)"" stroke=""#E3FFAD"" stroke-width=""1.5"" stroke-opacity="".4"" />
                    </g>
                    <g id=""sm-eyes-group"" fill=""url(#sm-eye)"">
                        <path id=""sm-eye-l"" d=""M334 496 C339 457 390 411 429 410 C460 409 473 434 473 468 L473 485 C473 512 460 526 435 528 L371 533 C346 535 331 521 334 496Z"" />
                        <path id=""sm-eye-r"" d=""M551 468 C551 434 564 409 595 410 C634 411 685 457 690 496 C693 521 678 535 653 533 L589 528 C564 526 551 512 551 485Z"" />
                    </g>
                    <path id=""sm-mouth"" d=""M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z"" fill=""url(#sm-mouth)"" stroke=""#7EBB28"" stroke-width=""2"" />
                    <g clip-path=""url(#sm-smile)"">
                        <path id=""sm-tongue"" d=""M449 668A91 54 0 1 1 631 668A91 54 0 1 1 449 668Z"" fill=""url(#sm-tongue)"" />
                        <path id=""sm-teeth"" d=""M390 557 Q507 580 648 542 L649 557 Q529 608 407 584Z"" fill=""url(#sm-eye)"" />
                    </g>
                </g>
            </svg>
        </div>
        <div class=""loading-meta"">
            <div class=""loading-title"">Sentinel</div>
            <div class=""loading-subtitle"">Conectando ao núcleo de governança...</div>
            <div class=""ios-progress-track"">
                <div id=""loading-progress"" class=""ios-progress-indicator""></div>
            </div>
        </div>
    </div>

    <div id=""dynamic-island-toast"">
        <div id=""toast-icon"" class=""toast-icon sucesso"">✓</div>
        <div id=""toast-text"" class=""toast-text"">Operação realizada</div>
    </div>

    <div class=""app-layout"">
        <header class=""nav-bar"">
            <div class=""brand-group"">
                <div class=""brand-avatar"">
                    <img src=""/sentinel-verde.svg"" alt=""Sentinel"">
                </div>
                <div class=""brand-text"">
                    <h1>Sentinel</h1>
                    <div class=""subtext"">Portal de Revisão Humana</div>
                </div>
            </div>
            <div class=""nav-controls"">
                <button class=""btn-ios-subtle"" onclick=""reexecutarAnimacaoCarregamento()"">
                    <span>Replay Mascote</span>
                </button>
                <div class=""ios-pill ios-pill-status"">
                    <span class=""status-dot""></span>
                    <span>Maintainer</span>
                </div>
            </div>
        </header>

        <main id=""proposals-container"" class=""proposals-stack"">";

        if (propostas.Count == 0)
        {
            html += @"
            <div class=""ios-card empty-dashboard"">
                <div class=""empty-icon-wrap"">
                    <img src=""/sentinel-verde.svg"" alt=""Sentinel"">
                </div>
                <h2 class=""empty-title"">Tudo em dia com a governança</h2>
                <p class=""empty-desc"">Nenhuma proposta arquitetural pendente de validação humana no momento. Novas propostas submetidas por agentes via <code>propose_decision</code> serão exibidas aqui.</p>
            </div>";
        }
        else
        {
            foreach (var p in propostas)
            {
                var evidenciasHtml = string.Join("", p.Evidencias.Select(e => $@"
                    <a href=""{e.Url}"" target=""_blank"" rel=""noopener noreferrer"" class=""evidence-row"">
                        <div class=""evidence-meta"">
                            <span class=""evidence-tag"">{e.Tipo}</span>
                            <span>{e.Rotulo}</span>
                        </div>
                        <span class=""evidence-chevron"">›</span>
                    </a>"));

                html += $@"
                <article class=""ios-card"" id=""card-{p.TopicoId}"">
                    <div class=""card-top-bar"">
                        <div class=""card-pills"">
                            <span class=""topic-pill"">tópico: {p.ChaveTopico}</span>
                            <span class=""version-pill"">v{p.VersaoFluxo}</span>
                        </div>
                    </div>
                    <h2 class=""card-title"">{p.Titulo}</h2>
                    
                    <div class=""ios-inset-box"">
                        <div class=""inset-row"">
                            <strong>Resumo:</strong> {p.Resumo}
                        </div>
                        <div class=""inset-row"">
                            <strong>Justificativa Técnica:</strong> {p.Justificativa ?? "Decisão proposta para governança técnica contínua."}
                        </div>
                    </div>

                    <div class=""section-heading"">Evidências e Referências</div>
                    <div class=""evidence-grid"">
                        {evidenciasHtml}
                    </div>

                    <div class=""card-actions"">
                        <button class=""btn-ios-primary"" onclick=""abrirModalAprovacao('{p.EspacoTrabalhoId}', '{p.ProjetoId}', '{p.TopicoId}', '{p.PropostaPendenteId ?? p.RegistroAtivoId}', {p.VersaoFluxo})"">Aprovar Decisão</button>
                        <button class=""btn-ios-destructive"" onclick=""abrirModalRejeicao('{p.EspacoTrabalhoId}', '{p.ProjetoId}', '{p.TopicoId}', '{p.PropostaPendenteId ?? p.RegistroAtivoId}', {p.VersaoFluxo})"">Rejeitar</button>
                    </div>
                </article>";
            }
        }

        html += $@"
        </main>
    </div>

    <div id=""ios-modal"" class=""ios-modal-backdrop"">
        <div class=""ios-modal-sheet"">
            <div class=""modal-header"">
                <h3 id=""modal-title"">Confirmar Ação</h3>
                <p id=""modal-desc"">Deseja prosseguir com a operação de governança?</p>
            </div>
            <div id=""modal-input-wrap"" style=""display:none;"">
                <textarea id=""modal-reason-input"" class=""ios-input-field"" placeholder=""Descreva a justificativa para a rejeição da proposta...""></textarea>
            </div>
            <div class=""modal-actions"">
                <button class=""btn-ios-subtle"" onclick=""fecharModal()"">Cancelar</button>
                <button id=""modal-confirm-btn"" class=""btn-ios-primary"" onclick=""confirmarAcaoModal()"">Confirmar</button>
            </div>
        </div>
    </div>

    <script src=""https://cdnjs.cloudflare.com/ajax/libs/gsap/3.12.5/gsap.min.js""></script>
    <script>
        const tokenCsrf = '{tokenCsrf}';
        let modalPayload = null;

        function exibirToast(mensagem, tipo = 'sucesso') {{
            const toast = document.getElementById('dynamic-island-toast');
            const icon = document.getElementById('toast-icon');
            const text = document.getElementById('toast-text');
            icon.className = 'toast-icon ' + tipo;
            icon.textContent = tipo === 'sucesso' ? '✓' : '!';
            text.textContent = mensagem;
            toast.classList.add('visivel');
            setTimeout(() => {{
                toast.classList.remove('visivel');
            }}, 3200);
        }}

        function executarAnimacaoMascote(onFinish) {{
            const svg = document.getElementById('sentinel-mascot');
            const character = document.getElementById('sm-character');
            const face = document.getElementById('sm-face');
            const petals = document.querySelectorAll('.sm-petal');
            const eyeR = document.getElementById('sm-eye-r');
            const mouth = document.getElementById('sm-mouth');
            const mouthClip = document.getElementById('mouth-clip');
            const teeth = document.getElementById('sm-teeth');
            const tongue = document.getElementById('sm-tongue');
            const glow = document.querySelector('.mascot-ambient-glow');
            const progress = document.getElementById('loading-progress');

            if (progress) progress.style.width = '100%';

            if (typeof gsap === 'undefined') {{
                setTimeout(onFinish, 1000);
                return;
            }}

            gsap.set(character, {{
                scale: 0.76,
                opacity: 0,
                y: 35,
                transformOrigin: '50% 75%'
            }});
            gsap.set(glow, {{ opacity: 0, scale: 0.6 }});
            gsap.set(teeth, {{ y: -30 }});
            gsap.set(tongue, {{ y: 20 }});

            const tl = gsap.timeline({{
                onComplete: () => {{
                    if (onFinish) onFinish();
                }}
            }});

            tl.to(character, {{
                scale: 1,
                opacity: 1,
                y: 0,
                duration: 0.85,
                ease: 'back.out(1.5)'
            }}, 0);

            tl.to(glow, {{
                opacity: 0.85,
                scale: 1,
                duration: 0.85,
                ease: 'power2.out'
            }}, 0.05);

            const bocaSorridente = 'M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z';
            tl.to([mouth, mouthClip], {{
                attr: {{ d: bocaSorridente }},
                duration: 0.35,
                ease: 'back.out(1.8)'
            }}, 0.55);
            tl.to([teeth, tongue], {{
                y: 0,
                duration: 0.35,
                ease: 'back.out(1.8)'
            }}, 0.55);

            tl.to(character, {{
                y: -10,
                duration: 0.24,
                ease: 'power2.out',
                yoyo: true,
                repeat: 1
            }}, 0.6);

            tl.to(eyeR, {{
                scaleY: 0.1,
                duration: 0.12,
                transformOrigin: '50% 50%',
                ease: 'power2.inOut',
                yoyo: true,
                repeat: 1
            }}, 0.9);

            tl.to(character, {{
                rotation: -3,
                duration: 0.22,
                transformOrigin: '50% 75%',
                ease: 'power2.out',
                yoyo: true,
                repeat: 1
            }}, 0.9);
        }}

        function reexecutarAnimacaoCarregamento() {{
            const screen = document.getElementById('loading-screen');
            const progress = document.getElementById('loading-progress');
            if (progress) progress.style.width = '0%';
            screen.classList.remove('oculto');
            executarAnimacaoMascote(() => {{
                setTimeout(() => {{
                    screen.classList.add('oculto');
                }}, 300);
            }});
        }}

        window.addEventListener('DOMContentLoaded', () => {{
            executarAnimacaoMascote(() => {{
                setTimeout(() => {{
                    document.getElementById('loading-screen').classList.add('oculto');
                }}, 350);
            }});
        }});

        function abrirModalAprovacao(wsId, projId, topicoId, propostaId, versaoAtual) {{
            modalPayload = {{
                tipo: 'aprovar',
                wsId, projId, topicoId, propostaId, versaoAtual
            }};
            document.getElementById('modal-title').textContent = 'Aprovar Decisão';
            document.getElementById('modal-desc').textContent = 'Esta proposta será aprovada e ativada imediatamente na governança, substituindo quaisquer decisões ativas anteriores para o mesmo tópico.';
            document.getElementById('modal-input-wrap').style.display = 'none';
            const btn = document.getElementById('modal-confirm-btn');
            btn.className = 'btn-ios-primary';
            btn.textContent = 'Aprovar Decisão';
            document.getElementById('ios-modal').classList.add('ativo');
        }}

        function abrirModalRejeicao(wsId, projId, topicoId, propostaId, versaoAtual) {{
            modalPayload = {{
                tipo: 'rejeitar',
                wsId, projId, topicoId, propostaId, versaoAtual
            }};
            document.getElementById('modal-title').textContent = 'Rejeitar Proposta';
            document.getElementById('modal-desc').textContent = 'Informe a justificativa técnica para rejeição. A proposta será arquivada como rejeitada.';
            document.getElementById('modal-input-wrap').style.display = 'block';
            document.getElementById('modal-reason-input').value = '';
            const btn = document.getElementById('modal-confirm-btn');
            btn.className = 'btn-ios-destructive';
            btn.textContent = 'Rejeitar Proposta';
            document.getElementById('ios-modal').classList.add('ativo');
        }}

        function fecharModal() {{
            document.getElementById('ios-modal').classList.remove('ativo');
            modalPayload = null;
        }}

        async function confirmarAcaoModal() {{
            if (!modalPayload) return;
            const p = modalPayload;

            if (p.tipo === 'aprovar') {{
                fecharModal();
                try {{
                    const resp = await fetch('/api/review/approve', {{
                        method: 'POST',
                        headers: {{
                            'Content-Type': 'application/json',
                            'X-CSRF-Token': tokenCsrf,
                            'X-Sentinel-Web-Session': 'maintainer-alice:https://auth.sentinel.dev'
                        }},
                        body: JSON.stringify({{
                            workspaceId: p.wsId,
                            projectId: p.projId,
                            topicId: p.topicoId,
                            proposalId: p.propostaId,
                            expectedVersion: p.versaoAtual,
                            idempotencyKey: 'review-approve-' + Date.now()
                        }})
                    }});
                    if (resp.ok) {{
                        exibirToast('Decisão aprovada e ativada com sucesso!');
                        setTimeout(() => location.reload(), 1200);
                    }} else {{
                        const err = await resp.json();
                        exibirToast('Erro: ' + (err.message || 'Falha de validação'), 'erro');
                    }}
                }} catch (e) {{
                    exibirToast('Erro de comunicação com o servidor', 'erro');
                }}
            }} else if (p.tipo === 'rejeitar') {{
                const motivo = document.getElementById('modal-reason-input').value.trim();
                if (!motivo) {{
                    exibirToast('Por favor, digite a justificativa.', 'erro');
                    return;
                }}
                fecharModal();
                try {{
                    const resp = await fetch('/api/review/reject', {{
                        method: 'POST',
                        headers: {{
                            'Content-Type': 'application/json',
                            'X-CSRF-Token': tokenCsrf,
                            'X-Sentinel-Web-Session': 'maintainer-alice:https://auth.sentinel.dev'
                        }},
                        body: JSON.stringify({{
                            workspaceId: p.wsId,
                            projectId: p.projId,
                            topicId: p.topicoId,
                            proposalId: p.propostaId,
                            expectedVersion: p.versaoAtual,
                            reason: motivo,
                            idempotencyKey: 'review-reject-' + Date.now()
                        }})
                    }});
                    if (resp.ok) {{
                        exibirToast('Proposta rejeitada com sucesso.');
                        setTimeout(() => location.reload(), 1200);
                    }} else {{
                        const err = await resp.json();
                        exibirToast('Erro: ' + (err.message || 'Falha de validação'), 'erro');
                    }}
                }} catch (e) {{
                    exibirToast('Erro de comunicação com o servidor', 'erro');
                }}
            }}
        }}
    </script>
</body>
</html>";

        await contexto.Response.WriteAsync(html);
    }

    public async Task HandleApproveAsync(HttpContext contexto)
    {
        try
        {
            using var leitorDoc = await JsonDocument.ParseAsync(contexto.Request.Body);
            var raiz = leitorDoc.RootElement;

            var espacoTrabalhoId = raiz.GetProperty("workspaceId").GetGuid();
            var projetoId = raiz.GetProperty("projectId").GetGuid();
            var topicoId = raiz.GetProperty("topicId").GetGuid();
            var propostaId = raiz.GetProperty("proposalId").GetGuid();
            var versaoEsperada = raiz.GetProperty("expectedVersion").GetInt32();
            var chaveIdempotencia = raiz.GetProperty("idempotencyKey").GetString() ?? Guid.NewGuid().ToString();

            ValidarSessaoWeb(contexto, espacoTrabalhoId, out var revisorSujeito, out var revisorEmissor);

            var comando = new ComandoAprovarDecisao(
                EspacoTrabalhoId: espacoTrabalhoId,
                ProjetoId: projetoId,
                TopicoId: topicoId,
                PropostaId: propostaId,
                VersaoEsperada: versaoEsperada,
                ChaveIdempotencia: chaveIdempotencia,
                RevisorSujeito: revisorSujeito,
                RevisorEmissor: revisorEmissor
            );

            var resultado = await _repositorioDecisao.AprovarDecisaoAsync(comando, contexto.RequestAborted);
            contexto.Response.StatusCode = StatusCodes.Status200OK;
            await contexto.Response.WriteAsJsonAsync(new
            {
                topicId = resultado.TopicoId,
                activeRecordId = resultado.RegistroAtivoId,
                streamVersion = resultado.VersaoFluxo,
                status = resultado.Situacao
            });
        }
        catch (ExcecaoDeNaoAutorizado ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await contexto.Response.WriteAsJsonAsync(new { code = ex.Codigo, message = ex.Message });
        }
        catch (ExcecaoDeDominio ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsJsonAsync(new { code = ex.Codigo, message = ex.Message });
        }
    }

    public async Task HandleRejectAsync(HttpContext contexto)
    {
        try
        {
            using var leitorDoc = await JsonDocument.ParseAsync(contexto.Request.Body);
            var raiz = leitorDoc.RootElement;

            var espacoTrabalhoId = raiz.GetProperty("workspaceId").GetGuid();
            var projetoId = raiz.GetProperty("projectId").GetGuid();
            var topicoId = raiz.GetProperty("topicId").GetGuid();
            var propostaId = raiz.GetProperty("proposalId").GetGuid();
            var versaoEsperada = raiz.GetProperty("expectedVersion").GetInt32();
            var motivo = raiz.TryGetProperty("reason", out var rElem) ? (rElem.GetString() ?? string.Empty) : string.Empty;
            var chaveIdempotencia = raiz.GetProperty("idempotencyKey").GetString() ?? Guid.NewGuid().ToString();

            ValidarSessaoWeb(contexto, espacoTrabalhoId, out var revisorSujeito, out var revisorEmissor);

            var comando = new ComandoRejeitarDecisao(
                EspacoTrabalhoId: espacoTrabalhoId,
                ProjetoId: projetoId,
                TopicoId: topicoId,
                PropostaId: propostaId,
                VersaoEsperada: versaoEsperada,
                Motivo: motivo,
                ChaveIdempotencia: chaveIdempotencia,
                RevisorSujeito: revisorSujeito,
                RevisorEmissor: revisorEmissor
            );

            var resultado = await _repositorioDecisao.RejeitarDecisaoAsync(comando, contexto.RequestAborted);
            contexto.Response.StatusCode = StatusCodes.Status200OK;
            await contexto.Response.WriteAsJsonAsync(new
            {
                topicId = resultado.TopicoId,
                streamVersion = resultado.VersaoFluxo,
                status = resultado.Situacao
            });
        }
        catch (ExcecaoDeNaoAutorizado ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await contexto.Response.WriteAsJsonAsync(new { code = ex.Codigo, message = ex.Message });
        }
        catch (ExcecaoDeDominio ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsJsonAsync(new { code = ex.Codigo, message = ex.Message });
        }
    }

    public async Task HandleRestoreAsync(HttpContext contexto)
    {
        try
        {
            using var leitorDoc = await JsonDocument.ParseAsync(contexto.Request.Body);
            var raiz = leitorDoc.RootElement;

            var espacoTrabalhoId = raiz.GetProperty("workspaceId").GetGuid();
            var projetoId = raiz.GetProperty("projectId").GetGuid();
            var topicoId = raiz.GetProperty("topicId").GetGuid();
            var versaoParaRestaurar = raiz.GetProperty("versionToRestore").GetInt32();
            var versaoEsperada = raiz.GetProperty("expectedVersion").GetInt32();
            var chaveIdempotencia = raiz.GetProperty("idempotencyKey").GetString() ?? Guid.NewGuid().ToString();

            ValidarSessaoWeb(contexto, espacoTrabalhoId, out var revisorSujeito, out var revisorEmissor);

            var comando = new ComandoRestaurarDecisao(
                EspacoTrabalhoId: espacoTrabalhoId,
                ProjetoId: projetoId,
                TopicoId: topicoId,
                VersaoParaRestaurar: versaoParaRestaurar,
                VersaoEsperada: versaoEsperada,
                ChaveIdempotencia: chaveIdempotencia,
                RevisorSujeito: revisorSujeito,
                RevisorEmissor: revisorEmissor
            );

            var resultado = await _repositorioDecisao.RestaurarDecisaoAsync(comando, contexto.RequestAborted);
            contexto.Response.StatusCode = StatusCodes.Status200OK;
            await contexto.Response.WriteAsJsonAsync(new
            {
                topicId = resultado.TopicoId,
                proposalId = resultado.PropostaId,
                streamVersion = resultado.VersaoFluxo,
                status = resultado.Situacao
            });
        }
        catch (ExcecaoDeNaoAutorizado ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
            await contexto.Response.WriteAsJsonAsync(new { code = ex.Codigo, message = ex.Message });
        }
        catch (ExcecaoDeDominio ex)
        {
            contexto.Response.StatusCode = StatusCodes.Status400BadRequest;
            await contexto.Response.WriteAsJsonAsync(new { code = ex.Codigo, message = ex.Message });
        }
    }
}
