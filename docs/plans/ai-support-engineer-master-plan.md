# Plan Maestro: Transformación de HiveOps en AI Support Engineer (L1 / L2 / L3 Autónomo)

**Objetivo:** Llevar HiveOps de "bot de soporte con auto-workflow" a un **AI Support Engineer** vendible enterprise-grade (estilo Resolve.ai / Cleric), capaz de investigar causas raíz cruzando **Base de Datos + Código + Git Blame + Telemetría/Logs**, ejecutar **Playbooks L1/L2 deterministas**, generar **remediaciones de datos con snapshot reversible a nivel fila (JSON diff + SQL inverso)**, generar **parches de código con test de regresión + validación cerrada en HiveArena antes de abrir PR**, exponer un **Evidence & Audit Pack criptográfico (SHA-256)** por incidente, y medir **Outcomes verificados** para facturación por resolución.

---

## Principios de diseño para agentes con modelos livianos

Para que cada paquete pueda ser ejecutado por un agente con un modelo más pequeño sin romper el trabajo de otros:
1. **Fase 0 congela todos los contratos (`HiveOps.Domain` y `HiveOps.Application`)** antes de paralelizar. Ningún agente posterior inventa interfaces ni cambia firmas compartidas.
2. **Ownership exclusivo de archivos por paquete (Work Package):** dos agentes en el mismo Wave **nunca** editan el mismo archivo `.cs` salvo el bloque final de registro DI (Fase de Integración).
3. **Prompts y lógica determinista primero:** en vez de depender de que un LLM pequeño "adivine", cada paquete construye recolectores deterministas (`RootCauseHypothesisService`, `RowLevelSnapshotService`, `TenantGitService`, `PlaybookRunner`) y le entrega al LLM contexto estructurado con esquema JSON fijo.
4. **Cada paquete tiene su propio comando `dotnet test --filter`** que debe quedar en verde antes de dar por terminado el turno del agente.
5. **Regla de Git del repositorio:** prohibido agregar trailers `"Generated with Devin"` o `"Co-Authored-By: Devin"` en commits.

---

## Arquitectura Objetivo (Qué cambia estructuralmente)

```
[Reporte Usuario: WhatsApp / Ticketera / API / Alerta]
                        │
                        ▼
     ┌──────────────────────────────────────┐
     │  1. Evidence & Hypothesis Engine     │  ◄── WP-1: Cruza Schema + Queries SELECT sugeridas
     │     (DB + Logs/OTel + Git Blame)     │            + Logs de App + últimos commits (git log)
     └──────────────────┬───────────────────┘            y produce un RootCauseBundle estructurado.
                        │
        ┌───────────────┼───────────────────┐
        ▼               ▼                   ▼
 ┌──────────────┐ ┌──────────────────┐ ┌──────────────────────────┐
 │ 2A. Playbook │ │ 2B. Governed DB  │ │ 2C. Autonomous SWE       │
 │ Runner (L1)  │ │ Remediator (L2)  │ │ Engine + Regression Test │
 │ (Acciones    │ │ - Pre-snapshot   │ │ - Localiza + genera fix  │
 │  declarativas│ │   JSON + Inverse │ │ - Genera test unitario   │
 │  pre-aproba- │ │   SQL por PK     │ │   que reproduce el bug   │
 │  das por     │ │ - Dry-run + Diff │ │ - Gate HiveArena MANDATORY│
 │  tenant)     │ │ - One-click Undo │ │   antes de PR / Deploy   │
 └──────┬───────┘ └────────┬─────────┘ └────────────┬─────────────┘
        └───────────────┬──┴────────────────────────┘
                        ▼
     ┌──────────────────────────────────────┐
     │  3. Post-Fix Verification &          │  ◄── WP-5: Re-ejecuta la query/probe de verificación
     │     Cryptographic Audit Pack (SHA256)│            post-fix + firma cadena de evidencia auditable.
     └──────────────────┬───────────────────┘
                        ▼
     ┌──────────────────────────────────────┐
     │  4. Outcome Metering & Self-Serve    │  ◄── WP-6: Cuenta "Verified Autonomous Resolutions",
     │     Target System Onboarding         │            y valida conectividad (DB + Git + KB + Canales)
     └──────────────────────────────────────┘            en 1 click.
```

---

## Mapa de Ejecución y Paralelismo (7 Paquetes / 3 Waves)

