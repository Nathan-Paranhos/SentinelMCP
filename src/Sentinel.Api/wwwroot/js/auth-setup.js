// Sugere slugs ASCII, alterna visibilidade da senha (olho) e valida confirmação nos formulários de autenticação.
(function () {
    'use strict';

    var SVG_EYE = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg>';
    var SVG_EYE_OFF = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9.88 9.88a3 3 0 1 0 4.24 4.24"/><path d="M10.73 5.08A10.43 10.43 0 0 1 12 5c6.5 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68"/><path d="M6.61 6.61A13.526 13.526 0 0 0 2 12s3.5 7 10 7a9.74 9.74 0 0 0 5.39-1.61"/><line x1="2" y1="2" x2="22" y2="22"/></svg>';

    function slugify(texto) {
        return String(texto || '')
            .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, '-')
            .replace(/^-+|-+$/g, '')
            .slice(0, 80);
    }

    function ligarPar(inputNomeId, inputSlugId) {
        var nome = document.getElementById(inputNomeId);
        var slug = document.getElementById(inputSlugId);
        if (!nome || !slug) return;

        slug.addEventListener('input', function () {
            slug.dataset.edited = slug.value.trim() ? '1' : '';
        });

        nome.addEventListener('input', function () {
            if (!slug.dataset.edited) {
                slug.value = slugify(nome.value);
            }
        });
    }

    function ligarBotoesOlho() {
        document.querySelectorAll('[data-toggle-password]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var targetId = btn.getAttribute('data-toggle-password');
                var input = targetId ? document.getElementById(targetId) : null;
                if (!input) return;

                var mostrar = input.type === 'password';
                input.type = mostrar ? 'text' : 'password';
                btn.setAttribute('aria-pressed', mostrar ? 'true' : 'false');
                btn.setAttribute('aria-label', mostrar ? 'Ocultar senha' : 'Mostrar senha');
                btn.setAttribute('title', mostrar ? 'Ocultar senha' : 'Mostrar senha');
                btn.innerHTML = mostrar ? SVG_EYE_OFF : SVG_EYE;
            });
        });
    }

    function ligarConfirmacaoSenha() {
        var senha = document.getElementById('setup-password');
        var confirma = document.getElementById('setup-confirm-password');
        var dica = document.getElementById('setup-password-match');
        if (!senha || !confirma) return;

        function atualizarEstado() {
            var s = senha.value || '';
            var c = confirma.value || '';

            if (!c) {
                confirma.setCustomValidity('');
                if (dica) {
                    dica.textContent = '';
                    dica.className = 'password-match-hint';
                }
                return;
            }

            if (s === c) {
                confirma.setCustomValidity('');
                if (dica) {
                    dica.textContent = 'As senhas conferem.';
                    dica.className = 'password-match-hint ok';
                }
            } else {
                confirma.setCustomValidity('As senhas informadas não conferem.');
                if (dica) {
                    dica.textContent = 'As senhas informadas não conferem.';
                    dica.className = 'password-match-hint error';
                }
            }
        }

        senha.addEventListener('input', atualizarEstado);
        confirma.addEventListener('input', atualizarEstado);
    }

    document.addEventListener('DOMContentLoaded', function () {
        ligarPar('setup-ws-name', 'setup-ws-slug');
        ligarPar('setup-proj-name', 'setup-proj-slug');
        ligarBotoesOlho();
        ligarConfirmacaoSenha();
    });
})();
