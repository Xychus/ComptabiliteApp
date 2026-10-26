# --- Exécution (phase 8) : PKI interne, Kafka, Debezium, services, équilibreur, observabilité ---
# Les images publiées par images.yml (ADR 0028) sont désignées par leur version exacte ; une
# variable de version nulle laisse le composant sans service (rien à démarrer). Les certificats
# de la racine et de l'intermédiaire (publics) sont versionnés dans ./pki après la cérémonie.

locals {
  domaine_interne = module.reseau.zone_interne.domaine
  chemin_racine   = "${path.module}/pki/racine.pem"
  chemin_inter    = "${path.module}/pki/intermediaire.pem"
  racine_pem      = fileexists(local.chemin_racine) ? file(local.chemin_racine) : null
  inter_pem       = fileexists(local.chemin_inter) ? file(local.chemin_inter) : null

  chaine_connexion = {
    for role in ["ledger_app", "ledger_query", "ledger_projection"] : role => join(";", [
      "Host=${module.aurora.point_de_terminaison_redacteur}",
      "Port=5432",
      "Database=${module.aurora.nom_base}",
      "Username=${role}",
      "SSL Mode=VerifyFull",
      "Root Certificate=/app/rds-ca-global.pem",
    ])
  }

  postgres_iam = {
    Ledger__Postgres__AuthentificationIam = "true"
    Ledger__Postgres__Region              = var.region
  }

  connexion_iam = {
    for role in ["ledger_app", "ledger_query", "ledger_projection"] : role => [{
      Sid      = "ConnexionIam"
      Effect   = "Allow"
      Action   = "rds-db:connect"
      Resource = "arn:aws:rds-db:${var.region}:${var.compte_aws}:dbuser:${module.aurora.identifiant_ressource}/${role}"
    }]
  }

  secret_tls = { for api in ["command-api", "query-api"] : api => module.pki.secrets_certificats[api] }
}

module "pki" {
  source = "../../modules/pki"

  prefixe                               = local.prefixe
  region                                = var.region
  compte_aws                            = var.compte_aws
  vpc_id                                = module.reseau.vpc_id
  sous_reseaux                          = module.reseau.sous_reseaux_applications
  groupe_securite_points_de_terminaison = module.reseau.groupe_securite_points_de_terminaison
  depot_ecr_url                         = module.ecr.urls["pki-lambda"]
  version_image                         = var.versions_images.pki
  generations_intermediaire             = var.generations_intermediaire
  generation_active                     = local.inter_pem != null ? var.generation_active : null
  certificat_racine_pem                 = local.inter_pem != null ? local.racine_pem : null
  certificat_intermediaire_pem          = local.inter_pem
  nom_grappe                            = module.ecs.nom_grappe
  administrateurs_kms                   = concat([local.arn_role_apply], var.administrateurs_kms)
  cle_kms_secrets_arn                   = module.kms.arns["secrets"]
  cle_kms_journaux_arn                  = module.kms.arns["logs"]
  cle_kms_lambda_arn                    = module.kms.arns["lambda"]
  frontiere_permissions_arn             = local.arn_frontiere
  delai_suppression_cles_jours          = var.delai_suppression_cles_pki_jours
  fenetre_recuperation_secrets_jours    = var.fenetre_recuperation_secrets_jours

  identites = merge(
    { for n in ["kafka-1", "kafka-2", "kafka-3"] : n => { nom_commun = n, usage = "serveur-client", noms_dns = [module.kafka.noms_noeuds[n]] } },
    {
      "kafka-admin"        = { nom_commun = "kafka-admin", usage = "client" }
      "debezium"           = { nom_commun = "debezium", usage = "client", services = ["debezium"] }
      "projections-worker" = { nom_commun = "projections-worker", usage = "client", services = ["projections-worker"] }
      "command-api"        = { nom_commun = "command-api", usage = "serveur", noms_dns = ["command-api.${local.domaine_interne}"], services = ["command-api"] }
      "query-api"          = { nom_commun = "query-api", usage = "serveur", noms_dns = ["query-api.${local.domaine_interne}"], services = ["query-api"] }
      "api"                = { nom_commun = "api", usage = "serveur", noms_dns = [module.equilibreur.nom_hote], certificat_acm_arn = module.equilibreur.certificat_acm_arn }
    },
  )

  mots_de_passe_postgres = {
    debezium = { role = "debezium", services = ["debezium"] }
  }

  aurora = {
    hote              = module.aurora.point_de_terminaison_redacteur
    base              = module.aurora.nom_base
    secret_maitre_arn = module.aurora.secret_maitre_arn
    groupe_securite   = module.aurora.groupe_securite
  }
}

