import { appStore } from './store.js';
import { Icons } from './icons.js';
import { CommandPalette } from './command-palette.js';
import { escapeHtml, urlHttpsSegura, gerarChaveIdempotencia, formatarData } from './util.js';
import {
    buscarEstadoRevisao,
    buscarLinhaDoTempo,
    buscarComprovante,
    buscarDetalhesWorkspaceAdmin,
    obterWorkspaceIdDaUrl,
    postarJson,
    criarProjetoAdmin,
    configurarVinculoAdmin,
    removerVinculoAdmin,
    encerrarSessao,
    redirecionarParaLogin
} from './review-api.js';

const MOTIVO_MAX_CARACTERES = 1000;
const AVISO_SOMENTE_MAINTAINERS = 'Somente Maintainers podem aprovar, rejeitar, restaurar ou administrar';

let commandPaletteInstance = null;
let modalPayload = null;
let operacaoEmAndamento = false;
let appIniciado = false;
const timelineCache = new Map();
const timelineLoading = new Set();
let adminLoadingWsId = null;

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
    appStore.subscribe('timelineFilter', renderActiveView);
    appStore.subscribe('activeMcpClient', renderActiveView);
    appStore.subscribe('adminDetails', () => {
        if (appStore.get('activeView') === 'admin') renderActiveView();
    });
    appStore.subscribe('receiptPreview', () => {
        if (appStore.get('activeView') === 'mcp') renderActiveView();
    });
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
    timelineCache.clear();
    appStore.set('adminDetails', null);
    appStore.initData(dados);
    renderSessionChrome();
    renderActiveView();
}

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

function nomeProjeto(projetoId) {
    const lista = appStore.get('projetos') || [];
    const proj = lista.find(p => p && p.id === projetoId);
    return proj ? proj.nome : (projetoId ? String(projetoId).slice(0, 8) + '...' : '—');
}

function copiarTexto(texto, rotulo = 'Valor') {
    if (!texto) return;
    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        navigator.clipboard.writeText(String(texto))
            .then(() => mostrarToast(`${rotulo} copiado para a área de transferência.`, 'sucesso'))
            .catch(() => mostrarToast('Não foi possível copiar automaticamente.', 'erro'));
    } else {
        mostrarToast('Área de transferência indisponível neste navegador.', 'erro');
    }
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
        const ativar = () => {
            const view = el.getAttribute('data-view-nav');
            if (view) appStore.set('activeView', view);
        };
        el.addEventListener('click', ativar);
        el.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                ativar();
            }
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
    if (sidebar) sidebar.classList.toggle('collapsed', !!collapsed);
    if (btn) btn.classList.toggle('active', !collapsed);
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
    if (inspector) inspector.classList.toggle('collapsed', !open);
    if (btn) btn.classList.toggle('active', !!open);
    renderInspectorContent();
}

// ---------------------------------------------------------------------------
// Eventos delegados
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
            case 'refresh-state':
                recarregarEstado().then(ok => {
                    if (ok) mostrarToast('Estado atualizado.', 'sucesso');
                });
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
            case 'restore':
                abrirModalRestauracao(ds.topicId, parseInt(ds.versionToRestore, 10));
                break;
            case 'copy-text':
                copiarTexto(ds.copyValue || '', ds.copyLabel || 'Valor');
                break;
            case 'copy-element': {
                const el = ds.copyTarget ? document.getElementById(ds.copyTarget) : null;
                if (el) copiarTexto(el.textContent || '', ds.copyLabel || 'Configuração');
                break;
            }
            case 'set-timeline-filter':
                appStore.set('timelineFilter', ds.filter || 'all');
                break;
            case 'set-mcp-client':
                appStore.set('activeMcpClient', ds.client || 'codex');
                break;
            case 'apply-membership-preset': {
                const issEl = document.getElementById('admin-member-issuer');
                const subEl = document.getElementById('admin-member-subject');
                const roleEl = document.getElementById('admin-member-role');
                if (issEl && ds.issuer) issEl.value = ds.issuer;
                if (subEl && ds.subject) subEl.value = ds.subject;
                if (roleEl && ds.role) roleEl.value = ds.role;
                break;
            }
            case 'delete-membership':
                abrirModalRemoverVinculo(ds.membershipId, ds.subject);
                break;
            case 'logout':
                evento.preventDefault();
                sair(alvo);
                break;
            default:
                break;
        }
    });

    document.addEventListener('input', (evento) => {
        const alvo = evento.target;
        if (!(alvo instanceof HTMLElement)) return;
        if (alvo.dataset.action === 'pane-search') {
            appStore.set('filterText', alvo.value || '');
            atualizarListaFiltradaSemPerderFoco(alvo);
        } else if (alvo.id === 'admin-proj-name') {
            const slugEl = document.getElementById('admin-proj-slug');
            if (slugEl && !slugEl.dataset.edited) {
                slugEl.value = gerarSlugSugerido(alvo.value);
            }
        } else if (alvo.id === 'admin-proj-slug') {
            alvo.dataset.edited = '1';
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
            case 'pane-project-filter':
                appStore.set('filterProjectId', alvo.value || 'all');
                renderActiveView();
                break;
            default:
                break;
        }
    });

    document.addEventListener('submit', (evento) => {
        const form = evento.target;
        if (!(form instanceof HTMLFormElement)) return;
        const acao = form.dataset.action;
        if (!acao) return;
        evento.preventDefault();

        if (acao === 'verify-receipt') {
            executarAuditoriaComprovante();
        } else if (acao === 'create-project') {
            executarCriacaoProjeto(form);
        } else if (acao === 'save-membership') {
            executarSalvarVinculo(form);
        }
    });

    document.addEventListener('keydown', (evento) => {
        if (evento.key !== 'Enter' && evento.key !== ' ') return;
        const alvo = evento.target;
        if (!(alvo instanceof Element) || alvo.getAttribute('role') !== 'button' || !alvo.hasAttribute('data-action')) return;
        evento.preventDefault();
        alvo.click();
    });
}

function atualizarListaFiltradaSemPerderFoco(inputEl) {
    const view = appStore.get('activeView');
    const scrollEl = document.querySelector('.split-pane-scroll');
    if (!scrollEl) {
        renderActiveView();
        return;
    }
    if (view === 'reviews') {
        scrollEl.innerHTML = renderListaPropostasHtml();
    } else if (view === 'decisions') {
        scrollEl.innerHTML = renderListaDecisoesHtml();
    } else if (view === 'incidents') {
        scrollEl.innerHTML = renderListaIncidentesHtml();
    }
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
// Helpers de renderização e filtros
// ---------------------------------------------------------------------------

function gerarSlugSugerido(texto) {
    return String(texto || '')
        .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 80);
}

function filtrarLista(itens, camposFn) {
    const projId = appStore.get('filterProjectId') || 'all';
    const termo = (appStore.get('filterText') || '').trim().toLowerCase();
    return (itens || []).filter(item => {
        if (!item) return false;
        if (projId !== 'all' && item.projetoId !== projId) return false;
        if (!termo) return true;
        return camposFn(item).some(v => String(v || '').toLowerCase().includes(termo));
    });
}

