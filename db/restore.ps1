# Restore Hive database from a .bak file. Requires sqlcmd. Destructive: replaces Hive if it exists.
# Usage: .\db\restore.ps1 -BackupPath C:\Backups\hive.bak
# Env: HIVEOPS_SQL_HOST, HIVEOPS_SQL_PORT, HIVEOPS_SQL_USER, HIVEOPS_SQL_PASSWORD

param(
    [Parameter(Mandatory = $true)]
    [string] $BackupPath
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $BackupPath)) { throw "Backup file not found: $BackupPath" }

$hostName = if ($env:HIVEOPS_SQL_HOST) { $env:HIVEOPS_SQL_HOST } else { 'localhost' }
$port = if ($env:HIVEOPS_SQL_PORT) { $env:HIVEOPS_SQL_PORT } else { '1433' }
$user = if ($env:HIVEOPS_SQL_USER) { $env:HIVEOPS_SQL_USER } else { 'sa' }
$password = $env:HIVEOPS_SQL_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) { throw 'Set HIVEOPS_SQL_PASSWORD' }

$server = "${hostName},${port}"
$disk = $BackupPath -replace '''', ''''''

$sql = @"
ALTER DATABASE [Hive] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [Hive] FROM DISK = N'$disk' WITH REPLACE, RECOVERY;
ALTER DATABASE [Hive] SET MULTI_USER;
"@

& sqlcmd -S $server -U $user -P $password -C -Q $sql -b
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit $LASTEXITCODE" }
Write-Host "Restore completed from $BackupPath"
