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
    <title>Sentinel-MCP — Painel de Governança e Revisão</title>
    <link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
    <style>
        :root {{
            --bg-page: #0B1008;
            --bg-surface: #13200D;
            --bg-elevated: #0E170A;
            --border-primary: #243B12;
            --border-accent: #355414;
            --brand-neon: #A3E635;
            --brand-lime: #B8F34B;
            --brand-glow: #D9FF8A;
            --brand-dark: #559912;
            --text-primary: #F8FAFC;
            --text-secondary: #E8F5D8;
            --text-muted: #94A3B8;
            --status-danger: #EF4444;
        }}
        * {{ box-sizing: border-box; }}
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            background: var(--bg-page);
            color: var(--text-primary);
            margin: 0;
            padding: 0;
            min-height: 100vh;
            overflow-x: hidden;
        }}
        #loading-screen {{
            position: fixed;
            top: 0; left: 0; width: 100vw; height: 100vh;
            background: var(--bg-page);
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            z-index: 9999;
            transition: opacity 0.6s cubic-bezier(0.16, 1, 0.3, 1), visibility 0.6s;
        }}
        #loading-screen.oculto {{
            opacity: 0;
            visibility: hidden;
            pointer-events: none;
        }}
        .mascot-container {{
            width: 280px;
            height: 280px;
            display: flex;
            align-items: center;
            justify-content: center;
        }}
        .mascot-svg {{
            width: 100%;
            height: 100%;
            overflow: visible;
        }}
        .loading-title {{
            margin-top: 1.5rem;
            font-size: 1.25rem;
            font-weight: 700;
            color: var(--brand-neon);
            letter-spacing: 0.05em;
        }}
        .loading-bar {{
            width: 220px;
            height: 4px;
            background: var(--border-primary);
            border-radius: 999px;
            margin-top: 1rem;
            overflow: hidden;
            position: relative;
        }}
        .loading-progress {{
            width: 0%;
            height: 100%;
            background: linear-gradient(90deg, var(--brand-dark), var(--brand-neon));
            box-shadow: 0 0 10px var(--brand-neon);
            border-radius: 999px;
            transition: width 2.4s ease-out;
        }}
        .app-container {{
            max-width: 1100px;
            margin: 0 auto;
            padding: 2.5rem 1.5rem;
        }}
        header {{
            background: var(--bg-surface);
            border: 1px solid var(--border-primary);
            border-radius: 16px;
            padding: 1.5rem 2rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 2rem;
            box-shadow: 0 8px 30px rgba(0,0,0,0.5);
        }}
        .brand-box {{
            display: flex;
            align-items: center;
            gap: 1.25rem;
        }}
        .brand-icon {{
            width: 52px;
            height: 52px;
            border-radius: 12px;
            border: 1px solid var(--border-accent);
            padding: 4px;
            background: var(--bg-elevated);
        }}
        h1 {{
            margin: 0;
            font-size: 1.4rem;
            color: var(--brand-neon);
            letter-spacing: -0.01em;
        }}
        .meta-header {{
            color: var(--text-muted);
            font-size: 0.85rem;
            margin-top: 0.2rem;
        }}
        .header-actions {{
            display: flex;
            align-items: center;
            gap: 1rem;
        }}
        .badge {{
            background: rgba(163, 230, 53, 0.12);
            color: var(--brand-neon);
            border: 1px solid var(--border-accent);
            padding: 0.35rem 0.75rem;
            border-radius: 999px;
            font-size: 0.75rem;
            font-weight: 600;
            text-transform: uppercase;
        }}
        .btn-replay {{
            background: transparent;
            color: var(--brand-neon);
            border: 1px solid var(--border-accent);
            padding: 0.4rem 0.9rem;
            border-radius: 8px;
            font-size: 0.8rem;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.2s;
        }}
        .btn-replay:hover {{
            background: var(--border-primary);
            border-color: var(--brand-neon);
        }}
        .card {{
            background: var(--bg-surface);
            border: 1px solid var(--border-primary);
            border-radius: 14px;
            padding: 1.75rem;
            margin-bottom: 1.75rem;
            box-shadow: 0 4px 20px rgba(0,0,0,0.3);
            transition: border-color 0.2s;
        }}
        .card:hover {{
            border-color: var(--border-accent);
        }}
        .card h2 {{
            margin-top: 0;
            color: var(--brand-glow);
            font-size: 1.25rem;
        }}
        .meta {{
            color: var(--text-muted);
            font-size: 0.85rem;
            margin-bottom: 1rem;
        }}
        code {{
            background: var(--bg-elevated);
            color: var(--brand-lime);
            padding: 0.2rem 0.4rem;
            border-radius: 4px;
            font-size: 0.85rem;
            border: 1px solid var(--border-primary);
        }}
        .summary {{
            background: var(--bg-elevated);
            padding: 1.25rem;
            border-radius: 8px;
            margin: 1.25rem 0;
            border-left: 4px solid var(--brand-neon);
            color: var(--text-secondary);
            line-height: 1.5;
        }}
        .evidence-list {{
            list-style: none;
            padding: 0;
            margin: 1rem 0;
        }}
        .evidence-list li {{
            margin-bottom: 0.6rem;
            font-size: 0.9rem;
        }}
        .evidence-list a {{
            color: var(--brand-neon);
            text-decoration: none;
            border-bottom: 1px dotted var(--brand-dark);
        }}
        .evidence-list a:hover {{
            color: var(--brand-glow);
            border-bottom-style: solid;
        }}
        .actions {{
            display: flex;
            gap: 1rem;
            margin-top: 1.5rem;
        }}
        button.action-btn {{
            padding: 0.65rem 1.4rem;
            border-radius: 8px;
            font-weight: 600;
            cursor: pointer;
            border: none;
            font-size: 0.875rem;
            transition: all 0.2s;
        }}
        .btn-approve {{
            background: var(--brand-neon);
            color: #0B1008;
        }}
        .btn-approve:hover {{
            background: var(--brand-lime);
            box-shadow: 0 0 15px rgba(163, 230, 53, 0.4);
        }}
        .btn-reject {{
            background: transparent;
            color: var(--status-danger);
            border: 1px solid #7F1D1D;
        }}
        .btn-reject:hover {{
            background: rgba(239, 68, 68, 0.1);
            border-color: var(--status-danger);
        }}
        .empty-state {{
            text-align: center;
            padding: 4rem 2rem;
            color: var(--text-muted);
        }}
        .empty-state h3 {{
            color: var(--brand-lime);
            font-size: 1.2rem;
            margin-bottom: 0.5rem;
        }}
    </style>
