using Sentinel.Core.Modelos;

namespace Sentinel.Core.Redutores;

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
        CargaMemoria? cargaAtiva = null;
        CargaMemoria? cargaMaisRecente = null;

        foreach (var ev in eventosFiltrados)
        {
            versaoFluxoCorrente = ev.VersaoFluxo;
            var carga = ev.CargaId.HasValue ? resolvedorDeCarga(ev.CargaId.Value) : null;
            if (carga != null)
            {
                cargaMaisRecente = carga;
            }

            switch (ev.Tipo)
            {
                case "DecisionProposed":
                    propostaPendenteId = ev.CargaId;
                    if (registroAtivoId == null)
                    {
                        situacao = StatusDecisao.Proposta;
                    }
                    break;

                case "DecisionApproved":
                    registroAtivoId = ev.CargaId ?? propostaPendenteId;
                    versaoRegistroAtivo = ev.VersaoFluxo;
                    cargaAtiva = carga ?? (registroAtivoId.HasValue ? resolvedorDeCarga(registroAtivoId.Value) : null);
                    propostaPendenteId = null;
                    situacao = StatusDecisao.Ativa;
                    break;

                case "DecisionRejected":
                    propostaPendenteId = null;
                    if (registroAtivoId == null)
                    {
                        situacao = StatusDecisao.Rejeitada;
                    }
                    break;

                case "DecisionSuperseded":
                    situacao = StatusDecisao.Substituida;
                    break;
            }
        }

        var cargaExibicao = cargaAtiva ?? cargaMaisRecente;

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
        );
    }
}
