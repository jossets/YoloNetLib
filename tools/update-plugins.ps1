<#
.SYNOPSIS
    Met à jour les DLL de YoloNetLib/Assets/Plugins (SIPSorcery + dépendances) depuis NuGet.

.DESCRIPTION
    Crée un projet temporaire qui référence SIPSorcery (et les paquets utilisés directement par le client),
    laisse NuGet résoudre toutes les dépendances pour la cible Unity, signale les vulnérabilités connues,
    puis compare avec le contenu actuel de Assets/Plugins.
    Sans -Apply, rien n'est modifié : le script affiche seulement ce qui changerait.

    Par défaut, seules les DLL déjà présentes sont mises à jour, et jamais vers une version plus ancienne.
    Les nouvelles dépendances sont listées sans être copiées : beaucoup sont des façades netstandard
    (System.Linq, System.Reflection.Emit...) déjà fournies par Unity, qui provoqueraient des conflits.
    -IncludeNew les copie quand même (après vérification au cas par cas).

.EXAMPLE
    .\tools\update-plugins.ps1
    Simulation avec la dernière version stable de SIPSorcery.

.EXAMPLE
    .\tools\update-plugins.ps1 -Apply
    Copie les DLL mises à jour dans Assets/Plugins.

.EXAMPLE
    .\tools\update-plugins.ps1 -SipSorceryVersion 10.0.17 -Apply -RemoveObsolete
    Version figée, et suppression des DLL qui ne font plus partie des dépendances.
