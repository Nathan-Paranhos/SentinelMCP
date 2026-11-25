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
    atualizarWorkspaceAdmin,
    criarWorkspaceAdmin,
    criarProjetoAdmin,
    removerProjetoAdmin,
    configurarVinculoAdmin,
    removerVinculoAdmin,
    listarChavesApiAdmin,
    criarChaveApiAdmin,
    revogarChaveApiAdmin,
    listarUsuariosAdmin,
    criarUsuarioAdmin,
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
let mcpKeysLoadingWsId = null;
let filtroAdminProjetoSlug = '';

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

    appStore.subscribe('activeView', () => {
        appStore.set('mobileDetailOpen', false);
        if (window.innerWidth <= 1080 && !appStore.get('sidebarCollapsed')) {
            appStore.set('sidebarCollapsed', true);
        }
        renderActiveView();
    });
    appStore.subscribe('selectedReviewId', renderActiveView);
    appStore.subscribe('selectedDecisionId', renderActiveView);
    appStore.subscribe('selectedIncidentId', renderActiveView);
    appStore.subscribe('mobileDetailOpen', renderActiveView);
    appStore.subscribe('timelineFilter', renderActiveView);
    appStore.subscribe('activeMcpClient', renderActiveView);
    appStore.subscribe('adminDetails', () => {
        if (appStore.get('activeView') === 'admin') renderActiveView();
    });
    appStore.subscribe('apiKeys', () => {
        const view = appStore.get('activeView');
        if (view === 'admin' || view === 'mcp' || view === 'overview') renderActiveView();
    });
    appStore.subscribe('localUsers', () => {
        if (appStore.get('activeView') === 'admin') renderActiveView();
    });
    appStore.subscribe('generatedApiToken', () => {
        const view = appStore.get('activeView');
        if (view === 'mcp' || view === 'admin') renderActiveView();
    });
    appStore.subscribe('receiptPreview', () => {
        if (appStore.get('activeView') === 'mcp') renderActiveView();
    });
    appStore.subscribe('sidebarCollapsed', updateSidebarState);
    appStore.subscribe('inspectorOpen', updateInspectorState);
    appStore.subscribe('theme', updateThemeToggleBtn);
    appStore.subscribe('theme', rerenderSettingsIfActive);
    appStore.subscribe('inspectorOpen', rerenderSettingsIfActive);

    let ultimaLarguraViewport = window.innerWidth;
    window.addEventListener('resize', () => {
        const atual = window.innerWidth;
        if (ultimaLarguraViewport > 1080 && atual <= 1080 && !appStore.get('sidebarCollapsed')) {
            appStore.set('sidebarCollapsed', true);
        }
        if (ultimaLarguraViewport > 1280 && atual <= 1280 && appStore.get('inspectorOpen')) {
            appStore.set('inspectorOpen', false);
        }
        ultimaLarguraViewport = atual;
        atualizarBackdropDrawers();
    });

    renderSessionChrome();
    updateSidebarState(appStore.get('sidebarCollapsed'));
    updateInspectorState(appStore.get('inspectorOpen'));
    renderActiveView();
    updateThemeToggleBtn();

    const ws = appStore.get('workspace');
    if (ws && podeRevisar()) {
        carregarChavesApiWorkspace(ws.id);
    }
}

function aplicarEstado(dados) {
    timelineCache.clear();
    appStore.set('adminDetails', null);
    appStore.set('apiKeys', []);
    appStore.set('localUsers', []);
    appStore.initData(dados);
    renderSessionChrome();
    renderActiveView();
    const ws = appStore.get('workspace');
    if (ws && podeRevisar()) {
        carregarChavesApiWorkspace(ws.id);
    }
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

    const closeBtn = document.getElementById('sidebar-close-btn');
    if (closeBtn) {
        closeBtn.addEventListener('click', () => {
            appStore.set('sidebarCollapsed', true);
        });
    }

    const backdrop = document.getElementById('drawer-backdrop');
    if (backdrop) {
        backdrop.addEventListener('click', () => {
            if (window.innerWidth <= 1080 && !appStore.get('sidebarCollapsed')) {
                appStore.set('sidebarCollapsed', true);
            }
            if (window.innerWidth <= 1280 && appStore.get('inspectorOpen')) {
                appStore.set('inspectorOpen', false);
            }
        });
    }
}

function atualizarBackdropDrawers() {
    const backdrop = document.getElementById('drawer-backdrop');
    if (!backdrop) return;
    const sidebarOverlay = window.innerWidth <= 1080 && !appStore.get('sidebarCollapsed');
    const inspectorOverlay = window.innerWidth <= 1280 && appStore.get('inspectorOpen');
    backdrop.classList.toggle('visible', !!(sidebarOverlay || inspectorOverlay));
}

