#Requires -Version 7
<#
.SYNOPSIS
    Applique les seuils de couverture par assembly sur un rapport Cobertura fusionné.
.DESCRIPTION
    Seuils définis dans CLAUDE.md : assemblies *.Domain >= 90 %, autres >= 75 % (couverture de lignes).
    Une assembly sans ligne exécutable est ignorée. Code de sortie 1 si un seuil n'est pas atteint.
.EXAMPLE
    pwsh eng/Check-Coverage.ps1 -Report coverage/Cobertura.xml
#>
param(
    [Parameter(Mandatory)] [string] $Report,
    [double] $DomainThreshold = 90,
    [double] $DefaultThreshold = 75
)

$ErrorActionPreference = 'Stop'

[xml] $cobertura = Get-Content -Raw -Path $Report
$packages = @($cobertura.coverage.packages.package)
$failures = 0
$rows = foreach ($package in $packages) {
    if ($null -eq $package) { continue }
    $lines = @($package.SelectNodes('.//class/lines/line'))
    $valid = $lines.Count
    if ($valid -eq 0) { continue }
    $covered = @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
    $rate = [math]::Round(100 * $covered / $valid, 2)
    $threshold = if ($package.name -like '*.Domain') { $DomainThreshold } else { $DefaultThreshold }
    $ok = $rate -ge $threshold
    if (-not $ok) { $failures++ }
    [pscustomobject]@{ Assembly = $package.name; Lignes = $valid; Couverture = "$rate %"; Seuil = "$threshold %"; Statut = if ($ok) { 'OK' } else { 'ÉCHEC' } }
}

if (-not $rows) {
    Write-Host 'Aucune ligne exécutable instrumentée : aucun seuil à appliquer.'
    exit 0
}

$rows | Format-Table -AutoSize | Out-String | Write-Host
if ($failures -gt 0) {
    Write-Host "$failures assembly(s) sous le seuil de couverture."
    exit 1
}
Write-Host 'Seuils de couverture respectés.'
