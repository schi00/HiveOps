# HiveOps

HiveOps es una plataforma **multi-tenant** de automatización conversacional y soporte técnico, diseñada para operar en modo **SaaS**, **Self-Hosted** o **Ephemeral** sobre **.NET 9**.

Su objetivo es combinar:
- Atención conversacional omnicanal (incluyendo WhatsApp).
- Flujos de soporte/incidentes con trazabilidad.
- Planeación híbrida (heurísticas + LLM) con guardrails.
- Seguridad por defecto (aislamiento por tenant, validaciones y controles operativos).

---

## ¿Qué resuelve HiveOps?

HiveOps permite que un negocio o equipo técnico:
1. Reciba mensajes de clientes por canales integrados.
2. Clasifique intención y estado conversacional.
3. Ejecute acciones automáticas seguras (información, soporte, handoff, diagnóstico).
4. Escale a humano cuando aplica.
5. Mantenga contexto, auditoría y estado por tenant.

Además, para soporte técnico avanzado, incluye componentes para:
- Análisis de incidentes.
- Propuesta de fixes (código/DB) bajo restricciones de seguridad.
- Flujos de aprobación y despliegue.

---

## Arquitectura del repositorio

### Capas principales

- `src/HiveOps.Api`
  - API HTTP, autenticación/autorización, webhooks, dashboard y middleware.
- `src/HiveOps.Agents`
  - Orquestación conversacional, plugins de agentes, planner y runtime.
- `src/HiveOps.Application`
  - Contratos de aplicación, opciones/configuración y reglas transversales.
- `src/HiveOps.Domain`
  - Entidades, enums, modelos de dominio e interfaces núcleo.
- `src/HiveOps.Infrastructure`
  - Persistencia, multitenancy, integración AI, messaging, Git/deploy, secretos.
- `src/HiveOps.Workers`
  - Procesos de fondo (ej. auto-workflow de incidentes).
- `tests`
  - Unit, integration y e2e.
- `db`
  - Scripts SQL de bootstrap, migraciones y seeds.
- `docs`
  - Runbooks y operación.

### Principios operativos clave

- **Tenant isolation first**: cada operación se ejecuta en contexto de tenant.
- **Fail-closed en rutas críticas**: ante ambigüedad de seguridad, falla de forma explícita.
- **Guardrails para decisiones de agentes/LLM**: validación previa a ejecución.
- **Configuración por entorno y modo de despliegue**.

---

## Flujo de una conversación (vista de alto nivel)

1. Llega un mensaje de usuario al API/webhook.
2. Se resuelve la conversación (crear/actualizar) y se persiste el mensaje.
3. Se carga estado conversacional (FSM) y contexto del tenant.
4. El orquestador clasifica intención y decide ruta.
5. El planner/rules selecciona respuesta, herramienta o escalamiento.
6. Se ejecuta acción segura (si aplica) y se guarda resultado.
7. Se envía respuesta al canal y se persiste trazabilidad.

---

## Intervención de cada agente dentro de HiveOps

A continuación se describe el rol de cada componente de agente en tiempo de ejecución.

### 1) `IncomingMessageHandler` (Orquestador principal)

Archivo clave: `src/HiveOps.Agents/Orchestration/IncomingMessageHandler.cs`

Responsabilidades:
- Punto central de entrada para mensajes.
- Deduplicación de mensajes externos.
- Resolución/creación de conversación.
- Persistencia de entrada y salida.
- Carga de estado conversacional y memoria por cliente.
- Registro de plugins en el `Kernel`.
- Enrutamiento entre flujo determinista, planner y soporte.
- Disparo de supervisión/escalado según señales.

En términos prácticos, es el “director de orquesta” que decide qué agente interviene y cuándo.

---

### 2) `RouterPlugin` (Clasificación de intención)

Archivo clave: `src/HiveOps.Agents/Router/RouterPlugin.cs`

Responsabilidades:
- Clasificar el mensaje entrante en intenciones como:
  - `Greeting`
  - `HumanHandoff`
  - `IncidentReport`
  - `IncidentQuery`
  - `IncidentApprove`
  - `IncidentReject`
  - `DeployRequest`
  - `ThankYou`
  - `Farewell`
  - `Unknown`
- Priorizar heurísticas locales cuando hay alta certeza.
- Usar fallback por prompt LLM si no hay clasificación robusta.
- Devolver salida segura (`Unknown`) ante fallo de invocación.

Resultado: reduce ambigüedad temprana y evita derivaciones erróneas en el flujo.

---

### 3) `AgentRulesEngine` (Reglas deterministas)

Archivo clave: `src/HiveOps.Agents/Orchestration/AgentRulesEngine.cs`

Responsabilidades:
- Aplicar reglas deterministas de negocio y estado.
- Resolver interacciones de botones/listas y acciones directas.
- Evitar que cada decisión dependa exclusivamente de LLM.

Resultado: respuestas más predecibles y estables para casos recurrentes.

---

### 4) Planner híbrido (decisión de siguiente paso)

