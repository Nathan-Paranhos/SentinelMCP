import { appStore } from './store.js';
import { Icons } from './icons.js';
import { CommandPalette } from './command-palette.js';
import { escapeHtml, urlHttpsSegura, gerarChaveIdempotencia, formatarData } from './util.js';
import {
    buscarEstadoRevisao,
    obterWorkspaceIdDaUrl,
    postarJson,
    encerrarSessao,
    redirecionarParaLogin
} from './review-api.js';

const MOTIVO_MAX_CARACTERES = 1000;
const AVISO_SOMENTE_MAINTAINERS = 'Somente Maintainers podem aprovar ou rejeitar';

let commandPaletteInstance = null;
let modalPayload = null;
let operacaoEmAndamento = false;
let appIniciado = false;

/**
 * Inicializa o painel com o estado vindo de GET /api/review/state.
 * O token CSRF vem dentro de `dados.csrf`.
 */
export function initApp(dados) {
    if (appIniciado) {
        aplicarEstado(dados);
        return;
    }
    appIniciado = true;

    appStore.initData(dados);

    commandPaletteInstance = new CommandPalette();

    setupSidebar();
    setupToolbar();
    setupModals();
    setupInspector();
    setupDelegatedEvents();

    appStore.subscribe('activeView', renderActiveView);
    appStore.subscribe('selectedReviewId', renderActiveView);
    appStore.subscribe('selectedDecisionId', renderActiveView);
    appStore.subscribe('selectedIncidentId', renderActiveView);
    appStore.subscribe('sidebarCollapsed', updateSidebarState);
    appStore.subscribe('inspectorOpen', updateInspectorState);
    appStore.subscribe('theme', updateThemeToggleBtn);
    appStore.subscribe('theme', rerenderSettingsIfActive);
    appStore.subscribe('inspectorOpen', rerenderSettingsIfActive);

    renderSessionChrome();
    renderActiveView();
    updateThemeToggleBtn();
}

function aplicarEstado(dados) {
    appStore.initData(dados);
    renderSessionChrome();
    renderActiveView();
}

/**
 * Refaz GET /api/review/state e reaplica o estado. Retorna true em caso de sucesso.
 */
export async function recarregarEstado() {
    const workspaceAtual = appStore.get('workspace');
    const workspaceId = (workspaceAtual && workspaceAtual.id) || obterWorkspaceIdDaUrl();
    try {
        const resultado = await buscarEstadoRevisao(workspaceId);
        if (resultado.status === 401) {
            redirecionarParaLogin();
            return false;
        }
        if (!resultado.ok) {
            mostrarToast((resultado.erro && resultado.erro.message) || `Não foi possível atualizar o estado (HTTP ${resultado.status}).`, 'erro');
            return false;
        }
        aplicarEstado(resultado.dados);
        return true;
    } catch (e) {
        mostrarToast('Erro de comunicação ao atualizar o estado do painel.', 'erro');
        return false;
    }
}

function podeRevisar() {
    const usuario = appStore.get('usuario');
    return !!(usuario && usuario.podeRevisar === true);
}

// ---------------------------------------------------------------------------
// Cabeçalho de sessão: usuário logado e seletor de workspace
// ---------------------------------------------------------------------------

function renderSessionChrome() {
    renderUserIndicator();
    renderWorkspaceSelector();
}

function renderUserIndicator() {
    const usuario = appStore.get('usuario') || {};
    const sujeitoEl = document.getElementById('user-subject');
    const papelEl = document.getElementById('user-role');
    const contenedor = document.getElementById('toolbar-user');

    if (sujeitoEl) sujeitoEl.textContent = usuario.sujeito || '—';
    if (papelEl) papelEl.textContent = usuario.papel ? String(usuario.papel) : '';
    if (contenedor) {
        const partes = [usuario.sujeito, usuario.papel, usuario.emissor].filter(Boolean).map(String);
        contenedor.title = partes.length ? `Sessão: ${partes.join(' • ')}` : 'Usuário autenticado na sessão web';
    }
}

function renderWorkspaceSelector() {
    const contenedor = document.getElementById('workspace-selector');
    if (!contenedor) return;

    const workspaces = appStore.get('workspaces') || [];
    const atual = appStore.get('workspace');
    contenedor.replaceChildren();

    if (workspaces.length > 1) {
        const select = document.createElement('select');
        select.className = 'workspace-select-dropdown';
        select.title = 'Alternar Espaço de Trabalho';
        select.setAttribute('aria-label', 'Alternar Espaço de Trabalho');
        select.dataset.action = 'switch-workspace';

        workspaces.forEach(ws => {
            if (!ws) return;
            const opcao = document.createElement('option');
            opcao.value = ws.id == null ? '' : String(ws.id);
            opcao.textContent = ws.nome || ws.slug || String(ws.id || '');
            if (atual && ws.id === atual.id) opcao.selected = true;
            select.appendChild(opcao);
        });

        contenedor.appendChild(select);
        return;
    }

    const nome = document.createElement('span');
    nome.style.fontWeight = '600';
    nome.textContent = (atual && (atual.nome || atual.slug)) || 'Workspace Principal';
    contenedor.appendChild(nome);
}

// ---------------------------------------------------------------------------
// Estrutura (sidebar, toolbar, inspector)
// ---------------------------------------------------------------------------

function setupSidebar() {
    document.querySelectorAll('[data-view-nav]').forEach(el => {
        el.addEventListener('click', () => {
            const view = el.getAttribute('data-view-nav');
            appStore.set('activeView', view);
        });
    });

    const toggleBtn = document.getElementById('sidebar-toggle-btn');
    if (toggleBtn) {
        toggleBtn.addEventListener('click', () => {
            appStore.set('sidebarCollapsed', !appStore.get('sidebarCollapsed'));
        });
    }
}

function updateSidebarState(collapsed) {
    const sidebar = document.getElementById('macos-sidebar');
    const btn = document.getElementById('sidebar-toggle-btn');
    if (sidebar) {
        sidebar.classList.toggle('collapsed', !!collapsed);
    }
    if (btn) {
        btn.classList.toggle('active', !collapsed);
    }
}

