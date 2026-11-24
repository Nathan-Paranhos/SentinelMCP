using Sentinel.Core.Modelos;
using Sentinel.Core.Redutores;
using Xunit;

namespace Sentinel.UnitTests;

public class ReducersTests
{
    [Fact]
    public void DecisionReducer_ProposeThenApprove_ReconstitutesActiveState()
    {
        var topicId = Guid.NewGuid();
        var wsId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        var payload = new CargaMemoria(
            proposalId, wsId, projId,
            "Idempotência em Pagamentos",
            "Resumo da decisão",
            "Justificativa técnica",
            ["BillingService"],
            [],
            [new Evidencia("pull_request", "PR 1", "https://example.com/pr/1")],
            DateTimeOffset.UtcNow
        );

        var events = new List<EventoMemoria>
        {
            new(Guid.NewGuid(), wsId, projId, topicId, 1, "DecisionProposed", 1, DateTimeOffset.UtcNow.AddMinutes(-5), "author", "iss", null, proposalId, null),
            new(Guid.NewGuid(), wsId, projId, topicId, 2, "DecisionApproved", 1, DateTimeOffset.UtcNow, "reviewer", "iss", null, proposalId, null)
        };

        var stateAt1 = RedutorDeDecisao.Reconstituir(topicId, wsId, projId, "pay-idem", events, id => payload, naVersao: 1);
        Assert.Equal(StatusDecisao.Proposta, stateAt1.Situacao);
        Assert.Equal(1, stateAt1.VersaoFluxo);
        Assert.Equal(proposalId, stateAt1.PropostaPendenteId);

        var stateAt2 = RedutorDeDecisao.Reconstituir(topicId, wsId, projId, "pay-idem", events, id => payload, naVersao: 2);
        Assert.Equal(StatusDecisao.Ativa, stateAt2.Situacao);
        Assert.Equal(2, stateAt2.VersaoFluxo);
        Assert.Equal(proposalId, stateAt2.RegistroAtivoId);
        Assert.Null(stateAt2.PropostaPendenteId);
    }

    [Fact]
    public void DecisionReducer_RejectProposal_ReconstitutesRejectedState()
    {
        var topicId = Guid.NewGuid();
        var wsId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        var payload = new CargaMemoria(
            proposalId, wsId, projId,
            "Proposta Descartada", "Resumo", null, [], [],
            [new Evidencia("pull_request", "PR 1", "https://example.com/pr/1")],
            DateTimeOffset.UtcNow
        );

        var events = new List<EventoMemoria>
        {
            new(Guid.NewGuid(), wsId, projId, topicId, 1, "DecisionProposed", 1, DateTimeOffset.UtcNow.AddMinutes(-5), "author", "iss", null, proposalId, null),
            new(Guid.NewGuid(), wsId, projId, topicId, 2, "DecisionRejected", 1, DateTimeOffset.UtcNow, "reviewer", "iss", null, proposalId, null)
        };

        var state = RedutorDeDecisao.Reconstituir(topicId, wsId, projId, "rejected-topic", events, id => payload);
        Assert.Equal(StatusDecisao.Rejeitada, state.Situacao);
        Assert.Null(state.PropostaPendenteId);
        Assert.Null(state.RegistroAtivoId);
    }

    private static CargaMemoria Carga(Guid id, Guid ws, Guid proj, string titulo) => new(
        id, ws, proj, titulo, "Resumo " + titulo, null, ["Key-" + titulo], [],
        [new Evidencia("pull_request", "PR", "https://example.com/pr/1")], DateTimeOffset.UtcNow);