function renderFiltroPainel(titulo, quantidade, placeholder) {
    const projetos = appStore.get('projetos') || [];
    const projSel = appStore.get('filterProjectId') || 'all';
    const texto = appStore.get('filterText') || '';

    const opcoesProjeto = projetos.map(p =>
        `<option value="${escapeHtml(p.id)}" ${projSel === p.id ? 'selected' : ''}>Projeto: ${escapeHtml(p.nome)}</option>`
    ).join('');

    return `
        <div class="pane-filter-bar">
            <div class="pane-filter-header">
                <span>${escapeHtml(titulo)} (${quantidade})</span>
                <span>${projetos.length} proj.</span>
            </div>
            <input type="search" class="pane-search-input" data-action="pane-search" value="${escapeHtml(texto)}" placeholder="${escapeHtml(placeholder)}">
            ${projetos.length > 0 ? `
                <select class="pane-select" data-action="pane-project-filter" aria-label="Filtrar por projeto">
                    <option value="all" ${projSel === 'all' ? 'selected' : ''}>Todos os projetos</option>
                    ${opcoesProjeto}
                </select>
            ` : ''}
        </div>
    `;
}

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
    return chaves.map(k => `<span class="tech-key-chip">${escapeHtml(k)}</span>`).join('');
}

function renderSecaoChaves(lista) {
    const html = renderChavesTecnicas(lista);
    if (!html) return '';
    return `
        <div class="doc-section">
            <div class="doc-section-title">Chaves Técnicas e Identificadores</div>
            <div class="tech-keys-wrap">${html}</div>
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
        <div class="empty-state-wrap">
            <div class="empty-state-inner">
                ${iconeHtml ? `<div class="empty-state-icon">${iconeHtml}</div>` : ''}
                <h3 class="empty-state-title">${escapeHtml(titulo)}</h3>
                <p class="empty-state-desc">${descricaoHtml}</p>
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
        if (toolbarTitle) toolbarTitle.textContent = 'Linha do Tempo Imutável';
        renderTimelineView(contentBody);
    } else if (view === 'mcp') {
        if (toolbarTitle) toolbarTitle.textContent = 'Conectar Agentes MCP';
        renderMcpView(contentBody);
    } else if (view === 'admin') {
        if (toolbarTitle) toolbarTitle.textContent = 'Administração do Workspace';
        renderAdminView(contentBody);
    } else if (view === 'settings') {
        if (toolbarTitle) toolbarTitle.textContent = 'Configurações do Sistema';
        renderSettingsView(contentBody);
    }

    renderInspectorContent();
}

function renderOverviewView(container) {
    const propostas = appStore.get('propostas');
    const decisoes = appStore.get('decisoes');
    const incidentes = appStore.get('incidentes');
    const eventos = appStore.get('eventos');
    const projetos = appStore.get('projetos') || [];
    const ws = appStore.get('workspace');
    const wsNome = ws ? (ws.nome || ws.slug) : 'Workspace Principal';

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Workspace: ${escapeHtml(wsNome)} &bull; ${projetos.length} projeto(s)</div>
                <h1 class="overview-title">Painel de Governança Arquitetural</h1>
                <p class="overview-desc">Acompanhe propostas submetidas por agentes via MCP, decisões arquiteturais ativas, incidentes e trilha imutável de eventos.</p>
            </div>

            <div class="metrics-grid">
                <div class="metric-card" role="button" tabindex="0" data-action="navigate" data-view="reviews">
                    <span class="metric-card-label">Aguardando Revisão</span>
                    <span class="metric-card-value">${propostas.length}</span>
                    <span class="metric-card-sub">propostas pendentes</span>
                </div>
                <div class="metric-card" role="button" tabindex="0" data-action="navigate" data-view="decisions">
                    <span class="metric-card-label">Decisões Vigentes</span>
                    <span class="metric-card-value">${decisoes.length}</span>
                    <span class="metric-card-sub">tópicos consolidados</span>
                </div>
                <div class="metric-card" role="button" tabindex="0" data-action="navigate" data-view="incidents">
                    <span class="metric-card-label">Incidentes</span>
                    <span class="metric-card-value">${incidentes.length}</span>
                    <span class="metric-card-sub">relatos e RCAs</span>
                </div>
                <div class="metric-card" role="button" tabindex="0" data-action="navigate" data-view="timeline">
                    <span class="metric-card-label">Eventos Recentes</span>
                    <span class="metric-card-value">${eventos.length}</span>
                    <span class="metric-card-sub">log append-only</span>
                </div>
            </div>

            ${ws ? `
                <div class="overview-section">
                    <div class="overview-section-header">
                        <div class="overview-section-title">Identificadores para Agentes MCP (Codex / Claude / Cursor)</div>
                        <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="mcp">Configurar Agentes</button>
                    </div>
                    <div class="tech-keys-wrap">
                        <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(ws.id)}" data-copy-label="Workspace ID">
                            ${Icons.copy} Workspace: ${escapeHtml(ws.id)}
                        </button>
                        ${projetos.map(p => `
                            <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(p.id)}" data-copy-label="Project ID (${escapeHtml(p.nome)})">
                                ${Icons.copy} Projeto ${escapeHtml(p.slug)}: ${escapeHtml(p.id)}
                            </button>
                        `).join('')}
                    </div>
                </div>
            ` : ''}

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Aguardando Validação Humana (${propostas.length})</div>
                    <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="reviews">Ver Todas</button>
                </div>
                ${propostas.length === 0 ? `
                    <div class="panel-card" style="text-align:center;color:var(--text-tertiary);">
                        Nenhuma proposta aguardando validação no momento.
                    </div>
                ` : propostas.map(p => `
                    <div class="overview-list-row" role="button" tabindex="0" data-action="open-review" data-topic-id="${escapeHtml(p.topicoId)}">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(p.titulo)}</span>
                            <span class="overview-row-sub">${escapeHtml(nomeProjeto(p.projetoId))} &bull; tópico: ${escapeHtml(p.chaveTopico)} &bull; v${escapeHtml(p.versaoFluxo)}</span>
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
                    <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="decisions">Ver Todas</button>
                </div>
                ${decisoes.length === 0 ? `
                    <div class="panel-card" style="text-align:center;color:var(--text-tertiary);">
                        Nenhuma decisão vigente consolidada no momento.
                    </div>
                ` : decisoes.map(d => `
                    <div class="overview-list-row" role="button" tabindex="0" data-action="open-decision" data-topic-id="${escapeHtml(d.topicoId)}">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(d.titulo)}</span>
                            <span class="overview-row-sub">${escapeHtml(nomeProjeto(d.projetoId))} &bull; tópico: ${escapeHtml(d.chaveTopico)} &bull; v${escapeHtml(d.versaoFluxo)}</span>
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

// --- Revisões ---

function renderListaPropostasHtml() {
    const todas = appStore.get('propostas');
    const filtradas = filtrarLista(todas, p => [p.chaveTopico, p.titulo, p.resumo, p.autorSujeito, ...(p.chavesTecnicas || [])]);
    const selectedId = appStore.get('selectedReviewId');
    const selected = filtradas.find(p => p.topicoId === selectedId) || filtradas[0];

    if (filtradas.length === 0) {
        return `<div style="padding:1.5rem;text-align:center;color:var(--text-tertiary);font-size:0.82rem;">Nenhuma proposta corresponde ao filtro.</div>`;
    }

    return filtradas.map(p => {
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
    }).join('');
}

function renderReviewsView(container) {
    const todas = appStore.get('propostas');
    if (todas.length === 0) {
        container.innerHTML = estadoVazio(
            'Nenhuma proposta pendente',
            'Todas as propostas submetidas por agentes via MCP foram validadas e consolidadas.',
            Icons.reviews
        );
        return;
    }

    const filtradas = filtrarLista(todas, p => [p.chaveTopico, p.titulo, p.resumo, p.autorSujeito, ...(p.chavesTecnicas || [])]);
    const selectedId = appStore.get('selectedReviewId');
    const selected = filtradas.find(p => p.topicoId === selectedId) || filtradas[0] || null;

    container.innerHTML = `
        <div class="split-view">
            <div class="split-pane-list">
                ${renderFiltroPainel('Propostas', filtradas.length, 'Filtrar propostas...')}
                <div class="split-pane-scroll">
                    ${renderListaPropostasHtml()}
                </div>
            </div>

            <div class="split-pane-detail">
                ${selected ? renderProposalDetail(selected) : estadoVazio('Nenhum resultado', 'Ajuste o filtro para exibir propostas.', Icons.reviews)}
            </div>
        </div>
    `;
}

function renderProposalDetail(p) {
    const vigente = p.decisaoVigente && typeof p.decisaoVigente === 'object' ? p.decisaoVigente : null;
    const autor = escapeHtml(p.autorSujeito || 'autor não informado');
    const cliente = p.clienteId ? `<code>${escapeHtml(p.clienteId)}</code>` : 'cliente não informado';
    const quando = escapeHtml(formatarData(p.propostaEm, 'data não informada'));

    const secaoComparacao = vigente ? `
        <div class="doc-section">
            <div class="doc-section-title">Comparação com Decisão Vigente (Substituição)</div>
            <div class="comparison-diff-grid">
                <div class="diff-card current">
                    <div class="diff-card-label">Vigente Atual (será substituída)</div>
                    <div class="diff-card-title">${escapeHtml(vigente.titulo)}</div>
                    <div class="diff-card-body">${escapeHtml(vigente.resumo)}</div>
                    <div class="diff-card-meta">registro v${escapeHtml(vigente.versaoRegistroAtivo)}</div>
                </div>
                <div class="diff-card proposed">
                    <div class="diff-card-label">Nova Proposta</div>
                    <div class="diff-card-title">${escapeHtml(p.titulo)}</div>
                    <div class="diff-card-body">${escapeHtml(p.resumo)}</div>
                    <div class="diff-card-meta">fluxo v${escapeHtml(p.versaoFluxo)}</div>
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
                ${vigente ? `<span class="status-pill reported-state">Substitui v${escapeHtml(vigente.versaoRegistroAtivo)}</span>` : '<span class="status-pill reported-state">Primeira decisão</span>'}
                <span class="mono-tag">projeto: ${escapeHtml(nomeProjeto(p.projetoId))}</span>
                <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(p.chaveTopico)}" data-copy-label="Chave do tópico">
                    ${Icons.copy} ${escapeHtml(p.chaveTopico)}
                </button>
                <span class="mono-tag">v${escapeHtml(p.versaoFluxo)}</span>
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

        ${secaoComparacao}
        ${renderSecaoChaves(p.chavesTecnicas)}
        ${renderSecaoIncidentesRelacionados(p.incidentesRelacionadosIds)}

        <div class="doc-section">
            <div class="doc-section-title">Evidências e Documentos Vinculados</div>
            <div class="evidence-table">${renderEvidencias(p.evidencias, 'Nenhuma evidência anexada.')}</div>
        </div>

        ${acoes}
    `;
}

// --- Decisões Vigentes ---

function renderListaDecisoesHtml() {
    const todas = appStore.get('decisoes');
    const filtradas = filtrarLista(todas, d => [d.chaveTopico, d.titulo, d.resumo, ...(d.chavesTecnicas || [])]);
    const selectedId = appStore.get('selectedDecisionId');
    const selected = filtradas.find(d => d.topicoId === selectedId) || filtradas[0];

    if (filtradas.length === 0) {
        return `<div style="padding:1.5rem;text-align:center;color:var(--text-tertiary);font-size:0.82rem;">Nenhuma decisão corresponde ao filtro.</div>`;
    }

    return filtradas.map(d => {
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
    }).join('');
}

function renderDecisionsView(container) {
    const todas = appStore.get('decisoes');
    if (todas.length === 0) {
        container.innerHTML = estadoVazio(
            'Nenhuma decisão vigente',
            'Não há decisões ativas consolidadas neste espaço de trabalho.',
            Icons.decisions
        );
        return;
    }

    const filtradas = filtrarLista(todas, d => [d.chaveTopico, d.titulo, d.resumo, ...(d.chavesTecnicas || [])]);
    const selectedId = appStore.get('selectedDecisionId');
    const selected = filtradas.find(d => d.topicoId === selectedId) || filtradas[0] || null;

    container.innerHTML = `
        <div class="split-view">
            <div class="split-pane-list">
                ${renderFiltroPainel('Decisões', filtradas.length, 'Filtrar decisões...')}
                <div class="split-pane-scroll">
                    ${renderListaDecisoesHtml()}
                </div>
            </div>

            <div class="split-pane-detail">
                ${selected ? renderDecisionDetail(selected) : estadoVazio('Nenhum resultado', 'Ajuste o filtro para exibir decisões.', Icons.decisions)}
            </div>
        </div>
    `;

    if (selected) carregarTimelineDecisao(selected);
}

function chaveCacheTimeline(d) {
    return `${d.topicoId}@${d.versaoFluxo}`;
}

async function carregarTimelineDecisao(d) {
    const chave = chaveCacheTimeline(d);
    if (timelineCache.has(chave) || timelineLoading.has(chave)) return;
    timelineLoading.add(chave);
    try {
        const res = await buscarLinhaDoTempo(d.espacoTrabalhoId, d.projetoId, 'decision', d.topicoId);
        if (res.ok && res.dados) {
            timelineCache.set(chave, res.dados);
            const host = document.getElementById(`decision-history-${d.topicoId}`);
            if (host) host.innerHTML = renderConteudoHistoricoDecisao(d, res.dados);
        }
    } finally {
        timelineLoading.delete(chave);
    }
}

function renderConteudoHistoricoDecisao(d, timeline) {
    const eventos = timeline && Array.isArray(timeline.events) ? timeline.events : [];
    if (eventos.length === 0) {
        return `<div style="color:var(--text-tertiary);font-size:0.82rem;">Nenhum evento encontrado na trilha deste tópico.</div>`;
    }

    return `
        <div class="version-timeline">
            ${eventos.map(ev => {
                const info = classificarEvento(ev.type);
                const podeRestaurar = podeRevisar()
                    && !d.propostaPendenteId
                    && ev.type === 'DecisionApproved'
                    && ev.streamVersion < d.versaoFluxo;
                return `
                    <div class="version-row">
                        <div class="version-row-left">
                            <div class="version-row-head">
                                <span class="status-pill ${info.classe}">${escapeHtml(info.rotulo)}</span>
                                <span class="mono-tag">v${escapeHtml(ev.streamVersion)}</span>
                                <strong>${escapeHtml(ev.type)}</strong>
                            </div>
                            <div class="version-row-meta">
                                Por ${escapeHtml(ev.authorSubject)} (${escapeHtml(ev.clientId || 'sessão web')}) em ${escapeHtml(formatarData(ev.recordedAt))}
                                ${ev.reason ? ` &bull; Motivo: "${escapeHtml(ev.reason)}"` : ''}
                            </div>
                        </div>
                        ${podeRestaurar ? `
                            <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm"
                                data-action="restore"
                                data-topic-id="${escapeHtml(d.topicoId)}"
                                data-version-to-restore="${escapeHtml(ev.streamVersion)}">
                                ${Icons.history} Restaurar v${escapeHtml(ev.streamVersion)}
                            </button>
                        ` : ''}
                    </div>
                `;
            }).join('')}
        </div>
    `;
}

function renderDecisionDetail(d) {
    const cache = timelineCache.get(chaveCacheTimeline(d));
    const historicoHtml = cache
        ? renderConteudoHistoricoDecisao(d, cache)
        : `<div style="color:var(--text-tertiary);font-size:0.82rem;">Carregando histórico de eventos...</div>`;

    const avisoPendente = d.propostaPendenteId ? `
        <div class="doc-section">
            <div class="evidence-link-row" role="button" tabindex="0" style="cursor:pointer;" data-action="open-review" data-topic-id="${escapeHtml(d.topicoId)}">
                <div class="evidence-row-left">
                    <span class="status-pill proposed-state">Proposta pendente</span>
                    <span>Há uma nova proposta aguardando revisão humana para substituir este tópico.</span>
                </div>
                <span style="color:var(--text-tertiary);">${Icons.chevronRight}</span>
            </div>
        </div>
    ` : '';

    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                ${pillSituacao(SITUACAO_DECISAO, d.situacao, SITUACAO_DECISAO.active)}
                <span class="mono-tag">projeto: ${escapeHtml(nomeProjeto(d.projetoId))}</span>
                <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(d.chaveTopico)}" data-copy-label="Chave do tópico">
                    ${Icons.copy} ${escapeHtml(d.chaveTopico)}
                </button>
                <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(d.topicoId)}" data-copy-label="Topic ID">
                    ${Icons.copy} ID: ${escapeHtml(String(d.topicoId).slice(0, 8))}...
                </button>
                ${d.versaoRegistroAtivo != null ? `<span class="mono-tag">registro v${escapeHtml(d.versaoRegistroAtivo)}</span>` : ''}
                <span class="mono-tag">fluxo v${escapeHtml(d.versaoFluxo)}</span>
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
            <div class="doc-section-title">Evidências Vinculadas</div>
            <div class="evidence-table">${renderEvidencias(d.evidencias, 'Nenhuma evidência anexada.')}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Histórico Imutável &amp; Restauração de Versões</div>
            <div id="decision-history-${escapeHtml(d.topicoId)}">${historicoHtml}</div>
        </div>
    `;
}