module "kafka" {
  source = "../../modules/kafka"

  prefixe                               = local.prefixe
  region                                = var.region
  compte_aws                            = var.compte_aws
  vpc_id                                = module.reseau.vpc_id
  sous_reseaux                          = module.reseau.sous_reseaux_donnees
  sous_reseaux_applications             = module.reseau.sous_reseaux_applications
  domaine_interne                       = local.domaine_interne
  nom_grappe                            = module.ecs.nom_grappe
  arn_grappe                            = module.ecs.arn_grappe
  groupe_securite_points_de_terminaison = module.reseau.groupe_securite_points_de_terminaison
  identifiant_grappe_kraft              = var.identifiant_grappe_kraft
  depot_kafka_url                       = module.ecr.urls["kafka"]
  depot_kafka_arn                       = module.ecr.arns["kafka"]
  version_kafka                         = var.versions_images.kafka
  depot_sentinelle_url                  = module.ecr.urls["pki-sentinelle"]
  depot_sentinelle_arn                  = module.ecr.arns["pki-sentinelle"]
  version_sentinelle                    = var.versions_images.pki
  secrets_noeuds                        = { for n in ["kafka-1", "kafka-2", "kafka-3"] : n => module.pki.secrets_certificats[n] }
  secret_administration                 = module.pki.secrets_certificats["kafka-admin"]
  type_instance                         = var.kafka.type_instance
  memoire_kafka_mio                     = var.kafka.memoire_mio
  taille_volume_go                      = var.kafka.taille_volume_go
  cle_kms_kafka_arn                     = module.kms.arns["kafka"]
  cle_kms_secrets_arn                   = module.kms.arns["secrets"]
  cle_kms_journaux_arn                  = module.kms.arns["logs"]
  frontiere_permissions_arn             = local.arn_frontiere

  acl = {
    debezium           = { sujets_ecriture = ["ledger.events"], prefixes_sujets = ["ledger-connect-"], groupes = ["ledger-connect"] }
    projections-worker = { sujets_lecture = ["ledger.events"], groupes = ["ledger-projections"] }
  }
}

module "debezium" {
  source = "../../modules/debezium"

  prefixe                               = local.prefixe
  region                                = var.region
  compte_aws                            = var.compte_aws
  vpc_id                                = module.reseau.vpc_id
  sous_reseaux                          = module.reseau.sous_reseaux_applications
  arn_grappe                            = module.ecs.arn_grappe
  groupe_securite_points_de_terminaison = module.reseau.groupe_securite_points_de_terminaison
  depot_debezium_url                    = module.ecr.urls["debezium-connect"]
  depot_debezium_arn                    = module.ecr.arns["debezium-connect"]
  version_debezium                      = var.versions_images.debezium
  depot_sentinelle_url                  = module.ecr.urls["pki-sentinelle"]
  depot_sentinelle_arn                  = module.ecr.arns["pki-sentinelle"]
  version_sentinelle                    = var.versions_images.pki
  serveurs_kafka                        = module.kafka.serveurs
  groupe_securite_kafka                 = module.kafka.groupe_securite
  port_kafka                            = module.kafka.port_client
  secret_certificat                     = module.pki.secrets_certificats["debezium"]
  secret_mot_de_passe                   = module.pki.secrets_mots_de_passe["debezium"]
  hote_aurora                           = module.aurora.point_de_terminaison_redacteur
  nom_base                              = module.aurora.nom_base
  groupe_securite_aurora                = module.aurora.groupe_securite
  certificat_racine_rds_pem             = file("${path.module}/../../rds/${var.region}-bundle.pem")
  cle_kms_secrets_arn                   = module.kms.arns["secrets"]
  cle_kms_journaux_arn                  = module.kms.arns["logs"]
  frontiere_permissions_arn             = local.arn_frontiere
}

module "equilibreur" {
  #checkov:skip=CKV_AWS_150:Dev jetable (ADR 0027) : détruit et recréé par le pipeline ; la prod garde la protection contre la suppression.
  source = "../../modules/equilibreur"

  prefixe                = local.prefixe
  vpc_id                 = module.reseau.vpc_id
  cidr_vpc               = module.reseau.cidr_vpc
  sous_reseaux           = module.reseau.sous_reseaux_applications
  zone_interne           = module.reseau.zone_interne
  protection_suppression = var.protection_suppression
}

module "observabilite" {
  source = "../../modules/observabilite"

