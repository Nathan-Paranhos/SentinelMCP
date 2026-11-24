// Comportamento da landing page: tela de loading, revelação ao rolar e indicador de rolagem.
(function () {
    'use strict';

    var DURACAO_LOADING_MS = 1200;
    var DURACAO_LOADING_REDUZIDA_MS = 300;

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

        var progresso = document.getElementById('landing-loading-progress');
        if (progresso) {
            // Dois frames garantem que a largura inicial já foi pintada e a transição CSS roda.
            window.requestAnimationFrame(function () {
                window.requestAnimationFrame(function () {
                    progresso.style.width = '100%';
                });
            });
        }

        var atraso = prefereMovimentoReduzido() ? DURACAO_LOADING_REDUZIDA_MS : DURACAO_LOADING_MS;
        window.setTimeout(function () {
            tela.classList.add('oculto');
        }, atraso);
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
        }, { threshold: 0.15 });

        elementos.forEach(function (el) { observador.observe(el); });
    }

    function iniciarEfeitoScrollPreview() {
        var janelaPreview = document.getElementById('preview-window');
        if (!janelaPreview) return;

        var atualizar = function () {
            janelaPreview.classList.toggle('scrolled', window.scrollY > 60);
        };
        window.addEventListener('scroll', atualizar, { passive: true });
        atualizar();
    }

    function iniciarIndicadorDeRolagem() {
        document.querySelectorAll('[data-scroll-target]').forEach(function (indicador) {
            var rolar = function () {
                var alvo = document.getElementById(indicador.getAttribute('data-scroll-target'));
                if (alvo) {
                    alvo.scrollIntoView({ behavior: prefereMovimentoReduzido() ? 'auto' : 'smooth' });
                }
            };
            indicador.addEventListener('click', rolar);
            indicador.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    rolar();
                }
            });
        });
    }

    function iniciar() {
        iniciarTelaLoading();
        iniciarRevelacaoAoRolar();
        iniciarEfeitoScrollPreview();
        iniciarIndicadorDeRolagem();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', iniciar);
    } else {
        iniciar();
    }
})();
