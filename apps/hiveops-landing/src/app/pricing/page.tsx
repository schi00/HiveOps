import type { Metadata } from "next";
import { pricingPlans } from "@/lib/site";
import { CTAButton } from "@/components/landing/CTAButton";

export const metadata: Metadata = {
  title: "Precios y Planes",
  description:
    "Modelo híbrido de HiveOps by HIVE: suscripción base + cobro por resolución autónoma verificada. Starter, Pro y Enterprise con opción self-hosted.",
};

export default function PricingPage() {
  const comparisonRows = [
    { label: "Canales de intake", starter: "WhatsApp + webhooks", pro: "WhatsApp + webhooks + ticketing + API", enterprise: "Multi-canal + ITSM custom" },
    { label: "Tracks de resolución", starter: "Playbooks L1 + Escalate", pro: "L1/L2: Playbook + DatabaseFix + Escalate", enterprise: "L1/L2/L3 completo: + CodeFix con PRs" },
    { label: "Diagnóstico", starter: "Reglas deterministas + triage IA", pro: "SQL read-only + logs + git blame + KB", enterprise: "Root cause multi-fuente + fuentes custom" },
    { label: "API Drift detection", starter: "—", pro: "Alertas en tiempo real", enterprise: "Alertas + remediación automática" },
    { label: "Remediación gobernada", starter: "Dry-run + aprobación total", pro: "Umbrales por acción + snapshots fila a fila", enterprise: "Políticas a medida + rollback 1-click" },
    { label: "Validación de codefixes", starter: "—", pro: "Verification query", enterprise: "HiveArena: build + tests → PR automático" },
    { label: "Audit trail", starter: "Registro de acciones", pro: "Ledger SHA-256 encadenado", enterprise: "Ledger + export a tu SIEM" },
    { label: "Aislamiento", starter: "Tenant en SaaS multi-tenant", pro: "Multi-tenant con aislamiento estricto", enterprise: "Dedicado o self-hosted (FailClosed)" },
    { label: "Workspaces aislados", starter: "Hasta 2", pro: "Hasta 5–10", enterprise: "Ilimitados" },
    { label: "Resoluciones incluidas", starter: "Incluidas en el plan", pro: "Cupo mensual + extras por unidad", enterprise: "Paquetes a medida" },
    { label: "Facturación", starter: "USD 200/mes fijo", pro: "USD 499–899/mes según volumen", enterprise: "USD 1.499–3.000+/mes o licencia self-hosted USD 10.000–50.000+/año" },
    { label: "Puesta en marcha", starter: "Onboarding guiado", pro: "Readiness check + playbooks", enterprise: "Despliegue asistido + SLA" },
  ];

  return (
    <div className="px-6 py-16">
      <div className="mx-auto w-full max-w-6xl">
        <span className="hive-badge-cyan mb-3">HIVE · MODELO HÍBRIDO</span>
        <h1 className="mb-3 text-4xl font-extrabold md:text-5xl">
          Base predecible + <span className="cyan-text">cobro por resolución verificada</span>
        </h1>
        <p className="mb-8 max-w-3xl text-slate-300">
          Una suscripción mensual cubre la plataforma, los canales y el gobierno. Encima, solo pagás por los
          incidentes que HiveOps resolvió de forma autónoma y que quedaron verificados por query o suite de
          tests — las derivaciones a tu equipo no se computan.
        </p>

        <div className="surface-card mb-10 rounded-2xl p-6 text-sm text-slate-200">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div>
              <p className="font-display text-base font-bold text-white">
                Piloto acotado con onboarding asistido
              </p>
              <p className="mt-1 text-slate-300">
                Hacemos un readiness check de tu stack (base de datos, repo Git, pipeline, canal de intake),
                configuramos playbooks y umbrales de aprobación, y medimos outcomes reales con el
                OutcomeMeteringService antes de escalar.
              </p>
            </div>
            <span className="rounded-full border border-emerald-500/30 bg-emerald-500/15 px-3.5 py-1 font-mono text-xs font-semibold text-emerald-300">
              Solo resultados verificados
            </span>
          </div>
        </div>

        <div className="grid gap-5 md:grid-cols-2 xl:grid-cols-3">
          {pricingPlans.map((plan) => (
            <article
              key={plan.name}
              className={`flex flex-col justify-between rounded-2xl p-6 sm:p-7 ${
                plan.highlight
                  ? "cyan-glow border border-[var(--cyan-primary)] bg-gradient-to-b from-cyan-500/15 to-[#161B22]"
                  : "surface-card surface-card-hover"
              }`}
            >
              <div>
                <div className="flex items-center justify-between gap-2">
                  <h2 className="text-2xl font-bold text-white">{plan.name}</h2>
                  {plan.highlight ? (
                    <span className="rounded-full bg-cyan-400 px-2.5 py-0.5 font-mono text-[10px] font-bold uppercase text-slate-950">
                      Más elegido
                    </span>
                  ) : null}
                </div>
                <p className="mt-1 text-xs text-slate-400">{plan.badge}</p>
                <p className="mt-4 font-display text-3xl font-extrabold text-white">
                  {plan.priceLabel}{" "}
                  <span className="text-sm font-normal text-slate-300">{plan.priceDetail}</span>
                </p>
                <p className="mt-3 inline-block rounded-md bg-white/5 px-2.5 py-1 font-mono text-xs text-slate-200">
                  {plan.metering}
                </p>
                <ul className="mt-5 space-y-2.5 text-sm text-slate-200">
                  {plan.features.map((feature) => (
                    <li key={feature} className="flex items-start gap-2">
                      <span className="mt-0.5 text-cyan-400">✓</span>
                      <span>{feature}</span>
                    </li>
                  ))}
                </ul>
              </div>
              <div className="mt-7">
                <CTAButton
                  label={plan.cta}
                  href={`/contact?plan=${plan.name.toLowerCase()}`}
                  eventName="plan_select"
                  className="cyan-glow w-full bg-[var(--cyan-primary)] text-slate-950 hover:bg-[var(--cyan-soft)]"
                />
              </div>
            </article>
          ))}
        </div>

        <p className="mt-4 max-w-3xl text-xs leading-relaxed text-slate-400">
          La base mensual cubre plataforma, canales y gobierno por workspace; las resoluciones autónomas
          verificadas por encima del cupo se facturan por unidad. Enterprise admite licencia self-hosted
          anual (USD 10.000–50.000+) en tu propia infraestructura. Volúmenes altos pueden traer sus propias
          API keys de LLM.
        </p>

        <section className="mt-14">
          <h2 className="mb-5 text-2xl font-bold md:text-3xl">Comparativo detallado de capacidades</h2>
          <div className="overflow-x-auto rounded-2xl border border-cyan-500/20 bg-[#161B22]/80">
            <table className="min-w-full text-sm">
              <thead>
                <tr className="border-b border-white/10 bg-white/5 text-left">
                  <th className="px-4 py-3.5 font-display font-semibold text-white">Capacidad</th>
                  <th className="px-4 py-3.5 font-display font-semibold text-white">Starter</th>
                  <th className="px-4 py-3.5 font-display font-semibold text-cyan-300">Pro</th>
                  <th className="px-4 py-3.5 font-display font-semibold text-white">Enterprise</th>
                </tr>
              </thead>
              <tbody>
                {comparisonRows.map((row) => (
                  <tr key={row.label} className="border-b border-white/5">
                    <td className="px-4 py-3.5 font-medium text-slate-200">{row.label}</td>
                    <td className="px-4 py-3.5 text-slate-300">{row.starter}</td>
                    <td className="px-4 py-3.5 font-medium text-cyan-200">{row.pro}</td>
                    <td className="px-4 py-3.5 text-slate-300">{row.enterprise}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </div>
  );
}
