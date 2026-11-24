using Sentinel.Core.Models;
using Sentinel.Core.Reducers;
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

        var payload = new MemoryPayload(
            proposalId, wsId, projId,
            "Idempotência em Pagamentos",
            "Resumo da decisão",
            "Justificativa técnica",
            ["BillingService"],
            [],
            [new Evidence("pull_request", "PR 1", "https://example.com/pr/1")],
            DateTimeOffset.UtcNow
        );

        var events = new List<MemoryEvent>
        {
            new(Guid.NewGuid(), wsId, projId, topicId, 1, "DecisionProposed", 1, DateTimeOffset.UtcNow.AddMinutes(-5), "author", "iss", null, proposalId, null),
            new(Guid.NewGuid(), wsId, projId, topicId, 2, "DecisionApproved", 1, DateTimeOffset.UtcNow, "reviewer", "iss", null, proposalId, null)
        };

        var stateAt1 = DecisionReducer.Reconstitute(topicId, wsId, projId, "pay-idem", events, id => payload, atVersion: 1);
        Assert.Equal(DecisionStatus.Proposed, stateAt1.Status);
        Assert.Equal(1, stateAt1.StreamVersion);
        Assert.Equal(proposalId, stateAt1.PendingProposalId);

        var stateAt2 = DecisionReducer.Reconstitute(topicId, wsId, projId, "pay-idem", events, id => payload, atVersion: 2);
        Assert.Equal(DecisionStatus.Active, stateAt2.Status);
        Assert.Equal(2, stateAt2.StreamVersion);
        Assert.Equal(proposalId, stateAt2.ActiveRecordId);
        Assert.Null(stateAt2.PendingProposalId);
    }

    [Fact]
    public void DecisionReducer_RejectProposal_ReconstitutesRejectedState()
    {
        var topicId = Guid.NewGuid();
        var wsId = Guid.NewGuid();
        var projId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        var payload = new MemoryPayload(
            proposalId, wsId, projId,
            "Proposta Descartada", "Resumo", null, [], [],
            [new Evidence("pull_request", "PR 1", "https://example.com/pr/1")],
            DateTimeOffset.UtcNow
        );

        var events = new List<MemoryEvent>
        {
            new(Guid.NewGuid(), wsId, projId, topicId, 1, "DecisionProposed", 1, DateTimeOffset.UtcNow.AddMinutes(-5), "author", "iss", null, proposalId, null),
            new(Guid.NewGuid(), wsId, projId, topicId, 2, "DecisionRejected", 1, DateTimeOffset.UtcNow, "reviewer", "iss", null, proposalId, null)
        };

        var state = DecisionReducer.Reconstitute(topicId, wsId, projId, "rejected-topic", events, id => payload);
        Assert.Equal(DecisionStatus.Rejected, state.Status);
        Assert.Null(state.PendingProposalId);
        Assert.Null(state.ActiveRecordId);
    }
}
