# Plan Maestro: HiveOps como Agente de Soporte Integral para HiveCrew

**Fecha:** 2026-10-20
**Estado:** En implementación (Sprint 1–4)

## Objetivo

Convertir HiveOps en el agente de soporte integral de **HiveCrew**, capaz de atender un issue reportado por una persona vía WhatsApp o Ticketera, diagnosticar sobre la base de datos, logs y código reales de HiveCrew, y resolver el problema en tres niveles (L1 funcional, L2 datos, L3 código) con confirmación del usuario y auto-aprendizaje en la base de conocimiento.

## Diagnóstico de brechas (estado actual)

1. **Desconexión conversación ↔ worker**: `SupportPlugin.AnalyzeIncidentAsync` crea incidentes en `Status = InProgress`, pero `IncidentAutoWorkflowService` solo procesa `Status == Open && AssignedTo == "bot"`. Los incidentes por WhatsApp nunca entran al flujo automático.
2. **Planner ciego a herramientas reales**: `AgentToolCatalog` contiene herramientas de e-commerce (`search_products`, `get_store_info`, `start_checkout`) y `ExecutePlannerToolAsync` solo implementa `escalate_to_human`; el resto devuelve "Unsupported planner tool".
3. **Diagnóstico SQL stub**: `SupportPlugin.QueryDatabaseDiagnosticAsync` no ejecuta la query; `SelfSupportPlugin` inspecciona el repo/BD de HiveOps, no los de HiveCrew.
4. **Fixes a ciegas**: `GenerateDatabaseFixProposalAsync` y `GenerateCodeFixProposalAsync` no reciben esquema real de tablas ni contenido real de archivos de HiveCrew. `ExtractTableNameFromSql` falla con `UPDATE`/`DELETE` (busca `" FROM "`).
5. **Connection strings hardcodeadas**: `IncidentAutoWorkflowService.GetTenantConnectionString` hardcodea `NB0080\SQLEXPRESS` para el tenant "HiveCrew"; `GetProjectRoot()` apunta a `c:/temp/hiveops-work`.
6. **Bug en SafetyValidator**: `HasSpecificWhereCondition` devuelve `true` para `hasAlwaysTruePattern` cuando el SQL NO contiene `"1=2"` (`Contains(...) == false` siempre verdadero), lo que hace que TODO UPDATE/DELETE sea rechazado. Además rechaza comentarios `--`, lo que rompe incluso las reglas hardcodeadas.
7. **Sin canal Ticketera / sin notificación al usuario**: `IDeploymentService` es stub; el envío saliente solo existe para WhatsApp; la resolución en background no notifica al usuario que reportó; medios de WhatsApp (imagen/audio) se descartan como "[Imagen]".
8. **Consulta de tickets falsa**: `IntentType.IncidentQuery` responde texto estático en vez de consultar `Incidents` reales.
9. **Sin confirmación del usuario**: `ResolveIncidentAsync` pasa directo a `Closed` sin verificar con el usuario ni disparar KB en el canal correcto.
10. **Backup de tabla completa**: `DatabaseBackupService.BackupTableAsync` hace `SELECT * INTO` de toda la tabla — inaceptable en producción.

## Contratos nuevos (fijos entre agentes)

```csharp
// src/HiveOps.Domain/Interfaces/ITargetDatabaseDiagnosticService.cs
public interface ITargetDatabaseDiagnosticService
{
    Task<TargetSchemaSummary> GetRelevantSchemaAsync(Guid tenantId, string incidentText, CancellationToken ct = default);
    Task<TargetQueryResult> ExecuteReadOnlyQueryAsync(Guid tenantId, string sqlQuery, CancellationToken ct = default);
}

// src/HiveOps.Domain/Models/TargetDatabaseModels.cs
public sealed record TargetSchemaSummary(string SchemaDdl, IReadOnlyList<string> CandidateTables);
public sealed record TargetQueryResult(bool Success, string RenderedResult, int RowCount, string? Error);

// src/HiveOps.Domain/Interfaces/IIncidentChannelNotifier.cs
public interface IIncidentChannelNotifier
{
    Task NotifyIncidentProgressAsync(Guid tenantId, Guid incidentId, string message, CancellationToken ct = default);
    Task NotifyResolutionConfirmationRequestAsync(Guid tenantId, Guid incidentId, string resolutionSummary, CancellationToken ct = default);
}
// impl: HiveOps.Infrastructure.Messaging.IncidentChannelNotifier

// src/HiveOps.Domain/Interfaces/IChannelDispatcher.cs
public interface IChannelDispatcher
{
    Task SendTextAsync(Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct = default);
    Task SendButtonsAsync(Guid tenantId, string channel, string channelUserId, string text,
        IReadOnlyList<InteractiveButtonOption> buttons, string? footer, CancellationToken ct = default);
}
// impl: HiveOps.Infrastructure.Messaging.ChannelDispatcher
//   "whatsapp" -> IMessagingChannel (MetaWhatsAppChannel)
//   "ticketing" -> HiveOps.Infrastructure.Ticketing.TicketingChannel

// src/HiveOps.Domain/Interfaces/IGitService.cs (aditivo)
Task<string> GetWorkspacePathAsync(Guid tenantId, CancellationToken ct = default);

// src/HiveOps.Application/Support/SqlStatementParser.cs (nuevo)
public static class SqlStatementParser
{
    string? ExtractTableName(string sql);       // soporta UPDATE t SET, INSERT INTO t, DELETE FROM t, SELECT ... FROM t
    string? ExtractWhereClause(string sql);
}
```

