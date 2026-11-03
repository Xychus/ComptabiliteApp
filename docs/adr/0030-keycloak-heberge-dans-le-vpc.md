# 0030 — Keycloak hébergé dans le VPC, realm importé, clients authentifiés par mTLS

- **Statut** : accepté
- **Date** : 2026-11-02
- **Phase** : 9

## Contexte

La phase 9 exige un émetteur de jetons (realm `ledger`, *client credentials*, audience
`ledger-api`, scopes `ledger:read` / `ledger:write`, jetons de 5 minutes à signature asymétrique).
L'autorizer Lambda et les deux API lisent la configuration OIDC et les clés publiques (JWKS) du
realm. Le VPC n'a aucune sortie Internet (ADR 0023) et le pipeline GitHub ne joint pas le VPC.
`CLAUDE.md` exige la rotation de tout secret aux 7 jours. Décisions du responsable du projet
(2026-11-02) : Keycloak sur ECS dans le VPC, realm importé au démarrage, clients internes
authentifiés par certificat de la PKI.

## Décision

1. **Keycloak sur ECS Fargate dans le VPC** : image officielle `quay.io/keycloak/keycloak:26.8`
   copiée dans ECR par `images.yml` (ADR 0028, étiquette exacte `26.8.1`), sous-réseaux
   `applications`, nom `keycloak.idp.<prefixe>.internal` (espace de noms Cloud Map dédié, comme
   Kafka), port 8443 : émetteur `https://keycloak.idp.<prefixe>.internal:8443/realms/ledger`,
   fixé par `KC_HOSTNAME`. Aucune sortie Internet, aucun amendement de l'ADR 0023.
2. **HTTPS par un certificat serveur de la PKI** (ADR 0010), installé par la sentinelle au
   démarrage de la tâche (modèle de Debezium) : `keystore.pem` (clé puis chaîne) sert à la fois de
   certificat et de clé, `truststore.pem` (racine) de magasin de confiance du mTLS. Keycloak tourne
   avec l'utilisateur de la sentinelle (`1001:0`, fichiers en `0600`). Après chaque renouvellement,
   la Lambda redéploie le service (nouvelle tâche saine avant l'arrêt de l'ancienne).
   `https-protocols=TLSv1.3` (tous les clients sont des .NET 10 et des Lambda .NET 10, qui
   négocient TLS 1.3).
3. **Clients internes authentifiés par mTLS** : `https-client-auth=request`, magasin de confiance =
   racine de la PKI de l'environnement (PEM) ; chaque client est un client confidentiel du realm
   avec l'authentificateur X.509 (DN du sujet attendu `CN=<client>`) et un certificat `client` de
   la PKI (7 jours, renouvelé tous les 5 jours). **Aucun secret client** à stocker ni à renouveler.
   `azp` du jeton = identifiant du client = `actor` enregistré dans `events`.
4. **Realm importé au démarrage** (`start --import-realm`) : `infra/keycloak/ledger-realm.json`,
   transmis en variable d'environnement de la tâche et écrit dans `/opt/keycloak/data/import`
   juste avant le lancement. Scopes, mapper d'audience, clients `client-x509`
   (`x509.subjectdn` = `CN=<client>`, comparaison exacte), durée des jetons (300 s), algorithme
   de signature par défaut **ES256**. Le module Terraform `keycloak/keycloak` du plan est abandonné : le runner
   ne joint pas Keycloak.
5. **Aucun compte d'administration permanent** : aucune option `bootstrap-admin-*` ; console non
   exposée hors du VPC. En cas de besoin, la commande `bootstrap-admin` est lancée en tâche
   ponctuelle (runbook), Keycloak arrêté, et le compte temporaire supprimé ensuite.
6. **Base** : schéma `keycloak` dans la base de la grappe Aurora, rôle `keycloak` (migration),
   propriétaire de ce seul schéma. Keycloak ne gère pas l'authentification IAM (pilote JDBC) :
   **mot de passe renouvelé tous les 5 jours** par la Lambda de rotation, comme Debezium
   (ADR 0029, vérificateur SCRAM), Keycloak redéployé après rotation ; connexion
   `db-tls-mode=verify-server` contre les certificats racines RDS.
7. Les API et l'autorizer font confiance à la **racine de la PKI** pour joindre Keycloak (canal
   arrière OIDC) ; aucune autre autorité n'est ajoutée.

## Conséquences

- **Modifier le realm** : l'import ignore un realm existant. Une modification passe par la
  procédure du runbook : arrêt des tâches Keycloak, tâche ponctuelle `import --override true`,
  redémarrage. L'écrasement régénère les clés de signature : les jetons en cours (≤ 5 min) sont
  refusés, les JWKS sont relus sur un `kid` inconnu. En dev, recréer l'environnement suffit.
- Keycloak devient une dépendance de disponibilité de toute l'entrée : 2 tâches en prod
  (grappe par `jdbc-ping`), 1 en dev.
- Un client interne doit détenir un certificat de la PKI : chaque nouvelle application interne
  ajoute une identité au module `pki` et un client au realm.
- Les jetons ne sont pas liés au certificat (RFC 8705) : API Gateway privée ne vérifie pas la
  liaison. La possession d'un jeton volé reste utile 5 minutes au plus.
- Une nouvelle surface : la Lambda de rotation administre aussi le rôle `keycloak`.

- Configuration éprouvée par un test d'intégration contre `quay.io/keycloak/keycloak:26.8` en mode
  `start` (`KeycloakMtlsTests`) : fichiers de la sentinelle, utilisateur `1001:0`, TLS 1.3, mTLS,
  realm importé, aucun compte d'administration ; jeton accepté par l'autorizer ; refus sans
  certificat, avec le certificat d'un autre client ou d'une autre PKI.

## Options écartées

- **Keycloak existant joint par Internet** : NAT et sous-réseau public (amendement de l'ADR 0023).
- **Provider Terraform `keycloak/keycloak`** : exige un runner dans le VPC (à exploiter et durcir).
- **Outil .NET de configuration par l'API d'administration** : plus souple pour modifier le realm,
  mais un composant de plus et un compte d'administration à renouveler.
- **Secrets clients renouvelés** : un nouveau type de rotation et un secret partagé par client ;
  le mTLS réutilise la PKI existante.
- **Base Keycloak dédiée** (`CREATE DATABASE`) : hors transaction, incompatible avec les migrations
  DbUp transactionnelles ; l'isolement par schéma et rôle suffit.