| Wave | Paquete | Nombre | Se puede correr en paralelo con |
|---|---|---|---|
| **Wave 0** | **WP-0** | Contratos, Entidades, Migración SQL y Modelos compartidos | *(Secuencial — base para todos)* |
| **Wave 1** | **WP-1** | Multi-Source Root Cause & Hypothesis Engine (DB + Logs + Git Blame) | WP-2, WP-3, WP-4, WP-6 |
| **Wave 1** | **WP-2** | Motor de Playbooks Operativos L1/L2 (Runbooks declarativos por tenant) | WP-1, WP-3, WP-4, WP-6 |
| **Wave 1** | **WP-3** | Reversibilidad Quirúrgica de Datos (Before/After JSON Diff + Inverse SQL) | WP-1, WP-2, WP-4, WP-6 |
| **Wave 1** | **WP-4** | Autonomous SWE Loop: Generación de Tests de Regresión + PR Real | WP-1, WP-2, WP-3, WP-6 |
| **Wave 1** | **WP-6** | Target System Onboarding Doctor + Outcome Metering (Cobro por Resolución) | WP-1, WP-2, WP-3, WP-4 |
| **Wave 2** | **WP-5** | Orquestación unificada en `IncidentAutoWorkflowService` + Evidence Ledger SHA-256 | *(Secuencial — integra WP-1..4 y WP-6)* |

---

# Especificación Detallada por Paquete (Prompts listos para cada Agente)

---

## WP-0 — Contratos, Entidades de Dominio y Migración SQL (Wave 0)

**Objetivo para el agente:** Crear todas las entidades, enums, interfaces y el script SQL aditivo sin tocar la lógica de `IncidentAutoWorkflowService` ni controladores existentes, dejando la solución compilando en verde.

### Archivos a CREAR
1. `src/HiveOps.Domain/Entities/IncidentRemediationSnapshot.cs`
   - Propiedades:
     - `Guid Id { get; init; } = Guid.NewGuid();`
     - `Guid TenantId { get; set; }` (implementa `ITenantScoped`)
     - `Guid IncidentId { get; set; }`
     - `string TargetTable { get; set; } = string.Empty;`
     - `string PrimaryKeyColumnsJson { get; set; } = "[]";`
     - `string BeforeRowsJson { get; set; } = "[]";`
     - `string AfterRowsJson { get; set; } = "[]";`
     - `string InverseSqlScript { get; set; } = string.Empty;`
     - `string ForwardSqlScript { get; set; } = string.Empty;`
     - `int AffectedRowCount { get; set; }`
     - `bool IsReverted { get; set; }`
     - `DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;`
     - `DateTimeOffset? RevertedAt { get; set; }`
     - `string? RevertedBy { get; set; }`
2. `src/HiveOps.Domain/Entities/TenantPlaybook.cs`
   - Propiedades:
     - `Guid Id { get; init; } = Guid.NewGuid();`
     - `Guid TenantId { get; set; }` (implementa `ITenantScoped`)
     - `string Code { get; set; } = string.Empty;` (ej. `"reset_stuck_order"`, `"recompute_invoice_totals"`)
     - `string Name { get; set; } = string.Empty;`
     - `string TriggerSymptoms { get; set; } = string.Empty;`
     - `string ActionType { get; set; } = "SqlTemplate";` (`"SqlTemplate"` | `"HttpWebhook"` | `"StoredProcedure"`)
     - `string ActionPayloadTemplate { get; set; } = string.Empty;`
     - `string RequiredParametersJson { get; set; } = "[]";`
     - `string? VerificationSqlQuery { get; set; }`
     - `int MaxAffectedRows { get; set; } = 5;`
     - `bool RequiresApproval { get; set; }`
     - `bool IsActive { get; set; } = true;`
     - `DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;`
3. `src/HiveOps.Domain/Entities/IncidentAuditEntry.cs`
   - Propiedades:
     - `Guid Id { get; init; } = Guid.NewGuid();`
     - `Guid TenantId { get; set; }` (implementa `ITenantScoped`)
     - `Guid IncidentId { get; set; }`
     - `int SequenceNumber { get; set; }`
     - `string EventType { get; set; } = string.Empty;` (`"Triage"`, `"Hypothesis"`, `"DryRun"`, `"Approval"`, `"Execution"`, `"Verification"`, `"Rollback"`, `"ConfirmedClosed"`)
     - `string Actor { get; set; } = "HiveOps.AutoEngine";`
     - `string PayloadJson { get; set; } = "{}";`
     - `string PreviousHash { get; set; } = string.Empty;`
     - `string EntryHash { get; set; } = string.Empty;` (SHA-256 hex de ` $"{TenantId}|{IncidentId}|{SequenceNumber}|{EventType}|{Actor}|{PayloadJson}|{PreviousHash}" `)
     - `DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;`
