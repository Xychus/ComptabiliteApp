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

    public void Valider()
    {
        if (string.IsNullOrWhiteSpace(Secret) || string.IsNullOrWhiteSpace(Serveurs) || NombreNoeuds < 1)
        {
            throw new InvalidOperationException("Ledger:Sentinelle : Secret, Serveurs et NombreNoeuds sont obligatoires.");
        }
    }
}
