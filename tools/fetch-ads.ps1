# Downloads what the ad blocker needs into ads-dl\ (the project picks it up from there, see
# src/neat/neat.csproj):
#   ads-dl\neat_adblock.dll   from the latest release of neat-adblock, checked against its .sha256
#   ads-dl\filters\*          from the latest release of neat-filters (the app checks every file
#                             against manifest.json when it starts)
# Needs the GitHub CLI (gh), signed in or with GH_TOKEN set. CI runs this before the build.
# Run it by hand from the repository root:  pwsh tools/fetch-ads.ps1
#
# Without -Strict a failure only prints a warning and leaves ads-dl as it was: the app then builds
# and runs without a blocker. With -Strict a failure stops the script with an error.
param(
    [string]$AdblockRepo = 'neatbrowser/neat-adblock',
    [string]$FiltersRepo = 'neatbrowser/neat-filters',
    [string]$Out = 'ads-dl',
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
    if ($Strict) { throw $msg }
    Write-Warning "ad blocker not bundled: $msg"
    exit 0
}

function Run-Gh {
    & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed ($LASTEXITCODE)" }
}

try {
    New-Item -ItemType Directory -Force $Out | Out-Null
    $tmp = Join-Path $Out '.download'
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $tmp | Out-Null

    # ---- the dll ----
    Run-Gh release download --repo $AdblockRepo --pattern 'neat_adblock.dll' --pattern 'neat_adblock.dll.sha256' --dir $tmp --clobber
    $dll = Join-Path $tmp 'neat_adblock.dll'
    $want = ((Get-Content (Join-Path $tmp 'neat_adblock.dll.sha256') -Raw).Trim() -split '\s+')[0].ToLower()
    $got = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLower()
    if ($want -ne $got) { throw "neat_adblock.dll does not match its .sha256 ($got, wanted $want)" }

    # ---- the filters ----
    $filters = Join-Path $tmp 'filters'
    New-Item -ItemType Directory -Force $filters | Out-Null
    Run-Gh release download --repo $FiltersRepo --pattern '*' --dir $filters --clobber
    if (-not (Test-Path (Join-Path $filters 'manifest.json'))) { throw 'the neat-filters release has no manifest.json' }

    # Everything arrived: only now replace what was there.
    Copy-Item $dll (Join-Path $Out 'neat_adblock.dll') -Force
    Remove-Item -Recurse -Force (Join-Path $Out 'filters') -ErrorAction SilentlyContinue
    Move-Item $filters (Join-Path $Out 'filters')
    Remove-Item -Recurse -Force $tmp

    $version = (Get-Content (Join-Path $Out 'filters/manifest.json') -Raw | ConvertFrom-Json).version
    Write-Host "ad blocker: neat_adblock.dll ok, filters $version"
}
catch {
    Fail $_.Exception.Message
}