## Sprint 1 — Orquestación punta a punta + diagnóstico SQL real

- `AgentToolCatalog`: quitar herramientas e-commerce; registrar `search_kb`, `analyze_incident`, `provide_incident_info`, `get_incident_status`, `run_db_diagnostic`, `approve_fix`, `reject_fix`, `escalate_to_human`.
- `IncomingMessageHandler.ExecutePlannerToolAsync`: cablear todas las herramientas a `SupportPlugin`/`SupervisionPlugin`.
- Bucle `PendingInfo`: si `state.State == InIncidentAnalysis` y el incidente está `PendingInfo`, la respuesta del usuario se anexa al incidente y se re-analiza (sin reclasificar intent).
- `IncidentQuery` → consulta real a `Incidents` de la conversación.
- `SupportPlugin.AnalyzeIncidentAsync`: dejar `Status = Open` + `AssignedTo = "bot"` cuando el triage está completo.
- `QueryDatabaseDiagnosticAsync`: ejecutar `SELECT` real vía `ITargetDatabaseDiagnosticService` (read-uncommitted, timeout 10s, máx. 50 filas).
- `IncidentAutoWorkflowService`: resolver conexión con `IDynamicConnectionStringResolver` (`WarmCacheAsync` + `Resolve`), sin hardcodeo; parser SQL correcto; dry-run transaccional (BEGIN TRAN → exec → ROLLBACK) antes de aprobar/ejecutar; backup selectivo de filas afectadas.
- `SafetyValidator` (Application): corregir `HasSpecificWhereCondition` (bug `1=2`), permitir comentarios `--`/`/* */` strippándolos antes de validar, y validar cada sentencia de un script multi-statement por separado.

## Sprint 2 — Canales: Ticketera bidireccional + WhatsApp multimodal

- `Incident`: agregar `ExternalTicketId`, `SourceChannel` (+ migración `db/AddIncidentChannelFields.sql` y `manifest.order.txt`).
- `IncomingMessage`: agregar `MediaId`, `MediaMimeType`, `MediaCaption`.
- `WebhookController`: `POST /webhooks/tickets` con verificación HMAC (`Ticketing:InboundSecret`); payload crea `Conversation` + `Incident` (Open/bot) o anexa comentario a incidente `PendingInfo`.
- `ChannelDispatcher` + `IncidentChannelNotifier`: notificación proactiva al canal de origen en cada hito del worker y al resolver (botones `[Ya funciona]` / `[Sigue fallando]`).
- `AgentRulesEngine.ExecuteInteractiveActionAsync`: manejar `btn_confirm_resolved:{id}` → `Closed` + KB; `btn_reopen_incident:{id}` → `Open` + feedback.
- `MultimodalMediaProcessor`: descarga media de Meta (imagen/audio/documento), guarda `IncidentAttachment`, extrae texto con modelo Vision (o transcripción de audio si está habilitado) y lo concatena al mensaje.

## Sprint 3 — Auto-fix de código sobre el repo real de HiveCrew + CI/CD

- `IGitService.GetWorkspacePathAsync` expone el clon por-tenant (`TenantGitService.ResolveRepositoryPathAsync`).
- `SelfSupportPlugin` usa el workspace del tenant y amplía extensiones (`.cshtml`, `.razor`, `.ts`, `.tsx`, `.js`, `.sql`) con contexto de líneas numeradas.
- `IncidentAutoWorkflowService.ApplyCodeFixAsync`: localización de archivos (stack trace/keywords → grep en workspace) → prompt con contenido real → `UnifiedDiffPatchApplier` + fallback SEARCH/REPLACE exacto (eliminar `ApplyCodeChangeHeuristic`) → gate `dotnet build`/`dotnet test` en el workspace (hasta 2 reintentos con feedback de compilación al LLM) → commit/push.
- `PipelineDeploymentService`: integración real (GitHub Actions / Azure DevOps) para crear PR y monitorear pipeline. Config: `DeployGit:Provider`, `DeployGit:ApiToken` (ref, no plaintext).

## Sprint 4 — Cierre confirmado + auto-aprendizaje

- `ResolveIncidentAsync`: estado `Resolved` + `NotifyResolutionConfirmationRequestAsync` (botones por WhatsApp / comentario en ticketera).
- `btn_confirm_resolved` → `Closed` + `KbArticleGenerator.GenerateFromIncidentAsync`; `btn_reopen_incident` → `Open` con feedback anexado.
- Playbooks operativos de HiveCrew (acciones pre-aprobadas vía API/SP) — fase posterior.
- KPIs: `effectiveness-metrics` ya existe; agregar FCR y handoff rate cuando el canal de confirmación esté operativo.

## Riesgos y guardarraíles

- Toda operación de escritura en BD de tenant pasa por `SafetyValidator` + dry-run + backup selectivo + aprobación si excede umbrales (`DbRecordThreshold`, `CodeLineThreshold`).
- Autorización explícita requerida para modificar bases que no sean SQL Express (regla de usuario).
- Nada de secrets en código: `EncryptedConnectionString` vía Data Protection; tokens vía configuración/secret refs.
- Sin commits automáticos; los cambios quedan en working tree para revisión.
