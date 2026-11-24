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
    private readonly IRepositorioIncidente _repositorioIncidente;
    private readonly IRepositorioEspacoTrabalho _repositorioEspacoTrabalho;

    public WebReviewHandler(
        IRepositorioDecisao repositorioDecisao,
        IRepositorioIncidente repositorioIncidente,
        IRepositorioEspacoTrabalho repositorioEspacoTrabalho)
    {
        _repositorioDecisao = repositorioDecisao;
        _repositorioIncidente = repositorioIncidente;
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
        var decisoesVigentes = await _repositorioDecisao.ObterDecisoesVigentesAsync(espacoPadraoId);
        var incidentes = await _repositorioIncidente.ObterIncidentesAsync(espacoPadraoId);

        contexto.Response.ContentType = "text/html; charset=utf-8";
        var html = $@"<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Sentinel — Governança e Decisões de Arquitetura</title>
    <link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
    <style>
        :root {{
            --base-bg: #09090b;
            --surface-card: #121215;
            --surface-elevated: #18181c;
            --surface-inset: #0e0e11;
            --border-subtle: rgba(255, 255, 255, 0.08);
            --border-hover: rgba(255, 255, 255, 0.16);
            --brand-emerald: #22c55e;
            --brand-emerald-dim: #16a34a;
            --brand-emerald-tint: rgba(34, 197, 94, 0.12);
            --brand-emerald-border: rgba(34, 197, 94, 0.25);
            --status-danger: #ef4444;
            --status-danger-tint: rgba(239, 68, 68, 0.12);
            --status-danger-border: rgba(239, 68, 68, 0.25);
            --status-warning: #f59e0b;
            --status-warning-tint: rgba(245, 158, 11, 0.12);
            --status-warning-border: rgba(245, 158, 11, 0.25);
            --text-primary: rgba(255, 255, 255, 0.92);
            --text-secondary: rgba(255, 255, 255, 0.60);
            --text-tertiary: rgba(255, 255, 255, 0.40);
            --font-stack: -apple-system, BlinkMacSystemFont, ""Geist"", ""Inter"", ""SF Pro Text"", system-ui, sans-serif;
            --radius-card: 16px;
            --radius-pill: 9999px;
            --radius-inset: 12px;
        }}
        * {{ box-sizing: border-box; -webkit-tap-highlight-color: transparent; }}
        body {{
            font-family: var(--font-stack);
            background-color: var(--base-bg);
            background-image: 
                radial-gradient(1000px circle at 50% 0%, rgba(34, 197, 94, 0.06), transparent 55%),
                radial-gradient(800px circle at 90% 100%, rgba(34, 197, 94, 0.03), transparent 50%);
            background-attachment: fixed;
            color: var(--text-primary);
            margin: 0;
            padding: 0;
            min-height: 100vh;
            overflow-x: hidden;
            letter-spacing: -0.012em;
        }}

        #loading-screen {{
            position: fixed;
            top: 0; left: 0; width: 100vw; height: 100vh;
            background: rgba(9, 9, 11, 0.96);
            backdrop-filter: blur(28px);
            -webkit-backdrop-filter: blur(28px);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            z-index: 99999;
            perspective: 1200px;
            transition: opacity 0.45s cubic-bezier(0.16, 1, 0.3, 1), visibility 0.45s, transform 0.45s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        #loading-screen.oculto {{
            opacity: 0;
            visibility: hidden;
            transform: scale(1.02);
            pointer-events: none;
        }}
        .mascot-3d-stage {{
            position: relative;
            width: 200px;
            height: 200px;
            display: flex;
            align-items: center;
            justify-content: center;
            perspective: 1000px;
            transform-style: preserve-3d;
        }}
        .mascot-shadow-3d {{
            position: absolute;
            bottom: -8px;
            width: 140px;
            height: 24px;
            border-radius: 50%;
            background: radial-gradient(ellipse at center, rgba(34, 197, 94, 0.4) 0%, rgba(0, 0, 0, 0.7) 50%, transparent 75%);
            filter: blur(10px);
            pointer-events: none;
            transform: rotateX(75deg);
        }}
        .mascot-svg-3d {{
            width: 100%;
            height: 100%;
            overflow: visible;
            transform-style: preserve-3d;
            filter: drop-shadow(0 16px 30px rgba(0, 0, 0, 0.65));
        }}
        .loading-details {{
            margin-top: 1.5rem;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 0.4rem;
        }}
        .loading-title {{
            font-size: 0.92rem;
            font-weight: 600;
            color: var(--text-primary);
            letter-spacing: -0.015em;
        }}
        .loading-subtitle {{
            font-size: 0.78rem;
            color: var(--text-tertiary);
        }}
        .progress-track-linear {{
            width: 140px;
            height: 3px;
            background: rgba(255, 255, 255, 0.08);
            border-radius: var(--radius-pill);
            overflow: hidden;
            position: relative;
            margin-top: 0.4rem;
        }}
        .progress-fill-linear {{
            width: 0%;
            height: 100%;
            background: linear-gradient(90deg, #16a34a, #22c55e);
            border-radius: var(--radius-pill);
            box-shadow: 0 0 10px rgba(34, 197, 94, 0.5);
            transition: width 1s cubic-bezier(0.16, 1, 0.3, 1);
        }}

        #toast-banner {{
            position: fixed;
            top: 20px;
            left: 50%;
            transform: translateX(-50%) translateY(-80px);
            background: rgba(18, 18, 21, 0.92);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-pill);
            padding: 8px 18px;
            display: flex;
            align-items: center;
            gap: 8px;
            z-index: 100000;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.1);
            opacity: 0;
            pointer-events: none;
            transition: transform 0.35s cubic-bezier(0.175, 0.885, 0.32, 1.275), opacity 0.25s ease;
        }}
        #toast-banner.ativo {{
            transform: translateX(-50%) translateY(0);
            opacity: 1;
            pointer-events: auto;
        }}
        .toast-pill-dot {{
            width: 7px;
            height: 7px;
            border-radius: 50%;
            background: var(--brand-emerald);
            box-shadow: 0 0 6px var(--brand-emerald);
        }}
        .toast-pill-dot.erro {{
            background: var(--status-danger);
            box-shadow: 0 0 6px var(--status-danger);
        }}
        .toast-label {{
            font-size: 0.85rem;
            font-weight: 500;
            color: var(--text-primary);
        }}

        .layout-container {{
            max-width: 1040px;
            margin: 0 auto;
            padding: 2rem 1.5rem 5rem;
        }}

        .top-navbar {{
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding: 1rem 0 1.5rem;
            border-bottom: 1px solid var(--border-subtle);
            margin-bottom: 2rem;
        }}
        .brand-section {{
            display: flex;
            align-items: center;
            gap: 12px;
        }}
        .brand-avatar-box {{
            width: 38px;
            height: 38px;
            border-radius: 10px;
            background: rgba(34, 197, 94, 0.08);
            border: 1px solid var(--brand-emerald-border);
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 3px;
        }}
        .brand-avatar-box img {{
            width: 100%;
            height: 100%;
            object-fit: contain;
        }}
        .brand-title-wrap h1 {{
            margin: 0;
            font-size: 1.15rem;
            font-weight: 600;
            letter-spacing: -0.025em;
            color: var(--text-primary);
        }}
        .brand-title-wrap .caption {{
            font-size: 0.76rem;
            color: var(--text-tertiary);
            margin-top: 1px;
        }}
        .navbar-status {{
            display: flex;
            align-items: center;
            gap: 10px;
        }}
        .session-badge {{
            display: inline-flex;
            align-items: center;
            gap: 6px;
            background: var(--brand-emerald-tint);
            border: 1px solid var(--brand-emerald-border);
            color: var(--brand-emerald);
            padding: 4px 12px;
            border-radius: var(--radius-pill);
            font-size: 0.76rem;
            font-weight: 500;
        }}
        .session-badge .live-dot {{
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--brand-emerald);
            box-shadow: 0 0 6px var(--brand-emerald);
        }}

        .metrics-grid {{
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
            gap: 1rem;
            margin-bottom: 2rem;
        }}
        .metric-card {{
            background: var(--surface-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-card);
            padding: 1.25rem 1.4rem;
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.06), 0 2px 8px rgba(0, 0, 0, 0.3);
            transition: border-color 0.2s, transform 0.2s;
        }}
        .metric-card:hover {{
            border-color: var(--border-hover);
            transform: translateY(-1px);
        }}
        .metric-label {{
            font-size: 0.76rem;
            font-weight: 500;
            text-transform: uppercase;
            letter-spacing: 0.03em;
            color: var(--text-tertiary);
            margin-bottom: 0.4rem;
        }}
        .metric-value-row {{
            display: flex;
            align-items: baseline;
            justify-content: space-between;
        }}
        .metric-number {{
            font-size: 1.6rem;
            font-weight: 600;
            letter-spacing: -0.03em;
            color: var(--text-primary);
            font-variant-numeric: tabular-nums;
        }}
        .metric-tag {{
            font-size: 0.72rem;
            font-weight: 500;
            padding: 2px 8px;
            border-radius: var(--radius-pill);
        }}
        .metric-tag.emerald {{
            background: var(--brand-emerald-tint);
            color: var(--brand-emerald);
            border: 1px solid var(--brand-emerald-border);
        }}
        .metric-tag.amber {{
            background: var(--status-warning-tint);
            color: var(--status-warning);
            border: 1px solid var(--status-warning-border);
        }}
        .metric-tag.neutral {{
            background: rgba(255, 255, 255, 0.06);
            color: var(--text-secondary);
            border: 1px solid var(--border-subtle);
        }}

        .segmented-tabs {{
            display: flex;
            align-items: center;
            background: var(--surface-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-pill);
            padding: 4px;
            gap: 4px;
            margin-bottom: 1.75rem;
            width: fit-content;
        }}
        .tab-btn {{
            background: transparent;
            border: none;
            color: var(--text-secondary);
            padding: 7px 18px;
            border-radius: var(--radius-pill);
            font-size: 0.82rem;
            font-weight: 500;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 7px;
            transition: all 0.18s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        .tab-btn:hover {{
            color: var(--text-primary);
        }}
        .tab-btn.active {{
            background: rgba(255, 255, 255, 0.08);
            color: var(--text-primary);
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.1), 0 2px 6px rgba(0, 0, 0, 0.3);
        }}
        .tab-count {{
            background: rgba(255, 255, 255, 0.08);
            font-size: 0.68rem;
            font-weight: 600;
            padding: 1px 6px;
            border-radius: var(--radius-pill);
            font-variant-numeric: tabular-nums;
        }}
        .tab-btn.active .tab-count {{
            background: var(--brand-emerald-tint);
            color: var(--brand-emerald);
        }}

        .tab-panel {{
            display: none;
            flex-direction: column;
            gap: 1.25rem;
        }}
        .tab-panel.active {{
            display: flex;
        }}

        .data-card {{
            background: var(--surface-card);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-card);
            padding: 1.6rem;
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.06), 0 4px 16px rgba(0, 0, 0, 0.3);
            transition: border-color 0.2s, box-shadow 0.2s;
        }}
        .data-card:hover {{
            border-color: var(--border-hover);
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.1), 0 6px 24px rgba(0, 0, 0, 0.4);
        }}
        .data-card-header {{
            display: flex;
            justify-content: space-between;
            align-items: flex-start;
            gap: 1rem;
            margin-bottom: 0.85rem;
        }}
        .data-card-tags {{
            display: flex;
            flex-wrap: wrap;
            gap: 6px;
            align-items: center;
        }}
        .tag-pill {{
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--border-subtle);
            border-radius: var(--radius-pill);
            padding: 2px 9px;
            font-size: 0.72rem;
            font-weight: 500;
            color: var(--text-secondary);
        }}
        .tag-pill.code {{
            font-family: ui-monospace, SFMono-Regular, monospace;
            color: var(--brand-emerald);
        }}
        .data-card-title {{
            margin: 0 0 0.85rem;
            font-size: 1.2rem;
            font-weight: 600;
            letter-spacing: -0.02em;
            color: var(--text-primary);
            line-height: 1.35;
        }}

        .inset-group {{
            background: var(--surface-inset);
            border: 1px solid rgba(255, 255, 255, 0.04);
            border-radius: var(--radius-inset);
            padding: 1.15rem;
            display: flex;
            flex-direction: column;
            gap: 0.75rem;
            margin-bottom: 1.25rem;
        }}
        .inset-text {{
            font-size: 0.88rem;
            line-height: 1.5;
            color: var(--text-secondary);
        }}
        .inset-text strong {{
            color: var(--text-primary);
            font-weight: 600;
        }}

        .evidence-stack {{
            display: flex;
            flex-direction: column;
            gap: 5px;
            margin-bottom: 1.4rem;
        }}
        .evidence-item {{
            background: rgba(255, 255, 255, 0.03);
            border: 1px solid rgba(255, 255, 255, 0.05);
            border-radius: 9px;
            padding: 8px 12px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            text-decoration: none;
            color: var(--text-secondary);
            font-size: 0.82rem;
            transition: all 0.16s ease-out;
        }}
        .evidence-item:hover {{
            background: rgba(34, 197, 94, 0.08);
            border-color: var(--brand-emerald-border);
            color: var(--text-primary);
            transform: translateX(2px);
        }}
        .evidence-left {{
            display: flex;
            align-items: center;
            gap: 8px;
        }}
        .evidence-badge {{
            font-size: 0.66rem;
            text-transform: uppercase;
            font-weight: 600;
            background: rgba(255, 255, 255, 0.06);
            color: var(--brand-emerald);
            padding: 2px 6px;
            border-radius: 4px;
        }}
        .evidence-arrow {{
            color: var(--text-tertiary);
            font-size: 0.95rem;
        }}

        .actions-toolbar {{
            display: flex;
            align-items: center;
            gap: 8px;
            padding-top: 1.15rem;
            border-top: 1px solid var(--border-subtle);
        }}
        .btn-primary-action {{
            background: var(--brand-emerald);
            color: #031405;
            border: none;
            border-radius: var(--radius-pill);
            padding: 0.55rem 1.4rem;
            font-size: 0.85rem;
            font-weight: 600;
            cursor: pointer;
            letter-spacing: -0.01em;
            transition: all 0.16s ease-out;
        }}
        .btn-primary-action:hover {{
            background: #27d967;
            box-shadow: 0 4px 12px rgba(34, 197, 94, 0.35);
            transform: scale(1.015);
        }}
        .btn-primary-action:active {{
            transform: scale(0.98);
        }}
        .btn-danger-action {{
            background: var(--status-danger-tint);
            color: #f87171;
            border: 1px solid var(--status-danger-border);
            border-radius: var(--radius-pill);
            padding: 0.55rem 1.25rem;
            font-size: 0.85rem;
            font-weight: 500;
            cursor: pointer;
            transition: all 0.16s ease-out;
        }}
        .btn-danger-action:hover {{
            background: rgba(239, 68, 68, 0.2);
            border-color: rgba(239, 68, 68, 0.4);
            transform: scale(1.015);
        }}
        .btn-danger-action:active {{
            transform: scale(0.98);
        }}

        .empty-box {{
            text-align: center;
            padding: 3.5rem 1.5rem;
            border: 1px dashed var(--border-subtle);
            border-radius: var(--radius-card);
            color: var(--text-tertiary);
        }}
        .empty-box h3 {{
            color: var(--text-secondary);
            font-size: 1.05rem;
            font-weight: 500;
            margin: 0 0 0.4rem;
        }}
        .empty-box p {{
            font-size: 0.85rem;
            margin: 0;
            max-width: 400px;
            margin: 0 auto;
        }}

        .modal-overlay {{
            position: fixed;
            top: 0; left: 0; width: 100vw; height: 100vh;
            background: rgba(0, 0, 0, 0.7);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
            opacity: 0;
            visibility: hidden;
            transition: opacity 0.24s ease, visibility 0.24s;
        }}
        .modal-overlay.ativo {{
            opacity: 1;
            visibility: visible;
        }}
        .modal-dialog {{
            background: #151518;
            border: 1px solid var(--border-subtle);
            border-radius: 20px;
            width: 90%;
            max-width: 440px;
            padding: 1.75rem;
            box-shadow: 0 24px 60px rgba(0, 0, 0, 0.6), inset 0 1px 0 rgba(255, 255, 255, 0.1);
            transform: scale(0.94);
            transition: transform 0.24s cubic-bezier(0.175, 0.885, 0.32, 1.275);
            display: flex;
            flex-direction: column;
            gap: 1rem;
        }}
        .modal-overlay.ativo .modal-dialog {{
            transform: scale(1);
        }}
        .dialog-header h3 {{
            margin: 0;
            font-size: 1.15rem;
            font-weight: 600;
            color: var(--text-primary);
        }}
        .dialog-header p {{
            margin: 0.35rem 0 0;
            font-size: 0.85rem;
            color: var(--text-secondary);
            line-height: 1.4;
        }}
        .dialog-input {{
            width: 100%;
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--border-subtle);
            border-radius: 10px;
            color: #fff;
            padding: 10px 12px;
            font-family: inherit;
            font-size: 0.88rem;
            resize: none;
            min-height: 80px;
            outline: none;
            box-sizing: border-box;
            transition: border-color 0.2s;
        }}
        .dialog-input:focus {{
            border-color: var(--brand-emerald);
        }}
        .dialog-actions {{
            display: flex;
            justify-content: flex-end;
            gap: 8px;
            margin-top: 0.4rem;
        }}
        .btn-dialog-cancel {{
            background: rgba(255, 255, 255, 0.06);
            border: 1px solid var(--border-subtle);
            color: var(--text-primary);
            padding: 6px 14px;
            border-radius: var(--radius-pill);
            font-size: 0.82rem;
            font-weight: 500;
            cursor: pointer;
        }}
    </style>
