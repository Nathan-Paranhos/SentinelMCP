using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sentinel.Persistence.Common;

public static class IdempotencyHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string ComputeFingerprint<T>(T arguments)
    {
        var json = JsonSerializer.Serialize(arguments, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
