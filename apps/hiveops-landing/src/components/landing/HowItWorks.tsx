import { AnimatedSection } from "./AnimatedSection";

const steps = [
  {
    number: "01",
    badge: "ENTRADA",
    title: "1. Entra el problema",
    text: "El reporte llega por WhatsApp, tu sistema de tickets o una integración. HiveOps lo ordena y lo asocia a la cuenta correcta.",
  },
  {
    number: "02",
    badge: "ANÁLISIS",
    title: "2. Encuentra la causa",
    text: "Investiga en paralelo: tus datos, registros de errores e historial de cambios. Entrega un diagnóstico con evidencia y nivel de confianza.",
  },
  {
    number: "03",
    badge: "SOLUCIÓN",
    title: "3. Elige cómo resolverlo",
    text: "Una guía conocida, una corrección de datos con copia de seguridad, un arreglo de código probado — o lo deriva a una persona con todo el contexto.",
  },
  {
    number: "04",
    badge: "PRUEBA",
    title: "4. Lo ensaya antes de aplicar",
    text: "Cada cambio se prueba primero en un entorno aislado. Si no funciona, no se aplica.",
  },
  {
    number: "05",
    badge: "TU CONTROL",
    title: "5. Vos aprobás",
    text: "Según las reglas que configures, un responsable aprueba la acción desde el panel. Sin aprobación, no se ejecuta nada.",
  },
  {
    number: "06",
    badge: "RESULTADO",
    title: "6. Resolución verificada",
    text: "Se comprueba automáticamente que el problema quedó resuelto, queda registrado — y es lo único que se factura.",
  },
];

export function HowItWorks() {
  return (
    <AnimatedSection className="px-6 py-16">
      <div id="como-funciona" className="mx-auto w-full max-w-6xl scroll-mt-24">
        <div className="mb-10 max-w-2xl">
          <span className="hive-badge-cyan mb-3">Cómo resuelve</span>
          <h2 className="text-3xl font-bold md:text-4xl">
            Del reporte a la <span className="cyan-text">solución verificada</span>, con tu control en cada paso
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
