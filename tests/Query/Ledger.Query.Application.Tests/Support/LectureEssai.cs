using Ledger.Query.Application;
using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Infrastructure;
using Ledger.Query.Infrastructure.Memoire;
using Ledger.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Query.Tests.Support;

/// <summary>Horloge réglable : la fraîcheur se teste sans attendre.</summary>
public sealed class HorlogeEssai(DateTimeOffset maintenant) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = maintenant;
}

/// <summary>Consultations de l'Application sur les lecteurs en mémoire, avec une horloge réglable.</summary>
public sealed class LectureEssai : IDisposable
{
    public const string Entite = "ACME";

    private readonly ServiceProvider _services;

    public LectureEssai(TimeSpan? retardMaximal = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(Horloge);
        services.AjouterConsultations(new OptionsFraicheur { RetardMaximal = retardMaximal ?? TimeSpan.FromSeconds(30) });
        services.AjouterLectureEnMemoire(Donnees);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public DonneesLectureEnMemoire Donnees { get; } = new();

    public HorlogeEssai Horloge { get; } = new(JeuEssai.Origine.AddDays(1));

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LectureEssai Amorcer(JeuEssai jeu)
    {
        ArgumentNullException.ThrowIfNull(jeu);
        Donnees.Modifier(jeu.AppliquerA);
        return this;
    }

    public T Consultation<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public void Dispose() => _services.Dispose();
}