4. `src/HiveOps.Domain/Models/RootCauseModels.cs`
   ```csharp
   namespace HiveOps.Domain.Models;

   public sealed record DiagnosticQueryEvidence(string SqlQuery, bool Success, int RowCount, string RenderedTable, string? Error);
   public sealed record GitCommitEvidence(string CommitHash, string Author, DateTimeOffset CommittedAt, string Subject, IReadOnlyList<string> TouchedFiles);
   public sealed record AppLogEvidence(DateTimeOffset Timestamp, string Level, string Message, string? ExceptionText, string? SourceContext);

   public sealed record RootCauseBundle(
       Guid IncidentId,
       string SummaryHypothesis,
       double Confidence,
       string RecommendedTrack, // "Playbook" | "DatabaseFix" | "CodeFix" | "Escalate"
       string? MatchedPlaybookCode,
       string? VerificationSqlQuery,
       IReadOnlyList<DiagnosticQueryEvidence> DatabaseEvidence,
       IReadOnlyList<GitCommitEvidence> RecentCommits,
       IReadOnlyList<AppLogEvidence> ApplicationLogs,
       string TargetSchemaDdl);
   ```
5. `src/HiveOps.Domain/Interfaces/IRootCauseHypothesisService.cs`
   ```csharp
   using HiveOps.Domain.Entities;
   using HiveOps.Domain.Models;

   namespace HiveOps.Domain.Interfaces;

   public interface IRootCauseHypothesisService
   {
       Task<RootCauseBundle> InvestigateAsync(Incident incident, CancellationToken ct = default);
       Task<bool> VerifyPostFixConditionAsync(Guid tenantId, string verificationSqlQuery, CancellationToken ct = default);
   }
   ```
6. `src/HiveOps.Domain/Interfaces/ITargetLogCollector.cs`
   ```csharp
   using HiveOps.Domain.Models;

   namespace HiveOps.Domain.Interfaces;

   public interface ITargetLogCollector
   {
       Task<IReadOnlyList<AppLogEvidence>> CollectRecentErrorsAsync(Guid tenantId, string searchHint, TimeSpan lookback, CancellationToken ct = default);
   }
   ```
7. `src/HiveOps.Domain/Interfaces/IPlaybookRunner.cs`
   ```csharp
   using HiveOps.Domain.Entities;

   namespace HiveOps.Domain.Interfaces;

   public sealed record PlaybookExecutionResult(bool Executed, bool Success, string PlaybookCode, int AffectedRows, string Summary, string? Error);

   public interface IPlaybookRunner
   {
       Task<TenantPlaybook?> FindMatchingPlaybookAsync(Guid tenantId, Incident incident, string? preferredCode, CancellationToken ct = default);
       Task<PlaybookExecutionResult> ExecuteAsync(TenantPlaybook playbook, Incident incident, IReadOnlyDictionary<string, string> extractedArgs, CancellationToken ct = default);
   }
   ```
8. `src/HiveOps.Domain/Interfaces/IRowLevelSnapshotService.cs`
   ```csharp
   using HiveOps.Domain.Entities;

   namespace HiveOps.Domain.Interfaces;

   public interface IRowLevelSnapshotService
   {
       Task<IncidentRemediationSnapshot> CaptureBeforeSnapshotAsync(Guid tenantId, Guid incidentId, string connectionString, string forwardSql, CancellationToken ct = default);
       Task PopulateAfterSnapshotAsync(IncidentRemediationSnapshot snapshot, string connectionString, CancellationToken ct = default);
       Task<int> RevertSnapshotAsync(IncidentRemediationSnapshot snapshot, string connectionString, string revertedBy, CancellationToken ct = default);
   }
   ```
9. `src/HiveOps.Domain/Interfaces/IIncidentAuditLedger.cs`
   ```csharp
   using HiveOps.Domain.Entities;

   namespace HiveOps.Domain.Interfaces;

   public interface IIncidentAuditLedger
   {
       Task<IncidentAuditEntry> AppendAsync(Guid tenantId, Guid incidentId, string eventType, string actor, object payload, CancellationToken ct = default);
       Task<(bool IsValid, int TotalEntries, string? BrokenAtEntryId)> VerifyChainAsync(Guid tenantId, Guid incidentId, CancellationToken ct = default);
   }
   ```
