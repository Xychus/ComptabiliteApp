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
- [ ] `PUT /v1/journal-entries/{entryId}` (création idempotente, `entryId` UUIDv7 fourni par le client)
- [ ] `POST /v1/journal-entries/{entryId}/reversals`
- [ ] `POST /v1/periods/{period}/open`, `POST /v1/periods/{period}/seal` (avec `expectedVersion`)
- [ ] `PUT /v1/accounts/{accountId}` (plan comptable)
- [ ] Réponses : `201`/`200` avec `entryId`, `sequenceNumber`, `recordedAt`, `period`, `hash` ;
      `422` (règle métier, ligne fautive indiquée), `409` (conflit, période scellée), `400`, `401`, `403`
- [ ] Problem Details (RFC 9457) pour toutes les erreurs
- [ ] Champs serveur (`sequenceNumber`, `recordedAt`, `period`, `hash`) ignorés en entrée
- [ ] Authentification JWT (Keycloak) en défense en profondeur (`AddJwtBearer`, audience `ledger-api`,
      scope `ledger:write`) ; `actor` = `azp` du jeton
- [ ] Génération de `docs/api/openapi.yaml` au build ; test qui échoue si le fichier versionné diffère
- [ ] Health checks (`/health/live`, `/health/ready`) ; journalisation structurée ; OpenTelemetry
- [ ] Dockerfile : image `aspnet:10.0`, utilisateur non root, système de fichiers en lecture seule
- [ ] Tests d'intégration API (`WebApplicationFactory` + Testcontainers PostgreSQL)

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
- [ ] Worker de projection : consommateur Kafka (Confluent.Kafka), traitement par partition,
      checkpoint transactionnel (`projection_checkpoints`) — modèle de lecture et checkpoint
      mis à jour dans la même transaction
- [ ] Projections (une classe chacune, `IEventHandler<T>`) : `account_balances`, `ledger_entries`
      (grand livre et sous-livres), `tax_projection` (TPS/TVQ collectées / payées par période)
- [ ] Snapshots de fin de période (`period_snapshots`), figés au scellement
- [ ] Upcasting des événements à la lecture (infrastructure prête, même si `schema_version = 1` seulement)
- [ ] Outil de **rejeu** : reconstruire une projection dans une table parallèle à partir de la
      table `events` (curseur sur `global_sequence`), puis bascule (blue/green)
- [ ] API de requête : `GET /v1/accounts/{id}/balance?asOf=`, `GET /v1/trial-balance?asOf=`,
      `GET /v1/accounts/{id}/entries` (pagination par curseur), `GET /v1/tax/returns/gst-qst?period=`,
      `GET /v1/journal-entries/{id}`, `GET /v1/periods/{period}` ; scope `ledger:read`
- [ ] Indicateur de fraîcheur dans les réponses (`throughSequence`, `stale` si le retard dépasse un seuil)
- [ ] Tests : idempotence (même événement livré deux fois → effet unique), rejeu (même résultat
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
- [ ] `docker-compose.yml` de développement : PostgreSQL (réplication logique), Kafka, Kafka
      Connect + Debezium PostgreSQL, Keycloak (realm de test importé), services .NET
- [ ] Configuration du connecteur Debezium (outbox + `EventRouter`, clé = `partition_key`)
      versionnée dans `deploy/debezium/`, **réutilisée telle quelle** en phase 7
- [ ] Test de bout en bout (Testcontainers) : `PUT` écriture → outbox → Debezium → Kafka →
      projection → `GET` solde, avec attente bornée sur `throughSequence`
- [ ] Tests de résilience : arrêt de Kafka pendant des écritures (les écritures continuent,
      rattrapage au retour) ; redémarrage du worker de projection (pas de double comptage)
- [ ] Vérification de l'ordre : événements d'un même flux toujours appliqués dans l'ordre

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
- [ ] `infra/bootstrap/` (appliqué une fois, manuellement, par un administrateur) : bucket d'état
      S3 (versionné, SSE-KMS CMK, accès public bloqué), CMK de l'état, fournisseur OIDC GitHub,
      rôles IAM `gha-plan` (lecture seule) et `gha-apply` (limité à l'environnement GitHub protégé)
- [ ] Backend S3 avec `use_lockfile = true` et `kms_key_id` (ADR 0011)
- [ ] Module `kms` : CMK + alias, rotation activée, politique au moindre privilège (conditions
      `kms:ViaService`), une clé par domaine : `aurora`, `msk`, `s3-archive`, `secrets`, `logs`,
      `ecr`, `ecs`, `lambda`, `ssm`
- [ ] Module `network` : VPC, sous-réseaux privés sur 3 AZ (pas de sous-réseau public sauf NAT si
      nécessaire), groupes de sécurité, **VPC endpoints** (KMS, Secrets Manager, SSM, ECR api/dkr,
      S3 gateway, CloudWatch Logs, STS, execute-api), journaux de flux VPC chiffrés CMK
- [ ] Module `ecr` : un dépôt par service, `IMMUTABLE`, chiffrement KMS CMK, scan à la poussée
- [ ] `infra/envs/dev` et `infra/envs/prod` composant les modules
- [ ] Workflow `infra-ci.yml` (sur PR touchant `infra/`) : `fmt -check`, `validate`, TFLint,
      Checkov, `terraform test` sur les modules, `terraform plan` (rôle `gha-plan`) publié en commentaire
- [ ] ADR 0011

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
Vérifie d'abord le point P4. N'exécute aucune commande qui modifie AWS.
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
  - [ ] `dev` : `terraform apply` → migrations → déploiement ECS → tests de fumée
  - [ ] `prod` : approbation manuelle (environnement protégé) → même artefact → mêmes étapes →
        retour arrière automatique sur alarme
- [ ] Détection des changements incompatibles sur PR : `oasdiff` sur `openapi.yaml`, vérification
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