</head>
<body>
    <div id=""loading-screen"">
        <div class=""mascot-container"">
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
                        <path id=""mouth-clip"" d=""M399 566 Q512 585 632 549 Q644 546 642 560 C637 580 585 596 520 596 C462 596 413 590 398 577 Q394 566 399 566Z"" />
                    </clipPath>
                    <filter id=""sm-shadow"" x=""-15%"" y=""-15%"" width=""130%"" height=""140%"" color-interpolation-filters=""sRGB"">
                        <feDropShadow dx=""0"" dy=""26"" stdDeviation=""22"" flood-color=""#20380C"" flood-opacity=""0.4"" />
                    </filter>
                    <filter id=""sm-groundBlur"" x=""-30%"" y=""-200%"" width=""160%"" height=""500%"">
                        <feGaussianBlur stdDeviation=""14"" />
                    </filter>
                </defs>
                <ellipse id=""sm-ground"" cx=""512"" cy=""942"" rx=""230"" ry=""24"" fill=""#20380C"" fill-opacity="".38"" filter=""url(#sm-groundBlur)"" />
                <g id=""sm-shell"" filter=""url(#sm-shadow)"">
                    <path id=""sm-face"" d=""M512 161A351 351 0 1 1 512 863A351 351 0 1 1 512 161Z"" pathLength=""1"" fill=""url(#sm-face)"" stroke-linecap=""round"" stroke-linejoin=""round"" />
                    <g fill=""url(#sm-petal)"" stroke-linecap=""round"" stroke-linejoin=""round"">
                        <path class=""sm-petal"" data-order=""0"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" transform=""rotate(0 512 512)"" />
                        <path class=""sm-petal"" data-order=""5"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" transform=""rotate(300 512 512)"" />
                        <path class=""sm-petal"" data-order=""4"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" transform=""rotate(240 512 512)"" />
                        <path class=""sm-petal"" data-order=""3"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" transform=""rotate(180 512 512)"" />
                        <path class=""sm-petal"" data-order=""2"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" transform=""rotate(120 512 512)"" />
                        <path class=""sm-petal"" data-order=""1"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" transform=""rotate(60 512 512)"" />
                        <path class=""sm-petal"" data-order=""0"" d=""M310 252 C360 133 491 90 612 135 C729 178 786 290 746 408 C743 417 737 416 734 407 C696 294 625 201 533 190 C464 182 416 205 380 246 C365 261 335 259 310 252Z"" pathLength=""1"" clip-path=""url(#sm-top)"" />
                    </g>
                </g>
                <g fill=""url(#sm-eye)"" stroke-linecap=""round"" stroke-linejoin=""round"">
                    <path id=""sm-eye-l"" class=""sm-eye"" d=""M334 496 C339 457 390 411 429 410 C460 409 473 434 473 468 L473 485 C473 512 460 526 435 528 L371 533 C346 535 331 521 334 496Z"" pathLength=""1"" />
                    <path id=""sm-eye-r"" class=""sm-eye"" d=""M551 468 C551 434 564 409 595 410 C634 411 685 457 690 496 C693 521 678 535 653 533 L589 528 C564 526 551 512 551 485Z"" pathLength=""1"" />
                </g>
                <path id=""sm-mouth"" d=""M399 566 Q512 585 632 549 Q644 546 642 560 C637 580 585 596 520 596 C462 596 413 590 398 577 Q394 566 399 566Z"" pathLength=""1"" fill=""url(#sm-mouth)"" stroke-linecap=""round"" stroke-linejoin=""round"" />
                <g clip-path=""url(#sm-smile)"">
                    <path id=""sm-tongue"" d=""M449 668A91 54 0 1 1 631 668A91 54 0 1 1 449 668Z"" fill=""url(#sm-tongue)"" />
                    <path id=""sm-teeth"" d=""M390 557 Q507 580 648 542 L649 557 Q529 608 407 584Z"" fill=""url(#sm-eye)"" />
                </g>
            </svg>
        </div>
        <div class=""loading-title"">INICIALIZANDO SENTINEL-MCP</div>
        <div class=""loading-bar"">
            <div id=""loading-progress"" class=""loading-progress""></div>
        </div>
    </div>

    <div class=""app-container"">
        <header>
            <div class=""brand-box"">
                <img src=""/sentinel-verde.svg"" alt=""Sentinel Logo"" class=""brand-icon"">
                <div>
                    <h1>Sentinel-MCP — Portal de Revisão Humana</h1>
                    <div class=""meta-header"">Governança Arquitetural & Auditoria Imutável de Decisões</div>
                </div>
            </div>
            <div class=""header-actions"">
                <button class=""btn-replay"" onclick=""reexecutarAnimacaoCarregamento()"">Replay Mascote</button>
                <span class=""badge"">Papel: Maintainer</span>
            </div>
        </header>

        <main id=""proposals-container"">";

        if (propostas.Count == 0)
        {
            html += @"<div class=""card empty-state"">
                <h3>Nenhuma proposta pendente no momento.</h3>
                <p>Novas propostas submetidas por agentes via tool <code>propose_decision</code> aparecerão aqui para validação humana.</p>
            </div>";
        }
        else
        {
            foreach (var p in propostas)
            {
                var evidenciasHtml = string.Join("", p.Evidencias.Select(e => $"<li><strong>[{e.Tipo}]</strong> <a href=\"{e.Url}\" target=\"_blank\">{e.Rotulo}</a></li>"));
                html += $@"
                <div class=""card"" id=""card-{p.TopicoId}"">
                    <h2>{p.Titulo}</h2>
                    <div class=""meta"">
                        Tópico: <code>{p.ChaveTopico}</code> | Versão Corrente: {p.VersaoFluxo} | Espaço: <code>{p.EspacoTrabalhoId}</code>
                    </div>
                    <div class=""summary"">
                        <strong>Resumo:</strong> {p.Resumo}<br><br>
                        <strong>Justificativa:</strong> {p.Justificativa ?? "N/A"}
                    </div>
                    <h4>Evidências Anexadas:</h4>
                    <ul class=""evidence-list"">{evidenciasHtml}</ul>
                    <div class=""actions"">
                        <button class=""action-btn btn-approve"" onclick=""aprovarProposta('{p.EspacoTrabalhoId}', '{p.ProjetoId}', '{p.TopicoId}', '{p.PropostaPendenteId ?? p.RegistroAtivoId}', {p.VersaoFluxo})"">Aprovar Decisão</button>
                        <button class=""action-btn btn-reject"" onclick=""rejeitarProposta('{p.EspacoTrabalhoId}', '{p.ProjetoId}', '{p.TopicoId}', '{p.PropostaPendenteId ?? p.RegistroAtivoId}', {p.VersaoFluxo})"">Rejeitar Proposta</button>
                    </div>
                </div>";
            }
        }

        html += $@"
        </main>
    </div>

    <script src=""https://cdnjs.cloudflare.com/ajax/libs/gsap/3.12.5/gsap.min.js""></script>
    <script>
        const tokenCsrf = '{tokenCsrf}';

        function executarAnimacaoMascote(onFinish) {{
            const svg = document.getElementById('sentinel-mascot');
            const face = document.getElementById('sm-face');
            const petals = document.querySelectorAll('.sm-petal');
            const eyes = document.querySelectorAll('.sm-eye');
            const eyeR = document.getElementById('sm-eye-r');
            const mouth = document.getElementById('sm-mouth');
            const mouthClip = document.getElementById('mouth-clip');
            const teeth = document.getElementById('sm-teeth');
            const tongue = document.getElementById('sm-tongue');
            const ground = document.getElementById('sm-ground');
            const bar = document.getElementById('loading-progress');

            if (bar) bar.style.width = '100%';

            if (typeof gsap === 'undefined') {{
                setTimeout(onFinish, 1200);
                return;
            }}

            const contornos = [face, ...petals, ...eyes, mouth];
            gsap.set(contornos, {{
                fillOpacity: 0,
                stroke: '#639C18',
                strokeOpacity: 1,
                attr: {{ 'stroke-width': 3.5, 'stroke-dasharray': '1 2', 'stroke-dashoffset': 1 }}
            }});
            gsap.set(ground, {{ opacity: 0, scaleX: 0.6, transformOrigin: '50% 50%' }});
            gsap.set(teeth, {{ y: -44 }});
            gsap.set(tongue, {{ y: 26 }});
            gsap.set(eyes, {{ transformOrigin: '50% 50%' }});
            gsap.set(svg, {{
                transformPerspective: 1100,
                transformOrigin: '50% 88%',
                scale: 0.96,
                rotationY: -12,
                rotationX: 6
            }});

            const tl = gsap.timeline({{
                onComplete: () => {{
                    if (onFinish) onFinish();
                }}
            }});

            tl.to(face, {{ attr: {{ 'stroke-dashoffset': 0 }}, duration: 0.75, ease: 'power2.inOut' }}, 0);

            petals.forEach(p => {{
                const order = Number(p.dataset.order || 0);
                tl.to(p, {{ attr: {{ 'stroke-dashoffset': 0 }}, duration: 0.62, ease: 'power2.inOut' }}, 0.16 + order * 0.07);
            }});

            tl.to(eyes, {{ attr: {{ 'stroke-dashoffset': 0 }}, duration: 0.55, ease: 'power2.inOut', stagger: 0.1 }}, 0.5);
            tl.to(mouth, {{ attr: {{ 'stroke-dashoffset': 0 }}, duration: 0.55, ease: 'power2.inOut' }}, 0.62);

            tl.to(contornos, {{ fillOpacity: 1, duration: 0.5, ease: 'power2.out' }}, 1.1);
            tl.to(contornos, {{ strokeOpacity: 0, duration: 0.45, ease: 'power1.out' }}, 1.25);
            tl.to(ground, {{ opacity: 1, scaleX: 1, duration: 0.7, ease: 'power2.out' }}, 1.1);

            tl.to(svg, {{
                scale: 1,
                rotationY: 0,
                rotationX: 0,
                duration: 0.9,
                ease: 'back.out(1.2)'
            }}, 1.1);

            const bocaAberta = 'M399 566 Q512 585 632 549 Q644 546 642 560 C631 625 581 671 520 671 C462 671 417 634 398 577 Q394 566 399 566Z';
            tl.to([mouth, mouthClip], {{ attr: {{ d: bocaAberta }}, duration: 0.45, ease: 'back.out(1.8)' }}, 1.7);
            tl.to(teeth, {{ y: 0, duration: 0.45, ease: 'back.out(1.8)' }}, 1.7);
            tl.to(tongue, {{ y: 0, duration: 0.45, ease: 'back.out(1.8)' }}, 1.7);
            tl.to(svg, {{ yPercent: -4, duration: 0.22, ease: 'power2.out', yoyo: true, repeat: 1 }}, 1.75);

            tl.to(eyeR, {{ scaleY: 0.08, duration: 0.12, ease: 'power2.in', yoyo: true, repeat: 1 }}, 2.5);
            tl.to(svg, {{ rotation: -4, duration: 0.25, ease: 'power2.out', yoyo: true, repeat: 1 }}, 2.5);
        }}

        function reexecutarAnimacaoCarregamento() {{
            const screen = document.getElementById('loading-screen');
            const bar = document.getElementById('loading-progress');
            if (bar) bar.style.width = '0%';
            screen.classList.remove('oculto');
            executarAnimacaoMascote(() => {{
                setTimeout(() => {{
                    screen.classList.add('oculto');
                }}, 400);
            }});
        }}

        window.addEventListener('DOMContentLoaded', () => {{
            executarAnimacaoMascote(() => {{
                setTimeout(() => {{
                    document.getElementById('loading-screen').classList.add('oculto');
                }}, 500);
            }});
        }});

        async function aprovarProposta(wsId, projId, topicoId, propostaId, versaoAtual) {{
            if (!confirm('Deseja aprovar esta decisão de arquitetura? Ela será promovida a ativa.')) return;
            const resp = await fetch('/api/review/approve', {{
                method: 'POST',
                headers: {{
                    'Content-Type': 'application/json',
                    'X-CSRF-Token': tokenCsrf,
                    'X-Sentinel-Web-Session': 'maintainer-alice:https://auth.sentinel.dev'
                }},
                body: JSON.stringify({{
                    workspaceId: wsId,
                    projectId: projId,
                    topicId: topicoId,
                    proposalId: propostaId,
                    expectedVersion: versaoAtual,
                    idempotencyKey: 'review-approve-' + Date.now()
                }})
            }});
            if (resp.ok) {{
                alert('Decisão aprovada e ativada com sucesso!');
                location.reload();
            }} else {{
                const err = await resp.json();
                alert('Erro ao aprovar: ' + (err.message || 'Falha de validação'));
            }}
        }}

        async function rejeitarProposta(wsId, projId, topicoId, propostaId, versaoAtual) {{
            const motivo = prompt('Informe a justificativa da rejeição:');
            if (!motivo) return;
            const resp = await fetch('/api/review/reject', {{
                method: 'POST',
                headers: {{
                    'Content-Type': 'application/json',
                    'X-CSRF-Token': tokenCsrf,
                    'X-Sentinel-Web-Session': 'maintainer-alice:https://auth.sentinel.dev'
                }},
                body: JSON.stringify({{
                    workspaceId: wsId,
                    projectId: projId,
                    topicId: topicoId,
                    proposalId: propostaId,
                    expectedVersion: versaoAtual,
                    reason: motivo,
                    idempotencyKey: 'review-reject-' + Date.now()
                }})
            }});
            if (resp.ok) {{
                alert('Proposta rejeitada.');
                location.reload();
            }} else {{
                const err = await resp.json();
                alert('Erro ao rejeitar: ' + (err.message || 'Falha de validação'));
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
