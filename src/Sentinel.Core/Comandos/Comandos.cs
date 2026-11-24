using Sentinel.Core.Modelos;

namespace Sentinel.Core.Comandos;

public sealed record ComandoProporDecisao(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string ChaveTopico,
    int VersaoEsperada,
    string Titulo,
    string Resumo,
    string? Justificativa,
    List<string>? ChavesTecnicas,
    List<Guid>? IncidentesRelacionadosIds,
    List<Evidencia> Evidencias,
    string ChaveIdempotencia,
    string AutorSujeito,
    string AutorEmissor,
    string? ClienteId
);

public sealed record ResultadoProporDecisao(
    Guid TopicoId,
    Guid PropostaId,
    int VersaoFluxo,
    string Situacao = "proposed"
);

public sealed record ComandoAprovarDecisao(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid TopicoId,
    Guid PropostaId,
    int VersaoEsperada,
    string ChaveIdempotencia,
    string RevisorSujeito,
    string RevisorEmissor
);

public sealed record ResultadoAprovarDecisao(
    Guid TopicoId,
    Guid RegistroAtivoId,
    int VersaoFluxo,
    string Situacao = "active"
);

public sealed record ComandoRejeitarDecisao(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid TopicoId,
    Guid PropostaId,
    int VersaoEsperada,
    string Motivo,
    string ChaveIdempotencia,
    string RevisorSujeito,
    string RevisorEmissor
);

public sealed record ResultadoRejeitarDecisao(
    Guid TopicoId,
    int VersaoFluxo,
    string Situacao
);

public sealed record ComandoRestaurarDecisao(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid TopicoId,
    int VersaoParaRestaurar,
    int VersaoEsperada,
    string ChaveIdempotencia,
    string RevisorSujeito,
    string RevisorEmissor
);

public sealed record ResultadoRestaurarDecisao(
    Guid TopicoId,
    Guid PropostaId,
    int VersaoFluxo,
    string Situacao = "proposed"
);

public sealed record ComandoRegistrarIncidente(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string Titulo,
    string Resumo,
    DateTimeOffset? OcorridoEm,
    List<string>? ChavesTecnicas,
    List<Evidencia> Evidencias,
    string ChaveIdempotencia,
    string AutorSujeito,
    string AutorEmissor,
    string? ClienteId
);

public sealed record ResultadoRegistrarIncidente(
    Guid IncidenteId,
    int VersaoFluxo,
    string Situacao = "reported"
);
