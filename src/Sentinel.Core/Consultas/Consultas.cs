using Sentinel.Core.Modelos;

namespace Sentinel.Core.Consultas;

public sealed record ConsultaContexto(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string TermoBusca,
    string TipoRegistro = "decision",
    int Limite = 10,
    string PrincipalId = "",
    string? ClienteId = null
);

public sealed record ItemContexto(
    string TipoRegistro,
    Guid? TopicoId,
    Guid? IncidenteId,
    Guid RegistroId,
    int VersaoRegistro,
    int VersaoFluxo,
    string Situacao,
    string Titulo,
    string Resumo,
    List<Evidencia> Evidencias
);

public sealed record ResultadoConsultaContexto(
    Guid ConsultaId,
    string StatusCorrespondencia,
    string ModoBusca,
    List<ItemContexto> Itens
);

public sealed record ConsultaLinhaDoTempo(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string TipoRegistro,
    Guid FluxoId,
    int? NaVersao = null,
    int? AntesDaVersao = null,
    int Limite = 20
);

public sealed record ItemEventoLinhaDoTempo(
    Guid EventoId,
    int VersaoFluxo,
    string Tipo,
    DateTimeOffset RegistradoEm,
    string AutorSujeito,
    string? ClienteId,
    Guid? CargaId
);

public sealed record ResultadoLinhaDoTempo(
    Guid FluxoId,
    int VersaoFluxoAtual,
    int VersaoAlvo,
    object EstadoReconstituido,
    List<ItemEventoLinhaDoTempo> Eventos,
    int? ProximaVersaoAnterior
);

public sealed record ConsultaComprovanteContexto(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid ConsultaId,
    string PrincipalId
);

public sealed record ResultadoComprovanteContexto(
    Guid ConsultaId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string PrincipalId,
    string? ClienteId,
    string ModoBusca,
    DateTimeOffset CriadoEm,
    List<ItemReciboConsulta> Itens
);
