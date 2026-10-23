# 0022 — Une CMK par domaine de données, utilisable seulement à travers son service

- **Statut** : proposé
- **Date** : 2026-10-21
- **Phase** : 6

## Contexte

CLAUDE.md exige le chiffrement au repos par des CMK gérées par le client, une par domaine de
données, et interdit toute clé `aws/*`. La politique de clé par défaut (« Enable IAM User
Permissions » : `kms:*` pour le compte) délègue tout à IAM : n'importe quel principal doté de
`kms:Decrypt` sur `*` (PowerUserAccess, par exemple) pourrait déchiffrer directement les
données d'un domaine, hors du service qui les détient.

## Décision

1. Module `infra/modules/kms` : neuf CMK symétriques, alias `alias/ledger-<domaine>` —
   `aurora`, `msk`, `s3-archive`, `secrets`, `logs`, `ecr`, `ecs`, `lambda`, `ssm` — rotation
   automatique (365 jours par défaut), suppression après 30 jours d'attente.
2. Politique de chaque clé :
   - **Administration** : actions de gestion seulement (ni chiffrement, ni `CreateGrant`),
     pour les principaux nommés (condition `aws:PrincipalArn` : `gha-apply`, administrateurs
     déclarés, utilisateur racine en secours) ;
   - **lecture des métadonnées** (`Describe`, `Get`, `List`) ouverte au compte, filtrée par IAM ;
   - **usage cryptographique et autorisations** uniquement avec `kms:ViaService` = le service
     du domaine dans la région, et `kms:CallerAccount` = le compte (autorisations limitées aux
     ressources AWS, `kms:GrantIsForAWSResource`) ;
   - **exceptions documentées** pour les services qui chiffrent en leur nom :
     CloudWatch Logs (principal `logs.<région>.amazonaws.com`, contexte
     `aws:logs:arn` = un groupe du compte) et le stockage éphémère Fargate (principal
     `fargate.amazonaws.com`, contexte `aws:ecs:clusterAccount` = le compte et
     `aws:ecs:clusterName` = `ledger-*`, autorisations limitées à `Decrypt`).
3. **Jamais de clé `aws/*`**, vérifié trois fois :
   - les modules n'acceptent qu'un ARN de clé (`arn:aws:kms:…:key/…`) : un alias est refusé
     par la validation des variables (tests `terraform test`) ;
   - vérification Checkov maison `CKV_LEDGER_1` : aucune ressource ni source de données ne
     mentionne `alias/aws/` ;
   - vérification Checkov maison `CKV_LEDGER_2` : toute ressource chiffrable connue (groupes de
     journaux, secrets, paramètres SecureString, dépôts ECR, chiffrement de bucket, Aurora,
     MSK, Lambda, grappe ECS, SNS, SQS, EBS) désigne explicitement une CMK. Sans elle, AWS
     choisirait sa propre clé.

## Conséquences

- Un principal IAM ne peut pas déchiffrer directement : il passe par le service du domaine
  avec ses propres droits sur la ressource (et la politique IAM doit aussi accorder l'usage
  de la clé).
- Un administrateur peut toujours réécrire la politique d'une clé (`PutKeyPolicy`) ; cette
  action est tracée par CloudTrail et fera l'objet d'une alarme (phase 11).
- Ajouter un service consommateur d'un domaine (ex. MSK Connect pour `msk`, AWS Backup pour
  `aurora`) impose de compléter le catalogue du module : un consommateur oublié échoue
  explicitement, il n'obtient pas d'accès implicite.
- La condition Fargate sur `ledger-*` sera resserrée sur le nom exact de la grappe en phase 8.
- La liste de `CKV_LEDGER_2` doit suivre les nouveaux types de ressources chiffrables.

## Options écartées

- **Politique par défaut (`kms:*` pour le compte)** : délègue tout à IAM, contournable par un
  rôle large.
- **Une clé par environnement, tous domaines confondus** : un seul compromis expose toutes les
  données ; pas de séparation des rôles.
- **Principaux utilisateurs nommés dans chaque politique de clé** : ils n'existent pas encore
  (phases 7 à 9) et un principal supprimé rend la politique invalide ; `kms:ViaService` plus
  IAM obtient le même cloisonnement sans couplage.
