import { appStore } from './store.js';
import { Icons } from './icons.js';
import { CommandPalette } from './command-palette.js';

let commandPaletteInstance = null;
let modalPayload = null;

export function initApp(initialData, csrfToken) {
    window.__sentinelCsrf = csrfToken;
    appStore.initData(initialData);

    commandPaletteInstance = new CommandPalette();

    setupSidebar();
    setupToolbar();
    setupModals();
    setupInspector();

    appStore.subscribe('activeView', renderActiveView);
    appStore.subscribe('selectedReviewId', renderActiveView);
    appStore.subscribe('selectedDecisionId', renderActiveView);
    appStore.subscribe('selectedIncidentId', renderActiveView);
    appStore.subscribe('sidebarCollapsed', updateSidebarState);
    appStore.subscribe('inspectorOpen', updateInspectorState);
    appStore.subscribe('theme', updateThemeToggleBtn);

    renderActiveView();
    updateThemeToggleBtn();
}

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

function renderActiveView() {
    const view = appStore.get('activeView');
    const contentBody = document.getElementById('content-body');
    const toolbarTitle = document.getElementById('toolbar-view-title');

    document.querySelectorAll('[data-view-nav]').forEach(el => {
        el.classList.toggle('active', el.getAttribute('data-view-nav') === view);
    });

    const counts = {
        reviews: appStore.get('propostas').length,
        decisions: appStore.get('decisoes').length,
        incidents: appStore.get('incidentes').length
    };
    const revCountEl = document.getElementById('count-reviews');
    if (revCountEl) revCountEl.textContent = counts.reviews;
    const decCountEl = document.getElementById('count-decisions');
    if (decCountEl) decCountEl.textContent = counts.decisions;
    const incCountEl = document.getElementById('count-incidents');
    if (incCountEl) incCountEl.textContent = counts.incidents;

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
    const incidentes = appStore.get('incidentes');
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
                    <button class="btn-macos btn-macos-secondary" onclick="window.__sentinelAppStore.set('activeView', 'reviews')">Ver Todas</button>
                </div>
                ${propostas.length === 0 ? `
                    <div style="padding: 1.5rem; text-align: center; color: var(--text-tertiary); font-size: 0.88rem;">
                        Nenhuma proposta aguardando validação no momento.
                    </div>
                ` : propostas.map(p => `
                    <div class="overview-list-row" onclick="window.__sentinelSelectReview('${p.topicoId}')">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(p.titulo)}</span>
                            <span class="overview-row-sub">tópico: ${escapeHtml(p.chaveTopico)} • v${p.versaoFluxo}</span>
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
                    <button class="btn-macos btn-macos-secondary" onclick="window.__sentinelAppStore.set('activeView', 'decisions')">Ver Todas</button>
                </div>
                ${decisoes.length === 0 ? `
                    <div style="padding: 1.5rem; text-align: center; color: var(--text-tertiary); font-size: 0.88rem;">
                        Nenhuma decisão vigente consolidada no momento.
                    </div>
                ` : decisoes.map(d => `
                    <div class="overview-list-row" onclick="window.__sentinelSelectDecision('${d.topicoId}')">
                        <div class="overview-row-left">
                            <span class="overview-row-title">${escapeHtml(d.titulo)}</span>
                            <span class="overview-row-sub">tópico: ${escapeHtml(d.chaveTopico)} • versão ${d.versaoFluxo}</span>
                        </div>
                        <div class="overview-row-right">
                            <span class="status-pill active-state">Vigente</span>
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
        container.innerHTML = `
            <div style="flex:1;display:flex;align-items:center;justify-content:center;padding:2rem;">
                <div style="text-align:center;max-width:380px;">
                    <div style="width:48px;height:48px;border-radius:12px;background:var(--bg-surface-secondary);display:flex;align-items:center;justify-content:center;margin:0 auto 1rem;color:var(--text-tertiary);">${Icons.reviews}</div>
                    <h3 style="margin:0 0 0.5rem;font-size:1.1rem;color:var(--text-primary);font-weight:600;">Nenhuma proposta pendente</h3>
                    <p style="margin:0;font-size:0.86rem;color:var(--text-secondary);line-height:1.5;">Todas as propostas submetidas por agentes via MCP foram validadas e consolidadas.</p>
                </div>
            </div>
        `;
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
                        <div class="record-list-item ${isSel ? 'selected' : ''}" onclick="window.__sentinelSelectReview('${p.topicoId}')">
                            <div class="record-item-top">
                                <span class="record-item-topic">${escapeHtml(p.chaveTopico)}</span>
                                <span class="record-item-version">v${p.versaoFluxo}</span>
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
    const evidenciasHtml = p.evidencias && p.evidencias.length > 0 ? p.evidencias.map(e => `
        <a href="${escapeHtml(e.url)}" target="_blank" rel="noopener noreferrer" class="evidence-link-row">
            <div class="evidence-row-left">
                <span class="evidence-type-badge">${escapeHtml(e.tipo)}</span>
                <span>${escapeHtml(e.rotulo)}</span>
            </div>
            <span style="color:var(--text-tertiary);">${Icons.externalLink}</span>
        </a>
    `).join('') : '<div style="color:var(--text-tertiary);font-size:0.84rem;">Nenhuma evidência anexada.</div>';

    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                <span class="status-pill proposed-state">Revisão Humana Obrigatória</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">tópico: ${escapeHtml(p.chaveTopico)}</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">versão atual: ${p.versaoFluxo}</span>
            </div>
            <h1 class="doc-title">${escapeHtml(p.titulo)}</h1>
            <div class="doc-timestamp">Submetido por agente autônomo via ferramenta MCP <code>propose_decision</code></div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Resumo da Proposta</div>
            <div class="doc-content-box">${escapeHtml(p.resumo)}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Justificativa Técnica</div>
            <div class="doc-content-box">${escapeHtml(p.justificativa || 'Nenhuma justificativa técnica detalhada fornecida.')}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Evidências e Documentos Vinculados</div>
            <div class="evidence-table">${evidenciasHtml}</div>
        </div>

        <div class="doc-actions-bar">
            <button class="btn-macos btn-macos-primary" onclick="window.__sentinelApproveModal('${p.espacoTrabalhoId}', '${p.projetoId}', '${p.topicoId}', '${p.propostaPendenteId || p.registroAtivoId}', ${p.versaoFluxo})">
                ${Icons.check} Aprovar Decisão
            </button>
            <button class="btn-macos btn-macos-danger" onclick="window.__sentinelRejectModal('${p.espacoTrabalhoId}', '${p.projetoId}', '${p.topicoId}', '${p.propostaPendenteId || p.registroAtivoId}', ${p.versaoFluxo})">
                ${Icons.x} Rejeitar Proposta
            </button>
        </div>
    `;
}

function renderDecisionsView(container) {
    const decisoes = appStore.get('decisoes');
    const selectedId = appStore.get('selectedDecisionId');
    const selected = decisoes.find(d => d.topicoId === selectedId) || decisoes[0];

    if (decisoes.length === 0) {
        container.innerHTML = `
            <div style="flex:1;display:flex;align-items:center;justify-content:center;padding:2rem;">
                <div style="text-align:center;max-width:380px;">
                    <h3 style="margin:0 0 0.5rem;font-size:1.1rem;color:var(--text-primary);font-weight:600;">Nenhuma decisão vigente</h3>
                    <p style="margin:0;font-size:0.86rem;color:var(--text-secondary);line-height:1.5;">Não há decisões ativas consolidadas neste espaço de trabalho.</p>
                </div>
            </div>
        `;
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
                        <div class="record-list-item ${isSel ? 'selected' : ''}" onclick="window.__sentinelSelectDecision('${d.topicoId}')">
                            <div class="record-item-top">
                                <span class="record-item-topic">${escapeHtml(d.chaveTopico)}</span>
                                <span class="record-item-version">v${d.versaoFluxo}</span>
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
    const evidenciasHtml = d.evidencias && d.evidencias.length > 0 ? d.evidencias.map(e => `
        <a href="${escapeHtml(e.url)}" target="_blank" rel="noopener noreferrer" class="evidence-link-row">
            <div class="evidence-row-left">
                <span class="evidence-type-badge">${escapeHtml(e.tipo)}</span>
                <span>${escapeHtml(e.rotulo)}</span>
            </div>
            <span style="color:var(--text-tertiary);">${Icons.externalLink}</span>
        </a>
    `).join('') : '<div style="color:var(--text-tertiary);font-size:0.84rem;">Nenhuma evidência anexada.</div>';

    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                <span class="status-pill active-state">Decisão Vigente Ativa</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">tópico: ${escapeHtml(d.chaveTopico)}</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">versão ${d.versaoFluxo}</span>
            </div>
            <h1 class="doc-title">${escapeHtml(d.titulo)}</h1>
            <div class="doc-timestamp">Última atualização registrada: ${d.atualizadoEm ? new Date(d.atualizadoEm).toLocaleString('pt-BR') : 'Data não disponível'}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Resumo Arquitetural</div>
            <div class="doc-content-box">${escapeHtml(d.resumo)}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Justificativa e Consequências</div>
            <div class="doc-content-box">${escapeHtml(d.justificativa || 'Decisão arquitetural consolidada pela equipe de engenharia.')}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Evidências e Histórico Formal</div>
            <div class="evidence-table">${evidenciasHtml}</div>
        </div>
    `;
}

function renderIncidentsView(container) {
    const incidentes = appStore.get('incidentes');
    const selectedId = appStore.get('selectedIncidentId');
    const selected = incidentes.find(i => i.incidenteId === selectedId) || incidentes[0];

    if (incidentes.length === 0) {
        container.innerHTML = `
            <div style="flex:1;display:flex;align-items:center;justify-content:center;padding:2rem;">
                <div style="text-align:center;max-width:380px;">
                    <h3 style="margin:0 0 0.5rem;font-size:1.1rem;color:var(--text-primary);font-weight:600;">Nenhum incidente registrado</h3>
                    <p style="margin:0;font-size:0.86rem;color:var(--text-secondary);line-height:1.5;">Nenhum relato de incidente foi submetido via ferramenta <code>log_incident</code>.</p>
                </div>
            </div>
        `;
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
                        <div class="record-list-item ${isSel ? 'selected' : ''}" onclick="window.__sentinelSelectIncident('${i.incidenteId}')">
                            <div class="record-item-top">
                                <span class="record-item-topic">${i.incidenteId.slice(0, 8)}...</span>
                                <span class="record-item-version">v${i.versaoFluxo}</span>
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
    const evidenciasHtml = inc.evidencias && inc.evidencias.length > 0 ? inc.evidencias.map(e => `
        <a href="${escapeHtml(e.url)}" target="_blank" rel="noopener noreferrer" class="evidence-link-row">
            <div class="evidence-row-left">
                <span class="evidence-type-badge">${escapeHtml(e.tipo)}</span>
                <span>${escapeHtml(e.rotulo)}</span>
            </div>
            <span style="color:var(--text-tertiary);">${Icons.externalLink}</span>
        </a>
    `).join('') : '<div style="color:var(--text-tertiary);font-size:0.84rem;">Nenhuma evidência vinculada.</div>';

    const tagsHtml = inc.chavesTecnicas && inc.chavesTecnicas.length > 0 ? inc.chavesTecnicas.map(k => `
        <span style="font-family:var(--font-mono);font-size:0.74rem;background:var(--code-bg);padding:2px 7px;border-radius:4px;color:var(--text-secondary);">${escapeHtml(k)}</span>
    `).join(' ') : '';

    return `
        <div class="doc-view-header">
            <div class="doc-meta-tags">
                <span class="status-pill reported-state">Incidente Reportado</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">id: ${inc.incidenteId}</span>
                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">versão ${inc.versaoFluxo}</span>
            </div>
            <h1 class="doc-title">${escapeHtml(inc.titulo)}</h1>
            <div class="doc-timestamp">Ocorrido em: ${inc.ocorridoEm ? new Date(inc.ocorridoEm).toLocaleString('pt-BR') : 'Data não informada'}</div>
        </div>

        <div class="doc-section">
            <div class="doc-section-title">Relato do Incidente</div>
            <div class="doc-content-box">${escapeHtml(inc.resumo)}</div>
        </div>

        ${tagsHtml ? `
            <div class="doc-section">
                <div class="doc-section-title">Chaves Técnicas e Identificadores</div>
                <div style="display:flex;flex-wrap:wrap;gap:6px;">${tagsHtml}</div>
            </div>
        ` : ''}

        <div class="doc-section">
            <div class="doc-section-title">Evidências (RCA / Pull Requests)</div>
            <div class="evidence-table">${evidenciasHtml}</div>
        </div>
    `;
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
                    const isApproval = ev.tipo.includes('Approved') || ev.tipo.includes('Aprovada');
                    const isRejection = ev.tipo.includes('Rejected') || ev.tipo.includes('Rejeitada');
                    const isIncident = ev.tipo.includes('Incident') || ev.tipo.includes('Incidente');
                    const pillClass = isApproval ? 'active-state' : isRejection ? 'status-pill' : isIncident ? 'reported-state' : 'proposed-state';
                    const pillText = isApproval ? 'Aprovado' : isRejection ? 'Rejeitado' : isIncident ? 'Incidente' : 'Proposta';

                    return `
                        <div class="overview-list-row" style="cursor:default;">
                            <div class="overview-row-left">
                                <div style="display:flex;align-items:center;gap:8px;margin-bottom:3px;">
                                    <span class="status-pill ${pillClass}">${pillText}</span>
                                    <span class="overview-row-title">${escapeHtml(ev.tipo)}</span>
                                    <span style="font-family:var(--font-mono);font-size:0.72rem;color:var(--text-tertiary);">v${ev.versaoFluxo}</span>
                                </div>
                                <span class="overview-row-sub">Autor: ${escapeHtml(ev.autorSujeito)} • Cliente: ${escapeHtml(ev.clienteId || 'sessão web')}</span>
                            </div>
                            <div class="overview-row-right">
                                <span style="font-family:var(--font-mono);font-size:0.74rem;color:var(--text-tertiary);">${ev.registradoEm || ev.gravadoEm ? new Date(ev.registradoEm || ev.gravadoEm).toLocaleString('pt-BR') : ''}</span>
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
        { name: 'query_context', desc: 'Recupera decisões vigentes e incidentes relevantes com busca textual e recibo imutável.' },
        { name: 'get_timeline', desc: 'Consulta a linha do tempo cronológica de eventos de um tópico de decisão ou incidente.' },
        { name: 'get_context_receipt', desc: 'Verifica o comprovante criptográfico e as versões dos registros retornados em uma consulta.' }
    ];

    container.innerHTML = `
        <div class="overview-view">
            <div class="overview-hero">
                <div class="overview-badge">Protocolo Streamable HTTP</div>
                <h1 class="overview-title">Servidor MCP Ativo</h1>
                <p class="overview-desc">Endpoint oficial exposto em <code>/mcp</code> com autenticação Bearer OAuth e isolamento por workspace.</p>
            </div>
            <div class="overview-section">
                <div class="overview-section-title">Ferramentas Disponíveis (${tools.length})</div>
                ${tools.map(t => `
                    <div class="mcp-tool-card">
                        <div class="mcp-tool-header">
                            <span class="mcp-tool-name">${t.name}</span>
                            <span style="font-size:0.7rem;color:var(--status-success);font-weight:600;">Ativo</span>
                        </div>
                        <p class="mcp-tool-desc">${t.desc}</p>
                    </div>
                `).join('')}
            </div>
        </div>
    `;
}

function renderSettingsView(container) {
    const isDark = appStore.get('theme') === 'dark';
    const isInspector = appStore.get('inspectorOpen');

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
                        <input type="checkbox" id="switch-theme" ${isDark ? 'checked' : ''} onchange="window.__sentinelAppStore.set('theme', this.checked ? 'dark' : 'light')">
                        <span class="macos-slider"></span>
                    </label>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Painel de Inspeção</span>
                        <span class="settings-caption">Exibe metadados técnicos e recibos à direita da janela principal.</span>
                    </div>
                    <label class="macos-switch">
                        <input type="checkbox" id="switch-inspector" ${isInspector ? 'checked' : ''} onchange="window.__sentinelAppStore.set('inspectorOpen', this.checked)">
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
                    <span style="font-family:var(--font-mono);font-size:0.8rem;color:var(--text-secondary);">maintainer-alice</span>
                </div>
                <div class="settings-row">
                    <div class="settings-row-text">
                        <span class="settings-label">Provedor de Identidade (Issuer)</span>
                        <span class="settings-caption">Serviço emissor de tokens de segurança.</span>
                    </div>
                    <span style="font-family:var(--font-mono);font-size:0.8rem;color:var(--text-secondary);">https://auth.sentinel.dev</span>
                </div>
            </div>
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

    inspector.innerHTML = `
        <div style="font-size:0.75rem;font-weight:600;text-transform:uppercase;color:var(--text-tertiary);letter-spacing:0.04em;margin-bottom:1.25rem;">
            Inspetor de Metadados
        </div>

        <div class="inspector-field">
            <div class="inspector-field-label">Tipo de Entidade</div>
            <div class="inspector-field-val">${tipo}</div>
        </div>

        <div class="inspector-field">
            <div class="inspector-field-label">Identificador do Tópico / Fluxo</div>
            <div class="inspector-field-val code">${item.topicoId || item.incidenteId}</div>
        </div>

        ${item.chaveTopico ? `
            <div class="inspector-field">
                <div class="inspector-field-label">Chave Canônica</div>
                <div class="inspector-field-val code">${item.chaveTopico}</div>
            </div>
        ` : ''}

        <div class="inspector-field">
            <div class="inspector-field-label">Versão do Fluxo</div>
            <div class="inspector-field-val code">v${item.versaoFluxo}</div>
        </div>

        <div class="inspector-field">
            <div class="inspector-field-label">Espaço de Trabalho</div>
            <div class="inspector-field-val code">${item.espacoTrabalhoId}</div>
        </div>

        <div class="inspector-field">
            <div class="inspector-field-label">Evidências Anexadas</div>
            <div class="inspector-field-val">${(item.evidencias || []).length} documento(s)</div>
        </div>

        <div class="inspector-field">
            <div class="inspector-field-label">Garantia Arquitetural</div>
            <div style="display:flex;align-items:center;gap:6px;font-size:0.8rem;color:var(--status-success);margin-top:2px;">
                ${Icons.shieldCheck} Isolamento RLS & Event Sourced
            </div>
        </div>
    `;
}

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
}

function abrirModalAprovacao(wsId, projId, topicoId, propostaId, versaoAtual) {
    modalPayload = {
        tipo: 'aprovar',
        wsId, projId, topicoId, propostaId, versaoAtual
    };
    document.getElementById('modal-title').textContent = 'Aprovar Decisão';
    document.getElementById('modal-desc').textContent = 'Esta decisão será promovida a vigente no sistema, substituindo qualquer versão anterior deste tópico. Deseja confirmar?';
    document.getElementById('modal-input-wrap').style.display = 'none';
    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-primary';
    btn.textContent = 'Confirmar Aprovação';
    document.getElementById('modal-dialog').classList.add('visible');
}

function abrirModalRejeicao(wsId, projId, topicoId, propostaId, versaoAtual) {
    modalPayload = {
        tipo: 'rejeitar',
        wsId, projId, topicoId, propostaId, versaoAtual
    };
    document.getElementById('modal-title').textContent = 'Rejeitar Proposta';
    document.getElementById('modal-desc').textContent = 'Por favor, informe a justificativa técnica para arquivar esta proposta de arquitetura.';
    document.getElementById('modal-input-wrap').style.display = 'block';
    document.getElementById('modal-reason-input').value = '';
    const btn = document.getElementById('modal-confirm-btn');
    btn.className = 'btn-macos btn-macos-danger';
    btn.textContent = 'Rejeitar Proposta';
    document.getElementById('modal-dialog').classList.add('visible');
    setTimeout(() => {
        const inp = document.getElementById('modal-reason-input');
        if (inp) inp.focus();
    }, 50);
}

function fecharModal() {
    const dialog = document.getElementById('modal-dialog');
    if (dialog) dialog.classList.remove('visible');
    modalPayload = null;
}

async function confirmarOperacaoModal() {
    if (!modalPayload) return;
    const p = modalPayload;

    if (p.tipo === 'aprovar') {
        fecharModal();
        try {
            const resp = await fetch('/api/review/approve', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-CSRF-Token': window.__sentinelCsrf,
                    'X-Sentinel-Web-Session': 'maintainer-alice:https://auth.sentinel.dev'
                },
                body: JSON.stringify({
                    workspaceId: p.wsId,
                    projectId: p.projId,
                    topicId: p.topicoId,
                    proposalId: p.propostaId,
                    expectedVersion: p.versaoAtual,
                    idempotencyKey: 'review-approve-' + Date.now()
                })
            });
            if (resp.ok) {
                mostrarToast('Decisão aprovada e consolidada como vigente.');
                setTimeout(() => location.reload(), 1000);
            } else {
                const err = await resp.json();
                mostrarToast('Erro ao aprovar: ' + (err.message || 'Falha de validação'));
            }
        } catch (e) {
            mostrarToast('Erro de comunicação com o servidor.');
        }
    } else if (p.tipo === 'rejeitar') {
        const motivo = (document.getElementById('modal-reason-input').value || '').trim();
        if (!motivo) {
            mostrarToast('Informe o motivo da rejeição.');
            return;
        }
        fecharModal();
        try {
            const resp = await fetch('/api/review/reject', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-CSRF-Token': window.__sentinelCsrf,
                    'X-Sentinel-Web-Session': 'maintainer-alice:https://auth.sentinel.dev'
                },
                body: JSON.stringify({
                    workspaceId: p.wsId,
                    projectId: p.projId,
                    topicId: p.topicoId,
                    proposalId: p.propostaId,
                    expectedVersion: p.versaoAtual,
                    reason: motivo,
                    idempotencyKey: 'review-reject-' + Date.now()
                })
            });
            if (resp.ok) {
                mostrarToast('Proposta de decisão rejeitada com sucesso.');
                setTimeout(() => location.reload(), 1000);
            } else {
                const err = await resp.json();
                mostrarToast('Erro ao rejeitar: ' + (err.message || 'Falha de validação'));
            }
        } catch (e) {
            mostrarToast('Erro de comunicação com o servidor.');
        }
    }
}

function mostrarToast(mensagem) {
    const container = document.getElementById('toast-container');
    if (!container) return;
    const pill = document.createElement('div');
    pill.className = 'toast-pill';
    pill.innerHTML = `
        <span style="color:var(--action-primary);">${Icons.check}</span>
        <span>${escapeHtml(mensagem)}</span>
    `;
    container.appendChild(pill);
    setTimeout(() => {
        pill.style.opacity = '0';
        pill.style.transform = 'translateY(10px)';
        pill.style.transition = 'all var(--motion-fast)';
        setTimeout(() => pill.remove(), 200);
    }, 3200);
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

window.__sentinelAppStore = appStore;
window.__sentinelSelectReview = (id) => appStore.set('selectedReviewId', id);
window.__sentinelSelectDecision = (id) => appStore.set('selectedDecisionId', id);
window.__sentinelSelectIncident = (id) => appStore.set('selectedIncidentId', id);
window.__sentinelApproveModal = abrirModalAprovacao;
window.__sentinelRejectModal = abrirModalRejeicao;
