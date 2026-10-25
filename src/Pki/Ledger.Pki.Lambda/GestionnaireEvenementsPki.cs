using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Rotation;
using Ledger.SharedKernel;

namespace Ledger.Pki.Lambda;

/// <summary>Échec signalé à l'appelant : Secrets Manager marque alors la rotation en échec et la retente.</summary>
public sealed class EchecPkiException : Exception
{
    public EchecPkiException()
    {
    }

    public EchecPkiException(string message)
        : base(message)
    {
    }

    public EchecPkiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static EchecPkiException Depuis(DomainError erreur)
    {
        ArgumentNullException.ThrowIfNull(erreur);
        return new EchecPkiException($"{erreur.Code} : {erreur.Message}");
    }
}

/// <summary>Aiguille un événement vers la rotation, l'inventaire ou la demande de signature (transport seulement).</summary>
public sealed class GestionnaireEvenementsPki(IOperationsAutorite autorite, DemandeSignatureIntermediaire demandeSignature)
{
    private static readonly Dictionary<string, EtapeRotation> _etapes = new(StringComparer.Ordinal)
    {
        ["createSecret"] = EtapeRotation.CreerSecret,
        ["setSecret"] = EtapeRotation.DefinirSecret,
        ["testSecret"] = EtapeRotation.TesterSecret,
        ["finishSecret"] = EtapeRotation.TerminerSecret,
    };

    public async Task<ReponsePki> TraiterAsync(EvenementPki evenement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evenement);

        if (evenement is { SecretId: { } secret, ClientRequestToken: { } version, Step: { } step })
        {
            if (!_etapes.TryGetValue(step, out var etape))
            {
                throw new EchecPkiException($"Étape de rotation inconnue : {step}.");
            }

            var resultat = await autorite.TournerAsync(new DemandeRotation(secret, version, etape), cancellationToken).ConfigureAwait(false);
            return resultat.IsSuccess ? new ReponsePki { Message = resultat.Value } : throw EchecPkiException.Depuis(resultat.Error);
        }

        return evenement.Operation switch
        {
            "inventaire" => await InventorierAsync(cancellationToken).ConfigureAwait(false),
            "demande-signature-intermediaire" => await DemanderSignatureAsync(evenement, cancellationToken).ConfigureAwait(false),
            _ => throw new EchecPkiException($"Événement non reconnu (opération « {evenement.Operation} »)."),
        };
    }

    private async Task<ReponsePki> InventorierAsync(CancellationToken cancellationToken)
    {
        var mesures = await autorite.InventorierAsync(cancellationToken).ConfigureAwait(false);
        return new ReponsePki { Message = string.Join("; ", mesures.Select(m => $"{m.Identite}={m.JoursRestants:0.##} j")) };
    }

    private async Task<ReponsePki> DemanderSignatureAsync(EvenementPki evenement, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evenement.Cle) || string.IsNullOrWhiteSpace(evenement.Sujet))
        {
            throw new EchecPkiException("« cle » (ARN de la clé KMS) et « sujet » (nom distinctif) sont exigés.");
        }

        var resultat = await demandeSignature.ProduireAsync(evenement.Cle, evenement.Sujet, cancellationToken).ConfigureAwait(false);
        return resultat.IsSuccess
            ? new ReponsePki { Message = "Demande de signature produite : à signer hors ligne par la racine.", Csr = resultat.Value }
            : throw EchecPkiException.Depuis(resultat.Error);
    }
}
