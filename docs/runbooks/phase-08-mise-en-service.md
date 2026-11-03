# Mise en service de la phase 8 en dev

Ordre des opérations au point d'arrêt. Claude Code n'exécute rien de tout cela : l'administrateur
et le pipeline s'en chargent.

1. **Amorçage** (administrateur) : réappliquer `infra/bootstrap` (frontière de permissions élargie,
   profils d'instance pour `gha-apply`, refus IAM sauf passage d'un rôle à ECS).
2. **Fusion de la PR** → `infra-apply` : PKI (secrets, rôle), Kafka (instances, volumes, sans
   service), réseau, équilibreur, observabilité. Les services restent absents (versions nulles).
3. **`images.yml`** (à la demande) : publication de nos images et copie des images tierces.
4. **PR « versions »** : `versions_images = { pki, command, query, projections, migrations }`,
   `version_image_migrations` (même version), `generations_intermediaire = ["2026"]` → apply :
   Lambda PKI en mode cérémonie ; nœuds Kafka et services en échec de démarrage tant que leurs
   certificats n'existent pas (attendu jusqu'à l'étape 5).
   **Lancer aussitôt la tâche de migrations** (sortie `migrations`) : elle crée les rôles, dont
   `debezium`. La première rotation du mot de passe de Debezium, déclenchée par l'apply, échoue si
   le rôle n'existe pas encore : la relancer ensuite avec
   `aws secretsmanager rotate-secret --secret-id ledger-dev/postgres/debezium`.
5. **Cérémonie** (`pki-ceremonie-annuelle.md`, étapes 1 à 5) → apply : certificats émis, nœuds
   Kafka démarrés (vérifier `NoeudsPresents = 3`).
6. **Initialisation de Kafka** : lancer la tâche `execution.initialisation_kafka` (run-task, réseau
   donné en sortie) ; journal : sujet `ledger.events` créé, ACL listées.
7. **Migrations** (avance sur la phase 10, faites à l'étape 4) : vérifier `ledger_pub`, les rôles
   et `rds_iam` (migration 0009) ; le test de fumée « connexion sans TLS refusée » peut être fait
   ici plutôt qu'en phase 10.
8. **Debezium** démarre, enregistre le connecteur ; **critère** : une écriture via l'API de
   commandes apparaît dans `ledger.events` et dans les projections.
9. **Critères d'acceptation** : client sans certificat valide refusé (test d'intégration
   `KafkaMtlsTests` + essai en dev avec un certificat d'une autre racine) ; aucune task definition
   en `latest` ni secret en clair (`terraform test`, Checkov) ; trafic en TLS partout.
10. **Essais de résilience** : `aurora-basculement-slot.md`, `kafka-remplacement-noeud.md`.
