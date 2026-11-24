using Sentinel.Core.Modelos;

namespace Sentinel.Core.Redutores;

public static class RedutorDeIncidente
{
    public static IncidenteReconstituido Reconstituir(
        Guid incidenteId,
        Guid espacoTrabalhoId,
        Guid projetoId,
        IEnumerable<EventoMemoria> eventos,
        Func<Guid, CargaMemoria?> resolvedorDeCarga,
        int? naVersao = null)
    {
        var eventosFiltrados = eventos
            .Where(e => !naVersao.HasValue || e.VersaoFluxo <= naVersao.Value)
            .OrderBy(e => e.VersaoFluxo);

        int versaoFluxoCorrente = 0;
        int versaoRegistro = 1;
        var situacao = StatusIncidente.Reportado;
        CargaMemoria? cargaMaisRecente = null;

        foreach (var ev in eventosFiltrados)
        {
            versaoFluxoCorrente = ev.VersaoFluxo;
            versaoRegistro = ev.VersaoFluxo;
            var carga = ev.CargaId.HasValue ? resolvedorDeCarga(ev.CargaId.Value) : null;
            if (carga != null)
            {
                cargaMaisRecente = carga;
            }

            if (ev.Tipo == "IncidentReported")
            {
                situacao = StatusIncidente.Reportado;
            }
        }

        return new IncidenteReconstituido(
            IncidenteId: incidenteId,
            EspacoTrabalhoId: espacoTrabalhoId,
            ProjetoId: projetoId,
            VersaoFluxo: versaoFluxoCorrente,
            VersaoRegistro: versaoRegistro,
            Situacao: situacao,
            Titulo: cargaMaisRecente?.Titulo ?? string.Empty,
            Resumo: cargaMaisRecente?.Resumo ?? string.Empty,
            OcorridoEm: cargaMaisRecente?.CriadoEm ?? DateTimeOffset.UtcNow,
            ChavesTecnicas: cargaMaisRecente?.ChavesTecnicas ?? [],
            Evidencias: cargaMaisRecente?.Evidencias ?? []
        );
    }
}