  prefixe                 = local.prefixe
  region                  = var.region
  cle_kms_xray_arn        = module.kms.arns["xray"]
  cle_kms_journaux_arn    = module.kms.arns["logs"]
  suffixe_equilibreur     = module.equilibreur.suffixe_equilibreur
  suffixes_groupes_cibles = module.equilibreur.suffixes_groupes_cibles
  instances_aurora        = module.aurora.identifiants_instances
}

locals {
  commun_services = {
    region                                = var.region
    compte_aws                            = var.compte_aws
    vpc_id                                = module.reseau.vpc_id
    sous_reseaux                          = module.reseau.sous_reseaux_applications
    arn_grappe                            = module.ecs.arn_grappe
    nom_grappe                            = module.ecs.nom_grappe
    groupe_securite_points_de_terminaison = module.reseau.groupe_securite_points_de_terminaison
    depot_collecteur_url                  = module.ecr.urls["aws-otel-collector"]
    depot_collecteur_arn                  = module.ecr.arns["aws-otel-collector"]
    version_collecteur                    = var.versions_images.collecteur
    groupe_journaux_metriques             = module.observabilite.groupe_journaux_metriques
  }
}

module "command_api" {
  source = "../../modules/service_ecs"

  prefixe                               = local.prefixe
  nom                                   = "command-api"
  region                                = local.commun_services.region
  compte_aws                            = local.commun_services.compte_aws
  vpc_id                                = local.commun_services.vpc_id
  sous_reseaux                          = local.commun_services.sous_reseaux
  arn_grappe                            = local.commun_services.arn_grappe
  nom_grappe                            = local.commun_services.nom_grappe
  groupe_securite_points_de_terminaison = local.commun_services.groupe_securite_points_de_terminaison
  depot_ecr_url                         = module.ecr.urls["command-api"]
  depot_ecr_arn                         = module.ecr.arns["command-api"]
  version_image                         = var.versions_images.command
  depot_collecteur_url                  = local.commun_services.depot_collecteur_url
  depot_collecteur_arn                  = local.commun_services.depot_collecteur_arn
  version_collecteur                    = local.commun_services.version_collecteur
  groupe_journaux_metriques             = local.commun_services.groupe_journaux_metriques
  port_https                            = 8443
  groupe_cible_arn                      = module.equilibreur.groupes_cibles["command-api"]
  groupe_securite_equilibreur           = module.equilibreur.groupe_securite
  nombre_taches                         = var.services.taches_minimum
  mise_a_echelle                        = { maximum = var.services.taches_maximum }

  environnement = merge(local.postgres_iam, {
    ConnectionStrings__Ledger         = local.chaine_connexion["ledger_app"]
    Ledger__Authentication__Authority = var.url_realm_keycloak
    Ledger__Tls__PortHttps            = "8443"
  })
  secrets = {
    Ledger__Tls__Chaine = "${local.secret_tls["command-api"]}:chaine::"
    Ledger__Tls__Cle    = "${local.secret_tls["command-api"]}:cle::"
  }
  arn_secrets_lus         = [local.secret_tls["command-api"]]
  sorties                 = { aurora = { groupe_securite = module.aurora.groupe_securite, port = 5432 } }
  declarations_role_tache = local.connexion_iam["ledger_app"]

  cle_kms_secrets_arn       = module.kms.arns["secrets"]
  cle_kms_journaux_arn      = module.kms.arns["logs"]
  cle_kms_xray_arn          = module.kms.arns["xray"]
  frontiere_permissions_arn = local.arn_frontiere
}

module "query_api" {
  source = "../../modules/service_ecs"

  prefixe                               = local.prefixe
  nom                                   = "query-api"
  region                                = local.commun_services.region
  compte_aws                            = local.commun_services.compte_aws
  vpc_id                                = local.commun_services.vpc_id
  sous_reseaux                          = local.commun_services.sous_reseaux
  arn_grappe                            = local.commun_services.arn_grappe
  nom_grappe                            = local.commun_services.nom_grappe
  groupe_securite_points_de_terminaison = local.commun_services.groupe_securite_points_de_terminaison
  depot_ecr_url                         = module.ecr.urls["query-api"]
  depot_ecr_arn                         = module.ecr.arns["query-api"]
  version_image                         = var.versions_images.query
  depot_collecteur_url                  = local.commun_services.depot_collecteur_url
  depot_collecteur_arn                  = local.commun_services.depot_collecteur_arn
  version_collecteur                    = local.commun_services.version_collecteur
  groupe_journaux_metriques             = local.commun_services.groupe_journaux_metriques
  port_https                            = 8443
  groupe_cible_arn                      = module.equilibreur.groupes_cibles["query-api"]
  groupe_securite_equilibreur           = module.equilibreur.groupe_securite
  nombre_taches                         = var.services.taches_minimum
  mise_a_echelle                        = { maximum = var.services.taches_maximum }

