using Sentinel.Core.Modelos;

namespace Sentinel.Core.Redutores;

public static class TiposDeEvento
{
    public const string DecisaoProposta = "DecisionProposed";
    public const string DecisaoAprovada = "DecisionApproved";
    public const string DecisaoRejeitada = "DecisionRejected";
    public const string DecisaoSubstituida = "DecisionSuperseded";
    public const string IncidenteReportado = "IncidentReported";
}

// Redutor de fluxo de decisões
public static class RedutorDeDecisao
{
    public static DecisaoReconstituida Reconstituir(
        Guid topicoId,
        Guid espacoTrabalhoId,
        Guid projetoId,
        string chaveTopico,
        IEnumerable<EventoMemoria> eventos,
        Func<Guid, CargaMemoria?> resolvedorDeCarga,
        int? naVersao = null)
    {
        var eventosFiltrados = eventos
            .Where(e => !naVersao.HasValue || e.VersaoFluxo <= naVersao.Value)
            .OrderBy(e => e.VersaoFluxo);

        int versaoFluxoCorrente = 0;
        var situacao = StatusDecisao.Proposta;
        Guid? registroAtivoId = null;
        int? versaoRegistroAtivo = null;
        Guid? propostaPendenteId = null;
        EventoMemoria? eventoPropostaPendente = null;
        CargaMemoria? cargaAtiva = null;
        CargaMemoria? cargaUltimaProposta = null;

        foreach (var ev in eventosFiltrados)
        {
            versaoFluxoCorrente = ev.VersaoFluxo;

            switch (ev.Tipo)
            {
                case TiposDeEvento.DecisaoProposta:
                    propostaPendenteId = ev.CargaId;
                    eventoPropostaPendente = ev;
                    cargaUltimaProposta = ev.CargaId.HasValue ? resolvedorDeCarga(ev.CargaId.Value) : null;
                    if (registroAtivoId == null)
                        situacao = StatusDecisao.Proposta;
                    break;

                case TiposDeEvento.DecisaoAprovada:
                    var aprovadaId = ev.CargaId ?? propostaPendenteId;
                    if (aprovadaId == null)
                        break; // evento sem proposta associada: ignorado de forma determinística
                    registroAtivoId = aprovadaId;
                    // Incrementa versão do registro ativo
                    versaoRegistroAtivo = (versaoRegistroAtivo ?? 0) + 1;
                    cargaAtiva = resolvedorDeCarga(aprovadaId.Value);
                    propostaPendenteId = null;
                    eventoPropostaPendente = null;
                    situacao = StatusDecisao.Ativa;
                    break;

                case TiposDeEvento.DecisaoRejeitada:
                    propostaPendenteId = null;
                    eventoPropostaPendente = null;
                    if (registroAtivoId == null)
                        situacao = StatusDecisao.Rejeitada;
                    break;

                case TiposDeEvento.DecisaoSubstituida:
                    situacao = StatusDecisao.Substituida;
                    break;
            }
        }

        var cargaPendente = propostaPendenteId.HasValue ? resolvedorDeCarga(propostaPendenteId.Value) : null;
        var cargaExibicao = cargaAtiva ?? cargaPendente ?? cargaUltimaProposta;

        return new DecisaoReconstituida(
            TopicoId: topicoId,
            EspacoTrabalhoId: espacoTrabalhoId,
            ProjetoId: projetoId,
            ChaveTopico: chaveTopico,
            VersaoFluxo: versaoFluxoCorrente,
            Situacao: situacao,
            RegistroAtivoId: registroAtivoId,
            VersaoRegistroAtivo: versaoRegistroAtivo,
            PropostaPendenteId: propostaPendenteId,
            Titulo: cargaExibicao?.Titulo ?? string.Empty,
            Resumo: cargaExibicao?.Resumo ?? string.Empty,
            Justificativa: cargaExibicao?.Justificativa,
            ChavesTecnicas: cargaExibicao?.ChavesTecnicas ?? [],
            IncidentesRelacionadosIds: cargaExibicao?.IncidentesRelacionadosIds ?? [],
            Evidencias: cargaExibicao?.Evidencias ?? []
        )
        {
            CargaPropostaPendente = cargaPendente,
            EventoPropostaPendente = eventoPropostaPendente
        };
    }

    // Converte o estado reconstituído em projeções
    public static (DecisaoAtual Atual, PropostaPendente? Pendente) Projetar(DecisaoReconstituida estado, DateTimeOffset atualizadoEm)
    {
        var atual = new DecisaoAtual(
            estado.TopicoId,
            estado.EspacoTrabalhoId,
            estado.ProjetoId,
            estado.ChaveTopico,
            estado.RegistroAtivoId,
            estado.VersaoRegistroAtivo,
            estado.VersaoFluxo,
            estado.Situacao,
            estado.PropostaPendenteId,
            estado.Titulo,
            estado.Resumo,
            estado.Justificativa,
            estado.ChavesTecnicas,
            estado.IncidentesRelacionadosIds,
            estado.Evidencias,
            atualizadoEm);

        PropostaPendente? pendente = null;
        if (estado.PropostaPendenteId.HasValue && estado.CargaPropostaPendente != null && estado.EventoPropostaPendente != null)
        {
            var c = estado.CargaPropostaPendente;
            var ev = estado.EventoPropostaPendente;
            pendente = new PropostaPendente(
                estado.TopicoId,
                estado.EspacoTrabalhoId,
                estado.ProjetoId,
                estado.ChaveTopico,
                estado.VersaoFluxo,
                estado.PropostaPendenteId.Value,
                c.Titulo,
                c.Resumo,
                c.Justificativa,
                c.ChavesTecnicas,
                c.IncidentesRelacionadosIds,
                c.Evidencias,
                ev.AutorSujeito,
                ev.AutorEmissor,
                ev.ClienteId,
                ev.RegistradoEm);
        }

        return (atual, pendente);
    }
}
