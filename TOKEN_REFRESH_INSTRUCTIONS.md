# 🔄 Token Refresh - WhatsApp Access Token Expirado

## Error Actual
```
Error validating access token: Session has expired on Friday, 10-Apr-26 15:00:00 PDT
The current time is Sunday, 12-Apr-26 15:46:18 PDT
```

## Solución Rápida (5 minutos)

### Paso 1: Regenerar Token en Meta Business Suite
1. Abre [Meta Business Suite](https://business.facebook.com)
2. Ingresa tu usuario/password
3. Selecciona tu negocio (organización)
4. Ve a: **Settings** → **Apps and Websites** → **Apps**
5. Haz clic en tu app de WhatsApp
6. En el menú izquierdo: **WhatsApp Accounts** o **Accounts**
7. Busca **Access Tokens** section
8. Haz clic en **Generate New Token** o **Refresh Token**
9. **Copia el token completo** (es una cadena muy larga que empezará con `EAA...`)

### Paso 2: Actualizar appsettings.json
```powershell
# Abre el archivo
notepad src/SaaSBot.Api/appsettings.json

# Busca la sección WhatsApp:
"WhatsApp": {
  "GraphApiBaseUrl": "https://graph.facebook.com/v22.0",
  "WebhookVerifyToken": "CHANGE_ME_META_VERIFY_TOKEN",
  "AppSecret": "",
  "ApiKey": "PASTE_NEW_TOKEN_HERE"  ← Reemplaza con el nuevo token
}

# Guarda (Ctrl+S)
```

### Paso 3: Reiniciar la API
```powershell
# En PowerShell, en la carpeta raíz del proyecto
dotnet run --project src/SaaSBot.Api
```

### Paso 4: Verificar
- Envía un mensaje por WhatsApp
- Si recibiste respuesta: ✅ **Token regenerado correctamente**
- Si sigue sin responder: verifica logs y busca "401"

---

## ⚠️ Seguridad
- **No compartas** el token públicamente
- **No lo hardes** en código (solo en `appsettings.json`)
- Regenera si sospechas que fue comprometido

## 📞 Tokens Permanentes vs Temporales
- **Temporal**: Expira cada ~60 días (lo que pasó aquí)
- **Permanente**: En Meta, selecciona "Generate Permanent Token" para evitar expiración

---

## Logs Esperados Después de Actualizar

### ✅ Correcto
```
info: SaaSBot.Infrastructure.Messaging.MetaWhatsAppChannel[0]
      Message sent successfully to X via WhatsApp. MessageId=wamid_...
```

### ❌ Aún con error 401
- Verifica que el token esté **completo** (sin espacios)
- Asegúrate de que es un token **reciente** (regenerado hoy)
- Revisa que el token sea para la app **correcta**
