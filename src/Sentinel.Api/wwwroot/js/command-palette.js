import { appStore } from './store.js';
import { Icons } from './icons.js';
import { escapeHtml } from './util.js';

export class CommandPalette {
    constructor() {
        this.backdrop = document.getElementById('command-palette');
        this.input = document.getElementById('command-search-input');
        this.resultsContainer = document.getElementById('command-results');
        this.focusedIndex = 0;
        this.items = [];
        this.initEvents();
    }

    initEvents() {
        window.addEventListener('keydown', (e) => {
            if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
                e.preventDefault();
                this.toggle();
            } else if (e.key === 'Escape' && this.isOpen()) {
                this.close();
            }
        });

        if (this.backdrop) {
            this.backdrop.addEventListener('click', (e) => {
                if (e.target === this.backdrop) this.close();
            });
        }

        if (this.input) {
            this.input.addEventListener('input', () => {
                this.focusedIndex = 0;
                this.renderResults();
            });

            this.input.addEventListener('keydown', (e) => {
                if (e.key === 'ArrowDown') {
                    e.preventDefault();
                    this.navigate(1);
                } else if (e.key === 'ArrowUp') {
                    e.preventDefault();
                    this.navigate(-1);
                } else if (e.key === 'Enter') {
                    e.preventDefault();
                    this.executeSelected();
                }
            });
        }
    }

    isOpen() {
        return this.backdrop && this.backdrop.classList.contains('visible');
    }

    toggle() {
        if (this.isOpen()) this.close();
        else this.open();
    }

    open() {
        if (!this.backdrop) return;
        this.backdrop.classList.add('visible');
        this.input.value = '';
        this.focusedIndex = 0;
        this.renderResults();
        setTimeout(() => this.input.focus(), 50);
    }

    close() {
        if (!this.backdrop) return;
        this.backdrop.classList.remove('visible');
    }

    navigate(direction) {
        if (this.items.length === 0) return;
        this.focusedIndex = (this.focusedIndex + direction + this.items.length) % this.items.length;
        this.renderResults();
        const activeElem = this.resultsContainer.querySelector('.command-result-item.focused');
        if (activeElem) activeElem.scrollIntoView({ block: 'nearest' });
    }

    executeSelected() {
        if (this.items.length === 0 || !this.items[this.focusedIndex]) return;
        const item = this.items[this.focusedIndex];
        this.close();
        item.action();
    }

    renderResults() {
        const query = (this.input.value || '').trim().toLowerCase();
        const propostas = appStore.get('propostas') || [];
        const decisoes = appStore.get('decisoes') || [];
        const incidentes = appStore.get('incidentes') || [];

        const pool = [];

        pool.push({
            group: 'Navegação',
            title: 'Ir para Revisões de Arquitetura',
            icon: Icons.reviews,
            action: () => appStore.set('activeView', 'reviews')
        });
        pool.push({
            group: 'Navegação',
            title: 'Ir para Decisões Vigentes',
            icon: Icons.decisions,
            action: () => appStore.set('activeView', 'decisions')
        });
        pool.push({
            group: 'Navegação',
            title: 'Ir para Incidentes Reportados',
            icon: Icons.incidents,
            action: () => appStore.set('activeView', 'incidents')
        });
        pool.push({
            group: 'Navegação',
            title: 'Ir para Visão Geral',
            icon: Icons.overview,
            action: () => appStore.set('activeView', 'overview')
        });
        pool.push({
            group: 'Navegação',
            title: 'Ir para Histórico (Timeline)',
            icon: Icons.timeline,
            action: () => appStore.set('activeView', 'timeline')
        });
        pool.push({
            group: 'Navegação',
            title: 'Ir para Integrações MCP',
            icon: Icons.mcp,
            action: () => appStore.set('activeView', 'mcp')
        });
        pool.push({
            group: 'Navegação',
            title: 'Ir para Configurações',
            icon: Icons.settings,
            action: () => appStore.set('activeView', 'settings')
        });

        pool.push({
            group: 'Ações Rápidas',
            title: 'Alternar Tema Claro / Escuro',
            icon: appStore.get('theme') === 'dark' ? Icons.sun : Icons.moon,
            action: () => {
                const next = appStore.get('theme') === 'dark' ? 'light' : 'dark';
                appStore.set('theme', next);
            }
        });
        pool.push({
            group: 'Ações Rápidas',
            title: 'Alternar Painel de Inspeção',
            icon: Icons.inspectorToggle,
            action: () => appStore.set('inspectorOpen', !appStore.get('inspectorOpen'))
        });
        pool.push({
            group: 'Ações Rápidas',
            title: 'Alternar Barra Lateral',
            icon: Icons.sidebarToggle,
            action: () => appStore.set('sidebarCollapsed', !appStore.get('sidebarCollapsed'))
        });

        propostas.forEach(p => {
            pool.push({
                group: 'Propostas Pendentes',
                title: p.titulo || '(sem título)',
                sub: `tópico: ${p.chaveTopico || ''} • v${p.versaoFluxo ?? ''}`,
                icon: Icons.reviews,
                action: () => {
                    appStore.set('selectedReviewId', p.topicoId);
                    appStore.set('activeView', 'reviews');
                }
            });
        });

        decisoes.forEach(d => {
            pool.push({
                group: 'Decisões Vigentes',
                title: d.titulo || '(sem título)',
                sub: `tópico: ${d.chaveTopico || ''} • v${d.versaoFluxo ?? ''}`,
                icon: Icons.decisions,
                action: () => {
                    appStore.set('selectedDecisionId', d.topicoId);
                    appStore.set('activeView', 'decisions');
                }
            });
        });

        incidentes.forEach(i => {
            pool.push({
                group: 'Incidentes',
                title: i.titulo || '(sem título)',
                sub: `incidente: ${String(i.incidenteId || '').slice(0, 8)}...`,
                icon: Icons.incidents,
                action: () => {
                    appStore.set('selectedIncidentId', i.incidenteId);
                    appStore.set('activeView', 'incidents');
                }
            });
        });

        this.items = query ? pool.filter(item => {
            return String(item.title).toLowerCase().includes(query) ||
                   (item.sub && item.sub.toLowerCase().includes(query)) ||
                   item.group.toLowerCase().includes(query);
        }) : pool;

        if (this.focusedIndex >= this.items.length) {
            this.focusedIndex = Math.max(0, this.items.length - 1);
        }

        if (this.items.length === 0) {
            this.resultsContainer.innerHTML = `
                <div style="padding: 2.5rem 1rem; text-align: center; color: var(--text-tertiary); font-size: 0.85rem;">
                    Nenhum resultado encontrado para "<strong>${escapeHtml(query)}</strong>"
                </div>
            `;
            return;
        }

        const groups = {};
        this.items.forEach((item, index) => {
            if (!groups[item.group]) groups[item.group] = [];
            groups[item.group].push({ item, index });
        });

        let html = '';
        for (const [groupName, groupItems] of Object.entries(groups)) {
            html += `<div class="command-group-title">${escapeHtml(groupName)}</div>`;
            groupItems.forEach(({ item, index }) => {
                const isFocused = index === this.focusedIndex;
                html += `
                    <div class="command-result-item ${isFocused ? 'focused' : ''}" data-index="${index}" role="option" aria-selected="${isFocused ? 'true' : 'false'}">
                        <div class="command-result-left">
                            <span style="display:flex;align-items:center;color:var(--text-secondary);">${item.icon}</span>
                            <div>
                                <div>${escapeHtml(item.title)}</div>
                                ${item.sub ? `<div style="font-size:0.72rem;color:var(--text-tertiary);">${escapeHtml(item.sub)}</div>` : ''}
                            </div>
                        </div>
                        <span style="font-size:0.72rem;color:var(--text-tertiary);">Abrir ↵</span>
                    </div>
                `;
            });
        }

        this.resultsContainer.innerHTML = html;

        this.resultsContainer.querySelectorAll('.command-result-item').forEach(el => {
            el.addEventListener('click', () => {
                const idx = parseInt(el.getAttribute('data-index'), 10);
                this.focusedIndex = idx;
                this.executeSelected();
            });
        });
    }
}
