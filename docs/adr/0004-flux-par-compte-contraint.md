# 0004 — Flux par compte uniquement pour les comptes contraints, avec `balance_after`

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 2

## Contexte

Certains comptes ne doivent jamais passer sous zéro dans leur sens normal (`no_negative_balance`,
ex. encaisse, dépôts de clients — ADR 0008, H7). La vérification ne peut pas lire une projection
(invariant 4) : elle doit se faire à partir du journal, avec concurrence optimiste.

Donner un flux à chaque compte sérialiserait les comptes « chauds » : la TPS à percevoir ou les
ventes sont touchées par presque chaque écriture, et toutes ces écritures se disputeraient la
version suivante du même flux.

## Décision

1. Seuls les comptes marqués `no_negative_balance` dans `ledger.accounts` ont un flux
   `account-{entité}-{compte}`. Les autres comptes n'ont pas de flux : leur solde est une
   projection (phase 4).
2. Pour chaque ligne d'une écriture qui touche un compte contraint, l'Application ajoute un
   `AccountLinePosted` à la version suivante de ce flux, avec `balance_after` (solde après la
   ligne, dans le sens normal). Plusieurs lignes sur le même compte donnent plusieurs versions
   consécutives.
3. L'invariant se vérifie sur le `balance_after` du dernier événement du flux (lecture du flux,
   jamais d'une projection). Deux écritures concurrentes sur le même compte se disputent la même
   version : la perdante relit le flux, redécide et peut être rejetée par `InsufficientBalance`.
4. `JournalEntryPosted` et tous les `AccountLinePosted` d'une écriture sont écrits dans **une seule
   transaction** (avec leurs lignes d'outbox) : soit tout est écrit, soit rien.
5. `balance_after` est aussi stocké dans la colonne `events.balance_after` (`NUMERIC(20,2)`), ce
   qui permet à l'outil d'intégrité de vérifier la suite des soldes en SQL. La date d'affaires et
   la période d'un `AccountLinePosted` sont celles de l'écriture source (colonnes du journal).
6. Les composants du nom de flux sont échappés (`%` → `%25`, `-` → `%2D`) pour que deux couples
   (entité, compte) distincts ne donnent jamais le même `stream_id`.

## Conséquences

- Les comptes non contraints n'imposent aucune sérialisation.
- Les écritures sur un même compte contraint sont sérialisées par la concurrence optimiste :
  un compte contraint très sollicité verra des tentatives répétées (bornées, ADR 0003).
- Changer l'attribut `no_negative_balance` d'un compte qui a déjà des mouvements impose de
  reconstituer son solde initial : à traiter avec la gestion du plan comptable (phase 3).
- Le test de concurrence de la phase 2 (deux retraits de 60 $ sur 100 $) prouve qu'un seul
  retrait est accepté.

## Options écartées

- **Flux par compte pour tous les comptes** : sérialise les comptes chauds.
- **Verrou pessimiste (`SELECT … FOR UPDATE`) sur une table de soldes** : la décision lirait un
  modèle dérivé et le rôle applicatif aurait besoin de `UPDATE`.
- **Contrainte `CHECK` en base sur une projection de soldes** : la projection est asynchrone
  (CDC) et ne peut pas porter l'invariant.
