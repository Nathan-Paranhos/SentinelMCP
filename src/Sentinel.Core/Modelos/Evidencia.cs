namespace Sentinel.Core.Modelos;

public sealed record Evidencia
{
    public string Tipo { get; init; } = string.Empty;
    public string Rotulo { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    public Evidencia() { }

    public Evidencia(string tipo, string rotulo, string url)
    {
        Tipo = tipo;
        Rotulo = rotulo;
        Url = url;
    }
}
