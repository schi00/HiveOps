import type { Metadata } from "next";
import { pricingPlans } from "@/lib/site";
import { CTAButton } from "@/components/landing/CTAButton";

export const metadata: Metadata = {
  title: "Precios y Planes",
  description:
    "Modelo híbrido de HiveOps by HIVE: suscripción base + cobro por resolución autónoma verificada. Starter, Pro y Enterprise con opción en tu propia infraestructura.",
};

export default function PricingPage() {
  const comparisonRows = [
    { label: "Por dónde entran los problemas", starter: "WhatsApp + formulario web", pro: "WhatsApp, web y tickets de tus sistemas", enterprise: "Todos los canales que uses, a medida" },
    { label: "Qué puede resolver solo", starter: "Consultas frecuentes; el resto lo deriva", pro: "Consultas + correcciones de datos en tu base", enterprise: "Lo anterior + correcciones de código listas para revisar" },
    { label: "Cómo encuentra la causa", starter: "Análisis automático del problema", pro: "Revisa datos, registros e historial de cambios", enterprise: "Investiga en todas tus fuentes, incluidas las propias" },
    { label: "Aviso si algo cambia en tus sistemas", starter: "—", pro: "Alerta en tiempo real", enterprise: "Alerta + corrección automática" },
    { label: "Cambios bajo tu control", starter: "Simula antes de aplicar; todo con tu OK", pro: "Límites por acción + copia de seguridad de cada dato", enterprise: "Tus propias reglas + volver atrás en 1 clic" },
    { label: "Correcciones verificadas", starter: "—", pro: "Comprueba el resultado antes de proponerlo", enterprise: "Compila y prueba el código antes de entregarlo" },
    { label: "Registro de todo lo que hace", starter: "Historial de acciones", pro: "Historial sellado e inalterable", enterprise: "Historial + exportación a tu sistema de seguridad" },
    { label: "Dónde corren tus datos", starter: "Nube con datos separados", pro: "Nube con separación estricta", enterprise: "Dedicado o en tu propia infraestructura" },
    { label: "Cuentas separadas", starter: "Hasta 2", pro: "Hasta 5–10", enterprise: "Ilimitadas" },
    { label: "Resoluciones incluidas", starter: "Incluidas en el plan", pro: "Cupo mensual + adicionales", enterprise: "Paquetes a medida" },
    { label: "Precio", starter: "USD 200/mes · anual USD 1.800", pro: "desde USD 499/mes · anual desde USD 4.190", enterprise: "desde USD 1.499/mes o licencia anual en tu infraestructura" },
    { label: "Cómo arrancás", starter: "Asistente guiado", pro: "Revisión de tu entorno + guías de acción", enterprise: "Instalación acompañada + SLA" },
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
          incidentes que HiveOps resolvió de forma autónoma y que quedaron verificados automáticamente — las
          derivaciones a tu equipo no se computan.
        </p>

        <div className="mb-6 rounded-2xl border border-cyan-500/40 bg-gradient-to-r from-cyan-500/15 to-cyan-600/5 p-6 text-sm">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div>
              <p className="font-display text-base font-bold text-white">
                Piloto fundador — solo para los primeros 50 clientes
              </p>
              <p className="mt-1 text-slate-300">
                Entrá a Starter pagando USD 99/mes los primeros 3 meses (después USD 200/mes), o contratá el plan
                anual y congelá el precio de lanzamiento mientras tu suscripción siga activa.
              </p>
            </div>
            <span className="rounded-full bg-cyan-400 px-3.5 py-1 font-mono text-xs font-bold uppercase text-slate-950">
              Hasta 50% off entrada
            </span>
          </div>
        </div>

        <div className="surface-card mb-10 rounded-2xl p-6 text-sm text-slate-200">
          <div className="flex flex-wrap items-center justify-between gap-4">
            <div>
              <p className="font-display text-base font-bold text-white">
                Piloto acotado con onboarding asistido
              </p>
              <p className="mt-1 text-slate-300">
                Revisamos tu entorno (base de datos, repositorio y canal de entrada), configuramos guías
                de acción y límites de aprobación, y medimos resultados reales antes de escalar.
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
                {plan.annual && plan.monthly ? (
                  <p className="font-mono text-xs text-cyan-300/90">
                    Anual: USD {plan.annual}/año — ahorrás USD {plan.monthly * 12 - plan.annual}
                  </p>
                ) : null}
                {plan.founderNote ? (
                  <p className="mt-1 font-mono text-xs text-emerald-300">{plan.founderNote}</p>
                ) : null}
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
          La base mensual cubre plataforma, canales y gobierno por cuenta; las resoluciones autónomas
          verificadas por encima del cupo se facturan por unidad. Enterprise admite licencia anual
          (USD 10.000–50.000+) en tu propia infraestructura. Volúmenes altos pueden usar sus propias
          credenciales de IA.
        </p>

        <section className="mt-14">
          <h2 className="mb-5 text-2xl font-bold md:text-3xl">Qué incluye cada plan, en detalle</h2>
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
