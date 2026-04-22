# WhatsApp Integration Setup Guide

## Problem: "Bot doesn't respond to me"

Si el bot recibe tu mensaje (`200 OK`) pero no te responde en WhatsApp, el problema probablemente es que **tu access token de Meta ha expirado**.

## Root Cause: 401 Unauthorized

En los logs verás:
```
Meta WhatsApp API call failed (401 Unauthorized): 
Error validating access token: Session has expired...
```

Esto significa que el token almacenado en `appsettings.json` ya no es válido.

---

## 🔧 Solución: Regenerar Access Token

### Paso 1: Ir a Meta Business Suite

1. Abre [Meta Business Suite](https://business.facebook.com)
2. Selecciona tu organización / negocio
3. Ve a **Settings** → **Apps and Websites** → **Apps**
4. Selecciona tu app de WhatsApp

### Paso 2: Obtener Access Token

1. En la app de WhatsApp, ve a **Accounts** o **WhatsApp Accounts**
2. Busca **Access Tokens** o **Permanent Tokens**
3. Si tu token está expirado, haz clic en **Generate Token** o **Refresh Token**
4. Copia el nuevo token (es una cadena muy larga)

**⚠️ IMPORTANTE:** Este token es como una contraseña. No lo compartas públicamente.

### Paso 3: Actualizar appsettings.json

Abre [appsettings.json](src/SaaSBot.Api/appsettings.json) (o appsettings.Development.json):

```json
"WhatsApp": {
  "GraphApiBaseUrl": "https://graph.facebook.com/v22.0",
  "WebhookVerifyToken": "CHANGE_ME_META_VERIFY_TOKEN",
  "AppSecret": "...",
  "ApiKey": "PASTE_YOUR_NEW_TOKEN_HERE"  ← Pega el token aquí
}
```

**Ejemplo:** 
```json
"ApiKey": "EAABa4lW8ZCgBOwKL92z5X1Kn7ZCH12W34fG567hij89klm123"
```

### Paso 4: Reiniciar la API

```bash
# Si la API está corriendo, detenerla (Ctrl+C)
# Luego:
cd src/SaaSBot.Api
dotnet run --urls "http://localhost:5197"
```

### Paso 5: Probar

Envía un mensaje a tu número de WhatsApp configurado. Deberías recibir respuesta del bot.

---

## 📋 Otras Razones por las que el bot no responde:

### 1. **Token vacío o inválido**
   - **Síntoma:** 401 Unauthorized en logs
   - **Solución:** Sigue pasos 1-5 arriba

### 2. **PhoneNumberId incorrecto**
   - **Síntoma:** 404 Not Found o "Object with ID ... does not exist"
   - **Solución:** Verifica que `phoneNumberId` en BD coincida con Meta
   - **Verificar:**
     ```sql
     SELECT ConfigJson FROM Tenants WHERE Id = '<tu-tenant-id>'
     -- Busca "phoneNumberId" en el JSON
     ```

### 3. **API no tiene acceso a tu número**
   - **Síntoma:** Bot responde localmente (200 OK) pero Meta dice "not authorized"
   - **Solución:** En Meta Business Suite, verifica que el número de WhatsApp esté asignado a tu app

### 4. **OpenRouter (LLM) rate-limited**
   - **Síntoma:** 429 Too Many Requests en logs durante clasificación
   - **Solución:** Usa el free tier de OpenRouter pero con delays entre llamadas, o suscríbete a plan pago
   - **Fallback:** El bot devuelve "Parece que hubo un problema..."

### 5. **Webhook no registrado en Meta**
   - **Síntoma:** Mensajes no llegan al webhook
   - **Solución:** En Meta Business Suite, **Webhooks**, verifica:
     - URL de callback: `https://<tu-dominio>/webhook/whatsapp`
     - Verify Token: coincida con `WebhookVerifyToken` en appsettings.json
     - Webhook reconoce eventos: *messages* y *message_statuses*

---

## 🔍 Debugging

### Ver logs en tiempo real (mientras API corre):
```bash
# En la terminal donde está corriendo el API, mira los logs
# Busca: "Meta WhatsApp API"
```

### Verificar configuración en BD:
```sql
-- Ver tenant configurado
SELECT Id, WhatsAppNumber, ConfigJson FROM Tenants;

-- Ver conversaciones recibidas
SELECT Content, Role, CreatedAt FROM ConversationMessages 
ORDER BY CreatedAt DESC LIMIT 10;
```

### Probar webhook manualmente (desde PowerShell):
```powershell
$payload = @{
  entry = @(@{
    changes = @(@{
      value = @{
        metadata = @{
          phone_number_id = "1025302084008893"
        }
        messages = @(@{
          from = "34611223344"
          type = "text"
          text = @{ body = "test" }
        })
      }
    })
  })
} | ConvertTo-Json -Depth 10

$res = Invoke-WebRequest -Uri "http://localhost:5197/webhook/whatsapp" `
  -Method POST -Body $payload -ContentType "application/json"
Write-Host $res.Content
```

---

## Contact & Support

Si después de estos pasos aún no funciona:

1. **Verifica los logs** en la terminal del API
2. **Captura el error exacto** (status code + mensaje)
3. **Revisa esta guía nuevamente** - 90% de los problemas son:
   - ❌ Token expirado
   - ❌ PhoneNumberId incorrecto
   - ❌ Webhook no verificado en Meta