function setupToolbar() {
    const searchTrigger = document.getElementById('toolbar-search-trigger');
    if (searchTrigger) {
        searchTrigger.addEventListener('click', () => {
            if (commandPaletteInstance) commandPaletteInstance.open();
        });
    }

    const themeBtn = document.getElementById('theme-toggle-btn');
    if (themeBtn) {
        themeBtn.addEventListener('click', () => {
            const next = appStore.get('theme') === 'dark' ? 'light' : 'dark';
            appStore.set('theme', next);
        });
    }

    const inspectorBtn = document.getElementById('inspector-toggle-btn');
    if (inspectorBtn) {
        inspectorBtn.addEventListener('click', () => {
            appStore.set('inspectorOpen', !appStore.get('inspectorOpen'));
        });
    }
}

function updateThemeToggleBtn() {
    const themeBtn = document.getElementById('theme-toggle-btn');
    if (!themeBtn) return;
    const isDark = appStore.get('theme') === 'dark';
    themeBtn.innerHTML = isDark ? Icons.sun : Icons.moon;
    themeBtn.setAttribute('title', isDark ? 'Mudar para Tema Claro' : 'Mudar para Tema Escuro');
}

function rerenderSettingsIfActive() {
    if (appStore.get('activeView') === 'settings') {
        const contentBody = document.getElementById('content-body');
        if (contentBody) renderSettingsView(contentBody);
    }
}

function setupInspector() {
    updateInspectorState(appStore.get('inspectorOpen'));
}

function updateInspectorState(open) {
    const inspector = document.getElementById('macos-inspector');
    const btn = document.getElementById('inspector-toggle-btn');
    if (inspector) {
        inspector.classList.toggle('collapsed', !open);
    }
    if (btn) {
        btn.classList.toggle('active', !!open);
    }
    renderInspectorContent();
}

// ---------------------------------------------------------------------------
// Eventos delegados (substituem todos os handlers inline)
// ---------------------------------------------------------------------------

function setupDelegatedEvents() {
    document.addEventListener('click', (evento) => {
        const alvo = evento.target instanceof Element ? evento.target.closest('[data-action]') : null;
        if (!alvo) return;
        const ds = alvo.dataset;

        switch (ds.action) {
            case 'navigate':
                if (ds.view) appStore.set('activeView', ds.view);
                break;
            case 'select-review':
                appStore.set('selectedReviewId', ds.topicId || null);
                break;
            case 'select-decision':
                appStore.set('selectedDecisionId', ds.topicId || null);
                break;
            case 'select-incident':
                appStore.set('selectedIncidentId', ds.incidentId || null);
                break;
            case 'open-review':
                appStore.set('selectedReviewId', ds.topicId || null);
                appStore.set('activeView', 'reviews');
                break;
            case 'open-decision':
                appStore.set('selectedDecisionId', ds.topicId || null);
                appStore.set('activeView', 'decisions');
                break;
            case 'open-incident':
                appStore.set('selectedIncidentId', ds.incidentId || null);
                appStore.set('activeView', 'incidents');
                break;
            case 'approve':
                abrirModalAprovacao(ds.topicId, ds.proposalId);
                break;
            case 'reject':
                abrirModalRejeicao(ds.topicId, ds.proposalId);
                break;
            case 'logout':
                evento.preventDefault();
                sair(alvo);
                break;
            default:
                break;
        }
    });

    document.addEventListener('change', (evento) => {
        const alvo = evento.target instanceof Element ? evento.target.closest('[data-action]') : null;
        if (!alvo) return;

        switch (alvo.dataset.action) {
            case 'toggle-theme':
                appStore.set('theme', alvo.checked ? 'dark' : 'light');
                break;
            case 'toggle-inspector':
                appStore.set('inspectorOpen', !!alvo.checked);
                break;
            case 'switch-workspace':
                if (alvo.value) {
                    window.location.href = '/review?workspaceId=' + encodeURIComponent(alvo.value);
                }
                break;
            default:
                break;
        }
    });

    // Acessibilidade: linhas clicáveis (role="button") respondem a Enter/Espaço.
    document.addEventListener('keydown', (evento) => {
        if (evento.key !== 'Enter' && evento.key !== ' ') return;
        const alvo = evento.target;
        if (!(alvo instanceof Element) || alvo.getAttribute('role') !== 'button' || !alvo.hasAttribute('data-action')) return;
        evento.preventDefault();
        alvo.click();
    });
}

async function sair(botao) {
    if (botao.disabled) return;
    botao.disabled = true;
    try {
        const ok = await encerrarSessao(appStore.get('csrf'));
        if (ok) {
            window.location.href = '/';
            return;
        }
        mostrarToast('Não foi possível encerrar a sessão.', 'erro');
    } catch (e) {
        mostrarToast('Erro de comunicação ao encerrar a sessão.', 'erro');
    }
    botao.disabled = false;
}

// ---------------------------------------------------------------------------
// Helpers de renderização
// ---------------------------------------------------------------------------

function renderEvidencias(lista, mensagemVazia) {
    const itens = Array.isArray(lista) ? lista.filter(Boolean) : [];
    if (itens.length === 0) {
        return `<div style="color:var(--text-tertiary);font-size:0.84rem;">${escapeHtml(mensagemVazia)}</div>`;
    }

    return itens.map(ev => {
        const href = urlHttpsSegura(ev.url);
        const esquerda = `
            <div class="evidence-row-left">
                <span class="evidence-type-badge">${escapeHtml(ev.tipo)}</span>
                <span>${escapeHtml(ev.rotulo || ev.url || 'Evidência')}</span>
            </div>`;

        if (href) {
            return `
                <a href="${escapeHtml(href)}" target="_blank" rel="noopener noreferrer" class="evidence-link-row">
                    ${esquerda}
                    <span style="color:var(--text-tertiary);">${Icons.externalLink}</span>
                </a>`;
        }

        // URL ausente ou não-https: exibida apenas como texto (sem link).
        return `
            <div class="evidence-link-row evidence-text-row" title="Somente URLs https:// são exibidas como link.">
                ${esquerda}
                ${ev.url ? `<span class="evidence-raw-url">${escapeHtml(ev.url)}</span>` : ''}
            </div>`;
    }).join('');
}

function renderChavesTecnicas(lista) {
    const chaves = Array.isArray(lista) ? lista.filter(k => k !== null && k !== undefined && k !== '') : [];
    if (chaves.length === 0) return '';
    return chaves.map(k => `
        <span style="font-family:var(--font-mono);font-size:0.74rem;background:var(--code-bg);padding:2px 7px;border-radius:4px;color:var(--text-secondary);">${escapeHtml(k)}</span>
    `).join(' ');
}

