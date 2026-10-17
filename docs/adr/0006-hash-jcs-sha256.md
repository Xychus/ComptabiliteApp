# 0006 — Hash sur la forme canonique JSON RFC 8785 (JCS), SHA-256

- **Statut** : proposé
- **Date** : 2026-10-16
- **Phase** : 2

## Contexte

Le hash d'un événement doit pouvoir être recalculé dans six ans, par un autre programme, à partir
des colonnes stockées. Or la représentation stockée change : `JSONB` réordonne les clés et
supprime les espaces, `NUMERIC` et `TIMESTAMPTZ` ont leurs propres formats et précisions.
Hacher une sérialisation .NET ou le texte renvoyé par PostgreSQL ne serait pas reproductible.

## Décision

1. Le hash est `SHA-256` (hexadécimal minuscule, 64 caractères) des octets UTF-8 de la forme
   canonique **JCS (RFC 8785)** d'un objet JSON construit explicitement à partir des champs de
   l'événement :

   | Clé | Valeur |
   |---|---|
   | `eventId`, `correlationId`, `causationId` | UUID en minuscules avec tirets, `causationId` peut être `null` |
   | `streamId`, `eventType`, `entityId`, `actor`, `period` | chaînes telles que stockées |
   | `streamVersion`, `schemaVersion` | nombres entiers |
   | `businessDate` | `AAAA-MM-JJ` ou `null` |
   | `recordedAt` | UTC, `AAAA-MM-JJTHH:mm:ss.ffffffZ` (microsecondes) |
   | `payload` | l'objet JSON de l'événement |
   | `balanceAfter` | chaîne décimale normalisée (sans zéros de fin, sans exposant) ou `null` |
   | `previousHash` | hash de la version précédente du flux ou `null` |

2. `recorded_at` est tronqué à la microseconde **avant** le calcul du hash, car c'est la
   précision de `TIMESTAMPTZ`. Un horodatage plus précis est refusé par le calcul du hash.
3. Les montants du `payload` sont des chaînes décimales (contrats V1) : JCS ne voit jamais un
   montant comme un nombre IEEE-754.
4. L'implémentation (canonicalisation JCS et calcul du hash) se trouve dans `Ledger.Contracts`
   (`Ledger.Contracts.Integrity`), sans dépendance externe : le format du hash fait partie du
   contrat public des événements, au même titre que leur schéma. Elle est utilisée par l'event
   store (calcul) et par `Ledger.Integrity` (vérification).
5. La canonicalisation est testée avec les exemples de la RFC 8785 (tri des clés par unités de
   code UTF-16, échappement des chaînes, sérialisation des nombres selon ECMAScript).

## Conséquences

- Le hash se recalcule à l'identique après un aller-retour par `JSONB` (test de contrat).
- Changer la liste des champs hachés ou leur format est un changement incompatible du composant
  `contracts` et invalide la vérification des événements existants : il faudrait versionner
  l'algorithme (ex. champ `hashVersion`) plutôt que le modifier.
- Une implémentation tierce (auditeur) peut reproduire le hash avec n'importe quelle
  bibliothèque JCS conforme.

## Options écartées

- **Hash du texte JSON produit par .NET** : dépend des options du sérialiseur et de l'ordre des
  propriétés.
- **Hash du texte `JSONB` renvoyé par PostgreSQL** : format non normatif, dépend de la version.
- **Dépendance à une bibliothèque JCS tierce** : peu de bibliothèques .NET maintenues ;
  l'algorithme est court et testable contre les vecteurs de la RFC.
