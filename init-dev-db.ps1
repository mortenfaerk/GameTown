<#
.SYNOPSIS
    Initializes a local development SQLite database for GameTown.

.DESCRIPTION
    The Windows/Aspire dev loop has no baseline-creation step. Program.cs only runs the numbered
    migrations (SchemaMigrator); the baseline schema (01_schema.sql + 02_seed.sql) is applied by the
    production installer (install.sh) or the test harness, never by 'dotnet run'. So a fresh dev
    database is never created, and pointing the app at an empty/missing .db wedges it: SQLite
    auto-creates an empty file, SchemaMigrator assumes a pre-versioning install and stamps it
    version 1, then the first migration fails because the baseline tables were never created.

    This script is the dev-only counterpart to the fresh-install branch of install.sh (lines 72-76).
    It creates the data directory and builds the database from the same two SQL files, in the same
    order. After it runs, the app applies the numbered migrations at startup as usual.

    The database path is read from user secrets (ConnectionStrings:DefaultConnection) so it cannot
    drift from what the app actually uses. Set it first if you have not:

        dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=C:\path\gametown.db" --project API

.PARAMETER Force
    Recreate the database even if one already exists (deletes the existing .db and its -wal/-shm).
    Without this, an existing database is left untouched, mirroring install.sh's upgrade behaviour.

.EXAMPLE
    ./init-dev-db.ps1
    ./init-dev-db.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot   = $PSScriptRoot
$apiProject = Join-Path $repoRoot 'API'
$schemaFile = Join-Path $repoRoot 'Database/sqlite/01_schema.sql'
$seedFile   = Join-Path $repoRoot 'Database/sqlite/02_seed.sql'

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Die($m)  { Write-Host "==> $m" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- prerequisites
if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    Die "sqlite3 is required and was not found on PATH. Install it (e.g. winget install SQLite.SQLite) and retry."
}
foreach ($f in @($schemaFile, $seedFile)) {
    if (-not (Test-Path $f)) { Die "Missing SQL file: $f" }
}

# ---------------------------------------------------------------- resolve db path from user secrets
Info "Reading ConnectionStrings:DefaultConnection from user secrets..."
$secrets = & dotnet user-secrets list --project $apiProject 2>$null
$line = $secrets | Where-Object { $_ -match 'ConnectionStrings:DefaultConnection' } | Select-Object -First 1
if (-not $line) {
    Write-Host "==> No ConnectionStrings:DefaultConnection in user secrets. Set it first:" -ForegroundColor Red
    Write-Host '    dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Data Source=C:\Users\<you>\AppData\Local\GameTown\gametown.db" --project API'
    exit 1
}

# Extract the file path from "...= Data Source=<path>[;other=...]"
if ($line -notmatch 'Data Source=([^;]+)') {
    Die "Could not parse a 'Data Source=' path out of: $line"
}
$dbPath  = $Matches[1].Trim()
$dataDir = Split-Path $dbPath -Parent
Info "Database:       $dbPath"
Info "Data directory: $dataDir"

# ---------------------------------------------------------------- data directory + the dirs that must survive
# Mirrors install.sh: the data directory holds the db, uploaded archives, re-hosted media and the
# Data Protection keyring. Create them so the app never has to.
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
foreach ($sub in @('games', 'media', 'keys')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $dataDir $sub) | Out-Null
}

# ---------------------------------------------------------------- create or preserve the database
if (Test-Path $dbPath) {
    if (-not $Force) {
        Info "Database already exists - leaving it untouched (the app applies migrations at startup)."
        Info "Re-run with -Force to delete and recreate it from the baseline."
        exit 0
    }
    Info "-Force: deleting existing database..."
    Remove-Item -Force -Path $dbPath, "$dbPath-wal", "$dbPath-shm" -ErrorAction SilentlyContinue
}

Info "Creating database from 01_schema.sql..."
Get-Content -Raw $schemaFile | & sqlite3 $dbPath
if ($LASTEXITCODE -ne 0) { Die "Applying 01_schema.sql failed." }

Info "Seeding from 02_seed.sql..."
Get-Content -Raw $seedFile | & sqlite3 $dbPath
if ($LASTEXITCODE -ne 0) { Die "Applying 02_seed.sql failed." }

Info "Done. Start the app and SchemaMigrator will apply the numbered migrations on top:"
Write-Host "    dotnet run --project Aspire/Aspire.AppHost"
