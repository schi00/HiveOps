import { AnimatedSection } from "./AnimatedSection";

const faqItems = [
  {
    q: "¿El agente escribe en mi base de datos productiva?",
    a: "Solo cuando vos lo autorizás. El diagnóstico corre con acceso read-only. Los fixes de datos pasan por SafetyValidator, se ensayan en dry-run, generan un snapshot fila a fila con SQL inverso para rollback, y se aplican únicamente si superan el umbral de aprobación humana que configuraste.",
  },
  {
    q: "¿Qué pasa si el fix es incorrecto?",
    a: "Cada remediación nace con su camino de vuelta: snapshot previo y SQL inverso permiten un rollback inmediato. Si la verification query no confirma el resultado, el caso se revierte y escala a un humano con todo el contexto del RootCauseBundle.",
  },
  {
    q: "¿Cómo se cobra?",
    a: "Modelo híbrido: una suscripción base mensual por tier (Starter USD 200 fijo con hasta 2 workspaces, Pro USD 499–899 con 5–10 workspaces, Enterprise USD 1.499–3.000+ con workspaces ilimitados) más cobro por resolución autónoma verificada por encima del cupo incluido. Enterprise también admite licencia self-hosted anual (USD 10.000–50.000+). Solo se computan los incidentes que HiveOps cerró y que quedaron confirmados por una verification query o una suite de tests. Las escalaciones a tu equipo no facturan.",
  },
  {
    q: "¿Qué necesita para funcionar en mi tenant?",
    a: "Un canal de intake (WhatsApp Business, ticketing o API), acceso read-only a la base de datos para diagnóstico, el repo Git y el pipeline de CI. En el onboarding hacemos un readiness check de tu stack y configuramos playbooks y umbrales de aprobación con vos.",
  },
  {
    q: "¿Es multi-tenant?",
    a: "Sí, desde la base de la arquitectura: cada tenant opera aislado con query filters de EF Core y el header X-Tenant-Id. Los datos, credenciales y hallazgos de un cliente nunca se cruzan con otro.",
  },
  {
    q: "¿Soporta despliegue self-hosted?",
    a: "Sí. El plan Enterprise permite despliegue dedicado o self-hosted asistido en tu infraestructura, con conexión de base de datos por tenant y SLA acordado. También existe un modo SaaS multi-tenant estándar.",
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
