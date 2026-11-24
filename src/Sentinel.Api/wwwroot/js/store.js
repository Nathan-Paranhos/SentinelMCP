function lerTemaSalvo() {
    try {
        const salvo = localStorage.getItem('sentinel_theme');
        return salvo === 'light' || salvo === 'dark' ? salvo : 'dark';
    } catch (e) {
        return 'dark';
    }
}

function salvarTema(tema) {
    try {
        localStorage.setItem('sentinel_theme', tema);
    } catch (e) {
        // localStorage indisponível: o tema vale apenas para esta página.
    }
}

function comoLista(valor) {
    return Array.isArray(valor) ? valor : [];
}

class Store {
    constructor() {
        this.state = {
            activeView: 'reviews',
            selectedReviewId: null,
            selectedDecisionId: null,
            selectedIncidentId: null,
            sidebarCollapsed: false,
            inspectorOpen: true,
            theme: lerTemaSalvo(),
            searchTerm: '',
            propostas: [],
            decisoes: [],
            incidentes: [],
            eventos: [],
            workspaces: [],
            workspace: null,
            // Preenchidos exclusivamente pelo servidor (GET /api/review/state).
            usuario: null,
            csrf: null
        };
        this.listeners = new Map();
    }

    get(key) {
        return this.state[key];
    }

    set(key, value) {
        this.state[key] = value;
        if (key === 'theme') {
            salvarTema(value);
            document.documentElement.setAttribute('data-theme', value);
        }
        this.notify(key, value);
    }

    /**
     * Aplica (ou reaplica, após recarregar) o estado vindo do servidor.
     * Mantém as seleções atuais quando o item ainda existe.
     */
    initData(payload) {
        const dados = payload || {};
        this.state.propostas = comoLista(dados.propostas);
        this.state.decisoes = comoLista(dados.decisoes);
        this.state.incidentes = comoLista(dados.incidentes);
        this.state.eventos = comoLista(dados.eventos);
        this.state.workspaces = comoLista(dados.workspaces);
        this.state.workspace = dados.workspace || null;
        this.state.usuario = dados.usuario || null;
        this.state.csrf = dados.csrf || null;

        this.state.selectedReviewId = this.manterSelecao(this.state.selectedReviewId, this.state.propostas, 'topicoId');
        this.state.selectedDecisionId = this.manterSelecao(this.state.selectedDecisionId, this.state.decisoes, 'topicoId');
        this.state.selectedIncidentId = this.manterSelecao(this.state.selectedIncidentId, this.state.incidentes, 'incidenteId');

        document.documentElement.setAttribute('data-theme', this.state.theme);
        this.notify('all', this.state);
    }

    manterSelecao(atual, lista, chave) {
        if (atual && lista.some(item => item && item[chave] === atual)) return atual;
        return lista.length > 0 && lista[0] ? lista[0][chave] : null;
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