#>
param(
    # Vide = dernière version stable publiée sur nuget.org
    [string]   $SipSorceryVersion = "",
    # Paquets utilisés directement par le code du client (pas seulement via SIPSorcery)
    [string[]] $ExtraPackages     = @("System.Text.Json"),
    # Cible des DLL actuelles (Unity, niveau de compatibilité API .NET Framework / .NET Standard)
    [string]   $TargetFramework   = "netstandard2.0",
    [string]   $PluginsDir        = (Join-Path $PSScriptRoot "..\YoloNetLib\Assets\Plugins"),
    [switch]   $Apply,
    [switch]   $IncludeNew,
    [switch]   $RemoveObsolete
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-LatestStableVersion([string] $packageId) {
    $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($packageId.ToLowerInvariant())/index.json"
    $stable = @($index.versions | Where-Object { $_ -notmatch "-" })
    if ($stable.Count -eq 0) { throw "Aucune version stable trouvée pour $packageId" }
    return $stable[-1]
}

function Get-DllVersion([string] $path) {
    $v = (Get-Item $path).VersionInfo.FileVersion
    if ([string]::IsNullOrEmpty($v)) { return "?" }
    return $v
}

# <0 si $a est plus ancienne que $b
function Compare-Version([string] $a, [string] $b) {
    try   { return ([version]$a).CompareTo([version]$b) }
    catch { return [string]::CompareOrdinal($a, $b) }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet SDK introuvable (https://dot.net)" }
$PluginsDir = (Resolve-Path $PluginsDir).Path

# ------------------------------------------------------------------ #
# 1. Versions à installer
# ------------------------------------------------------------------ #
if ([string]::IsNullOrEmpty($SipSorceryVersion)) { $SipSorceryVersion = Get-LatestStableVersion "SIPSorcery" }
$packages = [ordered]@{ "SIPSorcery" = $SipSorceryVersion }
foreach ($p in $ExtraPackages) { $packages[$p] = Get-LatestStableVersion $p }

Write-Host "Paquets ($TargetFramework) :" -ForegroundColor Cyan
foreach ($p in $packages.Keys) { Write-Host ("  {0,-30} {1}" -f $p, $packages[$p]) }

# ------------------------------------------------------------------ #
# 2. Résolution et téléchargement via un projet temporaire
# ------------------------------------------------------------------ #
$work = Join-Path ([IO.Path]::GetTempPath()) ("yolonet-plugins-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $refs = ($packages.Keys | ForEach-Object { "    <PackageReference Include=`"$_`" Version=`"[$($packages[$_])]`" />" }) -join "`n"
    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$TargetFramework</TargetFramework>
    <AssemblyName>YoloNetPluginsResolver</AssemblyName>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <NuGetAudit>true</NuGetAudit>
    <NuGetAuditMode>all</NuGetAuditMode>
  </PropertyGroup>
  <ItemGroup>
$refs
  </ItemGroup>
</Project>
"@
    $projPath = Join-Path $work "resolver.csproj"
    Set-Content -Path $projPath -Value $csproj -Encoding UTF8

    Write-Host "`nTéléchargement et résolution des dépendances..." -ForegroundColor Cyan
    $out = Join-Path $work "out"
    & dotnet build $projPath -c Release -o $out -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & dotnet build $projPath -c Release -o $out -nologo -v q
        throw "Échec de la résolution NuGet"
    }

    Write-Host "`nVulnérabilités connues dans les dépendances :" -ForegroundColor Cyan
    $audit = & dotnet list $projPath package --vulnerable --include-transitive 2>&1 | Out-String
    if ($audit -match "(?i)(High|Critical|Moderate|Low)\s") { Write-Host $audit -ForegroundColor Yellow }
    else { Write-Host "  aucune" -ForegroundColor Green }

    # ------------------------------------------------------------------ #
    # 3. Comparaison avec Assets/Plugins
    # ------------------------------------------------------------------ #
    $newDlls = @(Get-ChildItem $out -Filter *.dll | Where-Object { $_.Name -ne "YoloNetPluginsResolver.dll" })
    $newNames = @{}
    foreach ($d in $newDlls) { $newNames[$d.Name] = $true }

    $changes = @()
    $skippedNew = 0
    Write-Host "`nDLL ($PluginsDir) :" -ForegroundColor Cyan
    foreach ($d in ($newDlls | Sort-Object Name)) {
        $target = Join-Path $PluginsDir $d.Name
        $newV = Get-DllVersion $d.FullName
        if (-not (Test-Path $target)) {
            if ($IncludeNew) {
                Write-Host ("  + {0,-58} {1}" -f $d.Name, $newV) -ForegroundColor Green
                $changes += $d
            }
            else {
                Write-Host ("  . {0,-58} {1}  (nouvelle dépendance, non copiée)" -f $d.Name, $newV) -ForegroundColor DarkGray
                $skippedNew++
            }
            continue
        }

        $oldV = Get-DllVersion $target
        $cmp  = Compare-Version $newV $oldV
        if ($cmp -gt 0) {
            Write-Host ("  ~ {0,-58} {1} -> {2}" -f $d.Name, $oldV, $newV) -ForegroundColor Yellow
            $changes += $d
        }
        elseif ($cmp -lt 0) {
            Write-Host ("  ! {0,-58} {1} conservée (NuGet propose {2}, plus ancienne)" -f $d.Name, $oldV, $newV) -ForegroundColor Magenta
        }
        else {
            Write-Host ("  = {0,-58} {1}" -f $d.Name, $oldV) -ForegroundColor DarkGray
        }
    }

    $obsolete = @(Get-ChildItem $PluginsDir -Filter *.dll | Where-Object { -not $newNames.ContainsKey($_.Name) } | Sort-Object Name)
    foreach ($o in $obsolete) {
        Write-Host ("  ? {0,-58} {1}  (hors dépendances : à vérifier avant suppression)" -f $o.Name, (Get-DllVersion $o.FullName)) -ForegroundColor Magenta
    }

    # ------------------------------------------------------------------ #
    # 4. Application
    # ------------------------------------------------------------------ #
    if (-not $Apply) {
        Write-Host "`nSimulation : $($changes.Count) DLL à copier, $skippedNew nouvelle(s) dépendance(s) ignorée(s), $($obsolete.Count) hors dépendances. Relancer avec -Apply pour appliquer." -ForegroundColor Cyan
        return
    }

    foreach ($d in $changes) { Copy-Item $d.FullName -Destination $PluginsDir -Force }
    Write-Host "`n$($changes.Count) DLL copiées." -ForegroundColor Green

    if ($RemoveObsolete) {
        foreach ($o in $obsolete) {
            Remove-Item $o.FullName -Force
            $meta = "$($o.FullName).meta"
            if (Test-Path $meta) { Remove-Item $meta -Force }
        }
        Write-Host "$($obsolete.Count) DLL hors dépendances supprimées." -ForegroundColor Green
    }
    Write-Host "Vérifier avec 'git status', puis rouvrir Unity pour qu'il réimporte les plugins." -ForegroundColor Cyan
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