function updateSidebarState(collapsed) {
    const sidebar = document.getElementById('macos-sidebar');
    const btn = document.getElementById('sidebar-toggle-btn');
    if (sidebar) sidebar.classList.toggle('collapsed', !!collapsed);
    if (btn) {
        btn.classList.toggle('active', !collapsed);
        btn.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        const acaoRotulo = collapsed ? 'Expandir menu lateral' : 'Recolher menu lateral';
        btn.setAttribute('title', acaoRotulo);
        btn.setAttribute('aria-label', acaoRotulo);
        btn.innerHTML = `${collapsed ? Icons.chevronsRight : Icons.chevronsLeft}<span>Menu</span>`;
    }
    atualizarBackdropDrawers();
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
    if (btn) {
        btn.classList.toggle('active', !!open);
        btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    }
    renderInspectorContent();
    atualizarBackdropDrawers();
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
                appStore.set('mobileDetailOpen', true);
                break;
            case 'select-decision':
                appStore.set('selectedDecisionId', ds.topicId || null);
                appStore.set('mobileDetailOpen', true);
                break;
            case 'select-incident':
                appStore.set('selectedIncidentId', ds.incidentId || null);
                appStore.set('mobileDetailOpen', true);
                break;
            case 'close-mobile-detail':
                appStore.set('mobileDetailOpen', false);
                break;
            case 'close-inspector':
                appStore.set('inspectorOpen', false);
                break;
            case 'toggle-inspector-btn':
                appStore.set('inspectorOpen', !appStore.get('inspectorOpen'));
                break;
            case 'open-review':
                appStore.set('selectedReviewId', ds.topicId || null);
                appStore.set('activeView', 'reviews');
                appStore.set('mobileDetailOpen', true);
                break;
            case 'open-decision':
                appStore.set('selectedDecisionId', ds.topicId || null);
                appStore.set('activeView', 'decisions');
                appStore.set('mobileDetailOpen', true);
                break;
            case 'open-incident':
                appStore.set('selectedIncidentId', ds.incidentId || null);
                appStore.set('activeView', 'incidents');
                appStore.set('mobileDetailOpen', true);
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
            case 'dismiss-generated-token':
                appStore.set('generatedApiToken', null);
                break;
            case 'delete-project':
                abrirModalExcluirProjeto(ds.projectId, ds.projectSlug, ds.projectName);
                break;
            case 'revoke-api-key':
                abrirModalRevogarChaveApi(ds.keyId, ds.keyName);
                break;
            case 'delete-membership':
                abrirModalRemoverVinculo(ds.membershipId, ds.subject);
                break;
            case 'toggle-password': {
                const input = ds.targetInput ? document.getElementById(ds.targetInput) : null;
                if (input) {
                    const mostrar = input.type === 'password';
                    input.type = mostrar ? 'text' : 'password';
                    alvo.setAttribute('aria-pressed', mostrar ? 'true' : 'false');
                    alvo.setAttribute('aria-label', mostrar ? 'Ocultar senha' : 'Mostrar senha');
                    alvo.setAttribute('title', mostrar ? 'Ocultar senha' : 'Mostrar senha');
                    alvo.innerHTML = mostrar ? Icons.eyeOff : Icons.eye;
                }
                break;
            }
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
        } else if (alvo.id === 'admin-new-ws-name') {
            const slugEl = document.getElementById('admin-new-ws-slug');
            if (slugEl && !slugEl.dataset.edited) {
                slugEl.value = gerarSlugSugerido(alvo.value);
            }
        } else if (alvo.id === 'admin-new-ws-slug') {
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
        } else if (acao === 'save-workspace-name') {
            executarAtualizacaoWorkspace(form);
        } else if (acao === 'create-workspace') {
            executarCriacaoWorkspace(form);
        } else if (acao === 'create-project') {
            executarCriacaoProjeto(form);
        } else if (acao === 'save-membership') {
            executarSalvarVinculo(form);
        } else if (acao === 'create-api-key') {
            executarCriarChaveApi(form);
        } else if (acao === 'create-local-user') {
            executarCriarUsuarioLocal(form);
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
    const apiKeys = appStore.get('apiKeys') || [];
    const sistema = appStore.get('sistema') || {};
    const ws = appStore.get('workspace');
    const wsNome = ws ? (ws.nome || ws.slug) : 'Workspace';
    const temAtividade = propostas.length + decisoes.length + incidentes.length > 0;

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Workspace: ${escapeHtml(wsNome)} &bull; ${projetos.length} projeto(s) &bull; ${escapeHtml(sistema.armazenamento || 'Local')}</div>
                <h1 class="overview-title">Como funciona o Sentinel-MCP</h1>
                <p class="overview-desc">
                    Memória arquitetural governada para seus agentes de IA. Os modelos consultam e propõem via protocolo MCP; apenas revisores humanos consolidam decisões neste painel.
                </p>
            </div>

            <div class="flow-guide-grid">
                <div class="flow-guide-step">
                    <span class="flow-step-num">PASSO 1 &bull; CHAVE REAL</span>
                    <div class="flow-step-title">Conectar IA via MCP</div>
                    <div class="flow-step-desc">Gere uma chave <code>snt_...</code> (SHA-256) na aba <strong>Conectar IAs (MCP)</strong> para Codex, Claude, Cursor ou Gemini.</div>
                </div>
                <div class="flow-guide-step">
                    <span class="flow-step-num">PASSO 2 &bull; CONSULTA</span>
                    <div class="flow-step-title">IA lê contexto e recibo</div>
                    <div class="flow-step-desc">O agente chama <code>list_workspaces</code> e <code>query_context</code> antes de alterar código e recebe um comprovante SHA-256.</div>
                </div>
                <div class="flow-guide-step">
                    <span class="flow-step-num">PASSO 3 &bull; PROPOSTA</span>
                    <div class="flow-step-title">Proposta ou incidente</div>
                    <div class="flow-step-desc">Com <code>propose_decision</code> ou <code>log_incident</code>, o agente registra alterações sem nunca conseguir autoaprovar.</div>
                </div>
                <div class="flow-guide-step">
                    <span class="flow-step-num">PASSO 4 &bull; REVISÃO</span>
                    <div class="flow-step-title">Você aprova ou rejeita</div>
                    <div class="flow-step-desc">Em <strong>Revisões Pendentes</strong>, compare lado a lado com a versão vigente e aprove, rejeite ou restaure versões.</div>
                </div>
            </div>

            <div class="callout-guide">
                <div>
                    <strong>Primeiros passos neste workspace:</strong>
                    ${projetos.length > 0 ? '&#10003; Projeto ativo configurado' : '&#9675; Crie um projeto em Administração'} &nbsp;&bull;&nbsp;
                    ${apiKeys.length > 0 ? `&#10003; ${apiKeys.length} chave(s) MCP gerada(s)` : '&#9675; Gere uma chave de API MCP para o Codex'} &nbsp;&bull;&nbsp;
                    ${temAtividade ? '&#10003; Memória ativa recebendo eventos' : '&#9675; Envie a primeira proposta ou incidente pelo agente'}
                </div>
                <div class="tech-keys-wrap">
                    <button type="button" class="btn-macos btn-macos-primary btn-macos-sm" data-action="navigate" data-view="mcp">${Icons.key} Conectar IAs</button>
                    <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="admin">Gerenciar Projetos</button>
                </div>
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
                        <div class="overview-section-title">Identificadores do Workspace (UUIDs para Clientes MCP)</div>
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
                        Nenhuma proposta pendente. Quando um agente chamar <code>propose_decision</code>, ela aparecerá aqui para revisão.
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
                        Nenhuma decisão vigente consolidada ainda.
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

function renderDetailNavBar(rotuloLista) {
    const inspecionando = !!appStore.get('inspectorOpen');
    return `
        <div class="detail-nav-bar">
            <div class="tech-keys-wrap">
                <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm mobile-only-back-btn" data-action="close-mobile-detail">
                    ${Icons.arrowLeft} ${escapeHtml(rotuloLista)}
                </button>
                <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="overview">
                    Visão Geral
                </button>
            </div>
            <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="toggle-inspector-btn">
                ${inspecionando ? 'Fechar Inspetor' : 'Inspecionar IDs'}
            </button>
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
    const detalheAberto = !!appStore.get('mobileDetailOpen');

    container.innerHTML = `
        <div class="split-view ${detalheAberto ? 'mobile-detail-open' : ''}">
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
        ${renderDetailNavBar('Voltar à lista de Propostas')}
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
    const detalheAberto = !!appStore.get('mobileDetailOpen');

    container.innerHTML = `
        <div class="split-view ${detalheAberto ? 'mobile-detail-open' : ''}">
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
        ${renderDetailNavBar('Voltar à lista de Decisões')}
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
    const detalheAberto = !!appStore.get('mobileDetailOpen');

    container.innerHTML = `
        <div class="split-view ${detalheAberto ? 'mobile-detail-open' : ''}">
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
        ${renderDetailNavBar('Voltar à lista de Incidentes')}
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                ${pillSituacao(SITUACAO_INCIDENTE, inc.situacao, SITUACAO_INCIDENTE.reported)}
                <span class="mono-tag">projeto: ${escapeHtml(nomeProjeto(inc.projetoId))}</span>
                <button type="button" class="copy-chip" title="Copiar ID completo: ${escapeHtml(inc.incidenteId)}" data-action="copy-text" data-copy-value="${escapeHtml(inc.incidenteId)}" data-copy-label="Incident ID">
                    ${Icons.copy} ID: ${escapeHtml(String(inc.incidenteId).slice(0, 8))}...
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

function gerarConfigClienteMcp(cliente, origin, tokenChave, wsId, projId) {
    const mcpUrl = `${origin}/mcp`;
    const chave = tokenChave || '<SUA_CHAVE_API_SENTINEL>';
    const ehPostgres = String(appStore.get('sistema')?.armazenamento || '').toLowerCase() === 'postgresql';
    switch (cliente) {
        case 'codex':
            return {
                titulo: 'OpenAI Codex (CLI & Extensão VS Code / ~/.codex/config.toml)',
                codigo: `# Opção A — ~/.codex/config.toml com header explícito (recomendada no Windows / VS Code):
[mcp_servers.sentinel]
url = "${mcpUrl}"

[mcp_servers.sentinel.http_headers]
Authorization = "Bearer ${chave}"

# Opção B — Codex CLI via variável de ambiente:
# $env:SENTINEL_MCP_TOKEN = "${chave}"
# codex mcp add sentinel --url ${mcpUrl} --bearer-token-env-var SENTINEL_MCP_TOKEN

# Opção C — Transporte local STDIO (sem porta HTTP; --no-launch-profile mantém stdout limpo):
# [mcp_servers.sentinel]
# command = "dotnet"
# args = ["run", "--no-build", "--no-launch-profile", "--project", "<CAMINHO_REPO>/src/Sentinel.Api", "--", "--stdio", "--token", "${chave}"]${ehPostgres ? '' : '\n# [mcp_servers.sentinel.env]\n# ASPNETCORE_ENVIRONMENT = "Development"'}

# Primeiro prompt recomendado no Codex:
# "Chame list_workspaces e query_context no Sentinel (workspaceId=${wsId}, projectId=${projId})."`
            };
        case 'claude':
            return {
                titulo: 'Claude Code (Streamable HTTP ou STDIO)',
                codigo: `# HTTP:
claude mcp add --transport http sentinel ${mcpUrl} \\
  --header "Authorization: Bearer ${chave}"

# STDIO local:
# claude mcp add sentinel ${ehPostgres ? '' : '-e ASPNETCORE_ENVIRONMENT=Development '}-- dotnet run --no-build --no-launch-profile --project ./src/Sentinel.Api -- --stdio --token "${chave}"`
            };
        case 'cursor':
            return {
                titulo: '.cursor/mcp.json',
                codigo: JSON.stringify({
                    mcpServers: {
                        sentinel: {
                            url: mcpUrl,
                            headers: { Authorization: `Bearer ${chave}` }
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
                            headers: { Authorization: `Bearer ${chave}` }
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
                            headers: { Authorization: `Bearer ${chave}` }
                        }
                    }
                }, null, 2)
            };
        default:
            return {
                titulo: 'HTTP JSON-RPC direto (curl / PowerShell)',
                codigo: `curl -X POST ${mcpUrl} \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer ${chave}" \\
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_workspaces","arguments":{}}}'`
            };
    }
}

function renderAvisoChaveGeradaHtml() {
    const gerada = appStore.get('generatedApiToken');
    if (!gerada || !gerada.token) return '';
    return `
        <div class="token-reveal-box" role="status">
            <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;">
                <strong>Chave gerada (${escapeHtml(gerada.name || 'API Key')}): copie agora — ela não será exibida novamente!</strong>
                <button type="button" class="panel-close-btn" data-action="dismiss-generated-token" title="Fechar aviso" aria-label="Fechar aviso da chave">&times;</button>
            </div>
            <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">
                <code class="mono-tag" style="word-break:break-all;color:var(--text-primary);">${escapeHtml(gerada.token)}</code>
                <button type="button" class="btn-macos btn-macos-primary btn-macos-sm" data-action="copy-text" data-copy-value="${escapeHtml(gerada.token)}" data-copy-label="Chave de API">
                    ${Icons.copy} Copiar Chave
                </button>
            </div>
            <div style="font-size:0.78rem;color:var(--text-secondary);">Os blocos de configuração abaixo já foram preenchidos automaticamente com esta chave para você colar no Codex ou outro cliente.</div>
        </div>
    `;
}

function renderMcpView(container) {
    const ws = appStore.get('workspace');
    const projetos = appStore.get('projetos') || [];
    const wsId = ws ? ws.id : '<WORKSPACE_ID>';
    const projId = projetos.length > 0 ? projetos[0].id : '<PROJECT_ID>';
    const origin = window.location.origin;
    const clienteAtivo = appStore.get('activeMcpClient') || 'codex';
    const gerada = appStore.get('generatedApiToken');
    const chaveAtiva = gerada && gerada.token ? gerada.token : '';
    const snippet = gerarConfigClienteMcp(clienteAtivo, origin, chaveAtiva, wsId, projId);
    const chaves = appStore.get('apiKeys') || [];
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
        { name: 'list_workspaces', mode: 'readOnly', scope: 'read:memory', desc: 'Descobre workspaces, papéis e projetos acessíveis à chave do agente sem precisar decorar UUIDs.' },
        { name: 'query_context', mode: 'readOnly', scope: 'read:memory', desc: 'Busca decisões vigentes e incidentes relevantes (priorizando technicalKeys exatas) e emite comprovante SHA-256.' },
        { name: 'get_timeline', mode: 'readOnly', scope: 'read:memory', desc: 'Reconstitui deterministicamente o estado de uma decisão ou incidente em qualquer versão histórica.' },
        { name: 'get_context_receipt', mode: 'readOnly', scope: 'read:memory', desc: 'Audita o comprovante de uma consulta recalculando o digest SHA-256 sobre os itens retornados.' },
        { name: 'propose_decision', mode: 'write', scope: 'write:decision', desc: 'Submete uma proposta arquitetural com expectedVersion, idempotencyKey e evidências HTTPS para aprovação humana.' },
        { name: 'log_incident', mode: 'write', scope: 'write:incident', desc: 'Registra incidente técnico com chaves técnicas, data da ocorrência e evidências HTTPS (RCA/PR).' }
    ];

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">MCP 2025-11-25 &bull; Streamable HTTP, SDK SSE &amp; STDIO</div>
                <h1 class="overview-title">Conectar IAs ao Sentinel-MCP</h1>
                <p class="overview-desc">
                    Gere uma chave de API real (<code>snt_...</code>, armazenada apenas como hash SHA-256) e copie a configuração pronta para
                    <strong>OpenAI Codex</strong>, <strong>Claude Code</strong>, <strong>Cursor</strong>, <strong>Gemini CLI</strong> ou <strong>VS Code</strong>.
                    Endpoints disponíveis: <code>POST ${escapeHtml(origin)}/mcp</code> (stateless), <code>${escapeHtml(origin)}/mcp/sse</code> (SDK stateful) e <code>--stdio</code>.
                </p>
            </div>

            ${renderAvisoChaveGeradaHtml()}

            ${podeRevisar() ? `
                <div class="overview-section">
                    <div class="overview-section-header">
                        <div class="overview-section-title">1. Gerar Chave de API Real (${chaves.length} ativa(s))</div>
                        <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="admin">Gerenciar Todas</button>
                    </div>
                    <div class="panel-card">
                        <form data-action="create-api-key" class="form-grid">
                            <div class="form-field">
                                <label for="mcp-key-name">Nome da Chave</label>
                                <input id="mcp-key-name" name="keyName" type="text" class="form-input" maxlength="100" required placeholder="Ex.: Codex Workstation">
                            </div>
                            <div class="form-field">
                                <label for="mcp-key-subject">Identificador do Agente (sub)</label>
                                <input id="mcp-key-subject" name="keySubject" type="text" class="form-input" maxlength="120" required placeholder="Ex.: codex-equipe">
                            </div>
                            <div class="form-field">
                                <label for="mcp-key-client">Cliente</label>
                                <select id="mcp-key-client" name="keyClient" class="form-input">
                                    <option value="codex">OpenAI Codex</option>
                                    <option value="claude-code">Claude Code</option>
                                    <option value="cursor">Cursor IDE</option>
                                    <option value="gemini-cli">Gemini CLI</option>
                                    <option value="vscode">VS Code Copilot</option>
                                    <option value="mcp-client">Outro Cliente MCP</option>
                                </select>
                            </div>
                            <div class="form-field">
                                <label for="mcp-key-role">Permissão</label>
                                <select id="mcp-key-role" name="keyRole" class="form-input">
                                    <option value="Contributor" selected>Contributor (Consulta + Propõe)</option>
                                    <option value="Reader">Reader (Somente Leitura)</option>
                                </select>
                            </div>
                            <div>
                                <button type="submit" class="btn-macos btn-macos-primary">${Icons.key} Gerar Chave Agora</button>
                            </div>
                        </form>
                    </div>
                </div>
            ` : ''}

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">2. Configuração Pronta por Cliente de IA</div>
                    <div class="tech-keys-wrap">
                        <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(origin)}/mcp" data-copy-label="URL MCP">
                            ${Icons.copy} ${escapeHtml(origin)}/mcp
                        </button>
                        ${ws ? `
                            <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(ws.id)}" data-copy-label="Workspace ID">
                                ${Icons.copy} WS: ${escapeHtml(ws.id.slice(0, 8))}...
                            </button>
                        ` : ''}
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
                    <div class="overview-section-title">3. Auditor de Comprovantes (SHA-256)</div>
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
                    <p class="overview-desc">A gestão de projetos, chaves de API e vínculos exige papel <strong>Maintainer</strong> neste espaço de trabalho.</p>
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
    const chaves = appStore.get('apiKeys') || [];
    const usuarios = appStore.get('localUsers') || [];
    const workspaces = appStore.get('workspaces') || [];
    const sistema = appStore.get('sistema') || {};
    const modoOidc = sistema.autenticacaoWeb === 'OIDC PKCE';

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Administração &bull; ${escapeHtml(ws ? ws.nome : '')}</div>
                <h1 class="overview-title">Workspaces, Projetos, Chaves &amp; Controle de Acesso</h1>
                <p class="overview-desc">Alterações em workspaces, projetos, chaves SHA-256 e contas locais são salvas automaticamente na persistência configurada.</p>
            </div>

            ${renderAvisoChaveGeradaHtml()}

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Workspaces Salvos &amp; Persistência (${workspaces.length})</div>
                    <span class="mono-tag">${escapeHtml(sistema.persistenciaLocal || sistema.armazenamento || 'Local')}</span>
                </div>

                <div class="callout-guide">
                    <div>
                        <strong>Persistência e Autenticação ativos:</strong>
                        Dados gravados em <code>${escapeHtml(sistema.persistenciaLocal || sistema.armazenamento || 'Local')}</code> &bull;
                        Sessão Web via <strong>${escapeHtml(sistema.autenticacaoWeb || 'Local PBKDF2')}</strong> (cookie HttpOnly + CSRF) &bull;
                        Agentes MCP autenticados apenas por chave <code>snt_...</code> (SHA-256) ou JWT.
                    </div>
                </div>

                ${ws ? `
                    <div class="panel-card">
                        <form data-action="save-workspace-name" class="form-grid">
                            <div class="form-field">
                                <label for="admin-ws-name">Nome do Workspace Atual (slug: ${escapeHtml(ws.slug)})</label>
                                <input id="admin-ws-name" type="text" class="form-input" maxlength="120" required value="${escapeHtml(ws.nome || '')}">
                            </div>
                            <div>
                                <button type="submit" class="btn-macos btn-macos-primary">Salvar Workspace</button>
                            </div>
                        </form>
                    </div>
                ` : ''}

                <div class="panel-card">
                    <form data-action="create-workspace" class="form-grid">
                        <div class="form-field">
                            <label for="admin-new-ws-name">Novo Workspace</label>
                            <input id="admin-new-ws-name" type="text" class="form-input" maxlength="120" required placeholder="Ex.: Produto Financeiro">
                        </div>
                        <div class="form-field">
                            <label for="admin-new-ws-slug">Slug ASCII</label>
                            <input id="admin-new-ws-slug" type="text" class="form-input" maxlength="80" required placeholder="produto-financeiro">
                        </div>
                        <div class="form-field">
                            <label for="admin-new-ws-key">Chave Admin (ADMIN_API_KEY)</label>
                            <div class="password-input-wrap">
                                <input id="admin-new-ws-key" type="password" class="form-input" autocomplete="off" required placeholder="Chave administrativa">
                                <button type="button" class="password-toggle-btn" data-action="toggle-password" data-target-input="admin-new-ws-key" aria-label="Mostrar chave" title="Mostrar chave" aria-pressed="false">${Icons.eye}</button>
                            </div>
                        </div>
                        <div>
                            <button type="submit" class="btn-macos btn-macos-secondary">Criar Novo Workspace</button>
                        </div>
                    </form>
                    <p style="margin:0.65rem 0 0;font-size:0.78rem;color:var(--text-secondary);">
                        ${sistema.ambiente === 'Development'
                            ? 'Criar outro workspace exige <code>ADMIN_API_KEY</code> (em Development local sem variável configurada, o padrão é <code>sentinel-admin-dev-key</code>). Você será vinculado automaticamente como Maintainer.'
                            : 'Criar outro workspace exige a chave administrativa definida em <code>ADMIN_API_KEY</code>. Você será vinculado automaticamente como Maintainer.'}
                    </p>
                </div>

                ${workspaces.map(w => {
                    const ativo = ws && w.id === ws.id;
                    return `
                        <div class="overview-list-row" style="cursor:default;">
                            <div class="overview-row-left">
                                <span class="overview-row-title">${escapeHtml(w.nome)} ${ativo ? '<span class="status-pill active-state">Atual</span>' : ''}</span>
                                <span class="overview-row-sub">slug: <code>${escapeHtml(w.slug)}</code></span>
                            </div>
                            <div class="overview-row-right">
                                <button type="button" class="copy-chip" data-action="copy-text" data-copy-value="${escapeHtml(w.id)}" data-copy-label="Workspace ID">
                                    ${Icons.copy} ${escapeHtml(String(w.id).slice(0, 8))}...
                                </button>
                                ${!ativo ? `<a class="btn-macos btn-macos-secondary btn-macos-sm" href="/review?workspaceId=${encodeURIComponent(w.id)}">Abrir</a>` : ''}
                            </div>
                        </div>
                    `;
                }).join('')}
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Projetos do Workspace (${projetos.length})</div>
                </div>

                <div class="panel-card">
                    <form data-action="create-project" class="form-grid">
                        <div class="form-field">
                            <label for="admin-proj-name">Nome do Projeto</label>
                            <input id="admin-proj-name" type="text" class="form-input" maxlength="120" required placeholder="Ex.: Plataforma Core">
                        </div>
                        <div class="form-field">
                            <label for="admin-proj-slug">Slug ASCII</label>
                            <input id="admin-proj-slug" type="text" class="form-input" maxlength="80" required placeholder="plataforma-core">
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
                                    ${Icons.copy} ${escapeHtml(String(id).slice(0, 8))}...
                                </button>
                                <button type="button" class="btn-macos btn-macos-danger btn-macos-sm"
                                    data-action="delete-project"
                                    data-project-id="${escapeHtml(id)}"
                                    data-project-slug="${escapeHtml(slug)}"
                                    data-project-name="${escapeHtml(nome)}">
                                    ${Icons.trash} Excluir
                                </button>
                            </div>
                        </div>
                    `;
                }).join('')}
            </div>

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Chaves de API para Agentes MCP (${chaves.length})</div>
                    <button type="button" class="btn-macos btn-macos-secondary btn-macos-sm" data-action="navigate" data-view="mcp">Ver Snippets Codex/Claude</button>
                </div>

                <div class="panel-card">
                    <form data-action="create-api-key" class="form-grid">
                        <div class="form-field">
                            <label for="admin-key-name">Nome da Chave</label>
                            <input id="admin-key-name" name="keyName" type="text" class="form-input" maxlength="100" required placeholder="Ex.: Codex CLI">
                        </div>
                        <div class="form-field">
                            <label for="admin-key-subject">Identificador (sub)</label>
                            <input id="admin-key-subject" name="keySubject" type="text" class="form-input" maxlength="120" required placeholder="Ex.: codex-equipe">
                        </div>
                        <div class="form-field">
                            <label for="admin-key-client">Cliente</label>
                            <select id="admin-key-client" name="keyClient" class="form-input">
                                <option value="codex">codex</option>
                                <option value="claude-code">claude-code</option>
                                <option value="cursor">cursor</option>
                                <option value="gemini-cli">gemini-cli</option>
                                <option value="vscode">vscode</option>
                            </select>
                        </div>
                        <div class="form-field">
                            <label for="admin-key-role">Papel</label>
                            <select id="admin-key-role" name="keyRole" class="form-input">
                                <option value="Contributor" selected>Contributor</option>
                                <option value="Reader">Reader</option>
                            </select>
                        </div>
                        <div>
                            <button type="submit" class="btn-macos btn-macos-primary">${Icons.key} Gerar Chave</button>
                        </div>
                    </form>
                </div>

                ${chaves.length === 0 ? `
                    <div class="panel-card" style="text-align:center;color:var(--text-tertiary);">
                        Nenhuma chave de API cadastrada neste workspace ainda.
                    </div>
                ` : chaves.map(k => `
                    <div class="overview-list-row" style="cursor:default;">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(k.name)} <code class="mono-tag">${escapeHtml(k.keyPrefix)}...</code></span>
                            <span class="overview-row-sub">
                                sub: <code>${escapeHtml(k.subject)}</code> &bull; cliente: <code>${escapeHtml(k.clientId || 'padrão')}</code> &bull; escopos: ${escapeHtml((k.scopes || []).join(', '))}
                                ${k.lastUsedAt ? ` &bull; último uso: ${escapeHtml(formatarData(k.lastUsedAt))}` : ''}
                            </span>
                        </div>
                        <div class="overview-row-right">
                            <button type="button" class="btn-macos btn-macos-danger btn-macos-sm"
                                data-action="revoke-api-key"
                                data-key-id="${escapeHtml(k.keyId)}"
                                data-key-name="${escapeHtml(k.name)}">
                                ${Icons.trash} Revogar
                            </button>
                        </div>
                    </div>
                `).join('')}
            </div>

            ${!modoOidc ? `
                <div class="overview-section">
                    <div class="overview-section-header">
                        <div class="overview-section-title">Usuários Web Locais (PBKDF2) (${usuarios.length})</div>
                    </div>

                    <div class="panel-card">
                        <form data-action="create-local-user" class="form-grid">
                            <div class="form-field">
                                <label for="admin-user-username">Usuário</label>
                                <input id="admin-user-username" type="text" class="form-input" maxlength="80" required placeholder="ex.: revisor.silva">
                            </div>
                            <div class="form-field">
                                <label for="admin-user-display">Nome de Exibição</label>
                                <input id="admin-user-display" type="text" class="form-input" maxlength="120" placeholder="Nome completo">
                            </div>
                            <div class="form-field">
                                <label for="admin-user-password">Senha (mín. 8 chars)</label>
                                <div class="password-input-wrap">
                                    <input id="admin-user-password" type="password" class="form-input" minlength="8" maxlength="128" autocomplete="new-password" required>
                                    <button type="button" class="password-toggle-btn" data-action="toggle-password" data-target-input="admin-user-password" aria-label="Mostrar senha" title="Mostrar senha" aria-pressed="false">${Icons.eye}</button>
                                </div>
                            </div>
                            <div class="form-field">
                                <label for="admin-user-confirm-password">Confirmar Senha</label>
                                <div class="password-input-wrap">
                                    <input id="admin-user-confirm-password" type="password" class="form-input" minlength="8" maxlength="128" autocomplete="new-password" required>
                                    <button type="button" class="password-toggle-btn" data-action="toggle-password" data-target-input="admin-user-confirm-password" aria-label="Mostrar senha" title="Mostrar senha" aria-pressed="false">${Icons.eye}</button>
                                </div>
                            </div>
                            <div class="form-field">
                                <label for="admin-user-role">Papel</label>
                                <select id="admin-user-role" class="form-input">
                                    <option value="Maintainer">Maintainer</option>
                                    <option value="Reader">Reader</option>
                                </select>
                            </div>
                            <div>
                                <button type="submit" class="btn-macos btn-macos-primary">Criar Usuário Web</button>
                            </div>
                        </form>
                    </div>

                    ${usuarios.map(u => `
                        <div class="overview-list-row" style="cursor:default;">
                            <div class="overview-row-left">
                                <span class="overview-row-title">${escapeHtml(u.displayName || u.username)} <code class="mono-tag">${escapeHtml(u.username)}</code></span>
                                <span class="overview-row-sub">Emissor: <code>${escapeHtml(u.issuer)}</code> &bull; Criado em ${escapeHtml(formatarData(u.createdAt))}</span>
                            </div>
                            <div class="overview-row-right">
                                <span class="status-pill active-state">Conta Local</span>
                            </div>
                        </div>
                    `).join('')}
                </div>
            ` : ''}

            <div class="overview-section">
                <div class="overview-section-header">
                    <div class="overview-section-title">Vínculos / Permissões por Identidade (${vinculos.length})</div>
                </div>

                <div class="panel-card">
                    <form data-action="save-membership" class="form-grid">
                        <div class="form-field">
                            <label for="admin-member-issuer">Emissor (Issuer URL)</label>
                            <input id="admin-member-issuer" type="url" class="form-input" required placeholder="https://seu-emissor-oidc-ou-local">
                        </div>
                        <div class="form-field">
                            <label for="admin-member-subject">Sujeito (sub / agent-id)</label>
                            <input id="admin-member-subject" type="text" class="form-input" maxlength="120" required placeholder="identificador-da-identidade">
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

async function carregarChavesApiWorkspace(wsId) {
    if (!wsId || !podeRevisar() || mcpKeysLoadingWsId === wsId) return;
    mcpKeysLoadingWsId = wsId;
    try {
        const [chavesRes, usersRes] = await Promise.all([
            listarChavesApiAdmin(wsId),
            listarUsuariosAdmin(wsId)
        ]);
        if (chavesRes.ok && Array.isArray(chavesRes.dados)) {
            appStore.set('apiKeys', chavesRes.dados);
        }
        if (usersRes.ok && Array.isArray(usersRes.dados)) {
            appStore.set('localUsers', usersRes.dados);
        }
    } finally {
        mcpKeysLoadingWsId = null;
    }
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
        await carregarChavesApiWorkspace(wsId);
    } finally {
        adminLoadingWsId = null;
    }
}

async function executarAtualizacaoWorkspace(form) {
    const ws = appStore.get('workspace');
    const nameEl = document.getElementById('admin-ws-name');
    if (!ws || !nameEl) return;

    const nome = nameEl.value.trim();
    const res = await atualizarWorkspaceAdmin(ws.id, nome, appStore.get('csrf'));
    if (res.status === 401) {
        redirecionarParaLogin();
        return;
    }
    if (!res.ok) {
        mostrarToast((res.erro && res.erro.message) || 'Não foi possível salvar o workspace.', 'erro');
        return;
    }
    mostrarToast('Workspace salvo com sucesso.', 'sucesso');
    await recarregarEstado();
    await carregarAdminWorkspace(ws.id);
}

async function executarCriacaoWorkspace(form) {
    const nameEl = document.getElementById('admin-new-ws-name');
    const slugEl = document.getElementById('admin-new-ws-slug');
    const keyEl = document.getElementById('admin-new-ws-key');
    const usuario = appStore.get('usuario') || {};
    if (!nameEl || !slugEl || !keyEl) return;

    const res = await criarWorkspaceAdmin(
        nameEl.value.trim(),
        slugEl.value.trim(),
        keyEl.value,
        usuario.sujeito || null,
        usuario.emissor || null
    );
    if (!res.ok || !res.corpo) {
        mostrarToast((res.erro && res.erro.message) || 'Não foi possível criar o workspace. Verifique ADMIN_API_KEY e o slug.', 'erro');
        return;
    }
    mostrarToast('Novo workspace criado e salvo com sucesso.', 'sucesso');
    form.reset();
    delete slugEl.dataset.edited;
    window.location.href = '/review?workspaceId=' + encodeURIComponent(res.corpo.workspaceId);
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

async function executarCriarChaveApi(form) {
    const ws = appStore.get('workspace');
    if (!ws) return;
    const nome = form.querySelector('[name="keyName"]')?.value?.trim() || '';
    const sujeito = form.querySelector('[name="keySubject"]')?.value?.trim() || '';
    const cliente = form.querySelector('[name="keyClient"]')?.value?.trim() || 'codex';
    const papel = form.querySelector('[name="keyRole"]')?.value || 'Contributor';
    const escopos = papel === 'Reader'
        ? ['read:memory']
        : ['read:memory', 'write:decision', 'write:incident'];

    const res = await criarChaveApiAdmin(ws.id, {
        name: nome,
        subject: sujeito,
        clientId: cliente,
        role: papel,
        scopes: escopos
    }, appStore.get('csrf'));

    if (res.status === 401) {
        redirecionarParaLogin();
        return;
    }
    if (!res.ok || !res.corpo) {
        mostrarToast((res.erro && res.erro.message) || 'Não foi possível gerar a chave de API.', 'erro');
        return;
    }

    appStore.set('generatedApiToken', {
        name: res.corpo.key?.name || nome,
        token: res.corpo.token
    });
    if (['codex', 'claude-code', 'cursor', 'gemini-cli', 'vscode'].includes(cliente)) {
        const mapaTab = cliente === 'claude-code' ? 'claude' : (cliente === 'gemini-cli' ? 'gemini' : cliente);
        appStore.set('activeMcpClient', mapaTab);
    }
    mostrarToast('Chave de API gerada. Copie o token exibido acima.', 'sucesso');
    form.reset();
    await carregarAdminWorkspace(ws.id);
}

async function executarCriarUsuarioLocal(form) {
    const ws = appStore.get('workspace');
    if (!ws) return;
    const username = document.getElementById('admin-user-username')?.value?.trim() || '';
    const displayName = document.getElementById('admin-user-display')?.value?.trim() || '';
    const passwordEl = document.getElementById('admin-user-password');
    const confirmEl = document.getElementById('admin-user-confirm-password');
    const password = passwordEl?.value || '';
    const confirmPassword = confirmEl?.value || '';
    const role = document.getElementById('admin-user-role')?.value || 'Reader';

    if (password !== confirmPassword) {
        mostrarToast('As senhas informadas não conferem.', 'erro');
        if (confirmEl) confirmEl.focus();
        return;
    }

    const res = await criarUsuarioAdmin(ws.id, { username, displayName, password, role }, appStore.get('csrf'));
    if (res.status === 401) {
        redirecionarParaLogin();
        return;
    }
    if (!res.ok) {
        mostrarToast((res.erro && res.erro.message) || 'Não foi possível criar o usuário local.', 'erro');
        return;
    }
    mostrarToast(`Usuário "${username}" criado com papel ${role}.`, 'sucesso');
    form.reset();
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

    const cabecalhoInspector = `
        <div class="inspector-top-header">
            <span class="inspector-top-title">Inspetor Técnico</span>
            <button type="button" class="panel-close-btn" data-action="close-inspector" title="Fechar inspetor lateral" aria-label="Fechar inspetor lateral">&times;</button>
        </div>
    `;

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
        const sistema = appStore.get('sistema') || {};
        const ws = appStore.get('workspace');
        inspector.innerHTML = `
            ${cabecalhoInspector}
            ${ws ? campoInspector('Workspace Atual', ws.nome || ws.slug) : ''}
            ${ws ? campoInspector('Workspace ID', ws.id, true) : ''}
            ${campoInspector('Armazenamento', sistema.armazenamento || 'Local')}
            ${campoInspector('Autenticação Web', sistema.autenticacaoWeb || 'Local PBKDF2')}
            <div style="color:var(--text-tertiary);font-size:0.81rem;line-height:1.5;margin-top:0.5rem;">
                Selecione uma proposta, decisão ou incidente para visualizar metadados técnicos, IDs e proveniência.
            </div>
        `;
        return;
    }

    const ehProposta = view === 'reviews';
    const fluxoId = item.topicoId || item.incidenteId;

    inspector.innerHTML = `
        ${cabecalhoInspector}

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
// Modais (aprovar, rejeitar, restaurar e exclusões com confirmação digitada)
// ---------------------------------------------------------------------------