function renderSecaoChaves(lista) {
    const html = renderChavesTecnicas(lista);
    if (!html) return '';
    return `
        <div class="doc-section">
            <div class="doc-section-title">Chaves Técnicas e Identificadores</div>
            <div style="display:flex;flex-wrap:wrap;gap:6px;">${html}</div>
        </div>
    `;
}

function renderSecaoIncidentesRelacionados(ids) {
    const lista = Array.isArray(ids) ? ids.filter(Boolean) : [];
    if (lista.length === 0) return '';

    const incidentes = appStore.get('incidentes') || [];
    const linhas = lista.map(id => {
        const incidente = incidentes.find(i => i && i.incidenteId === id);
        if (incidente) {
            return `
                <div class="evidence-link-row" role="button" tabindex="0" style="cursor:pointer;" data-action="open-incident" data-incident-id="${escapeHtml(id)}">
                    <div class="evidence-row-left">
                        <span class="evidence-type-badge">incidente</span>
                        <span>${escapeHtml(incidente.titulo)}</span>
                    </div>
                    <span style="color:var(--text-tertiary);">${Icons.chevronRight}</span>
                </div>`;
        }
        return `
            <div class="evidence-link-row evidence-text-row">
                <div class="evidence-row-left">
                    <span class="evidence-type-badge">incidente</span>
                    <span style="font-family:var(--font-mono);font-size:0.78rem;">${escapeHtml(id)}</span>
                </div>
            </div>`;
    }).join('');

    return `
        <div class="doc-section">
            <div class="doc-section-title">Incidentes Relacionados</div>
            <div class="evidence-table">${linhas}</div>
        </div>
    `;
}

const SITUACAO_DECISAO = {
    proposed: { rotulo: 'Proposta', classe: 'proposed-state' },
    active: { rotulo: 'Decisão Vigente Ativa', classe: 'active-state' },
    superseded: { rotulo: 'Substituída', classe: 'reported-state' },
    rejected: { rotulo: 'Rejeitada', classe: 'rejected-state' }
};

const SITUACAO_INCIDENTE = {
    reported: { rotulo: 'Incidente Reportado', classe: 'reported-state' }
};

function pillSituacao(mapa, situacao, padrao) {
    const chave = typeof situacao === 'string' ? situacao.toLowerCase() : '';
    const info = mapa[chave] || (situacao ? { rotulo: String(situacao), classe: 'reported-state' } : padrao);
    return `<span class="status-pill ${info.classe}">${escapeHtml(info.rotulo)}</span>`;
}

function estadoVazio(titulo, descricaoHtml, iconeHtml = '') {
    return `
        <div style="flex:1;display:flex;align-items:center;justify-content:center;padding:2rem;">
            <div style="text-align:center;max-width:380px;">
                ${iconeHtml ? `<div style="width:48px;height:48px;border-radius:12px;background:var(--bg-surface-secondary);display:flex;align-items:center;justify-content:center;margin:0 auto 1rem;color:var(--text-tertiary);">${iconeHtml}</div>` : ''}
                <h3 style="margin:0 0 0.5rem;font-size:1.1rem;color:var(--text-primary);font-weight:600;">${escapeHtml(titulo)}</h3>
                <p style="margin:0;font-size:0.86rem;color:var(--text-secondary);line-height:1.5;">${descricaoHtml}</p>
            </div>
        </div>
    `;
}

// ---------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------

function renderActiveView() {
    const view = appStore.get('activeView');
    const contentBody = document.getElementById('content-body');
    const toolbarTitle = document.getElementById('toolbar-view-title');

    document.querySelectorAll('[data-view-nav]').forEach(el => {
        el.classList.toggle('active', el.getAttribute('data-view-nav') === view);
    });

    const revCountEl = document.getElementById('count-reviews');
    if (revCountEl) revCountEl.textContent = String(appStore.get('propostas').length);
    const decCountEl = document.getElementById('count-decisions');
    if (decCountEl) decCountEl.textContent = String(appStore.get('decisoes').length);
    const incCountEl = document.getElementById('count-incidents');
    if (incCountEl) incCountEl.textContent = String(appStore.get('incidentes').length);

    if (!contentBody) return;

    if (view === 'overview') {
        if (toolbarTitle) toolbarTitle.textContent = 'Visão Geral';
        renderOverviewView(contentBody);
    } else if (view === 'reviews') {
        if (toolbarTitle) toolbarTitle.textContent = 'Revisões de Arquitetura';
        renderReviewsView(contentBody);
    } else if (view === 'decisions') {
        if (toolbarTitle) toolbarTitle.textContent = 'Decisões Vigentes';
        renderDecisionsView(contentBody);
    } else if (view === 'incidents') {
        if (toolbarTitle) toolbarTitle.textContent = 'Incidentes Reportados';
        renderIncidentsView(contentBody);
    } else if (view === 'timeline') {
        if (toolbarTitle) toolbarTitle.textContent = 'Histórico de Eventos';
        renderTimelineView(contentBody);
    } else if (view === 'mcp') {
        if (toolbarTitle) toolbarTitle.textContent = 'Integrações MCP';
        renderMcpView(contentBody);
    } else if (view === 'settings') {
        if (toolbarTitle) toolbarTitle.textContent = 'Configurações do Sistema';
        renderSettingsView(contentBody);
    }

    renderInspectorContent();
}

