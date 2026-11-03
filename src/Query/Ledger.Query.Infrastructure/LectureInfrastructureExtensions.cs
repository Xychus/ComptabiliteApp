using Ledger.Postgres.Infrastructure;
using Ledger.Query.Application.Lecture;
using Ledger.Query.Infrastructure.Memoire;
using Ledger.Query.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Query.Infrastructure;

/// <summary>Implémentations des ports de lecture. Toutes sont sans état : une instance par processus.</summary>
public static class LectureInfrastructureExtensions
{
    /// <summary>
    /// Lecture PostgreSQL. La chaîne de connexion désigne le rôle <c>ledger_query</c>
    /// (<c>SSL Mode=VerifyFull</c> en AWS, authentification IAM sans mot de passe, ADR 0029) ;
    /// elle n'est lue qu'à la première résolution de la source de données.
    /// </summary>
    public static IServiceCollection AjouterLecturePostgres(this IServiceCollection services, Func<IServiceProvider, string> chaineConnexion)
    {
        ArgumentNullException.ThrowIfNull(chaineConnexion);
        services.AddSingleton(sp => SourceDonneesPostgres.Creer(sp, chaineConnexion(sp)));
        services.AddSingleton<ILecteurPlanComptable, NpgsqlLecteurPlanComptable>();
        services.AddSingleton<ILecteurSoldes, NpgsqlLecteurSoldes>();
        services.AddSingleton<ILecteurMouvements, NpgsqlLecteurMouvements>();
        services.AddSingleton<ILecteurLignesCompte, NpgsqlLecteurLignesCompte>();
        services.AddSingleton<ILecteurEcritures, NpgsqlLecteurEcritures>();
        services.AddSingleton<ILecteurPeriodes, NpgsqlLecteurPeriodes>();
        services.AddSingleton<ILecteurInstantanes, NpgsqlLecteurInstantanes>();
        services.AddSingleton<ILecteurTaxes, NpgsqlLecteurTaxes>();
        services.AddSingleton<ILecteurReperesFraicheur, NpgsqlLecteurReperesFraicheur>();
        return services;
    }

    /// <summary>Lecture en mémoire, alimentée directement : développement et tests uniquement.</summary>
    public static IServiceCollection AjouterLectureEnMemoire(this IServiceCollection services, DonneesLectureEnMemoire donnees)
    {
        ArgumentNullException.ThrowIfNull(donnees);
        services.AddSingleton(donnees);
        services.AddSingleton<ILecteurPlanComptable, LecteurPlanComptableEnMemoire>();
        services.AddSingleton<ILecteurSoldes, LecteurSoldesEnMemoire>();
        services.AddSingleton<ILecteurMouvements, LecteurMouvementsEnMemoire>();
        services.AddSingleton<ILecteurLignesCompte, LecteurLignesCompteEnMemoire>();
        services.AddSingleton<ILecteurEcritures, LecteurEcrituresEnMemoire>();
        services.AddSingleton<ILecteurPeriodes, LecteurPeriodesEnMemoire>();
        services.AddSingleton<ILecteurInstantanes, LecteurInstantanesEnMemoire>();
        services.AddSingleton<ILecteurTaxes, LecteurTaxesEnMemoire>();
        services.AddSingleton<ILecteurReperesFraicheur, LecteurReperesFraicheurEnMemoire>();
        return services;
    }
}