    [Fact]
    public void DecisionReducer_RecordVersionCountsApprovals_NotStreamVersion()
    {
        var (topic, ws, proj) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (p1, p2) = (Guid.NewGuid(), Guid.NewGuid());
        var cargas = new Dictionary<Guid, CargaMemoria> { [p1] = Carga(p1, ws, proj, "V1"), [p2] = Carga(p2, ws, proj, "V2") };
        var agora = DateTimeOffset.UtcNow;
        var events = new List<EventoMemoria>
        {
            new(Guid.NewGuid(), ws, proj, topic, 1, "DecisionProposed", 1, agora, "a", "i", null, p1, null),
            new(Guid.NewGuid(), ws, proj, topic, 2, "DecisionApproved", 1, agora, "m", "i", null, p1, null),
            new(Guid.NewGuid(), ws, proj, topic, 3, "DecisionProposed", 1, agora, "a", "i", null, p2, null),
            new(Guid.NewGuid(), ws, proj, topic, 4, "DecisionApproved", 1, agora, "m", "i", null, p2, null)
        };

        var estado = RedutorDeDecisao.Reconstituir(topic, ws, proj, "t", events, id => cargas[id]);

        Assert.Equal(StatusDecisao.Ativa, estado.Situacao);
        Assert.Equal(p2, estado.RegistroAtivoId);
        Assert.Equal(2, estado.VersaoRegistroAtivo);
        Assert.Equal(4, estado.VersaoFluxo);
        Assert.Equal("V2", estado.Titulo);
    }

    [Fact]
    public void DecisionReducer_PendingOverActive_ExposesProposedContentSeparately()
    {
        var (topic, ws, proj) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (p1, p2) = (Guid.NewGuid(), Guid.NewGuid());
        var cargas = new Dictionary<Guid, CargaMemoria> { [p1] = Carga(p1, ws, proj, "Vigente"), [p2] = Carga(p2, ws, proj, "Nova") };
        var agora = DateTimeOffset.UtcNow;
        var events = new List<EventoMemoria>
        {
            new(Guid.NewGuid(), ws, proj, topic, 1, "DecisionProposed", 1, agora, "a", "i", null, p1, null),
            new(Guid.NewGuid(), ws, proj, topic, 2, "DecisionApproved", 1, agora, "m", "i", null, null, null),
            new(Guid.NewGuid(), ws, proj, topic, 3, "DecisionProposed", 1, agora, "agent-2", "i", "cli", p2, null)
        };

        var estado = RedutorDeDecisao.Reconstituir(topic, ws, proj, "t", events, id => cargas[id]);
        var (atual, pendente) = RedutorDeDecisao.Projetar(estado, agora);

        Assert.Equal(StatusDecisao.Ativa, atual.Situacao);
        Assert.Equal("Vigente", atual.Titulo);
        Assert.NotNull(pendente);
        Assert.Equal("Nova", pendente!.Titulo);
        Assert.Equal(p2, pendente.PropostaId);
        Assert.Equal("agent-2", pendente.AutorSujeito);
        Assert.Equal(3, pendente.VersaoFluxo);
    }

    [Fact]
    public void IncidentReducer_UsesOccurredAtFromPayload()
    {
        var (inc, ws, proj, carga) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var ocorrido = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var payload = Carga(carga, ws, proj, "Inc") with { OcorridoEm = ocorrido };
        var events = new List<EventoMemoria>
        {
            new(Guid.NewGuid(), ws, proj, inc, 1, "IncidentReported", 1, DateTimeOffset.UtcNow, "a", "i", null, carga, null)
        };

        var estado = RedutorDeIncidente.Reconstituir(inc, ws, proj, events, _ => payload);

        Assert.Equal(ocorrido, estado.OcorridoEm);
        Assert.Equal(StatusIncidente.Reportado, estado.Situacao);
        Assert.Equal(1, estado.VersaoRegistro);
    }

    [Theory]
    [InlineData(StatusDecisao.Proposta, "proposed")]
    [InlineData(StatusDecisao.Ativa, "active")]
    [InlineData(StatusDecisao.Substituida, "superseded")]
    [InlineData(StatusDecisao.Rejeitada, "rejected")]
    public void StatusDecisao_RoundTripsThroughWireFormat(StatusDecisao status, string texto)
    {
        Assert.Equal(texto, status.ParaTexto());
        Assert.Equal(status, ConversorDeEnumeradores.ParaStatusDecisao(texto));
    }
}