Archivos clave:
- `src/HiveOps.Agents/Planning/HeuristicPlanner.cs`
- `src/HiveOps.Agents/Planning/LlmPlanner.cs`
- `src/HiveOps.Agents/Planning/HybridAgentPlanner.cs`
- `src/HiveOps.Agents/Planning/PromptBuilder.cs`

Cómo intervienen:
- `HeuristicPlanner`: intenta resolver con reglas de alta confianza (ej. pedido explícito de humano).
- `HybridAgentPlanner`: decide si quedarse con heurística o invocar LLM según configuración y tenant.
- `LlmPlanner`: genera una decisión estructurada (tool_call/respond/escalate).
- `PromptBuilder`: construye prompt contextual con tono, negocio y variables del tenant.

Resultado: equilibrio entre rapidez determinista y flexibilidad semántica.

---

### 5) `AgentRuntime` + `PlannerDecisionGuard` (ejecución controlada)

Archivos clave:
- `src/HiveOps.Agents/Planning/AgentRuntime.cs`
- `src/HiveOps.Agents/Planning/PlannerDecisionGuard.cs`
- `src/HiveOps.Agents/Planning/ToolResolver.cs`
- `src/HiveOps.Agents/Planning/AgentToolCatalog.cs`

Responsabilidades:
- Ejecutar un ciclo de planificación por pasos (`MaxSteps`) con límites.
- Validar cada decisión antes de ejecutar:
  - Tipo de decisión válido.
  - Herramienta permitida.
  - Estado permitido.
  - Argumentos requeridos.
- Resolver catálogo de herramientas por tenant/estado/prioridad.
- Mantener historial de pasos y transiciones de estado.

Resultado: evita alucinaciones operativas y llamadas peligrosas/inconsistentes.

---

### 6) `SupportPlugin` (Incidentes y remediación)

Archivo clave: `src/HiveOps.Agents/Support/SupportPlugin.cs`

Responsabilidades:
- Crear/actualizar incidentes y clasificar severidad/categoría.
- Pedir información faltante para diagnóstico.
- Ejecutar diagnósticos SQL seguros (solo permitido por validador).
- Proponer fixes (código o DB) con flujo de aprobación.
- Coordinar integración con Git, despliegue y notificaciones.

Resultado: soporte técnico accionable con trazabilidad y controles de seguridad.

---

### 7) `SelfSupportPlugin` (Autodiagnóstico del bot)

Archivo clave: `src/HiveOps.Agents/Support/SelfSupportPlugin.cs`

Responsabilidades:
- Leer y buscar en su propio código fuente con validaciones de ruta.
- Ejecutar chequeos básicos de salud de base de datos.
- Correr pruebas unitarias de forma controlada.

Controles destacados:
- Bloqueo de path traversal.
- Restricción a directorios permitidos (`src`, `tests`, `db`, `.github`).
- Sanitización de patrones de búsqueda.

Resultado: capacidad de autoinspección sin abrir brechas de seguridad.

---

### 8) `SupervisionPlugin` (Monitoreo y handoff humano)

Archivo clave: `src/HiveOps.Agents/Supervision/SupervisionPlugin.cs`

Responsabilidades:
- Evaluar frustración del usuario (sentimiento/señales).
- Solicitar escalamiento a humano.
- Marcar conversación como `AwaitingHuman` y sincronizar estado.

Resultado: protege experiencia de usuario cuando la automatización ya no es suficiente.

---

### 9) `IncidentAutoWorkflowService` (Worker de incidentes)

Archivo clave: `src/HiveOps.Workers/IncidentAutoWorkflowService.cs`

Responsabilidades:
- Procesar incidentes en background.
- Ejecutar secuencia de análisis/diagnóstico/propuesta.
- Respetar límites operativos y flujos de aprobación.

Resultado: automatiza backlog de incidentes manteniendo control y auditabilidad.

---

## Seguridad y aislamiento (resumen)

- Contexto de tenant obligatorio en ejecución de plugins.
- Validaciones de seguridad en SQL y rutas de archivos.
- Guardrails de planner antes de ejecutar herramientas.
- Modos de despliegue para endurecer comportamiento por entorno.
- En Self-Hosted, política de seguridad enfocada en aislamiento fuerte por tenant.

---

## Configuración de despliegue

HiveOps soporta:
- `SaaS`
- `SelfHosted`
- `Ephemeral`

Y política de billing:
- `None`
- `Stripe`

Referencias:
- `docs/operations.md`
- `src/HiveOps.Application/Configuration/HiveOpsDeploymentOptions.cs`

---

## Estado del proyecto (Consolidación v1.0)

Actualmente el repositorio está orientado a:
- Base sólida multi-tenant.
- Orquestación híbrida de agentes.
- Seguridad por defecto.
- Suite de pruebas para regresión funcional.

---

## Roadmap de trabajo

Este README resume la arquitectura actual para facilitar onboarding técnico y revisión de cambios.  
En la siguiente fase se continuará con los pasos restantes del plan de consolidación/distribución.

---

## Licencia y uso

Si este repositorio se usa en entorno cliente, definir política de licencia, compliance y gestión de secretos antes de producción.
