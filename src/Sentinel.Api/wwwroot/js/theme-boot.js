// Aplica o tema salvo antes da primeira pintura (carregado de forma síncrona no <head>).
(function () {
    'use strict';
    var tema = 'dark';
    try {
        var salvo = window.localStorage.getItem('sentinel_theme');
        if (salvo === 'light' || salvo === 'dark') {
            tema = salvo;
        }
    } catch (e) {
        // localStorage indisponível (modo privado, cookies bloqueados): mantém o tema padrão.
    }
    document.documentElement.setAttribute('data-theme', tema);
})();
