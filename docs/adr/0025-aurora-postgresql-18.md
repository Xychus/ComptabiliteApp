# 0025 — Aurora PostgreSQL 18 : paramètres, secret maître, migrations en tâche ECS

- **Statut** : proposé
- **Date** : 2026-10-25
- **Phase** : 7

## Contexte

Le journal comptable vit dans PostgreSQL (ADR 0007). Le point P4 du plan demandait de fixer la
version d'Aurora PostgreSQL (dernière version majeure, avec réplication logique) et de vérifier
le comportement des slots logiques au basculement. Contraintes : TLS partout, CMK par domaine
(ADR 0022), aucun secret en clair, rotation des secrets tous les 7 jours (CLAUDE.md), réseau sans
sortie Internet (ADR 0023). Les migrations (DbUp) doivent s'exécuter dans le VPC, avant chaque
déploiement.

État vérifié le 2026-10-25 dans la documentation AWS :

- Aurora PostgreSQL 18 est publié depuis le 2026-06-11 (calendrier des versions Aurora, à partir
  de 18.3) ; la dernière mineure est 18.6 (correctif 18.6.1 du 2026-10-15). Fin du support
  standard : 2031-02-28. PostgreSQL 18 est aussi la version des tests (`postgres:18-alpine`).
- `rds.logical_replication = 1` (paramètre de grappe, statique) donne `wal_level = logical`.
- Le décodage logique depuis un lecteur n'est pas pris en charge par Aurora : le slot vit sur le
  rédacteur.
- **La documentation ne dit pas explicitement si un slot logique survit au basculement.** Le
  stockage d'Aurora est partagé par les instances, ce qui le laisse attendre, mais ce n'est pas
  écrit.

## Décision

1. **Version** : Aurora PostgreSQL **18.6**, famille de paramètres `aurora-postgresql18`. Montée
   de version mineure par PR (`version_moteur`), d'abord appliquée en dev ;
   `auto_minor_version_upgrade = false`, `allow_major_version_upgrade = false`. Une montée de
   version majeure exige de supprimer les slots (documentation AWS) : elle fera l'objet d'un
   runbook.
2. **Paramètres de grappe** : `rds.force_ssl = 1`, `ssl_min_protocol_version = TLSv1.2`,
   `rds.logical_replication = 1`, journalisation des connexions, du DDL (`log_statement = ddl`)
   et des requêtes de plus d'une seconde. Pas de `log_statement = all` : les valeurs des écritures
   comptables et le texte des `ALTER ROLE … PASSWORD` finiraient dans les journaux.
3. **Topologie** : un rédacteur et au moins un lecteur (cible de basculement, ordre de promotion
   explicite), sous-réseaux `donnees`, aucune adresse publique, autorité de certification
   `rds-ca-rsa2048-g1`. Groupe de sécurité sans règle : **chaque client ajoute sa propre règle
   d'entrée** (5432 depuis son groupe), le module Aurora ne connaît pas ses clients.
4. **Chiffrement** : stockage et Performance Insights par la CMK `aurora` ; journaux PostgreSQL
   exportés dans un groupe créé d'avance (CMK `logs`, un an) ; sinon RDS le créerait sans CMK.
5. **Mot de passe maître géré par RDS** dans Secrets Manager (`manage_master_user_password`), chiffré
   par la CMK `secrets`. RDS le renouvelle automatiquement **tous les 7 jours** (période par
   défaut), sans Lambda de rotation à maintenir. Authentification IAM activée sur la grappe, en
   prévision des rôles applicatifs (phase 8).
6. **Sauvegardes** : 35 jours (PITR), protection contre la suppression, instantané final.
7. **Alarmes** CloudWatch : volume utilisé (grappe) ; stockage local libre, connexions,
   `OldestReplicationSlotLag` et `ReplicationSlotDiskUsage` (chaque instance, puisque le
   rédacteur change au basculement). Les actions (sujet SNS) arrivent en phase 11.
8. **Migrations en tâche ECS Fargate ponctuelle** (module `migrations`) : rôle maître (propriétaire
   du schéma). Utilisateur et mot de passe sont injectés comme **secrets ECS** depuis les clés JSON
   du secret géré par RDS. Hôte et base sont passés en clair. `Ledger.Migrations` compose la chaîne
   en `SSL Mode=VerifyFull`, avec le paquet de certificats racines RDS intégré à l'image (contenu
   public, épinglé par empreinte SHA-256 dans le `Dockerfile`). Pas de rôle de tâche ; le rôle
   d'exécution ne lit que ce secret, à travers Secrets Manager. Sorties réseau : 5432 vers Aurora,
   443 vers les points de terminaison et S3. La grappe ECS (Fargate seulement) est créée dès la
   phase 7 ; la phase 8 l'étend.
9. **P4, basculement des slots** : à **constater** au point d'arrêt de la phase 8 (basculement
   forcé en dev pendant que Debezium tourne, observation du slot et de la reprise), puis à
   consigner dans le runbook. En cas de perte du slot, le connecteur repart d'un nouveau snapshot.
   L'outbox n'est purgée que si le slot existe (ADR 0020), donc rien n'est perdu ; les doublons sont
   absorbés par l'idempotence des projections (ADR 0015).

## Conséquences

- Une connexion sans TLS est refusée par le serveur ; tout client doit fournir le certificat
  racine RDS pour `VerifyFull`. Debezium (phase 8) aussi.
- Le paquet de certificats intégré à l'image doit être mis à jour quand AWS le change : le build
  échoue sur l'empreinte, ce qui force une PR visible.
- Le mot de passe maître change tous les 7 jours : les tâches le relisent à chaque lancement
  (secrets ECS). Aucun service de longue durée ne doit utiliser le rôle maître.
- Coût en dev : deux instances `db.r7g.large` en continu (classe surchargeable par environnement).
- Les migrations ne s'exécutent qu'à partir de la phase 10 : aucune image n'est publiée avant
  `deploy.yml` (décision du responsable du projet, 2026-10-25). La définition de tâche référence
  `ledger/migrations:<version>`, fournie par le déploiement.

## Options écartées

- **Aurora PostgreSQL 17** : support plus court, et les tests tournent déjà en 18.
- **Rotation par Lambda (modèle Secrets Manager)** pour le rôle maître : une fonction de plus à
  maintenir dans le VPC, alors que RDS le fait nativement.
- **Chaîne de connexion complète dans un secret dédié** : un second secret à garder synchronisé
  avec celui que RDS renouvelle.
- **Montées de version mineures automatiques** : elles s'appliqueraient en prod sans passer par dev.
- **Surveillance améliorée** : elle écrit dans le groupe `RDSOSMetrics`, partagé et hors des CMK du
  projet ; Performance Insights et les métriques CloudWatch suffisent.
