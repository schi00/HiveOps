"use client";

import { useMemo } from "react";
import { pricingPlans } from "@/lib/site";
import { trackEvent } from "@/lib/analytics";
import { AnimatedSection } from "./AnimatedSection";

export function PricingPreview() {
  const plans = useMemo(() => pricingPlans, []);

  return (
    <AnimatedSection className="px-6 py-16">
      <div className="mx-auto w-full max-w-6xl">
        <div className="mb-8 flex flex-wrap items-end justify-between gap-4">
          <div className="max-w-2xl">
            <span className="hive-badge-cyan mb-3">Modelo híbrido</span>
            <h2 className="text-3xl font-bold md:text-4xl">
              Suscripción base + <span className="cyan-text">resoluciones verificadas</span>
            </h2>
          </div>
          <span className="rounded-full border border-cyan-500/30 bg-white/5 px-4 py-1.5 font-mono text-xs font-semibold text-cyan-200">
            Base mensual + outcome verificado · sin seats
          </span>
        </div>

        <div className="grid gap-5 md:grid-cols-2 xl:grid-cols-3">
          {plans.map((plan) => (
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
                  <h3 className="text-2xl font-bold text-white">{plan.name}</h3>
                  {plan.highlight ? (
                    <span className="rounded-full bg-cyan-400 px-2.5 py-0.5 font-mono text-[10px] font-bold uppercase text-slate-950">
                      Más elegido
                    </span>
                  ) : null}
                </div>
                <p className="mt-1 text-xs text-slate-400">{plan.badge}</p>
                <p className="mt-4 font-display text-3xl font-extrabold text-white">
                  {plan.priceLabel}
                  <span className="block text-sm font-normal text-slate-300">{plan.priceDetail}</span>
                </p>
                {plan.annual && plan.monthly ? (
                  <p className="mt-1 font-mono text-xs text-cyan-300/90">
                    Anual: USD {plan.annual}/año — ahorrás USD {plan.monthly * 12 - plan.annual}
                  </p>
                ) : null}
                {plan.founderNote ? (
                  <p className="mt-1 font-mono text-xs text-emerald-300">{plan.founderNote}</p>
                ) : null}
                <p className="mt-3 inline-block rounded-md bg-white/5 px-2.5 py-1 font-mono text-xs text-cyan-300">
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
              <a
                href={`/contact?plan=${plan.name.toLowerCase()}`}
                onClick={() => trackEvent("plan_select", { plan: plan.name })}
                className={`mt-7 inline-flex items-center justify-center rounded-full px-5 py-3 text-sm font-semibold transition-all ${
                  plan.highlight
                    ? "bg-[var(--cyan-primary)] text-slate-950 hover:bg-[var(--cyan-soft)]"
                    : "border border-cyan-500/30 bg-white/5 text-white hover:border-cyan-400 hover:bg-white/10"
                }`}
              >
                {plan.cta}
              </a>
            </article>
          ))}
        </div>
        <p className="mt-4 max-w-3xl text-xs leading-relaxed text-slate-400">
          La unidad de facturación variable es la resolución verificada: un incidente que HiveOps cerró y que
          quedó confirmado por verification query o suite de tests. Las derivaciones a tu equipo no se
          computan. Pro y Enterprise incluyen un cupo mensual; el excedente se factura por unidad.
        </p>
      </div>
    </AnimatedSection>
  );
}