// --- Incidentes ---

function renderListaIncidentesHtml() {
    const todos = appStore.get('incidentes');
    const filtrados = filtrarLista(todos, i => [i.titulo, i.resumo, i.incidenteId, ...(i.chavesTecnicas || [])]);
    const selectedId = appStore.get('selectedIncidentId');
    const selected = filtrados.find(i => i.incidenteId === selectedId) || filtrados[0];

    if (filtrados.length === 0) {
        return `<div style="padding:1.5rem;text-align:center;color:var(--text-tertiary);font-size:0.82rem;">Nenhum incidente corresponde ao filtro.</div>`;
    }

    return filtrados.map(i => {
        const isSel = selected && i.incidenteId === selected.incidenteId;
        return `
            <div class="record-list-item ${isSel ? 'selected' : ''}" role="button" tabindex="0" data-action="select-incident" data-incident-id="${escapeHtml(i.incidenteId)}">
                <div class="record-item-top">
                    <span class="record-item-topic">${escapeHtml(nomeProjeto(i.projetoId))}</span>
                    <span class="record-item-version">v${escapeHtml(i.versaoFluxo)}</span>
                </div>
                <div class="record-item-title">${escapeHtml(i.titulo)}</div>
                <div class="record-item-summary">${escapeHtml(i.resumo)}</div>
            </div>
        `;
    }).join('');
}

