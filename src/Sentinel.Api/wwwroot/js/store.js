class Store {
    constructor() {
        this.state = {
            activeView: 'reviews',
            selectedReviewId: null,
            selectedDecisionId: null,
            selectedIncidentId: null,
            sidebarCollapsed: false,
            inspectorOpen: true,
            theme: localStorage.getItem('sentinel_theme') || 'dark',
            searchTerm: '',
            propostas: [],
            decisoes: [],
            incidentes: [],
            eventos: [],
            workspace: {
                id: '11111111-1111-1111-1111-111111111111',
                nome: 'Pagamentos e Cobrança',
                slug: 'pagamentos-cobranca'
            },
            usuario: {
                sujeito: 'maintainer-alice',
                papel: 'Maintainer',
                emissor: 'https://auth.sentinel.dev'
            }
        };
        this.listeners = new Map();
    }

    get(key) {
        return this.state[key];
    }

    set(key, value) {
        this.state[key] = value;
        if (key === 'theme') {
            localStorage.setItem('sentinel_theme', value);
            document.documentElement.setAttribute('data-theme', value);
        }
        this.notify(key, value);
    }

    initData(payload) {
        if (payload.propostas) this.state.propostas = payload.propostas;
        if (payload.decisoes) this.state.decisoes = payload.decisoes;
        if (payload.incidentes) this.state.incidentes = payload.incidentes;
        if (payload.eventos) this.state.eventos = payload.eventos;
        if (payload.workspaces) this.state.workspaces = payload.workspaces;
        if (payload.workspace) this.state.workspace = payload.workspace;

        if (this.state.propostas.length > 0) {
            this.state.selectedReviewId = this.state.propostas[0].topicoId;
        }
        if (this.state.decisoes.length > 0) {
            this.state.selectedDecisionId = this.state.decisoes[0].topicoId;
        }
        if (this.state.incidentes.length > 0) {
            this.state.selectedIncidentId = this.state.incidentes[0].incidenteId;
        }

        document.documentElement.setAttribute('data-theme', this.state.theme);
        this.notify('all', this.state);
    }

    subscribe(key, callback) {
        if (!this.listeners.has(key)) {
            this.listeners.set(key, new Set());
        }
        this.listeners.get(key).add(callback);
        return () => this.listeners.get(key).delete(callback);
    }

    notify(key, value) {
        if (this.listeners.has(key)) {
            this.listeners.get(key).forEach(fn => fn(value));
        }
        if (key !== 'all' && this.listeners.has('all')) {
            this.listeners.get('all').forEach(fn => fn(this.state));
        }
    }
}

export const appStore = new Store();
