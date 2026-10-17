using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Tests.Support;

/// <summary>
/// Harnais Given / When / Then : événements passés → commande → événements attendus ou rejet.
/// </summary>
internal sealed class Scenario<TState>(TState initial, Func<TState, IDomainEvent, TState> evolve)
{
    private TState _state = initial;

    public Scenario<TState> Given(params IDomainEvent[] history)
    {
        _state = history.Aggregate(_state, evolve);
        return this;
    }

    public Outcome When(Func<TState, Result<IReadOnlyList<IDomainEvent>>> decide) => new(decide(_state));

    internal sealed class Outcome(Result<IReadOnlyList<IDomainEvent>> result)
    {
        public void Then(params IDomainEvent[] expected)
        {
            Assert.True(result.IsSuccess, result.IsFailure ? $"Rejet inattendu : {result.Error.Code} — {result.Error.Message}" : null);
            Assert.Equal(expected, result.Value);
        }

        public DomainError ThenRejected(string expectedCode)
        {
            Assert.True(result.IsFailure, "La commande aurait dû être rejetée.");
            Assert.Equal(expectedCode, result.Error.Code);
            return result.Error;
        }
    }
}