function renderIncidentsView(container) {
    const todos = appStore.get('incidentes');
    if (todos.length === 0) {
        container.innerHTML = estadoVazio(
            'Nenhum incidente registrado',
            'Nenhum relato de incidente foi submetido via ferramenta <code>log_incident</code>.',
            Icons.incidents
        );
        return;
    }

    const filtrados = filtrarLista(todos, i => [i.titulo, i.resumo, i.incidenteId, ...(i.chavesTecnicas || [])]);
    const selectedId = appStore.get('selectedIncidentId');
    const selected = filtrados.find(i => i.incidenteId === selectedId) || filtrados[0] || null;

    container.innerHTML = `
        <div class="split-view">
            <div class="split-pane-list">
                ${renderFiltroPainel('Incidentes', filtrados.length, 'Filtrar incidentes...')}
                <div class="split-pane-scroll">
                    ${renderListaIncidentesHtml()}
                </div>
            </div>

            <div class="split-pane-detail">
                ${selected ? renderIncidentDetail(selected) : estadoVazio('Nenhum resultado', 'Ajuste o filtro para exibir incidentes.', Icons.incidents)}
            </div>
        </div>
    `;
}

function renderIncidentDetail(inc) {
    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                ${pillSituacao(SITUACAO_INCIDENTE, inc.situacao, SITUACAO_INCIDENTE.reported)}
                <span class="mono-tag">projeto: ${escapeHtml(nomeProjeto(inc.projetoId))}</span>
                <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(inc.incidenteId)}" data-copy-label="Incident ID">
                    ${Icons.copy} ID: ${escapeHtml(inc.incidenteId)}
                </button>
                <span class="mono-tag">v${escapeHtml(inc.versaoFluxo)}</span>
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

// --- Linha do Tempo ---

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
    const todos = appStore.get('eventos') || [];
    const filtro = appStore.get('timelineFilter') || 'all';
    const eventos = filtro === 'all' ? todos : todos.filter(e => e && e.tipo === filtro);

    const pills = [
        { id: 'all', label: `Todos (${todos.length})` },
        { id: 'DecisionProposed', label: 'Propostas' },
        { id: 'DecisionApproved', label: 'Aprovações' },
        { id: 'DecisionRejected', label: 'Rejeições' },
        { id: 'IncidentReported', label: 'Incidentes' }
    ];

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Trilha Auditável Event Sourced</div>
                <h1 class="overview-title">Histórico de Eventos Imutáveis</h1>
                <p class="overview-desc">Registro permanente e append-only de todas as propostas, aprovações, rejeições e incidentes neste workspace.</p>
            </div>

            <div class="filter-pills-row">
                ${pills.map(p => `
                    <button type="button" class="filter-pill-btn ${filtro === p.id ? 'active' : ''}" data-action="set-timeline-filter" data-filter="${p.id}">
                        ${escapeHtml(p.label)}
                    </button>
                `).join('')}
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Eventos (${eventos.length})</div>
                </div>
                ${eventos.length === 0 ? `
                    <div class="panel-card" style="text-align:center;color:var(--text-tertiary);">
                        Nenhum evento encontrado para o filtro selecionado.
                    </div>
                ` : eventos.map(ev => {
                    const info = classificarEvento(ev.tipo);
                    const ehInc = ev.tipo === 'IncidentReported';
                    const action = ehInc ? 'open-incident' : 'open-decision';
                    const attrId = ehInc
                        ? `data-incident-id="${escapeHtml(ev.fluxoId)}"`
                        : `data-topic-id="${escapeHtml(ev.fluxoId)}"`;

                    return `
                        <div class="overview-list-row" role="button" tabindex="0" data-action="${action}" ${attrId}>
                            <div class="overview-row-left">
                                <div style="display:flex;align-items:center;gap:8px;margin-bottom:3px;flex-wrap:wrap;">
                                    <span class="status-pill ${info.classe}">${escapeHtml(info.rotulo)}</span>
                                    <span class="overview-row-title">${escapeHtml(ev.tipo)}</span>
                                    <span class="mono-tag">v${escapeHtml(ev.versaoFluxo)}</span>
                                </div>
                                <span class="overview-row-sub">${escapeHtml(nomeProjeto(ev.projetoId))} &bull; Autor: ${escapeHtml(ev.autorSujeito)} &bull; Cliente: ${escapeHtml(ev.clienteId || 'sessão web')}</span>
                            </div>
                            <div class="overview-row-right">
                                <span class="mono-tag">${escapeHtml(formatarData(ev.registradoEm))}</span>
                                <span style="color:var(--text-tertiary);">${Icons.chevronRight}</span>
                            </div>
                        </div>
                    `;
                }).join('')}
            </div>
        </div>
    `;
}

// --- Conectar Agentes MCP & Auditoria de Comprovantes ---

function gerarConfigClienteMcp(cliente, origin, tokenExemplo, wsId, projId) {
    const mcpUrl = `${origin}/mcp`;
    switch (cliente) {
        case 'codex':
            return {
                titulo: 'OpenAI Codex (CLI & Extensão VS Code / ~/.codex/config.toml)',
                codigo: `# Opção A — CLI com variável de ambiente (defina SENTINEL_MCP_TOKEN antes):
