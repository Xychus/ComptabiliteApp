namespace Ledger.SharedKernel;

/// <summary>Source du temps, injectée : jamais <c>DateTime.Now</c> dans le domaine.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Horloge système, adossée à <see cref="TimeProvider"/>.</summary>
public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