function atualizarEstadoBotaoConfirmacao() {
    const confirmBtn = document.getElementById('modal-confirm-btn');
    const phraseWrap = document.getElementById('modal-confirm-phrase-wrap');
    const confirmInput = document.getElementById('modal-confirm-input');
    if (!confirmBtn) return;

    if (operacaoEmAndamento) {
        confirmBtn.disabled = true;
        return;
    }

    if (modalPayload && modalPayload.confirmPhrase && phraseWrap && phraseWrap.style.display !== 'none') {
        const digitado = (confirmInput ? confirmInput.value : '').trim();
        confirmBtn.disabled = digitado !== modalPayload.confirmPhrase;
    } else {
        confirmBtn.disabled = false;
    }
}

function setupModals() {
    const backdrop = document.getElementById('modal-dialog');
    if (backdrop) {
        backdrop.addEventListener('click', (e) => {
            if (e.target === backdrop) fecharModal();
        });
    }

    const closeX = document.getElementById('modal-close-x');
    if (closeX) closeX.addEventListener('click', fecharModal);

    const cancelBtn = document.getElementById('modal-cancel-btn');
    if (cancelBtn) cancelBtn.addEventListener('click', fecharModal);

    const confirmBtn = document.getElementById('modal-confirm-btn');
    if (confirmBtn) confirmBtn.addEventListener('click', confirmarOperacaoModal);

    const motivoInput = document.getElementById('modal-reason-input');
    if (motivoInput) {
        motivoInput.addEventListener('input', () => atualizarDicaMotivo(null));
    }

    const confirmInput = document.getElementById('modal-confirm-input');
    if (confirmInput) {
        confirmInput.addEventListener('input', atualizarEstadoBotaoConfirmacao);
    }

    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;
        if (modalPayload) {
            fecharModal();
            return;
        }
        if (window.innerWidth <= 1080 && !appStore.get('sidebarCollapsed')) {
            appStore.set('sidebarCollapsed', true);
        }
        if (window.innerWidth <= 1280 && appStore.get('inspectorOpen')) {
            appStore.set('inspectorOpen', false);
        }
    });
}