function renderOverviewView(container) {
    const propostas = appStore.get('propostas');
    const decisoes = appStore.get('decisoes');
    const ws = appStore.get('workspace');
    const wsNome = ws ? (ws.nome || ws.slug) : 'Workspace Principal';

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Espaço: ${escapeHtml(wsNome)}</div>
                <h1 class="overview-title">Governança Arquitetural Ativa</h1>
                <p class="overview-desc">Consolidação contínua de decisões de arquitetura e relatos de incidentes com proveniência imutável e controle de concorrência.</p>
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Aguardando Validação Humana (${propostas.length})</div>
                    <button type="button" class="btn-macos btn-macos-secondary" data-action="navigate" data-view="reviews">Ver Todas</button>
                </div>
                ${propostas.length === 0 ? `
                    <div style="padding: 1.5rem; text-align: center; color: var(--text-tertiary); font-size: 0.88rem;">
                        Nenhuma proposta aguardando validação no momento.
                    </div>
                ` : propostas.map(p => `
                    <div class="overview-list-row" role="button" tabindex="0" data-action="open-review" data-topic-id="${escapeHtml(p.topicoId)}">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(p.titulo)}</span>
                            <span class="overview-row-sub">tópico: ${escapeHtml(p.chaveTopico)} • v${escapeHtml(p.versaoFluxo)}</span>
                        </div>
                        <div class="overview-row-right">
                            <span class="status-pill proposed-state">Revisão Necessária</span>
                            <span style="color:var(--text-tertiary);">${Icons.chevronRight}</span>
                        </div>
                    </div>
                `).join('')}
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Decisões Vigentes Ativas (${decisoes.length})</div>
                    <button type="button" class="btn-macos btn-macos-secondary" data-action="navigate" data-view="decisions">Ver Todas</button>
                </div>
                ${decisoes.length === 0 ? `
                    <div style="padding: 1.5rem; text-align: center; color: var(--text-tertiary); font-size: 0.88rem;">
                        Nenhuma decisão vigente consolidada no momento.
                    </div>
                ` : decisoes.map(d => `
                    <div class="overview-list-row" role="button" tabindex="0" data-action="open-decision" data-topic-id="${escapeHtml(d.topicoId)}">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(d.titulo)}</span>
                            <span class="overview-row-sub">tópico: ${escapeHtml(d.chaveTopico)} • versão ${escapeHtml(d.versaoFluxo)}</span>
                        </div>
                        <div class="overview-row-right">
                            ${pillSituacao(SITUACAO_DECISAO, d.situacao, SITUACAO_DECISAO.active)}
                            <span style="color:var(--text-tertiary);">${Icons.chevronRight}</span>
                        </div>
                    </div>
                `).join('')}
            </div>
        </div>
    `;
}

function renderReviewsView(container) {
    const propostas = appStore.get('propostas');
    const selectedId = appStore.get('selectedReviewId');
    const selected = propostas.find(p => p.topicoId === selectedId) || propostas[0];

    if (propostas.length === 0) {
        container.innerHTML = estadoVazio(
            'Nenhuma proposta pendente',
            'Todas as propostas submetidas por agentes via MCP foram validadas e consolidadas.',
            Icons.reviews
        );
        return;
    }

    container.innerHTML = `
        <div class="split-view">
            <div class="split-pane-list">
                <div style="font-size:0.72rem;font-weight:600;text-transform:uppercase;color:var(--text-tertiary);letter-spacing:0.04em;padding:4px 8px 8px;">
                    Propostas (${propostas.length})
                </div>
                ${propostas.map(p => {
                    const isSel = selected && p.topicoId === selected.topicoId;
                    return `
                        <div class="record-list-item ${isSel ? 'selected' : ''}" role="button" tabindex="0" data-action="select-review" data-topic-id="${escapeHtml(p.topicoId)}">
                            <div class="record-item-top">
                                <span class="record-item-topic">${escapeHtml(p.chaveTopico)}</span>
                                <span class="record-item-version">v${escapeHtml(p.versaoFluxo)}</span>
                            </div>
                            <div class="record-item-title">${escapeHtml(p.titulo)}</div>
                            <div class="record-item-summary">${escapeHtml(p.resumo)}</div>
                        </div>
                    `;
                }).join('')}
            </div>

            <div class="split-pane-detail">
                ${selected ? renderProposalDetail(selected) : ''}
            </div>
        </div>
    `;
}

function renderProposalDetail(p) {
    const vigente = p.decisaoVigente && typeof p.decisaoVigente === 'object' ? p.decisaoVigente : null;
    const autor = escapeHtml(p.autorSujeito || 'autor não informado');
    const cliente = p.clienteId ? `<code>${escapeHtml(p.clienteId)}</code>` : 'cliente não informado';
    const quando = escapeHtml(formatarData(p.propostaEm, 'data não informada'));

    const secaoVigente = vigente ? `
        <div class="doc-section">
            <div class="doc-section-title">Decisão vigente que será substituída</div>
            <div class="superseded-decision-box">
                <div class="superseded-decision-title">${escapeHtml(vigente.titulo)}</div>
                <div>${escapeHtml(vigente.resumo)}</div>
                <div style="margin-top:8px;font-family:var(--font-mono);font-size:0.72rem;color:var(--text-tertiary);">
                    registro vigente: v${escapeHtml(vigente.versaoRegistroAtivo)}${vigente.registroAtivoId ? ` • ${escapeHtml(vigente.registroAtivoId)}` : ''}
                </div>
            </div>
        </div>
    ` : '';

    const temIdsValidos = !!(p.topicoId && p.propostaPendenteId);
    const acoes = podeRevisar() ? `
        <div class="doc-actions-bar">
            <button type="button" class="btn-macos btn-macos-primary" data-action="approve" data-topic-id="${escapeHtml(p.topicoId)}" data-proposal-id="${escapeHtml(p.propostaPendenteId)}" ${temIdsValidos ? '' : 'disabled'}>
                ${Icons.check} Aprovar Decisão
            </button>
            <button type="button" class="btn-macos btn-macos-danger" data-action="reject" data-topic-id="${escapeHtml(p.topicoId)}" data-proposal-id="${escapeHtml(p.propostaPendenteId)}" ${temIdsValidos ? '' : 'disabled'}>
                ${Icons.x} Rejeitar Proposta
            </button>
        </div>
    ` : `
        <div class="doc-actions-bar">
            <div class="review-permission-notice" role="note">${Icons.shieldCheck} ${escapeHtml(AVISO_SOMENTE_MAINTAINERS)}</div>
        </div>
    `;

    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                <span class="status-pill proposed-state">Revisão Humana Obrigatória</span>
                ${vigente ? `<span class="status-pill reported-state">Substitui decisão vigente v${escapeHtml(vigente.versaoRegistroAtivo)}</span>` : '<span class="status-pill reported-state">Primeira decisão do tópico</span>'}
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">tópico: ${escapeHtml(p.chaveTopico)}</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">versão do fluxo: ${escapeHtml(p.versaoFluxo)}</span>
            </div>
            <h1 class="doc-title">${escapeHtml(p.titulo)}</h1>
            <div class="doc-timestamp">Proposto por <strong>${autor}</strong> via ${cliente} em ${quando}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Resumo Proposto</div>
            <div class="doc-content-box">${escapeHtml(p.resumo)}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Justificativa Técnica</div>
            <div class="doc-content-box">${escapeHtml(p.justificativa || 'Nenhuma justificativa técnica detalhada fornecida.')}</div>
        </div>

        ${renderSecaoChaves(p.chavesTecnicas)}

        ${renderSecaoIncidentesRelacionados(p.incidentesRelacionadosIds)}

        <div class="doc-section">
            <div class="doc-section-title">Evidências e Documentos Vinculados</div>
            <div class="evidence-table">${renderEvidencias(p.evidencias, 'Nenhuma evidência anexada.')}</div>
        </div>

        ${secaoVigente}

        ${acoes}
    `;
}

