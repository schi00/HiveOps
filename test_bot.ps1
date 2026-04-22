$base = "http://localhost:5197/webhook/whatsapp"
$ct = "application/json"
$phoneId = "1025302084008893"
$displayPhone = "+15556689857"

function Send-WhatsApp {
    param($msgId, $from, $text)
    $body = @{
        object = "whatsapp_business_account"
        entry = @(@{
            id = "TEST"
            changes = @(@{
                value = @{
                    messaging_product = "whatsapp"
                    metadata = @{ display_phone_number = $displayPhone; phone_number_id = $phoneId }
                    messages = @(@{ id = $msgId; from = $from; type = "text"; text = @{ body = $text } })
                }
                field = "messages"
            })
        })
    } | ConvertTo-Json -Depth 10
    $resp = Invoke-WebRequest -Uri $base -Method POST -Body $body -ContentType $ct
    return ($resp.Content | ConvertFrom-Json).reply
}

$user = "549111222333"
Write-Host ""
Write-Host "=== TEST INTEGRAL BOT ===" -ForegroundColor Cyan
Write-Host ""

$r1 = Send-WhatsApp "test-001" $user "hola"
Write-Host "[1] Saludo 1:     $r1" -ForegroundColor Green

$r2 = Send-WhatsApp "test-002" $user "hola de nuevo amigo"
Write-Host "[2] Saludo 2:     $r2" -ForegroundColor Green

$r3 = Send-WhatsApp "test-003" $user "tienen zapatillas nike"
Write-Host "[3] Inventario Nike:" -ForegroundColor Yellow
Write-Host $r3

$r4 = Send-WhatsApp "test-004" $user "cuales son los horarios"
Write-Host "[4] Horarios:" -ForegroundColor Yellow
Write-Host $r4

$r5 = Send-WhatsApp "test-003" $user "tienen zapatillas nike"
Write-Host "[5] Dedup (mismo ID): $r5" -ForegroundColor Magenta

Write-Host ""
Write-Host "=== Verificando BD ===" -ForegroundColor Cyan
$conv = sqlcmd -S ".\SQLEXPRESS" -d "VadiSuite" -Q "EXEC sp_set_session_context N'TenantId', '11111111-1111-1111-1111-111111111111'; SELECT COUNT(*) AS Mensajes FROM ConversationMessages WHERE TenantId='11111111-1111-1111-1111-111111111111';" 2>&1
Write-Host $conv
