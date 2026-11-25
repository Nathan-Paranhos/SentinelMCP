using Sentinel.Core.Comandos;
using Sentinel.Core.Consultas;
using Sentinel.Core.Modelos;

namespace Sentinel.Core.Contratos;

// Interfaces de persistência do domínio
public interface IRepositorioEspacoTrabalho
{
    Task<EspacoTrabalho?> ObterEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default);

    // Lista workspaces com vínculo ativo da identidade
    Task<List<EspacoTrabalho>> ObterEspacosDaIdentidadeAsync(string emissor, string sujeito, CancellationToken ct = default);

    Task<VinculoMembro?> ObterVinculoMembroAsync(Guid espacoTrabalhoId, string emissor, string sujeito, CancellationToken ct = default);
    Task<Projeto?> ObterProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default);
    Task<List<Projeto>> ObterProjetosPorEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default);

    // Administração de workspaces, projetos e vínculos
    Task<bool> PossuiAlgumEspacoAsync(CancellationToken ct = default);
    Task<EspacoTrabalho> CriarEspacoTrabalhoAsync(string nome, string slug, Guid? espacoTrabalhoId = null, CancellationToken ct = default);
    Task<EspacoTrabalho?> AtualizarEspacoTrabalhoAsync(Guid espacoTrabalhoId, string nome, CancellationToken ct = default);
    Task<List<EspacoTrabalho>> ListarTodosEspacosAsync(CancellationToken ct = default);
    Task<Projeto> CriarProjetoAsync(Guid espacoTrabalhoId, string nome, string slug, Guid? projetoId = null, CancellationToken ct = default);
    Task<bool> ProjetoPossuiHistoricoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default);
    Task<bool> RemoverProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default);
    Task<VinculoMembro> CriarOuAtualizarVinculoAsync(Guid espacoTrabalhoId, string emissor, string sujeito, PapelUsuario papel, bool ativo = true, CancellationToken ct = default);
    Task<List<VinculoMembro>> ListarVinculosDoEspacoAsync(Guid espacoTrabalhoId, CancellationToken ct = default);
    Task<bool> RemoverVinculoAsync(Guid espacoTrabalhoId, Guid vinculoId, CancellationToken ct = default);
}

public interface IRepositorioDecisao
{
    Task<TopicoDecisao?> ObterTopicoPorChaveAsync(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico, CancellationToken ct = default);
    Task<TopicoDecisao?> ObterTopicoPorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default);
    Task<DecisaoAtual?> ObterDecisaoAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default);
    Task<List<PropostaPendente>> ObterPropostasPendentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default);
    Task<List<DecisaoAtual>> ObterDecisoesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default);
    Task<List<DecisaoAtual>> ObterDecisoesVigentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default);
    Task<List<EventoMemoria>> ObterEventosRecentesAsync(Guid espacoTrabalhoId, int limite = 50, CancellationToken ct = default);
    Task<ResultadoProporDecisao> ProporDecisaoAsync(ComandoProporDecisao comando, CancellationToken ct = default);
    Task<ResultadoAprovarDecisao> AprovarDecisaoAsync(ComandoAprovarDecisao comando, CancellationToken ct = default);
    Task<ResultadoRejeitarDecisao> RejeitarDecisaoAsync(ComandoRejeitarDecisao comando, CancellationToken ct = default);
    Task<ResultadoRestaurarDecisao> RestaurarDecisaoAsync(ComandoRestaurarDecisao comando, CancellationToken ct = default);
}

public interface IRepositorioIncidente
{
    Task<FluxoIncidente?> ObterIncidentePorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default);
    Task<IncidenteAtual?> ObterIncidenteAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid incidenteId, CancellationToken ct = default);
    Task<List<IncidenteAtual>> ObterIncidentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default);
    Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(ComandoRegistrarIncidente comando, CancellationToken ct = default);
}

public interface IRepositorioHistorico
{
    // Retorna histórico de eventos e payloads do fluxo
    Task<HistoricoDoFluxo> ObterHistoricoDoFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, CancellationToken ct = default);
}

public interface IRepositorioContexto
{
    Task<ResultadoConsultaContexto> ConsultarContextoAsync(ConsultaContexto consulta, CancellationToken ct = default);
    Task<ResultadoComprovanteContexto?> ObterComprovanteContextoAsync(Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, CancellationToken ct = default);
}

public interface IReconstrutorProjecoes
{
    Task ReconstruirProjecoesAsync(Guid? espacoTrabalhoId = null, CancellationToken ct = default);
}

public interface IManutencaoRetencao
{
    // Expurga recibos e comprovantes expirados
    Task<int> ExpurgarRecibosAsync(DateTimeOffset criadosAntesDe, CancellationToken ct = default);
}

public interface IVerificadorProntidao
{
    // Valida prontidão da persistência
    Task VerificarAsync(CancellationToken ct = default);
}

public interface IRepositorioUsuariosLocais
{
    Task<bool> PossuiAlgumUsuarioAsync(CancellationToken ct = default);
    Task<UsuarioLocal?> ObterPorNomeUsuarioAsync(string nomeUsuario, CancellationToken ct = default);
    Task<List<UsuarioLocal>> ListarUsuariosDoEspacoAsync(Guid espacoTrabalhoId, CancellationToken ct = default);
    Task<UsuarioLocal> CriarUsuarioLocalAsync(UsuarioLocal usuario, CancellationToken ct = default);
}

public interface IRepositorioChavesApi
{
    Task<ChaveApiAgente> CriarChaveApiAsync(ChaveApiAgente chave, CancellationToken ct = default);
    Task<List<ChaveApiAgente>> ListarChavesApiAsync(Guid espacoTrabalhoId, CancellationToken ct = default);
    Task<ChaveApiAgente?> ObterChaveApiPorHashAsync(string hashSha256Hex, CancellationToken ct = default);
    Task RegistrarUsoChaveApiAsync(Guid espacoTrabalhoId, Guid chaveId, DateTimeOffset usadoEm, CancellationToken ct = default);
    Task<bool> RevogarChaveApiAsync(Guid espacoTrabalhoId, Guid chaveId, CancellationToken ct = default);
}