  environnement = merge(local.postgres_iam, {
    ConnectionStrings__Ledger         = local.chaine_connexion["ledger_query"]
    Ledger__Authentication__Authority = var.url_realm_keycloak
    Ledger__Tls__PortHttps            = "8443"
  })
  secrets = {
    Ledger__Tls__Chaine = "${local.secret_tls["query-api"]}:chaine::"
    Ledger__Tls__Cle    = "${local.secret_tls["query-api"]}:cle::"
  }
  arn_secrets_lus         = [local.secret_tls["query-api"]]
  sorties                 = { aurora = { groupe_securite = module.aurora.groupe_securite, port = 5432 } }
  declarations_role_tache = local.connexion_iam["ledger_query"]

  cle_kms_secrets_arn       = module.kms.arns["secrets"]
  cle_kms_journaux_arn      = module.kms.arns["logs"]
  cle_kms_xray_arn          = module.kms.arns["xray"]
  frontiere_permissions_arn = local.arn_frontiere
}

module "projections_worker" {
  source = "../../modules/service_ecs"

  prefixe                               = local.prefixe
  nom                                   = "projections-worker"
  region                                = local.commun_services.region
  compte_aws                            = local.commun_services.compte_aws
  vpc_id                                = local.commun_services.vpc_id
  sous_reseaux                          = local.commun_services.sous_reseaux
  arn_grappe                            = local.commun_services.arn_grappe
  nom_grappe                            = local.commun_services.nom_grappe
  groupe_securite_points_de_terminaison = local.commun_services.groupe_securite_points_de_terminaison
  depot_ecr_url                         = module.ecr.urls["projections-worker"]
  depot_ecr_arn                         = module.ecr.arns["projections-worker"]
  version_image                         = var.versions_images.projections
  depot_collecteur_url                  = local.commun_services.depot_collecteur_url
  depot_collecteur_arn                  = local.commun_services.depot_collecteur_arn
  version_collecteur                    = local.commun_services.version_collecteur
  groupe_journaux_metriques             = local.commun_services.groupe_journaux_metriques
  nombre_taches                         = var.services.taches_worker

  environnement = merge(local.postgres_iam, {
    ConnectionStrings__Projections  = local.chaine_connexion["ledger_projection"]
    Ledger__Kafka__BootstrapServers = module.kafka.serveurs
    Ledger__Kafka__SecurityProtocol = "Ssl"
  })
  secrets = {
    Ledger__Kafka__CertificatClientPem = "${module.pki.secrets_certificats["projections-worker"]}:chaine::"
    Ledger__Kafka__CleClientPem        = "${module.pki.secrets_certificats["projections-worker"]}:cle::"
    Ledger__Kafka__RacinePem           = "${module.pki.secrets_certificats["projections-worker"]}:racine::"
  }
  arn_secrets_lus = [module.pki.secrets_certificats["projections-worker"]]
  sorties = {
    aurora = { groupe_securite = module.aurora.groupe_securite, port = 5432 }
    kafka  = { groupe_securite = module.kafka.groupe_securite, port = module.kafka.port_client }
  }
  declarations_role_tache = local.connexion_iam["ledger_projection"]

  cle_kms_secrets_arn       = module.kms.arns["secrets"]
  cle_kms_journaux_arn      = module.kms.arns["logs"]
  cle_kms_xray_arn          = module.kms.arns["xray"]
  frontiere_permissions_arn = local.arn_frontiere
}

module "purge_outbox" {
  source = "../../modules/purge_outbox"

  prefixe                               = local.prefixe
  region                                = var.region
  compte_aws                            = var.compte_aws
  vpc_id                                = module.reseau.vpc_id
  sous_reseaux                          = module.reseau.sous_reseaux_applications
  arn_grappe                            = module.ecs.arn_grappe
  groupe_securite_points_de_terminaison = module.reseau.groupe_securite_points_de_terminaison
  depot_ecr_url                         = module.ecr.urls["migrations"]
  depot_ecr_arn                         = module.ecr.arns["migrations"]
  version_image                         = var.versions_images.migrations
  hote_aurora                           = module.aurora.point_de_terminaison_redacteur
  nom_base                              = module.aurora.nom_base
  identifiant_ressource_aurora          = module.aurora.identifiant_ressource
  groupe_securite_aurora                = module.aurora.groupe_securite
  cle_kms_journaux_arn                  = module.kms.arns["logs"]
  frontiere_permissions_arn             = local.arn_frontiere
}
