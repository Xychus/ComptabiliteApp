# 0014 — Contrat HTTP de l'API de commandes

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 3

## Contexte

L'API de commandes devient l'API publique du composant `command` au sens SemVer. Elle doit
traduire HTTP en commandes de l'Application sans logique métier, rester idempotente lorsque le
client rejoue une requête et décrire ses erreurs de façon exploitable.

## Décision

1. **Ressources** (préfixe `/v1`, indépendant de la version SemVer) :
   - `PUT /v1/journal-entries/{entryId}` : passation idempotente ; `201` si l'écriture est créée,
     `200` si une requête identique a déjà été traitée ;
   - `POST /v1/journal-entries/{entryId}/reversals` : contre-passation ; `reversalEntryId` (UUIDv7
     choisi par le client) dans le corps sert de clé d'idempotence (`201` / `200`) ;
   - `POST /v1/periods/{period}/open` et `.../seal` : `expectedVersion` (version du flux de la
     période) obligatoire, `409 WrongExpectedVersion` si elle diffère ; `200` ;
   - `PUT /v1/accounts/{accountId}` : définition d'un compte (ADR 0012) ; `201` à la création,
     `200` sinon.
2. **Entité** : l'identifiant d'entité est dans le corps de chaque requête (`entityId`), pas dans
   le chemin. Le chemin garde l'identité choisie par le client (`entryId`, `accountId`, période).
3. **Champs d'autorité serveur** : les contrats de requête ne les déclarent pas ; un
   `sequenceNumber`, `recordedAt`, `period` ou `hash` reçu est ignoré (membres JSON inconnus
   ignorés). Les réponses les renvoient (`sequenceNumber` = `global_sequence`).
4. **Erreurs en Problem Details (RFC 9457)**, `type` = `urn:ledger:problem:{code}`, avec les
   membres d'extension `code` (code de l'erreur de domaine), `details` (ex. `lineIndex`,
   `account`) et `traceId`. Correspondance code → statut tenue dans une table de l'API (ajout d'un
   code = ajout d'une ligne) :
   - `400` : requête illisible ou invalide (JSON, UUID non v7, montant, devise, sens) ; membre
     `errors` par champ ;
   - `401` : jeton absent ou invalide ; `403` : jeton valide sans le scope `ledger:write` ;
   - `404` : `EntryNotFound` ;
   - `409` : conflits de concurrence ou d'identifiant (`ConcurrencyConflict`,
     `WrongExpectedVersion`, `EntryIdAlreadyUsed`, `EntryIdConflict`, `EntryAlreadyReversed`) et
     état de période (`PeriodSealed`, `PeriodNotOpen`, `PeriodAlreadyOpen`, `PeriodAlreadySealed`) ;
   - `422` : tout autre rejet métier (partie double, solde insuffisant, attribut immuable...).
5. **Authentification en défense en profondeur** (l'autorizer de l'API Gateway reste le premier
   contrôle, phase 9) : JWT Keycloak validé par `AddJwtBearer` (émetteur, signature, durée,
   audience `ledger-api`), politique exigeant le scope `ledger:write` et le claim `azp`.
   `actor` des événements = `azp` (client OAuth appelant). Les claims ne sont pas renommés
   (`MapInboundClaims = false`).
6. **Corrélation** : l'en-tête `X-Correlation-Id` (UUID) est repris comme `correlation_id` des
   événements, sinon un UUIDv7 est généré ; il est renvoyé dans la réponse.
7. **Contrat OpenAPI 3.1** : généré au build (`Microsoft.Extensions.ApiDescription.Server`) dans
   `docs/api/openapi.json`, versionné. Un test compare le document servi par l'application au
   fichier versionné, et le CI échoue si le build modifie le fichier (`git diff --exit-code`).
   Le format est JSON et non YAML comme le prévoyait le plan : l'outil de génération au build
   (`dotnet-getdocument` 10.0) n'écrit que du JSON, et un convertisseur maison dans MSBuild
   ajouterait du code non testé au build. Le document n'est servi (`/openapi/v1.json`) qu'en
   environnement de développement.
8. **Entrées bornées** : corps de requête limité à 1 Mio ; identifiant d'entité ≤ 64 caractères,
   code de compte ≤ 32 (lettres, chiffres, `.`, `_`, `-`) ; libellé ≤ 500 caractères ; montant en
   chaîne décimale d'au plus 18 chiffres entiers ; nombres JSON stricts (pas de nombre en chaîne).

## Conséquences

- Rejouer une requête (`PUT` ou contre-passation) renvoie le même accusé, sans doublon, grâce au
  journal (ADR 0003) ; la différence `201` / `200` indique au client s'il a créé la ressource.
- Les champs `code` et `details` des Problem Details font partie du contrat public : renommer un
  code d'erreur de domaine est un changement incompatible.
- Toute modification de l'API apparaît dans le diff de `docs/api/openapi.yaml` en revue de PR.

## Options écartées

- **Entité dans le chemin** (`/v1/entities/{entityId}/...`) : plus REST, mais l'identité d'une
  écriture est son `entryId` seul (ADR 0003) ; à reconsidérer si l'autorisation devient
  par entité (phase 9).
- **Clé d'idempotence en en-tête** (`Idempotency-Key`) : redondante avec l'identifiant choisi par
  le client, et demanderait une table de clés à purger.
- **`POST /v1/journal-entries`** avec identifiant serveur : contraire à l'invariant 5.
