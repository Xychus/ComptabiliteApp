# 0024 — Kafka autogéré sur ECS (EC2 + EBS), mTLS, Debezium en conteneur

- **Statut** : accepté (révisé le 2026-10-25, phase 8 : voir « Révision »)
- **Date** : 2026-10-23
- **Phase** : 6 (décision anticipée ; mise en œuvre en phase 8)
- **Remplace** : ADR 0009 (MSK avec authentification IAM), jamais rédigé

## Contexte

Le plan prévoyait Amazon MSK et MSK Connect. Le responsable du projet veut l'indépendance du
transport d'événements vis-à-vis d'AWS (2026-10-23) : la même image, la même configuration et
la même authentification doivent pouvoir tourner ailleurs. Kafka n'est qu'un transport
(ADR 0007) : le journal PostgreSQL reste la source de vérité et les projections se reconstruisent
depuis lui (ADR 0016) ; une perte dans Kafka se répare, mais doit rester exceptionnelle.

## Décision

1. **Image officielle** `apache/kafka` (la même qu'en local et en test), en **KRaft**, trois
   nœuds combinés contrôleur + broker, un par zone de disponibilité, dans les sous-réseaux
   `donnees`.
2. **ECS sur EC2** : un groupe de capacité (une instance par zone) dans la grappe ECS ; un
   service ECS par nœud (`node.id` stable, nom DNS stable par Service Connect / Cloud Map).
   Données sur un **volume EBS persistant par nœud**, chiffré par la CMK `kafka` (domaine
   renommé depuis `msk`, utilisable via EC2), qui survit au remplacement de la tâche et de
   l'instance. Facteur de réplication 3, `min.insync.replicas = 2`, remplacement d'un seul nœud
   à la fois.
3. **mTLS** pour tout le trafic (clients et entre brokers), certificats de la PKI interne
   (ADR 0010), valables 7 jours ; brokers rechargés à chaud, clients redémarrés à la rotation.
   **ACL** par identité de certificat : Debezium écrit `ledger.events` et ses sujets internes,
   le worker de projections lit `ledger.events` dans son groupe ; refus par défaut
   (`allow.everyone.if.no.acl.found = false`).
4. **Debezium** : image officielle `quay.io/debezium/connect` en service ECS (Fargate), même
   connecteur invariant qu'en local (`deploy/debezium/ledger-outbox.json`, ADR 0020), paramètres
   d'Aurora fournis par l'environnement, mot de passe par fournisseur de configuration (secret
   injecté). Plus de MSK Connect.
5. **Images sans sortie Internet** (ADR 0023) : les images officielles sont servies par des
   règles de cache *pull-through* ECR (Docker Hub pour `apache/kafka`, Quay pour Debezium),
   chiffrées par la CMK `ecr` ; étiquettes exactes, jamais `latest`.
6. Placement : **phase 8**, après la PKI et la grappe ECS ; la phase 7 garde Aurora, les
   migrations et l'archive.

## Conséquences

- Le transport est portable : seules l'orchestration (ECS) et le stockage (EBS) restent propres
  à AWS.
- À notre charge : montées de version nœud par nœud, surveillance (sous-réplication, espace
  disque, retard des groupes), runbooks de remplacement d'un nœud et de perte du quorum.
- La rotation des certificats tous les 7 jours redémarre Debezium et le worker : sans perte
  (offsets et checkpoints), mais à surveiller.
- La clé `kafka` devra aussi autoriser le rôle lié au service d'Auto Scaling pour les volumes
  chiffrés des instances (phase 8, une fois le rôle créé).
- La frontière de permissions de l'amorçage perd ses droits MSK (`kafka:*`, `kafka-cluster:*`) :
  réappliquer l'amorçage en dev.
- Le worker de projections et la configuration de Connect devront passer de `PLAINTEXT` à `SSL`
  avec certificat client (la chaîne locale pourra adopter le même mode pour la parité).

## Options écartées

- **Amazon MSK + MSK Connect** (ADR 0009 prévu) : dépendance à AWS pour le transport.
- **Fargate avec le stockage de la tâche** : disque perdu à chaque remplacement ; perdre deux
  nœuds simultanément fait perdre la grappe.
- **EC2 sans ECS** : un second modèle d'exploitation à côté d'ECS.
- **SASL/SCRAM** : mots de passe à synchroniser entre Secrets Manager et Kafka ; le mTLS réutilise
  la PKI interne et donne une identité forte par client.

## Révision (2026-10-25, phase 8)

1. **Instances EC2 gérées par Terraform, sans groupe Auto Scaling ni groupe de capacité** : un
   volume EBS persistant doit être rattaché à une instance stable (`aws_volume_attachment`) ; un
   groupe Auto Scaling exigerait un mécanisme de rattachement au démarrage. Remplacer un nœud est
   une opération manuelle, un nœud à la fois (`docs/runbooks/kafka-remplacement-noeud.md`). Le
   rôle lié au service d'Auto Scaling n'a donc pas à être autorisé sur la clé `kafka`.
2. **Tâches en réseau `awsvpc`** (IMDS bloqué pour les tâches, `ECS_AWSVPC_BLOCK_IMDS`) et noms
   stables publiés par **Cloud Map** (`kafka-N.kafka.<prefixe>.internal`) ; pas de Service Connect.
3. **Écouteurs nommés `MTLS` et `CONTROLLER`**, tous deux en SSL, **TLS 1.3 seul**, keystore et
   truststore au format PEM écrits par la sentinelle ; un écouteur nommé `SSL` déclenche dans le
   script de l'image l'exigence de keystores JKS protégés par mot de passe.
4. **Rotation des certificats des brokers par redémarrage ordonné** (ADR 0010, révision) au lieu
   du rechargement à chaud.
5. **Images copiées dans ECR par le CI** (ADR 0028) au lieu du cache *pull-through* : celui de
   Docker Hub exige un secret chiffré par `aws/secretsmanager`.
6. Sujet `ledger.events` : 6 partitions, réplication 3, `min.insync.replicas = 2`, créé avec les
   ACL par la tâche d'initialisation (identité `kafka-admin`). Mesures et alarmes publiées par les
   sentinelles (`Ledger/Kafka` : nœuds présents, sous-réplication, disque, retard des groupes).
