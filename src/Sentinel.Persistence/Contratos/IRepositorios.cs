using Sentinel.Core.Comandos;
using Sentinel.Core.Consultas;
using Sentinel.Core.Modelos;

namespace Sentinel.Persistence.Contratos;

public interface IRepositorioEspacoTrabalho
{
    Task<EspacoTrabalho?> ObterEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default);
    Task<VinculoMembro?> ObterVinculoMembroAsync(Guid espacoTrabalhoId, string emissor, string sujeito, CancellationToken ct = default);
    Task<Projeto?> ObterProjetoAsync(Guid espacoTrabalhoId, Guid projetoId, CancellationToken ct = default);
    Task<List<Projeto>> ObterProjetosPorEspacoTrabalhoAsync(Guid espacoTrabalhoId, CancellationToken ct = default);
}

public interface IRepositorioDecisao
{
    Task<TopicoDecisao?> ObterTopicoPorChaveAsync(Guid espacoTrabalhoId, Guid projetoId, string chaveTopico, CancellationToken ct = default);
    Task<TopicoDecisao?> ObterTopicoPorIdAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default);
    Task<DecisaoAtual?> ObterDecisaoAtualAsync(Guid espacoTrabalhoId, Guid projetoId, Guid topicoId, CancellationToken ct = default);
    Task<List<DecisaoAtual>> ObterPropostasPendentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default);
    Task<List<DecisaoAtual>> ObterDecisoesVigentesAsync(Guid espacoTrabalhoId, Guid? projetoId = null, CancellationToken ct = default);
    Task<List<EventoMemoria>> ObterEventosPorFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, int? naVersao = null, int? antesDaVersao = null, int limite = 50, CancellationToken ct = default);
    Task<CargaMemoria?> ObterCargaAsync(Guid espacoTrabalhoId, Guid cargaId, CancellationToken ct = default);
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
    Task<List<EventoMemoria>> ObterEventosPorFluxoAsync(Guid espacoTrabalhoId, Guid fluxoId, int? naVersao = null, int? antesDaVersao = null, int limite = 50, CancellationToken ct = default);
    Task<CargaMemoria?> ObterCargaAsync(Guid espacoTrabalhoId, Guid cargaId, CancellationToken ct = default);
    Task<ResultadoRegistrarIncidente> RegistrarIncidenteAsync(ComandoRegistrarIncidente comando, CancellationToken ct = default);
}

public interface IRepositorioContexto
{
    Task<ResultadoConsultaContexto> ConsultarContextoAsync(ConsultaContexto consulta, CancellationToken ct = default);
    Task<ResultadoComprovanteContexto> ObterComprovanteContextoAsync(Guid espacoTrabalhoId, Guid projetoId, Guid consultaId, string principalId, CancellationToken ct = default);
}

public interface IRepositorioRecibo
{
    Task<ReciboComando?> ObterReciboComandoAsync(Guid espacoTrabalhoId, string principalId, string operacao, string chaveIdempotencia, CancellationToken ct = default);
    Task SalvarReciboComandoAsync(ReciboComando recibo, CancellationToken ct = default);
}

public interface IReconstrutorProjecoes
{
    Task ReconstruirProjecoesAsync(Guid? espacoTrabalhoId = null, CancellationToken ct = default);
}
