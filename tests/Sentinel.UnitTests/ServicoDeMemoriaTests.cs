using Sentinel.Core.CasosDeUso;
using Sentinel.Core.Excecoes;
using Sentinel.Core.Modelos;
using Sentinel.Persistence.EmMemoria;
using Xunit;

namespace Sentinel.UnitTests;

// Testes da camada de casos de uso
public class ServicoDeMemoriaTests
{
    private static readonly Guid Ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WsOutro = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Proj = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Emissor = "https://auth.sentinel.dev";

    private static readonly Ator Agente = new(Emissor, "agent-dev-1", "claude-code", CanalDeAcesso.Mcp);
    private static readonly Ator MantenedorWeb = new(Emissor, "maintainer-alice", "web-session", CanalDeAcesso.Web);
    private static readonly Ator MantenedorViaMcp = new(Emissor, "maintainer-alice", "claude-code", CanalDeAcesso.Mcp);
    private static readonly Ator Leitor = new(Emissor, "reader-bob", "cursor", CanalDeAcesso.Mcp);
    private static readonly Ator LeitorWeb = new(Emissor, "reader-bob", "web-session", CanalDeAcesso.Web);
    private static readonly Ator AdminSistema = new("system", "admin-api-key", "admin-api", CanalDeAcesso.AdminSistema);

    private static ServicoDeMemoria NovoServico(bool popularDemo = true)
    {
        var repo = new RepositorioSentinelEmMemoria();
        if (popularDemo)
            repo.PopularDadosDemonstracao();
        return new ServicoDeMemoria(repo, repo, repo, repo, repo, repo, repo);
    }

    private static EntradaProporDecisao Proposta(string chave, int versao = 0, string url = "https://example.com/pr/1") => new(
        Ws, Proj, chave, versao, "Título", "Resumo", null, ["Key"], null,
        [new Evidencia("pull_request", "PR", url)], Guid.NewGuid().ToString());

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.com/pr/1")]
    [InlineData("https://user:pass@example.com/x")]
    [InlineData("https://example.com/x?token=abc")]
    public async Task ProporDecisao_RejeitaEvidenciaInsegura(string url)
    {
        var servico = NovoServico();
        var ex = await Assert.ThrowsAsync<ExcecaoDeValidacao>(() => servico.ProporDecisaoAsync(Agente, Proposta("xss-topic", url: url)));
        Assert.Equal("VALIDATION_ERROR", ex.Codigo);
    }

    [Fact]
    public async Task ProporDecisao_ChaveDoTopicoInvalida_EhRejeitada()
    {
        var servico = NovoServico();
        await Assert.ThrowsAsync<ExcecaoDeValidacao>(() => servico.ProporDecisaoAsync(Agente, Proposta("chave com espaço")));
    }

    [Fact]
    public async Task ProporDecisao_ChaveNormalizadaParaMinusculas()
    {
        var servico = NovoServico();
        var r1 = await servico.ProporDecisaoAsync(Agente, Proposta("Case-Topic"));
        var ex = await Assert.ThrowsAsync<ExcecaoDeConflitoDeVersao>(() => servico.ProporDecisaoAsync(Agente, Proposta("case-topic")));
        Assert.Equal(r1.TopicoId, ex.TopicoId);
    }

    [Fact]
    public async Task ProporDecisao_LeitorNaoPodePropor()
    {
        var servico = NovoServico();
        await Assert.ThrowsAsync<ExcecaoDeAcessoNegado>(() => servico.ProporDecisaoAsync(Leitor, Proposta("reader-topic")));
    }

    [Fact]
    public async Task SemVinculo_RespondeNotFoundParaNaoRevelarWorkspace()
    {
        var servico = NovoServico();
        var ex = await Assert.ThrowsAsync<ExcecaoDeNaoEncontrado>(() => servico.ConsultarContextoAsync(Agente,
            new EntradaConsultarContexto(WsOutro, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "audit", "decision", 10)));
        Assert.Equal("NOT_FOUND", ex.Codigo);
    }

    [Fact]
    public async Task Aprovar_ExigeCanalWeb_MesmoParaMaintainer()
    {
        var servico = NovoServico();
        var r = await servico.ProporDecisaoAsync(Agente, Proposta("canal-topic"));
        var entrada = new EntradaRevisao(Ws, Proj, r.TopicoId, r.PropostaId, r.VersaoFluxo, "k1");

        await Assert.ThrowsAsync<ExcecaoDeAcessoNegado>(() => servico.AprovarDecisaoAsync(MantenedorViaMcp, entrada));
        var aprovado = await servico.AprovarDecisaoAsync(MantenedorWeb, entrada);
        Assert.Equal("active", aprovado.Situacao);
    }

    [Fact]
    public async Task Aprovar_ColaboradorViaWebNaoTemPapel()
    {
        var servico = NovoServico();
        var r = await servico.ProporDecisaoAsync(Agente, Proposta("papel-topic"));
        var agenteWeb = Agente with { Canal = CanalDeAcesso.Web };
        await Assert.ThrowsAsync<ExcecaoDeAcessoNegado>(() => servico.AprovarDecisaoAsync(agenteWeb,
            new EntradaRevisao(Ws, Proj, r.TopicoId, r.PropostaId, r.VersaoFluxo, "k2")));
    }