function ocultarConfirmacaoDigitada() {
    const wrap = document.getElementById('modal-confirm-phrase-wrap');
    const input = document.getElementById('modal-confirm-input');
    const expected = document.getElementById('modal-confirm-expected');
    if (wrap) wrap.style.display = 'none';
    if (input) input.value = '';
    if (expected) expected.textContent = '';
}

function exigirConfirmacaoDigitada(textoEsperado) {
    const wrap = document.getElementById('modal-confirm-phrase-wrap');
    const input = document.getElementById('modal-confirm-input');
    const expected = document.getElementById('modal-confirm-expected');
    if (wrap) wrap.style.display = 'flex';
    if (expected) expected.textContent = textoEsperado;
    if (input) {
        input.value = '';
        setTimeout(() => input.focus(), 60);
    }
    atualizarEstadoBotaoConfirmacao();
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
    ocultarConfirmacaoDigitada();

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
    ocultarConfirmacaoDigitada();

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
    ocultarConfirmacaoDigitada();

    document.getElementById('modal-title').textContent = `Restaurar Versão v${versionToRestore}`;
    document.getElementById('modal-desc').textContent =
        `Será criada uma nova proposta pendente no tópico "${decisao.chaveTopico}" baseada na decisão vigente da versão ${versionToRestore}. Ela precisará de aprovação final em Revisões Pendentes.`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-primary';
    btn.textContent = 'Criar Proposta de Restauração';
    definirModalOcupado(false);
    document.getElementById('modal-dialog').classList.add('visible');
    setTimeout(() => btn.focus(), 50);
}