function renderDecisionsView(container) {
    const decisoes = appStore.get('decisoes');
    const selectedId = appStore.get('selectedDecisionId');
    const selected = decisoes.find(d => d.topicoId === selectedId) || decisoes[0];

    if (decisoes.length === 0) {
        container.innerHTML = estadoVazio(
            'Nenhuma decisão vigente',
            'Não há decisões ativas consolidadas neste espaço de trabalho.'
        );
        return;
    }

    container.innerHTML = `
        <div class="split-view">
            <div class="split-pane-list">
                <div style="font-size:0.72rem;font-weight:600;text-transform:uppercase;color:var(--text-tertiary);letter-spacing:0.04em;padding:4px 8px 8px;">
                    Decisões Vigentes (${decisoes.length})
                </div>
                ${decisoes.map(d => {
                    const isSel = selected && d.topicoId === selected.topicoId;
                    return `
                        <div class="record-list-item ${isSel ? 'selected' : ''}" role="button" tabindex="0" data-action="select-decision" data-topic-id="${escapeHtml(d.topicoId)}">
                            <div class="record-item-top">
                                <span class="record-item-topic">${escapeHtml(d.chaveTopico)}</span>
                                <span class="record-item-version">v${escapeHtml(d.versaoFluxo)}</span>
                            </div>
                            <div class="record-item-title">${escapeHtml(d.titulo)}</div>
                            <div class="record-item-summary">${escapeHtml(d.resumo)}</div>
                        </div>
                    `;
                }).join('')}
            </div>

            <div class="split-pane-detail">
                ${selected ? renderDecisionDetail(selected) : ''}
            </div>
        </div>
    `;
}

function renderDecisionDetail(d) {
    const avisoPendente = d.propostaPendenteId ? `
        <div class="doc-section">
            <div class="evidence-link-row" role="button" tabindex="0" style="cursor:pointer;" data-action="open-review" data-topic-id="${escapeHtml(d.topicoId)}">
                <div class="evidence-row-left">
                    <span class="status-pill proposed-state">Proposta pendente</span>
                    <span>Há uma proposta aguardando revisão para este tópico.</span>
                </div>
                <span style="color:var(--text-tertiary);">${Icons.chevronRight}</span>
            </div>
        </div>
    ` : '';

    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                ${pillSituacao(SITUACAO_DECISAO, d.situacao, SITUACAO_DECISAO.active)}
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">tópico: ${escapeHtml(d.chaveTopico)}</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">versão ${escapeHtml(d.versaoFluxo)}</span>
                ${d.versaoRegistroAtivo !== undefined && d.versaoRegistroAtivo !== null ? `<span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">registro v${escapeHtml(d.versaoRegistroAtivo)}</span>` : ''}
            </div>
            <h1 class="doc-title">${escapeHtml(d.titulo)}</h1>
            <div class="doc-timestamp">Última atualização registrada: ${escapeHtml(formatarData(d.atualizadoEm, 'Data não disponível'))}</div>
        </div>

        ${avisoPendente}

        <div class="doc-section">
            <div class="doc-section-title">Resumo Arquitetural</div>
            <div class="doc-content-box">${escapeHtml(d.resumo)}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Justificativa e Consequências</div>
            <div class="doc-content-box">${escapeHtml(d.justificativa || 'Decisão arquitetural consolidada pela equipe de engenharia.')}</div>
        </div>

        ${renderSecaoChaves(d.chavesTecnicas)}

        ${renderSecaoIncidentesRelacionados(d.incidentesRelacionadosIds)}

        <div class="doc-section">
            <div class="doc-section-title">Evidências e Histórico Formal</div>
            <div class="evidence-table">${renderEvidencias(d.evidencias, 'Nenhuma evidência anexada.')}</div>
        </div>
    `;
}

function renderIncidentsView(container) {
    const incidentes = appStore.get('incidentes');
    const selectedId = appStore.get('selectedIncidentId');
    const selected = incidentes.find(i => i.incidenteId === selectedId) || incidentes[0];

    if (incidentes.length === 0) {
        container.innerHTML = estadoVazio(
            'Nenhum incidente registrado',
            'Nenhum relato de incidente foi submetido via ferramenta <code>log_incident</code>.'
        );
        return;
    }

    container.innerHTML = `
        <div class="split-view">
            <div class="split-pane-list">
                <div style="font-size:0.72rem;font-weight:600;text-transform:uppercase;color:var(--text-tertiary);letter-spacing:0.04em;padding:4px 8px 8px;">
                    Incidentes (${incidentes.length})
                </div>
                ${incidentes.map(i => {
                    const isSel = selected && i.incidenteId === selected.incidenteId;
                    return `
                        <div class="record-list-item ${isSel ? 'selected' : ''}" role="button" tabindex="0" data-action="select-incident" data-incident-id="${escapeHtml(i.incidenteId)}">
                            <div class="record-item-top">
                                <span class="record-item-topic">${escapeHtml(String(i.incidenteId || '').slice(0, 8))}...</span>
                                <span class="record-item-version">v${escapeHtml(i.versaoFluxo)}</span>
                            </div>
                            <div class="record-item-title">${escapeHtml(i.titulo)}</div>
                            <div class="record-item-summary">${escapeHtml(i.resumo)}</div>
                        </div>
                    `;
                }).join('')}
            </div>

            <div class="split-pane-detail">
                ${selected ? renderIncidentDetail(selected) : ''}
            </div>
        </div>
    `;
}

function renderIncidentDetail(inc) {
    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                ${pillSituacao(SITUACAO_INCIDENTE, inc.situacao, SITUACAO_INCIDENTE.reported)}
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">id: ${escapeHtml(inc.incidenteId)}</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">versão ${escapeHtml(inc.versaoFluxo)}</span>
            </div>
            <h1 class="doc-title">${escapeHtml(inc.titulo)}</h1>
            <div class="doc-timestamp">Ocorrido em: ${escapeHtml(formatarData(inc.ocorridoEm, 'Data não informada'))}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Relato do Incidente</div>
            <div class="doc-content-box">${escapeHtml(inc.resumo)}</div>
        </div>

        ${renderSecaoChaves(inc.chavesTecnicas)}

        <div class="doc-section">
            <div class="doc-section-title">Evidências (RCA / Pull Requests)</div>
            <div class="evidence-table">${renderEvidencias(inc.evidencias, 'Nenhuma evidência vinculada.')}</div>
        </div>
    `;
}

