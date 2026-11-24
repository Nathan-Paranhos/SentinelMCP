using Sentinel.Core.Modelos;

namespace Sentinel.Core.Consultas;

public sealed record ConsultaContexto(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string TermoBusca,
    string TipoRegistro = "decision",
    int Limite = 10,
    string PrincipalId = "",
    string? ClienteId = null,
    string? PrincipalEmissor = null
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

public sealed record ResultadoLinhaDoTempo(
    string TipoRegistro,
    Guid FluxoId,
    int VersaoFluxoAtual,
    int VersaoAlvo,
    DecisaoReconstituida? EstadoDecisao,
    IncidenteReconstituido? EstadoIncidente,
    string? ChaveTopico,
    List<EventoMemoria> Eventos,
    int? ProximaVersaoAnterior
);

public sealed record ResultadoComprovanteContexto(
    Guid ConsultaId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string PrincipalId,
    string? PrincipalEmissor,
    string? ClienteId,
    string ModoBusca,
    DateTimeOffset CriadoEm,
    List<ItemReciboConsulta> Itens,
    string Digest,
    bool IntegridadeVerificada
);

public sealed record HistoricoDoFluxo(
    List<EventoMemoria> Eventos,
    Dictionary<Guid, CargaMemoria> Cargas
);

public sealed record PainelDeRevisao(
    EspacoTrabalho EspacoTrabalho,
    List<EspacoTrabalho> EspacosDisponiveis,
    VinculoMembro Vinculo,
    List<PropostaPendente> Propostas,
    Dictionary<Guid, DecisaoAtual> DecisoesPorTopico,
    List<DecisaoAtual> DecisoesVigentes,
    List<IncidenteAtual> Incidentes,
    List<EventoMemoria> Eventos
);
