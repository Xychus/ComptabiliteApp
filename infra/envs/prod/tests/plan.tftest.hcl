# Plan complet de l'environnement avec des fournisseurs simulés : les attributs calculés restent
# inconnus au plan, comme lors d'un premier apply réel. Détecte un count ou un for_each qui
# dépend d'une valeur connue seulement à l'apply (invisible aux tests des modules).
mock_provider "aws" {}

variables {
  compte_aws = "111122223333"
}

run "le_plan_complet_se_calcule" {
  command = plan
}

run "le_plan_complet_se_calcule_avec_l_entree" {
  command = plan

  variables {
    version_autorizer = "0.1.0"
  }

  assert {
    condition     = length(module.apigateway) == 1
    error_message = "Le paquet publié fait naître l'API privée."
  }
}
