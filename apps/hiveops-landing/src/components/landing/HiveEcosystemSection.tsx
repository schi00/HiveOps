import { AnimatedSection } from "./AnimatedSection";

const ecosystemProducts = [
  {
    name: "HiveCrew",
    status: "Disponible hoy",
    active: false,
    accentClass: "border-amber-500/25 bg-gradient-to-b from-amber-500/10 to-transparent",
    dotClass: "bg-amber-400",
    badgeClass: "border-amber-400/30 bg-amber-400/10 text-amber-200",
    category: "Ventas y atención con IA",
    description:
      "Ventas y atención autónoma por WhatsApp e Instagram conectada a tu catálogo real y cobros con MercadoPago.",
  },
  {
    name: "HiveOps",
    status: "Este producto",
    active: true,
    accentClass: "border-cyan-500/50 bg-gradient-to-b from-cyan-500/15 to-transparent",
    dotClass: "bg-cyan-400",
    badgeClass: "border-cyan-400/40 bg-cyan-400/15 text-cyan-200",
    category: "Soporte e incidentes con IA",
    description:
      "El agente de soporte de la suite: atiende reportes, encuentra la causa y propone soluciones que solo se aplican con tu aprobación.",
  },
  {
    name: "HiveGathers",
    status: "Próximamente",
    active: false,
    accentClass: "border-emerald-500/25 bg-gradient-to-b from-emerald-500/10 to-transparent",
    dotClass: "bg-emerald-400",
    badgeClass: "border-emerald-400/30 bg-emerald-400/10 text-emerald-200",
    category: "Orquestación logística",
    description:
      "Seguimiento de envíos en tiempo real: proveedores, costos y el estado de cada entrega.",
  },
];

export function HiveEcosystemSection() {
  return (
    <AnimatedSection className="px-6 py-16">
      <div id="ecosistema" className="mx-auto w-full max-w-6xl scroll-mt-24">
        <div className="mb-10 flex flex-col justify-between gap-4 md:flex-row md:items-end">
          <div className="max-w-2xl">
            <span className="hive-badge-cyan mb-3">HIVE · SOFTWARE ECOSYSTEM</span>
            <h2 className="text-3xl font-bold md:text-4xl">
              Un ecosistema pensado para <span className="cyan-text">escalar cada área de tu operación</span>
            </h2>
          </div>
          <p className="max-w-md text-sm text-slate-300">
            Empezás hoy con <strong className="text-white">HiveOps</strong> y tu cuenta HIVE ya queda lista para sumar
            nuevos módulos a medida que crece tu negocio.
          </p>
        </div>

        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {ecosystemProducts.map((product) => (
            <article
              key={product.name}
              className={`rounded-2xl border p-6 backdrop-blur-md transition-transform hover:-translate-y-1 ${product.accentClass}`}
            >
              <div className="mb-4 flex items-center justify-between gap-2">
                <span className="inline-flex items-center gap-2 font-display text-lg font-bold text-white">
                  <span className={`h-2.5 w-2.5 rounded-full ${product.dotClass}`} />
                  {product.name}
                </span>
                <span className={`rounded-full border px-2.5 py-0.5 font-mono text-[10px] font-semibold uppercase ${product.badgeClass}`}>
                  {product.status}
                </span>
              </div>
              <p className="mb-2 font-mono text-xs text-slate-400">{product.category}</p>
              <p className="text-sm leading-relaxed text-slate-200">{product.description}</p>
            </article>
          ))}
        </div>
      </div>
    </AnimatedSection>
  );
}