    [Fact]
    public async Task NovaProposta_ComOutraPendente_RetornaProposalPending()
    {
        var servico = NovoServico();
        var r = await servico.ProporDecisaoAsync(Agente, Proposta("pendente-topic"));
        var ex = await Assert.ThrowsAsync<ExcecaoDePropostaPendente>(() => servico.ProporDecisaoAsync(Agente, Proposta("pendente-topic", r.VersaoFluxo)));
        Assert.Equal("PROPOSAL_PENDING", ex.Codigo);
        Assert.Equal(r.PropostaId, ex.PropostaPendenteId);
    }

    [Fact]
    public async Task Rejeitar_ExigeMotivo_EPreservaDecisaoVigente()
    {
        var servico = NovoServico();
        var r = await servico.ProporDecisaoAsync(Agente, Proposta("motivo-topic"));
        await Assert.ThrowsAsync<ExcecaoDeValidacao>(() => servico.RejeitarDecisaoAsync(MantenedorWeb,
            new EntradaRevisao(Ws, Proj, r.TopicoId, r.PropostaId, r.VersaoFluxo, "k3", Motivo: " ")));

        var rejeitado = await servico.RejeitarDecisaoAsync(MantenedorWeb,
            new EntradaRevisao(Ws, Proj, r.TopicoId, r.PropostaId, r.VersaoFluxo, "k4", Motivo: "Falta evidência de carga."));
        Assert.Equal("rejected", rejeitado.Situacao);
    }

    [Fact]
    public async Task Comprovante_SoAutorOuMaintainerPodeLer()
    {
        var servico = NovoServico();
        var consulta = await servico.ConsultarContextoAsync(Agente, new EntradaConsultarContexto(Ws, Proj, "BillingService", "decision", 5));

        var proprio = await servico.ObterComprovanteAsync(Agente, Ws, Proj, consulta.ConsultaId);
        Assert.True(proprio.IntegridadeVerificada);
        Assert.Equal(64, proprio.Digest.Length);

        await Assert.ThrowsAsync<ExcecaoDeNaoEncontrado>(() => servico.ObterComprovanteAsync(Leitor, Ws, Proj, consulta.ConsultaId));
        var auditoria = await servico.ObterComprovanteAsync(MantenedorViaMcp, Ws, Proj, consulta.ConsultaId);
        Assert.Equal(consulta.ConsultaId, auditoria.ConsultaId);
    }

    [Fact]
    public async Task LinhaDoTempo_PaginaDoMaisRecenteParaOMaisAntigo()
    {
        var servico = NovoServico();
        var r = await servico.ProporDecisaoAsync(Agente, Proposta("pagina-topic"));
        await servico.RejeitarDecisaoAsync(MantenedorWeb, new EntradaRevisao(Ws, Proj, r.TopicoId, r.PropostaId, 1, "p1", Motivo: "x"));
        var r2 = await servico.ProporDecisaoAsync(Agente, Proposta("pagina-topic", 2));
        await servico.AprovarDecisaoAsync(MantenedorWeb, new EntradaRevisao(Ws, Proj, r.TopicoId, r2.PropostaId, 3, "p2"));

        var pagina1 = await servico.ObterLinhaDoTempoAsync(Agente, new Sentinel.Core.Consultas.ConsultaLinhaDoTempo(Ws, Proj, "decision", r.TopicoId, Limite: 2));
        Assert.Equal([4, 3], pagina1.Eventos.Select(e => e.VersaoFluxo));
        Assert.Equal(3, pagina1.ProximaVersaoAnterior);
        Assert.Equal(StatusDecisao.Ativa, pagina1.EstadoDecisao!.Situacao);

        var pagina2 = await servico.ObterLinhaDoTempoAsync(Agente, new Sentinel.Core.Consultas.ConsultaLinhaDoTempo(Ws, Proj, "decision", r.TopicoId, AntesDaVersao: 3, Limite: 2));
        Assert.Equal([2, 1], pagina2.Eventos.Select(e => e.VersaoFluxo));
        Assert.Null(pagina2.ProximaVersaoAnterior);
        // O estado reconstituído independe da página
        Assert.Equal(StatusDecisao.Ativa, pagina2.EstadoDecisao!.Situacao);
    }

    [Fact]
    public async Task DescobertaDeEspacos_RetornaSomenteEspacosEProjetosAcessiveis()
    {
        var servico = NovoServico();
        var espacosAgente = await servico.ObterEspacosEProjetosDaIdentidadeAsync(Agente);
        Assert.Single(espacosAgente);
        Assert.Equal(Ws, espacosAgente[0].EspacoTrabalho.EspacoTrabalhoId);
        Assert.Equal(PapelUsuario.Colaborador, espacosAgente[0].Papel);
        Assert.Contains(espacosAgente[0].Projetos, p => p.ProjetoId == Proj);

        var fora = new Ator(Emissor, "nao-cadastrado", "codex", CanalDeAcesso.Mcp);
        Assert.Empty(await servico.ObterEspacosEProjetosDaIdentidadeAsync(fora));
    }

