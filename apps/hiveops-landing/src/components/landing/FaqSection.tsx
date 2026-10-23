import { AnimatedSection } from "./AnimatedSection";

const faqItems = [
  {
    q: "¿El agente escribe en mi base de datos productiva?",
    a: "Solo cuando vos lo autorizás. Primero investiga con acceso de solo lectura. Cada corrección de datos se valida, se ensaya, genera una copia de seguridad con su camino de vuelta, y se aplica únicamente si supera la aprobación que configuraste.",
  },
  {
    q: "¿Qué pasa si la solución es incorrecta?",
    a: "Cada cambio nace con su vuelta atrás: la copia previa permite revertir en segundos. Si la verificación no confirma el resultado, el caso se revierte y pasa a una persona con todo el contexto.",
  },
  {
    q: "¿Cómo se cobra?",
    a: "Modelo híbrido: una suscripción base mensual por plan (Starter USD 200 fijo con hasta 2 cuentas, Pro USD 499–899 con 5–10 cuentas, Enterprise USD 1.499–3.000+ con cuentas ilimitadas) más cobro por resolución autónoma verificada por encima del cupo incluido. Enterprise también admite licencia anual en tu infraestructura (USD 10.000–50.000+). Solo se computan los problemas que HiveOps cerró y quedaron confirmados automáticamente. Las derivaciones a tu equipo no facturan.",
  },
  {
    q: "¿Qué necesito para empezar?",
    a: "Un canal de entrada (WhatsApp, sistema de tickets o integración), acceso de solo lectura a tu base de datos y tu repositorio de código. En el arranque revisamos tu entorno y configuramos guías de acción y límites de aprobación con vos.",
  },
  {
    q: "¿Mis datos están separados de otros clientes?",
    a: "Sí, desde la base del diseño: cada cuenta opera completamente aislada. Los datos, credenciales y hallazgos de un cliente nunca se cruzan con los de otro.",
  },
  {
    q: "¿Puede instalarse en mi propia infraestructura?",
    a: "Sí. El plan Enterprise permite una instalación dedicada o en tus propios servidores, con soporte asistido y SLA acordado. También existe el modo nube compartida con separación estricta.",
  },
];

export function FaqSection() {
  return (
    <AnimatedSection className="px-6 py-16">
      <div className="mx-auto w-full max-w-5xl">
        <div className="mb-8">
          <span className="hive-badge-cyan mb-3">Resolvé tus dudas</span>
          <h2 className="text-3xl font-bold md:text-4xl">Preguntas frecuentes</h2>
        </div>
        <div className="space-y-3">
          {faqItems.map((item) => (
            <details key={item.q} className="surface-card surface-card-hover group rounded-2xl p-5">
              <summary className="flex cursor-pointer list-none items-center justify-between gap-4 font-display text-lg font-semibold text-white">
                <span>{item.q}</span>
                <span className="font-mono text-cyan-400 transition-transform group-open:rotate-45">+</span>
              </summary>
              <p className="mt-3 text-sm leading-relaxed text-slate-300 sm:text-base">{item.a}</p>
            </details>
          ))}
        </div>
      </div>
    </AnimatedSection>
  );
}
