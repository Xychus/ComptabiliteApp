# 0019 — API de requête : structure, contrat HTTP et indicateur de fraîcheur

- **Statut** : proposé
- **Date** : 2026-10-20
- **Phase** : 4 (4b)

## Contexte

Les projections de la phase 4a (ADR 0015 à 0018) alimentent le schéma `projections`. L'API de
requête doit les exposer (`scope ledger:read`) sans jamais participer à une décision d'écriture
(invariant 4), et dire au client si ce qu'il lit est à jour : les projections sont alimentées de
façon asynchrone et peuvent prendre du retard ou s'arrêter sur un message empoisonné (ADR 0015,
point 7).

Les tests d'architecture interdisent à `Ledger.Query.Api` (composition) de toucher Npgsql. Il
fallait donc une couche qui porte la lecture PostgreSQL.

## Décision

1. **Structure hexagonale du service de requête** (choix du responsable du projet, 2026-10-20) :
   - `Ledger.Query.Application` : ports de lecture étroits, un par besoin (`ILecteurSoldes`,
     `ILecteurMouvements`, `ILecteurLignesCompte`, `ILecteurEcritures`, `ILecteurPeriodes`,
     `ILecteurInstantanes`, `ILecteurTaxes`, `ILecteurPlanComptable`, `ILecteurReperesFraicheur`),
     une classe par consultation (`ConsulterSoldeCompte`, `ConsulterBalanceVerification`…),
     l'évaluateur de fraîcheur et le curseur de pagination ;
   - `Ledger.Query.Infrastructure` : implémentations Npgsql (rôle `ledger_query`, `SELECT`
     seulement) et en mémoire, vérifiées par la même suite de contrat ;
   - `Ledger.Query.Api` : composition et transport HTTP.
   Le service de requête ne dépend ni du service de commandes ni de celui des projections (règle
   d'architecture) : il ne partage avec le worker que le schéma SQL (migration 0007). Un test de
   bout en bout (journal → worker réel → lecteurs `ledger_query`) garantit que les deux côtés
   s'accordent, y compris après une reconstruction.
2. **Ressources** (préfixe `/v1`, toutes en `GET`, entité en paramètre `entityId` obligatoire sauf
   pour une écriture, dont l'identifiant est global) :
   - `/accounts/{accountId}/balance?entityId=&asOf=` : solde par devise, **positif dans le sens
     normal du compte** ; `404 AccountNotFound` si le compte n'est pas au plan comptable ;
   - `/trial-balance?entityId=&asOf=` : une ligne par compte mouvementé et devise (débits,
     crédits, solde débiteur ou créditeur), totaux par devise et indicateur `balanced` ;
   - `/accounts/{accountId}/entries?entityId=&cursor=&limit=` : sous-livre ;
   - `/journal-entries/{entryId}` : écriture, lignes, `reversalOf` / `reversedBy` ;
     `404 JournalEntryNotFound` ;
   - `/periods/{period}?entityId=` : statut, version du flux (valeur attendue par la commande
     suivante), empreinte du sceau, instantané une fois scellée ; `404 PeriodNotFound` ;
   - `/tax/returns/gst-qst?entityId=&period=` : TPS/TVQ perçues, payées et nettes par devise
     (net = perçue − payée, ADR 0018), avec le statut de la période.
3. **`asOf` = date d'affaires, incluse** (choix du responsable du projet). Sans `asOf`, les soldes
   courants (`account_balances`) ; avec `asOf`, la somme des lignes du grand livre
   (`ledger_lines`) dont la date d'affaires est au plus `asOf`, par l'index
   `ledger_lines_account_order`. Une écriture saisie plus tard avec une date antérieure modifie donc
   le résultat d'un même `asOf` tant que sa période n'est pas scellée ; pour une vue figée, lire
   l'instantané de la période scellée.
4. **Pagination par curseur** : ordre comptable (date d'affaires, `global_sequence`, numéro de
   ligne), jamais par UUID ; le curseur est la position de la dernière ligne renvoyée, opaque
   (base64url, versionné), et la page suivante est lue par comparaison de valeurs de ligne
   (`(date, séquence, ligne) > (…)`), sans `OFFSET`. Une ligne insérée avant le curseur ne décale
   aucune page. `limit` de 1 à 500 (100 par défaut) ; un curseur illisible est un
   `400 InvalidCursor`.
5. **Indicateur de fraîcheur** dans chaque réponse : `freshness { throughSequence,
   journalSequence, stale }`.
   - `throughSequence` = plus petite position (`projection_positions`) des projections lues par
     la consultation ; une projection sans position compte pour 0 ;
   - `journalSequence` = plus grande séquence de `ledger.events` ;
   - `stale` = le **premier événement du journal au-delà de `throughSequence` a été enregistré il
     y a plus que `Ledger:Fraicheur:RetardMaximal`** (30 s par défaut, strictement positif sinon
     le démarrage échoue), mesuré avec `IClock`. Un journal calme n'est jamais périmé, et un
     retard de quelques millisecondes après une longue période calme non plus.
   - Les repères sont lus **avant** les données : les positions ne reculent jamais, les données
     sont donc au moins aussi récentes que `throughSequence`.
6. **Sécurité et erreurs** comme l'API de commandes (ADR 0014) : JWT Keycloak validé par l'API,
   audience `ledger-api`, **scope `ledger:read`** (le scope `ledger:write` ne l'accorde pas),
   sondes anonymes ; Problem Details avec `code` et `details`, table code → statut
   (`AccountNotFound`, `JournalEntryNotFound`, `PeriodNotFound` → `404`, `InvalidCursor` → `400`),
   `400 InvalidRequest` avec `errors` par paramètre ; aucun corps de requête accepté.
7. **Contrat** généré au build dans `docs/api/query-openapi.json`, vérifié par un test et par
   l'étape « contrat OpenAPI à jour » du CI. Les DTO HTTP (`Ledger.Contracts.Http.V1`) et les
   codes d'erreur restent en anglais (contrat externe) ; les montants sont des chaînes décimales
   à deux décimales accompagnées de leur devise.

## Conséquences

- La lecture `asOf` agrège les lignes de l'entité : coût proportionnel au nombre de lignes
  jusqu'à la date. Si le volume l'exige, partir du dernier instantané scellé et n'agréger que les
  lignes postérieures (les périodes antérieures doivent alors toutes être scellées, ADR 0017).
- La fraîcheur est approximative de deux façons, assumées : `throughSequence` est la plus grande
  séquence vue (un événement de séquence inférieure validé plus tard, ADR 0007, n'est pas
  signalé), et la position est commune à toutes les partitions Kafka (une partition bloquée
  pendant que les autres avancent n'est pas visible). L'alarme sur le retard du groupe de
  consommateurs (phase 11) reste le contrôle opérationnel.
- Aucune autorisation par entité : tout jeton `ledger:read` lit toutes les entités (déjà noté
  pour la phase 9).
- `stale` dépend de l'horloge du conteneur de l'API et de `recorded_at` (horloge PostgreSQL) :
  un décalage d'horloge fausse l'indicateur d'autant.

## Options écartées

- **Lecteurs dans les projets Projections** : schéma et types de lignes partagés, mais l'API de
  requête embarquerait Confluent.Kafka et chaque changement de lecture ferait monter la version du
  composant `projections`.
- **`asOf` sur la séquence du journal** : vue d'audit (« ce qui était enregistré à tel moment »),
  pas la vue comptable attendue d'un solde à une date.
- **Fraîcheur par écart d'horodatage entre la tête du journal et la projection** : une projection
  à un événement près après une longue période calme paraîtrait en retard de plusieurs jours.
- **Pagination par `OFFSET`** : coût croissant et pages décalées par les insertions.