10. `src/HiveOps.Domain/Interfaces/IOutcomeMeteringService.cs`
    ```csharp
    using HiveOps.Domain.Entities;

    namespace HiveOps.Domain.Interfaces;

    public sealed record TenantOutcomeSummary(Guid TenantId, DateTimeOffset From, DateTimeOffset To, int TotalIncidents, int VerifiedAutonomousResolutions, int AssistedResolutions, int RollbacksCount);

    public interface IOutcomeMeteringService
    {
        Task RecordVerifiedResolutionAsync(Incident incident, CancellationToken ct = default);
        Task<TenantOutcomeSummary> GetSummaryAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    }
    ```
11. `db/V1.2__AiSupportEngineerFoundation.sql`
    - Script T-SQL **idempotente** (`IF OBJECT_ID(...) IS NULL`) que crea las tablas `IncidentRemediationSnapshots`, `TenantPlaybooks`, `IncidentAuditEntries` y agrega a `Incidents` las columnas opcionales:
      - `RootCauseJson NVARCHAR(MAX) NULL`
      - `VerificationQuery NVARCHAR(MAX) NULL`
      - `VerificationPassed BIT NULL`
      - `PullRequestUrl NVARCHAR(500) NULL`
      - `ArenaValidationStatus NVARCHAR(50) NULL`

### Archivos a MODIFICAR en WP-0
- `src/HiveOps.Domain/Entities/Incident.cs`: agregar las 5 propiedades nuevas (`RootCauseJson`, `VerificationQuery`, `VerificationPassed`, `PullRequestUrl`, `ArenaValidationStatus`).
- `src/HiveOps.Domain/Interfaces/IGitService.cs`: agregar:
  - `Task<IReadOnlyList<GitCommitEvidence>> GetRecentCommitsForFilesAsync(Guid tenantId, IReadOnlyList<string> relativePaths, int maxCount = 5, CancellationToken ct = default);`
- `src/HiveOps.Domain/Interfaces/IDeploymentService.cs`: agregar:
  - `Task<string?> CreatePullRequestAsync(Guid tenantId, string branchName, string title, string body, CancellationToken ct = default);`
- `src/HiveOps.Infrastructure/Persistence/AppDbContext.cs`: agregar `DbSet<IncidentRemediationSnapshot>`, `DbSet<TenantPlaybook>`, `DbSet<IncidentAuditEntry>` y sus `HasQueryFilter(e => e.TenantId == CurrentTenantId)`.
- `db/manifest.order.txt`: agregar `V1.2__AiSupportEngineerFoundation.sql` al final.

### Verificación de WP-0
```powershell
dotnet build HiveOps.slnx
```

---

## WP-1 — Multi-Source Root Cause & Hypothesis Engine (Wave 1)

**Objetivo para el agente:** Construir el motor de investigación que, antes de proponer cualquier fix, ejecuta las queries diagnósticas sugeridas por el triage sobre la BD del tenant, inspecciona logs de aplicación en la BD del tenant (tablas tipo `Logs`, `AppLogs`, `Serilog`, `EventLog`, `AuditLogs` si existen) y consulta los últimos commits de Git (`git log`) sobre los archivos sospechosos para producir un `RootCauseBundle`.

### Archivos EXCLUSIVOS a crear/modificar
- **Crear:** `src/HiveOps.Infrastructure/Diagnostics/TargetDatabaseLogCollector.cs` (implementa `ITargetLogCollector`)
  - Descubre de forma segura en la BD del tenant si existe alguna tabla de logs (`INFORMATION_SCHEMA.TABLES` buscando nombres `Logs`, `AppLogs`, `ErrorLogs`, `SystemLogs`, `Serilog`).
  - Si existe, arma un `SELECT TOP (20)` con `READ UNCOMMITTED` y timeout de 5s filtrando por las últimas 24h o por keywords del incidente, devolviendo `IReadOnlyList<AppLogEvidence>`. Si no existe tabla de logs, devuelve lista vacía sin lanzar excepción.
- **Modificar:** `src/HiveOps.Infrastructure/Git/TenantGitService.cs`
  - Implementar `GetRecentCommitsForFilesAsync`: ejecuta `git log -n {maxCount} --date=iso-strict --pretty=format:"%H|%an|%ad|%s" -- {paths}` en el workspace del tenant y parsea `GitCommitEvidence`. Si el workspace no existe o falla git, devuelve lista vacía.
