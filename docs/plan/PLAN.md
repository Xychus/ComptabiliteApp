# Plan de mise en œuvre — Grand livre comptable événementiel sur AWS

> Emplacement dans le dépôt : `docs/plan/PLAN.md`. À utiliser avec `CLAUDE.md` à la racine.
> Statut du projet : `0.y.z` (développement initial) jusqu'à la phase 11.

## Comment utiliser ce plan avec Claude Code

1. **Une phase = une session = une branche = une PR.** Ne pas enchaîner deux phases dans la même session :
   le contexte reste propre et chaque PR reste révisable.
2. Démarrer chaque session en **mode plan** (`Shift+Tab` jusqu'à « plan mode »), coller le
   *prompt de lancement* de la phase, relire le plan proposé par Claude Code, puis le laisser exécuter.
3. Claude Code coche les cases au fil de l'eau et ajoute une entrée dans le **Journal** en fin de phase.
4. Chaque phase se termine par un **point d'arrêt humain** : revue de la PR, et pour les phases
   d'infrastructure, exécution du `terraform apply` par le pipeline (jamais par Claude Code).
5. Si une phase révèle un problème de conception, on écrit un ADR et on met ce plan à jour
   **avant** de continuer, plutôt que de contourner.

Prompt générique de reprise, utilisable à tout moment :

```
Lis CLAUDE.md et docs/plan/PLAN.md. Indique-moi quelle phase est en cours d'après les cases
cochées et le Journal, ce qu'il reste à faire, puis propose un plan pour la suite de cette phase.
```

## Vue d'ensemble des phases

| # | Phase | Livrable principal | Dépend de |
|---|---|---|---|
| 0 | Fondations du dépôt | Squelette .NET 10 + CI de base + conventions | — |
| 1 | Domaine du grand livre | Agrégats et règles purs, testés | 0 |
| 2 | Event store PostgreSQL | Journal append-only, hash, outbox, concurrence | 1 |
| 3 | API de commandes | `PUT` écritures, contre-passation, scellement | 2 |
| 4 | Projections et API de requête | Soldes, balance, TPS/TVQ, snapshots, rejeu | 2, 3 |
| 5 | Bout en bout local (CDC) | Outbox → Debezium → Kafka → projections en conteneurs | 4 |
| 6 | Infra : amorçage et fondations AWS | État Terraform, OIDC GitHub, KMS, réseau, ECR | 0 |
| 7 | Infra : données | Aurora, MSK, MSK Connect, archive S3 WORM | 6 |
| 8 | Infra : exécution | ECS Fargate, Service Connect TLS, PKI, observabilité | 7, 5 |
| 9 | Entrée sécurisée | API Gateway privée, autorizer Keycloak, plans d'usage | 8 |
| 10 | CI/CD complet et versions | Déploiement dev/prod, release-please, signature | 8, 9 |
| 11 | Conformité et exploitation | Intégrité, archive 6 ans, alarmes, runbooks, DR | 10 |

Les phases 1 à 5 (application) et 6 à 7 (infrastructure) peuvent avancer en parallèle si deux
personnes travaillent sur le projet.

---

## Décisions d'architecture prises par ce plan

À consigner en ADR pendant la phase indiquée. Elles précisent la conception discutée en amont.

| ADR | Décision | Raison | Phase |
|---|---|---|---|
| 0001 | Monorepo, versions SemVer **par composant** (release-please, mode manifest) | Changements atomiques API ↔ consommateurs, cycles de version indépendants | 0 |
| 0002 | Architecture hexagonale par service, règles vérifiées par ArchUnitNET | SOLID (inversion des dépendances) vérifié par le CI | 0 |
| 0003 | **Flux par écriture** (`entry-{entryId}`) : `JournalEntryPosted` en `stream_version = 1` | Idempotence naturelle : un second `PUT` du même `entryId` entre en collision | 2 |
| 0004 | **Flux par compte uniquement pour les comptes contraints** (`no_negative_balance`), avec `balance_after` | Évite de sérialiser les comptes « chauds » (ex. TPS à percevoir touchée par chaque vente) | 2 |
| 0005 | **Chaînage par hash par flux** + **sceau de période** (racine de Merkle de tous les événements de la période) | Une chaîne globale sérialiserait toutes les écritures du système | 2 |
| 0006 | Hash calculé sur la forme canonique JSON **RFC 8785 (JCS)**, SHA-256 | Reproductibilité du hash indépendamment de la sérialisation | 2 |
| 0007 | PostgreSQL est la source de vérité, Kafka n'est qu'un transport ; outbox + Debezium | Garanties ACID, pas de double écriture | 2 |
| 0008 | Règles d'arrondi TPS (5 %) / TVQ (9,975 %) : niveau ligne ou facture, `MidpointRounding` | **À valider avec le comptable / fiscaliste** | 1 |
| 0009 | MSK avec authentification **IAM (SASL/IAM sur TLS)** pour MSK Connect et les services | Supportée par MSK Connect ; pas de certificats clients Kafka à gérer | 7 |
| 0010 | Racine de PKI interne : voir point à valider P1 ci-dessous | Exigence « transit adossé à une clé non gérée par AWS » | 8 |
| 0011 | Verrouillage de l'état Terraform par fichier de verrou S3 natif (`use_lockfile`) | Le verrou DynamoDB est déprécié dans les versions récentes de Terraform | 6 |
| 0012 | **Plan comptable tenu dans le journal** (flux `chart-{entité}`, `AccountDefined`) ; devise, sens normal et contrainte de solde immuables | Piste d'audit des changements du plan comptable ; aucune décision ne lit une table modifiable | 3 |
| 0013 | Sceau de période : `eventCount` + racine de Merkle, nombre vérifié par le trigger sous verrou exclusif | Une écriture validée pendant le calcul ne peut pas échapper à la racine | 3 |
| 0014 | Contrat HTTP de l'API de commandes : ressources, statuts, Problem Details (`code`, `details`), JWT, OpenAPI généré au build | L'API devient l'API publique du composant `command` | 3 |
| 0015 | Projections : une transaction par message (modèles, événements appliqués par `event_id`, position, checkpoint Kafka) ; reprise depuis la base ; erreur = arrêt | `global_sequence` n'est pas monotone à la validation ; livraison « au moins une fois » | 4 |
| 0016 | Rejeu par tables parallèles `__rebuild`, rattrapage par anti-jointure sous verrou exclusif, bascule par renommage | Reconstruire sans interrompre l'API ni le consommateur, sans perte ni doublon | 4 |
| 0017 | Instantanés de période = mouvements de la période scellée, recalculés depuis le journal au `PeriodSealed` | Résultat indépendant du retard et de l'ordre de traitement | 4 |
| 0018 | TPS/TVQ : comptes fiscaux par entité en configuration du worker ; **format à valider avec le comptable** | Les événements ne portent pas de rôle fiscal ; pas de changement du contrat de commandes en phase 4 | 4 |
| 0019 | API de requête : projets `Query.{Application,Infrastructure,Api}`, `asOf` = date d'affaires, pagination par curseur, fraîcheur = âge du premier événement non projeté | Lecture PostgreSQL hors de la composition ; vue comptable ; pages stables ; pas de faux retard après une période calme | 4 |
| 0020 | Chaîne CDC : connecteur Debezium invariant + paramètres d'environnement, routeur d'outbox, snapshot initial, publication des insertions seules, purge par fonction sous condition de slot | Même configuration en local et sur MSK Connect ; aucune perte, ordre par entité ; outbox purgeable sans risque | 5 |
| 0021 | Identité du pipeline : OIDC, `gha-plan` (pull requests, lecture seule) / `gha-apply` (environnement protégé, PowerUser + IAM sous frontière de permissions) | Aucun identifiant durable ; pas d'escalade de privilèges par IAM ; état hors d'atteinte | 6 |
| 0022 | Une CMK par domaine, usage cryptographique uniquement via le service (`kms:ViaService`), administration nommée ; jamais `aws/*` (validation des modules + Checkov maison) | La politique par défaut délègue tout à IAM | 6 |
| 0023 | VPC privé sans passerelle Internet ni NAT, deux niveaux sur 3 zones, points de terminaison VPC | Aucune sortie Internet sans besoin avéré (Keycloak : à revoir en phase 9) | 6 |

### Points à valider par un humain avant la phase concernée

- **P1 (avant phase 8) — PKI et clé non gérée par AWS.** Vérifier dans la documentation AWS
  si la clé privée d'une CA AWS Private CA peut être adossée à une CMK. Si ce n'est pas le cas
  (la clé de CA réside alors dans les HSM gérés par AWS Private CA), l'option recommandée est :
  **CA racine hors ligne détenue par l'organisation** (HSM interne ou CloudHSM), et AWS Private CA
  en **CA subordonnée signée par cette racine**. L'ancre de confiance reste sous ton contrôle.
  La CRL et les journaux d'audit de la CA sont dans un bucket S3 chiffré par CMK.
- **P2 (avant phase 9)** — Intégration privée API Gateway REST → backend : vérifier si le VPC
  Link cible directement un ALB dans la région, sinon NLB devant l'ALB interne.
- **P3 (avant phase 9)** — Runtime Lambda `dotnet10` disponible dans la région, sinon Native AOT
  sur `provided.al2023`.
- **P4 (avant phase 7)** — Version Aurora PostgreSQL : dernière version majeure supportée au
  moment de l'implémentation, avec réplication logique ; vérifier le comportement des slots de
  réplication logique au basculement (*failover*) pour cette version.
- **P5 (avant phase 1)** — Plan comptable, granularité des périodes (mensuelle), périodes de
  déclaration TPS/TVQ, règles d'arrondi (ADR 0008) : à fournir par le comptable.
- **P6 (avant phase 6)** — Région AWS (`ca-central-1` par défaut), identifiants des comptes AWS
  dev/prod, URL du realm Keycloak, nom de l'organisation et du dépôt GitHub.

---

## Phase 0 — Fondations du dépôt

**Objectif** : un monorepo .NET 10 qui compile, se teste et se vérifie en CI, avec toutes les
conventions en place avant la première ligne de code métier.

### Tâches
- [x] `global.json` épinglant le SDK .NET 10 ; `Ledger.slnx`
- [x] `Directory.Build.props` : `Nullable`, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`,
      `ImplicitUsings`, `InvariantGlobalization`, version lue depuis un fichier géré par release-please
- [x] `Directory.Packages.props` (gestion centralisée des paquets)
- [x] `.editorconfig`, `.gitignore`, `.gitattributes`
- [x] Squelette des projets de `src/` et `tests/` décrits dans `CLAUDE.md` (projets vides qui compilent)
- [x] `tests/Ledger.Architecture.Tests` avec ArchUnitNET : règles de dépendances entre couches
      (un test échoue volontairement si on ajoute une référence interdite — le vérifier puis retirer)
- [x] `.github/workflows/ci.yml` (sur PR) : restore, build `-warnaserror`, tests sans Docker,
      tests avec Docker, couverture (Microsoft.Testing.Extensions.CodeCoverage + ReportGenerator,
      au lieu de coverlet : voir Journal) avec seuils, CodeQL
- [x] Vérification Conventional Commits sur les PR (titre de PR ou commits)
- [x] `release-please-config.json` + `.release-please-manifest.json` (mode manifest, composants :
      `command`, `projections`, `query`, `authorizer`, `contracts`, `infra`), tous à `0.1.0`
- [x] `.github/dependabot.yml` (NuGet, GitHub Actions, Terraform, Docker)
- [x] Gabarit de PR (`.github/pull_request_template.md`) avec la définition de « terminé »
- [x] `README.md` (lien vers https://semver.org, comment lancer les tests)
- [x] ADR 0001 et 0002

### Critères d'acceptation
- `dotnet build Ledger.slnx -warnaserror` et `dotnet test Ledger.slnx` passent localement et en CI.
- Ajouter une référence `Ledger.Command.Domain → Npgsql` fait échouer les tests d'architecture.
- Une PR dont le titre n'est pas en Conventional Commits est bloquée.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 0 uniquement, sur la branche
phase/00-fondations. Commence en mode plan : liste les fichiers que tu vas créer, puis exécute.
Termine en cochant les cases de la phase 0 et en ajoutant une entrée au Journal.
```

**Point d'arrêt** : revue de la PR ; activer la protection de branche sur `main`
(PR obligatoire, CI vert obligatoire, historique linéaire).

---

## Phase 1 — Domaine du grand livre (pur, sans infrastructure)

**Objectif** : toute la logique comptable, testée exhaustivement, sans base de données ni réseau.

### Tâches
- [x] `Ledger.SharedKernel` : `Money` (montant `decimal` + devise, opérations, arrondi explicite),
      `EntityId`, `AccountId`, `EntryId` (UUIDv7), `Result<T>` / erreurs de domaine, `IClock`
- [x] Modèle de période : dérivation `business_date → period` (ex. `2026-03`), statut `OPEN`/`SEALED`
- [x] Agrégat `JournalEntry` : commande `PostEntry` → `JournalEntryPosted` ou rejet ;
      commande `ReverseEntry` → `JournalEntryReversed` (écriture inverse, `reversalOf`)
- [x] Agrégat `ConstrainedAccount` (comptes `no_negative_balance`) : état = `balance_after` +
      version ; `ApplyLine` → `AccountLinePosted` ou rejet `InsufficientBalance`
- [x] Agrégat `Period` : `OpenPeriod`, `SealPeriod` → `PeriodSealed` ; refus des écritures en période scellée
- [x] Règles d'écriture extensibles (`IEntryRule`) : équilibre débit/crédit par devise, au moins
      deux lignes, montants strictement positifs, compte actif, devise unique par écriture
- [x] Calcul TPS/TVQ derrière `ITaxCalculator` (implémentation selon ADR 0008)
- [x] Contrats d'événements dans `Ledger.Contracts` avec `schema_version = 1`
- [x] Tests Given / When / Then pour chaque commande et chaque rejet
- [x] Tests de propriétés FsCheck : (a) toute écriture acceptée est équilibrée ; (b) aucune suite
      de commandes acceptées ne rend un compte contraint négatif ; (c) écriture + contre-passation = effet nul
- [x] ADR 0008 (rédigé avec les hypothèses, marqué « à valider »)

### Critères d'acceptation
- `Ledger.Command.Domain` ne référence que `Ledger.SharedKernel` (test d'architecture vert).
- Couverture `Domain` ≥ 90 % ; les trois propriétés FsCheck passent sur ≥ 1 000 cas.
- Exemple de référence (vente 100 $ : clients 114,98 / ventes 100,00 / TPS 5,00 / TVQ 9,98) reproduit en test.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 1 sur la branche phase/01-domaine.
Le domaine doit rester pur : aucune référence d'infrastructure. Écris les tests en même temps
que le code (Given/When/Then + FsCheck). Si une règle comptable est ambiguë, écris l'hypothèse
dans l'ADR 0008 et signale-la-moi plutôt que de trancher seul.
```

**Point d'arrêt** : revue des règles métier et de l'ADR 0008 **avec le comptable**.

---

## Phase 2 — Event store PostgreSQL

**Objectif** : le journal d'événements durable, append-only, chaîné, avec concurrence optimiste
et outbox dans la même transaction.

### Tâches
- [x] `Ledger.Migrations` (DbUp, scripts SQL numérotés, exécutable en tâche ponctuelle) :
  - [x] table `events` (`global_sequence`, `event_id`, `stream_id`, `stream_version`, `event_type`,
        `schema_version`, `entity_id`, `business_date`, `recorded_at`, `period`, `payload JSONB`,
        `balance_after NULL`, `correlation_id`, `causation_id`, `actor`, `hash`, `previous_hash`),
        contrainte `UNIQUE (stream_id, stream_version)`, index utiles
  - [x] partitionnement de `events` par période (déclaratif PostgreSQL)
  - [x] trigger qui refuse `UPDATE` et `DELETE` sur `events` ; rôle applicatif sans ces privilèges
  - [x] table `outbox` (`event_id`, `topic`, `partition_key`, `payload`)
  - [x] tables de référence `accounts` (avec `no_negative_balance`) et `periods`
  - [x] rôles : `ledger_app` (INSERT/SELECT), `ledger_projection`, `debezium` (REPLICATION) ; publication `ledger_pub`
- [x] Ports dans `Application` : `IEventAppender`, `IEventReader`, `IUnitOfWork`
- [x] Implémentation en mémoire (tests) et implémentation Npgsql (Infrastructure)
- [x] Écriture atomique d'une écriture comptable : `JournalEntryPosted` (flux `entry-{id}`, v1) +
      `AccountLinePosted` pour chaque compte contraint (flux `account-{entité}-{compte}`, v+1,
      `balance_after`) + lignes d'outbox, **dans une seule transaction**
- [x] Conflit d'unicité → `ConcurrencyConflict` (retry côté application, borné) ;
      `entryId` déjà existant avec charge identique → renvoyer l'existant ; charge différente → conflit
- [x] Hash : JCS (RFC 8785) + SHA-256, chaînage par flux (`previous_hash` = hash de la version précédente du flux)
- [x] Suite de **tests de contrat** de l'event store (classe abstraite) exécutée contre la version
      en mémoire et contre PostgreSQL via Testcontainers : append-only, ordre, conflit de version,
      idempotence `entryId`, chaînage, atomicité (échec → rien n'est écrit, ni événement ni outbox)
- [x] Test de concurrence : deux retraits simultanés sur un compte à 100 $ (60 $ chacun) →
      exactement un accepté, solde final 40 $
- [x] ADR 0003 à 0007

### Critères d'acceptation
- Les deux implémentations passent la même suite de contrat.
- Une tentative d'`UPDATE` ou `DELETE` sur `events` échoue, même avec le rôle applicatif.
- Le test de concurrence passe 100 fois de suite sans échec.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 2 sur la branche phase/02-event-store.
Commence par la suite de tests de contrat de l'event store, puis l'implémentation en mémoire,
puis Npgsql. Rédige les ADR 0003 à 0007 avant d'implémenter ce qu'ils décident.
```

**Point d'arrêt** : revue du schéma SQL et des ADR (c'est la partie la plus difficile à changer ensuite).

---

## Phase 3 — API de commandes

**Objectif** : exposer les commandes en HTTP avec un contrat OpenAPI explicite.

### Tâches
- [x] `PUT /v1/journal-entries/{entryId}` (création idempotente, `entryId` UUIDv7 fourni par le client)
- [x] `POST /v1/journal-entries/{entryId}/reversals`
- [x] `POST /v1/periods/{period}/open`, `POST /v1/periods/{period}/seal` (avec `expectedVersion`)
- [x] `PUT /v1/accounts/{accountId}` (plan comptable)
- [x] Réponses : `201`/`200` avec `entryId`, `sequenceNumber`, `recordedAt`, `period`, `hash` ;
      `422` (règle métier, ligne fautive indiquée), `409` (conflit, période scellée), `400`, `401`, `403`
- [x] Problem Details (RFC 9457) pour toutes les erreurs
- [x] Champs serveur (`sequenceNumber`, `recordedAt`, `period`, `hash`) ignorés en entrée
- [x] Authentification JWT (Keycloak) en défense en profondeur (`AddJwtBearer`, audience `ledger-api`,
      scope `ledger:write`) ; `actor` = `azp` du jeton
- [x] Génération de `docs/api/openapi.json` au build ; test qui échoue si le fichier versionné diffère
      (JSON et non YAML : voir ADR 0014)
- [x] Health checks (`/health/live`, `/health/ready`) ; journalisation structurée ; OpenTelemetry
- [x] Dockerfile : image `aspnet:10.0`, utilisateur non root, système de fichiers en lecture seule
- [x] Tests d'intégration API (`WebApplicationFactory` + Testcontainers PostgreSQL)

### Critères d'acceptation
- Rejouer deux fois le même `PUT` renvoie le même résultat sans doublon en base.
- Une écriture déséquilibrée renvoie `422` en Problem Details désignant la cause.
- Un appel sans jeton valide renvoie `401`.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 3 sur la branche phase/03-api-commandes.
L'API ne contient aucune logique métier : elle traduit HTTP ↔ commandes de l'Application.
```

**Point d'arrêt** : revue du contrat OpenAPI (il devient l'API publique au sens SemVer).

---

## Phase 4 — Projections et API de requête

**Objectif** : modèles de lecture reconstructibles et API de consultation.

### Tâches
- [x] Worker de projection : consommateur Kafka (Confluent.Kafka), traitement par partition,
      checkpoint transactionnel (`projection_checkpoints`) — modèle de lecture et checkpoint
      mis à jour dans la même transaction
- [x] Projections (une classe chacune, `IEventHandler<T>`) : `account_balances`, `ledger_entries`
      (grand livre et sous-livres), `tax_projection` (TPS/TVQ collectées / payées par période)
- [x] Snapshots de fin de période (`period_snapshots`), figés au scellement
- [x] Upcasting des événements à la lecture (infrastructure prête, même si `schema_version = 1` seulement)
- [x] Outil de **rejeu** : reconstruire une projection dans une table parallèle à partir de la
      table `events` (curseur sur `global_sequence`), puis bascule (blue/green)
- [x] API de requête : `GET /v1/accounts/{id}/balance?asOf=`, `GET /v1/trial-balance?asOf=`,
      `GET /v1/accounts/{id}/entries` (pagination par curseur), `GET /v1/tax/returns/gst-qst?period=`,
      `GET /v1/journal-entries/{id}`, `GET /v1/periods/{period}` ; scope `ledger:read`
- [x] Indicateur de fraîcheur dans les réponses (`throughSequence`, `stale` si le retard dépasse un seuil)
- [x] Tests : idempotence (même événement livré deux fois → effet unique), rejeu (même résultat
      qu'en incrémental), balance équilibrée après rejeu, exemple TPS/TVQ de référence

### Critères d'acceptation
- Vider puis rejouer une projection donne exactement les mêmes soldes qu'en incrémental.
- Livrer deux fois le même événement ne modifie pas les soldes.
- La balance de vérification est équilibrée sur tous les jeux de tests.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 4 sur la branche phase/04-projections.
Chaque projection est une classe indépendante. Le rejeu et l'idempotence sont testés avant
d'ajouter l'API de requête.
```

**Point d'arrêt** : revue ; vérifier avec le comptable le format de la projection TPS/TVQ.

---

## Phase 5 — Bout en bout local avec CDC

**Objectif** : prouver la chaîne complète en conteneurs avant tout déploiement AWS.

### Tâches
- [x] `docker-compose.yml` de développement : PostgreSQL (réplication logique), Kafka, Kafka
      Connect + Debezium PostgreSQL, Keycloak (realm de test importé), services .NET
- [x] Configuration du connecteur Debezium (outbox + `EventRouter`, clé = `partition_key`)
      versionnée dans `deploy/debezium/`, **réutilisée telle quelle** en phase 7
- [x] Test de bout en bout (Testcontainers) : `PUT` écriture → outbox → Debezium → Kafka →
      projection → `GET` solde, avec attente bornée sur `throughSequence`
- [x] Tests de résilience : arrêt de Kafka pendant des écritures (les écritures continuent,
      rattrapage au retour) ; redémarrage du worker de projection (pas de double comptage)
- [x] Vérification de l'ordre : événements d'un même flux toujours appliqués dans l'ordre

### Critères d'acceptation
- Le test de bout en bout passe en CI.
- Pendant une panne Kafka simulée, l'API de commandes continue de répondre `201`.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 5 sur la branche phase/05-bout-en-bout.
La configuration Debezium doit être indépendante de l'environnement (paramétrée), car elle
sera réutilisée sur MSK Connect.
```

**Point d'arrêt** : démonstration locale de la chaîne complète.

---

## Phase 6 — Infra : amorçage et fondations AWS

**Objectif** : socle Terraform sécurisé. Claude Code **écrit** le code, le pipeline **applique**.

### Tâches
- [x] `infra/bootstrap/` (appliqué une fois, manuellement, par un administrateur) : bucket d'état
      S3 (versionné, SSE-KMS CMK, accès public bloqué), CMK de l'état, fournisseur OIDC GitHub,
      rôles IAM `gha-plan` (lecture seule) et `gha-apply` (limité à l'environnement GitHub protégé)
- [x] Backend S3 avec `use_lockfile = true` et `kms_key_id` (ADR 0011)
- [x] Module `kms` : CMK + alias, rotation activée, politique au moindre privilège (conditions
      `kms:ViaService`), une clé par domaine : `aurora`, `msk`, `s3-archive`, `secrets`, `logs`,
      `ecr`, `ecs`, `lambda`, `ssm`
- [x] Module `network` (nommé `reseau`, règle des identifiants français) : VPC, sous-réseaux privés sur 3 AZ (pas de sous-réseau public sauf NAT si
      nécessaire), groupes de sécurité, **VPC endpoints** (KMS, Secrets Manager, SSM, ECR api/dkr,
      S3 gateway, CloudWatch Logs, STS, execute-api), journaux de flux VPC chiffrés CMK
- [x] Module `ecr` : un dépôt par service, `IMMUTABLE`, chiffrement KMS CMK, scan à la poussée
- [x] `infra/envs/dev` et `infra/envs/prod` composant les modules
- [x] Workflow `infra-ci.yml` (sur PR touchant `infra/`) : `fmt -check`, `validate`, TFLint,
      Checkov, `terraform test` sur les modules, `terraform plan` (rôle `gha-plan`) publié en commentaire
- [x] ADR 0011

### Critères d'acceptation
- Checkov ne remonte aucune alerte non justifiée (exceptions documentées dans le code).
- Aucune ressource n'utilise une clé gérée par AWS (`aws/*`) : vérifié par une règle Checkov
  ou un test `terraform test`.
- Le `plan` s'affiche en commentaire de PR sans aucun identifiant AWS stocké dans GitHub.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 6 sur la branche phase/06-infra-fondations.
N'exécute aucune commande qui modifie AWS. Tu peux lancer fmt, validate, tflint, checkov et
terraform test localement. Les valeurs propres à l'organisation (P6) sont des variables.
```

**Point d'arrêt** : un administrateur applique `infra/bootstrap/` manuellement ; configurer les
environnements GitHub `dev` et `prod` (approbation requise pour `prod`).

---

## Phase 7 — Infra : données

**Objectif** : Aurora, MSK, CDC managé et archive immuable, tous chiffrés par CMK.

### Tâches
- [ ] **En premier** — Workflow `infra-apply.yml` (apply en dev, rôle `gha-apply`) : déclenché
      par un push sur `main` touchant `infra/` et à la demande (`workflow_dispatch`) ; job dans
      l'environnement GitHub `dev` (jeton `environment:dev`, seul sujet accepté par `gha-apply`,
      ADR 0021) ; `init` (mêmes paramètres de backend qu'`infra-ci.yml`) → `plan -out` → `apply`
      de ce plan exact ; un seul apply à la fois (`concurrency` sans annulation) ; dev seulement
      (prod : phase 10). Premier apply = fondations de la phase 6 (57 ressources), à faire avant
      les modules ci-dessous. Fusionné seul ou en tête de la PR de la phase 7.
- [ ] Module `aurora` : cluster Aurora PostgreSQL (version selon P4), `storage_encrypted` + CMK,
      groupe de paramètres (`rds.force_ssl = 1`, `rds.logical_replication = 1`), sous-réseaux privés,
      mot de passe maître géré dans Secrets Manager (CMK), sauvegardes + PITR, protection contre
      la suppression, Performance Insights chiffré CMK, journaux exportés chiffrés CMK
- [ ] Tâche ECS ponctuelle de migrations (`Ledger.Migrations`) exécutée par le pipeline avant déploiement
- [ ] Module `msk` : cluster MSK, chiffrement au repos CMK, `client_broker = "TLS"`, chiffrement
      intra-cluster, authentification IAM (ADR 0009), journaux des brokers chiffrés CMK
- [ ] Module `msk-connect` : plugin Debezium (bucket S3 CMK), connecteur basé sur
      `deploy/debezium/`, rôle IAM d'exécution, secrets Debezium dans Secrets Manager
- [ ] Module `archive` : bucket S3 Object Lock en mode **COMPLIANCE, 6 ans**, SSE-KMS CMK,
      `bucket_key_enabled`, versionnement, politique refusant tout transport non TLS
      (`aws:SecureTransport = false`) et tout objet non chiffré par la bonne CMK
- [ ] Alarmes CloudWatch : retard du slot de réplication, stockage Aurora, retard des
      consommateurs MSK, erreurs du connecteur
- [ ] ADR 0009

### Critères d'acceptation
- `terraform plan` en dev ne contient aucun chiffrement par clé gérée par AWS.
- Une connexion PostgreSQL sans TLS est refusée (test de fumée après apply).
- Le connecteur Debezium publie dans MSK après le premier apply en dev.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 7 sur la branche phase/07-infra-donnees.
Commence par le workflow infra-apply.yml (apply en dev). Vérifie ensuite le point P4. N'exécute aucune commande qui modifie AWS.
```

**Point d'arrêt** : apply en dev par le pipeline ; test de fumée Aurora/MSK ; test de basculement
Aurora en dev et observation du comportement du slot Debezium (documenter dans un runbook).

---

## Phase 8 — Infra : exécution

**Objectif** : services .NET sur ECS Fargate, TLS de service à service, observabilité.

### Tâches
- [ ] Résoudre **P1** et rédiger l'ADR 0010 (racine de PKI)
- [ ] Module `pki` : AWS Private CA (racine ou subordonnée selon ADR 0010), CRL dans S3 CMK
- [ ] Module `ecs` : cluster Fargate avec chiffrement du stockage éphémère par CMK, Container
      Insights, **Service Connect avec TLS** (certificats émis par la Private CA)
- [ ] Services : `command`, `projections`, `query` — task definitions (image par tag SemVer exact,
      secrets via Secrets Manager, rôle de tâche au moindre privilège, FS en lecture seule, non root)
- [ ] Load balancer interne (ALB ou NLB selon P2), TLS 1.2+ avec certificat ACM
- [ ] Mise à l'échelle automatique ; déploiement avec disjoncteur et retour arrière automatique
- [ ] Journaux CloudWatch chiffrés CMK, rétention définie ; OpenTelemetry → X-Ray / CloudWatch
- [ ] Tableaux de bord : latence API, taux de `409`/`422`, retard des projections, retard CDC

### Critères d'acceptation
- Le trafic entre services est en TLS (vérifiable dans la configuration Service Connect).
- Aucune task definition n'utilise `latest` ni un secret en variable d'environnement en clair.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 8 sur la branche phase/08-infra-execution.
Commence par me présenter l'analyse du point P1 et une proposition d'ADR 0010 ; attends ma
validation avant d'écrire le module pki.
```

**Point d'arrêt** : validation de l'ADR 0010 (avant le module `pki`), puis apply en dev.

---

## Phase 9 — Entrée sécurisée : API Gateway privée et Keycloak

**Objectif** : authentification et throttling par client interne.

### Tâches
- [ ] Module `keycloak` (provider `keycloak/keycloak`) : realm, client confidentiel par
      application interne (*client credentials* uniquement), mapper d'audience `ledger-api`,
      client scopes `ledger:read` / `ledger:write`, jetons de 5 minutes, signature asymétrique
- [ ] `Ledger.Authorizer` (.NET 10, runtime selon P3) : validation JWT via la configuration
      OIDC du realm mise en cache, lecture de `azp` et des scopes, recherche de la clé du plan
      d'usage (SSM SecureString CMK), politique couvrant **toutes** les routes autorisées par
      les scopes (piège du cache de politique), `usageIdentifierKey`, `context.clientId`
- [ ] Tests unitaires de l'autorizer (jetons valides, expirés, mauvaise audience, mauvais émetteur,
      scopes → politique) avec un JWKS de test
- [ ] Module `apigateway` : API REST **privée** (`api_key_source = "AUTHORIZER"`), resource policy
      limitée au VPC endpoint `execute-api`, autorizer Lambda (TTL ≤ durée des jetons), intégration
      privée (selon P2), plans d'usage par catégorie de clients, throttling distinct écriture/lecture,
      journaux d'accès chiffrés CMK, cache API Gateway **désactivé**
- [ ] Clients .NET d'exemple : obtention du jeton avec cache, retry à backoff sur `429`

### Critères d'acceptation
- Sans jeton, ou avec un jeton d'une mauvaise audience : `401`/`403`.
- Un client avec seulement `ledger:read` ne peut pas appeler `PUT /v1/journal-entries/*`.
- Dépasser le débit du plan renvoie `429`.
- L'`actor` enregistré dans `events` correspond au `azp` du jeton.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 9 sur la branche phase/09-entree.
Vérifie P2 et P3 d'abord. Teste l'autorizer en isolation avant d'écrire le module apigateway.
```

**Point d'arrêt** : apply en dev ; test d'un client interne réel avec Keycloak.

---

## Phase 10 — CI/CD complet et versions

**Objectif** : de la fusion dans `main` jusqu'à la production, versionné en SemVer et traçable.

### Tâches
- [ ] Workflow `release.yml` : release-please (mode manifest) → PR de release par composant,
      changelog, tag `<composant>-v<x.y.z>`, release GitHub
- [ ] Workflow `deploy.yml` (déclenché par une release) :
  - [ ] build de l'image **une seule fois**, tag = version SemVer + métadonnées `+sha.<commit>` dans les labels
  - [ ] scan Trivy (échec sur vulnérabilité critique), SBOM (Syft), signature cosign
  - [ ] push ECR (authentification OIDC)
  - [ ] `dev` : `terraform apply` (réutilise le job d'`infra-apply.yml`, phase 7) → migrations →
        déploiement ECS → tests de fumée
  - [ ] `prod` : approbation manuelle (environnement protégé) → même artefact → mêmes étapes →
        retour arrière automatique sur alarme
- [ ] Détection des changements incompatibles sur PR : `oasdiff` sur `openapi.json` et `query-openapi.json`, vérification
      de compatibilité des contrats d'événements ; échec si incompatible sans `!` / `BREAKING CHANGE`
- [ ] Préversions : `x.y.z-rc.n` pour une validation en préproduction si un environnement est ajouté
- [ ] Stryker.NET (tests de mutation) sur le domaine, en tâche planifiée hebdomadaire
- [ ] Documentation du processus de release dans `docs/runbooks/release.md`

### Critères d'acceptation
- Une PR `fix(command): …` fusionnée produit une version PATCH du seul composant `command`.
- L'image déployée en prod a le même digest que celle validée en dev.
- Une modification incompatible de l'OpenAPI sans `!` bloque la PR.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 10 sur la branche phase/10-cicd.
Aucun identifiant AWS long terme : OIDC uniquement. L'artefact est construit une fois et promu.
```

**Point d'arrêt** : première release complète dev → prod sous supervision.

---

## Phase 11 — Conformité et exploitation

**Objectif** : répondre aux exigences de Revenu Québec et rendre le système exploitable.

### Tâches
- [ ] `Ledger.Integrity` : vérification de la chaîne de hash par flux et de la cohérence
      `balance_after[n] = balance_after[n-1] + delta[n]` ; tâche ECS planifiée ; alarme si écart
- [ ] Scellement de période : calcul du sceau (racine de Merkle des événements de la période),
      stocké dans `periods.period_hash` et dans l'archive
- [ ] Export d'archive : à chaque scellement, export du journal de la période (format ouvert et
      lisible, ex. JSON Lines + manifeste des hash) vers le bucket Object Lock
- [ ] Export d'audit à la demande (période, entité) produisant des « copies accessibles et
      utilisables » ; procédure de vérification indépendante des hash documentée
- [ ] Pièces justificatives : `document_refs` (référence S3 + SHA-256), dépôt dans le bucket WORM
- [ ] Runbooks (`docs/runbooks/`) : panne Kafka/MSK, retard du slot de réplication, basculement
      Aurora et reprise Debezium, reconstruction d'une projection (blue/green), rotation des clés
      API des plans d'usage, perte d'une AZ, restauration PITR
- [ ] Exercice de reprise après sinistre en dev : restauration PITR + rejeu des projections, chronométré
- [ ] Passage en `1.0.0` : l'API publique est déclarée stable (release-please `release-as: 1.0.0`)

### Critères d'acceptation
- Une altération simulée d'un événement (dans une copie de test) est détectée par `Ledger.Integrity`.
- L'archive d'une période scellée ne peut être ni modifiée ni supprimée avant 6 ans.
- L'exercice de reprise est documenté avec son RTO / RPO mesurés.

### Prompt de lancement
```
Lis CLAUDE.md et docs/plan/PLAN.md. Exécute la phase 11 sur la branche phase/11-conformite.
Commence par Ledger.Integrity et son test d'altération, puis le scellement et l'export.
```

**Point d'arrêt** : revue de conformité avec le comptable / fiscaliste ; décision de passage en 1.0.0.

---

## Journal

> Claude Code ajoute une entrée à la fin de chaque session : date, phase, ce qui a été fait,
> décisions prises (avec lien vers l'ADR), problèmes ouverts, besoins hors périmètre notés.

| Date | Phase | Résumé | Décisions / ADR | Points ouverts |
|---|---|---|---|---|
| 2026-10-16 | 0 | Monorepo .NET 10 (SDK 10.0.113) : `Ledger.slnx`, 13 projets `src/` vides avec `AssemblyMarker`, 14 projets de tests (dont `tests/Architecture`), props communs et paquets centralisés, CI (build `-warnaserror`, tests sans/avec Docker, couverture avec seuils par assembly via `eng/Check-Coverage.ps1`, CodeQL), contrôle du titre de PR, release-please (6 composants à `0.1.0`, `version.txt` par composant), Dependabot, gabarit de PR, README. `PLAN.md` déplacé dans `docs/plan/`. Critère vérifié : ajouter `Npgsql` à `Ledger.Command.Domain` fait échouer les tests d'architecture (référence seule → test `.csproj` ; usage d'un type → ArchUnitNET aussi). | [ADR 0001](../adr/0001-monorepo-semver-par-composant.md), [ADR 0002](../adr/0002-architecture-hexagonale-archunitnet.md). **Tests sous Microsoft.Testing.Platform** (`global.json`) : xUnit v3 4.x et le SDK .NET 10 refusent `dotnet test` en mode VSTest. Conséquences : `dotnet test --solution Ledger.slnx`, filtres `--filter-not-trait` / `--filter-trait "Category=Integration"`, couverture par `Microsoft.Testing.Extensions.CodeCoverage` (`coverage.config`) ; `CLAUDE.md` mis à jour. Code de sortie 8 (aucun test) ignoré tant que des projets sont vides. `Program.cs` exclus de la couverture (racines de composition). | (1) À faire par un humain : protection de `main` (PR + checks `ci` et `pr-title` obligatoires, historique linéaire, fusion squash uniquement). (2) Le blocage d'un titre de PR non conventionnel reste à constater sur GitHub. (3) Hors périmètre : `src/SharedKernel` et `src/Tools` n'appartiennent à aucun composant release-please — décider avant la phase 2 si `Ledger.Migrations` suit le composant `command` (ADR à écrire). (4) Dependabot `terraform`/`docker` sans fichiers avant les phases 3 et 6 (avertissement attendu). |
| 2026-10-16 | 0 (clôture) | Points (1) et (2) ci-dessus résolus : ruleset de `main` = PR obligatoire, squash uniquement, historique linéaire, checks requis (`Titre de PR en Conventional Commits`, `Build et tests sans Docker`, `Tests d'intégration (Docker)`, `Seuils de couverture`, `CodeQL`). Règle « code coverage » native de GitHub retirée (seuils appliqués par `eng/Check-Coverage.ps1`). Test : une PR intitulée « Test titre » échoue au check de titre et reste `BLOCKED`. | — | Restent ouverts : (3) et (4) de l'entrée précédente. |
| 2026-10-16 | 1 | Domaine pur : SharedKernel (`Money`, `Currency`, `EntityId`/`AccountId`/`EntryId` UUIDv7, `Result<T>`/`DomainError`, `IClock`, `EquatableList<T>`) ; deciders `Period`, `JournalEntry` (passation, contre-passation), `ConstrainedAccount` (`balance_after`, solde non négatif) ; 7 règles `IEntryRule` ; `QuebecSalesTaxCalculator` ; contrats d'événements V1 (`Ledger.Contracts`) et `EventContractMapper` (`Ledger.Command.Application`). 132 tests : Given/When/Then par commande et par rejet, 4 propriétés FsCheck sur 1 000 cas chacune (nombre vérifié par une sonde temporaire), exemple de référence 114,98 reproduit. Couverture : Domain 99,3 %, SharedKernel 99,2 %, Application et Contracts 100 %. | [ADR 0008](../adr/0008-regles-comptables-tps-tvq.md) (statut « à valider ») : H1 arrondi TPS/TVQ au niveau facture, H2 `AwayFromZero`, H3 TVQ sur le hors-taxes, H4 périodes mensuelles, H5 période explicitement ouverte, H6 une devise par compte et par écriture, H7 solde contraint dans le sens normal, H8 contre-passation unique, H9 montants au cent. Conception : un decider fonctionnel par agrégat (`Evolve` + `Decide` → `Result<événements>`), contexte (période, plan comptable via le port `IChartOfAccounts`) passé par l'appelant, jamais lu dans une projection. La contre-passation est stockée dans le flux de l'écriture d'origine (`entry-{id}` v2) : une seconde contre-passation entre en conflit. | (1) **Revue de l'ADR 0008 avec le comptable** (point d'arrêt), en priorité H1 et H4 (H4 conditionne le partitionnement de la phase 2). (2) Phase 2 : le `reversalEntryId` n'a pas de flux propre — réserver cet identifiant (ex. unicité de `entryId` sur tous les événements) pour qu'un `PUT` ultérieur ne puisse pas le réutiliser ; à trancher dans l'ADR 0003. (3) Phase 2 : idempotence d'un `PUT` identique (`EntryAlreadyPosted` renvoyé par le domaine ; comparaison de charge à faire dans l'Application). |
| 2026-10-16 | 2 | Event store : ports `IEventReader` / `IEventAppender` / `IUnitOfWork` (+ `IChartOfAccountsReader`) dans l'Application ; `EventCatalog` (domaine ↔ contrats V1 dans les deux sens) ; JCS RFC 8785 + `EventHash` et `EventEnvelopeV1` dans `Ledger.Contracts` (vecteurs de la RFC, hash de référence recalculé par `sha256sum`) ; implémentation en mémoire (`Infrastructure.InMemory`) et Npgsql (`Infrastructure.Postgres`, rôle `ledger_app`, `READ COMMITTED`, deux lots `NpgsqlBatch` par validation) ; `PostJournalEntryHandler` (écriture + comptes contraints + outbox en une transaction, 5 tentatives, rejeu idempotent, `EntryIdConflict`) ; migrations DbUp 0001–0005. Critères vérifiés : la même suite de contrat (31 tests) passe sur les deux implémentations ; `UPDATE`/`DELETE`/`TRUNCATE` refusés au propriétaire (`LG001`) et au rôle applicatif (`42501`) ; le test de concurrence (deux retraits de 60 $ sur 100 $) boucle 100 fois sur chaque implémentation sans échec, avec un conflit réel résolu par nouvelle tentative dans 100 essais sur 100 en PostgreSQL. 324 tests (262 sans Docker, aucun conteneur démarré par ceux-ci). Couverture : Application 100 %, Infrastructure 98,9 %, Contracts 99,6 %, Migrations 100 %. | [ADR 0003](../adr/0003-flux-par-ecriture.md) (flux `entry-{id}`, identifiant réservé dans tout le journal par `ledger.entry_ids` → `EntryIdAlreadyUsed` : règle le point (2) de la phase 1), [ADR 0004](../adr/0004-flux-par-compte-contraint.md) (composants des noms de flux échappés), [ADR 0005](../adr/0005-chainage-par-flux-et-sceau-de-periode.md) (trigger `BEFORE INSERT` : verrou consultatif exclusif pour `PeriodSealed`, partagé sinon, refus `LG002` ; `global_sequence` imposé par le trigger), [ADR 0006](../adr/0006-hash-jcs-sha256.md) (`recordedAt` tronqué à la µs, `balanceAfter` normalisé ; implémentation dans `Ledger.Contracts.Integrity`), [ADR 0007](../adr/0007-postgresql-source-de-verite-outbox.md) (partition **par année** de périodes 2020–2039 sans partition par défaut ; unicité portée par les tables compagnes `stream_versions` et `entry_ids`, car PostgreSQL exige la clé de partition dans toute contrainte unique ; rôles créés `NOLOGIN`). L'implémentation en mémoire est dans `Infrastructure` (réutilisable en phase 3). | (1) **Point d'arrêt : revue du schéma SQL et des ADR 0003–0007.** (2) Partitionnement par année confirmé par le responsable du projet (2026-10-16). Branche rebasée sur `main` après la fusion de la PR #6. (3) Le partitionnement suppose H4 (ADR 0008, périodes mensuelles, à valider) ; ajouter une migration pour les années ≥ 2040 et une alarme (phase 11). Une année hors plage est une exception en PostgreSQL mais acceptée en mémoire. (4) Phase 3 : racine de Merkle du sceau à calculer dans la commande de scellement ; gestionnaire de contre-passation (les lignes inversées touchent aussi les comptes contraints) ; changement de `no_negative_balance` d'un compte mouvementé. (5) Phase 5 : purge de l'outbox et correspondance des colonnes avec l'*outbox event router* de Debezium. (6) Npgsql masque le détail des erreurs : l'identifiant ou la période d'un rejet `EntryIdAlreadyUsed` / `PeriodSealed` est repris du lot. (7) Toujours ouvert depuis la phase 0 : composant release-please de `Ledger.Migrations`. (8) P4 : image de test `postgres:17.6-alpine`, à aligner sur la version Aurora retenue. |
| 2026-10-16 | 3 | API de commandes (`Ledger.Command.Api`) : `PUT /v1/journal-entries/{entryId}`, `POST .../reversals`, `POST /v1/periods/{period}/open` et `/seal` (`expectedVersion`), `PUT /v1/accounts/{accountId}` ; l'API ne fait que traduire HTTP ↔ commandes (`RequestMapping`, `DomainProblems`). Application : `ReverseJournalEntryHandler` (écriture inverse + lignes de comptes contraints, rejeu idempotent), `OpenPeriodHandler` / `SealPeriodHandler` (`PeriodJournal`, `WrongExpectedVersion`), `DefineAccountHandler`, `JournalChartOfAccountsReader` ; `PendingBatch`, `ConstrainedAccountLines` et `ConcurrencyRetryPolicy.RunAsync` factorisés. Domaine : agrégat plan comptable (`ChartState`, `ChartDecider`, `AccountDefined`), `PeriodDigest` dans `SealPeriod` / `PeriodSealed`. Contrats : `AccountDefinedV1`, `PeriodSealedV1.EventCount` / `MerkleRoot` (ajout compatible), `MerkleTree`, DTO HTTP V1. Infrastructure : port `IPeriodEventReader` (mémoire et Npgsql), contrôle du nombre d'événements au sceau, enregistrement DI, `PostgresHealthCheck` ; migration 0006 (trigger). JWT Keycloak (audience `ledger-api`, scope `ledger:write`, `azp` exigé = `actor`), Problem Details partout (y compris 400 de désérialisation, 401, 403, 404), `X-Correlation-Id` → `correlation_id`, JSON strict, corps ≤ 1 Mio, journaux JSON, OpenTelemetry (OTLP si `OTEL_EXPORTER_OTLP_ENDPOINT`). Dockerfile (`aspnet:10.0.13-noble-chiseled`, uid 1654) vérifié en local avec `--read-only --cap-drop ALL` ; job CI correspondant et étape « contrat OpenAPI à jour ». Critères vérifiés : deux `PUT` identiques → même réponse, une seule ligne `events` / `entry_ids` en PostgreSQL ; écriture déséquilibrée → `422` `EntryNotBalanced` avec `currency`, `debits`, `credits` (ligne fautive `lineIndex` pour les règles par ligne) ; sans jeton → `401`. 472 tests (79 d'intégration). Couverture : Api 97,5 %, Application 100 %, Domain 99,3 %, Infrastructure 99,0 %, Contracts 99,6 %. | [ADR 0012](../adr/0012-plan-comptable-evenementiel.md) (plan comptable en événements, choisi par le responsable du projet ; règle le point (4) de la phase 2 : attributs immuables), [ADR 0013](../adr/0013-sceau-racine-de-merkle-et-compte.md) (empreinte du sceau ; `LG003` → `ConcurrencyConflict`, recalcul), [ADR 0014](../adr/0014-contrat-http-api-de-commandes.md) (contrat HTTP ; entité dans le corps ; table code → statut). **Écart au plan** : le contrat est `docs/api/openapi.json` (l'outil de génération au build n'écrit que du JSON) ; `CLAUDE.md` et la tâche `oasdiff` de la phase 10 mis à jour. Image SDK `10.0.402` dans le Dockerfile (MCR ne publie pas `10.0.113`) ; `global.json` non copié dans l'image. Validation de configuration au démarrage (autorité Keycloak, chaîne de connexion) suspendue uniquement pendant la génération du contrat (`GetDocument.Insider`). | (1) **Point d'arrêt : revue du contrat OpenAPI** (API publique au sens SemVer) et des ADR 0012–0014. (2) La table `ledger.accounts` n'est plus lue : en faire une projection ou la supprimer en phase 4. (3) Une écriture passée pendant la désactivation d'un compte peut être acceptée (pas de version sur `chart-{entité}`, ADR 0012). (4) Événements du plan comptable rangés dans la période de la date UTC serveur : définir un compte échoue (`409`) si cette période est scellée. (5) Racine de Merkle sans séparation de domaine feuille / nœud (ADR 0005) : à réexaminer à la revue de l'ADR 0005. (6) À faire par un humain : ajouter le check « Image de l'API de commandes » aux checks requis de `main`. (7) Toujours ouverts : composant release-please de `Ledger.Migrations` (phase 0) ; P4 (image `postgres:17.6-alpine`). (8) Hors périmètre : autorisation par entité (le jeton ne restreint pas les entités accessibles) — à traiter avec l'autorizer en phase 9. |
| 2026-10-20 | 4a | Projections (PR 4a ; l'API de requête et l'indicateur de fraîcheur suivent en 4b). Application : `EventDecoder` + `EventUpcasterChain` (upcasting à la lecture, chaîne `n → n+1` testée sur un type d'essai en trois versions), `IProjection` / `IEventHandler<T>`, `ProjectionApplier`, `ProjectionMessageProcessor`, `ProjectionPump`, `ProjectionRebuilder` ; six projections, une classe et un port chacune : `account_balances`, `ledger_entries` (`journal_entries` + `ledger_lines`, sous-livre par compte trié par date d'affaires puis séquence), `chart_of_accounts`, `periods`, `period_snapshots`, `tax_projection`. Infrastructure : session Npgsql (transaction par message : modèles, `{projection}_applied`, `projection_positions`, `consumer_offsets`), magasins Npgsql et en mémoire, `NpgsqlJournalReader`, reconstruction par tables `__rebuild`, `KafkaEventSource` (Confluent.Kafka 2.15.1 ; reprise depuis le checkpoint en base à l'affectation des partitions). Worker : consommation continue ou `rebuild <projection>` ; comptes fiscaux en configuration (`Ledger:Tax:Entities`). Migration 0007 : schéma `projections` (propriété de `ledger_projection`), rôle `ledger_query` (lecture seule), suppression de `ledger.accounts`. Critères vérifiés : vider puis rejouer chaque projection redonne exactement les tables incrémentales (mémoire et PostgreSQL, et propriété FsCheck sur 200 journaux générés) ; un événement livré deux fois, ou republié à un autre offset dans Kafka, ne modifie rien ; la balance est équilibrée avant et après rejeu (propriété) ; exemple TPS/TVQ de référence (5,00 / 2,50 / 9,98 / 4,99). Également testés : annulation complète sur erreur (modèles et checkpoint), validation réellement hors ordre de séquence en PostgreSQL, consommateur bloqué pendant la bascule puis écrivant dans les nouvelles tables, reprise Kafka après redémarrage. 594 tests (dont 2 Kafka). Couverture : Projections.Application 98,5 %, Projections.Infrastructure 99,5 %, Projections.Worker 97,8 %. | [ADR 0015](../adr/0015-projections-idempotence-et-checkpoint.md) (idempotence par `event_id`, pas par séquence ; checkpoint Kafka en base dans la transaction ; erreur = arrêt du worker), [ADR 0016](../adr/0016-rejeu-par-tables-paralleles.md) (rejeu sans interruption, rattrapage sous verrou consultatif exclusif), [ADR 0017](../adr/0017-instantanes-de-periode.md) (instantané = mouvements de la période, recalculés depuis le journal : une écriture peut être scellée avec une séquence supérieure à celle du sceau), [ADR 0018](../adr/0018-projection-tps-tvq.md) (comptes fiscaux en configuration, choisi par le responsable du projet). Partage du code HTTP entre les deux API : duplication dans `Query.Api` (choix du responsable du projet). `IEventHandler<T>` : suppression ciblée et justifiée de CA1711 (nom imposé par `CLAUDE.md`). | (1) **Point d'arrêt : vérifier avec le comptable le format de la projection TPS/TVQ** (ADR 0018). (2) Les « sous-livres » se limitent au sous-livre d'un compte : aucune dimension (client, fournisseur) dans les événements ; à ajouter au contrat si nécessaire (hors périmètre). (3) Tables `{projection}_applied` : une ligne par événement et par projection, purge à étudier (phase 11). (4) Phase 5 : vérifier le format exact produit par l'*outbox event router* (valeur = enveloppe JSON, clé = entité) et l'image Kafka (`confluentinc/cp-kafka:7.9.2` en test). (5) Phase 7/8 : authentification IAM de MSK (aujourd'hui `SecurityProtocol` seul), Dockerfile du worker, sondes de santé et OpenTelemetry du worker (journaux JSON seulement pour l'instant). (6) P4 : sur Aurora, la migration 0007 accorde `ledger_projection` au rôle qui migre pour lui transférer les tables. |
| 2026-10-20 | 4b | API de requête (PR 4b). Nouveaux projets `Ledger.Query.Application` (9 ports de lecture étroits, une classe par consultation : `ConsulterSoldeCompte`, `ConsulterBalanceVerification`, `ConsulterLignesCompte`, `ConsulterEcriture`, `ConsulterPeriode`, `ConsulterDeclarationTpsTvq` ; `EvaluateurFraicheur`, `CurseurLignes`) et `Ledger.Query.Infrastructure` (lecteurs Npgsql, rôle `ledger_query`, et en mémoire), avec leurs projets de tests ; `Ledger.Query.Api` : `GET /v1/accounts/{id}/balance`, `/v1/trial-balance`, `/v1/accounts/{id}/entries` (curseur, `limit` 1–500), `/v1/journal-entries/{id}`, `/v1/periods/{period}`, `/v1/tax/returns/gst-qst` ; scope `ledger:read`, Problem Details (table code → statut), `X-Correlation-Id`, journaux JSON, OpenTelemetry, sondes, contrat `docs/api/query-openapi.json` généré au build (paramètres `entityId` / `period` déclarés obligatoires). DTO HTTP V1 ajoutés à `Ledger.Contracts` (anglais : contrat externe). Chaque réponse porte `freshness { throughSequence, journalSequence, stale }`. Premier code écrit avec la règle des identifiants français. Critères vérifiés : même suite de contrat (12 tests) sur les lecteurs en mémoire et PostgreSQL ; de bout en bout, événements du journal → worker de projection réel → consultations `ledger_query` (soldes courant et à une date, balance équilibrée, sous-livre paginé, contre-passation, instantané, TPS/TVQ nette), puis lecture identique après reconstruction de `account_balances` ; propriétés FsCheck : la pagination redonne exactement le sous-livre trié pour toute taille de page, le curseur restitue la position. Tests d'architecture étendus (`Query` ne dépend ni de `Command` ni de `Projections`). 720 tests (126 nouveaux, dont 16 d'intégration). Couverture : Query.Application 100 %, Query.Infrastructure 99,0 %, Query.Api 97,8 %. | [ADR 0019](../adr/0019-api-de-requete.md) : structure hexagonale du service de requête et `asOf` = date d'affaires incluse (choix du responsable du projet) ; fraîcheur = âge du premier événement au-delà de la plus petite position des projections lues (`Ledger:Fraicheur:RetardMaximal`, 30 s), repères lus avant les données ; curseur opaque sur (date d'affaires, séquence, ligne). Les clés `Ledger:Authentication:Authority` / `Audience` restent celles de l'API de commandes (configuration de déploiement partagée). Code HTTP dupliqué depuis l'API de commandes (décision de la phase 4a). Branche `phase/04b-api-requete` créée depuis `main` ; le commit de la règle des identifiants français (fait sur `phase/04-projections` après la fusion de 4a) y est repris. | (1) **Point d'arrêt : revue du contrat `query-openapi.json`** (API publique du composant `query`) et de l'ADR 0019 ; **format TPS/TVQ toujours à valider avec le comptable** (ADR 0018). (2) Phase 9 : créer le scope `ledger:read` dans Keycloak ; autorisation par entité (déjà noté en phase 3). (3) Phase 8 : Dockerfile et job CI d'image de l'API de requête (comme celle de commandes), avec ceux du worker. (4) `asOf` agrège toutes les lignes jusqu'à la date : partir du dernier instantané scellé si le volume l'exige (ADR 0019). (5) Fraîcheur approximative (séquence non monotone à la validation, position commune aux partitions) : l'alarme sur le retard du groupe Kafka reste le contrôle (phase 11). |
| 2026-10-21 | 5 | Chaîne complète en conteneurs. `docker-compose.yml` (racine) : PostgreSQL `wal_level=logical`, migration, connexion des rôles (`deploy/local/roles-locaux.sh`), Kafka en KRaft et sujet `ledger.events` (6 partitions, auto-création désactivée), Kafka Connect (image Debezium) et enregistrement du connecteur (`deploy/local/enregistrer_connecteur.py`, image Debezium, sans image supplémentaire), Keycloak avec le realm `ledger` importé (`deploy/local/keycloak/ledger-realm.json` : scopes `ledger:write` / `ledger:read` avec audience `ledger-api`, clients `ledger-local` et `ledger-lecture-local`, secrets lus dans l'environnement), les deux API et le worker ; mots de passe dans `.env` (modèle `.env.example`) ; démonstration `deploy/local/demo.sh`. Connecteur : `deploy/debezium/ledger-outbox.json`, objet JSON plat sans aucun paramètre d'environnement, fusionné avec les seuls paramètres `database.*` d'un environnement (`deploy/local/debezium-local.json`), sans redéfinition possible. Dockerfiles de l'API de requête, du worker et de la migration (même modèle que l'API de commandes). Migration 0008 : publication `ledger_pub` limitée aux insertions, fonction `ledger.purger_outbox(retention, slot)` et rôle `ledger_maintenance`. Les deux API tolèrent des métadonnées OIDC en HTTP en `Development` seulement. Nouveau projet `tests/EndToEnd/Ledger.EndToEnd.Tests` (Testcontainers : PostgreSQL, Kafka, Debezium, Keycloak ; API et worker en processus, jetons émis par Keycloak). Critères vérifiés : `PUT` → outbox → Debezium → Kafka → projection → `GET` solde avec attente bornée sur `throughSequence` ; pendant l'arrêt du conteneur Kafka, 10 écritures sur 10 reçoivent `201`, puis les projections rattrapent ; redémarrages du worker (dont un en plein rattrapage) et relecture complète du sujet (checkpoints remis à zéro) sans double comptage ; versions de chaque flux consécutives dans l'ordre des offsets, une seule partition par entité, solde projeté = `balance_after` du dernier événement ; événements écrits avant le connecteur publiés par le snapshot initial ; message = enveloppe du journal (séquence, hash), clé = entité ; client de lecture : `200` en lecture, `403` en écriture. Suite de bout en bout passée 4 fois de suite après correction de l’attente de publication (≈ 1 min 20). Chaîne compose vérifiée à la main (démo, panne de Kafka). 746 tests (26 nouveaux, dont 16 d'intégration). Couverture inchangée au-dessus des seuils (Command.Api 97,3 %, Query.Api 97,8 %, Migrations 100 %). | [ADR 0020](../adr/0020-chaine-cdc-debezium.md) : configuration invariante + paramètres (pas de `templatefile` : `${routedByValue}` appartient à Debezium) ; `snapshot.mode=initial`, `errors.tolerance=none`, `producer.override.delivery.timeout.ms` maximal ; purge refusée sans slot logique (le WAL d'une insertion non confirmée reste décodable après suppression de la ligne) ; worker Connect unique : `scheduled.rebalance.max.delay.ms=10000` (sinon, après une panne de Kafka, le connecteur reste `UNASSIGNED` 5 min — constaté sous Testcontainers). Règle le point (5) de la phase 2 (purge, correspondance des colonnes) et le point (4) de la phase 4a (format du routeur : valeur = texte JSON de l'enveloppe, rendu `jsonb`, clé = entité). **Écarts au plan** : Dockerfiles du worker, de l'API de requête et de la migration écrits dès la phase 5 (le compose en a besoin) ; leurs jobs CI restent en phase 8. `tests/EndToEnd` est le seul projet de tests sans projet `src/` miroir (`CLAUDE.md` mis à jour). | (1) **Point d'arrêt : démonstration locale de la chaîne complète** (`deploy/local/README.md`) et revue de l'ADR 0020. (2) Phase 7 : module MSK Connect lisant `ledger-outbox.json` ; paramètres Aurora (`verify-full`, certificat racine RDS), mot de passe par fournisseur de configuration Secrets Manager, plugin Debezium de la même version mineure que l'image, création du sujet `ledger.events` (partitions à fixer) ; vérifier que MSK Connect accepte `producer.override.*`. (3) Phase 8 : tâche planifiée de purge (`ledger_maintenance`, rétention à fixer) ; jobs CI d'image du worker, de l'API de requête et de la migration. (4) Phase 11 : alarmes sur l'état du connecteur et de sa tâche, la rétention de WAL du slot `ledger_outbox` et le retard du groupe Kafka. (5) Les images de test (constantes C#) et celles du compose sont alignées à la main ; Dependabot ne suit que le compose. (6) Toujours ouverts : format TPS/TVQ à valider avec le comptable (ADR 0018) ; **P4 : PostgreSQL 18 retenu (voir l'entrée suivante) — vérifier qu'Aurora le propose avant la phase 7**. |
| 2026-10-21 | 5 (images) | Règle ajoutée à `CLAUDE.md` (décision du responsable du projet) : images officielles de l'éditeur uniquement, dernière version stable, tag flottant sur le correctif seulement (ex. `3.7` ; PostgreSQL : version majeure, son second chiffre étant le correctif), variante Alpine si elle existe, sauf .NET qui reste en « chiseled ». Appliquée partout (compose, Dockerfiles, constantes Testcontainers) : `postgres:18-alpine` (PostgreSQL 18, à la place de 17.6), `apache/kafka:4.3.2` (à la place de `confluentinc/cp-kafka:7.9.2` ; tag exact faute de tag flottant publié ; image Alpine), `quay.io/debezium/connect:3.7` (3.7.0.Final vérifiée dans l'image), `quay.io/keycloak/keycloak:26.8`, `mcr.microsoft.com/dotnet/sdk:10.0`, `aspnet` / `runtime:10.0-noble-chiseled`. Compose : volume PostgreSQL monté sur `/var/lib/postgresql` (disposition de la 18), outils Kafka sous `/opt/kafka/bin`. Vérifié : chaîne compose (démo et panne de Kafka) ; 746 tests passent deux fois de suite ; seuils de couverture respectés. | Règle consignée dans `CLAUDE.md` ; ADR 0020 mis à jour (versions, conséquence des tags flottants : un correctif arrive sans commit, vérifié par le test de bout en bout du CI). | (1) **P4 : vérifier la disponibilité de PostgreSQL 18 sur Aurora** (version majeure et réplication logique) avant la phase 7 ; sinon, ADR et retour à la dernière majeure proposée. (2) Avec des tags flottants, deux exécutions peuvent tester des correctifs différents : la version exacte se lit dans les journaux du CI. (3) La règle s'applique aussi aux images des phases suivantes (Lambda, CI). |
| 2026-10-21 | 6 | Socle Terraform (rien n'est appliqué : aucune commande AWS exécutée). `infra/bootstrap` (par compte, appliqué à la main) : bucket `ledger-tfstate-<compte>-<région>` (versionné, SSE-KMS par `alias/ledger-tfstate`, accès public bloqué, TLS obligatoire, accès réservé aux administrateurs déclarés, aux deux rôles du pipeline et à l'utilisateur racine), fournisseur OIDC GitHub, `role/bootstrap/gha-plan` (`ReadOnlyAccess` + fichiers `*.tflock` seulement ; sujet `pull_request`), `role/bootstrap/gha-apply` (`PowerUserAccess` + IAM limité au chemin `/ledger/` sous la frontière `policy/bootstrap/ledger-frontiere-charges`, refus explicites sur l'état et sa clé ; sujet `environment:<env>`), procédure en deux temps (état local puis migration) dans `infra/bootstrap/README.md`. Backends partiels `use_lockfile = true` + `kms_key_id`, sans DynamoDB. Modules `kms` (9 CMK, rotation, administration nommée, usage via `kms:ViaService` ; Logs et Fargate par principal de service avec contexte), `reseau` (VPC /16, sous-réseaux `applications` / `donnees` en /20 sur 3 zones, ni passerelle Internet ni NAT, 8 points de terminaison d'interface + passerelle S3 restreinte, groupe par défaut vide, journaux de flux chiffrés conservés 1 an), `ecr` (`ledger/{command-api,query-api,projections-worker,migrations}`, `IMMUTABLE`, CMK, analyse à la poussée, expiration des seules images sans étiquette). `envs/dev` et `envs/prod` composent les modules (CIDR 10.20/16 et 10.30/16, zones figées) ; verrous des fournisseurs (AWS 6.70.0) pour 4 plateformes. Workflow `infra-ci.yml` : fmt, validate (`-lockfile=readonly`), `terraform test`, TFLint 0.64 (preset `all` + règles AWS 0.49), Checkov 3.3.30, puis plan dev et prod (OIDC `gha-plan`, job **sans** environnement GitHub) publié et mis à jour en commentaire ; plan ignoré tant que `AWS_ACCOUNT_ID_<ENV>` est absente. Critères vérifiés en local : Checkov 0 échec (208 réussis, 12 exceptions justifiées dans le code) ; aucune clé `aws/*` : validations des modules (ARN `key/…` exigé, testées) + vérifications maison `CKV_LEDGER_1` (aucune mention de `alias/aws/`) et `CKV_LEDGER_2` (CMK explicite sur 12 types chiffrables), dont l'échec a été constaté sur un exemple fautif (5 fautes sur 5) ; 15 tests `terraform test` (fournisseur simulé) ; TFLint propre ; actionlint propre. | [ADR 0011](../adr/0011-etat-terraform-verrou-s3-natif.md) (un bucket par compte, noms reconstruits depuis le numéro de compte : GitHub ne stocke que `AWS_REGION`, `AWS_ACCOUNT_ID_DEV`, `AWS_ACCOUNT_ID_PROD`, en variables), [ADR 0021](../adr/0021-identite-du-pipeline-oidc.md) (pas d'`AdministratorAccess` ; frontière de permissions), [ADR 0022](../adr/0022-cmk-par-domaine.md) (politique des CMK), [ADR 0023](../adr/0023-reseau-prive-sans-sortie-internet.md) (pas de NAT). **Écarts au plan** : module `network` nommé `reseau` ; `.checkov.yaml` à la racine (pour que `checkov -d infra` charge les vérifications maison) ; TFLint exige `--config` (commandes de `CLAUDE.md` mises à jour). | (1) **Point d'arrêt** : amorçage **appliqué dans le compte dev** le 2026-10-22 (état migré dans le bucket) ; **compte prod : amorçage reporté à la toute fin des phases** (décision du responsable du projet, 2026-10-23). D'ici là, `AWS_ACCOUNT_ID_PROD` reste absente et le plan prod s'ignore ; les phases 7 à 11 sont vérifiées sur dev seulement, et tout ce qui vise prod (apply et approbation de `prod` en phase 10, reprise après sinistre et intégrité en phase 11) reste écrit mais non exercé jusqu'à cet amorçage. Le responsable du projet crée aussi les variables de dépôt et les environnements GitHub `dev` / `prod` (relecteurs obligatoires pour `prod`, branche `main`). (2) Critère « plan en commentaire de PR » : premier essai sur la PR #19 (2026-10-23) : `AssumeRoleWithWebIdentity` refusé, le dépôt émettant des sujets OIDC immuables (`repo:Xychus@153566096/ComptabiliteApp@1422798619:…`) ; amorçage corrigé (`id_organisation_github`, `id_depot_github`, ADR 0021) et réappliqué dans le compte dev ; **critère constaté le 2026-10-23** : commentaire « Terraform plan — dev ✅ », `Plan: 57 to add, 0 to change, 0 to destroy` (kms 18, reseau 31, ecr 8), plan prod ignoré faute de variable. Corrigé aussi : le plan prod retombait sur le compte dev quand `AWS_ACCOUNT_ID_PROD` est absente (`cond && a || b`), il lit maintenant `vars[matrix.variable_compte]`. (2b) **Rien n'est appliqué en dev avant la phase 7** : le plan prévoyait l'apply en phase 10 alors que les points d'arrêt des phases 7 à 9 exigent « apply en dev par le pipeline ». Décision du responsable du projet (2026-10-23) : workflow `infra-apply.yml` (dev seulement) = première tâche de la phase 7 ; la phase 10 le réutilise et y ajoute prod. Plan mis à jour. (3) Ne pas rendre `infra-ci` obligatoire dans le ruleset tant qu'il est filtré par chemins (un check requis jamais lancé bloque la PR). (4) Phase 7 : la politique du bucket d'archive doit refuser `s3:GetObject` à `gha-plan` (`ReadOnlyAccess`) ; ajouter MSK Connect au catalogue de la clé `msk` et le point de terminaison `kafka` si nécessaire ; groupes de sécurité Aurora / MSK. (5) Phase 8 : resserrer la condition Fargate de la clé `ecs` sur le nom exact de la grappe ; ajouter les rôles sous `/ledger/` avec la frontière ; élargir la frontière si besoin (PR sur l'amorçage). (6) Phase 9 : accès de l'autorizer aux JWKS Keycloak sans NAT (ADR 0023) ; dépôt ECR `authorizer` si la Lambda est en image (P3). (7) P6 toujours ouvert : numéros de comptes et URL du realm Keycloak (variables, non renseignées). |
