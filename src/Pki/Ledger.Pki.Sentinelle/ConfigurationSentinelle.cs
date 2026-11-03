namespace Ledger.Pki.Sentinelle;

/// <summary>Emplacements de la sentinelle (section <c>Ledger:Sentinelle</c>, variables <c>Ledger__Sentinelle__*</c>).</summary>
public sealed class ConfigurationSentinelle
{
    /// <summary>ARN du secret du certificat du nœud.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Volume partagé avec le broker où sont écrits les fichiers PEM.</summary>
    public string Dossier { get; set; } = "/certificats";

    /// <summary>Serveurs d'amorçage Kafka (santé de la grappe), ex. <c>kafka-1.ledger-dev.interne:9093</c>.</summary>
    public string Serveurs { get; set; } = string.Empty;

    public int NombreNoeuds { get; set; } = 3;

    /// <summary>Nom du nœud (dimension des mesures), ex. <c>kafka-1</c>.</summary>
    public string Noeud { get; set; } = string.Empty;

    /// <summary>Volume de données du broker, monté en lecture seule (espace disque libre).</summary>
    public string DossierDonnees { get; set; } = "/donnees";

    /// <summary>Groupes de consommateurs dont le retard est mesuré.</summary>
    public IReadOnlyList<string> Groupes { get; set; } = ["ledger-projections"];

    /// <summary>Intervalle de publication des mesures.</summary>
    public TimeSpan IntervalleMesures { get; set; } = TimeSpan.FromMinutes(1);

    public void Valider()
    {
        if (string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(Serveurs) || string.IsNullOrWhiteSpace(Noeud) || NombreNoeuds < 1)
        {
            throw new InvalidOperationException("Ledger:Sentinelle : Secret, Serveurs, Noeud et NombreNoeuds sont obligatoires.");
        }
    }
}
