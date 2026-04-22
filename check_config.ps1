# Check tenant WhatsApp configuration
$output = & sqlcmd -S ".\SQLEXPRESS" -d "VadiSuite" -h -1 -Q "SET NOCOUNT ON; SELECT ConfigJson FROM Tenants WHERE Id = '11111111-1111-1111-1111-111111111111';"
$json = $output -join ""
$obj = $json | ConvertFrom-Json
Write-Host "WhatsApp Configuration:" -ForegroundColor Green
$obj.whatsApp | ConvertTo-Json -Depth 10
