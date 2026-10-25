# 0026 — Archive d'intégrité : S3 Object Lock en mode COMPLIANCE, 6 ans

- **Statut** : proposé
- **Date** : 2026-10-25
- **Phase** : 7

## Contexte

Revenu Québec exige la conservation des registres pendant 6 ans, avec une intégrité vérifiable.
Le journal est append-only dans PostgreSQL, mais une base reste modifiable par un administrateur
de la base. Les exports d'intégrité (événements, sceaux de période, racines de Merkle, produits par
`Ledger.Integrity` en phase 11) doivent être déposés dans un stockage que personne ne peut altérer.

## Décision

1. Module `infra/modules/archive` : un bucket par compte et environnement
   (`<préfixe>-archive-<compte>-<région>`), **Object Lock activé à la création**, versionnement,
   conservation par défaut **COMPLIANCE, 6 ans** (variable, validée à 6 ans au moins).
2. SSE-KMS par la CMK `s3-archive` uniquement, avec clé de bucket (`bucket_key_enabled`).
3. Politique du bucket :
   - refus de tout accès sans TLS (`aws:SecureTransport = false`) ;
   - refus d'un `PutObject` dont l'en-tête désigne autre chose que SSE-KMS, une autre clé que la
     CMK, ou `aws:kms` sans identifiant de clé (ce qui désignerait `aws/s3`) ; sans en-tête, le
     chiffrement par défaut (la CMK) s'applique ;
   - refus de la lecture des objets au rôle `gha-plan` (ADR 0021) : `ReadOnlyAccess` accorde
     `s3:Get*`.
4. Accès public bloqué, propriété des objets imposée au propriétaire du bucket, `force_destroy`
   désactivé, téléversements incomplets abandonnés après 7 jours.

## Conséquences

- En COMPLIANCE, **personne**, pas même l'utilisateur racine, ne peut supprimer ou écraser une
  version avant son échéance, ni raccourcir la conservation. Un objet déposé par erreur reste
  stocké et facturé pendant 6 ans. Cela vaut **aussi en dev** : n'y déposer que des exports
  d'essai minuscules.
- Le bucket ne peut pas être supprimé tant qu'il contient des versions verrouillées.
- Les rôles d'export (phase 11) auront besoin de `s3:PutObject` et de l'usage de la CMK via S3.
  La frontière de permissions le permet déjà (`s3:PutObject`, `s3:PutObjectRetention`).
- Journalisation des accès, réplication interrégionale et notifications : phase 11 (CloudTrail
  événements de données, reprise après sinistre).

## Options écartées

- **Mode GOVERNANCE** : contournable par un principal qui a `s3:BypassGovernanceRetention`. Ce
  n'est pas une garantie d'immuabilité opposable.
- **Conservation posée objet par objet seulement** : un oubli de l'exportateur laisserait un objet
  sans protection ; la conservation par défaut couvre tout dépôt.
- **Glacier Vault Lock** : API distincte, restitution plus lente. Une transition de cycle de vie
  vers une classe d'archivage reste possible plus tard (phase 11).
