using Sentinel.Core.Comandos;
using Sentinel.Core.Comum;
using Sentinel.Core.Consultas;
using Sentinel.Core.Contratos;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Sentinel.Core.Validacao;

namespace Sentinel.Core.CasosDeUso;

public enum CanalDeAcesso
{
    Mcp,
    Web,
    AdminSistema
}

// Identidade autenticada
public sealed record Ator(string Emissor, string Sujeito, string? ClienteId, CanalDeAcesso Canal);

public sealed record EntradaProporDecisao(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string? ChaveTopico,
    int VersaoEsperada,
    string? Titulo,
    string? Resumo,
    string? Justificativa,
    List<string>? ChavesTecnicas,
    List<Guid>? IncidentesRelacionadosIds,
    List<Evidencia>? Evidencias,
    string? ChaveIdempotencia);

public sealed record EntradaRegistrarIncidente(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string? Titulo,
    string? Resumo,
    DateTimeOffset? OcorridoEm,
    List<string>? ChavesTecnicas,
    List<Evidencia>? Evidencias,
    string? ChaveIdempotencia);

public sealed record EntradaConsultarContexto(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    string? TermoBusca,
    string? TipoRegistro,
    int Limite);

public sealed record EntradaRevisao(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid TopicoId,
    Guid PropostaId,
    int VersaoEsperada,
    string? ChaveIdempotencia,
    string? Motivo = null);

public sealed record EntradaRestaurar(
    Guid EspacoTrabalhoId,
    Guid ProjetoId,
    Guid TopicoId,
    int VersaoParaRestaurar,
    int VersaoEsperada,
    string? ChaveIdempotencia);

public sealed record EntradaConfiguracaoInicial(
    string? NomeWorkspace,
    string? SlugWorkspace,
    string? NomeProjeto,
    string? SlugProjeto,
    string? NomeUsuario,
    string? NomeExibicao,
    string? Senha);

public sealed record EntradaCriarChaveApi(
    Guid EspacoTrabalhoId,
    string? Nome,
    string? Sujeito,
    string? ClienteId,
    PapelUsuario Papel,
    List<string>? Escopos,
    int? DiasExpiracao);

public sealed record EntradaCriarUsuarioLocal(
    Guid EspacoTrabalhoId,
    string? NomeUsuario,
    string? NomeExibicao,
    string? Senha,
    PapelUsuario Papel);

// Camada de casos de uso de governança e memória
public sealed class ServicoDeMemoria
{
    private static readonly HashSet<string> EscoposValidosApi =
        ["read:memory", "write:decision", "write:incident"];

    private readonly IRepositorioEspacoTrabalho _espacos;
    private readonly IRepositorioDecisao _decisoes;
    private readonly IRepositorioIncidente _incidentes;
    private readonly IRepositorioContexto _contexto;
    private readonly IRepositorioHistorico _historico;
    private readonly IRepositorioUsuariosLocais? _usuarios;
    private readonly IRepositorioChavesApi? _chavesApi;

    public ServicoDeMemoria(
        IRepositorioEspacoTrabalho espacos,
        IRepositorioDecisao decisoes,
        IRepositorioIncidente incidentes,
        IRepositorioContexto contexto,
        IRepositorioHistorico historico,
        IRepositorioUsuariosLocais? usuarios = null,
        IRepositorioChavesApi? chavesApi = null)
    {
        _espacos = espacos;
        _decisoes = decisoes;
        _incidentes = incidentes;
        _contexto = contexto;
        _historico = historico;
        _usuarios = usuarios;
        _chavesApi = chavesApi;
    }

    // Valida vínculo e permissão no workspace
    public async Task<VinculoMembro> ExigirVinculoAsync(Guid espacoTrabalhoId, Ator ator, PapelUsuario papelMinimo, CancellationToken ct = default)
    {
        var vinculo = await _espacos.ObterVinculoMembroAsync(espacoTrabalhoId, ator.Emissor, ator.Sujeito, ct);
        if (vinculo == null || !vinculo.Ativo)
            throw new ExcecaoDeNaoEncontrado("Espaço de trabalho não encontrado.");

        if (!vinculo.Papel.Atende(papelMinimo))
            throw new ExcecaoDeAcessoNegado($"Papel insuficiente para a operação. Mínimo exigido: '{papelMinimo.ParaTexto()}'.");

        return vinculo;
    }

