# 0020 — Chaîne CDC : connecteur Debezium invariant, routeur d'outbox, purge et environnement local

- **Statut** : proposé
- **Date** : 2026-10-21
- **Phase** : 5

## Contexte

L'outbox (ADR 0007) est écrite dans la transaction de chaque événement ; le worker de projections
(ADR 0015) attend dans Kafka l'enveloppe `EventEnvelopeV1` en JSON, avec l'entité pour clé.
Il restait à relier les deux par Debezium, d'une façon qui :

- fonctionne à l'identique en local (Kafka Connect) et sur MSK Connect (phase 7), où la
  configuration d'un connecteur est une table clé → valeur fournie par Terraform ;
- ne perde ni ne saute aucun événement, y compris ceux écrits avant la création du connecteur,
  pendant une panne de Kafka ou au redémarrage de Connect ;
- garde l'ordre des événements d'un même flux ;
- permette de purger l'outbox (point ouvert de la phase 2).

## Décision

1. **Configuration en deux parties.** `deploy/debezium/ledger-outbox.json` contient tout ce qui
   ne dépend pas de l'environnement, sous forme d'objet JSON plat (le corps de
   `PUT /connectors/{nom}/config`, et la table `connector_configuration` de MSK Connect via
   `jsondecode(file(...))`, **sans** `templatefile` : la valeur `${routedByValue}` appartient à
   Debezium). Chaque environnement fournit seulement les paramètres `database.hostname`,
   `database.port`, `database.dbname`, `database.user`, `database.password`,
   `database.sslmode` (et `database.sslrootcert` en AWS). La fusion **refuse** qu'un paramètre
   redéfinisse une clé invariante (`deploy/local/enregistrer_connecteur.py`, et la même règle
   dans les tests). Le mot de passe n'est jamais une valeur littérale : référence de fournisseur
   de configuration (`${env:…}` en local, Secrets Manager sur MSK Connect).
2. **Routeur d'outbox** (`io.debezium.transforms.outbox.EventRouter`) : identifiant =
   `event_id`, clé = `partition_key` (l'entité), valeur = `payload` (l'enveloppe, non
   développée : `table.expand.json.payload=false`), sujet = colonne `topic` (`ledger.events`).
   Convertisseurs `StringConverter` pour la clé et la valeur : le message Kafka est le texte
   JSON de l'enveloppe (rendu `jsonb`, clés réordonnées — sans effet, le hash porte sur la forme
   canonique JCS, ADR 0006). Le sujet est créé par l'infrastructure (pas d'auto-création).
