"use client";

import { motion } from "framer-motion";
import { CTAButton } from "./CTAButton";

const pipelineSteps = [
  {
    number: "01",
    tag: "INTAKE · WHATSAPP BUSINESS",
    title: "Error 500 en módulo de facturación",
    body: "Reportado tras el deploy de anoche. Clientes esperando emisión de comprobantes.",
    borderClass: "border-white/10",
    bgClass: "bg-slate-900/60",
    tagClass: "text-slate-400",
    icon: (
      <svg className="h-4 w-4 text-emerald-400" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M12.04 2C6.58 2 2.13 6.45 2.13 11.91c0 1.75.46 3.45 1.32 4.95L2.05 22l5.25-1.38c1.45.79 3.08 1.21 4.74 1.21 5.46 0 9.91-4.45 9.91-9.91C21.95 6.45 17.5 2 12.04 2zm5.82 14.01c-.24.68-1.41 1.3-1.95 1.38-.5.08-1.13.11-1.83-.11-.42-.13-.97-.31-1.67-.61-2.94-1.27-4.86-4.25-5.01-4.45-.15-.2-1.2-1.59-1.2-3.04 0-1.45.76-2.16 1.03-2.46.27-.3.59-.37.79-.37.2 0 .4 0 .57.01.18.01.43-.07.67.51.24.59.83 2.04.9 2.19.07.15.12.32.02.52-.1.2-.15.32-.3.5-.15.17-.31.39-.44.52-.15.15-.3.31-.13.61.17.3.76 1.25 1.63 2.03 1.12 1 2.06 1.31 2.36 1.46.3.15.47.12.64-.07.17-.2.74-.86.94-1.16.2-.3.4-.25.67-.15.27.1 1.73.82 2.03.97.3.15.49.22.57.35.07.12.07.72-.17 1.4z" />
      </svg>
    ),
  },
  {
    number: "02",
    tag: "ROOT CAUSE ENGINE",
    title: "Pool de conexiones agotado",
    body: "Confianza 87% · Playbook L1 «Reinicio de Pool» ejecutado con dry-run OK. Snapshot fila a fila generado.",
    borderClass: "border-cyan-400/40",
    bgClass: "bg-cyan-400/[0.06]",
    tagClass: "text-cyan-300",
    icon: <span className="text-sm leading-none">⚡</span>,
  },
  {
    number: "03",
    tag: "REMEDIACIÓN VERIFICADA",
    title: "Fix validado en HiveArena",
    body: "Build + suite de tests en verde · PR #142 abierto en GitHub · aguarda aprobación humana.",
    borderClass: "border-emerald-500/40",
    bgClass: "bg-emerald-500/[0.07]",
    tagClass: "text-emerald-300",
    icon: <span className="text-sm leading-none text-emerald-400">✓</span>,
  },
];

const kpis = [
  { value: "-72%", label: "Tiempo de resolución (MTTR)" },
  { value: "100%", label: "Auditado con firma SHA-256" },
  { value: "0", label: "Cambios sin aprobación humana o dry-run" },
];