const TIPO_EVENTO = {
    DecisionProposed: { rotulo: 'Proposta', classe: 'proposed-state' },
    DecisionApproved: { rotulo: 'Aprovado', classe: 'active-state' },
    DecisionRejected: { rotulo: 'Rejeitado', classe: 'rejected-state' },
    IncidentReported: { rotulo: 'Incidente', classe: 'reported-state' }
};

function classificarEvento(tipo) {
    const texto = typeof tipo === 'string' ? tipo : '';
    if (TIPO_EVENTO[texto]) return TIPO_EVENTO[texto];
    if (texto.includes('Approved')) return TIPO_EVENTO.DecisionApproved;
    if (texto.includes('Rejected')) return TIPO_EVENTO.DecisionRejected;
    if (texto.includes('Incident')) return TIPO_EVENTO.IncidentReported;
    return { rotulo: 'Evento', classe: 'reported-state' };
}

function renderTimelineView(container) {
    const eventos = appStore.get('eventos') || [];
    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Trilha Auditável Event Sourced</div>
                <h1 class="overview-title">Histórico de Eventos Imutáveis</h1>
                <p class="overview-desc">Registro permanente e append-only de todas as transações, propostas e validações ocorridas neste espaço de trabalho.</p>
            </div>
            <div class="overview-section">
                <div class="overview-section-title">Eventos Recentes (${eventos.length})</div>
                ${eventos.length === 0 ? `
                    <div style="padding: 2.5rem; text-align: center; color: var(--text-tertiary); font-size: 0.88rem;">
                        Nenhum evento registrado no histórico do workspace no momento.
                    </div>
                ` : eventos.map(ev => {
                    const info = classificarEvento(ev.tipo);
                    return `
                        <div class="overview-list-row" style="cursor:default;">
                            <div class="overview-row-left">
                                <div style="display:flex;align-items:center;gap:8px;margin-bottom:3px;">
                                    <span class="status-pill ${info.classe}">${escapeHtml(info.rotulo)}</span>
                                    <span class="overview-row-title">${escapeHtml(ev.tipo)}</span>
                                    <span style="font-family:var(--font-mono);font-size:0.72rem;color:var(--text-tertiary);">v${escapeHtml(ev.versaoFluxo)}</span>
                                </div>
                                <span class="overview-row-sub">Autor: ${escapeHtml(ev.autorSujeito)} • Cliente: ${escapeHtml(ev.clienteId || 'sessão web')}</span>
                            </div>
                            <div class="overview-row-right">
                                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">${escapeHtml(formatarData(ev.registradoEm))}</span>
                            </div>
                        </div>
                    `;
                }).join('')}
            </div>
        </div>
    `;
}

function renderMcpView(container) {
    const tools = [
        { name: 'propose_decision', desc: 'Submete uma proposta arquitetural para um tópico com evidências e justificativa.' },
        { name: 'log_incident', desc: 'Registra o relato de um incidente com referências técnicas, RCA e correções.' },
        { name: 'query_context', desc: 'Recupera decisões vigentes e incidentes relevantes com busca textual e emite um comprovante da consulta.' },
        { name: 'get_timeline', desc: 'Consulta a linha do tempo cronológica de eventos de um tópico de decisão ou incidente.' },
        { name: 'get_context_receipt', desc: 'Recupera o comprovante de uma consulta: versões dos registros entregues e digest SHA-256 do conteúdo.' }
    ];

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Protocolo Streamable HTTP</div>
                <h1 class="overview-title">Servidor MCP Ativo</h1>
                <p class="overview-desc">Endpoint oficial exposto em <code>/mcp</code> com autenticação Bearer e isolamento por workspace.</p>
            </div>
            <div class="overview-section">
                <div class="overview-section-title">Ferramentas Disponíveis (${tools.length})</div>
                ${tools.map(t => `
                    <div class="mcp-tool-card">
                        <div class="mcp-tool-header">
                            <span class="mcp-tool-name">${escapeHtml(t.name)}</span>
                            <span style="font-size:0.7rem;color:var(--status-success);font-weight:600;">Ativo</span>
                        </div>
                        <p class="mcp-tool-desc">${escapeHtml(t.desc)}</p>
                    </div>
                `).join('')}
            </div>
        </div>
    `;
}

function renderSettingsView(container) {
    const isDark = appStore.get('theme') === 'dark';
    const isInspector = appStore.get('inspectorOpen');
    const usuario = appStore.get('usuario') || {};

    container.innerHTML = `
        <div class="settings-view">
            <h1 class="overview-title" style="margin-bottom: 2rem;">Preferências do Sistema</h1>

            <div class="settings-group">
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Modo Escuro (Dark Mode)</span>
                        <span class="settings-caption">Ajusta o contraste do sistema entre o tema escuro e claro.</span>
                    </div>
                    <label class="macos-switch">
                        <input type="checkbox" id="switch-theme" data-action="toggle-theme" ${isDark ? 'checked' : ''}>
                        <span class="macos-slider"></span>
                    </label>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Painel de Inspeção</span>
                        <span class="settings-caption">Exibe metadados técnicos e recibos à direita da janela principal.</span>
                    </div>
                    <label class="macos-switch">
                        <input type="checkbox" id="switch-inspector" data-action="toggle-inspector" ${isInspector ? 'checked' : ''}>
                        <span class="macos-slider"></span>
                    </label>
                </div>
            </div>

            <div class="settings-group">
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Identidade do Revisor Humano</span>
                        <span class="settings-caption">Usuário autenticado na sessão web atual.</span>
                    </div>
                    <span style="font-family:var(--font-mono);font-size:0.8rem;color:var(--text-secondary);">${escapeHtml(usuario.sujeito || '—')}</span>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Papel no Espaço de Trabalho</span>
                        <span class="settings-caption">${escapeHtml(podeRevisar() ? 'Pode aprovar e rejeitar propostas.' : AVISO_SOMENTE_MAINTAINERS + '.')}</span>
                    </div>
                    <span style="font-family:var(--font-mono);font-size:0.8rem;color:var(--text-secondary);">${escapeHtml(usuario.papel || '—')}</span>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Provedor de Identidade (Issuer)</span>
                        <span class="settings-caption">Serviço emissor de tokens de segurança.</span>
                    </div>
                    <span style="font-family:var(--font-mono);font-size:0.8rem;color:var(--text-secondary);">${escapeHtml(usuario.emissor || '—')}</span>
                </div>
            </div>
        </div>
    `;
}

