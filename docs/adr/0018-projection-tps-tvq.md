# 0018 — Projection TPS/TVQ : comptes fiscaux par configuration

- **Statut** : proposé — **format à valider avec le comptable** (point d'arrêt de la phase 4)
- **Date** : 2026-10-20
- **Phase** : 4

## Contexte

La projection `tax_projection` doit donner, par période, la TPS et la TVQ perçues et payées
(base des déclarations). Les événements ne portent pas de rôle fiscal : une ligne d'écriture ne
dit que compte, sens et montant. Il faut savoir quels comptes sont « TPS à percevoir », « CTI »,
« TVQ à percevoir », « RTI ». Le responsable du projet a choisi (2026-10-20) une configuration
du worker plutôt qu'un attribut du plan comptable, pour ne pas modifier le contrat de l'API de
commandes en phase 4.

## Décision

1. Configuration par entité, section `Ledger:Tax:Entities:{entité}` :
   `GstCollected`, `GstPaid`, `QstCollected`, `QstPaid` (listes de codes de comptes). Un compte
   ne peut avoir qu'un rôle (erreur au démarrage sinon).
2. Pour chaque écriture ou contre-passation, la projection cumule, par entité, **période de la
   date d'affaires** et devise :
   - taxes perçues : crédits − débits des comptes `GstCollected` / `QstCollected` ;
   - taxes payées (CTI, RTI) : débits − crédits des comptes `GstPaid` / `QstPaid`.
   Une contre-passation compte dans sa propre période (ADR 0008, H8) : elle réduit les taxes de
   cette période, pas celles de la période d'origine.
3. Taxe nette (à verser) = perçue − payée, calculée par l'API de requête.
4. La période de déclaration est la période comptable mensuelle (ADR 0008, H4).

### Exemple de référence

| Écriture (mars) | TPS perçue | TPS payée | TVQ perçue | TVQ payée |
|---|---:|---:|---:|---:|
| Vente 100 $ + TPS 5,00 + TVQ 9,98 | 5,00 | | 9,98 | |
| Achat 50 $ + TPS 2,50 + TVQ 4,99 | | 2,50 | | 4,99 |
| **Mars** | **5,00** | **2,50** | **9,98** | **4,99** |

## Conséquences

- Les projections ne lisent que des événements et cette configuration : changer la
  configuration exige un rejeu de `tax_projection` (ADR 0016), sinon les périodes passées
  restent calculées avec l'ancienne.
- Le changement de configuration n'est pas tracé dans le journal (seulement dans Git et le
  déploiement). Si l'auditabilité du rôle fiscal devient nécessaire, ajouter un attribut au plan
  comptable (`AccountDefined`, ajout compatible) et faire évoluer cette projection.

## À valider avec le comptable

- Périodes de déclaration (mensuelle, trimestrielle, annuelle) et rattachement par date
  d'affaires.
- Traitement des contre-passations dans leur période plutôt que dans la période d'origine.
- Lignes de déclaration attendues (ex. TPS : 101, 105, 108, 109 ; TVQ : 205, 206, 209) et
  autres montants nécessaires (ventes taxables, détaxées, exonérées).

## Options écartées

- **Rôle fiscal dans `AccountDefined`** : auditable, mais change le contrat de l'API de
  commandes ; reporté, voir Conséquences.
- **Convention de numérotation des comptes** : implicite et fragile.