const integrations = [
  {
    name: "WhatsApp Business",
    icon: (
      <svg className="h-4 w-4 text-emerald-400" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M12.04 2C6.58 2 2.13 6.45 2.13 11.91c0 1.75.46 3.45 1.32 4.95L2.05 22l5.25-1.38c1.45.79 3.08 1.21 4.74 1.21 5.46 0 9.91-4.45 9.91-9.91C21.95 6.45 17.5 2 12.04 2zm5.82 14.01c-.24.68-1.41 1.3-1.95 1.38-.5.08-1.13.11-1.83-.11-.42-.13-.97-.31-1.67-.61-2.94-1.27-4.86-4.25-5.01-4.45-.15-.2-1.2-1.59-1.2-3.04 0-1.45.76-2.16 1.03-2.46.27-.3.59-.37.79-.37.2 0 .4 0 .57.01.18.01.43-.07.67.51.24.59.83 2.04.9 2.19.07.15.12.32.02.52-.1.2-.15.32-.3.5-.15.17-.31.39-.44.52-.15.15-.3.31-.13.61.17.3.76 1.25 1.63 2.03 1.12 1 2.06 1.31 2.36 1.46.3.15.47.12.64-.07.17-.2.74-.86.94-1.16.2-.3.4-.25.67-.15.27.1 1.73.82 2.03.97.3.15.49.22.57.35.07.12.07.72-.17 1.4z" />
      </svg>
    ),
  },
  {
    name: "Ticketing / API",
    icon: (
      <svg className="h-4 w-4 text-sky-400" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
        <path d="M4 6h16M4 12h16M4 18h10" />
      </svg>
    ),
  },
  {
    name: "SQL Server",
    icon: (
      <svg className="h-4 w-4 text-cyan-300" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
        <ellipse cx="12" cy="6" rx="8" ry="3" />
        <path d="M4 6v12c0 1.66 3.58 3 8 3s8-1.34 8-3V6" />
        <path d="M4 12c0 1.66 3.58 3 8 3s8-1.34 8-3" />
      </svg>
    ),
  },
  {
    name: "Git / GitHub",
    icon: (
      <svg className="h-4 w-4 text-slate-300" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
        <circle cx="6" cy="6" r="2.5" />
        <circle cx="6" cy="18" r="2.5" />
        <circle cx="18" cy="8" r="2.5" />
        <path d="M6 8.5v7M18 10.5c0 4-4 4.5-8.5 5" />
      </svg>
    ),
  },
  {
    name: "HiveArena",
    icon: (
      <svg className="h-4 w-4 text-cyan-400" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
        <path d="M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3z" />
        <path d="M12 12l8-4.5M12 12v9M12 12L4 7.5" />
      </svg>
    ),
  },
  {
    name: "Dashboard SignalR",
    icon: (
      <svg className="h-4 w-4 text-emerald-300" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
        <path d="M3 13a9 9 0 0 1 18 0" />
        <path d="M7.5 13a4.5 4.5 0 0 1 9 0" />
        <circle cx="12" cy="15" r="1.5" fill="currentColor" />
      </svg>
    ),
  },
];