- **Crear:** `src/HiveOps.Agents/Support/RootCauseHypothesisService.cs` (implementa `IRootCauseHypothesisService`)
  - Paso 1: Llama a `ITargetDatabaseDiagnosticService.GetRelevantSchemaAsync(incident.TenantId, $"{incident.Title}\n{incident.Description}")`.
  - Paso 2: Extrae las `suggestedDiagnostics` de `DiagnosticLogs` (`StepName == "llm_triage"`). Para cada SELECT válido (máx 3), si no tiene `SELECT`, pide al LLM una query `SELECT TOP 20` sobre el `SchemaDdl` real, la valida con `SafetyValidator.IsSafeDiagnosticSelect` y la ejecuta con `ExecuteReadOnlyQueryAsync`.
  - Paso 3: Llama a `ITargetLogCollector.CollectRecentErrorsAsync`.
  - Paso 4: Si hay tokens de archivos/clases en el incidente, llama a `IGitService.GetRecentCommitsForFilesAsync`.
  - Paso 5: Envía toda esa evidencia real en un prompt estructurado al LLM con JSON fijo para obtener: `summaryHypothesis`, `confidence`, `recommendedTrack` (`Playbook` | `DatabaseFix` | `CodeFix` | `Escalate`), `matchedPlaybookCode` y `verificationSqlQuery` (estandarizado como query que devuelve `0` filas anómalas cuando el fix funcionó).
  - Implementa `VerifyPostFixConditionAsync`: ejecuta `verificationSqlQuery` vía `ITargetDatabaseDiagnosticService.ExecuteReadOnlyQueryAsync` y verifica que `RowCount == 0` (o que el escalar devuelto sea `0`).
- **Crear tests:** `tests/HiveOps.UnitTests/Support/RootCauseHypothesisServiceTests.cs`

### Verificación de WP-1
```powershell
dotnet test tests/HiveOps.UnitTests/HiveOps.UnitTests.csproj --filter "FullyQualifiedName~RootCauseHypothesisServiceTests"
```

---

## WP-2 — Motor de Playbooks Operativos L1/L2 (Wave 1)

**Objetivo para el agente:** Permitir que problemas recurrentes conocidos de un producto (como HiveCrew: destrabar una orden, recalcular totales, limpiar un flag colgado, disparar un webhook de resync) se resuelvan con **Runbooks deterministas parametrizados** en vez de SQL inventado libremente por el LLM.

### Archivos EXCLUSIVOS a crear/modificar
- **Crear:** `src/HiveOps.Infrastructure/Support/PlaybookRunner.cs` (implementa `IPlaybookRunner`)
  - `FindMatchingPlaybookAsync`: busca en `AppDbContext.TenantPlaybooks` los playbooks activos del tenant; matchea por `preferredCode` exacto o por solapamiento de keywords con `TriggerSymptoms`.
  - `ExecuteAsync`:
    1. Verifica que todos los parámetros listados en `RequiredParametersJson` estén presentes en `extractedArgs` (extraídos de `incident.ExtractedEntitiesJson` o del texto del incidente por regex `(?<key>\w+)\s*[:=#]\s*(?<val>[\w\-]+)`).
    2. Si `ActionType == "SqlTemplate"`: reemplaza los placeholders `@paramName` usando **`SqlParameter` reales** (nunca interpolación de strings cruda), valida el SQL resultante con `SafetyValidator.IsSafeSql`, hace dry-run en transacción y verifica que `rowsAffected <= playbook.MaxAffectedRows`.
    3. Si `playbook.RequiresApproval` es `true`, no comitea y devuelve `Executed = false` con resumen para cola de aprobación.
    4. Si no requiere aprobación, ejecuta dentro de transacción, y si `VerificationSqlQuery` está definida, la corre en la misma conexión para confirmar el resultado.
- **Crear controlador API:** `src/HiveOps.Api/Controllers/PlaybooksController.cs`
  - Ruta `[Route("api/support/playbooks")]` con `[Authorize]`.
  - `GET /api/support/playbooks` — lista playbooks del tenant efectivo.
  - `POST /api/support/playbooks` — crea un playbook para el tenant.
  - `PUT /api/support/playbooks/{id:guid}` — actualiza playbook.
  - `DELETE /api/support/playbooks/{id:guid}` — desactiva (`IsActive = false`).
  - `POST /api/support/playbooks/{id:guid}/dry-run` — prueba el playbook con parámetros de ejemplo sin comitear.
- **Crear tests:** `tests/HiveOps.UnitTests/Support/PlaybookRunnerTests.cs`

