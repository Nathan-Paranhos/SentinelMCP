namespace Sentinel.Core.Modelos;

public enum StatusDecisao
{
    Proposta = 0,
    Ativa = 1,
    Substituida = 2,
    Rejeitada = 3,
    Proposed = 0,
    Active = 1,
    Superseded = 2,
    Rejected = 3
}

public enum StatusIncidente
{
    Reportado = 0,
    Reported = 0
}

public enum PapelUsuario
{
    Leitor = 0,
    Colaborador = 1,
    Mantenedor = 2,
    Reader = 0,
    Contributor = 1,
    Maintainer = 2
}

public enum ModoBusca
{
    Lexical,
    Hibrido
}

public enum StatusCorrespondencia
{
    Correspondido,
    SemCorrespondencia
}