function campoInspector(rotulo, valor, codigo = false) {
    return `
        <div class="inspector-field">
            <div class="inspector-field-label">${escapeHtml(rotulo)}</div>
            <div class="inspector-field-val${codigo ? ' code' : ''}">${escapeHtml(valor)}</div>
        </div>
    `;
}

function renderInspectorContent() {
    const inspector = document.getElementById('macos-inspector');
    if (!inspector || inspector.classList.contains('collapsed')) return;

    const view = appStore.get('activeView');
    let item = null;
    let tipo = '';

    if (view === 'reviews') {
        const id = appStore.get('selectedReviewId');
        item = appStore.get('propostas').find(p => p.topicoId === id);
        tipo = 'Proposta Arquitetural';
    } else if (view === 'decisions') {
        const id = appStore.get('selectedDecisionId');
        item = appStore.get('decisoes').find(d => d.topicoId === id);
        tipo = 'Decisão Vigente';
    } else if (view === 'incidents') {
        const id = appStore.get('selectedIncidentId');
        item = appStore.get('incidentes').find(i => i.incidenteId === id);
        tipo = 'Relato de Incidente';
    }

    if (!item) {
        inspector.innerHTML = `
            <div style="font-size:0.75rem;font-weight:600;text-transform:uppercase;color:var(--text-tertiary);letter-spacing:0.04em;margin-bottom:1rem;">
                Inspetor de Metadados
            </div>
            <div style="color:var(--text-tertiary);font-size:0.82rem;line-height:1.5;">
                Selecione um registro na lista para visualizar seus metadados técnicos e proveniência.
            </div>
        `;
        return;
    }

    const ehProposta = view === 'reviews';

    inspector.innerHTML = `
        <div style="font-size:0.75rem;font-weight:600;text-transform:uppercase;color:var(--text-tertiary);letter-spacing:0.04em;margin-bottom:1.25rem;">
            Inspetor de Metadados
        </div>

        ${campoInspector('Tipo de Entidade', tipo)}
        ${campoInspector('Identificador do Tópico / Fluxo', item.topicoId || item.incidenteId, true)}
        ${item.chaveTopico ? campoInspector('Chave Canônica', item.chaveTopico, true) : ''}
        ${campoInspector('Versão do Fluxo', `v${item.versaoFluxo ?? '—'}`, true)}
        ${ehProposta && item.propostaPendenteId ? campoInspector('Proposta Pendente', item.propostaPendenteId, true) : ''}
        ${ehProposta ? campoInspector('Autor', item.autorSujeito || '—', true) : ''}
        ${ehProposta ? campoInspector('Cliente MCP', item.clienteId || 'não informado', true) : ''}
        ${ehProposta ? campoInspector('Proposto em', formatarData(item.propostaEm, '—')) : ''}
        ${campoInspector('Espaço de Trabalho', item.espacoTrabalhoId, true)}
        ${campoInspector('Evidências Anexadas', `${(Array.isArray(item.evidencias) ? item.evidencias : []).length} documento(s)`)}

        <div class="inspector-field">
            <div class="inspector-field-label">Garantia Arquitetural</div>
            <div style="display:flex;align-items:center;gap:6px;font-size:0.8rem;color:var(--status-success);margin-top:2px;">
                ${Icons.shieldCheck} Isolamento RLS &amp; Event Sourced
            </div>
        </div>
    `;
}

// ---------------------------------------------------------------------------
// Modal de aprovação / rejeição
// ---------------------------------------------------------------------------

function setupModals() {
    const backdrop = document.getElementById('modal-dialog');
    if (backdrop) {
        backdrop.addEventListener('click', (e) => {
            if (e.target === backdrop) fecharModal();
        });
    }

    const cancelBtn = document.getElementById('modal-cancel-btn');
    if (cancelBtn) {
        cancelBtn.addEventListener('click', fecharModal);
    }

    const confirmBtn = document.getElementById('modal-confirm-btn');
    if (confirmBtn) {
        confirmBtn.addEventListener('click', confirmarOperacaoModal);
    }

    const motivoInput = document.getElementById('modal-reason-input');
    if (motivoInput) {
        motivoInput.addEventListener('input', () => atualizarDicaMotivo(null));
    }

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && modalPayload) fecharModal();
    });
}

function encontrarProposta(topicoId, propostaId) {
    if (!topicoId || !propostaId) return null;
    return (appStore.get('propostas') || []).find(p => p.topicoId === topicoId && p.propostaPendenteId === propostaId) || null;
}

function prepararPayload(tipo, topicoId, propostaId) {
    if (!podeRevisar()) {
        mostrarToast(AVISO_SOMENTE_MAINTAINERS + '.', 'erro');
        return null;
    }
    if (operacaoEmAndamento) return null;

    const proposta = encontrarProposta(topicoId, propostaId);
    if (!proposta) {
        mostrarToast('Proposta não encontrada no estado atual. Recarregando...', 'erro');
        recarregarEstado();
        return null;
    }

    return {
        tipo,
        proposta,
        workspaceId: proposta.espacoTrabalhoId,
        projectId: proposta.projetoId,
        topicId: proposta.topicoId,
        proposalId: proposta.propostaPendenteId,
        expectedVersion: proposta.versaoFluxo,
        // Uma chave por abertura do modal: cliques repetidos e novas tentativas após falha de rede reutilizam a mesma chave.
        idempotencyKey: gerarChaveIdempotencia()
    };
}

function abrirModalAprovacao(topicoId, propostaId) {
    const payload = prepararPayload('aprovar', topicoId, propostaId);
    if (!payload) return;
    modalPayload = payload;

    const vigente = payload.proposta.decisaoVigente;
    const chave = payload.proposta.chaveTopico || 'deste tópico';
    document.getElementById('modal-title').textContent = 'Aprovar Decisão';
    document.getElementById('modal-desc').textContent = vigente
        ? `Esta proposta será promovida a decisão vigente do tópico "${chave}", substituindo a decisão atual "${vigente.titulo || 'sem título'}". Deseja confirmar?`
        : `Esta proposta será promovida a decisão vigente do tópico "${chave}". Deseja confirmar?`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-primary';
    btn.textContent = 'Confirmar Aprovação';
    definirModalOcupado(false);
    document.getElementById('modal-dialog').classList.add('visible');
    setTimeout(() => btn.focus(), 50);
}