### Verificación de WP-2
```powershell
dotnet test tests/HiveOps.UnitTests/HiveOps.UnitTests.csproj --filter "FullyQualifiedName~PlaybookRunnerTests"
```

---

## WP-3 — Reversibilidad Quirúrgica de Datos: Before/After JSON Diff + Inverse SQL (Wave 1)

**Objetivo para el agente:** Reemplazar el backup por tablas sueltas (`SELECT * INTO Backups_...`) como mecanismo primario por un **Snapshot estructurado a nivel fila** (`IncidentRemediationSnapshot`) que guarda el JSON exacto de las filas antes y después del cambio y genera el script `InverseSqlScript` exacto por clave primaria (`UPDATE ... SET col = @oldVal WHERE pk = @pkVal`), permitiendo rollback instantáneo y visualización de diff en el dashboard.

### Archivos EXCLUSIVOS a crear/modificar
- **Crear:** `src/HiveOps.Infrastructure/Persistence/RowLevelSnapshotService.cs` (implementa `IRowLevelSnapshotService`)
  - `CaptureBeforeSnapshotAsync`:
    1. Usa `SqlStatementParser.ExtractTableName(forwardSql)` y `SqlStatementParser.ExtractWhereClause(forwardSql)`.
    2. Consulta las columnas Primary Key reales de la tabla en la BD del tenant vía `INFORMATION_SCHEMA.TABLE_CONSTRAINTS` + `KEY_COLUMN_USAGE`. Si la tabla no tiene PK declarada, usa todas las columnas no-texto como clave compuesta de respaldo.
    3. Ejecuta `SELECT TOP (500) * FROM {table} {whereClause}` antes de aplicar el fix y serializa cada fila como diccionario JSON (`BeforeRowsJson`).
    4. Si la sentencia es `UPDATE`, genera automáticamente un `UPDATE {table} SET {col1}={oldVal1}, ... WHERE {pk}={pkVal}` por cada fila capturada y lo guarda en `InverseSqlScript`. Si es `DELETE`, genera los `INSERT INTO {table} (...) VALUES (...)` correspondientes. Si es `INSERT` con PK literal, genera el `DELETE FROM {table} WHERE ...`.
    5. Persiste `IncidentRemediationSnapshot` en `AppDbContext`.
  - `PopulateAfterSnapshotAsync`:
    1. Re-ejecuta el `SELECT TOP (500) * FROM {table} {whereClause}` luego de aplicar el fix y guarda `AfterRowsJson`.
  - `RevertSnapshotAsync`:
    1. Ejecuta `snapshot.InverseSqlScript` dentro de una transacción en la BD del tenant, marca `snapshot.IsReverted = true`, `RevertedAt = DateTimeOffset.UtcNow`, `RevertedBy = revertedBy`, y guarda en `AppDbContext`.
- **Crear tests:** `tests/HiveOps.UnitTests/Support/RowLevelSnapshotServiceTests.cs` (testeando el generador de SQL inverso y serialización de diffs con tablas en memoria / parser).

### Verificación de WP-3
```powershell
dotnet test tests/HiveOps.UnitTests/HiveOps.UnitTests.csproj --filter "FullyQualifiedName~RowLevelSnapshotServiceTests"
```

---

## WP-4 — Autonomous SWE Loop: Test de Regresión + Gate HiveArena + Pull Request Real (Wave 1)

**Objetivo para el agente:** Hacer que cuando HiveOps toca código:
1. Genere o actualice un **test unitario** que reproduzca el bug además del fix de producción.
2. Ejecute `dotnet test` localmente en el workspace sobre el test afectado.
3. Envíe la rama a **HiveArena** y **espere el veredicto (`Survival`)** antes de abrir PR o desplegar.
4. Cree un **Pull Request real** en GitHub / Azure DevOps con la hipótesis, evidencia, diff y resultado de HiveArena.

### Archivos EXCLUSIVOS a crear/modificar
- **Crear:** `src/HiveOps.Application/Support/CodePatchAndTestGenerator.cs`
  - Helper puro que construye el prompt de ingeniería para el LLM pidiendo dos secciones claramente delimitadas:
    - `[PRODUCTION_PATCH]` (unified diff o `<<<<<<< SEARCH` / `>>>>>>> REPLACE` sobre archivos de `src/`)
    - `[REGRESSION_TEST_PATCH]` (opcional pero solicitado cuando existe carpeta `tests/` en el workspace: un método `[Fact]` que valide el caso borde reportado)
  - Extrae ambos conjuntos de cambios y el filtro de nombre del test (`FullyQualifiedName~...`) para correr `dotnet test --filter`.
