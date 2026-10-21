using Ledger.Query.Application.Lecture;
using Ledger.Query.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Query.Infrastructure.Tests.Contrat;

/// <summary>
/// Une implémentation des ports de lecture, vue par la suite de contrat : amorçage des tables de
/// projection, du journal et des positions, puis lecture par les ports.
/// </summary>
public abstract class MagasinLectureEssai : IAsyncDisposable
{
    private ServiceProvider? _services;

    /// <summary>Entités propres à chaque test (la base PostgreSQL est partagée par la collection).</summary>
    public string Entite { get; } = "ACME-" + Guid.CreateVersion7().ToString("N");

    public string AutreEntite { get; } = "AUTRE-" + Guid.CreateVersion7().ToString("N");

    public ILecteurPlanComptable PlanComptable => Services.GetRequiredService<ILecteurPlanComptable>();

    public ILecteurSoldes Soldes => Services.GetRequiredService<ILecteurSoldes>();

    public ILecteurMouvements Mouvements => Services.GetRequiredService<ILecteurMouvements>();

    public ILecteurLignesCompte LignesCompte => Services.GetRequiredService<ILecteurLignesCompte>();

    public ILecteurEcritures Ecritures => Services.GetRequiredService<ILecteurEcritures>();

    public ILecteurPeriodes Periodes => Services.GetRequiredService<ILecteurPeriodes>();

    public ILecteurInstantanes Instantanes => Services.GetRequiredService<ILecteurInstantanes>();

    public ILecteurTaxes Taxes => Services.GetRequiredService<ILecteurTaxes>();

    public ILecteurReperesFraicheur Reperes => Services.GetRequiredService<ILecteurReperesFraicheur>();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Services => _services ??= Construire();

    public abstract Task AmorcerAsync(JeuEssai jeu);

    /// <summary>Ajoute un événement au journal ; renvoie sa séquence (attribuée par le serveur en PostgreSQL).</summary>
    public abstract Task<long> EnregistrerEvenementAsync(DateTimeOffset enregistreLe);

    public abstract Task PositionnerAsync(string projection, long sequence);

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    protected abstract void AjouterLecture(IServiceCollection services);

    private ServiceProvider Construire()
    {
        var services = new ServiceCollection();
        AjouterLecture(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