    [Fact]
    public async Task Administracao_RespeitaCanalAdminSistemaEEscopoDoMantenedor()
    {
        var servico = NovoServico();

        // Criar workspace global exige canal AdminSistema
        await Assert.ThrowsAsync<ExcecaoDeAcessoNegado>(() =>
            servico.CriarEspacoTrabalhoAsync(MantenedorWeb, "WS Web", "ws-web"));

        var criado = await servico.CriarEspacoTrabalhoAsync(AdminSistema, "WS Novo", "ws-novo");
        Assert.Equal("ws-novo", criado.IdentificadorSlug);

        // AdminSistema enxerga todos os workspaces; usuário web vê só os seus
        Assert.True((await servico.ListarTodosEspacosAsync(AdminSistema)).Count >= 3);
        Assert.Single(await servico.ListarTodosEspacosAsync(LeitorWeb));

        // Leitor web não pode administrar projetos nem vínculos; mantenedor via MCP também é bloqueado
        await Assert.ThrowsAsync<ExcecaoDeAcessoNegado>(() =>
            servico.CriarProjetoAsync(LeitorWeb, Ws, "Proj Bloqueado", "proj-bloqueado"));
        await Assert.ThrowsAsync<ExcecaoDeAcessoNegado>(() =>
            servico.ConfigurarVinculoAsync(MantenedorViaMcp, Ws, Emissor, "codex-agent", PapelUsuario.Colaborador));

        // Mantenedor não acessa detalhes de outro workspace
        await Assert.ThrowsAsync<ExcecaoDeNaoEncontrado>(() =>
            servico.ObterDetalhesEspacoAsync(MantenedorWeb, WsOutro));

        // Mantenedor no próprio workspace cria projeto e vínculo
        var projeto = await servico.CriarProjetoAsync(MantenedorWeb, Ws, "Proj Web", "proj-web");
        var vinculo = await servico.ConfigurarVinculoAsync(MantenedorWeb, Ws, Emissor, "codex-agent", PapelUsuario.Colaborador);
        Assert.Equal(Ws, projeto.EspacoTrabalhoId);
        Assert.Equal("codex-agent", vinculo.Sujeito);
    }

    [Fact]
    public async Task ConfiguracaoInicial_EAutenticacaoLocalPbkdf2_SemDadosMock()
    {
        var servico = NovoServico(popularDemo: false);
        Assert.True(await servico.ConfiguracaoInicialPendenteAsync());

        var cfg = await servico.ExecutarConfiguracaoInicialAsync(new EntradaConfiguracaoInicial(
            "Minha Org", "minha-org", "Core API", "core-api", "nathan.eng", "Nathan", "SenhaForte#123"), Emissor);

        Assert.False(await servico.ConfiguracaoInicialPendenteAsync());
        Assert.Equal(PapelUsuario.Mantenedor, cfg.Vinculo.Papel);

        await Assert.ThrowsAsync<ExcecaoDeConflito>(() => servico.ExecutarConfiguracaoInicialAsync(new EntradaConfiguracaoInicial(
            "Outra", "outra", "P2", "p2", "outro", null, "SenhaForte#123"), Emissor));

        var autenticado = await servico.AutenticarUsuarioLocalAsync("nathan.eng", "SenhaForte#123");
        Assert.Equal("nathan.eng", autenticado.Sujeito);

        await Assert.ThrowsAsync<ExcecaoDeNaoAutorizado>(() =>
            servico.AutenticarUsuarioLocalAsync("nathan.eng", "SenhaErrada"));
    }

    [Fact]
    public async Task ChavesApi_ERemoverProjeto_ValidamEscoposEHistoricoImutavel()
    {
        var servico = NovoServico();

        var chaveRes = await servico.CriarChaveApiAsync(MantenedorWeb, new EntradaCriarChaveApi(
            Ws, "Codex Local", "codex-runner", "codex", PapelUsuario.Colaborador, ["read:memory", "write:decision"], 30), Emissor);
        Assert.StartsWith("snt_", chaveRes.TokenPlano);
        Assert.Single((await servico.ListarChavesApiAsync(MantenedorWeb, Ws)));
        Assert.True(await servico.RevogarChaveApiAsync(MantenedorWeb, Ws, chaveRes.Chave.ChaveId));
        Assert.Empty(await servico.ListarChavesApiAsync(MantenedorWeb, Ws));

        // Projeto com histórico não pode ser removido; projeto vazio pode
        await Assert.ThrowsAsync<ExcecaoDeConflito>(() => servico.RemoverProjetoAsync(MantenedorWeb, Ws, Proj));

        var vazio = await servico.CriarProjetoAsync(MantenedorWeb, Ws, "Temporario", "temporario-1");
        Assert.True(await servico.RemoverProjetoAsync(MantenedorWeb, Ws, vazio.ProjetoId));
    }
}