- **Modificar:** `src/HiveOps.Infrastructure/Deployment/PipelineDeploymentService.cs`
  - Implementar `CreatePullRequestAsync(Guid tenantId, string branchName, string title, string body, CancellationToken ct)`:
    - Lee configuración `DeployGit:Provider` (`github` | `azdo`), `DeployGit:Owner`, `DeployGit:Repo`, `DeployGit:ApiToken` (o secret ref del tenant).
    - Si hay token configurado y `Provider == "github"`, hace `POST https://api.github.com/repos/{owner}/{repo}/pulls` con `head = branchName`, `base = defaultBranch`, `title`, `body` y devuelve el `html_url` del PR.
    - Si no hay credenciales externas configuradas (modo local/dev), registra warning y devuelve `$"local-pr://{branchName}"` sin romper el flujo.
- **Crear tests:** `tests/HiveOps.UnitTests/Support/CodePatchAndTestGeneratorTests.cs`

### Verificación de WP-4
```powershell
dotnet test tests/HiveOps.UnitTests/HiveOps.UnitTests.csproj --filter "FullyQualifiedName~CodePatchAndTestGeneratorTests"
```

---

## WP-6 — Target System Onboarding Doctor + Outcome Metering (Wave 1)

**Objetivo para el agente:** Resolver los dos bloqueadores comerciales clave:
1. **Onboarding Doctor:** un endpoint y servicio que valida en 1 click si un tenant nuevo está listo para soporte autónomo L1/L2/L3 (conexión a su BD, permisos de lectura de `INFORMATION_SCHEMA`, acceso al repo Git y workspace, configuración de LLM y webhook de canal).
2. **Outcome Metering:** contabilizar únicamente **resoluciones verificadas** (cuando el usuario confirma `btn_confirm_resolved` o cuando `VerificationPassed == true` y no hubo rollback) para facturación outcome-based.

### Archivos EXCLUSIVOS a crear/modificar
- **Crear:** `src/HiveOps.Infrastructure/Services/OutcomeMeteringService.cs` (implementa `IOutcomeMeteringService`)
  - `RecordVerifiedResolutionAsync`: registra un `DiagnosticLog` con `StepName = "billing.verified_resolution"` con metadatos (`ResolutionMode`, `Category`, `Confidence`, `Timestamp`) de forma idempotente (máximo 1 cobro por `IncidentId`).
  - `GetSummaryAsync`: calcula para un rango de fechas `TotalIncidents`, `VerifiedAutonomousResolutions` (`AutoDatabase`, `AutoCode`, `Playbook`), `AssistedResolutions` y `RollbacksCount`.
- **Crear:** `src/HiveOps.Api/Controllers/TenantReadinessController.cs`
  - Ruta `[Route("api/support/readiness")]` con `[Authorize]`.
  - `GET /api/support/readiness/check`: ejecuta y devuelve un reporte JSON con 5 checks independientes:
    1. `targetDatabase`: resuelve connection string del tenant y corre `SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES`.
    2. `gitWorkspace`: verifica `IGitService.GetWorkspacePathAsync` y que el directorio exista y sea un repo git válido.
    3. `knowledgeBase`: cuenta artículos publicados y si `IEmbeddingService` responde.
    4. `playbooks`: cuenta `TenantPlaybooks` activos del tenant.
    5. `channels`: verifica si WhatsApp o Ticketing están configurados para el tenant.
  - `GET /api/support/readiness/outcomes?from=...&to=...`: devuelve el `TenantOutcomeSummary` de `IOutcomeMeteringService` + el estado de la cadena de auditoría.
- **Crear tests:** `tests/HiveOps.UnitTests/Support/OutcomeMeteringServiceTests.cs`

### Verificación de WP-6
```powershell
dotnet test tests/HiveOps.UnitTests/HiveOps.UnitTests.csproj --filter "FullyQualifiedName~OutcomeMeteringServiceTests"
```

---

## WP-5 — Integración Final: `IncidentAutoWorkflowService` + Cryptographic Audit Ledger + Rollback Quirúrgico en Dashboard (Wave 2)

**Objetivo para el agente:** Conectar todos los módulos de Wave 1 dentro del flujo principal y habilitar la trazabilidad SHA-256 inmutable y el rollback por fila desde el Dashboard.

