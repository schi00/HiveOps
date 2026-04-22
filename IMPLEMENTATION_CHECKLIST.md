# ✅ CHECKLIST: Implementación del Fix de Deduplicación WhatsApp

## Pre-Implementación
- [x] **Análisis completado** - Causa raíz identificada: reintentOS de Meta sin validación
- [x] **Código implementado** - Todos los cambios C# listos
- [x] **Tests creados** - WebhookDeduplicationTests.cs listo
- [x] **Build validado** - ✅ Compilación exitosa

## 🔧 Implementación (Ahora - 15 minutos)

### Paso 1: Actualizar Base de Datos (5 min)
- [ ] Abre un cliente SQL (SSMS / sqlcmd)
- [ ] Selecciona la BD: `USE [VadiSuite];`
- [ ] **Ejecuta el script**: `db/AddExternalMessageIdDeduplication.sql`
- [ ] Verifica output:
  ```
  Column ExternalMessageId added to ConversationMessages.
  Index IX_ConversationMessages_TenantId_ExternalMessageId created...
  ```

### Paso 2: Reiniciar API (3 min)
- [ ] Detén el servidor (Ctrl+C si está corriendo)
- [ ] Espera 3 segundos
- [ ] Inicia nuevamente:
  ```bash
  cd src/SaaSBot.Api
  dotnet run --urls "http://localhost:5197"
  ```

### Paso 3: Validar Funcionamiento (5 min)
- [ ] Envía UN mensaje por WhatsApp a tu número de prueba
- [ ] Deberías recibir UNA respuesta
- [ ] **Verifica en SSMS**:
  ```sql
  SELECT TOP 10 
    ConversationId, 
    Role, 
    Content, 
    ExternalMessageId,
    CreatedAt
  FROM ConversationMessages
  WHERE ExternalMessageId IS NOT NULL
  ORDER BY CreatedAt DESC;
  ```
- [ ] Busca tu mensaje - debe tener un `ExternalMessageId` (ej: `wamid_AXXz...`)

### Paso 4: Simular Reintento Meta (Opcional - Verificar deduplicación)
Si tienes acceso a Meta Business Suite:
- [ ] Obtén el `ExternalMessageId` del mensaje anterior
- [ ] Simula reintento webhook (si Meta lo permite)
- [ ] **RESULTADO ESPERADO**: 
  - ✅ Mismo `ExternalMessageId` en BD
  - ✅ NO hay 2 mensajes con el mismo ID
  - ✅ Cliente NO recibe mensaje duplicado

---

## ✅ Validación Final

| Verificación | ✅/❌ |
|--------------|-------|
| Script BD ejecutado sin errores | [ ] |
| API reiniciada | [ ] |
| Mensaje recibido por WhatsApp | [ ] |
| `ExternalMessageId` en BD es NULL-able | [ ] |
| Campo visible de SQL con SELECT | [ ] |
| Index existe en BD | [ ] |

---

## 📋 Documentación de Referencia

- **Análisis completo**: [WHATSAPP_DEDUPLICATION_FIX.md](WHATSAPP_DEDUPLICATION_FIX.md)
- **Instrucciones SQL**: [db/MIGRATION_ExternalMessageId.md](db/MIGRATION_ExternalMessageId.md)
- **Tests**: [tests/SaaSBot.IntegrationTests/WebhookDeduplicationTests.cs](tests/SaaSBot.IntegrationTests/WebhookDeduplicationTests.cs)
- **El código**: 
  - [IncomingMessageHandler.cs](src/SaaSBot.Agents/Orchestration/IncomingMessageHandler.cs#L92-L105)
  - [ConversationMessage.cs](src/SaaSBot.Domain/Entities/ConversationMessage.cs#L8)

---

## 🆘 Troubleshooting

### ❌ Script SQL falla con "Object already exists"
→ Normal, el script es idempotente. Log dice "already exists"? Está bien, ignora.

### ❌ API no inicia después de cambios
→ Verifica que compiló: `dotnet build` desde raíz

### ❌ `ExternalMessageId` sigue en NULL después de sendMessage
→ Verifica que WebhookController está pasando `messageId` desde Meta  
→ Revisa logs: ¿llegó el webhook a `/webhook/whatsapp`?

### ❌ Sigo recibiendo mensajes duplicados
→ Verifica que el script SQL se ejecutó correctamente  
→ Haz SELECT y busca la columna:  
```sql
SELECT COLUMN_NAME 
FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME = 'ConversationMessages' 
AND COLUMN_NAME = 'ExternalMessageId';
```

---

## 📞 Siguientes Pasos
1. ✅ Completar checklist arriba
2. ✅ Testear durante 24h normalmente
3. ✅ Monitorear logs: grep "deduplication" (debería estar vacío si todo anda bien)
4. ✅ Si surgen issues, revisar: TenantId + ExternalMessageId en índice

**LISTO PARA PRODUCCIÓN** ✅
