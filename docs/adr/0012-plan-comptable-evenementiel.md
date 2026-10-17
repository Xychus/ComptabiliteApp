# 0012 — Plan comptable tenu dans le journal

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 3

## Contexte

La phase 3 expose `PUT /v1/accounts/{accountId}`. Jusqu'ici le plan comptable était une table de
référence (`ledger.accounts`, migration 0004) en lecture seule pour `ledger_app`, alimentée hors
du service. L'écrire depuis l'API par mise à jour de cette table poserait trois problèmes :

- un changement de compte (désactivation, nom) n'aurait ni auteur, ni horodatage serveur, ni
  hash : aucune piste d'audit vérifiable, alors que le plan comptable conditionne la validité de
  toutes les écritures ;
- `ledger_app` devrait recevoir `UPDATE`, ce que tout le reste du schéma évite ;
- changer la devise, le sens normal ou `no_negative_balance` d'un compte déjà mouvementé
  invaliderait l'historique (point ouvert (4) du Journal, phase 2).

## Décision

1. Le plan comptable d'une entité est un agrégat dont le flux est `chart-{entité}` (composant
   échappé comme pour les autres flux, ADR 0004). Chaque définition ou modification d'un compte
   ajoute un événement `AccountDefined` portant **l'état complet** du compte (nom, devise, sens
   normal, actif, `no_negative_balance`).
2. Règles du décideur (`ChartDecider`) :
   - un compte inconnu est créé ;
   - la devise, le sens normal et `no_negative_balance` sont **immuables** une fois le compte
     défini (rejet `AccountAttributeImmutable`) ; seuls le nom et l'état actif changent ;
   - une définition identique à l'état courant ne produit aucun événement (idempotence du `PUT`) ;
   - le nom est obligatoire (1 à 200 caractères).
3. Les décisions d'écriture lisent le plan comptable **dans le journal** : `IChartOfAccountsReader`
   est implémenté dans l'Application en rejouant `chart-{entité}`. Les lecteurs de la table
   `ledger.accounts` (en mémoire et Npgsql) sont supprimés.
4. Plusieurs `PUT` concurrents sur la même entité se disputent la version suivante du flux ; un
   conflit relance lecture et décision (même politique que l'ADR 0003).
5. Les événements du plan comptable n'ont pas de date d'affaires. Leur période (colonne
   obligatoire, clé de partition) est celle de la date UTC de l'horloge serveur au moment de la
   décision. Ils font donc partie du sceau de cette période (ADR 0013).
6. La table `ledger.accounts` n'est plus lue ni écrite. Elle reste en place (migration fusionnée) ;
   la phase 4 décidera d'en faire une projection du flux ou de la supprimer par migration.

## Conséquences

- Toute modification du plan comptable est hachée, chaînée, attribuée (`actor`) et publiée par
  l'outbox comme n'importe quel événement : les projections de la phase 4 peuvent en tirer le plan
  comptable lisible.
- Charger le plan comptable coûte la lecture d'un flux par entité ; un plan de quelques centaines
  de comptes et de modifications reste de l'ordre de la milliseconde. Un instantané (snapshot)
  pourra être ajouté si un flux devient long.
- Une écriture passée en même temps qu'une désactivation de compte peut encore être acceptée :
  la décision d'écriture ne prend pas de version sur `chart-{entité}` (sinon toutes les écritures
  d'une entité se sérialiseraient sur ce flux). C'est le même compromis qu'avec la table de
  référence.
- Si la période courante d'une entité est déjà scellée, définir un compte est refusé
  (`PeriodSealed`, `409`) : cas d'exploitation anormal, le scellement suivant la clôture.

## Options écartées

- **Mise à jour de `ledger.accounts`** : pas de piste d'audit, privilège `UPDATE` pour
  `ledger_app`.
- **Un flux par compte** (`chart-{entité}-{compte}`) : charger le plan comptable demanderait de
  lire autant de flux que de comptes à chaque écriture.
- **Table `accounts` mise à jour dans la même transaction que l'événement** : ce serait une
  projection lue par une décision (invariant 4).