### Archivos a crear/modificar
1. **Crear:** `src/HiveOps.Infrastructure/Services/IncidentAuditLedger.cs` (implementa `IIncidentAuditLedger`)
   - Calcula el hash SHA-256 encadenado buscando el último `IncidentAuditEntry` del incidente (`PreviousHash`, o `"GENESIS"` para `SequenceNumber = 1`).
   - Implementa `VerifyChainAsync` recalculando todos los hashes en orden y detectando cualquier adulteración.
2. **Modificar:** `src/HiveOps.Infrastructure/ServiceCollectionExtensions.cs` y `src/HiveOps.Agents/AgentsServiceCollectionExtensions.cs`
   - Registrar en DI:
     - `ITargetLogCollector` → `TargetDatabaseLogCollector`
     - `IRootCauseHypothesisService` → `RootCauseHypothesisService`
     - `IPlaybookRunner` → `PlaybookRunner`
     - `IRowLevelSnapshotService` → `RowLevelSnapshotService`
     - `IIncidentAuditLedger` → `IncidentAuditLedger`
     - `IOutcomeMeteringService` → `OutcomeMeteringService`
3. **Modificar:** `src/HiveOps.Workers/IncidentAutoWorkflowService.cs`
   - En `ExecuteDiagnosticsAsync`:
     - Invocar `IRootCauseHypothesisService.InvestigateAsync(incident, cancellationToken)`.
     - Guardar `incident.RootCauseJson = JsonSerializer.Serialize(bundle)` y `incident.VerificationQuery = bundle.VerificationSqlQuery`.
     - Registrar entrada `"Hypothesis"` en `IIncidentAuditLedger`.
   - En `ProcessIncidentAsync`:
     - Si `bundle.RecommendedTrack == "Playbook"` (o hay match en `IPlaybookRunner.FindMatchingPlaybookAsync`), intentar primero el Playbook L1/L2 antes de generar SQL o código libre.
   - En `ApplyDatabaseFixAsync`:
     - Capturar snapshot previo con `IRowLevelSnapshotService.CaptureBeforeSnapshotAsync` y, tras ejecutar el SQL, llamar a `PopulateAfterSnapshotAsync`.
     - Si `incident.VerificationQuery` no es nula, llamar a `VerifyPostFixConditionAsync` y guardar `incident.VerificationPassed`. Si la verificación falla, ejecutar automáticamente `RevertSnapshotAsync` y escalar a ingeniero con la evidencia.
   - En `ApplyCodeFixAsync`:
     - Usar `CodePatchAndTestGenerator` para aplicar también el test de regresión si fue generado y correr `dotnet test --no-build --filter ...` cuando corresponda.
     - Si `HiveArena:Publisher:Enabled` es `true`, dejar el incidente con `ArenaValidationStatus = "Pending"` y **esperar** al callback de `InternalHiveArenaCallbacksController` antes de llamar a `CreatePullRequestAsync` y `DeployFixAsync`. Si HiveArena está deshabilitado, llamar a `CreatePullRequestAsync` y guardar `incident.PullRequestUrl`.
4. **Modificar:** `src/HiveOps.Api/Controllers/InternalHiveArenaCallbacksController.cs`
   - Cuando `body.Outcome == 0` (`Survival`), actualizar `incident.ArenaValidationStatus = "Survived"`, abrir el PR con `IDeploymentService.CreatePullRequestAsync` y registrar en `IIncidentAuditLedger`.
5. **Modificar:** `src/HiveOps.Api/Controllers/SupportDashboardController.cs` y `src/HiveOps.Agents/Orchestration/AgentRulesEngine.cs`
   - Agregar endpoints en `SupportDashboardController`:
     - `GET /api/support/incidents/{id:guid}/snapshots` (devuelve los `IncidentRemediationSnapshots` con el diff `BeforeRowsJson` vs `AfterRowsJson` y el `InverseSqlScript`).
     - `POST /api/support/incidents/{id:guid}/snapshots/{snapshotId:guid}/revert` (ejecuta `IRowLevelSnapshotService.RevertSnapshotAsync` y registra en `IIncidentAuditLedger`).
     - `GET /api/support/incidents/{id:guid}/audit-chain` (devuelve las entradas de `IncidentAuditEntries` y el resultado de `VerifyChainAsync`).
   - En `AgentRulesEngine` (`btn_confirm_resolved`): invocar `IOutcomeMeteringService.RecordVerifiedResolutionAsync` y `IIncidentAuditLedger.AppendAsync(..., "ConfirmedClosed", ...)`.

### Verificación Final de WP-5 (Toda la solución)
```powershell
dotnet build HiveOps.slnx
dotnet test HiveOps.slnx
```
