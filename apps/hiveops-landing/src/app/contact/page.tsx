import type { Metadata } from "next";
import { siteConfig } from "@/lib/site";

export const metadata: Metadata = {
  title: "Contacto Comercial",
  description: "Solicitá una demo o un piloto de HiveOps by HIVE con onboarding asistido sobre tu propio stack.",
};

export default function ContactPage() {
  return (
    <div className="px-6 py-16">
      <div className="mx-auto grid w-full max-w-6xl gap-6 md:grid-cols-2">
        <section className="surface-card rounded-2xl p-8">
          <span className="hive-badge-cyan mb-4">HIVE · EQUIPO COMERCIAL</span>
          <h1 className="mb-3 text-4xl font-extrabold">
            Hablemos de <span className="cyan-text">tu soporte</span>
          </h1>
          <p className="mb-6 text-slate-300">
            Si tu equipo de soporte está saturado de L1 repetitivos, si los incidentes esperan horas por triage o
            si querés ver una resolución verificada sobre tu propio stack, nuestro equipo te acompaña paso a paso.
          </p>
          <div className="space-y-3 text-sm text-slate-200 sm:text-base">
            <p className="flex items-center gap-2">
              <span className="text-cyan-400">✓</span> Demo guiada con un escenario real de tu operación
            </p>
            <p className="flex items-center gap-2">
              <span className="text-cyan-400">✓</span> Readiness check de tu stack: base de datos, repo Git, pipeline e intake
            </p>
            <p className="flex items-center gap-2">
              <span className="text-cyan-400">✓</span> Piloto acotado con onboarding asistido y métricas de MTTR
            </p>
          </div>
          <p className="mt-8 font-mono text-xs text-slate-400">
            Contacto directo:{" "}
            <a className="text-[var(--cyan-soft)] underline-offset-4 hover:underline" href={`mailto:${siteConfig.supportEmail}`}>
              {siteConfig.supportEmail}
            </a>
          </p>
        </section>

        <section className="surface-card rounded-2xl p-8">
          <h2 className="mb-4 text-2xl font-bold">Solicitar demo o piloto</h2>
          <form className="space-y-4">
            <input
              className="w-full rounded-xl border border-white/15 bg-black/40 px-4 py-3 text-sm outline-none transition-colors focus:border-[var(--cyan-primary)]"
              type="text"
              placeholder="Nombre y apellido"
              required
            />
            <input
              className="w-full rounded-xl border border-white/15 bg-black/40 px-4 py-3 text-sm outline-none transition-colors focus:border-[var(--cyan-primary)]"
              type="email"
              placeholder="Email de trabajo"
              required
            />
            <input
              className="w-full rounded-xl border border-white/15 bg-black/40 px-4 py-3 text-sm outline-none transition-colors focus:border-[var(--cyan-primary)]"
              type="text"
              placeholder="Empresa / Producto de software"
            />
            <textarea
              className="min-h-32 w-full rounded-xl border border-white/15 bg-black/40 px-4 py-3 text-sm outline-none transition-colors focus:border-[var(--cyan-primary)]"
              placeholder="Contanos tu stack (BD, repo, CI, ticketing) y cuántos incidentes manejan por semana"
              required
            />
            <button
              type="submit"
              className="cyan-glow w-full rounded-full bg-[var(--cyan-primary)] px-6 py-3 text-sm font-semibold text-slate-950 transition-colors hover:bg-[var(--cyan-soft)]"
            >
              Coordinar demo con un especialista
            </button>
          </form>
        </section>
      </div>
    </div>
  );
}
