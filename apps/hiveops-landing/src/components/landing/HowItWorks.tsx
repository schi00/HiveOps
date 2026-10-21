import { AnimatedSection } from "./AnimatedSection";

const steps = [
  {
    number: "01",
    badge: "INTAKE",
    title: "1. Entra el incidente",
    text: "El reporte llega por WhatsApp, tu sistema de ticketing o la API. HiveOps normaliza el caso y lo asocia al tenant correcto.",
  },
  {
    number: "02",
    badge: "TRIAGE",
    title: "2. Triage + causa raíz",
    text: "Investiga en paralelo: schema SQL, logs del tenant, git blame y KB. Produce un RootCauseBundle con evidencia y nivel de confianza.",
  },
  {
    number: "03",
    badge: "TRACK",
    title: "3. Elige el track de resolución",
    text: "Playbook L1 conocido, DatabaseFix con snapshot, CodeFix con parche y test — o Escalate a un humano con todo el contexto.",
  },
  {
    number: "04",
    badge: "VALIDACIÓN",
    title: "4. Valida antes de aplicar",
    text: "Los cambios corren en dry-run o en HiveArena (build + tests + sandbox de datos). Si no pasan, no se aplican.",
  },
  {
    number: "05",
    badge: "GOBIERNO",
    title: "5. Confirmación humana",
    text: "Según el umbral configurado, un responsable aprueba la acción desde el dashboard. Sin aprobación, no hay ejecución.",
  },
  {
    number: "06",
    badge: "OUTCOME",
    title: "6. Resolución medida",
    text: "Una verification query confirma el fix, se registra en el ledger SHA-256 y se computa como resolución — la unidad que se factura.",
  },
];

export function HowItWorks() {
  return (
    <AnimatedSection className="px-6 py-16">
      <div id="como-funciona" className="mx-auto w-full max-w-6xl scroll-mt-24">
        <div className="mb-10 max-w-2xl">
          <span className="hive-badge-cyan mb-3">Pipeline de resolución</span>
          <h2 className="text-3xl font-bold md:text-4xl">
            Del reporte al <span className="cyan-text">fix verificado</span>, con gobierno en cada paso
          </h2>
        </div>
        <div className="grid gap-5 md:grid-cols-2 lg:grid-cols-3">
          {steps.map((step) => (
            <article
              key={step.title}
              className="surface-card surface-card-hover pipeline-step relative overflow-hidden rounded-2xl p-6 sm:p-7"
            >
              <div className="mb-4 flex items-center justify-between">
                <span className="font-mono text-3xl font-extrabold text-[var(--cyan-primary)] [text-shadow:0_0_18px_rgba(0,240,255,0.45)]">
                  {step.number}
                </span>
                <span className="rounded-full border border-cyan-500/25 bg-cyan-500/10 px-2.5 py-0.5 font-mono text-[10px] font-semibold uppercase tracking-wider text-cyan-200">
                  {step.badge}
                </span>
              </div>
              <h3 className="mb-2.5 text-xl font-bold text-white">{step.title}</h3>
              <p className="text-sm leading-relaxed text-slate-300 sm:text-base">{step.text}</p>
            </article>
          ))}
        </div>
      </div>
    </AnimatedSection>
  );
}
