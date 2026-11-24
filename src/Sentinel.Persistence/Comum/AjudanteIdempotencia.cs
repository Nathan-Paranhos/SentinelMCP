using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sentinel.Persistence.Comum;

public static class AjudanteIdempotencia
{
    private static readonly JsonSerializerOptions OpcoesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string CalcularImpressaoDigital<T>(T argumentos)
    {
        var json = JsonSerializer.Serialize(argumentos, OpcoesJson);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