codex mcp add sentinel --url ${mcpUrl} --bearer-token-env-var SENTINEL_MCP_TOKEN

# Opção B — ~/.codex/config.toml (ideal no Windows para extensão VS Code):
[mcp_servers.sentinel]
url = "${mcpUrl}"

[mcp_servers.sentinel.http_headers]
Authorization = "Bearer ${tokenExemplo}"

# Opção C — STDIO local sem conflito de porta HTTP:
# codex mcp add sentinel --env ASPNETCORE_ENVIRONMENT=Development --env SENTINEL_MCP_TOKEN=${tokenExemplo} -- dotnet run --no-build --no-launch-profile --project src/Sentinel.Api -- --stdio

# Prompt sugerido no Codex:
# "Use list_workspaces e query_context no Sentinel antes de alterar o código (workspaceId=${wsId}, projectId=${projId})."`
            };
        case 'claude':
            return {
                titulo: 'Claude Code (Streamable HTTP)',
                codigo: `claude mcp add --transport http sentinel ${mcpUrl} \\
  --header "Authorization: Bearer ${tokenExemplo}"`
            };
        case 'cursor':
            return {
                titulo: '.cursor/mcp.json',
                codigo: JSON.stringify({
                    mcpServers: {
                        sentinel: {
                            url: mcpUrl,
                            headers: { Authorization: `Bearer ${tokenExemplo}` }
                        }
                    }
                }, null, 2)
            };
        case 'gemini':
            return {
                titulo: '~/.gemini/settings.json (Gemini CLI)',
                codigo: JSON.stringify({
                    mcpServers: {
                        sentinel: {
                            httpUrl: mcpUrl,
                            headers: { Authorization: `Bearer ${tokenExemplo}` }
                        }
                    }
                }, null, 2)
            };
        case 'vscode':
            return {
                titulo: '.vscode/mcp.json (VS Code / Copilot)',
                codigo: JSON.stringify({
                    servers: {
                        sentinel: {
                            type: 'http',
                            url: mcpUrl,
                            headers: { Authorization: `Bearer ${tokenExemplo}` }
                        }
                    }
                }, null, 2)
            };
        default:
            return {
                titulo: 'HTTP JSON-RPC direto (curl / PowerShell)',
                codigo: `curl -X POST ${mcpUrl} \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer ${tokenExemplo}" \\
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_workspaces","arguments":{}}}'`
            };
    }
}

function renderMcpView(container) {
    const ws = appStore.get('workspace');
    const projetos = appStore.get('projetos') || [];
    const wsId = ws ? ws.id : '11111111-1111-1111-1111-111111111111';
    const projId = projetos.length > 0 ? projetos[0].id : 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    const origin = window.location.origin;
    const clienteAtivo = appStore.get('activeMcpClient') || 'codex';
    const tokenExemplo = `mock:agent-dev-1|read:memory,write:decision,write:incident|${clienteAtivo}`;
    const snippet = gerarConfigClienteMcp(clienteAtivo, origin, tokenExemplo, wsId, projId);
    const recibo = appStore.get('receiptPreview');

    const tabs = [
        { id: 'codex', label: 'OpenAI Codex' },
        { id: 'claude', label: 'Claude Code' },
        { id: 'cursor', label: 'Cursor IDE' },
        { id: 'gemini', label: 'Gemini CLI' },
        { id: 'vscode', label: 'VS Code' },
        { id: 'curl', label: 'HTTP / curl' }
    ];

    const tools = [
        { name: 'list_workspaces', mode: 'readOnly', scope: 'read:memory', desc: 'Descobre workspaces, papéis e projetos acessíveis à identidade do agente sem precisar decorar UUIDs.' },
        { name: 'query_context', mode: 'readOnly', scope: 'read:memory', desc: 'Busca decisões vigentes e incidentes relevantes (priorizando technicalKeys exatas) e emite comprovante SHA-256.' },
        { name: 'get_timeline', mode: 'readOnly', scope: 'read:memory', desc: 'Reconstitui deterministicamente o estado de uma decisão ou incidente em qualquer versão histórica.' },
        { name: 'get_context_receipt', mode: 'readOnly', scope: 'read:memory', desc: 'Audita o comprovante de uma consulta recalculando o digest SHA-256 sobre os itens retornados.' },
        { name: 'propose_decision', mode: 'write', scope: 'write:decision', desc: 'Submete uma proposta arquitetural com expectedVersion, idempotencyKey e evidências HTTPS para aprovação humana.' },
        { name: 'log_incident', mode: 'write', scope: 'write:incident', desc: 'Registra incidente técnico com chaves técnicas, data da ocorrência e evidências HTTPS (RCA/PR).' }
    ];

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">MCP 2025-11-25 &bull; Streamable HTTP &amp; SDK Oficial</div>
                <h1 class="overview-title">Conectar Agentes e Modelos (MCP)</h1>
                <p class="overview-desc">
                    Endpoints ativos: <code>POST ${escapeHtml(origin)}/mcp</code> (stateless), <code>${escapeHtml(origin)}/mcp/sse</code> (SDK oficial) e modo CLI <code>--stdio</code>.
                    Em produção, substitua o token <code>mock:</code> por chave de API (<code>Sentinel:ApiKeys</code>) ou JWT OAuth.
                </p>
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Configuração Pronta por Cliente</div>
                    <div class="tech-keys-wrap">
                        <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(origin)}/mcp" data-copy-label="URL MCP">
                            ${Icons.copy} ${escapeHtml(origin)}/mcp
                        </button>
                        <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(wsId)}" data-copy-label="Workspace ID">
                            ${Icons.copy} WS: ${escapeHtml(wsId.slice(0, 8))}...
                        </button>
                    </div>
                </div>

                <div class="client-tabs" role="tablist">
                    ${tabs.map(t => `
                        <button type="button" class="client-tab-btn ${clienteAtivo === t.id ? 'active' : ''}" data-action="set-mcp-client" data-client="${t.id}">
                            ${escapeHtml(t.label)}
                        </button>
                    `).join('')}
                </div>

                <div class="code-panel">
                    <div class="code-panel-header">
                        <span>${escapeHtml(snippet.titulo)}</span>
                        <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="copy-element" data-copy-target="mcp-snippet-code" data-copy-label="Configuração">
                            ${Icons.copy} Copiar
                        </button>
                    </div>
                    <pre id="mcp-snippet-code" class="code-panel-pre">${escapeHtml(snippet.codigo)}</pre>
                </div>
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Auditor de Comprovantes (SHA-256)</div>
                </div>
                <div class="panel-card">
                    <form data-action="verify-receipt" class="form-grid">
                        <div class="form-field">
                            <label for="receipt-project-select">Projeto</label>
                            <select id="receipt-project-select" class="form-input" required>
                                ${projetos.map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.nome)} (${escapeHtml(p.slug)})</option>`).join('')}
                            </select>
                        </div>
                        <div class="form-field">
                            <label for="receipt-query-id">ID da Consulta (queryId)</label>
                            <input id="receipt-query-id" type="text" class="form-input" placeholder="UUID retornado por query_context" required value="${escapeHtml(recibo && recibo.queryId ? recibo.queryId : '')}">
                        </div>
                        <div>
                            <button type="submit" class="btn-macos btn-macos-primary">Verificar Comprovante</button>
                        </div>
                    </form>
                    ${recibo ? renderResultadoComprovanteHtml(recibo) : ''}
                </div>
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Catálogo de Ferramentas, Recursos &amp; Prompts (${tools.length} tools)</div>
                </div>
                ${tools.map(t => `
                    <div class="mcp-tool-card">
                        <div class="mcp-tool-header">
                            <span class="mcp-tool-name">${escapeHtml(t.name)}</span>
                            <div class="mcp-tool-badges">
                                <span class="mono-tag">${escapeHtml(t.scope)}</span>
                                <span class="status-pill ${t.mode === 'readOnly' ? 'active-state' : 'proposed-state'}">${t.mode === 'readOnly' ? 'Somente Leitura' : 'Escrita'}</span>
                            </div>
                        </div>
                        <p class="mcp-tool-desc">${escapeHtml(t.desc)}</p>
                    </div>
                `).join('')}
                <div class="mcp-tool-card">
                    <div class="mcp-tool-header">
                        <span class="mcp-tool-name">Resources &amp; Prompts MCP</span>
                        <span class="status-pill info-state">Descoberta Guiada</span>
                    </div>
                    <p class="mcp-tool-desc">
                        Resources: <code>sentinel://workspaces</code>, <code>sentinel://guide</code>, <code>sentinel://workspaces/{workspaceId}/projects/{projectId}/decisions|incidents</code>.
                        Prompts: <code>consult_context_before_change</code>, <code>propose_architecture_decision</code>, <code>report_incident_rca</code>.
                    </p>
                </div>
            </div>
        </div>
    `;
}

