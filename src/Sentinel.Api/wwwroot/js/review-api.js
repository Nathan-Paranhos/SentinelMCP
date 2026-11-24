// Acesso HTTP do painel de revisão (sessão por cookie + header CSRF).

const CABECALHO_CSRF_PADRAO = 'X-CSRF-Token';
const NOME_CABECALHO_VALIDO = /^[A-Za-z0-9-]+$/;

/**
 * Lê o workspaceId da querystring da página atual (ou null).
 */
export function obterWorkspaceIdDaUrl() {
    try {
        const valor = new URLSearchParams(window.location.search).get('workspaceId');
        return valor && valor.trim() ? valor.trim() : null;
    } catch (e) {
        return null;
    }
}

/**
 * Redireciona para o login preservando a página atual como returnUrl.
 */
export function redirecionarParaLogin() {
    window.location.href = '/auth/login?returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search);
}

/**
 * Lê o corpo de erro `{ code, message }` sem lançar exceção se não for JSON.
 */
export async function lerErro(resposta) {
    try {
        const corpo = await resposta.json();
        if (corpo && typeof corpo === 'object') {
            return {
                code: typeof corpo.code === 'string' ? corpo.code : null,
                message: typeof corpo.message === 'string' ? corpo.message : null
            };
        }
    } catch (e) {
        // corpo vazio ou não-JSON
    }
    return { code: null, message: null };
}

/**
 * GET /api/review/state. Retorna { status, ok, dados, erro }.
 * Lança apenas em falha de rede.
 */
export async function buscarEstadoRevisao(workspaceId) {
    const url = '/api/review/state' + (workspaceId ? '?workspaceId=' + encodeURIComponent(workspaceId) : '');
    const resposta = await fetch(url, {
        method: 'GET',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { Accept: 'application/json' }
    });

    if (resposta.ok) {
        const dados = await resposta.json();
        return { status: resposta.status, ok: true, dados, erro: null };
    }
    return { status: resposta.status, ok: false, dados: null, erro: await lerErro(resposta) };
}

/**
 * Monta os headers CSRF a partir do objeto `csrf` do estado.
 */
export function cabecalhosCsrf(csrf) {
    const nome = csrf && typeof csrf.headerName === 'string' && NOME_CABECALHO_VALIDO.test(csrf.headerName)
        ? csrf.headerName
        : CABECALHO_CSRF_PADRAO;
    const token = csrf && typeof csrf.token === 'string' ? csrf.token : '';
    return { [nome]: token };
}

/**
 * POST JSON autenticado por cookie de sessão + CSRF. Retorna { status, ok, corpo, erro }.
 * Lança apenas em falha de rede.
 */
export async function postarJson(url, corpo, csrf) {
    const resposta = await fetch(url, {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: {
            'Content-Type': 'application/json',
            Accept: 'application/json',
            ...cabecalhosCsrf(csrf)
        },
        body: JSON.stringify(corpo)
    });

    if (resposta.ok) {
        let dados = null;
        try {
            dados = await resposta.json();
        } catch (e) {
            dados = null;
        }
        return { status: resposta.status, ok: true, corpo: dados, erro: null };
    }
    return { status: resposta.status, ok: false, corpo: null, erro: await lerErro(resposta) };
}

/**
 * POST /auth/logout. Considera sucesso 2xx, redirecionamento (302) ou 401 (sessão já encerrada).
 */
export async function encerrarSessao(csrf) {
    const resposta = await fetch('/auth/logout', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        redirect: 'manual',
        headers: { ...cabecalhosCsrf(csrf) }
    });
    return resposta.ok
        || resposta.type === 'opaqueredirect'
        || (resposta.status >= 300 && resposta.status < 400)
        || resposta.status === 401;
}
