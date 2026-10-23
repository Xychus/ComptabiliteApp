# 0011 — État Terraform : un bucket par compte, CMK dédiée, verrou S3 natif

- **Statut** : proposé
- **Date** : 2026-10-21
- **Phase** : 6

## Contexte

L'état Terraform contient la description complète de l'infrastructure, parfois des valeurs
sensibles ; il doit être chiffré par une CMK (CLAUDE.md), versionné et protégé contre les
écritures concurrentes. Le verrou par table DynamoDB est déprécié dans les versions récentes de
Terraform au profit d'un fichier de verrou dans le bucket (`use_lockfile`, disponible depuis
Terraform 1.10, stable depuis 1.11). Dev et prod sont deux comptes AWS distincts (P6). Le
pipeline doit retrouver le backend sans qu'aucun identifiant AWS ne soit stocké dans GitHub.

## Décision

1. **Un bucket d'état par compte**, créé par l'amorçage (`infra/bootstrap`), nommé
   `ledger-tfstate-<compte>-<région>` ; une **CMK dédiée** `alias/ledger-tfstate` (rotation
   annuelle). Clés d'objet : `bootstrap/terraform.tfstate`, `envs/<env>/terraform.tfstate`.
2. Backend S3 en **configuration partielle** : `key`, `encrypt = true` et
   `use_lockfile = true` sont dans le code ; `bucket`, `region` et `kms_key_id` (ARN de
   l'alias) sont passés à `terraform init` par le pipeline, **reconstruits à partir du seul
   numéro de compte** (variable de dépôt GitHub, non secrète). **Aucune table DynamoDB.**
3. Bucket : versionné, chiffrement par défaut SSE-KMS avec clé de bucket, accès public bloqué,
   `BucketOwnerEnforced`, versions antérieures conservées 365 jours. Politique du bucket :
   TLS obligatoire ; tout principal autre que les administrateurs déclarés, `gha-plan`,
   `gha-apply` et l'utilisateur racine est refusé ; seule une personne administratrice peut
   supprimer une version d'objet.
4. Politique de la CMK : chiffrement uniquement via S3 (`kms:ViaService`), pour ce bucket
   (`kms:EncryptionContext:aws:s3:arn`) et pour les mêmes principaux.
5. Le rôle de plan écrit **uniquement** les fichiers `*.tflock` : un `plan` prend le verrou
   comme un `apply`, et ne lit donc jamais un état en cours d'écriture.
6. Premier apply de l'amorçage en état local (`backend_override.tf`, ignoré par Git), puis
   migration de cet état dans le bucket (`init -migrate-state`).

## Conséquences

- Plus de table DynamoDB à créer, chiffrer et payer ; le verrou suit le chiffrement du bucket.
- Le compte prod ne dépend pas du compte dev, et inversement ; une fuite d'un rôle de dev ne
  donne pas accès à l'état de prod.
- La convention de nom est un contrat entre `infra/bootstrap` et `infra-ci.yml` : la changer
  impose de modifier les deux et de migrer l'état.
- La journalisation des accès S3 n'est pas activée (le bucket cible devrait être en SSE-S3,
  contraire à la règle des CMK) : l'audit passe par les événements de données CloudTrail
  (phase 11). Réplication interrégionale reportée à la reprise après sinistre (phase 11).
- Un `plan` qui échoue au milieu peut laisser un verrou : `terraform force-unlock` par un
  administrateur.

## Options écartées

- **Table DynamoDB** : dépréciée ; une ressource de plus à chiffrer par CMK.
- **Un seul bucket central** dans un compte d'outillage : un compte de plus à gérer, et des
  politiques intercomptes pour chaque rôle ; sans bénéfice à deux environnements.
- **Stocker le nom du bucket et l'ARN de la clé dans GitHub** : inutile, la convention suffit,
  et chaque variable stockée est une valeur de plus à garder synchronisée.
- **Plan sans verrou (`-lock=false`)** : le rôle de plan resterait strictement en lecture,
  mais pourrait lire un état partiellement écrit pendant un apply.
