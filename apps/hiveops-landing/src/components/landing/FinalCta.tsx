import { CTAButton } from "./CTAButton";
import { AnimatedSection } from "./AnimatedSection";

export function FinalCta() {
  return (
    <AnimatedSection className="px-6 py-18">
      <div className="mx-auto w-full max-w-5xl rounded-3xl border border-cyan-500/40 bg-gradient-to-br from-cyan-500/15 via-[#161B22] to-[#050608] p-8 text-center sm:p-12">
        <span className="hive-badge-cyan mb-4">HIVE ECOSYSTEM · EMPEZÁ HOY</span>
        <h2 className="mb-4 text-3xl font-extrabold md:text-5xl">
          Que ningún incidente se quede <span className="cyan-text">esperando al lunes</span>
        </h2>
        <p className="mx-auto mb-8 max-w-2xl text-slate-200">
          Agendá una demo sobre tu propio entorno: conectamos un canal, investigamos en solo lectura y
          resolvemos un caso piloto para que veas el resultado de punta a punta.
        </p>
        <div className="flex flex-wrap items-center justify-center gap-3">
          <CTAButton
            label="Solicitar demo"
            href="/contact"
            eventName="cta_click_primary"
            className="cyan-glow bg-[var(--cyan-primary)] text-slate-950 hover:bg-[var(--cyan-soft)]"
          />
          <CTAButton
            label="Ver cómo se cobra"
            href="/pricing?utm_source=landing&utm_medium=final_cta"
            eventName="cta_click_secondary"
            className="border border-cyan-500/30 bg-white/10 text-white hover:border-cyan-400 hover:bg-white/15"
          />
        </div>
      </div>
    </AnimatedSection>
  );
}
