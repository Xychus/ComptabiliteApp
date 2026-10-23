# 0023 — Réseau : VPC privé sans sortie Internet, points de terminaison VPC

- **Statut** : proposé
- **Date** : 2026-10-21
- **Phase** : 6

## Contexte

Aucune ressource ne doit être publique, et les services AWS doivent être joints par des
points de terminaison VPC (CLAUDE.md). Le plan autorise un sous-réseau public « seulement pour
un NAT si nécessaire ». Les charges prévues (ECS Fargate, Aurora, MSK, MSK Connect, autorizer
Lambda) n'ont besoin, aux phases 7 et 8, que de services AWS.

## Décision

1. Module `infra/modules/reseau` : un VPC (`/16`), **deux niveaux de sous-réseaux privés sur
   trois zones** — `applications` (ECS, Lambda, points de terminaison) et `donnees` (Aurora,
   MSK) —, chacun un `/20`. Aucune adresse publique, **aucune passerelle Internet, aucun NAT**,
   une table de routage par niveau avec la seule route locale (plus la passerelle S3).
2. Points de terminaison d'interface, DNS privé, dans les trois sous-réseaux `applications` :
   `kms`, `secretsmanager`, `ssm`, `ecr.api`, `ecr.dkr`, `logs`, `sts`, `execute-api`. Groupe
   de sécurité : HTTPS (443) depuis le CIDR du VPC seulement.
3. Passerelle S3 avec une politique limitée aux buckets du compte (`aws:ResourceAccount`) et
   au bucket des couches d'images ECR de la région (lecture).
4. Groupe de sécurité par défaut vidé de toute règle.
5. Journaux de flux VPC (tout le trafic, agrégation 60 s) dans un groupe CloudWatch chiffré par
   la CMK `logs`, conservés au moins un an ; rôle `/ledger/` sous la frontière de permissions.
6. Zones de disponibilité **figées** en variable de l'environnement
   (`ca-central-1a`, `-1b`, `-1d`), pas lues dynamiquement.

## Conséquences

- Pas de chemin vers Internet : une image, une dépendance ou un service tiers non joint par un
  point de terminaison est inaccessible. C'est le comportement voulu.
- **Phase 9** : l'autorizer Lambda doit obtenir les clés publiques (JWKS) du realm Keycloak.
  Si Keycloak n'est pas joignable de façon privée (VPC, PrivateLink, VPN), il faudra soit un
  NAT et un sous-réseau public (amendement de cet ADR), soit fournir les clés autrement.
- Chaque phase ajoute ses propres groupes de sécurité (Aurora, MSK, ECS) en référençant le
  VPC et les sous-réseaux exportés ; d'autres points de terminaison seront ajoutés au besoin
  (ex. `kafka`, `monitoring`, `ssmmessages`) par la variable `services_points_de_terminaison`.
- Coût : huit points de terminaison d'interface sur trois zones, facturés à l'heure.
- Les noms de zones ne désignent pas les mêmes zones physiques d'un compte à l'autre ; sans
  incidence tant qu'aucune ressource n'est partagée entre comptes.

## Options écartées

- **NAT dès la phase 6** : une sortie Internet et un sous-réseau public sans besoin avéré.
- **Un seul niveau de sous-réseaux** : les règles d'accès aux données ne pourraient plus
  s'exprimer par sous-réseau (NACL, routage) si elles devenaient nécessaires.
- **Zones lues par `aws_availability_zones`** : la liste peut s'allonger quand AWS ouvre une
  zone et décaler les sous-réseaux (Checkov `CKV_AWS_394`).
