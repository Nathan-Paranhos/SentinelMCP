using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Comandos;

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

    // Hashes dos argumentos validados para controle de idempotência
    public static string HashProporDecisao(ComandoProporDecisao c) => CalcularImpressaoDigital(new
    {
        c.EspacoTrabalhoId,
        c.ProjetoId,
        c.ChaveTopico,
        c.VersaoEsperada,
        c.Titulo,
        c.Resumo,
        c.Justificativa,
        c.ChavesTecnicas,
        c.IncidentesRelacionadosIds,
        c.Evidencias
    });

    public static string HashAprovarDecisao(ComandoAprovarDecisao c) => CalcularImpressaoDigital(new
    {
        c.EspacoTrabalhoId,
        c.ProjetoId,
        c.TopicoId,
        c.PropostaId,
        c.VersaoEsperada
    });

    public static string HashRejeitarDecisao(ComandoRejeitarDecisao c) => CalcularImpressaoDigital(new
    {
        c.EspacoTrabalhoId,
        c.ProjetoId,
        c.TopicoId,
        c.PropostaId,
        c.VersaoEsperada,
        c.Motivo
    });

    public static string HashRestaurarDecisao(ComandoRestaurarDecisao c) => CalcularImpressaoDigital(new
    {
        c.EspacoTrabalhoId,
        c.ProjetoId,
        c.TopicoId,
        c.VersaoParaRestaurar,
        c.VersaoEsperada
    });

    public static string HashRegistrarIncidente(ComandoRegistrarIncidente c) => CalcularImpressaoDigital(new
    {
        c.EspacoTrabalhoId,
        c.ProjetoId,
        c.Titulo,
        c.Resumo,
        c.OcorridoEm,
        c.ChavesTecnicas,
        c.Evidencias
    });
}
