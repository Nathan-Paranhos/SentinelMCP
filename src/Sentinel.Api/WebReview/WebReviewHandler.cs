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

    public async Task RenderLandingPageAsync(HttpContext contexto)
    {
        if (contexto.Request.Query.ContainsKey("app"))
        {
            await RenderReviewPageAsync(contexto);
            return;
        }

        contexto.Response.ContentType = "text/html; charset=utf-8";
        var html = @"<!DOCTYPE html>
<html lang=""pt-BR"" data-theme=""dark"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Sentinel — A Memória da sua Engenharia</title>
    <link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
    <link rel=""stylesheet"" href=""/css/tokens.css"">
    <link rel=""stylesheet"" href=""/css/landing.css"">
    <link rel=""stylesheet"" href=""/css/components.css"">
    <script>
        const temaSalvo = localStorage.getItem('sentinel_theme') || 'dark';
        document.documentElement.setAttribute('data-theme', temaSalvo);
    </script>
</head>
<body class=""landing-page"">
    <nav class=""landing-nav"">
        <div class=""landing-nav-inner"">
            <a href=""/"" class=""landing-brand"">
                <img src=""/sentinel-verde.svg"" alt=""Sentinel Logo"">
                <span>Sentinel</span>
            </a>
            <div class=""landing-nav-links"">
                <a href=""#arquitetura"">Arquitetura</a>
                <a href=""#pilares"">Pilares</a>
                <a href=""https://github.com/Nathan-Paranhos/SentinelMCP"" target=""_blank"" rel=""noopener noreferrer"">Repositório</a>
                <a href=""/review"" class=""btn-landing-primary"">Abrir Workspace</a>
            </div>
        </div>
    </nav>

    <header class=""landing-hero"">
        <div class=""landing-eyebrow"">Governança Arquitetural & Auditoria Imutável</div>
        <h1 class=""landing-headline"">A memória da sua engenharia.</h1>
        <p class=""landing-subhead"">
            Consolidação contínua de decisões de arquitetura e relatos de incidentes com proveniência imutável.
            Garanta que seus agentes operem com rigor técnico, contexto compartilhado e validação humana em tempo real via MCP.
        </p>
        <div class=""landing-cta-row"">
            <a href=""/review"" class=""btn-landing-primary"">Abrir Sentinel Workspace</a>
            <a href=""https://github.com/Nathan-Paranhos/SentinelMCP"" target=""_blank"" rel=""noopener noreferrer"" class=""btn-landing-secondary"">Explorar no GitHub</a>
        </div>
    </header>

    <section class=""landing-preview-section"">
        <div class=""landing-preview-window"">
            <div style=""height: 38px; background: var(--bg-surface-secondary); border-bottom: 1px solid var(--border-subtle); display: flex; align-items: center; justify-content: space-between; padding: 0 16px;"">
                <div style=""display: flex; gap: 7px; align-items: center;"">
                    <span style=""width: 11px; height: 11px; border-radius: 50%; background: #ff5f56; display: inline-block;""></span>
                    <span style=""width: 11px; height: 11px; border-radius: 50%; background: #ffbd2e; display: inline-block;""></span>
                    <span style=""width: 11px; height: 11px; border-radius: 50%; background: #27c93f; display: inline-block;""></span>
                </div>
                <div style=""font-size: 0.76rem; font-weight: 500; color: var(--text-tertiary); font-family: var(--font-mono);"">Sentinel — Pagamentos e Cobrança — macOS Native Experience</div>
                <div style=""width: 48px;""></div>
            </div>
            <div style=""display: flex; height: 460px; background: var(--bg-window);"">
                <div style=""width: 220px; border-right: 1px solid var(--border-subtle); background: var(--bg-sidebar); padding: 1.25rem 0.85rem;"">
                    <div style=""font-size: 0.68rem; font-weight: 600; text-transform: uppercase; color: var(--text-tertiary); letter-spacing: 0.05em; margin-bottom: 8px;"">Espaço de Trabalho</div>
                    <div style=""padding: 6px 10px; border-radius: var(--radius-sm); background: var(--bg-selection); color: var(--text-primary); font-size: 0.82rem; font-weight: 600; margin-bottom: 3px;"">Revisões de Arquitetura</div>
                    <div style=""padding: 6px 10px; border-radius: var(--radius-sm); color: var(--text-secondary); font-size: 0.82rem; font-weight: 500; margin-bottom: 3px;"">Decisões Vigentes</div>
                    <div style=""padding: 6px 10px; border-radius: var(--radius-sm); color: var(--text-secondary); font-size: 0.82rem; font-weight: 500; margin-bottom: 3px;"">Incidentes Reportados</div>
                    <div style=""padding: 6px 10px; border-radius: var(--radius-sm); color: var(--text-secondary); font-size: 0.82rem; font-weight: 500;"">Linha do Tempo</div>
                </div>
                <div style=""width: 320px; border-right: 1px solid var(--border-subtle); padding: 1rem; overflow: hidden;"">
                    <div style=""padding: 0.75rem 0.85rem; border-radius: var(--radius-md); background: var(--bg-selection); border: 1px solid var(--border-subtle); margin-bottom: 8px;"">
                        <div style=""display: flex; justify-content: space-between; margin-bottom: 4px;"">
                            <span style=""font-family: var(--font-mono); font-size: 0.7rem; color: var(--action-primary);"">payment-idempotency</span>
                            <span style=""font-family: var(--font-mono); font-size: 0.68rem; color: var(--text-secondary);"">v2</span>
                        </div>
                        <div style=""font-size: 0.86rem; font-weight: 600; color: var(--text-primary); margin-bottom: 4px;"">Chave de Idempotência Obrigatória na Cobrança Pix</div>
                        <div style=""font-size: 0.76rem; color: var(--text-secondary);"">Garante idempotência estrita em cobranças Pix via redis token lock...</div>
                    </div>
                    <div style=""padding: 0.75rem 0.85rem; border-radius: var(--radius-md); background: var(--bg-surface-secondary); opacity: 0.75;"">
                        <div style=""display: flex; justify-content: space-between; margin-bottom: 4px;"">
                            <span style=""font-family: var(--font-mono); font-size: 0.7rem; color: var(--text-tertiary);"">circuit-breaker</span>
                            <span style=""font-family: var(--font-mono); font-size: 0.68rem; color: var(--text-secondary);"">v1</span>
                        </div>
                        <div style=""font-size: 0.86rem; font-weight: 600; color: var(--text-primary); margin-bottom: 4px;"">Circuit Breaker no Gateway Adquirente</div>
                        <div style=""font-size: 0.76rem; color: var(--text-secondary);"">Fallback para adquirente secundária após 3 timeouts consecutivos...</div>
                    </div>
                </div>
                <div style=""flex: 1; padding: 2rem; background: var(--bg-window); display: flex; flex-direction: column; justify-content: space-between;"">
                    <div>
                        <div style=""display: inline-flex; align-items: center; gap: 6px; font-size: 0.72rem; font-weight: 600; background: var(--status-success-bg); color: var(--status-success); padding: 3px 10px; border-radius: var(--radius-pill); margin-bottom: 1rem;"">
                            ● Decisão Vigente Ativa
                        </div>
                        <h2 style=""font-size: 1.5rem; font-weight: 600; margin: 0 0 0.5rem; color: var(--text-primary);"">Chave de Idempotência Obrigatória na Cobrança Pix</h2>
                        <p style=""font-size: 0.88rem; color: var(--text-secondary); line-height: 1.6; margin: 0 0 1.5rem;"">
                            Toda requisição de criação de cobrança Pix deve obrigatoriamente incluir o cabeçalho X-Idempotency-Key.
                            Em caso de retentativa com a mesma chave em intervalo inferior a 24 horas, o resultado persistido deve ser retornado sem reprocessamento financeiro.
                        </p>
                        <div style=""background: var(--bg-surface-secondary); border-radius: var(--radius-md); padding: 1rem; border-left: 3px solid var(--action-primary);"">
                            <div style=""font-size: 0.76rem; font-weight: 600; text-transform: uppercase; color: var(--text-tertiary); margin-bottom: 4px;"">Consequência Arquitetural</div>
                            <div style=""font-size: 0.84rem; color: var(--text-primary);"">Elimina 100% de cobranças duplicadas sob concorrência de webhook e retentativas em rede instável.</div>
                        </div>
                    </div>
                    <div style=""display: flex; gap: 10px;"">
                        <a href=""/review"" class=""btn-landing-primary"" style=""font-size: 0.84rem; padding: 7px 18px;"">Experimentar Interativamente</a>
                    </div>
                </div>
            </div>
        </div>
    </section>

    <section id=""pilares"" class=""landing-pillars"">
        <div style=""text-align: center; margin-bottom: 3.5rem;"">
            <h2 style=""font-size: 2.2rem; font-weight: 600; letter-spacing: -0.025em; color: var(--text-primary); margin: 0 0 0.75rem;"">
                Engenharia construída para autonomia com controle
            </h2>
            <p style=""font-size: 1.05rem; color: var(--text-secondary); max-width: 600px; margin: 0 auto;"">
                Desenvolvido sobre ASP.NET Core (.NET 9) e PostgreSQL 16+, combinando o protocolo MCP com garantias formais de concorrência e auditoria.
            </p>
        </div>
        <div class=""pillars-grid"">
            <div class=""pillar-item"">
                <div class=""pillar-icon-box"">
                    <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""10""/><polyline points=""12 6 12 12 16 14""/></svg>
                </div>
                <h3 class=""pillar-title"">Event Sourcing Imutável</h3>
                <p class=""pillar-desc"">
                    Cada decisão, substituição ou rejeição é persistida como um evento imutável. Projeções vigentes e comprovantes criptográficos são derivados deterministicamente do histórico.
                </p>
            </div>
            <div class=""pillar-item"">
                <div class=""pillar-icon-box"">
                    <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/><path d=""m9 12 2 2 4-4""/></svg>
                </div>
                <h3 class=""pillar-title"">Isolamento RLS</h3>
                <p class=""pillar-desc"">
                    Row-Level Security nativo e controle granular de papéis. Agentes só acessam tópicos e incidentes do espaço de trabalho autorizado, prevenindo vazamentos de contexto.
                </p>
            </div>
            <div class=""pillar-item"">
                <div class=""pillar-icon-box"">
                    <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><rect width=""18"" height=""18"" x=""3"" y=""3"" rx=""2""/><path d=""m9 9 3 3-3 3""/><path d=""M15 15h.01""/></svg>
                </div>
                <h3 class=""pillar-title"">Protocolo MCP Oficial</h3>
                <p class=""pillar-desc"">
                    Servidor Streamable HTTP implementando tools para propor decisões, reportar incidentes, consultar contexto e obter comprovantes com total compatibilidade com Claude Code e Cursor.
                </p>
            </div>
            <div class=""pillar-item"">
                <div class=""pillar-icon-box"">
                    <svg width=""20"" height=""20"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z""/><polyline points=""""14 2 14 8 20 8""/><path d=""m9 15 2 2 4-4""/></svg>
                </div>
                <h3 class=""pillar-title"">Validação Humana & Anti-Divergência</h3>
                <p class=""pillar-desc"">
                    Propostas de agentes necessitam de aprovação de Maintainers. Verificação de versão esperada impede sobrescritas concorrentes e retentativas não intencionais.
                </p>
            </div>
        </div>
    </section>

    <footer class=""landing-footer"">
        <div style=""display: flex; align-items: center; justify-content: center; gap: 8px; margin-bottom: 0.5rem;"">
            <img src=""/sentinel-verde.svg"" alt=""Sentinel"" style=""width: 20px; height: 20px;"">
            <span style=""font-weight: 600; color: var(--text-primary);"">Sentinel MCP</span>
        </div>
        <p style=""margin: 0;"">Governança técnica, memória corporativa e conformidade arquitetural para equipes e agentes autônomos.</p>
    </footer>
</body>
</html>";

        await contexto.Response.WriteAsync(html);
    }

    public async Task RenderReviewPageAsync(HttpContext contexto)
    {
        var tokenCsrf = GerarTokenCsrf();
        contexto.Response.Cookies.Append("sentinel_csrf_token", tokenCsrf, new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Strict });

        var espacoPadraoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var propostas = await _repositorioDecisao.ObterPropostasPendentesAsync(espacoPadraoId);
        var decisoesVigentes = await _repositorioDecisao.ObterDecisoesVigentesAsync(espacoPadraoId);
        var incidentes = await _repositorioIncidente.ObterIncidentesAsync(espacoPadraoId);

        var opcoesJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        var pacoteDados = new
        {
            propostas = propostas,
            decisoes = decisoesVigentes,
            incidentes = incidentes,
            workspace = new
            {
                id = espacoPadraoId.ToString(),
                nome = "Pagamentos e Cobrança",
                slug = "pagamentos-cobranca"
            }
        };

        var dadosJson = JsonSerializer.Serialize(pacoteDados, opcoesJson);

        contexto.Response.ContentType = "text/html; charset=utf-8";
        var html = $@"<!DOCTYPE html>
<html lang=""pt-BR"" data-theme=""dark"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Sentinel — Governança e Decisões de Arquitetura</title>
    <link rel=""icon"" href=""/sentinel-verde.svg"" type=""image/svg+xml"">
    <link rel=""stylesheet"" href=""/css/tokens.css"">
    <link rel=""stylesheet"" href=""/css/app-shell.css"">
    <link rel=""stylesheet"" href=""/css/components.css"">
    <link rel=""stylesheet"" href=""/css/views.css"">
    <script>
        const temaArmazenado = localStorage.getItem('sentinel_theme') || 'dark';
        document.documentElement.setAttribute('data-theme', temaArmazenado);
    </script>
</head>
<body>
    <div class=""app-viewport"">
        <main class=""macos-window"">
            <aside class=""macos-sidebar"" id=""macos-sidebar"">
                <div>
                    <div class=""sidebar-header"">
                        <div class=""sidebar-brand-mark"">
                            <img src=""/sentinel-verde.svg"" alt=""Sentinel"">
                        </div>
                        <div class=""sidebar-brand-text"">
                            <span class=""sidebar-brand-title"">Sentinel</span>
                            <span class=""sidebar-brand-badge"">Governança v0.1.0</span>
                        </div>
                    </div>

                    <div class=""sidebar-nav-scroll"">
                        <div class=""nav-group-title"">Espaço de Trabalho</div>
                        <div class=""nav-item active"" data-view-nav=""reviews"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z""/><polyline points=""14 2 14 8 20 8""/><path d=""m9 15 2 2 4-4""/></svg>
                                <span>Revisões</span>
                            </div>
                            <span class=""nav-item-count"" id=""count-reviews"">{propostas.Count}</span>
                        </div>
                        <div class=""nav-item"" data-view-nav=""decisions"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><polygon points=""12 2 2 7 12 12 22 7 12 2""/><polyline points=""2 17 12 22 22 17""/><polyline points=""2 12 12 17 22 12""/></svg>
                                <span>Decisões Vigentes</span>
                            </div>
                            <span class=""nav-item-count"" id=""count-decisions"">{decisoesVigentes.Count}</span>
                        </div>
                        <div class=""nav-item"" data-view-nav=""incidents"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/><line x1=""12"" y1=""8"" x2=""12"" y2=""12""/><line x1=""12"" y1=""16"" x2=""12.01"" y2=""16""/></svg>
                                <span>Incidentes</span>
                            </div>
                            <span class=""nav-item-count"" id=""count-incidents"">{incidentes.Count}</span>
                        </div>
                        <div class=""nav-item"" data-view-nav=""overview"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><rect width=""7"" height=""9"" x=""3"" y=""3"" rx=""1.5""/><rect width=""7"" height=""5"" x=""14"" y=""3"" rx=""1.5""/><rect width=""7"" height=""9"" x=""14"" y=""12"" rx=""1.5""/><rect width=""7"" height=""5"" x=""3"" y=""16"" rx=""1.5""/></svg>
                                <span>Visão Geral</span>
                            </div>
                        </div>

                        <div class=""nav-group-title"" style=""margin-top:0.75rem;"">Auditoria & MCP</div>
                        <div class=""nav-item"" data-view-nav=""timeline"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""10""/><polyline points=""12 6 12 12 16 14""/></svg>
                                <span>Linha do Tempo</span>
                            </div>
                        </div>
                        <div class=""nav-item"" data-view-nav=""mcp"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><rect width=""18"" height=""18"" x=""3"" y=""3"" rx=""2""/><path d=""m9 9 3 3-3 3""/><path d=""M15 15h.01""/></svg>
                                <span>Ferramentas MCP</span>
                            </div>
                        </div>

                        <div class=""nav-group-title"" style=""margin-top:0.75rem;"">Sistema</div>
                        <div class=""nav-item"" data-view-nav=""settings"">
                            <div class=""nav-item-content"">
                                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""3""/><path d=""M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z""/></svg>
                                <span>Configurações</span>
                            </div>
                        </div>
                    </div>
                </div>

                <div class=""sidebar-footer"">
                    <div class=""workspace-pill"">
                        <div class=""workspace-pill-left"">
                            <span class=""workspace-dot""></span>
                            <span style=""font-weight:600;"">Pagamentos</span>
                        </div>
                        <span style=""font-family:var(--font-mono);font-size:0.68rem;color:var(--status-success);"">RLS</span>
                    </div>
                    <a href=""/"" style=""text-decoration:none;font-size:0.75rem;color:var(--text-tertiary);display:flex;align-items:center;justify-content:center;gap:4px;padding:4px 0;transition:color var(--motion-fast);"" onmouseover=""this.style.color='var(--text-primary)'"" onmouseout=""this.style.color='var(--text-tertiary)'"">
                        <span>Página Inicial</span>
                        <span>›</span>
                    </a>
                </div>
            </aside>

            <section class=""macos-main"">
                <header class=""macos-toolbar"">
                    <div class=""toolbar-left"">
                        <button id=""sidebar-toggle-btn"" class=""toolbar-icon-btn active"" title=""Alternar Barra Lateral"">
                            <svg width=""17"" height=""17"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.6"" stroke-linecap=""round"" stroke-linejoin=""round""><rect width=""18"" height=""18"" x=""3"" y=""3"" rx=""2""/><path d=""M9 3v18""/></svg>
                        </button>
                        <span class=""toolbar-title"" id=""toolbar-view-title"">Revisões de Arquitetura</span>
                    </div>
                    <div class=""toolbar-right"">
                        <button id=""toolbar-search-trigger"" class=""toolbar-search-btn"">
                            <svg width=""14"" height=""14"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""11"" cy=""11"" r=""8""/><line x1=""21"" y1=""21"" x2=""16.65"" y2=""16.65""/></svg>
                            <span>Buscar</span>
                            <kbd class=""toolbar-search-kbd"">⌘K</kbd>
                        </button>
                        <button id=""theme-toggle-btn"" class=""toolbar-icon-btn"" title=""Alternar Tema"">
                            <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.6"" stroke-linecap=""round"" stroke-linejoin=""round""><circle cx=""12"" cy=""12"" r=""4""/><path d=""M12 2v2""/><path d=""M12 20v2""/><path d=""m4.93 4.93 1.41 1.41""/><path d=""m17.66 17.66 1.41 1.41""/><path d=""M2 12h2""/><path d=""M20 12h2""/><path d=""m6.34 17.66-1.41 1.41""/><path d=""m19.07 4.93-1.41 1.41""/></svg>
                        </button>
                        <button id=""inspector-toggle-btn"" class=""toolbar-icon-btn active"" title=""Alternar Painel Inspetor"">
                            <svg width=""17"" height=""17"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.6"" stroke-linecap=""round"" stroke-linejoin=""round""><rect width=""18"" height=""18"" x=""3"" y=""3"" rx=""2""/><path d=""M15 3v18""/></svg>
                        </button>
                    </div>
                </header>

                <div class=""content-body"" id=""content-body"">
                </div>
            </section>

            <aside class=""macos-inspector"" id=""macos-inspector"">
            </aside>
        </main>
    </div>

    <div id=""command-palette"" class=""command-backdrop"">
        <div class=""command-box"">
            <div class=""command-input-row"">
                <svg width=""16"" height=""16"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""1.8"" stroke-linecap=""round"" stroke-linejoin=""round"" style=""color:var(--text-tertiary);""><circle cx=""11"" cy=""11"" r=""8""/><line x1=""21"" y1=""21"" x2=""16.65"" y2=""16.65""/></svg>
                <input id=""command-search-input"" class=""command-input"" type=""text"" placeholder=""Buscar decisões, propostas, incidentes ou comandos..."" autocomplete=""off"">
                <span style=""font-family:var(--font-mono);font-size:0.7rem;color:var(--text-tertiary);background:var(--bg-surface-secondary);padding:2px 6px;border-radius:4px;"">ESC</span>
            </div>
            <div id=""command-results"" class=""command-results"">
            </div>
            <div class=""command-footer"">
                <div class=""command-shortcuts"">
                    <span><kbd>↑</kbd><kbd>↓</kbd> navegar</span>
                    <span><kbd>↵</kbd> selecionar</span>
                    <span><kbd>esc</kbd> fechar</span>
                </div>
                <span>Spotlight de Governança Sentinel</span>
            </div>
        </div>
    </div>

    <div id=""modal-dialog"" class=""modal-dialog-backdrop"">
        <div class=""modal-dialog-box"">
            <div>
                <h3 id=""modal-title"" class=""modal-dialog-title"">Confirmar Operação</h3>
                <p id=""modal-desc"" class=""modal-dialog-desc"" style=""margin-top:0.4rem;"">Deseja prosseguir com a governança?</p>
            </div>
            <div id=""modal-input-wrap"" style=""display:none;"">
                <textarea id=""modal-reason-input"" class=""modal-dialog-textarea"" placeholder=""Descreva a justificativa técnica para o arquivamento da proposta...""></textarea>
            </div>
            <div class=""modal-dialog-actions"">
                <button id=""modal-cancel-btn"" class=""btn-macos btn-macos-secondary"">Cancelar</button>
                <button id=""modal-confirm-btn"" class=""btn-macos btn-macos-primary"">Confirmar</button>
            </div>
        </div>
    </div>

    <div id=""toast-container"" class=""toast-container""></div>

    <script type=""module"">
        import {{ initApp }} from '/js/app.js';
        const dados = {dadosJson};
        const tokenCsrf = '{tokenCsrf}';
        initApp(dados, tokenCsrf);
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
