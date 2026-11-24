// Comportamento da landing page: loading, tabs de clientes MCP, cópia rápida e scroll reveal.
(function () {
    'use strict';

    var DURACAO_LOADING_MS = 650;
    var DURACAO_LOADING_REDUZIDA_MS = 180;

    function prefereMovimentoReduzido() {
        try {
            return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
        } catch (e) {
            return false;
        }
    }

    function iniciarTelaLoading() {
        var tela = document.getElementById('landing-loading-screen');
        if (!tela) return;

        var esconder = function () {
            tela.classList.add('oculto');
        };

        var progresso = document.getElementById('landing-loading-progress');
        if (progresso) {
            window.requestAnimationFrame(function () {
                window.requestAnimationFrame(function () {
                    progresso.style.width = '100%';
                });
            });
        }

        tela.addEventListener('click', esconder);
        var atraso = prefereMovimentoReduzido() ? DURACAO_LOADING_REDUZIDA_MS : DURACAO_LOADING_MS;
        window.setTimeout(esconder, atraso);
    }

    function iniciarRevelacaoAoRolar() {
        var elementos = document.querySelectorAll('.scroll-reveal');
        if (!('IntersectionObserver' in window)) {
            elementos.forEach(function (el) { el.classList.add('visible'); });
            return;
        }

        var observador = new IntersectionObserver(function (entradas) {
            entradas.forEach(function (entrada) {
                if (entrada.isIntersecting) {
                    entrada.target.classList.add('visible');
                    observador.unobserve(entrada.target);
                }
            });
        }, { threshold: 0.12 });

        elementos.forEach(function (el) { observador.observe(el); });
    }

    function iniciarEfeitoScrollPreview() {
        var janelaPreview = document.getElementById('preview-window');
        if (!janelaPreview) return;

        var atualizar = function () {
            janelaPreview.classList.toggle('scrolled', window.scrollY > 50);
        };
        window.addEventListener('scroll', atualizar, { passive: true });
        atualizar();
    }

    function iniciarTabsClientes() {
        var botoes = document.querySelectorAll('[data-client-tab]');
        var paineis = document.querySelectorAll('[data-client-panel]');
        if (!botoes.length) return;

        botoes.forEach(function (btn) {
            btn.addEventListener('click', function () {
                var alvo = btn.getAttribute('data-client-tab');
                botoes.forEach(function (b) { b.classList.toggle('active', b.getAttribute('data-client-tab') === alvo); });
                paineis.forEach(function (p) { p.classList.toggle('active', p.getAttribute('data-client-panel') === alvo); });
            });
        });
    }

    function iniciarBotoesCopia() {
        document.querySelectorAll('[data-copy-target]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var id = btn.getAttribute('data-copy-target');
                var el = id ? document.getElementById(id) : null;
                if (!el || !navigator.clipboard) return;
                navigator.clipboard.writeText(el.textContent || '').then(function () {
                    var original = btn.textContent;
                    btn.textContent = 'Copiado!';
                    window.setTimeout(function () { btn.textContent = original; }, 1600);
                });
            });
        });
    }

    function iniciar() {
        iniciarTelaLoading();
        iniciarRevelacaoAoRolar();
        iniciarEfeitoScrollPreview();
        iniciarTabsClientes();
        iniciarBotoesCopia();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', iniciar);
    } else {
        iniciar();
    }
})();
