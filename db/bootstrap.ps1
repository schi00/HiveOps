# Idempotent-friendly bootstrap (Windows): runs SQL files from manifest.order.txt
# Requires: sqlcmd on PATH (SQL Server Command Line Utilities)
# Environment: HIVEOPS_SQL_HOST (default localhost), HIVEOPS_SQL_PORT (1433), HIVEOPS_SQL_USER (sa), HIVEOPS_SQL_PASSWORD (required)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Join-Path $root 'db\manifest.order.txt'

$hostName = if ($env:HIVEOPS_SQL_HOST) { $env:HIVEOPS_SQL_HOST } else { 'localhost' }
$port = if ($env:HIVEOPS_SQL_PORT) { $env:HIVEOPS_SQL_PORT } else { '1433' }
$user = if ($env:HIVEOPS_SQL_USER) { $env:HIVEOPS_SQL_USER } else { 'sa' }
$password = $env:HIVEOPS_SQL_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) { throw 'Set environment variable HIVEOPS_SQL_PASSWORD' }

$server = "${hostName},${port}"

function Wait-SqlReady {
    for ($i = 1; $i -le 60; $i++) {
        try {
            sqlcmd -S $server -U $user -P $password -C -Q "SET NOCOUNT ON; SELECT 1" -b -h -1 | Out-Null
            Write-Host 'SQL Server is reachable.'
            return
        } catch { }
        Write-Host "Waiting for SQL Server ($i/60)..."
        Start-Sleep -Seconds 2
    }
    throw 'SQL Server did not become ready in time.'
}

Wait-SqlReady

Get-Content $manifest | ForEach-Object {
    $line = $_.Trim()
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { return }
    $script = Join-Path $root "db\$line"
    if (-not (Test-Path $script)) { throw "Missing script: $script" }
    Write-Host "Running $line ..."
    & sqlcmd -S $server -U $user -P $password -C -d master -b -I -i $script
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed on $line exit $LASTEXITCODE" }
}

Write-Host 'Bootstrap finished.'
