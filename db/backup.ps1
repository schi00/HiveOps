# Backup Hive database to a .bak file (full backup). Requires sqlcmd.
# Usage: .\db\backup.ps1 -BackupPath C:\Backups\hive.bak
# Env: HIVEOPS_SQL_HOST, HIVEOPS_SQL_PORT, HIVEOPS_SQL_USER, HIVEOPS_SQL_PASSWORD (same as bootstrap)

param(
    [Parameter(Mandatory = $true)]
    [string] $BackupPath
)

$ErrorActionPreference = 'Stop'
$hostName = if ($env:HIVEOPS_SQL_HOST) { $env:HIVEOPS_SQL_HOST } else { 'localhost' }
$port = if ($env:HIVEOPS_SQL_PORT) { $env:HIVEOPS_SQL_PORT } else { '1433' }
$user = if ($env:HIVEOPS_SQL_USER) { $env:HIVEOPS_SQL_USER } else { 'sa' }
$password = $env:HIVEOPS_SQL_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) { throw 'Set HIVEOPS_SQL_PASSWORD' }

$server = "${hostName},${port}"
$disk = $BackupPath -replace '''', ''''''
$sql = "BACKUP DATABASE [Hive] TO DISK = N'$disk' WITH FORMAT, INIT, NAME = N'Hive-full', SKIP, NOREWIND, NOUNLOAD, STATS = 10"
& sqlcmd -S $server -U $user -P $password -C -Q $sql -b
if ($LASTEXITCODE -ne 0) { throw "Backup failed with exit $LASTEXITCODE" }
Write-Host "Backup written to $BackupPath"
