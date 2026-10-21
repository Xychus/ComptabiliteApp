# 0017 — Instantanés de fin de période, figés au scellement

- **Statut** : proposé
- **Date** : 2026-10-20
- **Phase** : 4

## Contexte

Le plan prévoit des instantanés de fin de période (`period_snapshots`) figés au scellement. Deux
façons de les calculer au moment où le worker traite `PeriodSealed` :

- recopier l'état courant des projections (soldes) : le résultat dépendrait du retard du worker
  et de l'ordre de traitement. Avec une séquence attribuée à l'insertion, une écriture de la
  période peut avoir une séquence **supérieure** à celle du sceau : le trigger attribue la
  séquence avant de prendre le verrou, si bien qu'une écriture qui obtient son verrou partagé
  avant le verrou exclusif du sceau, mais sa séquence juste après celle du sceau, est validée et
  scellée (ADR 0005 et 0013). Un rejeu dans l'ordre de la séquence ne la verrait pas encore ;
- un solde de clôture cumulé (toutes les périodes ≤ P) n'est pas figé non plus : une période
  antérieure peut rester ouverte et recevoir des écritures après le scellement de P.

## Décision

1. L'instantané contient les **mouvements de la période scellée**, par compte et devise
   (débits, crédits), la séquence du sceau et son nombre d'événements.
2. Il est **recalculé depuis `ledger.events`** (écritures et contre-passations de l'entité dans
   la période), pas depuis une autre projection. Le sceau garantit que la période est complète
   à sa validation et qu'elle ne changera plus (invariant 8). Le résultat ne dépend ni du retard
   ni de l'ordre de traitement : l'incrémental et le rejeu donnent le même instantané.
3. Une période scellée sans écriture n'a pas de ligne d'instantané (le statut et l'empreinte du
   sceau sont dans la projection `periods`).
4. Les soldes d'ouverture et de clôture se déduisent des instantanés des périodes antérieures,
   lorsqu'elles sont toutes scellées (API de requête, phase 4b).

## Conséquences

- La projection lit le journal avec le rôle `ledger_projection` (`SELECT`), par l'index
  `(entity_id, period, event_type)` : une période à la fois.
- La somme des débits égale celle des crédits dans chaque instantané (partie double), vérifiée
  par les tests.
- Un instantané ne couvre que les mouvements, pas les soldes cumulés : une balance de clôture
  exige que toutes les périodes antérieures soient scellées, ce qui est la règle comptable.

## Options écartées

- **Copie des soldes courants au traitement du sceau** : non déterministe (voir Contexte).
- **Solde cumulé à la clôture** : pas figé tant qu'une période antérieure est ouverte.
