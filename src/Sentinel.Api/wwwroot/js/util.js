// Utilitários compartilhados do painel de revisão.

/**
 * Escapa texto para uso seguro em conteúdo HTML e em valores de atributos entre aspas.
 */
export function escapeHtml(valor) {
    if (valor === null || valor === undefined) return '';
    return String(valor)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

/**
 * Retorna a URL somente se ela for absoluta e usar https:// (case-insensitive).
 * Qualquer outro esquema (javascript:, data:, http:, relativo...) retorna null,
 * e quem chama deve exibir o valor apenas como texto.
 */
export function urlHttpsSegura(url) {
    if (typeof url !== 'string' || !/^https:\/\//i.test(url)) return null;
    try {
        const analisada = new URL(url);
        return analisada.protocol === 'https:' ? analisada.href : null;
    } catch (e) {
        return null;
    }
}

/**
 * Gera uma chave de idempotência (UUID v4). Usa crypto.randomUUID quando disponível
 * (contextos seguros) e cai para crypto.getRandomValues em HTTP não-local.
 */
export function gerarChaveIdempotencia() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return window.crypto.randomUUID();
    }
    const bytes = new Uint8Array(16);
    window.crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Formata uma data ISO em pt-BR. Retorna o texto alternativo se a data for ausente ou inválida.
 */
export function formatarData(iso, alternativo = '') {
    if (!iso) return alternativo;
    const data = new Date(iso);
    return Number.isNaN(data.getTime()) ? alternativo : data.toLocaleString('pt-BR');
}