function renderResultadoComprovanteHtml(r) {
    if (r.erro) {
        return `<div class="receipt-box"><span class="status-pill rejected-state">${escapeHtml(r.erro)}</span></div>`;
    }
    return `
        <div class="receipt-box">
            <div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;margin-bottom:8px;">
                <span class="status-pill ${r.integrityVerified ? 'active-state' : 'rejected-state'}">
                    ${r.integrityVerified ? 'Integridade SHA-256 Verificada' : 'Falha na Integridade'}
                </span>
                <span class="mono-tag">${escapeHtml(formatarData(r.createdAt))}</span>
            </div>
            <div style="font-size:0.78rem;color:var(--text-secondary);margin-bottom:6px;">
                Autor: <strong>${escapeHtml(r.principalId)}</strong> (${escapeHtml(r.clientId || '—')}) &bull; Modo: <code>${escapeHtml(r.searchMode)}</code>
            </div>
            <div class="mono-tag" style="word-break:break-all;margin-bottom:8px;">SHA-256: ${escapeHtml(r.digest)}</div>
            <div style="font-size:0.76rem;color:var(--text-secondary);">Itens entregues (${(r.items || []).length}):</div>
            ${(r.items || []).map(i => `
                <div class="mono-tag" style="margin-top:4px;display:block;">
                    #${escapeHtml(i.position)} [${escapeHtml(i.recordType)}] fluxo=${escapeHtml(i.streamId)} &bull; reg v${escapeHtml(i.recordVersion)} / fluxo v${escapeHtml(i.streamVersion)} (${escapeHtml(i.status)})
                </div>
            `).join('')}
        </div>
    `;
}

async function executarAuditoriaComprovante() {
    const ws = appStore.get('workspace');
    const projEl = document.getElementById('receipt-project-select');
    const qEl = document.getElementById('receipt-query-id');
    if (!ws || !projEl || !qEl) return;

    const res = await buscarComprovante(ws.id, projEl.value, qEl.value.trim());
    if (res.status === 401) {
        redirecionarParaLogin();
        return;
    }
    if (res.ok) {
        appStore.set('receiptPreview', res.dados);
    } else {
        appStore.set('receiptPreview', { queryId: qEl.value.trim(), erro: (res.erro && res.erro.message) || `Não encontrado (HTTP ${res.status})` });
    }
}

// --- Administração do Workspace ---

