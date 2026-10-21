using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;

namespace Ledger.Query.Application.Consultations;

/// <summary>Montants de TPS et de TVQ d'une période dans une devise ; taxe nette = perçue − payée (ADR 0018).</summary>
public sealed record MontantsTpsTvq(string Devise, decimal TpsPercue, decimal TpsPayee, decimal TvqPercue, decimal TvqPayee)
{
    public decimal TpsNette => TpsPercue - TpsPayee;

    public decimal TvqNette => TvqPercue - TvqPayee;
}

/// <summary>
/// Base de la déclaration TPS/TVQ d'une période. <see cref="StatutPeriode"/> est nul si la période
/// n'a jamais été ouverte ; une déclaration se dépose normalement sur une période scellée.
/// </summary>
public sealed record DeclarationTpsTvq(string Entite, string Periode, string? StatutPeriode, IReadOnlyList<MontantsTpsTvq> Montants, EtatFraicheur Fraicheur);

/// <summary>Déclaration TPS/TVQ d'une période (projection <c>tax_projection</c>, format à valider avec le comptable, ADR 0018).</summary>
public sealed class ConsulterDeclarationTpsTvq(ILecteurTaxes taxes, ILecteurPeriodes periodes, EvaluateurFraicheur fraicheur)
{
    public async Task<DeclarationTpsTvq> ExecuterAsync(string entite, string periode, CancellationToken cancellationToken)
    {
        var etat = await fraicheur.EvaluerAsync([NomsProjections.Taxes, NomsProjections.Periodes], cancellationToken).ConfigureAwait(false);
        var statut = (await periodes.TrouverPeriodeAsync(entite, periode, cancellationToken).ConfigureAwait(false))?.Statut;
        var montants = (await taxes.LireTaxesAsync(entite, periode, cancellationToken).ConfigureAwait(false))
            .Select(t => new MontantsTpsTvq(t.Devise, t.TpsPercue, t.TpsPayee, t.TvqPercue, t.TvqPayee))
            .ToList();
        return new DeclarationTpsTvq(entite, periode, statut, montants, etat);
    }
}