    private async Task ExigirProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct)
    {
        if (await _espacos.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct) == null)
            throw new ExcecaoDeNaoEncontrado("Projeto não encontrado.");
    }

    private static void ExigirCanalWeb(Ator ator)
    {
        // Exige canal web para operações de revisão
        if (ator.Canal != CanalDeAcesso.Web)
            throw new ExcecaoDeAcessoNegado("Revisão de decisões exige sessão web humana; credenciais MCP não são aceitas.");
    }

    private static void ExigirAdminSistema(Ator ator)
    {
        if (ator.Canal != CanalDeAcesso.AdminSistema)
            throw new ExcecaoDeAcessoNegado("Esta operação administrativa exige chave de administração (ADMIN_API_KEY).");
    }

    private async Task ExigirAdminSistemaOuMantenedorAsync(Ator ator, Guid espacoTrabalhoId, CancellationToken ct)
    {
        if (ator.Canal == CanalDeAcesso.AdminSistema)
        {
            if (await _espacos.ObterEspacoTrabalhoAsync(espacoTrabalhoId, ct) == null)
                throw new ExcecaoDeNaoEncontrado($"Workspace {espacoTrabalhoId} não encontrado.");
            return;
        }

        if (ator.Canal == CanalDeAcesso.Web)
        {
            await ExigirVinculoAsync(espacoTrabalhoId, ator, PapelUsuario.Mantenedor, ct);
            return;
        }

        throw new ExcecaoDeAcessoNegado("Credenciais MCP não podem administrar workspaces ou vínculos.");
    }

    public async Task<ResultadoProporDecisao> ProporDecisaoAsync(Ator ator, EntradaProporDecisao e, CancellationToken ct = default)
    {
        ValidadorDeInvariantes.ValidarChaveDoTopico(e.ChaveTopico);
        ValidadorDeInvariantes.ValidarVersaoEsperada(e.VersaoEsperada);
        ValidadorDeInvariantes.ValidarTitulo(e.Titulo);
        ValidadorDeInvariantes.ValidarResumo(e.Resumo);
        ValidadorDeInvariantes.ValidarJustificativa(e.Justificativa);
        ValidadorDeInvariantes.ValidarChavesTecnicas(e.ChavesTecnicas);
        ValidadorDeInvariantes.ValidarIncidentesRelacionados(e.IncidentesRelacionadosIds);
        ValidadorDeInvariantes.ValidarEvidencias(e.Evidencias, obrigatorio: true);
        ValidadorDeInvariantes.ValidarChaveIdempotencia(e.ChaveIdempotencia);

        await ExigirVinculoAsync(e.EspacoTrabalhoId, ator, PapelUsuario.Colaborador, ct);
        await ExigirProjetoAsync(e.EspacoTrabalhoId, e.ProjetoId, ct);

        var incidentes = (e.IncidentesRelacionadosIds ?? []).Distinct().ToList();
        foreach (var incidenteId in incidentes)
        {
            if (await _incidentes.ObterIncidentePorIdAsync(e.EspacoTrabalhoId, e.ProjetoId, incidenteId, ct) == null)
                throw new ExcecaoDeValidacao($"Incidente relacionado '{incidenteId}' não existe neste projeto.");
        }

        var comando = new ComandoProporDecisao(
            EspacoTrabalhoId: e.EspacoTrabalhoId,
            ProjetoId: e.ProjetoId,
            ChaveTopico: ValidadorDeInvariantes.NormalizarChaveDoTopico(e.ChaveTopico!),
            VersaoEsperada: e.VersaoEsperada,
            Titulo: e.Titulo!.Trim(),
            Resumo: e.Resumo!.Trim(),
            Justificativa: string.IsNullOrWhiteSpace(e.Justificativa) ? null : e.Justificativa.Trim(),
            ChavesTecnicas: (e.ChavesTecnicas ?? []).Select(k => k.Trim()).Distinct().ToList(),
            IncidentesRelacionadosIds: incidentes,
            Evidencias: e.Evidencias!,
            ChaveIdempotencia: e.ChaveIdempotencia!,
            AutorSujeito: ator.Sujeito,
            AutorEmissor: ator.Emissor,
            ClienteId: ator.ClienteId);

        return await _decisoes.ProporDecisaoAsync(comando, ct);
    }

    public async Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(Ator ator, EntradaRegistrarIncidente e, CancellationToken ct = default)
    {
        ValidadorDeInvariantes.ValidarTitulo(e.Titulo);
        ValidadorDeInvariantes.ValidarResumo(e.Resumo);
        ValidadorDeInvariantes.ValidarChavesTecnicas(e.ChavesTecnicas);
        ValidadorDeInvariantes.ValidarEvidencias(e.Evidencias, obrigatorio: true);
        ValidadorDeInvariantes.ValidarChaveIdempotencia(e.ChaveIdempotencia);
        if (e.OcorridoEm.HasValue && e.OcorridoEm.Value > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ExcecaoDeValidacao("A data de ocorrência não pode estar no futuro.");

        await ExigirVinculoAsync(e.EspacoTrabalhoId, ator, PapelUsuario.Colaborador, ct);
        await ExigirProjetoAsync(e.EspacoTrabalhoId, e.ProjetoId, ct);

        var comando = new ComandoRegistrarIncidente(
            EspacoTrabalhoId: e.EspacoTrabalhoId,
            ProjetoId: e.ProjetoId,
            Titulo: e.Titulo!.Trim(),
            Resumo: e.Resumo!.Trim(),
            OcorridoEm: e.OcorridoEm,
            ChavesTecnicas: (e.ChavesTecnicas ?? []).Select(k => k.Trim()).Distinct().ToList(),
            Evidencias: e.Evidencias!,
            ChaveIdempotencia: e.ChaveIdempotencia!,
            AutorSujeito: ator.Sujeito,
            AutorEmissor: ator.Emissor,
            ClienteId: ator.ClienteId);

        return await _incidentes.RegistrarIncidenteAsync(comando, ct);
    }

    public async Task<ResultadoAprovarDecisao> AprovarDecisaoAsync(Ator ator, EntradaRevisao e, CancellationToken ct = default)
    {
        ExigirCanalWeb(ator);
        ValidadorDeInvariantes.ValidarVersaoEsperada(e.VersaoEsperada);
        ValidadorDeInvariantes.ValidarChaveIdempotencia(e.ChaveIdempotencia);
        await ExigirVinculoAsync(e.EspacoTrabalhoId, ator, PapelUsuario.Mantenedor, ct);
        await ExigirProjetoAsync(e.EspacoTrabalhoId, e.ProjetoId, ct);

        return await _decisoes.AprovarDecisaoAsync(new ComandoAprovarDecisao(
            e.EspacoTrabalhoId, e.ProjetoId, e.TopicoId, e.PropostaId, e.VersaoEsperada,
            e.ChaveIdempotencia!, ator.Sujeito, ator.Emissor), ct);
    }

    public async Task<ResultadoRejeitarDecisao> RejeitarDecisaoAsync(Ator ator, EntradaRevisao e, CancellationToken ct = default)
    {
        ExigirCanalWeb(ator);
        ValidadorDeInvariantes.ValidarVersaoEsperada(e.VersaoEsperada);
        ValidadorDeInvariantes.ValidarChaveIdempotencia(e.ChaveIdempotencia);
        ValidadorDeInvariantes.ValidarMotivoRejeicao(e.Motivo);
        await ExigirVinculoAsync(e.EspacoTrabalhoId, ator, PapelUsuario.Mantenedor, ct);
        await ExigirProjetoAsync(e.EspacoTrabalhoId, e.ProjetoId, ct);

        return await _decisoes.RejeitarDecisaoAsync(new ComandoRejeitarDecisao(
            e.EspacoTrabalhoId, e.ProjetoId, e.TopicoId, e.PropostaId, e.VersaoEsperada,
            e.Motivo!.Trim(), e.ChaveIdempotencia!, ator.Sujeito, ator.Emissor), ct);
    }

    public async Task<ResultadoRestaurarDecisao> RestaurarDecisaoAsync(Ator ator, EntradaRestaurar e, CancellationToken ct = default)
    {
        ExigirCanalWeb(ator);
        ValidadorDeInvariantes.ValidarVersaoEsperada(e.VersaoEsperada);
        ValidadorDeInvariantes.ValidarChaveIdempotencia(e.ChaveIdempotencia);
        if (e.VersaoParaRestaurar < 1)
            throw new ExcecaoDeValidacao("A versão a restaurar deve ser maior ou igual a 1.");
        await ExigirVinculoAsync(e.EspacoTrabalhoId, ator, PapelUsuario.Mantenedor, ct);
        await ExigirProjetoAsync(e.EspacoTrabalhoId, e.ProjetoId, ct);

        return await _decisoes.RestaurarDecisaoAsync(new ComandoRestaurarDecisao(
            e.EspacoTrabalhoId, e.ProjetoId, e.TopicoId, e.VersaoParaRestaurar, e.VersaoEsperada,
            e.ChaveIdempotencia!, ator.Sujeito, ator.Emissor), ct);
    }

    public async Task<ResultadoConsultaContexto> ConsultarContextoAsync(Ator ator, EntradaConsultarContexto e, CancellationToken ct = default)
    {
        ValidadorDeInvariantes.ValidarConsulta(e.TermoBusca);
        ValidadorDeInvariantes.ValidarLimite(e.Limite, 1, 20);
        var tipo = ValidadorDeInvariantes.ValidarTipoConsulta(e.TipoRegistro);

        await ExigirVinculoAsync(e.EspacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        await ExigirProjetoAsync(e.EspacoTrabalhoId, e.ProjetoId, ct);

        return await _contexto.ConsultarContextoAsync(new ConsultaContexto(
            EspacoTrabalhoId: e.EspacoTrabalhoId,
            ProjetoId: e.ProjetoId,
            TermoBusca: e.TermoBusca!.Trim(),
            TipoRegistro: tipo,
            Limite: e.Limite,
            PrincipalId: ator.Sujeito,
            ClienteId: ator.ClienteId,
            PrincipalEmissor: ator.Emissor), ct);
    }

    public async Task<ResultadoComprovanteContexto> ObterComprovanteAsync(Ator ator, Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, CancellationToken ct = default)
    {
        var vinculo = await ExigirVinculoAsync(espacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        await ExigirProjetoAsync(espacoTrabalhoId, projetoId, ct);

        var comprovante = await _contexto.ObterComprovanteContextoAsync(espacoTrabalhoId, projetoId, consultaId, ct);

        // Valida permissão para visualizar comprovante
        var ehAutor = comprovante != null
                      && comprovante.PrincipalId == ator.Sujeito
                      && (comprovante.PrincipalEmissor == null || comprovante.PrincipalEmissor == ator.Emissor);
        if (comprovante == null || (!ehAutor && vinculo.Papel != PapelUsuario.Mantenedor))
            throw new ExcecaoDeNaoEncontrado("Comprovante de consulta não encontrado.");

        return comprovante;
    }

    public async Task<ResultadoLinhaDoTempo> ObterLinhaDoTempoAsync(Ator ator, ConsultaLinhaDoTempo c, CancellationToken ct = default)
    {
        var tipo = ValidadorDeInvariantes.ValidarTipoFluxo(c.TipoRegistro);
        ValidadorDeInvariantes.ValidarLimite(c.Limite, 1, 50);
        if (c.NaVersao is < 1)
            throw new ExcecaoDeValidacao("atVersion deve ser maior ou igual a 1.");
        if (c.AntesDaVersao is < 1)
            throw new ExcecaoDeValidacao("beforeVersion deve ser maior ou igual a 1.");

        await ExigirVinculoAsync(c.EspacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        await ExigirProjetoAsync(c.EspacoTrabalhoId, c.ProjetoId, ct);

        string? chaveTopico = null;
        if (tipo == "decision")
        {
            var topico = await _decisoes.ObterTopicoPorIdAsync(c.EspacoTrabalhoId, c.ProjetoId, c.FluxoId, ct)
                ?? throw new ExcecaoDeNaoEncontrado("Tópico de decisão não encontrado.");
            chaveTopico = topico.ChaveTopico;
        }
        else if (await _incidentes.ObterIncidentePorIdAsync(c.EspacoTrabalhoId, c.ProjetoId, c.FluxoId, ct) == null)
        {
            throw new ExcecaoDeNaoEncontrado("Incidente não encontrado.");
        }

        // Carrega histórico de eventos do fluxo
        var historico = await _historico.ObterHistoricoDoFluxoAsync(c.EspacoTrabalhoId, c.FluxoId, ct);
        var versaoAtual = historico.Eventos.Count == 0 ? 0 : historico.Eventos.Max(ev => ev.VersaoFluxo);
        if (c.NaVersao > versaoAtual)
            throw new ExcecaoDeValidacao($"atVersion ({c.NaVersao}) é maior que a versão atual do fluxo ({versaoAtual}).");

        var versaoAlvo = c.NaVersao ?? versaoAtual;
        CargaMemoria? Resolver(Guid id) => historico.Cargas.TryGetValue(id, out var carga) ? carga : null;

        DecisaoReconstituida? estadoDecisao = null;
        IncidenteReconstituido? estadoIncidente = null;
        if (tipo == "decision")
            estadoDecisao = RedutorDeDecisao.Reconstituir(c.FluxoId, c.EspacoTrabalhoId, c.ProjetoId, chaveTopico!, historico.Eventos, Resolver, versaoAlvo);
        else
            estadoIncidente = RedutorDeIncidente.Reconstituir(c.FluxoId, c.EspacoTrabalhoId, c.ProjetoId, historico.Eventos, Resolver, versaoAlvo);

        // Paginação reversa por versão
        var teto = Math.Min(versaoAlvo, (c.AntesDaVersao ?? int.MaxValue) - 1);
        var candidatos = historico.Eventos
            .Where(ev => ev.VersaoFluxo <= teto)
            .OrderByDescending(ev => ev.VersaoFluxo)
            .ToList();
        var pagina = candidatos.Take(c.Limite).ToList();
        int? proxima = candidatos.Count > pagina.Count ? pagina[^1].VersaoFluxo : null;

        return new ResultadoLinhaDoTempo(tipo, c.FluxoId, versaoAtual, versaoAlvo, estadoDecisao, estadoIncidente, chaveTopico, pagina, proxima);
    }

    public async Task<PainelDeRevisao> ObterPainelAsync(Ator ator, Guid? espacoTrabalhoId, CancellationToken ct = default)
    {
        ExigirCanalWeb(ator);
        var espacos = await _espacos.ObterEspacosDaIdentidadeAsync(ator.Emissor, ator.Sujeito, ct);
        if (espacos.Count == 0)
            throw new ExcecaoDeNaoEncontrado("Nenhum espaço de trabalho vinculado a esta identidade.");

        var espaco = espacoTrabalhoId.HasValue
            ? espacos.FirstOrDefault(x => x.EspacoTrabalhoId == espacoTrabalhoId.Value)
              ?? throw new ExcecaoDeNaoEncontrado("Espaço de trabalho não encontrado.")
            : espacos[0];

        var vinculo = await ExigirVinculoAsync(espaco.EspacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        var id = espaco.EspacoTrabalhoId;

        var projetos = await _espacos.ObterProjetosPorEspacoTrabalhoAsync(id, ct);
        var propostas = await _decisoes.ObterPropostasPendentesAsync(id, ct: ct);
        var decisoes = await _decisoes.ObterDecisoesAsync(id, ct: ct);
        var vigentes = decisoes.Where(d => d.Situacao == StatusDecisao.Ativa && d.RegistroAtivoId != null).ToList();
        var incidentes = await _incidentes.ObterIncidentesAsync(id, ct: ct);
        var eventos = await _decisoes.ObterEventosRecentesAsync(id, 50, ct);

        return new PainelDeRevisao(
            espaco, espacos, vinculo, projetos, propostas,
            decisoes.ToDictionary(d => d.TopicoId),
            vigentes, incidentes, eventos);
    }

    // Lista workspaces e projetos acessíveis à identidade autenticada
    public async Task<List<EspacoComProjetos>> ObterEspacosEProjetosDaIdentidadeAsync(Ator ator, CancellationToken ct = default)
    {
        var espacos = await _espacos.ObterEspacosDaIdentidadeAsync(ator.Emissor, ator.Sujeito, ct);
        var resultado = new List<EspacoComProjetos>(espacos.Count);

        foreach (var espaco in espacos)
        {
            var vinculo = await _espacos.ObterVinculoMembroAsync(espaco.EspacoTrabalhoId, ator.Emissor, ator.Sujeito, ct);
            if (vinculo == null || !vinculo.Ativo)
                continue;

            var projetos = await _espacos.ObterProjetosPorEspacoTrabalhoAsync(espaco.EspacoTrabalhoId, ct);
            resultado.Add(new EspacoComProjetos(espaco, vinculo.Papel, projetos));
        }

        return resultado;
    }

    public async Task<List<DecisaoAtual>> ListarDecisoesVigentesDoProjetoAsync(Ator ator, Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        await ExigirVinculoAsync(espacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        await ExigirProjetoAsync(espacoTrabalhoId, projetoId, ct);
        return await _decisoes.ObterDecisoesVigentesAsync(espacoTrabalhoId, projetoId, ct);
    }

    public async Task<List<IncidenteAtual>> ListarIncidentesDoProjetoAsync(Ator ator, Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        await ExigirVinculoAsync(espacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        await ExigirProjetoAsync(espacoTrabalhoId, projetoId, ct);
        return await _incidentes.ObterIncidentesAsync(espacoTrabalhoId, projetoId, ct);
    }

    public async Task<EspacoTrabalho> CriarEspacoTrabalhoAsync(Ator ator, string nome, string slug, Guid? espacoTrabalhoId = null, CancellationToken ct = default)
    {
        ExigirAdminSistema(ator);
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120)
            throw new ExcecaoDeValidacao("Nome do workspace deve ter entre 1 e 120 caracteres.");
        ValidadorDeInvariantes.ValidarSlug(slug, "slug do workspace");

        return await _espacos.CriarEspacoTrabalhoAsync(nome.Trim(), slug.Trim().ToLowerInvariant(), espacoTrabalhoId, ct);
    }

    public async Task<EspacoTrabalho> AtualizarEspacoTrabalhoAsync(Ator ator, Guid espacoTrabalhoId, string nome, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (string.IsNullOrWhiteSpace(nome) || nome.Trim().Length > 120)
            throw new ExcecaoDeValidacao("Nome do workspace deve ter entre 1 e 120 caracteres.");

        return await _espacos.AtualizarEspacoTrabalhoAsync(espacoTrabalhoId, nome.Trim(), ct)
            ?? throw new ExcecaoDeNaoEncontrado($"Workspace {espacoTrabalhoId} não encontrado.");
    }

    public async Task<List<EspacoTrabalho>> ListarTodosEspacosAsync(Ator ator, CancellationToken ct = default)
    {
        if (ator.Canal == CanalDeAcesso.AdminSistema)
            return await _espacos.ListarTodosEspacosAsync(ct);

        ExigirCanalWeb(ator);
        return await _espacos.ObterEspacosDaIdentidadeAsync(ator.Emissor, ator.Sujeito, ct);
    }

    public async Task<DetalhesEspacoTrabalho> ObterDetalhesEspacoAsync(Ator ator, Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        var espaco = await _espacos.ObterEspacoTrabalhoAsync(espacoTrabalhoId, ct)
            ?? throw new ExcecaoDeNaoEncontrado($"Workspace {espacoTrabalhoId} não encontrado.");
        var projetos = await _espacos.ObterProjetosPorEspacoTrabalhoAsync(espacoTrabalhoId, ct);
        var vinculos = await _espacos.ListarVinculosDoEspacoAsync(espacoTrabalhoId, ct);
        return new DetalhesEspacoTrabalho(espaco, projetos, vinculos);
    }

    public async Task<List<Projeto>> ListarProjetosDoEspacoAsync(Ator ator, Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        if (ator.Canal == CanalDeAcesso.AdminSistema)
        {
            if (await _espacos.ObterEspacoTrabalhoAsync(espacoTrabalhoId, ct) == null)
                throw new ExcecaoDeNaoEncontrado($"Workspace {espacoTrabalhoId} não encontrado.");
        }
        else
        {
            await ExigirVinculoAsync(espacoTrabalhoId, ator, PapelUsuario.Leitor, ct);
        }

        return await _espacos.ObterProjetosPorEspacoTrabalhoAsync(espacoTrabalhoId, ct);
    }

    public async Task<Projeto> CriarProjetoAsync(Ator ator, Guid espacoTrabalhoId, string nome, string slug, Guid? projetoId = null, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120)
            throw new ExcecaoDeValidacao("Nome do projeto deve ter entre 1 e 120 caracteres.");
        ValidadorDeInvariantes.ValidarSlug(slug, "slug do projeto");

        return await _espacos.CriarProjetoAsync(espacoTrabalhoId, nome.Trim(), slug.Trim().ToLowerInvariant(), projetoId, ct);
    }

    public async Task<VinculoMembro> ConfigurarVinculoAsync(Ator ator, Guid espacoTrabalhoId, string emissor, string sujeito, PapelUsuario papel, bool ativo = true, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (string.IsNullOrWhiteSpace(emissor) || !Uri.TryCreate(emissor, UriKind.Absolute, out var uriEmissor) || (uriEmissor.Scheme != "https" && uriEmissor.Scheme != "http"))
            throw new ExcecaoDeValidacao("Emissor do vínculo deve ser uma URL válida (HTTP/HTTPS).");
        if (string.IsNullOrWhiteSpace(sujeito) || sujeito.Length > 120)
            throw new ExcecaoDeValidacao("Sujeito do vínculo deve ter entre 1 e 120 caracteres.");

        return await _espacos.CriarOuAtualizarVinculoAsync(espacoTrabalhoId, emissor.Trim(), sujeito.Trim(), papel, ativo, ct);
    }

    public async Task<List<VinculoMembro>> ListarVinculosDoEspacoAsync(Ator ator, Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        return await _espacos.ListarVinculosDoEspacoAsync(espacoTrabalhoId, ct);
    }

    public async Task<bool> RemoverVinculoAsync(Ator ator, Guid espacoTrabalhoId, Guid vinculoId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        return await _espacos.RemoverVinculoAsync(espacoTrabalhoId, vinculoId, ct);
    }

    public async Task<bool> RemoverProjetoAsync(Ator ator, Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (await _espacos.ObterProjetoAsync(espacoTrabalhoId, projetoId, ct) == null)
            throw new ExcecaoDeNaoEncontrado($"Projeto {projetoId} não encontrado no workspace.");

        if (await _espacos.ProjetoPossuiHistoricoAsync(espacoTrabalhoId, projetoId, ct))
            throw new ExcecaoDeConflito("Este projeto já possui decisões, incidentes ou eventos imutáveis e não pode ser excluído.");

        return await _espacos.RemoverProjetoAsync(espacoTrabalhoId, projetoId, ct);
    }

    // Configuração inicial (bootstrap sem dados mock)
    public async Task<bool> ConfiguracaoInicialPendenteAsync(CancellationToken ct = default)
    {
        if (_usuarios == null)
            return false;
        return !await _espacos.PossuiAlgumEspacoAsync(ct) && !await _usuarios.PossuiAlgumUsuarioAsync(ct);
    }

    public async Task<ResultadoConfiguracaoInicial> ExecutarConfiguracaoInicialAsync(
        EntradaConfiguracaoInicial e,
        string emissorLocal,
        CancellationToken ct = default)
    {
        var repoUsuarios = _usuarios ?? throw new InvalidOperationException("Repositório de usuários locais não configurado.");

        if (await _espacos.PossuiAlgumEspacoAsync(ct) || await repoUsuarios.PossuiAlgumUsuarioAsync(ct))
            throw new ExcecaoDeConflito("A configuração inicial já foi concluída neste ambiente.");

        if (string.IsNullOrWhiteSpace(e.NomeWorkspace) || e.NomeWorkspace.Trim().Length > 120)
            throw new ExcecaoDeValidacao("Nome do workspace deve ter entre 1 e 120 caracteres.");
        ValidadorDeInvariantes.ValidarSlug(e.SlugWorkspace, "slug do workspace");

        if (string.IsNullOrWhiteSpace(e.NomeProjeto) || e.NomeProjeto.Trim().Length > 120)
            throw new ExcecaoDeValidacao("Nome do projeto deve ter entre 1 e 120 caracteres.");
        ValidadorDeInvariantes.ValidarSlug(e.SlugProjeto, "slug do projeto");

        var usuarioNorm = ValidarNomeUsuario(e.NomeUsuario);
        ValidarSenha(e.Senha);

        var espaco = await _espacos.CriarEspacoTrabalhoAsync(e.NomeWorkspace.Trim(), e.SlugWorkspace!.Trim().ToLowerInvariant(), null, ct);
        var projeto = await _espacos.CriarProjetoAsync(espaco.EspacoTrabalhoId, e.NomeProjeto.Trim(), e.SlugProjeto!.Trim().ToLowerInvariant(), null, ct);
        var usuario = await repoUsuarios.CriarUsuarioLocalAsync(new UsuarioLocal(
            Guid.NewGuid(),
            usuarioNorm,
            emissorLocal.Trim(),
            usuarioNorm,
            string.IsNullOrWhiteSpace(e.NomeExibicao) ? usuarioNorm : e.NomeExibicao.Trim(),
            SegurancaLocal.GerarHashSenha(e.Senha!),
            true,
            Relogio.AgoraUtc()), ct);

        var vinculo = await _espacos.CriarOuAtualizarVinculoAsync(
            espaco.EspacoTrabalhoId, usuario.Emissor, usuario.Sujeito, PapelUsuario.Mantenedor, true, ct);

        return new ResultadoConfiguracaoInicial(espaco, projeto, usuario, vinculo);
    }

    public async Task<UsuarioLocal> AutenticarUsuarioLocalAsync(string? nomeUsuario, string? senha, CancellationToken ct = default)
    {
        var repoUsuarios = _usuarios ?? throw new ExcecaoDeNaoAutorizado("Autenticação local indisponível.");
        if (string.IsNullOrWhiteSpace(nomeUsuario) || string.IsNullOrEmpty(senha))
            throw new ExcecaoDeNaoAutorizado("Usuário e senha são obrigatórios.");

        var usuario = await repoUsuarios.ObterPorNomeUsuarioAsync(nomeUsuario.Trim().ToLowerInvariant(), ct);
        if (usuario == null || !usuario.Ativo || !SegurancaLocal.VerificarSenha(senha, usuario.HashSenha))
            throw new ExcecaoDeNaoAutorizado("Usuário ou senha inválidos.");

        return usuario;
    }

    public async Task<List<UsuarioLocal>> ListarUsuariosDoEspacoAsync(Ator ator, Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (_usuarios == null) return [];
        return await _usuarios.ListarUsuariosDoEspacoAsync(espacoTrabalhoId, ct);
    }

    public async Task<UsuarioLocal> CriarUsuarioLocalNoEspacoAsync(
        Ator ator,
        EntradaCriarUsuarioLocal e,
        string emissorLocal,
        CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, e.EspacoTrabalhoId, ct);
        var repoUsuarios = _usuarios ?? throw new InvalidOperationException("Repositório de usuários locais não configurado.");

        var usuarioNorm = ValidarNomeUsuario(e.NomeUsuario);
        ValidarSenha(e.Senha);

        var existente = await repoUsuarios.ObterPorNomeUsuarioAsync(usuarioNorm, ct);
        if (existente != null)
            throw new ExcecaoDeConflito($"Usuário local '{usuarioNorm}' já existe.");

        var novo = await repoUsuarios.CriarUsuarioLocalAsync(new UsuarioLocal(
            Guid.NewGuid(),
            usuarioNorm,
            emissorLocal.Trim(),
            usuarioNorm,
            string.IsNullOrWhiteSpace(e.NomeExibicao) ? usuarioNorm : e.NomeExibicao.Trim(),
            SegurancaLocal.GerarHashSenha(e.Senha!),
            true,
            Relogio.AgoraUtc()), ct);

        await _espacos.CriarOuAtualizarVinculoAsync(e.EspacoTrabalhoId, novo.Emissor, novo.Sujeito, e.Papel, true, ct);
        return novo;
    }

    // Gestão de chaves de API para clientes MCP
    public async Task<ResultadoCriarChaveApi> CriarChaveApiAsync(
        Ator ator,
        EntradaCriarChaveApi e,
        string emissorPadrao,
        CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, e.EspacoTrabalhoId, ct);
        var repoChaves = _chavesApi ?? throw new InvalidOperationException("Repositório de chaves de API não configurado.");

        if (string.IsNullOrWhiteSpace(e.Nome) || e.Nome.Trim().Length > 100)
            throw new ExcecaoDeValidacao("Nome da chave deve ter entre 1 e 100 caracteres.");

        if (string.IsNullOrWhiteSpace(e.Sujeito) || e.Sujeito.Trim().Length > 120)
            throw new ExcecaoDeValidacao("Identificador do agente (subject) deve ter entre 1 e 120 caracteres.");

        var escopos = (e.Escopos ?? ["read:memory", "write:decision", "write:incident"])
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (escopos.Count == 0 || escopos.Any(s => !EscoposValidosApi.Contains(s)))
            throw new ExcecaoDeValidacao("Escopos válidos: read:memory, write:decision, write:incident.");

        if (e.DiasExpiracao.HasValue && (e.DiasExpiracao.Value < 1 || e.DiasExpiracao.Value > 365))
            throw new ExcecaoDeValidacao("Expiração da chave deve ficar entre 1 e 365 dias.");

        var sujeito = e.Sujeito.Trim();
        var emissor = emissorPadrao.Trim();
        var clienteId = string.IsNullOrWhiteSpace(e.ClienteId) ? null : e.ClienteId.Trim();
        if (clienteId != null && clienteId.Length > 80)
            throw new ExcecaoDeValidacao("ClienteId não pode exceder 80 caracteres.");

        // Garante vínculo ativo para que a nova chave tenha acesso imediato ao workspace
        var vinculoAtual = await _espacos.ObterVinculoMembroAsync(e.EspacoTrabalhoId, emissor, sujeito, ct);
        if (vinculoAtual == null || !vinculoAtual.Ativo || !vinculoAtual.Papel.Atende(e.Papel))
        {
            var papelFinal = vinculoAtual != null && vinculoAtual.Papel.Atende(e.Papel) ? vinculoAtual.Papel : e.Papel;
            await _espacos.CriarOuAtualizarVinculoAsync(e.EspacoTrabalhoId, emissor, sujeito, papelFinal, true, ct);
        }

        var (tokenPlano, prefixo, hashHex) = SegurancaLocal.GerarTokenChaveApi();
        var agora = Relogio.AgoraUtc();
        var expiraEm = e.DiasExpiracao.HasValue ? agora.AddDays(e.DiasExpiracao.Value) : (DateTimeOffset?)null;

        var chave = await repoChaves.CriarChaveApiAsync(new ChaveApiAgente(
            Guid.NewGuid(),
            e.EspacoTrabalhoId,
            e.Nome.Trim(),
            prefixo,
            hashHex,
            emissor,
            sujeito,
            clienteId,
            escopos,
            true,
            ator.Sujeito,
            agora,
            expiraEm,
            null), ct);

        return new ResultadoCriarChaveApi(chave, tokenPlano);
    }

    public async Task<List<ChaveApiAgente>> ListarChavesApiAsync(Ator ator, Guid espacoTrabalhoId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (_chavesApi == null) return [];
        return await _chavesApi.ListarChavesApiAsync(espacoTrabalhoId, ct);
    }

    public async Task<bool> RevogarChaveApiAsync(Ator ator, Guid espacoTrabalhoId, Guid chaveId, CancellationToken ct = default)
    {
        await ExigirAdminSistemaOuMantenedorAsync(ator, espacoTrabalhoId, ct);
        if (_chavesApi == null) return false;
        return await _chavesApi.RevogarChaveApiAsync(espacoTrabalhoId, chaveId, ct);
    }

    private static string ValidarNomeUsuario(string? nomeUsuario)
    {
        if (string.IsNullOrWhiteSpace(nomeUsuario))
            throw new ExcecaoDeValidacao("Nome de usuário é obrigatório.");
        var limpo = nomeUsuario.Trim().ToLowerInvariant();
        if (limpo.Length is < 3 or > 80 || limpo.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@')))
            throw new ExcecaoDeValidacao("Usuário deve ter 3-80 caracteres (letras, dígitos, ., _, - ou @).");
        return limpo;
    }

    private static void ValidarSenha(string? senha)
    {
        if (string.IsNullOrEmpty(senha) || senha.Length < 8 || senha.Length > 128)
            throw new ExcecaoDeValidacao("A senha deve ter entre 8 e 128 caracteres.");
    }
}
