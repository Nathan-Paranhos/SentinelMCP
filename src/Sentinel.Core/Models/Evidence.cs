namespace Sentinel.Core.Models;

public sealed record Evidence
{
    public string Type { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    public Evidence() { }

    public Evidence(string type, string label, string url)
    {
        Type = type;
        Label = label;
        Url = url;
    }
}
