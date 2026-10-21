# Contrato de medición: efectividad de resolución de incidentes (objetivo 70%)

## KPI principal: `AutoResolutionEffectiveness`

**Definición (auditable):** proporción de incidentes **elegibles** que fueron **cerrados por resolución automática o asistida por bot** (sin retrabajo humano sustitutivo en la ventana de estabilidad), medida sobre una ventana móvil.

### Numerador

Incidentes que cumplen **todas** las condiciones:

1. `EligibleForEffectivenessKpi = 1` al momento del cierre (marcado tras el triage; ver reglas abajo).
2. Estado final `Closed` o `Resolved`.
3. `ResolutionMode` ∈ { `AutoCode`, `AutoDatabase`, `Hybrid` }.
   - `Hybrid`: corrección generada/aplicada por el flujo automático con intervención mínima (p. ej. aprobación explícita del usuario) **sin** reasignación a ingeniero como dueño final del fix.
4. **Estabilidad (recomendado):** no existe un nuevo incidente con el mismo `Fingerprint` abierto en las **24 h** posteriores al cierre (proxy de “no reapertura inmediata”). Si no hay `Fingerprint`, el filtro 4 se omite en el cómputo conservador.

### Denominador

Incidentes **elegibles** en la misma ventana:

1. `EligibleForEffectivenessKpi = 1`.
2. Creados dentro de la ventana de observación (p. ej. últimos **28 días** rodantes).
3. Estado final `Closed`, `Resolved` **o** `Open`/`InProgress`/`AwaitingApproval` con categoría soportada por auto-workflow (`Code`, `Database`, `Infrastructure` con drift) **excluyendo**:
   - incidentes de prueba marcados como no elegibles (futuro flag `IsSynthetic`),
   - severidad `Critical` con escalación forzada (no elegibles para auto-éxito).

### Fórmula

```
AutoResolutionEffectiveness = numerador / denominador
```

**SLO:** `AutoResolutionEffectiveness >= 0.70` en ventana de 28 días, evaluable **por tenant** y global.

### Elegibilidad (`EligibleForEffectivenessKpi`)

Se establece **después del triage LLM** (o equivalente):

| Condición | Elegible |
|-----------|----------|
| `RequiresEscalation = true` (triage) | No |
| Severidad triage `Critical` | No |
| Categoría dominio `Other` y sin ruta auto | No |
| `Confidence` triage < 0.60 | No (se prioriza pedir datos / humano) |
| Categoría `Code` / `Database` / `Infrastructure` (drift), confianza ≥ 0.60, sin escalación forzada | Sí |

Los valores se persisten en la entidad `Incident` para auditoría y agregación SQL.

### KPI secundarios (salud del SLO)

| Métrica | Uso |
|---------|-----|
| `handoff_rate` | Incidentes con `AssignedTo != 'bot'` antes del cierre / elegibles |
| `pending_info_rate` | `PendingInfo` / creados |
| `avg_triage_confidence` | Media de `TriageConfidence` en elegibles |
| `mttr_auto` vs `mttr_human` | Tiempo hasta `ResolvedAt` segmentado por `ResolutionMode` |

### Fuente de datos

- **Primaria:** tabla `Incidents` (`TriageConfidence`, `EligibleForEffectivenessKpi`, `ResolutionMode`, `Fingerprint`, `OccurrenceCount`, `FinalUserResponse`, `TechnicalDiagnosticNote`, timestamps).
- **Complementaria:** `DiagnosticLogs` (`StepName`, JSON en `Result`) para reconstrucción forense y replay de prompts.

### API

- `GET /api/support/effectiveness-metrics?windowDays=28` — agregados alineados a este contrato (ver implementación en `SupportDashboardController`).

### Revisiones

Cualquier cambio en la definición de elegibilidad o modos de resolución debe versionarse en este documento (`Revision: YYYY-MM-DD`) para mantener comparabilidad histórica de series.
