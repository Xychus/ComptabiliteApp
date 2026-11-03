# Plan complet de l'environnement avec des fournisseurs simulés : les attributs calculés restent
# inconnus au plan, comme lors d'un premier apply réel. Détecte un count ou un for_each qui
# dépend d'une valeur connue seulement à l'apply (invisible aux tests des modules).
mock_provider "aws" {}
mock_provider "tls" {}

variables {
  compte_aws = "111122223333"
}

run "le_plan_complet_se_calcule" {
  command = plan
}
