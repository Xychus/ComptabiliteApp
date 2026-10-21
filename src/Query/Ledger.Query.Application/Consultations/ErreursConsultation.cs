using Ledger.SharedKernel;

namespace Ledger.Query.Application.Consultations;

/// <summary>
/// Rejets des consultations. Les codes font partie du contrat HTTP (membre <c>code</c> des
/// Problem Details) : ils restent en anglais, comme ceux de l'API de commandes.
/// </summary>
public static class ErreursConsultation
{
    public const string CompteIntrouvableCode = "AccountNotFound";
    public const string EcritureIntrouvableCode = "JournalEntryNotFound";
    public const string PeriodeIntrouvableCode = "PeriodNotFound";
    public const string CurseurInvalideCode = "InvalidCursor";

    public static DomainError CompteIntrouvable(string entite, string compte) =>
        DomainError.WithDetails(CompteIntrouvableCode, $"Le compte {compte} n'existe pas dans le plan comptable de l'entité {entite}.", ("entityId", entite), ("accountId", compte));

    public static DomainError EcritureIntrouvable(Guid ecriture) =>
        DomainError.WithDetails(EcritureIntrouvableCode, $"L'écriture {ecriture} est introuvable.", ("entryId", ecriture.ToString()));

    public static DomainError PeriodeIntrouvable(string entite, string periode) =>
        DomainError.WithDetails(PeriodeIntrouvableCode, $"La période {periode} n'a jamais été ouverte pour l'entité {entite}.", ("entityId", entite), ("period", periode));

    public static DomainError CurseurInvalide() =>
        new(CurseurInvalideCode, "Curseur de pagination illisible : reprendre la valeur nextCursor d'une réponse précédente.");
}