function renderAdminView(container) {
    const ws = appStore.get('workspace');
    if (!podeRevisar()) {
        container.innerHTML = `
            <div class="overview-view">
                <div class="overview-hero">
                    <div class="overview-badge">Acesso Restrito</div>
                    <h1 class="overview-title">Administração do Workspace</h1>
                    <p class="overview-desc">A gestão de projetos e vínculos de agentes/usuários exige papel <strong>Maintainer</strong> neste espaço de trabalho.</p>
                </div>
                <div class="review-permission-notice">${Icons.shieldCheck} ${escapeHtml(AVISO_SOMENTE_MAINTAINERS)}.</div>
            </div>
        `;
        return;
    }

    const detalhes = appStore.get('adminDetails');
    if (ws && (!detalhes || !detalhes.workspace || detalhes.workspace.workspaceId !== ws.id) && adminLoadingWsId !== ws.id) {
        carregarAdminWorkspace(ws.id);
    }

    const projetos = detalhes ? (detalhes.projects || []) : (appStore.get('projetos') || []);
    const vinculos = detalhes ? (detalhes.memberships || []) : [];

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Governança &bull; ${escapeHtml(ws ? ws.nome : '')}</div>
                <h1 class="overview-title">Projetos e Vínculos de Acesso</h1>
                <p class="overview-desc">Crie projetos e gerencie identidades (agentes Codex, Claude, Cursor ou revisores humanos) com isolamento RLS garantido por workspace.</p>
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Projetos do Workspace (${projetos.length})</div>
                </div>

                <div class="panel-card">
                    <form data-action="create-project" class="form-grid">
                        <div class="form-field">
                            <label for="admin-proj-name">Nome do Projeto</label>
                            <input id="admin-proj-name" type="text" class="form-input" maxlength="120" required placeholder="Ex.: Checkout Pix">
                        </div>
                        <div class="form-field">
                            <label for="admin-proj-slug">Slug ASCII</label>
                            <input id="admin-proj-slug" type="text" class="form-input" maxlength="80" required placeholder="checkout-pix">
                        </div>
                        <div>
                            <button type="submit" class="btn-macos btn-macos-primary">Criar Projeto</button>
                        </div>
                    </form>
                </div>

                ${projetos.map(p => {
                    const id = p.projectId || p.id;
                    const nome = p.name || p.nome;
                    const slug = p.slug;
                    return `
                        <div class="overview-list-row" style="cursor:default;">
                            <div class="overview-row-left">
                                <span class="overview-row-title">${escapeHtml(nome)}</span>
                                <span class="overview-row-sub">slug: <code>${escapeHtml(slug)}</code></span>
                            </div>
                            <div class="overview-row-right">
                                <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(id)}" data-copy-label="Project ID">
                                    ${Icons.copy} ${escapeHtml(id)}
                                </button>
                            </div>
                        </div>
                    `;
                }).join('')}
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Vínculos / Memberships (${vinculos.length})</div>
                    <div class="tech-keys-wrap">
                        <button type="button" class="filter-pill-btn" data-action="apply-membership-preset" data-issuer="https://auth.sentinel.dev" data-subject="codex-agent" data-role="Contributor">+ Preset Codex</button>
                        <button type="button" class="filter-pill-btn" data-action="apply-membership-preset" data-issuer="https://auth.sentinel.dev" data-subject="reviewer-humano" data-role="Maintainer">+ Preset Mantenedor</button>
                    </div>
                </div>

                <div class="panel-card">
                    <form data-action="save-membership" class="form-grid">
                        <div class="form-field">
                            <label for="admin-member-issuer">Emissor (Issuer URL)</label>
                            <input id="admin-member-issuer" type="url" class="form-input" required value="https://auth.sentinel.dev">
                        </div>
                        <div class="form-field">
                            <label for="admin-member-subject">Sujeito (sub / agent-id)</label>
                            <input id="admin-member-subject" type="text" class="form-input" maxlength="120" required placeholder="codex-agent">
                        </div>
                        <div class="form-field">
                            <label for="admin-member-role">Papel</label>
                            <select id="admin-member-role" class="form-input">
                                <option value="Reader">Reader (Leitura)</option>
                                <option value="Contributor" selected>Contributor (Propõe decisões)</option>
                                <option value="Maintainer">Maintainer (Aprova &amp; Administra)</option>
                            </select>
                        </div>
                        <div>
                            <button type="submit" class="btn-macos btn-macos-primary">Salvar Vínculo</button>
                        </div>
                    </form>
                </div>

                ${vinculos.length === 0 ? `
                    <div class="panel-card" style="text-align:center;color:var(--text-tertiary);">
                        ${detalhes ? 'Nenhum vínculo cadastrado.' : 'Carregando vínculos do workspace...'}
                    </div>
                ` : vinculos.map(m => `
                    <div class="overview-list-row" style="cursor:default;">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(m.subject)}</span>
                            <span class="overview-row-sub">Emissor: <code>${escapeHtml(m.issuer)}</code> &bull; ${m.isActive ? 'Ativo' : 'Inativo'}</span>
                        </div>
                        <div class="overview-row-right">
                            <span class="status-pill ${m.role === 'Maintainer' ? 'active-state' : (m.role === 'Contributor' ? 'proposed-state' : 'reported-state')}">${escapeHtml(m.role)}</span>
                            <button type="button" class="btn-macos btn-macos-danger btn-macos-sm"
                                data-action="delete-membership"
                                data-membership-id="${escapeHtml(m.membershipId)}"
                                data-subject="${escapeHtml(m.subject)}">
                                ${Icons.trash} Remover
                            </button>
                        </div>
                    </div>
                `).join('')}
            </div>
        </div>
    `;
}

async function carregarAdminWorkspace(wsId) {
    adminLoadingWsId = wsId;
    try {
        const res = await buscarDetalhesWorkspaceAdmin(wsId);
        if (res.status === 401) {
            redirecionarParaLogin();
            return;
        }
        if (res.ok && res.dados) {
            appStore.set('adminDetails', res.dados);
        }
    } finally {
        adminLoadingWsId = null;
    }
}

async function executarCriacaoProjeto(form) {
    const ws = appStore.get('workspace');
    const nameEl = document.getElementById('admin-proj-name');
    const slugEl = document.getElementById('admin-proj-slug');
    if (!ws || !nameEl || !slugEl) return;

    const res = await criarProjetoAdmin(ws.id, nameEl.value.trim(), slugEl.value.trim(), appStore.get('csrf'));
    if (res.status === 401) {
        redirecionarParaLogin();
        return;
    }
    if (!res.ok) {
        mostrarToast((res.erro && res.erro.message) || 'Não foi possível criar o projeto.', 'erro');
        return;
    }
    mostrarToast('Projeto criado com sucesso.', 'sucesso');
    form.reset();
    delete slugEl.dataset.edited;
    await recarregarEstado();
    await carregarAdminWorkspace(ws.id);
}

async function executarSalvarVinculo(form) {
    const ws = appStore.get('workspace');
    const issEl = document.getElementById('admin-member-issuer');
    const subEl = document.getElementById('admin-member-subject');
    const roleEl = document.getElementById('admin-member-role');
    if (!ws || !issEl || !subEl || !roleEl) return;

    const res = await configurarVinculoAdmin(ws.id, issEl.value.trim(), subEl.value.trim(), roleEl.value, true, appStore.get('csrf'));
    if (res.status === 401) {
        redirecionarParaLogin();
        return;
    }
    if (!res.ok) {
        mostrarToast((res.erro && res.erro.message) || 'Não foi possível configurar o vínculo.', 'erro');
        return;
    }
    mostrarToast(`Vínculo de '${subEl.value.trim()}' salvo como ${roleEl.value}.`, 'sucesso');
    subEl.value = '';
    await carregarAdminWorkspace(ws.id);
}

// --- Configurações ---

function renderSettingsView(container) {
    const isDark = appStore.get('theme') === 'dark';
    const isInspector = appStore.get('inspectorOpen');
    const usuario = appStore.get('usuario') || {};

    container.innerHTML = `
        <div class="settings-view">
            <h1 class="overview-title" style="margin-bottom: 1.5rem;">Preferências do Sistema</h1>

            <div class="settings-group">
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Modo Escuro (Dark Mode)</span>
                        <span class="settings-caption">Ajusta o contraste do sistema entre tema escuro e claro.</span>
                    </div>
                    <label class="macos-switch">
                        <input type="checkbox" id="switch-theme" data-action="toggle-theme" ${isDark ? 'checked' : ''}>
                        <span class="macos-slider"></span>
                    </label>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Painel Inspetor Lateral</span>
                        <span class="settings-caption">Exibe metadados técnicos, IDs e garantias à direita da janela principal.</span>
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
                        <span class="settings-label">Identidade do Revisor</span>
                        <span class="settings-caption">Usuário autenticado na sessão web atual.</span>
                    </div>
                    <span class="mono-tag">${escapeHtml(usuario.sujeito || '—')}</span>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Papel no Espaço de Trabalho</span>
                        <span class="settings-caption">${escapeHtml(podeRevisar() ? 'Pode aprovar, rejeitar, restaurar versões e administrar.' : AVISO_SOMENTE_MAINTAINERS + '.')}</span>
                    </div>
                    <span class="mono-tag">${escapeHtml(usuario.papel || '—')}</span>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Emissor (Issuer)</span>
                        <span class="settings-caption">Provedor de identidade da sessão.</span>
                    </div>
                    <span class="mono-tag">${escapeHtml(usuario.emissor || '—')}</span>
                </div>
            </div>
        </div>
    `;
}

// --- Inspetor Lateral ---

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
            <div class="doc-section-title">Inspetor de Metadados</div>
            <div style="color:var(--text-tertiary);font-size:0.82rem;line-height:1.5;">
                Selecione uma proposta, decisão ou incidente para visualizar metadados técnicos, IDs e proveniência.
            </div>
        `;
        return;
    }

    const ehProposta = view === 'reviews';
    const fluxoId = item.topicoId || item.incidenteId;

    inspector.innerHTML = `
        <div class="doc-section-title" style="margin-bottom:1.15rem;">Inspetor de Metadados</div>

        ${campoInspector('Entidade', tipo)}
        ${campoInspector('Projeto', nomeProjeto(item.projetoId))}
        ${campoInspector('ID do Projeto', item.projetoId, true)}
        ${campoInspector('ID do Tópico / Fluxo', fluxoId, true)}
        ${item.chaveTopico ? campoInspector('Chave Canônica', item.chaveTopico, true) : ''}
        ${campoInspector('Versão do Fluxo', `v${item.versaoFluxo ?? '—'}`, true)}
        ${ehProposta && item.propostaPendenteId ? campoInspector('ID da Proposta', item.propostaPendenteId, true) : ''}
        ${ehProposta ? campoInspector('Autor', item.autorSujeito || '—', true) : ''}
        ${ehProposta ? campoInspector('Cliente MCP', item.clienteId || 'não informado', true) : ''}
        ${ehProposta ? campoInspector('Proposto em', formatarData(item.propostaEm, '—')) : ''}
        ${campoInspector('Evidências', `${(Array.isArray(item.evidencias) ? item.evidencias : []).length} link(s) HTTPS`)}

        <div class="inspector-field">
            <div class="inspector-field-label">Garantia Arquitetural</div>
            <div class="inspector-guarantee">${Icons.shieldCheck} Isolamento RLS &amp; Event Sourced</div>
        </div>
    `;
}

