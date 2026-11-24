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
        Assert.Equal(StatusDecisao.Proposed, stateAt1.Situacao);
        Assert.Equal(1, stateAt1.VersaoFluxo);
        Assert.Equal(proposalId, stateAt1.PropostaPendenteId);

        var stateAt2 = RedutorDeDecisao.Reconstituir(topicId, wsId, projId, "pay-idem", events, id => payload, naVersao: 2);
        Assert.Equal(StatusDecisao.Active, stateAt2.Situacao);
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
        Assert.Equal(StatusDecisao.Rejected, state.Situacao);
        Assert.Null(state.PropostaPendenteId);
        Assert.Null(state.RegistroAtivoId);
    }
}