3. **Aucune perte.** `snapshot.mode=initial` : au premier démarrage, les lignes déjà présentes
   dans l'outbox sont publiées, puis la diffusion reprend au slot. `errors.tolerance=none` :
   une erreur arrête la tâche plutôt que d'ignorer un message. `producer.override.delivery.timeout.ms`
   au maximum : pendant une panne de Kafka, le producteur attend au lieu d'abandonner l'envoi
   (la tâche échouerait au bout de 2 min par défaut). La publication `ledger_pub` et le slot
   `ledger_outbox` sont nommés ; la publication est créée par les migrations
   (`publication.autocreate.mode=disabled`, le rôle `debezium` n'a besoin que de `SELECT`).
   La livraison est « au moins une fois » : Connect republie ce qui suit son dernier offset
   validé après un redémarrage ; les consommateurs sont idempotents par `event_id` (ADR 0015).
4. **Ordre.** Tous les événements d'une entité ont la même clé, donc la même partition ;
   Debezium les lit dans l'ordre de validation du WAL ; le worker traite une partition dans
   l'ordre de ses offsets. Les versions d'un flux (toujours propre à une entité) sont donc
   publiées et appliquées dans l'ordre.
5. **Purge de l'outbox** (migration 0008). La publication ne publie plus que les insertions
   (`publish = 'insert'`) : un `DELETE` ne produit aucun message. La fonction
   `ledger.purger_outbox(retention, slot)` (`SECURITY DEFINER`, exécutable par le seul rôle
   `ledger_maintenance`) supprime les lignes plus anciennes que la rétention (au moins une heure)
   et **refuse** si le slot logique n'existe pas : tant que le slot existe, le WAL de toute
   insertion non confirmée est conservé et reste décodable après la suppression de la ligne ;
   sans slot, le snapshot initial a encore besoin des lignes. La planification (tâche ECS)
   arrive en phase 8.
6. **Environnement local** (`docker-compose.yml`, `deploy/local/`) : PostgreSQL
   18 `wal_level=logical`, Apache Kafka 4.3 en KRaft, Kafka Connect (image Debezium `3.7`),
   Keycloak `26.8` avec le realm `ledger` importé (scopes `ledger:write` et `ledger:read` avec l'audience
   `ledger-api`, un client d'écriture et lecture, un client de lecture seule ; secrets lus dans
   l'environnement), les deux API, le worker et la migration. Mots de passe dans `.env`, jamais
   versionné. Les API n'acceptent des métadonnées OIDC en HTTP qu'en environnement
   `Development` (`RequireHttpsMetadata = !IsDevelopment()`) ; partout ailleurs, HTTPS reste exigé.
   Les images du worker, de l'API de requête et de la migration ont leur Dockerfile (même modèle
   que l'API de commandes) ; leurs jobs CI et la publication restent en phases 8 et 10.
7. **Worker Connect unique en local** : `scheduled.rebalance.max.delay.ms=10000`. Par défaut,
   après la perte du groupe (panne de Kafka), Connect attend 5 minutes le retour d'un autre
   worker avant de réaffecter le connecteur ; avec un seul worker, l'attente ne sert à rien.
8. **Test de bout en bout** (`tests/EndToEnd`) : les mêmes fichiers (connecteur, paramètres
   locaux, realm) sur des conteneurs Testcontainers, les API et le worker en processus avec leur
   composition réelle et des jetons émis par Keycloak. Il vérifie la chaîne `PUT` → `GET` avec
   attente bornée sur `throughSequence`, le snapshot initial, le format du message, l'ordre par
   flux, la panne de Kafka (`201` pendant la panne, rattrapage ensuite) et le redémarrage ou la
   relecture complète par le worker (aucun double comptage).

## Conséquences

- Phase 7 : le module MSK Connect lit le même fichier ; il fournit les paramètres d'Aurora
  (`database.sslmode=verify-full`, certificat racine RDS), le mot de passe par le fournisseur de
  configuration Secrets Manager, le plugin Debezium de la même version mineure (`3.7`), et crée le sujet
  `ledger.events` (nombre de partitions à fixer). Vérifier que MSK Connect accepte les
  surcharges `producer.override.*` (sinon : configuration de worker personnalisée).
- Le slot `ledger_outbox` retient le WAL tant que Debezium ne confirme pas : une panne prolongée
  du connecteur fait croître le stockage d'Aurora. Alarmes à prévoir en phase 11 : état du
  connecteur et de sa tâche, âge du slot (`pg_replication_slots`), retard du groupe Kafka.
- Sur MSK Connect, le comportement de rééquilibrage est géré par AWS : un connecteur en échec
  doit être détecté par l'alarme ci-dessus.
- Images officielles, dernière version stable, tag flottant sur le correctif (règle de
  `CLAUDE.md`) : un correctif de Debezium, Keycloak ou PostgreSQL arrive sans commit et est
  vérifié par le test de bout en bout à chaque exécution du CI. Un changement de version mineure
  ou majeure passe par un commit et rejoue ce test.

## Options écartées

- **Fichier de configuration à gabarit** (`templatefile`, `envsubst`) : la syntaxe `${…}` entre en
  collision avec `${routedByValue}` de Debezium et les références des fournisseurs de
  configuration ; la fusion d'un fichier invariant et de paramètres est plus simple à vérifier.
- **`snapshot.mode=no_data`** : les événements écrits avant la création du slot seraient perdus
  pour Kafka (seul un rejeu des projections les rattraperait).
- **Suppression de la ligne d'outbox dans la transaction d'écriture** (motif recommandé par
  Debezium) : exigerait `DELETE` pour le rôle applicatif et rendrait le snapshot initial inutile ;
  la purge différée garde l'outbox lisible pour le diagnostic.
- **Keycloak en HTTPS en local** : certificat auto-signé à faire approuver par les images
  « chiseled » (sans magasin modifiable) ; la tolérance limitée à `Development` est plus simple et
  ne change rien en AWS.
