import { AnimatedSection } from "./AnimatedSection";

const values = [
  {
    tag: "RESOLUCIÓN VERIFICADA",
    title: "Resuelve de punta a punta, no deflecta tickets",
    text: "Cada caso termina con una verification query o una suite de tests que confirma que el incidente quedó resuelto — no con una respuesta genérica que patea el problema.",
  },
  {
    tag: "CAUSA RAÍZ MULTI-FUENTE",
    title: "Investiga en schema SQL, logs del tenant y git blame",
    text: "Arma un RootCauseBundle con evidencia cruzada: estructura de base de datos, logs de la app y el diff que introdujo el bug. La confianza del diagnóstico se reporta explícita.",
  },
  {
    tag: "REMEDIACIÓN GOBERNADA",
    title: "Dry-run, aprobaciones y snapshots reversibles",
    text: "Nada se aplica sin pasar por el nivel de aprobación configurado. Los fixes de datos llevan snapshot fila a fila y SQL inverso generado para volver atrás en segundos.",
  },
  {
    tag: "AUDIT TRAIL SHA-256",
    title: "Cada acción queda firmada en un ledger encadenado",
    text: "Triage, diagnóstico, aprobación, ejecución y verificación se registran en una cadena criptográfica inmutable. Auditá quién aprobó qué, cuándo y con qué evidencia.",
  },
  {
    tag: "MENOS MTTR, MENOS HANDOFFS",
    title: "El tiempo de resolución cae sin quemar a tu equipo",
    text: "El agente atiende 24/7, resuelve lo que puede y escala a humanos solo con el contexto completo del caso. Tu equipo deja de hacer triage manual y pases de mano.",
  },
  {
    tag: "KB QUE APRENDE",
    title: "Cada resolución alimenta la knowledge base",
    text: "Los fixes validados se convierten en playbooks y entradas de KB reutilizables. La próxima vez que aparezca el mismo patrón, se resuelve más rápido y con más confianza.",
  },
];

export function ValueGrid() {
  return (
    <AnimatedSection className="px-6 py-16">
      <div className="mx-auto w-full max-w-6xl">
        <div className="mb-10 max-w-3xl">
          <span className="hive-badge-cyan mb-3">Por qué eligen HiveOps</span>
          <h2 className="text-3xl font-bold md:text-4xl">
            No es un chatbot que deriva tickets.{" "}
            <span className="cyan-text">Es un ingeniero de soporte que cierra incidentes.</span>
          </h2>
        </div>

        <div className="grid gap-5 md:grid-cols-2">
          {values.map((value) => (
            <article key={value.title} className="surface-card surface-card-hover rounded-2xl p-6 sm:p-7">
              <span className="mb-3 inline-block rounded-md border border-cyan-500/30 bg-cyan-500/10 px-2.5 py-1 font-mono text-[11px] font-semibold uppercase tracking-wider text-cyan-300">
                {value.tag}
              </span>
              <h3 className="mb-2 text-xl font-bold text-white">{value.title}</h3>
              <p className="text-sm leading-relaxed text-slate-300 sm:text-base">{value.text}</p>
            </article>
          ))}
        </div>

        {/* ROI Banner */}
        <div className="mt-8 flex flex-col items-start justify-between gap-4 rounded-2xl border border-cyan-500/35 bg-gradient-to-r from-cyan-500/15 via-cyan-500/5 to-transparent p-6 sm:flex-row sm:items-center">
          <div>
            <p className="font-mono text-xs uppercase tracking-widest text-cyan-300">Modelo por resultado</p>
            <p className="mt-1 font-display text-lg font-semibold text-white sm:text-xl">
              “Pagás por incidente resuelto y verificado — no por seats ni por tickets derivados.”
            </p>
          </div>
          <span className="shrink-0 rounded-full border border-cyan-400/40 bg-cyan-400/15 px-4 py-1.5 font-mono text-xs font-semibold text-cyan-200">
            Outcome metering · solo resultados
          </span>
        </div>
      </div>
    </AnimatedSection>
  );
}
