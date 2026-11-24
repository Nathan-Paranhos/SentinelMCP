using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Persistence.Contratos;

namespace Sentinel.Api.Auth;

public sealed record IdentidadeResolvida(
    string Emissor,
    string Sujeito,
    string? ClienteId,
    List<string> Escopos,
    PapelUsuario? PapelDeclarado
);

public interface IServicoIdentidade
{
    Task<VinculoMembro> AutenticarEAutorizarAsync(
        HttpContext contextoHttp,
        Guid espacoTrabalhoId,
        string escopoNecessario,
        PapelUsuario papelMinimoObrigatorio,
        CancellationToken ct = default);

    IdentidadeResolvida ResolverIdentidade(HttpContext contextoHttp);
}

public sealed class ServicoIdentidade : IServicoIdentidade
{
    private readonly IRepositorioEspacoTrabalho _repositorioEspacoTrabalho;

    public ServicoIdentidade(IRepositorioEspacoTrabalho repositorioEspacoTrabalho)
    {
        _repositorioEspacoTrabalho = repositorioEspacoTrabalho;
    }

    public IdentidadeResolvida ResolverIdentidade(HttpContext contextoHttp)
    {
        var cabecalhoAutorizacao = contextoHttp.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(cabecalhoAutorizacao) || !cabecalhoAutorizacao.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExcecaoDeNaoAutorizado("Cabeçalho de autorização ausente ou inválido. Esperado token Bearer.");
        }

        var token = cabecalhoAutorizacao["Bearer ".Length..].Trim();

        if (token.StartsWith("mock:", StringComparison.OrdinalIgnoreCase))
        {
            var dadosBrutos = token["mock:".Length..].Trim();
            string emissor = "https://auth.sentinel.dev";
            string sujeito = "agent-dev-1";
            string papelTexto = "Contributor";
            string escoposTexto = "read:memory,write:decision,write:incident";
            string clientId = "claude-code";

            if (dadosBrutos.Contains('|'))
            {
                var partes = dadosBrutos.Split('|');
                if (partes.Length > 0 && !string.IsNullOrEmpty(partes[0])) sujeito = partes[0];
                if (partes.Length > 1 && !string.IsNullOrEmpty(partes[1])) papelTexto = partes[1];
                if (partes.Length > 2 && !string.IsNullOrEmpty(partes[2])) escoposTexto = partes[2];
                if (partes.Length > 3 && !string.IsNullOrEmpty(partes[3])) clientId = partes[3];
                if (partes.Length > 4 && !string.IsNullOrEmpty(partes[4])) emissor = partes[4];
            }
            else
            {
                var fimEsquema = dadosBrutos.IndexOf("://", StringComparison.OrdinalIgnoreCase);
                if (fimEsquema >= 0)
                {
                    var proximoDoisPontos = dadosBrutos.IndexOf(':', fimEsquema + 3);
                    if (proximoDoisPontos >= 0)
                    {
                        emissor = dadosBrutos[..proximoDoisPontos];
                        dadosBrutos = dadosBrutos[(proximoDoisPontos + 1)..];
                    }
                }

                var partes = dadosBrutos.Split(':');
                if (partes.Length >= 4)
                {
                    sujeito = partes[0];
                    papelTexto = partes[1];
                    clientId = partes[^1];
                    escoposTexto = string.Join(':', partes.Skip(2).Take(partes.Length - 3));
                }
                else
                {
                    if (partes.Length > 0 && !string.IsNullOrEmpty(partes[0])) sujeito = partes[0];
                    if (partes.Length > 1 && !string.IsNullOrEmpty(partes[1])) papelTexto = partes[1];
                    if (partes.Length > 2 && !string.IsNullOrEmpty(partes[2])) escoposTexto = partes[2];
                }
            }

            var escopos = escoposTexto.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Enum.TryParse<PapelUsuario>(papelTexto, true, out var papel);

            return new IdentidadeResolvida(emissor, sujeito, clientId, escopos, papel);
        }

        var tokensJwtFicticios = new Dictionary<string, (string Sujeito, string Emissor, string? ClienteId, List<string> Escopos, PapelUsuario Papel)>
        {
            ["agent-token"] = ("agent-dev-1", "https://auth.sentinel.dev", "claude-code", ["read:memory", "write:decision", "write:incident"], PapelUsuario.Contributor),
            ["maintainer-token"] = ("maintainer-alice", "https://auth.sentinel.dev", null, ["read:memory", "write:decision", "write:incident", "admin:all"], PapelUsuario.Maintainer),
            ["reader-token"] = ("reader-bob", "https://auth.sentinel.dev", "cursor-ide", ["read:memory"], PapelUsuario.Reader)
        };

        if (tokensJwtFicticios.TryGetValue(token, out var info))
        {
            return new IdentidadeResolvida(info.Emissor, info.Sujeito, info.ClienteId, info.Escopos, info.Papel);
        }

        throw new ExcecaoDeNaoAutorizado($"Token Bearer inválido ou não reconhecido: '{token}'.");
    }

    public async Task<VinculoMembro> AutenticarEAutorizarAsync(
        HttpContext contextoHttp,
        Guid espacoTrabalhoId,
        string escopoNecessario,
        PapelUsuario papelMinimoObrigatorio,
        CancellationToken ct = default)
    {
        var identidade = ResolverIdentidade(contextoHttp);

        if (!identidade.Escopos.Contains(escopoNecessario) && !identidade.Escopos.Contains("admin:all"))
        {
            throw new ExcecaoDeNaoAutorizado($"Escopo insuficiente. Necessário: '{escopoNecessario}'. Escopos presentes: '{string.Join(", ", identidade.Escopos)}'.");
        }

        var vinculo = await _repositorioEspacoTrabalho.ObterVinculoMembroAsync(espacoTrabalhoId, identidade.Emissor, identidade.Sujeito, ct);
        if (vinculo == null)
        {
            throw new ExcecaoDeNaoAutorizado($"O usuário '{identidade.Sujeito}' não possui vínculo cadastrado no espaço de trabalho '{espacoTrabalhoId}'.");
        }

        if (!vinculo.Ativo)
        {
            throw new ExcecaoDeNaoAutorizado($"O vínculo do usuário '{identidade.Sujeito}' no espaço de trabalho '{espacoTrabalhoId}' está inativo.");
        }

        if (!PossuiPapelSuficiente(vinculo.Papel, papelMinimoObrigatorio))
        {
            throw new ExcecaoDeNaoAutorizado($"Papel insuficiente. Atual: '{vinculo.Papel}', Mínimo exigido: '{papelMinimoObrigatorio}'.");
        }

        return vinculo;
    }

    private static bool PossuiPapelSuficiente(PapelUsuario papelAtual, PapelUsuario papelNecessario)
    {
        var niveis = new Dictionary<PapelUsuario, int>
        {
            [PapelUsuario.Leitor] = 1,
            [PapelUsuario.Colaborador] = 2,
            [PapelUsuario.Mantenedor] = 3,
            [PapelUsuario.Reader] = 1,
            [PapelUsuario.Contributor] = 2,
            [PapelUsuario.Maintainer] = 3
        };

        return niveis.GetValueOrDefault(papelAtual, 0) >= niveis.GetValueOrDefault(papelNecessario, 0);
    }
}
