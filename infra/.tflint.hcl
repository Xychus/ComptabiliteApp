# Configuration TFLint de infra/ (tflint --recursive --chdir infra --config "$PWD/infra/.tflint.hcl").
config {
  call_module_type = "local"
}

plugin "terraform" {
  enabled = true
  preset  = "all"
}

plugin "aws" {
  enabled = true
  version = "0.49.0"
  source  = "github.com/terraform-linters/tflint-ruleset-aws"
}