function abrirModalExcluirProjeto(projectId, projectSlug, projectName) {
    const ws = appStore.get('workspace');
    if (!ws || !podeRevisar() || operacaoEmAndamento || !projectId) return;

    const frase = projectSlug || projectId;
    modalPayload = {
        tipo: 'excluir-projeto',
        workspaceId: ws.id,
        projectId,
        projectSlug: frase,
        confirmPhrase: frase
    };

    document.getElementById('modal-title').textContent = 'Excluir Projeto';
    document.getElementById('modal-desc').textContent =
        `Excluir o projeto "${projectName || frase}" do workspace? Projetos que já possuem decisões, incidentes ou eventos imutáveis são protegidos contra exclusão.`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-danger';
    btn.textContent = 'Confirmar Exclusão';
    definirModalOcupado(false);
    exigirConfirmacaoDigitada(frase);
    document.getElementById('modal-dialog').classList.add('visible');
}

function abrirModalRevogarChaveApi(keyId, keyName) {
    const ws = appStore.get('workspace');
    if (!ws || !podeRevisar() || operacaoEmAndamento || !keyId) return;

    const frase = keyName || keyId;
    modalPayload = {
        tipo: 'revogar-api-key',
        workspaceId: ws.id,
        keyId,
        keyName: frase,
        confirmPhrase: frase
    };

    document.getElementById('modal-title').textContent = 'Revogar Chave de API';
    document.getElementById('modal-desc').textContent =
        `Agentes MCP que utilizam "${frase}" perderão imediatamente o acesso ao Sentinel-MCP. Esta ação é permanente.`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-danger';
    btn.textContent = 'Revogar Permanentemente';
    definirModalOcupado(false);
    exigirConfirmacaoDigitada(frase);
    document.getElementById('modal-dialog').classList.add('visible');
}

