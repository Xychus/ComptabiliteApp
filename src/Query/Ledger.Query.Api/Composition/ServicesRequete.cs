using Ledger.Query.Api.Observabilite;
using Ledger.Query.Application;
using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Infrastructure;
using Ledger.Query.Infrastructure.Memoire;
using Ledger.Query.Infrastructure.Postgres;
using Ledger.SharedKernel;

namespace Ledger.Query.Api.Composition;

/// <summary>Consultations de l'Application et implémentation des ports de lecture.</summary>
public static class ServicesRequete
{
    /// <summary>Clé de configuration du fournisseur de lecture : <c>Postgres</c> (défaut) ou <c>EnMemoire</c>.</summary>
    public const string CleFournisseur = "Ledger:Lecture:Fournisseur";

    /// <summary>Chaîne de connexion du rôle <c>ledger_query</c>, injectée depuis Secrets Manager.</summary>
    public const string NomChaineConnexion = "Ledger";

    public const string FournisseurEnMemoire = "EnMemoire";

    public static IServiceCollection AjouterConsultationsRequete(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environnement)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environnement);
        services.AddSingleton<IClock, SystemClock>();
        services.AjouterConsultations(LireOptionsFraicheur(configuration));

        var sondes = services.AddHealthChecks();
        if (string.Equals(configuration[CleFournisseur], FournisseurEnMemoire, StringComparison.Ordinal))
        {
            if (!environnement.IsDevelopment())
            {
                throw new InvalidOperationException($"La lecture en mémoire ({CleFournisseur}={FournisseurEnMemoire}) est réservée au développement.");
            }

            services.AjouterLectureEnMemoire(new DonneesLectureEnMemoire());
            return services;
        }

        // Lue à la résolution, pas à l'enregistrement : la génération du contrat OpenAPI au build
        // compose l'application sans chaîne de connexion. Son absence fait échouer le démarrage.
        services.AjouterLecturePostgres(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString(NomChaineConnexion)
            ?? throw new InvalidOperationException($"Chaîne de connexion « {NomChaineConnexion} » absente."));
        if (!GenerationDocument.EnCours)
        {
            services.AddHostedService<ControleChaineConnexion>();
        }

        sondes.AddCheck<VerificationSantePostgres>("postgres", tags: [SondesSante.EtiquetteDisponible]);
        return services;
    }

    /// <summary>Seuil de fraîcheur : strictement positif, sinon le démarrage échoue.</summary>
    public static OptionsFraicheur LireOptionsFraicheur(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(OptionsFraicheur.Section).Get<OptionsFraicheur>() ?? new OptionsFraicheur();
        return options.RetardMaximal > TimeSpan.Zero
            ? options
            : throw new InvalidOperationException($"{OptionsFraicheur.Section}:{nameof(OptionsFraicheur.RetardMaximal)} doit être strictement positif.");
    }

    /// <summary>Fait échouer le démarrage, plutôt que la première requête, si la chaîne de connexion manque.</summary>
    private sealed class ControleChaineConnexion(IConfiguration configuration) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            string.IsNullOrWhiteSpace(configuration.GetConnectionString(NomChaineConnexion))
                ? throw new InvalidOperationException($"Chaîne de connexion « {NomChaineConnexion} » absente.")
                : Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
