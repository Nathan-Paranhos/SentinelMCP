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
        int versaoRegistro = 0;
        var situacao = StatusIncidente.Reportado;
        CargaMemoria? cargaMaisRecente = null;
        DateTimeOffset? registradoEm = null;

        foreach (var ev in eventosFiltrados)
        {
            versaoFluxoCorrente = ev.VersaoFluxo;

            if (ev.Tipo == TiposDeEvento.IncidenteReportado)
            {
                versaoRegistro++;
                situacao = StatusIncidente.Reportado;
                registradoEm ??= ev.RegistradoEm;
                var carga = ev.CargaId.HasValue ? resolvedorDeCarga(ev.CargaId.Value) : null;
                if (carga != null)
                    cargaMaisRecente = carga;
            }
        }

        return new IncidenteReconstituido(
            IncidenteId: incidenteId,
            EspacoTrabalhoId: espacoTrabalhoId,
            ProjetoId: projetoId,
            VersaoFluxo: versaoFluxoCorrente,
            VersaoRegistro: Math.Max(versaoRegistro, versaoFluxoCorrente > 0 ? 1 : 0),
            Situacao: situacao,
            Titulo: cargaMaisRecente?.Titulo ?? string.Empty,
            Resumo: cargaMaisRecente?.Resumo ?? string.Empty,
            // Data do incidente obtida do payload
            OcorridoEm: cargaMaisRecente?.OcorridoEm ?? registradoEm ?? cargaMaisRecente?.CriadoEm ?? DateTimeOffset.UnixEpoch,
            ChavesTecnicas: cargaMaisRecente?.ChavesTecnicas ?? [],
            Evidencias: cargaMaisRecente?.Evidencias ?? []
        );
    }

    public static IncidenteAtual Projetar(IncidenteReconstituido estado, DateTimeOffset criadoEm) => new(
        estado.IncidenteId,
        estado.EspacoTrabalhoId,
        estado.ProjetoId,
        estado.VersaoRegistro,
        estado.VersaoFluxo,
        estado.Situacao,
        estado.Titulo,
        estado.Resumo,
        estado.OcorridoEm,
        estado.ChavesTecnicas,
        estado.Evidencias,
        criadoEm);
}