function abrirModalRemoverVinculo(membershipId, subject) {
    const ws = appStore.get('workspace');
    if (!ws || !podeRevisar() || operacaoEmAndamento || !membershipId) return;

    const frase = subject || membershipId;
    modalPayload = {
        tipo: 'remover-vinculo',
        workspaceId: ws.id,
        membershipId,
        subject: frase,
        confirmPhrase: frase
    };

    document.getElementById('modal-title').textContent = 'Remover Vínculo de Acesso';
    document.getElementById('modal-desc').textContent =
        `Revogar imediatamente o acesso de "${frase}" ao workspace "${ws.nome || ws.slug}"?`;
    document.getElementById('modal-input-wrap').style.display = 'none';

    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-danger';
    btn.textContent = 'Remover Vínculo';
    definirModalOcupado(false);
    exigirConfirmacaoDigitada(frase);
    document.getElementById('modal-dialog').classList.add('visible');
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
    const closeX = document.getElementById('modal-close-x');
    const input = document.getElementById('modal-reason-input');
    const confirmInput = document.getElementById('modal-confirm-input');
    if (confirmBtn) {
        confirmBtn.setAttribute('aria-busy', ocupado ? 'true' : 'false');
    }
    if (cancelBtn) cancelBtn.disabled = ocupado;
    if (closeX) closeX.disabled = ocupado;
    if (input) input.readOnly = ocupado;
    if (confirmInput) confirmInput.readOnly = ocupado;
    atualizarEstadoBotaoConfirmacao();
}

