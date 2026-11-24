namespace Sentinel.Core.Modelos;

public sealed record EspacoTrabalho(
    Guid EspacoTrabalhoId,
    string Nome,
    string IdentificadorSlug,
    DateTimeOffset CriadoEm
);

public sealed record Projeto(
    Guid ProjetoId,
    Guid EspacoTrabalhoId,
    string Nome,
    string IdentificadorSlug,
    DateTimeOffset CriadoEm
);

public sealed record VinculoMembro(
    Guid VinculoId,
    Guid EspacoTrabalhoId,
    string Emissor,
    string Sujeito,
    PapelUsuario Papel,
    bool Ativo,
    DateTimeOffset CriadoEm
);

public sealed record TopicoDecisao(
    Guid TopicoId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string ChaveTopico,
    int VersaoAtual,
    DateTimeOffset CriadoEm,
    DateTimeOffset AtualizadoEm
);

public sealed record FluxoIncidente(
    Guid IncidenteId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    int VersaoAtual,
    DateTimeOffset CriadoEm,
    DateTimeOffset AtualizadoEm
);

public sealed record EventoMemoria(
    Guid EventoId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid FluxoId,
    int VersaoFluxo,
    string Tipo,
    int VersaoEsquema,
    DateTimeOffset RegistradoEm,
    string AutorSujeito,
    string AutorEmissor,
    string? ClienteId,
    Guid? CargaId,
    Guid? EventoReferenciaId
)
{
    // Motivo da rejeição
    public string? Motivo { get; init; }
}

public sealed record CargaMemoria(
    Guid CargaId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string Titulo,
    string Resumo,
    string? Justificativa,
    List<string> ChavesTecnicas,
    List<Guid> IncidentesRelacionadosIds,
    List<Evidencia> Evidencias,
    DateTimeOffset CriadoEm
)
{
    // Data informada da ocorrência
    public DateTimeOffset? OcorridoEm { get; init; }
}

public sealed record PropostaPendente(
    Guid TopicoId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string ChaveTopico,
    int VersaoFluxo,
    Guid PropostaId,
    string Titulo,
    string Resumo,
    string? Justificativa,
    List<string> ChavesTecnicas,
    List<Guid> IncidentesRelacionadosIds,
    List<Evidencia> Evidencias,
    string AutorSujeito,
    string AutorEmissor,
    string? ClienteId,
    DateTimeOffset PropostaEm
);

public sealed record DecisaoAtual(
    Guid TopicoId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string ChaveTopico,
    Guid? RegistroAtivoId,
    int? VersaoRegistroAtivo,
    int VersaoFluxo,
    StatusDecisao Situacao,
    Guid? PropostaPendenteId,
    string Titulo,
    string Resumo,
    string? Justificativa,
    List<string> ChavesTecnicas,
    List<Guid> IncidentesRelacionadosIds,
    List<Evidencia> Evidencias,
    DateTimeOffset AtualizadoEm
);

public sealed record IncidenteAtual(
    Guid IncidenteId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    int VersaoRegistro,
    int VersaoFluxo,
    StatusIncidente Situacao,
    string Titulo,
    string Resumo,
    DateTimeOffset OcorridoEm,
    List<string> ChavesTecnicas,
    List<Evidencia> Evidencias,
    DateTimeOffset CriadoEm
);

public sealed record ReciboComando(
    Guid ReciboId,
    Guid EspacoTrabalhoId,
    string PrincipalId,
    string Operacao,
    string ChaveIdempotencia,
    string HashArgumentos,
    string ResultadoSerializado,
    DateTimeOffset CriadoEm
);

public sealed record ReciboConsulta(
    Guid ConsultaId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string PrincipalId,
    string? PrincipalEmissor,
    string? ClienteId,
    string ModoBusca,
    DateTimeOffset CriadoEm,
    List<ItemReciboConsulta> Itens,
    string Digest
);

public sealed record ItemReciboConsulta(
    Guid ItemReciboId,
    Guid ConsultaId,
    Guid EspacoTrabalhoId,
    int Posicao,
    string TipoRegistro,
    Guid FluxoId,
    Guid RegistroId,
    int VersaoRegistro,
    int VersaoFluxo,
    string Situacao
);

public sealed record DecisaoReconstituida(
    Guid TopicoId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string ChaveTopico,
    int VersaoFluxo,
    StatusDecisao Situacao,
    Guid? RegistroAtivoId,
    int? VersaoRegistroAtivo,
    Guid? PropostaPendenteId,
    string Titulo,
    string Resumo,
    string? Justificativa,
    List<string> ChavesTecnicas,
    List<Guid> IncidentesRelacionadosIds,
    List<Evidencia> Evidencias
)
{
    // Carga e evento da proposta pendente
    public CargaMemoria? CargaPropostaPendente { get; init; }
    public EventoMemoria? EventoPropostaPendente { get; init; }
}

public sealed record IncidenteReconstituido(
    Guid IncidenteId,
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    int VersaoFluxo,
    int VersaoRegistro,
    StatusIncidente Situacao,
    string Titulo,
    string Resumo,
    DateTimeOffset OcorridoEm,
    List<string> ChavesTecnicas,
    List<Evidencia> Evidencias
);

public sealed record UsuarioLocal(
    Guid UsuarioId,
    string NomeUsuario,
    string Emissor,
    string Sujeito,
    string? NomeExibicao,
    string HashSenha,
    bool Ativo,
    DateTimeOffset CriadoEm
);

public sealed record ChaveApiAgente(
    Guid ChaveId,
    Guid EspacoTrabalhoId,
    string Nome,
    string PrefixoChave,
    string HashChave,
    string Emissor,
    string Sujeito,
    string? ClienteId,
    List<string> Escopos,
    bool Ativo,
    string CriadoPorSujeito,
    DateTimeOffset CriadoEm,
    DateTimeOffset? ExpiraEm,
    DateTimeOffset? UltimoUsoEm
);

public sealed record ResultadoCriarChaveApi(
    ChaveApiAgente Chave,
    string TokenPlano
);

public sealed record ResultadoConfiguracaoInicial(
    EspacoTrabalho EspacoTrabalho,
    Projeto Projeto,
    UsuarioLocal Usuario,
    VinculoMembro Vinculo
);
