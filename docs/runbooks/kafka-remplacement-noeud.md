# Kafka : remplacement d'un nœud, redémarrage, perte du quorum

ADR 0024 (révision de la phase 8). Trois nœuds KRaft combinés, un par zone, chacun avec son
instance EC2 et son volume EBS persistant (`/donnees/kafka`). Réplication 3,
`min.insync.replicas = 2` : un nœud peut être absent sans interruption ; deux, non.

**Règle d'or : un seul nœud à la fois, et seulement si la grappe est saine** (alarmes
`*-noeuds-absents` et `*-partitions-sous-repliquees` au repos, métriques `Ledger/Kafka`).

## Redémarrer un nœud (tâche seulement)

```bash
aws ecs update-service --cluster ledger-<env> --service kafka-2 --force-new-deployment
```

La tâche s'arrête puis redémarre sur la même instance avec le même volume. Attendre
`PartitionsSousRepliquees = 0` avant tout autre nœud. Les sentinelles font de même après chaque
rotation de certificat, chacune dans son créneau horaire.

## Remplacer l'instance d'un nœud (AMI, panne matérielle)

1. Vérifier que la grappe est saine.
2. Lancer `infra-apply` à la demande avec le remplacement ciblé (à exécuter par le pipeline,
   jamais à la main) :
   `terraform apply -replace='module.kafka.aws_instance.noeud["kafka-2"]'`.
   Le volume est détaché, l'instance recréée (AMI recommandée courante), le volume rattaché et
   monté par le script de démarrage ; la tâche ECS revient sur la nouvelle instance.
3. Attendre `NoeudsPresents = 3` et `PartitionsSousRepliquees = 0`, puis passer au nœud suivant.

## Perte d'un volume de données

Le nœud repart vide (`CLUSTER_ID` identique) et se resynchronise depuis les deux autres
répliques. Kafka n'est qu'un transport (ADR 0007) : en dernier recours, les projections se
reconstruisent depuis le journal (ADR 0016).

## Perte du quorum (deux nœuds ou plus)

Le contrôleur KRaft est indisponible : Debezium et le worker attendent (aucune perte, le slot
logique d'Aurora retient le WAL, alarme `OldestReplicationSlotLag`). Rétablir les nœuds un par
un ; si un volume est perdu pendant que le quorum l'est aussi, reconstruire la grappe (nouveaux
volumes), relancer la tâche d'initialisation (`execution.initialisation_kafka`), puis le
connecteur repart du slot (offsets perdus : snapshot `initial` ignoré, le slot fait foi) et les
projections se rejouent depuis leurs checkpoints en base.

## À constater au point d'arrêt de la phase 8

Remplacement d'un nœud en dev, avec Debezium et le worker actifs : aucune perte d'événement,
retard résorbé. Consigner les durées observées ici.