function abrirModalRejeicao(topicoId, propostaId) {
    const payload = prepararPayload('rejeitar', topicoId, propostaId);
    if (!payload) return;
    modalPayload = payload;

    document.getElementById('modal-title').textContent = 'Rejeitar Proposta';
    document.getElementById('modal-desc').textContent = 'Informe a justificativa técnica para rejeitar esta proposta (obrigatório, até 1000 caracteres).';
    document.getElementById('modal-input-wrap').style.display = 'block';
    const input = document.getElementById('modal-reason-input');
    input.value = '';
    atualizarDicaMotivo(null);

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-danger';
    btn.textContent = 'Rejeitar Proposta';
    definirModalOcupado(false);
    document.getElementById('modal-dialog').classList.add('visible');
    setTimeout(() => input.focus(), 50);
}

function validarMotivo(motivo) {
    if (!motivo) return 'Informe o motivo da rejeição.';
    if (motivo.length > MOTIVO_MAX_CARACTERES) return `O motivo deve ter no máximo ${MOTIVO_MAX_CARACTERES} caracteres.`;
    return null;
}

function atualizarDicaMotivo(mensagemErro) {
    const dica = document.getElementById('modal-reason-hint');
    const input = document.getElementById('modal-reason-input');
    if (!dica || !input) return;
    const tamanho = (input.value || '').trim().length;
    const excedeu = tamanho > MOTIVO_MAX_CARACTERES;
    dica.textContent = mensagemErro || `${tamanho}/${MOTIVO_MAX_CARACTERES}`;
    dica.classList.toggle('error', !!mensagemErro || excedeu);
}

function definirModalOcupado(ocupado) {
    operacaoEmAndamento = ocupado;
    const confirmBtn = document.getElementById('modal-confirm-btn');
    const cancelBtn = document.getElementById('modal-cancel-btn');
    const input = document.getElementById('modal-reason-input');
    if (confirmBtn) {
        confirmBtn.disabled = ocupado;
        confirmBtn.setAttribute('aria-busy', ocupado ? 'true' : 'false');
    }
    if (cancelBtn) cancelBtn.disabled = ocupado;
    if (input) input.readOnly = ocupado;
}

function fecharModal() {
    if (operacaoEmAndamento) return;
    const dialog = document.getElementById('modal-dialog');
    if (dialog) dialog.classList.remove('visible');
    modalPayload = null;
}

async function confirmarOperacaoModal() {
    if (!modalPayload || operacaoEmAndamento) return;
    const p = modalPayload;

    let url;
    let corpo;
    let mensagemSucesso;

    if (p.tipo === 'aprovar') {
        url = '/api/review/approve';
        corpo = {
            workspaceId: p.workspaceId,
            projectId: p.projectId,
            topicId: p.topicId,
            proposalId: p.proposalId,
            expectedVersion: p.expectedVersion,
            idempotencyKey: p.idempotencyKey
        };
        mensagemSucesso = 'Decisão aprovada e consolidada como vigente.';
    } else if (p.tipo === 'rejeitar') {
        const input = document.getElementById('modal-reason-input');
        const motivo = (input && input.value ? input.value : '').trim();
        const erroMotivo = validarMotivo(motivo);
        if (erroMotivo) {
            atualizarDicaMotivo(erroMotivo);
            if (input) input.focus();
            return;
        }
        url = '/api/review/reject';
        corpo = {
            workspaceId: p.workspaceId,
            projectId: p.projectId,
            topicId: p.topicId,
            proposalId: p.proposalId,
            expectedVersion: p.expectedVersion,
            reason: motivo,
            idempotencyKey: p.idempotencyKey
        };
        mensagemSucesso = 'Proposta de decisão rejeitada com sucesso.';
    } else {
        return;
    }

    definirModalOcupado(true);
    let resultado;
    try {
        resultado = await postarJson(url, corpo, appStore.get('csrf'));
    } catch (e) {
        // Falha de rede: mantém o modal aberto para que uma nova tentativa reutilize a mesma chave de idempotência.
        definirModalOcupado(false);
        mostrarToast('Erro de comunicação com o servidor. Tente novamente.', 'erro');
        return;
    }

    definirModalOcupado(false);
    fecharModal();
    await tratarRespostaRevisao(resultado, mensagemSucesso);
}

async function tratarRespostaRevisao(resultado, mensagemSucesso) {
    if (resultado.ok) {
        mostrarToast(mensagemSucesso, 'sucesso');
        await recarregarEstado();
        return;
    }

    const mensagemServidor = resultado.erro && resultado.erro.message;
    switch (resultado.status) {
        case 401:
            redirecionarParaLogin();
            return;
        case 403:
            mostrarToast('Sem permissão', 'erro');
            return;
        case 409:
            mostrarToast(mensagemServidor || 'Conflito de versão: a proposta foi alterada por outra operação.', 'erro');
            await recarregarEstado();
            return;
        case 400:
            mostrarToast(mensagemServidor || 'Requisição inválida.', 'erro');
            return;
        default:
            mostrarToast(mensagemServidor || `Falha inesperada no servidor (HTTP ${resultado.status}).`, 'erro');
    }
}

// ---------------------------------------------------------------------------
// Toast
// ---------------------------------------------------------------------------

/**
 * Exibe um toast. `tipo`: 'sucesso' | 'erro' | 'info'.
 */
export function mostrarToast(mensagem, tipo = 'sucesso') {
    const container = document.getElementById('toast-container');
    if (!container) return;
    const ehErro = tipo === 'erro';
    const pill = document.createElement('div');
    pill.className = 'toast-pill' + (ehErro ? ' toast-error' : '');
    pill.setAttribute('role', ehErro ? 'alert' : 'status');
    pill.innerHTML = `
        <span style="color:${ehErro ? 'var(--status-danger)' : 'var(--action-primary)'};">${ehErro ? Icons.x : Icons.check}</span>
        <span>${escapeHtml(mensagem)}</span>
    `;
    container.appendChild(pill);
    setTimeout(() => {
        pill.style.opacity = '0';
        pill.style.transform = 'translateY(10px)';
        pill.style.transition = 'all var(--motion-fast)';
        setTimeout(() => pill.remove(), 200);
    }, ehErro ? 5000 : 3200);
}
