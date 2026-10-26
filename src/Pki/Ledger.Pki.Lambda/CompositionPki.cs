using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.MotsDePasse;
using Ledger.Pki.Application.Rotation;
using Ledger.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Pki.Lambda;

/// <summary>
/// Configuration de la Lambda, lue dans ses variables d'environnement (chiffrées par la CMK
/// <c>lambda</c>). Sans les trois valeurs, la Lambda est en mode cérémonie : seule la demande de
/// signature d'une intermédiaire est possible.
/// </summary>
/// <param name="CertificatIntermediaire">PEM de l'intermédiaire active (public, versionné dans Git).</param>
/// <param name="CertificatRacine">PEM de la racine (public, versionné dans Git).</param>
/// <param name="CleIntermediaire">ARN de la clé KMS de l'intermédiaire active.</param>
public sealed record OptionsPki(string? CertificatIntermediaire, string? CertificatRacine, string? CleIntermediaire)
{
    public bool AutoriteInstallee => CertificatIntermediaire is not null && CertificatRacine is not null && CleIntermediaire is not null;

    public const string VariableIntermediaire = "Ledger__Pki__CertificatIntermediaire";
    public const string VariableRacine = "Ledger__Pki__CertificatRacine";
    public const string VariableCle = "Ledger__Pki__CleIntermediaire";
    public const string VariableHotePostgres = "Ledger__Pki__Postgres__Hote";
    public const string VariableBasePostgres = "Ledger__Pki__Postgres__Base";
    public const string VariableSecretMaitre = "Ledger__Pki__Postgres__SecretMaitre";
    public const string VariableCertificatRacineRds = "Ledger__Pki__Postgres__CertificatRacine";

    /// <summary>Aurora (rotation des mots de passe PostgreSQL) ; null si la Lambda n'en renouvelle aucun.</summary>
    public PostgresPki? Postgres { get; init; }

    public static OptionsPki DepuisEnvironnement(Func<string, string?> lire)
    {
        ArgumentNullException.ThrowIfNull(lire);
        var options = new OptionsPki(Lire(lire, VariableIntermediaire), Lire(lire, VariableRacine), Lire(lire, VariableCle))
        {
            Postgres = Lire(lire, VariableHotePostgres) is { } hote
                ? new PostgresPki(
                    hote,
                    Lire(lire, VariableBasePostgres) ?? throw new InvalidOperationException($"Variable manquante : {VariableBasePostgres}."),
                    Lire(lire, VariableSecretMaitre) ?? throw new InvalidOperationException($"Variable manquante : {VariableSecretMaitre}."),
                    Lire(lire, VariableCertificatRacineRds) ?? "/app/rds-ca-global.pem")
                : null,
        };
        var presentes = new[] { options.CertificatIntermediaire, options.CertificatRacine, options.CleIntermediaire }.Count(v => v is not null);
        return presentes is 0 or 3
            ? options
            : throw new InvalidOperationException(
                $"Configuration partielle : {VariableIntermediaire}, {VariableRacine} et {VariableCle} vont ensemble (toutes ou aucune).");
    }

    private static string? Lire(Func<string, string?> lire, string variable) => lire(variable) is { Length: > 0 } valeur ? valeur : null;
}

/// <summary>Aurora pour la rotation des mots de passe : hôte rédacteur, base, secret maître géré par RDS, certificat racine RDS.</summary>
public sealed record PostgresPki(string Hote, string Base, string SecretMaitre, string CertificatRacine);

/// <summary>Composition des services applicatifs de la PKI ; les adaptateurs (AWS ou mémoire) sont enregistrés par l'appelant.</summary>
public static class CompositionPki
{
    public static IServiceCollection AjouterPki(this IServiceCollection services, OptionsPki options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<DemandeSignatureIntermediaire>();
        services.AddSingleton<GestionnaireEvenementsPki>();
        if (options.Postgres is not null)
        {
            // Mots de passe PostgreSQL : indépendants de l'intermédiaire (mode cérémonie compris).
            services.AddSingleton<IEtapeRotation, CreationMotDePasse>();
            services.AddSingleton<IEtapeRotation, DefinitionMotDePasse>();
            services.AddSingleton<IEtapeRotation, TestMotDePasse>();
            services.AddSingleton<IEtapeRotation>(sp => new FinSecret(
                sp.GetRequiredService<IMagasinCertificats>(), sp.GetServices<IActionApresRotation>(), TypesSecret.MotDePassePostgres));
        }

        services.AddSingleton<GestionnaireRotation>();
        if (!options.AutoriteInstallee)
        {
            services.AddSingleton<IOperationsAutorite, AutoriteNonInstallee>();
            return services;
        }

        var autorite = AutoriteIntermediaire.Charger(options.CertificatIntermediaire!, options.CertificatRacine!, options.CleIntermediaire!);
        if (autorite.IsFailure)
        {
            throw EchecPkiException.Depuis(autorite.Error);
        }

        services.AddSingleton(autorite.Value);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new OptionsEmission());
        services.AddSingleton<EmetteurCertificats>();
        services.AddSingleton<VerificateurCertificat>();
        services.AddSingleton<InventaireEcheances>();
        services.AddSingleton<IEtapeRotation, CreationSecret>();
        services.AddSingleton<IEtapeRotation, DefinitionSecret>();
        services.AddSingleton<IEtapeRotation, TestSecret>();
        services.AddSingleton<IEtapeRotation, FinSecret>();
        services.AddSingleton<IOperationsAutorite, OperationsAutorite>();
        return services;
    }
}
