// Acesso HTTP do painel de revisão e administração (sessão por cookie + header CSRF).

const CABECALHO_CSRF_PADRAO = 'X-CSRF-Token';
const NOME_CABECALHO_VALIDO = /^[A-Za-z0-9-]+$/;

export function obterWorkspaceIdDaUrl() {
    try {
        const valor = new URLSearchParams(window.location.search).get('workspaceId');
        return valor && valor.trim() ? valor.trim() : null;
    } catch (e) {
        return null;
    }
}

export function redirecionarParaLogin() {
    window.location.href = '/auth/login?returnUrl=' + encodeURIComponent(window.location.pathname + window.location.search);
}

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
        // Corpo não JSON
    }
    return { code: null, message: null };
}

async function buscarJson(url) {
    const resposta = await fetch(url, {
        method: 'GET',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { Accept: 'application/json' }
    });

    if (resposta.ok) {
        return { status: resposta.status, ok: true, dados: await resposta.json(), erro: null };
    }
    return { status: resposta.status, ok: false, dados: null, erro: await lerErro(resposta) };
}

export function buscarEstadoRevisao(workspaceId) {
    const url = '/api/review/state' + (workspaceId ? '?workspaceId=' + encodeURIComponent(workspaceId) : '');
    return buscarJson(url);
}

export function buscarLinhaDoTempo(workspaceId, projectId, kind, streamId) {
    const params = new URLSearchParams({ workspaceId, projectId, kind, streamId });
    return buscarJson('/api/review/timeline?' + params.toString());
}

export function buscarComprovante(workspaceId, projectId, queryId) {
    const params = new URLSearchParams({ workspaceId, projectId, queryId });
    return buscarJson('/api/review/receipt?' + params.toString());
}

export function buscarDetalhesWorkspaceAdmin(workspaceId) {
    return buscarJson('/api/admin/workspaces/' + encodeURIComponent(workspaceId));
}

export function cabecalhosCsrf(csrf) {
    const nome = csrf && typeof csrf.headerName === 'string' && NOME_CABECALHO_VALIDO.test(csrf.headerName)
        ? csrf.headerName
        : CABECALHO_CSRF_PADRAO;
    const token = csrf && typeof csrf.token === 'string' ? csrf.token : '';
    return { [nome]: token };
}

async function enviarJson(metodo, url, corpo, csrf) {
    const headers = {
        Accept: 'application/json',
        ...cabecalhosCsrf(csrf)
    };
    const init = {
        method: metodo,
        credentials: 'same-origin',
        cache: 'no-store',
        headers
    };
    if (corpo !== undefined) {
        headers['Content-Type'] = 'application/json';
        init.body = JSON.stringify(corpo);
    }

    const resposta = await fetch(url, init);
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

export function postarJson(url, corpo, csrf) {
    return enviarJson('POST', url, corpo, csrf);
}

export function criarProjetoAdmin(workspaceId, nome, slug, csrf) {
    return enviarJson('POST', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/projects`, { name: nome, slug }, csrf);
}

export function removerProjetoAdmin(workspaceId, projectId, csrf) {
    return enviarJson('DELETE', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/projects/${encodeURIComponent(projectId)}`, undefined, csrf);
}

export function configurarVinculoAdmin(workspaceId, issuer, subject, role, isActive, csrf) {
    return enviarJson('POST', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/memberships`, { issuer, subject, role, isActive }, csrf);
}

export function removerVinculoAdmin(workspaceId, membershipId, csrf) {
    return enviarJson('DELETE', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/memberships/${encodeURIComponent(membershipId)}`, undefined, csrf);
}

export function listarChavesApiAdmin(workspaceId) {
    return buscarJson(`/api/admin/workspaces/${encodeURIComponent(workspaceId)}/api-keys`);
}

export function criarChaveApiAdmin(workspaceId, payload, csrf) {
    return enviarJson('POST', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/api-keys`, payload, csrf);
}

export function revogarChaveApiAdmin(workspaceId, keyId, csrf) {
    return enviarJson('DELETE', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/api-keys/${encodeURIComponent(keyId)}`, undefined, csrf);
}

export function listarUsuariosAdmin(workspaceId) {
    return buscarJson(`/api/admin/workspaces/${encodeURIComponent(workspaceId)}/users`);
}

export function criarUsuarioAdmin(workspaceId, payload, csrf) {
    return enviarJson('POST', `/api/admin/workspaces/${encodeURIComponent(workspaceId)}/users`, payload, csrf);
}

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
