using System.Text.RegularExpressions;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;

namespace Sentinel.Core.Validacao;

public static class ValidadorDeInvariantes
{
    private static readonly Regex PadraoSlugAscii = new(@"^[a-zA-Z0-9_\-\.]{1,80}$", RegexOptions.Compiled);
    private static readonly string[] ParametrosSensiveisUrl = ["token", "secret", "password", "key", "api_key", "access_token"];

    public static void ValidarChaveDoTopico(string? chaveTopico)
    {
        if (string.IsNullOrWhiteSpace(chaveTopico))
            throw new ExcecaoDeValidacao("A chave do tópico é obrigatória.");
        if (chaveTopico.Length is < 1 or > 80)
            throw new ExcecaoDeValidacao("A chave do tópico deve possuir entre 1 e 80 caracteres.");
        if (!PadraoSlugAscii.IsMatch(chaveTopico))
            throw new ExcecaoDeValidacao("A chave do tópico deve ser um slug ASCII (alfanumérico, hifens, sublinhados ou pontos).");
    }

    public static void ValidarTitulo(string? titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo))
            throw new ExcecaoDeValidacao("O título é obrigatório.");
        if (titulo.Length > 160)
            throw new ExcecaoDeValidacao("O título não pode exceder 160 caracteres.");
    }

    public static void ValidarResumo(string? resumo)
    {
        if (string.IsNullOrWhiteSpace(resumo))
            throw new ExcecaoDeValidacao("O resumo é obrigatório.");
        if (resumo.Length > 2000)
            throw new ExcecaoDeValidacao("O resumo não pode exceder 2000 caracteres.");
    }

    public static void ValidarJustificativa(string? justificativa)
    {
        if (justificativa != null && justificativa.Length > 4000)
            throw new ExcecaoDeValidacao("A justificativa não pode exceder 4000 caracteres.");
    }

    public static void ValidarConsulta(string? consulta)
    {
        if (string.IsNullOrWhiteSpace(consulta))
            throw new ExcecaoDeValidacao("A consulta de busca é obrigatória.");
        if (consulta.Length > 300)
            throw new ExcecaoDeValidacao("A consulta não pode exceder 300 caracteres.");
    }

    public static void ValidarEvidencias(List<Evidencia>? evidencias, bool obrigatorio = true)
    {
        if (evidencias == null || evidencias.Count == 0)
        {
            if (obrigatorio)
                throw new ExcecaoDeValidacao("Pelo menos uma evidência técnica é obrigatória.");
            return;
        }

        if (evidencias.Count > 10)
            throw new ExcecaoDeValidacao("O limite máximo é de 10 evidências por registro.");

        foreach (var item in evidencias)
        {
            if (string.IsNullOrWhiteSpace(item.Tipo))
                throw new ExcecaoDeValidacao("O tipo da evidência é obrigatório.");
            if (string.IsNullOrWhiteSpace(item.Rotulo))
                throw new ExcecaoDeValidacao("O rótulo da evidência é obrigatório.");
            if (string.IsNullOrWhiteSpace(item.Url))
                throw new ExcecaoDeValidacao("A URL da evidência é obrigatória.");

            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ExcecaoDeValidacao($"A URL da evidência deve ser HTTPS válida: '{item.Url}'.");

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ExcecaoDeValidacao("A URL da evidência não pode conter credenciais embutidas.");

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var queryLower = uri.Query.ToLowerInvariant();
                foreach (var param in ParametrosSensiveisUrl)
                {
                    if (queryLower.Contains($"{param}="))
                        throw new ExcecaoDeValidacao($"A URL da evidência contém parâmetro sensível proibido: '{param}'.");
                }
            }
        }
    }

    public static void ValidarChavesTecnicas(List<string>? chavesTecnicas)
    {
        if (chavesTecnicas == null) return;
        if (chavesTecnicas.Count > 20)
            throw new ExcecaoDeValidacao("O limite máximo é de 20 chaves técnicas.");

        foreach (var chave in chavesTecnicas)
        {
            if (string.IsNullOrWhiteSpace(chave))
                throw new ExcecaoDeValidacao("A chave técnica não pode ser vazia.");
            if (chave.Length > 100)
                throw new ExcecaoDeValidacao("A chave técnica não pode exceder 100 caracteres.");
        }
    }

    public static void ValidarIncidentesRelacionados(List<Guid>? incidentesIds)
    {
        if (incidentesIds == null) return;
        if (incidentesIds.Count > 10)
            throw new ExcecaoDeValidacao("O limite máximo é de 10 incidentes relacionados por proposta.");
    }

    public static void ValidarChaveIdempotencia(string? chaveIdempotencia)
    {
        if (string.IsNullOrWhiteSpace(chaveIdempotencia))
            throw new ExcecaoDeValidacao("A chave de idempotência é obrigatória.");
        if (chaveIdempotencia.Length > 128)
            throw new ExcecaoDeValidacao("A chave de idempotência não pode exceder 128 caracteres.");
    }

    public static void ValidarLimite(int limite, int minimo = 1, int maximo = 20)
    {
        if (limite < minimo || limite > maximo)
            throw new ExcecaoDeValidacao($"O limite deve estar entre {minimo} e {maximo}.");
    }

    public static void ValidarVersaoEsperada(int versaoEsperada)
    {
        if (versaoEsperada < 0)
            throw new ExcecaoDeValidacao("A versão esperada deve ser maior ou igual a zero.");
    }
}
