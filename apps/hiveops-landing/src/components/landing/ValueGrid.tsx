import { AnimatedSection } from "./AnimatedSection";

const values = [
  {
    tag: "SOLO RESULTADOS",
    title: "Resuelve de punta a punta, no solo deriva tickets",
    text: "Cada caso termina con una verificación automática que confirma que el problema quedó resuelto — no con una respuesta genérica que patea el problema.",
  },
  {
    tag: "ENCUENTRA LA CAUSA",
    title: "Investiga en tus datos, registros e historial de cambios",
    text: "Cruza evidencia de varias fuentes para llegar al origen real del problema, y te dice con qué nivel de confianza llegó a ese diagnóstico.",
  },
  {
    tag: "BAJO TU CONTROL",
    title: "Todo se simula, se aprueba y se puede revertir",
    text: "Nada se aplica sin pasar por el nivel de aprobación que configures. Cada cambio de datos lleva su copia de seguridad para volver atrás en segundos.",
  },
  {
    tag: "TODO REGISTRADO",
    title: "Cada acción queda registrada y sellada",
    text: "Análisis, aprobación, ejecución y verificación quedan en un registro inalterable. Auditá quién aprobó qué, cuándo y con qué evidencia.",
  },
  {
    tag: "MENOS TIEMPO, MENOS DERIVACIONES",
    title: "Resuelve más rápido sin quemar a tu equipo",
    text: "El agente atiende 24/7, resuelve lo que puede y deriva a personas solo con el contexto completo del caso. Tu equipo deja de clasificar tickets a mano.",
  },
  {
    tag: "APRENDE DE CADA CASO",
    title: "Cada resolución lo hace más inteligente",
    text: "Las soluciones validadas se convierten en guías reutilizables. La próxima vez que aparezca el mismo problema, se resuelve más rápido y con más confianza.",
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
              “Pagás por problema resuelto y verificado — no por usuarios ni por tickets derivados.”
            </p>
          </div>
          <span className="shrink-0 rounded-full border border-cyan-400/40 bg-cyan-400/15 px-4 py-1.5 font-mono text-xs font-semibold text-cyan-200">
            Solo resultados verificados
          </span>
        </div>
      </div>
    </AnimatedSection>
  );
}