</head>
<body>
    <div id=""loading-screen"">
        <div class=""mascot-3d-stage"">
            <div class=""mascot-shadow-3d"" id=""mascot-shadow""></div>
            <svg id=""sentinel-mascot"" class=""mascot-svg-3d"" viewBox=""0 0 1024 1024"" role=""img"" aria-label=""Mascote Sentinel 3D"">
                <defs>
                    <radialGradient id=""sm-3d-face"" cx=""38%"" cy=""36%"" r=""65%"">
                        <stop offset=""0%"" stop-color=""#416518"" />
                        <stop offset=""35%"" stop-color=""#223a0d"" />
                        <stop offset=""72%"" stop-color=""#0f1908"" />
                        <stop offset=""92%"" stop-color=""#070d04"" />
                        <stop offset=""100%"" stop-color=""#233f0e"" />
                    </radialGradient>
                    <linearGradient id=""sm-3d-petal"" x1=""0.2"" y1=""0"" x2=""0.8"" y2=""1"">
                        <stop offset=""0%"" stop-color=""#f0ffc7"" />
                        <stop offset=""22%"" stop-color=""#b8f34b"" />
                        <stop offset=""65%"" stop-color=""#7ec722"" />
                        <stop offset=""100%"" stop-color=""#3b690d"" />
                    </linearGradient>
                    <radialGradient id=""sm-3d-eye"" cx=""42%"" cy=""35%"" r=""55%"">
                        <stop offset=""0%"" stop-color=""#ffffff"" />
                        <stop offset=""75%"" stop-color=""#e6f5d8"" />
                        <stop offset=""100%"" stop-color=""#a6caa0"" />
                    </radialGradient>
                    <linearGradient id=""sm-3d-mouth"" x1=""0.2"" y1=""0"" x2=""0.5"" y2=""1"">
                        <stop offset=""0%"" stop-color=""#080e05"" />
                        <stop offset=""100%"" stop-color=""#1a2d0c"" />
                    </linearGradient>
                    <radialGradient id=""sm-3d-tongue"" cx=""50%"" cy=""95%"" r=""90%"">
                        <stop offset=""0%"" stop-color=""#ecffcf"" />
                        <stop offset=""60%"" stop-color=""#90db2c"" />
                        <stop offset=""100%"" stop-color=""#528813"" />
                    </radialGradient>
                    <clipPath id=""sm-top-3d"">
                        <path d=""M512 0H1024V1024H512Z"" />
                    </clipPath>
                    <clipPath id=""sm-smile-3d"">
                        <path id=""mouth-clip"" d=""M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z"" />
                    </clipPath>
                    <filter id=""petal-depth"" x=""-10%"" y=""-10%"" width=""120%"" height=""120%"">
                        <feDropShadow dx=""0"" dy=""6"" stdDeviation=""8"" flood-color=""#000000"" flood-opacity=""0.45"" />
                    </filter>
                </defs>
                <g id=""sm-character-3d"">
                    <path id=""sm-face"" d=""M512 161A351 351 0 1 1 512 863A351 351 0 1 1 512 161Z"" fill=""url(#sm-3d-face)"" />
                    <g id=""sm-petals"" fill=""url(#sm-3d-petal)"" filter=""url(#petal-depth)"">
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(0 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(300 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(240 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(180 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(120 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" transform=""rotate(60 512 512)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                        <path class=""sm-petal"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" clip-path=""url(#sm-top-3d)"" stroke=""#E3FFAD"" stroke-width=""1.2"" stroke-opacity="".5"" />
                    </g>
                    <g id=""sm-eyes"" fill=""url(#sm-3d-eye)"">
                        <path id=""sm-eye-l"" d=""M334 496 C339 457 390 411 429 410 C460 409 473 434 473 468 L473 485 C473 512 460 526 435 528 L371 533 C346 535 331 521 334 496Z"" />
                        <path id=""sm-eye-r"" d=""M551 468 C551 434 564 409 595 410 C634 411 685 457 690 496 C693 521 678 535 653 533 L589 528 C564 526 551 512 551 485Z"" />
                    </g>
                    <path id=""sm-mouth"" d=""M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z"" fill=""url(#sm-3d-mouth)"" stroke=""#67a31b"" stroke-width=""1.5"" />
                    <g clip-path=""url(#sm-smile-3d)"">
                        <path id=""sm-tongue"" d=""M449 668A91 54 0 1 1 631 668A91 54 0 1 1 449 668Z"" fill=""url(#sm-3d-tongue)"" />
                        <path id=""sm-teeth"" d=""M390 557 Q507 580 648 542 L649 557 Q529 608 407 584Z"" fill=""url(#sm-3d-eye)"" />
                    </g>
                </g>
            </svg>
        </div>
        <div class=""loading-details"">
            <div class=""loading-title"">Sentinel</div>
            <div class=""loading-subtitle"">Conectando aos registros de governança...</div>
            <div class=""progress-track-linear"">
                <div id=""loading-progress"" class=""progress-fill-linear""></div>
            </div>
        </div>
    </div>

    <div id=""toast-banner"">
        <div id=""toast-dot"" class=""toast-pill-dot""></div>
        <div id=""toast-text"" class=""toast-label"">Operação realizada</div>
    </div>

    <div class=""layout-container"">
        <header class=""top-navbar"">
            <div class=""brand-section"">
                <div class=""brand-avatar-box"">
                    <img src=""/sentinel-verde.svg"" alt=""Sentinel"">
                </div>
                <div class=""brand-title-wrap"">
                    <h1>Sentinel</h1>
                    <div class=""caption"">Governança Arquitetural & Auditoria Imutável</div>
                </div>
            </div>
            <div class=""navbar-status"">
                <div class=""session-badge"">
                    <span class=""live-dot""></span>
                    <span>Sessão: Maintainer</span>
                </div>
            </div>
        </header>

        <section class=""metrics-grid"">
            <div class=""metric-card"">
                <div class=""metric-label"">Decisões Vigentes</div>
                <div class=""metric-value-row"">
                    <span class=""metric-number"">{decisoesVigentes.Count}</span>
                    <span class=""metric-tag emerald"">Ativas</span>
                </div>
            </div>
            <div class=""metric-card"">
                <div class=""metric-label"">Propostas Pendentes</div>
                <div class=""metric-value-row"">
                    <span class=""metric-number"">{propostas.Count}</span>
                    <span class=""metric-tag {(propostas.Count > 0 ? "amber" : "neutral")}"">{(propostas.Count > 0 ? "Revisão Necessária" : "Em dia")}</span>
                </div>
            </div>
            <div class=""metric-card"">
                <div class=""metric-label"">Incidentes Registrados</div>
                <div class=""metric-value-row"">
                    <span class=""metric-number"">{incidentes.Count}</span>
                    <span class=""metric-tag neutral"">Histórico</span>
                </div>
            </div>
            <div class=""metric-card"">
                <div class=""metric-label"">Espaço de Trabalho</div>
                <div class=""metric-value-row"">
                    <span style=""font-size: 0.95rem; font-weight: 600; color: var(--text-primary);"">Pagamentos</span>
                    <span class=""metric-tag emerald"">RLS Ativo</span>
                </div>
            </div>
        </section>

        <nav class=""segmented-tabs"">
            <button class=""tab-btn active"" onclick=""trocarAba('tab-propostas', this)"">
                <span>Propostas Pendentes</span>
                <span class=""tab-count"">{propostas.Count}</span>
            </button>
            <button class=""tab-btn"" onclick=""trocarAba('tab-vigentes', this)"">
                <span>Decisões Vigentes</span>
                <span class=""tab-count"">{decisoesVigentes.Count}</span>
            </button>
            <button class=""tab-btn"" onclick=""trocarAba('tab-incidentes', this)"">
                <span>Incidentes Reportados</span>
                <span class=""tab-count"">{incidentes.Count}</span>
            </button>
        </nav>

        <section id=""tab-propostas"" class=""tab-panel active"">";

        if (propostas.Count == 0)
        {
            html += @"
            <div class=""empty-box"">
                <h3>Nenhuma proposta aguardando revisão</h3>
                <p>Todas as decisões arquiteturais estão em dia. Novas propostas geradas por agentes via MCP aparecerão aqui para validação humana.</p>
            </div>";
        }
        else
        {
            foreach (var p in propostas)
            {
                var evidenciasHtml = string.Join("", p.Evidencias.Select(e => $@"
                    <a href=""{e.Url}"" target=""_blank"" rel=""noopener noreferrer"" class=""evidence-item"">
                        <div class=""evidence-left"">
                            <span class=""evidence-badge"">{e.Tipo}</span>
                            <span>{e.Rotulo}</span>
                        </div>
                        <span class=""evidence-arrow"">›</span>
                    </a>"));

                html += $@"
                <article class=""data-card"" id=""proposta-{p.TopicoId}"">
                    <div class=""data-card-header"">
                        <div class=""data-card-tags"">
                            <span class=""tag-pill code"">tópico: {p.ChaveTopico}</span>
                            <span class=""tag-pill"">fluxo v{p.VersaoFluxo}</span>
                            <span class=""tag-pill"" style=""color: var(--status-warning);"">Aguardando Aprovação</span>
                        </div>
                    </div>
                    <h2 class=""data-card-title"">{p.Titulo}</h2>
                    <div class=""inset-group"">
                        <div class=""inset-text""><strong>Resumo:</strong> {p.Resumo}</div>
                        <div class=""inset-text""><strong>Justificativa Técnica:</strong> {p.Justificativa ?? "Decisão proposta para governança técnica contínua."}</div>
                    </div>
                    <div style=""font-size: 0.76rem; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-tertiary); margin-bottom: 0.5rem; font-weight: 600;"">Evidências Anexadas</div>
                    <div class=""evidence-stack"">{evidenciasHtml}</div>
                    <div class=""actions-toolbar"">
                        <button class=""btn-primary-action"" onclick=""abrirModalAprovacao('{p.EspacoTrabalhoId}', '{p.ProjetoId}', '{p.TopicoId}', '{p.PropostaPendenteId ?? p.RegistroAtivoId}', {p.VersaoFluxo})"">Aprovar Decisão</button>
                        <button class=""btn-danger-action"" onclick=""abrirModalRejeicao('{p.EspacoTrabalhoId}', '{p.ProjetoId}', '{p.TopicoId}', '{p.PropostaPendenteId ?? p.RegistroAtivoId}', {p.VersaoFluxo})"">Rejeitar</button>
                    </div>
                </article>";
            }
        }

        html += $@"
        </section>

        <section id=""tab-vigentes"" class=""tab-panel"">";

        if (decisoesVigentes.Count == 0)
        {
            html += @"
            <div class=""empty-box"">
                <h3>Nenhuma decisão vigente ativa</h3>
                <p>Ainda não há decisões arquiteturais promovidas a vigentes neste espaço de trabalho.</p>
            </div>";
        }
        else
        {
            foreach (var d in decisoesVigentes)
            {
                var evidenciasHtml = string.Join("", d.Evidencias.Select(e => $@"
                    <a href=""{e.Url}"" target=""_blank"" rel=""noopener noreferrer"" class=""evidence-item"">
                        <div class=""evidence-left"">
                            <span class=""evidence-badge"">{e.Tipo}</span>
                            <span>{e.Rotulo}</span>
                        </div>
                        <span class=""evidence-arrow"">›</span>
                    </a>"));

                html += $@"
                <article class=""data-card"">
                    <div class=""data-card-header"">
                        <div class=""data-card-tags"">
                            <span class=""tag-pill code"">tópico: {d.ChaveTopico}</span>
                            <span class=""tag-pill"">fluxo v{d.VersaoFluxo}</span>
                            <span class=""tag-pill"" style=""color: var(--brand-emerald);"">● Vigente / Ativa</span>
                        </div>
                        <span style=""font-size: 0.74rem; color: var(--text-tertiary);"">Atualizado: {d.AtualizadoEm:dd/MM/yyyy HH:mm}</span>
                    </div>
                    <h2 class=""data-card-title"">{d.Titulo}</h2>
                    <div class=""inset-group"">
                        <div class=""inset-text""><strong>Resumo:</strong> {d.Resumo}</div>
                        <div class=""inset-text""><strong>Justificativa Arquitetural:</strong> {d.Justificativa ?? "Governança arquitetural consolidada."}</div>
                    </div>
                    <div style=""font-size: 0.76rem; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-tertiary); margin-bottom: 0.5rem; font-weight: 600;"">Evidências e Histórico</div>
                    <div class=""evidence-stack"">{evidenciasHtml}</div>
                </article>";
            }
        }

        html += $@"
        </section>

        <section id=""tab-incidentes"" class=""tab-panel"">";

        if (incidentes.Count == 0)
        {
            html += @"
            <div class=""empty-box"">
                <h3>Nenhum incidente reportado</h3>
                <p>Nenhum relato de incidente foi submetido via ferramenta <code>log_incident</code>.</p>
            </div>";
        }
        else
        {
            foreach (var inc in incidentes)
            {
                var evidenciasHtml = string.Join("", inc.Evidencias.Select(e => $@"
                    <a href=""{e.Url}"" target=""_blank"" rel=""noopener noreferrer"" class=""evidence-item"">
                        <div class=""evidence-left"">
                            <span class=""evidence-badge"">{e.Tipo}</span>
                            <span>{e.Rotulo}</span>
                        </div>
                        <span class=""evidence-arrow"">›</span>
                    </a>"));

                var tagsHtml = string.Join(" ", inc.ChavesTecnicas.Select(k => $@"<span class=""tag-pill code"">{k}</span>"));

                html += $@"
                <article class=""data-card"">
                    <div class=""data-card-header"">
                        <div class=""data-card-tags"">
                            <span class=""tag-pill"">incidente: {inc.IncidenteId.ToString()[..8]}...</span>
                            <span class=""tag-pill"">versão v{inc.VersaoFluxo}</span>
                            <span class=""tag-pill"" style=""color: var(--text-secondary);"">Reportado</span>
                        </div>
                        <span style=""font-size: 0.74rem; color: var(--text-tertiary);"">Ocorrido: {inc.OcorridoEm:dd/MM/yyyy HH:mm}</span>
                    </div>
                    <h2 class=""data-card-title"">{inc.Titulo}</h2>
                    <div class=""inset-group"">
                        <div class=""inset-text""><strong>Relato:</strong> {inc.Resumo}</div>
                        <div class=""inset-text""><strong>Chaves Técnicas:</strong> {tagsHtml}</div>
                    </div>
                    <div style=""font-size: 0.76rem; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-tertiary); margin-bottom: 0.5rem; font-weight: 600;"">Evidências (RCA / Correções)</div>
                    <div class=""evidence-stack"">{evidenciasHtml}</div>
                </article>";
            }
        }

        html += $@"
        </section>
    </div>

    <div id=""dialog-overlay"" class=""modal-overlay"">
        <div class=""modal-dialog"">
            <div class=""dialog-header"">
                <h3 id=""dialog-title"">Confirmar Operação</h3>
                <p id=""dialog-desc"">Deseja prosseguir com a operação de governança?</p>
            </div>
            <div id=""dialog-input-container"" style=""display:none;"">
                <textarea id=""dialog-reason"" class=""dialog-input"" placeholder=""Descreva a justificativa técnica para a rejeição da proposta...""></textarea>
            </div>
            <div class=""dialog-actions"">
                <button class=""btn-dialog-cancel"" onclick=""fecharModal()"">Cancelar</button>
                <button id=""dialog-confirm-btn"" class=""btn-primary-action"" onclick=""confirmarOperacaoModal()"">Confirmar</button>
            </div>
        </div>
    </div>

    <script src=""https://cdnjs.cloudflare.com/ajax/libs/gsap/3.12.5/gsap.min.js""></script>
    <script>
        const tokenCsrf = '{tokenCsrf}';
        let dadosModal = null;

        function trocarAba(idPainel, botao) {{
            document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
            document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
            document.getElementById(idPainel).classList.add('active');
            botao.classList.add('active');
        }}

        function exibirToast(mensagem, tipo = 'sucesso') {{
            const toast = document.getElementById('toast-banner');
            const dot = document.getElementById('toast-dot');
            const label = document.getElementById('toast-text');
            dot.className = tipo === 'sucesso' ? 'toast-pill-dot' : 'toast-pill-dot erro';
            label.textContent = mensagem;
            toast.classList.add('ativo');
            setTimeout(() => {{
                toast.classList.remove('ativo');
            }}, 3000);
        }}

        function executarAnimacaoMascote3D(aoFinalizar) {{
            const character = document.getElementById('sm-character-3d');
            const shadow = document.getElementById('mascot-shadow');
            const eyeR = document.getElementById('sm-eye-r');
            const mouth = document.getElementById('sm-mouth');
            const mouthClip = document.getElementById('mouth-clip');
            const teeth = document.getElementById('sm-teeth');
            const tongue = document.getElementById('sm-tongue');
            const progress = document.getElementById('loading-progress');

            if (progress) progress.style.width = '100%';

            if (typeof gsap === 'undefined') {{
                setTimeout(aoFinalizar, 900);
                return;
            }}

            gsap.set(character, {{
                scale: 0.78,
                opacity: 0,
                y: 30,
                rotationX: 18,
                rotationY: -22,
                rotationZ: -4,
                transformOrigin: '50% 75%'
            }});
            gsap.set(shadow, {{ opacity: 0, scale: 0.5 }});
            gsap.set(teeth, {{ y: -28 }});
            gsap.set(tongue, {{ y: 20 }});

            const tl = gsap.timeline({{
                onComplete: () => {{
                    if (aoFinalizar) aoFinalizar();
                }}
            }});

            tl.to(character, {{
                scale: 1,
                opacity: 1,
                y: 0,
                rotationX: 0,
                rotationY: 0,
                rotationZ: 0,
                duration: 0.75,
                ease: 'back.out(1.4)'
            }}, 0);

            tl.to(shadow, {{
                opacity: 0.8,
                scale: 1,
                duration: 0.75,
                ease: 'power2.out'
            }}, 0.05);

            const bocaAberta = 'M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z';
            tl.to([mouth, mouthClip], {{
                attr: {{ d: bocaAberta }},
                duration: 0.3,
                ease: 'back.out(1.8)'
            }}, 0.45);
            tl.to([teeth, tongue], {{
                y: 0,
                duration: 0.3,
                ease: 'back.out(1.8)'
            }}, 0.45);

            tl.to(character, {{
                y: -10,
                rotationX: -6,
                rotationY: 8,
                duration: 0.22,
                ease: 'power2.out',
                yoyo: true,
                repeat: 1
            }}, 0.5);

            tl.to(eyeR, {{
                scaleY: 0.1,
                duration: 0.1,
                transformOrigin: '50% 50%',
                ease: 'power2.inOut',
                yoyo: true,
                repeat: 1
            }}, 0.75);
        }}

        window.addEventListener('DOMContentLoaded', () => {{
            executarAnimacaoMascote3D(() => {{
                setTimeout(() => {{
                    document.getElementById('loading-screen').classList.add('oculto');
                }}, 250);
            }});
        }});

        function abrirModalAprovacao(wsId, projId, topicoId, propostaId, versaoAtual) {{
            dadosModal = {{
                tipo: 'aprovar',
                wsId, projId, topicoId, propostaId, versaoAtual
            }};
            document.getElementById('dialog-title').textContent = 'Aprovar Decisão';
            document.getElementById('dialog-desc').textContent = 'Esta proposta será aprovada e ativada imediatamente na governança, tornando-se a versão vigente para o tópico.';
            document.getElementById('dialog-input-container').style.display = 'none';
            const btn = document.getElementById('dialog-confirm-btn');
            btn.className = 'btn-primary-action';
            btn.textContent = 'Aprovar Decisão';
            document.getElementById('dialog-overlay').classList.add('ativo');
        }}

        function abrirModalRejeicao(wsId, projId, topicoId, propostaId, versaoAtual) {{
            dadosModal = {{
                tipo: 'rejeitar',
                wsId, projId, topicoId, propostaId, versaoAtual
            }};
            document.getElementById('dialog-title').textContent = 'Rejeitar Proposta';
            document.getElementById('dialog-desc').textContent = 'Informe a justificativa técnica para rejeição. A proposta será arquivada no histórico.';
            document.getElementById('dialog-input-container').style.display = 'block';
            document.getElementById('dialog-reason').value = '';
            const btn = document.getElementById('dialog-confirm-btn');
            btn.className = 'btn-danger-action';
            btn.textContent = 'Rejeitar Proposta';
            document.getElementById('dialog-overlay').classList.add('ativo');
        }}

        function fecharModal() {{
            document.getElementById('dialog-overlay').classList.remove('ativo');
            dadosModal = null;
        }}

        async function confirmarOperacaoModal() {{
            if (!dadosModal) return;
            const p = dadosModal;

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
                        setTimeout(() => location.reload(), 1100);
                    }} else {{
                        const err = await resp.json();
                        exibirToast('Erro: ' + (err.message || 'Falha de validação'), 'erro');
                    }}
                }} catch (e) {{
                    exibirToast('Erro de comunicação com o servidor', 'erro');
                }}
            }} else if (p.tipo === 'rejeitar') {{
                const motivo = document.getElementById('dialog-reason').value.trim();
                if (!motivo) {{
                    exibirToast('Por favor, informe a justificativa.', 'erro');
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
                        setTimeout(() => location.reload(), 1100);
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
