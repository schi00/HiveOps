# SaaSBot Support Agent — Especificación de Implementación

## Contexto

Adaptar SaaSBot (.NET 9, Semantic Kernel, EF Core, Redis, MediatR) a un bot de resolución de incidentes de soporte técnico.

## Objetivo

1. Recibir y analizar incidentes de soporte (tickets de código y base de datos)
2. Preguntar al usuario cuando falte información crítica
3. Diagnosticar problemas de código y de base de datos
4. Aplicar correcciones con salvaguardas obligatorias:
   - Nunca ejecutar DELETE/UPDATE sin `WHERE` con clave primaria
   - Nunca hacer ALTER/DROP sin confirmación explícita del usuario
   - Todo cambio en DB en transacción con rollback posible
   - Todo cambio de código en rama Git y pasar por PR
5. Crear commits y branches en Git para fixes de código
6. Enviar a deployment (CI/CD) solo después de aprobación
7. **Etapa 1**: el código a soportar es el propio SaaSBot (autoadministración)

## Arquitectura

### Nuevas Entidades de Dominio

- `Incident` — ticket principal
- `IncidentAttachment` — logs, stack traces, screenshots
- `DiagnosticLog` — pasos de diagnóstico ejecutados

### Nuevos Plugins

- `SupportPlugin` — análisis, diagnóstico, propuesta de fixes, deploy
- `SelfSupportPlugin` — auto-soporte (lee fuentes propias, verifica DB, ejecuta tests)

### Servicios

- `SafetyValidator` — valida que SQL/scripts sean seguros antes de ejecutar
- `IGitService` / `LocalGitService` — operaciones Git
- `IDeploymentService` / `PipelineDeploymentService` — trigger de deploy

### Router Extension

Nuevas intenciones: `IncidentReport`, `IncidentQuery`, `IncidentApprove`, `IncidentReject`, `DeployRequest`

### Tool Catalog Extension

Nuevas herramientas: `analyze_incident`, `query_database_diagnostic`, `propose_code_fix`, `propose_db_fix`, `deploy_fix`, `read_own_source`, `run_unit_tests`, `escalate_to_engineer`

## Restricciones de Seguridad (Hard Rules)

1. No ejecutar DELETE/UPDATE sin WHERE con clave primaria
2. No modificar más de 1 fila sin confirmación explícita del usuario
3. No hacer commit en main/master — siempre rama `support/*`
4. No deployear sin tests verdes y aprobación explícita
5. No exponer credenciales de DB/Git en logs ni mensajes de chat

## Entregables

1. Nuevas entidades + migration EF Core
2. `SupportPlugin.cs` + `SelfSupportPlugin.cs`
3. `SafetyValidator.cs`
4. `IGitService` + `IDeploymentService` interfaces + implementaciones
5. Cambios en `RouterPlugin`, `AgentToolCatalog`, `IncomingMessageHandler`, DI
6. Tests unitarios
7. Scripts SQL en `db/`