// ---------------------------------------------------------------------------
// Modais (aprovar, rejeitar, restaurar, remover vínculo)
// ---------------------------------------------------------------------------

function setupModals() {
    const backdrop = document.getElementById('modal-dialog');
    if (backdrop) {
        backdrop.addEventListener('click', (e) => {
            if (e.target === backdrop) fecharModal();
        });
    }

    const cancelBtn = document.getElementById('modal-cancel-btn');
    if (cancelBtn) cancelBtn.addEventListener('click', fecharModal);

    const confirmBtn = document.getElementById('modal-confirm-btn');
    if (confirmBtn) confirmBtn.addEventListener('click', confirmarOperacaoModal);

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
        ? `Esta proposta será promovida a decisão vigente do tópico "${chave}", substituindo "${vigente.titulo || 'sem título'}". Deseja confirmar?`
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

function abrirModalRestauracao(topicoId, versionToRestore) {
    if (!podeRevisar() || operacaoEmAndamento || !topicoId || !versionToRestore) return;
    const decisao = (appStore.get('decisoes') || []).find(d => d.topicoId === topicoId);
    if (!decisao) return;

    modalPayload = {
        tipo: 'restaurar',
        workspaceId: decisao.espacoTrabalhoId,
        projectId: decisao.projetoId,
        topicId: decisao.topicoId,
        versionToRestore,
        expectedVersion: decisao.versaoFluxo,
        idempotencyKey: gerarChaveIdempotencia()
    };

    document.getElementById('modal-title').textContent = `Restaurar Versão v${versionToRestore}`;
    document.getElementById('modal-desc').textContent =
        `Será criada uma nova proposta pendente no tópico "${decisao.chaveTopico}" baseada na decisão vigente da versão ${versionToRestore}. Ela precisará de aprovação final na aba Revisões.`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-primary';
    btn.textContent = 'Criar Proposta de Restauração';
    definirModalOcupado(false);
    document.getElementById('modal-dialog').classList.add('visible');
    setTimeout(() => btn.focus(), 50);
}

function abrirModalRemoverVinculo(membershipId, subject) {
    const ws = appStore.get('workspace');
    if (!ws || !podeRevisar() || operacaoEmAndamento || !membershipId) return;

    modalPayload = {
        tipo: 'remover-vinculo',
        workspaceId: ws.id,
        membershipId,
        subject: subject || membershipId
    };

    document.getElementById('modal-title').textContent = 'Remover Vínculo';
    document.getElementById('modal-desc').textContent =
        `Revogar imediatamente o acesso de "${subject || membershipId}" ao workspace "${ws.nome || ws.slug}"?`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-danger';
    btn.textContent = 'Remover Vínculo';
    definirModalOcupado(false);
    document.getElementById('modal-dialog').classList.add('visible');
    setTimeout(() => btn.focus(), 50);
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

    if (p.tipo === 'remover-vinculo') {
        definirModalOcupado(true);
        try {
            const res = await removerVinculoAdmin(p.workspaceId, p.membershipId, appStore.get('csrf'));
            definirModalOcupado(false);
            fecharModal();
            if (res.status === 401) { redirecionarParaLogin(); return; }
            if (!res.ok) {
                mostrarToast((res.erro && res.erro.message) || 'Erro ao remover vínculo.', 'erro');
                return;
            }
            mostrarToast(`Vínculo de "${p.subject}" removido.`, 'sucesso');
            await carregarAdminWorkspace(p.workspaceId);
        } catch (e) {
            definirModalOcupado(false);
            mostrarToast('Erro de comunicação ao remover vínculo.', 'erro');
        }
        return;
    }

    let url;
    let corpo;
    let mensagemSucesso;
    let irParaRevisoes = false;

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
        mensagemSucesso = 'Proposta rejeitada e registrada na trilha de auditoria.';
    } else if (p.tipo === 'restaurar') {
        url = '/api/review/restore';
        corpo = {
            workspaceId: p.workspaceId,
            projectId: p.projectId,
            topicId: p.topicId,
            versionToRestore: p.versionToRestore,
            expectedVersion: p.expectedVersion,
            idempotencyKey: p.idempotencyKey
        };
        mensagemSucesso = 'Proposta de restauração criada. Aprove-a na aba Revisões.';
        irParaRevisoes = true;
    } else {
        return;
    }

    definirModalOcupado(true);
    let resultado;
    try {
        resultado = await postarJson(url, corpo, appStore.get('csrf'));
    } catch (e) {
        definirModalOcupado(false);
        mostrarToast('Erro de comunicação com o servidor. Tente novamente.', 'erro');
        return;
    }

    definirModalOcupado(false);
    fecharModal();
    await tratarRespostaRevisao(resultado, mensagemSucesso);
    if (irParaRevisoes && resultado.ok) {
        appStore.set('selectedReviewId', p.topicId);
        appStore.set('activeView', 'reviews');
    }
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
            mostrarToast('Sem permissão.', 'erro');
            return;
        case 409:
            mostrarToast(mensagemServidor || 'Conflito de versão: o tópico foi alterado por outra operação.', 'erro');
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
