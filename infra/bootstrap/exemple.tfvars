# Exemple de variables d'amorçage (une copie par compte : dev.tfvars, prod.tfvars).
# Ces valeurs ne sont pas secrètes, mais le dépôt est public : les copies sont ignorées par Git.

compte_aws          = "111122223333"
region              = "ca-central-1"
environnement       = "dev"
organisation_github = "Xychus"
depot_github        = "ComptabiliteApp"

# L'administrateur qui applique l'amorçage DOIT figurer ici : la politique du bucket d'état
# refuse tout autre principal (hors rôles du pipeline et utilisateur racine).
administrateurs = [
  "arn:aws:iam::111122223333:role/aws-reserved/sso.amazonaws.com/*/AWSReservedSSO_AdministratorAccess_*",
]
