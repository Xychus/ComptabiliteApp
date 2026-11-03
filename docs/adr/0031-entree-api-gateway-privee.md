# 0031 — Entrée par API Gateway privée : autorizer Lambda, VPC Link V2, certificat ACM public

- **Statut** : accepté
- **Date** : 2026-11-02
- **Phase** : 9

## Contexte

Points P2 et P3 du plan. P3 : le runtime géré `dotnet10` existe dans toutes les régions depuis le
2026-01-08. P2 : depuis l'arrivée des VPC Links V2 pour les API REST, une intégration privée
cible directement un ALB (`integration_target`, fournisseur AWS 6.70) ; plus besoin d'un NLB.
API Gateway vérifie le certificat de la cible : autorité reconnue, nom d'hôte de l'URI,
expiration. `insecureSkipVerification` ne lève que la première vérification. L'ALB interne de la
phase 8 présente un certificat de la PKI interne, que API Gateway refuse. Décision du responsable
du projet (2026-11-02) : **certificat ACM public sur l'ALB**, dans une zone Route 53 publique du
compte, sous `aws.usinageone.ca`.

## Décision

1. **API REST privée** (`PRIVATE`), joignable seulement par le point de terminaison VPC
   `execute-api` (politique de ressource), **cache désactivé**, journaux d'accès dans un groupe
   chiffré par la CMK `logs`.
2. **Autorizer Lambda de type `REQUEST`** (`Ledger.Authorizer`, runtime géré `dotnet10`, dans le
   VPC pour joindre Keycloak) : valide le JWT (émetteur, audience `ledger-api`, signature par le
   JWKS mis en cache, durée), lit `azp` et les scopes, renvoie une politique couvrant **toutes**
   les routes permises par les scopes (la politique est mise en cache par jeton), la clé du plan
   d'usage (`usageIdentifierKey`) et `context.clientId`. TTL du cache de l'autorizer = 300 s
   (≤ durée des jetons). `api_key_source = "AUTHORIZER"`.
3. **Clés des plans d'usage** : une clé API par client, valeur générée par API Gateway et
   recopiée dans un paramètre SSM `SecureString` (CMK `ssm`) lu par l'autorizer. La clé n'est
   **pas un secret d'authentification** : aucun client ne la détient, elle ne sert qu'à compter
   le débit d'un `azp` déjà authentifié. Elle n'entre donc pas dans la rotation de 7 jours.
4. **Plans d'usage par catégorie de clients** ; débit distinct en écriture et en lecture par
   limitation de méthode (`throttle` par chemin et méthode du plan).
5. **Intégration privée par VPC Link V2 vers l'ALB**, en HTTPS ; l'URI porte le nom public du
   certificat (`api.<zone publique>`), utilisé pour l'en-tête `Host` et la vérification du nom.
6. **Certificat ACM public sur l'ALB** (validation DNS) dans une **zone publique par compte**
   (`dev.aws.usinageone.ca`, `prod.aws.usinageone.ca`), créée par l'amorçage pour que sa
   délégation survive à la destruction de dev (ADR 0027). Seul le CNAME de validation y est
   publié : l'ALB reste interne, sans enregistrement public. Délégation NS dans
   `aws.usinageone.ca` par un humain. L'identité `api` de la PKI et son réimport ACM sont retirés
   (amende l'ADR 0010 pour l'ALB seulement ; les cibles restent en PKI).
7. L'ALB n'accepte que le groupe de sécurité du VPC Link : toute requête passe par API Gateway.
   Les API valident toujours le JWT elles-mêmes (défense en profondeur, ADR 0014).

## Conséquences

- Clients internes : point de terminaison `execute-api` du VPC et en-tête `x-apigw-api-id` (ou
  nom DNS privé de l'API).
- Le certificat de l'ALB est émis et renouvelé par ACM (autorité publique d'Amazon), pas par la
  PKI ; ce n'est pas AWS Private CA (`CLAUDE.md`).
- La zone publique n'est pas signée (DNSSEC) ni journalisée : à revoir en phase 11.
- Dépasser le débit d'un plan renvoie `429` ; les clients d'exemple réessaient avec un recul
  exponentiel.

## Options écartées

- **`insecureSkipVerification`** avec le certificat PKI : serveur non authentifié par API Gateway.
- **NLB devant l'ALB (VPC Link V1)** : déconseillé par AWS pour les nouveaux liens.
- **Zone publique dans `envs/dev`** : recréée à chaque destruction de dev, avec de nouveaux NS à
  redéléguer.
- **Image conteneur pour l'autorizer** : démarrage à froid plus lent sur le chemin de chaque
  requête non mise en cache ; le runtime géré suffit.
