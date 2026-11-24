using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Core.Comum;

// Utilitários criptográficos para senhas locais (PBKDF2) e chaves de API (SHA-256)
public static class SegurancaLocal
{
    private const int IteracoesPbkdf2 = 210_000;
    private const int TamanhoSaltBytes = 16;
    private const int TamanhoHashBytes = 32;

    public static string GerarHashSenha(string senha)
    {
        var salt = RandomNumberGenerator.GetBytes(TamanhoSaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(senha),
            salt,
            IteracoesPbkdf2,
            HashAlgorithmName.SHA256,
            TamanhoHashBytes);

        return $"pbkdf2-sha256${IteracoesPbkdf2}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerificarSenha(string senha, string? hashArmazenado)
    {
        if (string.IsNullOrEmpty(senha) || string.IsNullOrWhiteSpace(hashArmazenado))
            return false;

        var partes = hashArmazenado.Split('$');
        if (partes.Length != 4 || partes[0] != "pbkdf2-sha256")
            return false;

        if (!int.TryParse(partes[1], out var iteracoes) || iteracoes < 10_000)
            return false;

        try
        {
            var salt = Convert.FromBase64String(partes[2]);
            var esperado = Convert.FromBase64String(partes[3]);
            var calculado = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(senha),
                salt,
                iteracoes,
                HashAlgorithmName.SHA256,
                esperado.Length);

            return CryptographicOperations.FixedTimeEquals(calculado, esperado);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static (string TokenPlano, string PrefixoVisivel, string HashSha256Hex) GerarTokenChaveApi()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var sufixo = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var token = $"snt_{sufixo}";
        var prefixo = token[..Math.Min(14, token.Length)];
        var hashHex = HashChaveApiSha256Hex(token);
        return (token, prefixo, hashHex);
    }

    public static string HashChaveApiSha256Hex(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
