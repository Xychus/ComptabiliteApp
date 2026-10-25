# 0027 — Environnement dev jetable, Aurora Serverless v2 en dev

- **Statut** : proposé
- **Date** : 2026-10-25
- **Phase** : 7

## Contexte

Le responsable du projet veut pouvoir supprimer l'environnement dev une fois un essai terminé, puis
le recréer, et réduire son coût entre deux essais. Telle que la phase 7 l'a écrite, dev a les
protections de la prod :

- protection contre la suppression d'Aurora et instantané final ;
- dépôts ECR non supprimables tant qu'ils contiennent des images ;
- CMK supprimées après 30 jours ;
- aucun workflow de destruction.

Dev coûte surtout par Aurora : deux `db.r7g.large` en continu. `CLAUDE.md` interdit à Claude Code
d'exécuter `terraform destroy` : la destruction doit passer par le pipeline.

La documentation AWS (Aurora Serverless v2, mise en pause automatique, consultée le 2026-10-25)
dit ceci :

- un minimum de 0 ACU est possible à partir d'Aurora PostgreSQL 18.4 ;
- la reprise prend environ 15 secondes, 30 secondes ou plus après 24 heures de pause ;
- **lorsque `rds.logical_replication` est actif, le rédacteur et les lecteurs de rang de
  promotion 0 ou 1 ne se mettent jamais en pause** ; un lecteur de rang 2 à 15 se met en pause
  seul ;
- toute connexion ouverte empêche aussi la pause ;
- Performance Insights n'empêche pas la pause.

## Décision

1. **Dev est jetable ; prod ne change pas.** Les modules gardent des valeurs par défaut protectrices.
   Seul `envs/dev` les assouplit :
   - Aurora : `protection_suppression = false`, `instantane_final = false` ;
   - ECR : `suppression_forcee = true` ;
   - KMS : `delai_suppression_jours = 7`.

   L'exception Checkov `CKV_AWS_139` est posée sur l'appel du module dans dev seulement.
2. **Aurora Serverless v2 en dev** (`capacite_serverless`, instances `db.serverless`) : 0 à 4 ACU,
   pause après 15 minutes. **La réplication logique reste active**, pour que dev reste identique à
   la prod pour Debezium. Le rédacteur ne se met donc jamais en pause et reste à sa plus petite
   capacité active. Le lecteur passe au rang de promotion 2 : il se met en pause seul et reste la
   cible du basculement. Chiffrement, TLS et paramètres ne changent pas. L'alarme `FreeLocalStorage`
   n'est pas créée en Serverless, car la métrique n'y existe pas. La prod reste en instances
   provisionnées.
3. **`infra-destroy.yml`**, lancé à la demande seulement :
   - saisie obligatoire de « ledger-dev » ;
   - environnement GitHub `dev`, rôle `gha-apply` ;
   - même file d'attente qu'`infra-apply.yml` (`infra-apply-dev`, sans annulation) ;
   - vérifications préalables : bucket d'archive vide (sinon arrêt : des objets en COMPLIANCE
     rendent le bucket indestructible pendant 6 ans) et protection de la grappe déjà levée ;
   - `plan -destroy -out`, puis `apply` de ce plan.

   On recrée dev en lançant `infra-apply.yml`.

## Conséquences

- Coût de dev entre deux essais : surtout le rédacteur au minimum d'ACU et les points de
  terminaison VPC. Détruire dev ramène le coût à presque rien : il ne reste que l'amorçage et les
  CMK en attente de suppression.
- Une destruction de dev **efface les données** : aucun instantané final, des sauvegardes
  automatiques supprimées avec la grappe, et des CMK supprimées après 7 jours.
- Après un `infra-destroy`, la base recréée est vide. Les migrations sont à relancer (phase 10).
- Le basculement testé en phase 8 se fait vers un lecteur Serverless qui peut être en pause : la
  durée du basculement en dev n'est pas représentative de la prod.
- Si une donnée d'essai est déposée dans l'archive, dev ne peut plus être détruit entièrement
  pendant 6 ans. Le workflow s'arrête avant de détruire quoi que ce soit.

## Options écartées

- **Réplication logique en variable, désactivée en dev jusqu'à la phase 8** : vraie pause à
  0 ACU d'ici là, mais dev différerait de la prod et la phase 8 retomberait sur le comportement
  retenu (choix du responsable du projet, 2026-10-25).
- **Aurora arrêtée** (`stop-db-cluster`) : AWS la redémarre d'office après 7 jours.
- **Destruction par un administrateur, à la main** : identifiants personnels étendus, aucune trace
  dans le pipeline, et aucune vérification préalable de l'archive.
