using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Modelos;

namespace Sentinel.Core.Comum;

public static class Relogio
{
    // Data/hora UTC truncada em microssegundos
    public static DateTimeOffset AgoraUtc() => Truncar(DateTimeOffset.UtcNow);

    public static DateTimeOffset Truncar(DateTimeOffset instante)
    {
        var utc = instante.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}

public static class DigestDeComprovante
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Calcula hash SHA-256 do comprovante
    public static string Calcular(
        Guid consultaId,
        Guid espacoTrabalhoId,
        Guid projetoId,
        string principalId,
        string? principalEmissor,
        string? clienteId,
        string modoBusca,
        DateTimeOffset criadoEm,
        IEnumerable<ItemReciboConsulta> itens)
    {
        var canonico = new
        {
            v = 1,
            queryId = consultaId,
            workspaceId = espacoTrabalhoId,
            projectId = projetoId,
            principalId,
            principalIssuer = principalEmissor,
            clientId = clienteId,
            searchMode = modoBusca,
            // Microssegundos desde a época Unix
            createdAtMicros = (Relogio.Truncar(criadoEm).UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
            items = itens.OrderBy(i => i.Posicao).Select(i => new
            {
                position = i.Posicao,
                recordType = i.TipoRegistro,
                streamId = i.FluxoId,
                recordId = i.RegistroId,
                recordVersion = i.VersaoRegistro,
                streamVersion = i.VersaoFluxo,
                status = i.Situacao
            })
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonico, Opcoes));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