function fecharModal() {
    if (operacaoEmAndamento) return;
    const dialog = document.getElementById('modal-dialog');
    if (dialog) dialog.classList.remove('visible');
    ocultarConfirmacaoDigitada();
    modalPayload = null;
}

async function confirmarOperacaoModal() {
    if (!modalPayload || operacaoEmAndamento) return;
    const p = modalPayload;

    if (p.confirmPhrase) {
        const digitado = (document.getElementById('modal-confirm-input')?.value || '').trim();
        if (digitado !== p.confirmPhrase) return;
    }

    if (p.tipo === 'remover-vinculo' || p.tipo === 'revogar-api-key' || p.tipo === 'excluir-projeto') {
        definirModalOcupado(true);
        try {
            let res;
            let sucessoMsg;
            if (p.tipo === 'remover-vinculo') {
                res = await removerVinculoAdmin(p.workspaceId, p.membershipId, appStore.get('csrf'));
                sucessoMsg = `Vínculo de "${p.subject}" removido.`;
            } else if (p.tipo === 'revogar-api-key') {
                res = await revogarChaveApiAdmin(p.workspaceId, p.keyId, appStore.get('csrf'));
                sucessoMsg = `Chave "${p.keyName}" revogada.`;
            } else {
                res = await removerProjetoAdmin(p.workspaceId, p.projectId, appStore.get('csrf'));
                sucessoMsg = `Projeto "${p.projectSlug}" excluído.`;
            }

            definirModalOcupado(false);
            fecharModal();
            if (res.status === 401) { redirecionarParaLogin(); return; }
            if (!res.ok) {
                mostrarToast((res.erro && res.erro.message) || 'Operação não concluída.', 'erro');
                return;
            }
            mostrarToast(sucessoMsg, 'sucesso');
            if (p.tipo === 'excluir-projeto') {
                await recarregarEstado();
            }
            await carregarAdminWorkspace(p.workspaceId);
        } catch (e) {
            definirModalOcupado(false);
            mostrarToast('Erro de comunicação ao executar exclusão.', 'erro');
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
