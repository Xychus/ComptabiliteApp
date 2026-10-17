# 0008 — Règles comptables et fiscales du domaine (arrondis TPS/TVQ, périodes, soldes)

- **Statut** : proposé — **à valider avec le comptable / fiscaliste** (point P5 du plan)
- **Date** : 2026-10-16
- **Phase** : 1

## Contexte

Le plan comptable, la granularité des périodes, les périodes de déclaration TPS/TVQ et les règles
d'arrondi n'ont pas encore été fournis (point P5). La phase 1 doit pourtant implémenter un domaine
testé. Chaque règle ambiguë est donc posée ici comme une **hypothèse explicite**, isolée dans le
code pour pouvoir être changée sans refonte (paramètre ou classe de règle dédiée).

## Hypothèses

| # | Hypothèse | Où dans le code | Pour changer |
|---|---|---|---|
| H1 | Taxes arrondies **au niveau de la facture** : TPS et TVQ calculées sur le total taxable, puis arrondies au cent. | `QuebecSalesTaxCalculator`, `TaxRoundingPolicy.Default` | `TaxRoundingLevel.Line` (taxe arrondie ligne par ligne, puis additionnée) |
| H2 | Demi-cent arrondi **à l'unité supérieure en valeur absolue** (`MidpointRounding.AwayFromZero`). | `TaxRoundingPolicy.Default` | autre `MidpointRounding` |
| H3 | TPS 5 % et TVQ 9,975 % calculées **sur le même montant hors taxes** (pas de TVQ sur la TPS). | `QuebecSalesTaxCalculator` | taux en constantes |
| H4 | Périodes **mensuelles** (`AAAA-MM`), dérivées de la date d'affaires fournie par le client. | `PeriodId.FromBusinessDate` | nouvel ADR (impact sur le partitionnement de la phase 2) |
| H5 | Une écriture exige une période **explicitement ouverte** : période jamais ouverte → `PeriodNotOpen`, scellée → `PeriodSealed`. | `JournalEntryDecider` | règle de période |
| H6 | Chaque compte a **une seule devise** ; une écriture a une seule devise (CAD par défaut). | `AccountCurrencyRule`, `SingleCurrencyRule` | retirer ou remplacer la règle |
| H7 | Le solde d'un compte contraint (`no_negative_balance`) est mesuré **dans son sens normal** : actif = débits − crédits, passif = crédits − débits. Il ne peut pas devenir négatif. | `ConstrainedAccountDecider` | — |
| H8 | Une écriture se **contre-passe une seule fois**, dans la période de la date de contre-passation (pas forcément celle d'origine). Une contre-passation n'est pas elle-même contre-passable : on passe une nouvelle écriture corrigée. | `JournalEntryDecider.Reverse` | — |
| H9 | Montants des lignes strictement positifs, au plus au cent (pas de fraction de cent saisie). | `StrictlyPositiveAmountsRule`, `MinorUnitPrecisionRule` | — |

### Exemple de référence (H1 à H3)

Vente de 100,00 $ taxable :

| Compte | Débit | Crédit |
|---|---:|---:|
| Clients | 114,98 | |
| Ventes | | 100,00 |
| TPS à percevoir | | 5,00 |
| TVQ à percevoir | | 9,98 (9,975 arrondi) |

Les deux niveaux d'arrondi divergent sur les factures à nombreuses petites lignes. Exemple : dix
lignes de 0,05 $.

| Niveau | TPS | TVQ |
|---|---:|---:|
| Facture (H1) : taxe sur 0,50 $ | 0,03 (0,025) | 0,05 (0,049875) |
| Ligne : taxe sur chaque 0,05 $, puis somme | 0,00 (10 × 0,0025 → 0,00) | 0,00 (10 × 0,0049875 → 0,00) |

**C'est le point principal à valider.**

## Décision

Le domaine implémente les hypothèses H1 à H9. Les valeurs fiscales et l'arrondi sont centralisés
dans `TaxRoundingPolicy` et `QuebecSalesTaxCalculator` ; les autres règles sont chacune une classe
`IEntryRule` ou une méthode de décision isolée et testée.

## Conséquences

- Toute réponse du comptable qui contredit une hypothèse se traduit par un changement local
  (paramètre ou classe de règle) et une mise à jour de cet ADR, sans toucher aux autres règles.
- Les contrats d'événements (`schema_version = 1`) ne dépendent pas de ces choix : seuls les
  montants calculés changeraient, pas la forme des événements.
- H4 conditionne le partitionnement de la table `events` (phase 2) : à valider avant la phase 2.