export function HeroSection() {
  return (
    <section className="hero-grid grain-overlay relative overflow-hidden px-6 pb-16 pt-14 md:pt-22">
      <div className="mx-auto grid w-full max-w-6xl gap-10 lg:grid-cols-12 lg:items-center">
        {/* Columna izquierda — propuesta de valor */}
        <div className="space-y-6 lg:col-span-6">
          <span className="hive-badge-cyan">
            <span className="relative flex h-2 w-2">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-cyan-400 opacity-75" />
              <span className="relative inline-flex h-2 w-2 rounded-full bg-cyan-400" />
            </span>
            HIVE ECOSYSTEM · AI SUPPORT ENGINEER V1.2
          </span>

          <h1 className="text-4xl font-extrabold leading-[1.06] tracking-tight sm:text-5xl md:text-6xl">
            Soporte técnico autónomo que analiza logs,{" "}
            <span className="cyan-text block sm:inline">
              diagnostica tu DB y abre Pull Requests.
            </span>
          </h1>

          <p className="max-w-xl text-base leading-relaxed text-slate-300 md:text-lg">
            HiveOps investiga causas raíz en tiempo real, ejecuta playbooks deterministas con dry-run
            y resuelve incidentes L1-L3 sin saturar a tu equipo de ingeniería.
          </p>

          <div className="flex flex-wrap items-center gap-3 pt-1">
            <CTAButton
              label="Solicitar demo"
              href="/contact"
              eventName="demo_request_click"
              className="bg-gradient-to-r from-cyan-400 to-blue-600 font-bold text-slate-950 shadow-[0_0_40px_rgba(0,240,255,0.35)] hover:shadow-[0_0_56px_rgba(0,240,255,0.5)]"
            />
            <CTAButton
              label="Cómo se cobra"
              href="/pricing?utm_source=landing&utm_medium=hero"
              eventName="pricing_view"
              className="border border-cyan-500/30 bg-white/5 text-white hover:border-cyan-400/60 hover:bg-white/10"
            />
          </div>

          <div className="flex flex-wrap items-center gap-x-5 gap-y-2 text-xs text-slate-400">
            <span className="flex items-center gap-1.5">
              <span className="text-cyan-400">✓</span> Modelo por resolución verificada
            </span>
            <span className="flex items-center gap-1.5">
              <span className="text-cyan-400">✓</span> Snapshots reversibles con 1-click
            </span>
            <span className="flex items-center gap-1.5">
              <span className="text-cyan-400">✓</span> Audit Trail SHA-256
            </span>
          </div>
        </div>

        {/* Columna derecha — consola de pipeline en vivo */}
        <motion.div
          initial={{ opacity: 0, scale: 0.96, y: 24 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          transition={{ duration: 0.7, ease: "easeOut" }}
          className="surface-card rounded-2xl border-cyan-400/25 p-5 shadow-[0_0_50px_rgba(0,240,255,0.10)] sm:p-6 lg:col-span-6"
        >
          <div className="mb-5 flex flex-wrap items-center justify-between gap-3 border-b border-white/10 pb-4">
            <div className="flex items-center gap-2">
              <span className="h-2.5 w-2.5 rounded-full bg-red-500/70" />
              <span className="h-2.5 w-2.5 rounded-full bg-amber-400/70" />
              <span className="h-2.5 w-2.5 rounded-full bg-emerald-500/70" />
              <span className="ml-2 font-mono text-[11px] text-slate-400">hiveops · pipeline en vivo</span>
            </div>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-500/30 bg-emerald-500/15 px-3 py-1 font-mono text-[11px] font-medium text-emerald-300">
              <span className="relative flex h-1.5 w-1.5">
                <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-400 opacity-75" />
                <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-emerald-400" />
              </span>
              Incidente #1042 — Resuelto &amp; verificado en 4m 12s
            </span>
          </div>

          <div className="space-y-0">
            {pipelineSteps.map((step, idx) => (
              <div key={step.number}>
                <motion.div
                  initial={{ opacity: 0, x: 20 }}
                  animate={{ opacity: 1, x: 0 }}
                  transition={{ duration: 0.5, delay: 0.25 + idx * 0.2, ease: "easeOut" }}
                  className={`rounded-xl border p-4 ${step.borderClass} ${step.bgClass}`}
                >
                  <div className="flex items-start gap-3">
                    <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-white/10 bg-white/5">
                      {step.icon}
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="mb-1 flex items-center justify-between gap-2">
                        <span className={`font-mono text-[10px] font-semibold uppercase tracking-widest ${step.tagClass}`}>
                          {step.tag}
                        </span>
                        <span className="font-mono text-[10px] text-slate-500">{step.number}</span>
                      </div>
                      <p className="font-display text-sm font-semibold text-white">{step.title}</p>
                      <p className="mt-0.5 text-xs leading-relaxed text-slate-400">{step.body}</p>
                    </div>
                  </div>
                </motion.div>
                {idx < pipelineSteps.length - 1 ? (
                  <div className="flex justify-start pl-8">
                    <div className="h-4 w-px bg-gradient-to-b from-cyan-400/50 to-transparent" />
                  </div>
                ) : null}
              </div>
            ))}
          </div>
        </motion.div>
      </div>

      {/* Barra flotante de KPIs */}
      <div className="mx-auto mt-16 w-full max-w-6xl">
        <motion.div
          initial={{ opacity: 0, y: 24 }}
          whileInView={{ opacity: 1, y: 0 }}
          viewport={{ once: true, amount: 0.4 }}
          transition={{ duration: 0.6, ease: "easeOut" }}
          className="grid grid-cols-1 gap-6 rounded-2xl border border-cyan-400/25 bg-[#161B22]/80 px-8 py-7 shadow-[0_0_50px_rgba(0,240,255,0.10)] backdrop-blur-md sm:grid-cols-3"
        >
          {kpis.map((kpi) => (
            <div key={kpi.label} className="text-center sm:text-left">
              <p className="font-display text-4xl font-extrabold tracking-tight text-white md:text-5xl">
                {kpi.value}
              </p>
              <p className="mt-1.5 font-mono text-xs uppercase tracking-widest text-cyan-300/80">
                {kpi.label}
              </p>
            </div>
          ))}
        </motion.div>
      </div>

      {/* Integrations Bar */}
      <div className="mx-auto mt-14 w-full max-w-6xl border-t border-white/10 pt-8">
        <p className="mb-4 text-center font-mono text-xs uppercase tracking-widest text-slate-500">
          Se conecta a tu stack real: intake, base de datos, repo y proving ground
        </p>
        <div className="flex flex-wrap items-center justify-center gap-3 sm:gap-4">
          {integrations.map((item) => (
            <div
              key={item.name}
              className="flex items-center gap-2 rounded-full border border-white/10 bg-white/[0.03] px-4 py-2 text-xs font-medium text-slate-200"
            >
              {item.icon}
              <span>{item.name}</span>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
