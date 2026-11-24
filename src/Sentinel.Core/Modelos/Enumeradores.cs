namespace Sentinel.Core.Modelos;

public enum StatusDecisao
{
    Proposta = 0,
    Ativa = 1,
    Substituida = 2,
    Rejeitada = 3
}

public enum StatusIncidente
{
    Reportado = 0
}

public enum PapelUsuario
{
    Leitor = 0,
    Colaborador = 1,
    Mantenedor = 2
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

public static class ConversorDeEnumeradores
{
    public static string ParaTexto(this StatusDecisao status) => status switch
    {
        StatusDecisao.Proposta => "proposed",
        StatusDecisao.Ativa => "active",
        StatusDecisao.Substituida => "superseded",
        StatusDecisao.Rejeitada => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static StatusDecisao ParaStatusDecisao(string texto) => texto switch
    {
        "proposed" => StatusDecisao.Proposta,
        "active" => StatusDecisao.Ativa,
        "superseded" => StatusDecisao.Substituida,
        "rejected" => StatusDecisao.Rejeitada,
        _ => throw new ArgumentOutOfRangeException(nameof(texto), texto, "Status de decisão desconhecido.")
    };

    public static string ParaTexto(this StatusIncidente status) => status switch
    {
        StatusIncidente.Reportado => "reported",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static StatusIncidente ParaStatusIncidente(string texto) => texto switch
    {
        "reported" => StatusIncidente.Reportado,
        _ => throw new ArgumentOutOfRangeException(nameof(texto), texto, "Status de incidente desconhecido.")
    };

    public static string ParaTexto(this PapelUsuario papel) => papel switch
    {
        PapelUsuario.Leitor => "Reader",
        PapelUsuario.Colaborador => "Contributor",
        PapelUsuario.Mantenedor => "Maintainer",
        _ => throw new ArgumentOutOfRangeException(nameof(papel), papel, null)
    };

    public static bool TentarConverterPapel(string? texto, out PapelUsuario papel)
    {
        switch (texto?.Trim().ToLowerInvariant())
        {
            case "reader":
                papel = PapelUsuario.Leitor;
                return true;
            case "contributor":
                papel = PapelUsuario.Colaborador;
                return true;
            case "maintainer":
                papel = PapelUsuario.Mantenedor;
                return true;
            default:
                papel = default;
                return false;
        }
    }

    public static PapelUsuario ParaPapel(string texto) =>
        TentarConverterPapel(texto, out var papel)
            ? papel
            : throw new ArgumentOutOfRangeException(nameof(texto), texto, "Papel desconhecido.");

    public static bool Atende(this PapelUsuario papelAtual, PapelUsuario papelMinimo) => (int)papelAtual >= (int)papelMinimo;
}
