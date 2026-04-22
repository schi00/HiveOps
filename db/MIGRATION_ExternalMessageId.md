# Migración: AddExternalMessageIdDeduplication.sql

## Propósito
Agregar soporte para deduplicación de webhooks de Meta WhatsApp en la tabla `ConversationMessages`.

## Contexto
El bot estaba recibiendo mensajes duplicados porque Meta reintenta webhooks automáticamente (hasta 5 intentos).
Sin validación del `ExternalMessageId`, cada reintento generaba:
- 1 ConversationMessage duplicado en BD
- 1 respuesta enviada duplicada al cliente

## Cambios SQL
1. **Columna**: `ExternalMessageId NVARCHAR(255) NULL` → almacena ID único de Meta
2. **Índice**: `(TenantId, ExternalMessageId)` → búsqueda rápida para deduplicación

## Ejecución

### Opción 1: SQL Server Management Studio (SSMS)
```sql
-- 1. Abre archivos VadiSuite_Init.sql y AddExternalMessageIdDeduplication.sql
-- 2. Asegúrate de estar en la BD correcta:
USE [VadiSuite];

-- 3. Ejecuta AddExternalMessageIdDeduplication.sql
-- Debe mostrar:
--   Column ExternalMessageId added to ConversationMessages.
--   Index IX_ConversationMessages_TenantId_ExternalMessageId created...
```

### Opción 2: PowerShell / sqlcmd
```powershell
# Ajusta los valores según tu entorno
$server = "localhost"
$database = "VadiSuite"
$scriptPath = "c:\path\to\AddExternalMessageIdDeduplication.sql"

sqlcmd -S $server -d $database -i $scriptPath
```

### Opción 3: Visual Studio (sqlproj)
```
1. En Solution Explorer, derecha clic en el proyecto SQL
2. Build
3. Publish → selecciona el servidor/BD
```

## Verificación
```sql
-- Ejecuta después de la migración:
SELECT COUNT(*) as TotalMessages, 
       COUNT(DISTINCT ExternalMessageId) as UniqueExternalIds,
       COUNT(CASE WHEN ExternalMessageId IS NOT NULL THEN 1 END) as MessagesWithExternalId
FROM dbo.ConversationMessages;

-- Resultado esperado al recibir nuevos mensajes:
-- TotalMessages: N
-- MessagesWithExternalId: N (igual a TotalMessages si todos son de WhatsApp)
```

## Código C# Relacionado
- **Handler**: `src/SaaSBot.Agents/Orchestration/IncomingMessageHandler.cs` (línea 0: deduplication check)
- **Entity**: `src/SaaSBot.Domain/Entities/ConversationMessage.cs` (agregó propiedad)
- **Config**: `src/SaaSBot.Infrastructure/Persistence/Configurations/ConversationConfiguration.cs`

## Reversión (si es necesario)
```sql
-- NO EJECUTAR a menos que sea explícitamente solicitado
-- DROP INDEX IF EXISTS IX_ConversationMessages_TenantId_ExternalMessageId ON dbo.ConversationMessages;
-- ALTER TABLE dbo.ConversationMessages DROP COLUMN ExternalMessageId;
```

## Impacto
- ✅ Ninguno en datos existentes (columna NULL-able, índice no bloqueante)
- ✅ Cero downtime
- ✅ Compatible con replicación
