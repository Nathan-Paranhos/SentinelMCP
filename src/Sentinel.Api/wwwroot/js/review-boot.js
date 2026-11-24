// Bootstrap do painel de revisão: busca o estado da sessão e inicializa o app.
import { initApp, mostrarToast } from './app.js';
import { buscarEstadoRevisao, obterWorkspaceIdDaUrl, redirecionarParaLogin } from './review-api.js';

function mostrarFalhaCarregamento(mensagem) {
    const corpo = document.getElementById('content-body');
    if (corpo) {
        const caixa = document.createElement('div');
        caixa.style.cssText = 'flex:1;display:flex;align-items:center;justify-content:center;padding:2rem;';

        const interno = document.createElement('div');
        interno.style.cssText = 'text-align:center;max-width:420px;';

        const titulo = document.createElement('h3');
        titulo.style.cssText = 'margin:0 0 0.5rem;font-size:1.1rem;color:var(--text-primary);font-weight:600;';
        titulo.textContent = 'Não foi possível carregar o painel';

        const texto = document.createElement('p');
        texto.style.cssText = 'margin:0 0 1rem;font-size:0.86rem;color:var(--text-secondary);line-height:1.5;';
        texto.textContent = mensagem;

        const botao = document.createElement('button');
        botao.type = 'button';
        botao.className = 'btn-macos btn-macos-secondary';
        botao.textContent = 'Tentar novamente';
        botao.addEventListener('click', () => {
            botao.disabled = true;
            iniciar();
        });

        interno.append(titulo, texto, botao);
        caixa.appendChild(interno);
        corpo.replaceChildren(caixa);
    }
    mostrarToast(mensagem, 'erro');
}

async function iniciar() {
    let resultado;
    try {
        resultado = await buscarEstadoRevisao(obterWorkspaceIdDaUrl());
    } catch (e) {
        mostrarFalhaCarregamento('Erro de comunicação com o servidor ao carregar o estado de revisão.');
        return;
    }

    if (resultado.status === 401) {
        redirecionarParaLogin();
        return;
    }

    if (!resultado.ok) {
        const mensagem = (resultado.erro && resultado.erro.message)
            || (resultado.status === 403 ? 'Sem permissão para acessar este espaço de trabalho.' : `Falha ao carregar o estado de revisão (HTTP ${resultado.status}).`);
        mostrarFalhaCarregamento(mensagem);
        return;
    }

    try {
        initApp(resultado.dados);
    } catch (e) {
        console.error('Falha ao inicializar o painel de revisão.', e);
        mostrarFalhaCarregamento('Ocorreu um erro ao renderizar o painel de revisão.');
    }
}

iniciar();
