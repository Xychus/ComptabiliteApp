using System.Globalization;
using Ledger.Contracts.Http.V1;
using Ledger.Query.Application.Consultations;
using Ledger.Query.Application.Fraicheur;

namespace Ledger.Query.Api.Http;

/// <summary>Traduction des résultats de consultation en contrats HTTP V1 (montants en chaînes décimales).</summary>
public static class ConversionsReponses
{
    /// <summary>Montant en chaîne décimale invariante à deux décimales (ex. <c>"114.98"</c>, <c>"-5.00"</c>).</summary>
    public static string Montant(decimal montant) => montant.ToString("0.00", CultureInfo.InvariantCulture);

    public static FreshnessDto VersContrat(this EtatFraicheur fraicheur)
    {
        ArgumentNullException.ThrowIfNull(fraicheur);
        return new FreshnessDto(fraicheur.JusquaSequence, fraicheur.SequenceJournal, fraicheur.Perimee);
    }

    public static AccountBalanceResponse VersContrat(this SoldeCompte solde)
    {
        ArgumentNullException.ThrowIfNull(solde);
        return new AccountBalanceResponse(
            solde.Compte.Entite,
            solde.Compte.Compte,
            solde.Compte.Nom,
            solde.Compte.SensNormal,
            solde.AuPlusTard,
            [.. solde.Soldes.Select(s => new CurrencyBalanceDto(s.Devise, Montant(s.Debits), Montant(s.Credits), Montant(s.Solde)))],
            solde.Fraicheur.VersContrat());
    }

    public static TrialBalanceResponse VersContrat(this BalanceVerification balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        return new TrialBalanceResponse(
            balance.Entite,
            balance.AuPlusTard,
            [.. balance.Lignes.Select(l => new TrialBalanceLineDto(l.Compte, l.Nom, l.Devise, Montant(l.Debits), Montant(l.Credits), Montant(l.SoldeDebiteur), Montant(l.SoldeCrediteur)))],
            [.. balance.Totaux.Select(t => new TrialBalanceTotalDto(t.Devise, Montant(t.Debits), Montant(t.Credits), Montant(t.SoldeDebiteur), Montant(t.SoldeCrediteur), t.Equilibree))],
            balance.Equilibree,
            balance.Fraicheur.VersContrat());
    }

    public static AccountEntriesResponse VersContrat(this PageLignesCompte page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new AccountEntriesResponse(
            page.Entite,
            page.Compte,
            [.. page.Lignes.Select(l => new AccountEntryLineDto(l.Ecriture, l.NumeroLigne, l.DateAffaires, l.Periode, l.Sens, new MoneyDto(Montant(l.Montant), l.Devise), l.Sequence, l.Libelle))],
            page.CurseurSuivant,
            page.Fraicheur.VersContrat());
    }

    public static JournalEntryDetailResponse VersContrat(this EcritureConsultee consultee)
    {
        ArgumentNullException.ThrowIfNull(consultee);
        var e = consultee.Ecriture;
        return new JournalEntryDetailResponse(
            e.Ecriture,
            e.Entite,
            e.DateAffaires,
            e.Periode,
            e.Libelle,
            e.ContrePassationDe,
            e.ContrePasseePar,
            e.Sequence,
            e.EnregistreeLe,
            e.Acteur,
            [.. e.Lignes.Select(l => new JournalEntryLineDetailDto(l.NumeroLigne, l.Compte, l.Sens, new MoneyDto(Montant(l.Montant), l.Devise)))],
            consultee.Fraicheur.VersContrat());
    }

    public static PeriodDetailResponse VersContrat(this PeriodeConsultee consultee)
    {
        ArgumentNullException.ThrowIfNull(consultee);
        var p = consultee.Periode;
        var instantane = consultee.Instantane is { } i
            ? new PeriodSnapshotDto(i.SequenceSceau, i.NombreEvenements, [.. i.Mouvements.Select(m => new PeriodMovementDto(m.Compte, m.Devise, Montant(m.Debits), Montant(m.Credits)))])
            : null;
        return new PeriodDetailResponse(p.Entite, p.Periode, p.Statut, p.Version, p.OuverteLe, p.ScelleeLe, p.NombreEvenements, p.RacineMerkle, instantane, consultee.Fraicheur.VersContrat());
    }

    public static GstQstReturnResponse VersContrat(this DeclarationTpsTvq declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return new GstQstReturnResponse(
            declaration.Entite,
            declaration.Periode,
            declaration.StatutPeriode,
            [
                .. declaration.Montants.Select(m => new GstQstAmountsDto(
                    m.Devise,
                    Montant(m.TpsPercue),
                    Montant(m.TpsPayee),
                    Montant(m.TpsNette),
                    Montant(m.TvqPercue),
                    Montant(m.TvqPayee),
                    Montant(m.TvqNette))),
            ],
            declaration.Fraicheur.VersContrat());
    }
}
