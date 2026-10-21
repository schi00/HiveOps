import { AnimatedSection } from "./AnimatedSection";

export function TrustSection() {
  return (
    <AnimatedSection className="px-6 py-14">
      <div id="seguridad" className="mx-auto grid w-full max-w-6xl gap-5 scroll-mt-24 md:grid-cols-2">
        <article className="surface-card surface-card-hover rounded-2xl p-6 sm:p-7">
          <span className="mb-3 inline-block font-mono text-xs uppercase tracking-widest text-cyan-300">
            Remediación gobernada
          </span>
          <h3 className="mb-3 text-2xl font-bold text-white">Autonomía con frenos de verdad</h3>
          <ul className="space-y-2.5 text-sm text-slate-300 sm:text-base">
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Diagnóstico con acceso read-only a la base de datos del tenant</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Dry-run obligatorio y SafetyValidator sobre todo SQL antes de ejecutar</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Aprobación humana por umbral: vos decidís qué puede aplicarse solo</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Snapshots fila a fila con SQL inverso generado para rollback inmediato</span>
            </li>
          </ul>
        </article>
        <article className="surface-card surface-card-hover rounded-2xl p-6 sm:p-7">
          <span className="mb-3 inline-block font-mono text-xs uppercase tracking-widest text-cyan-300">
            Auditabilidad verificable
          </span>
          <h3 className="mb-3 text-2xl font-bold text-white">Todo queda probado y registrado</h3>
          <ul className="space-y-2.5 text-sm text-slate-300 sm:text-base">
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Ledger de auditoría SHA-256 encadenado sobre cada acción del agente</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>HiveArena como proving ground: build y tests antes de cada PR</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Aislamiento por tenant: query filters EF Core y header X-Tenant-Id</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Credenciales cifradas en servidor — nunca expuestas al agente ni al navegador</span>
            </li>
          </ul>
        </article>
      </div>
    </AnimatedSection>
  );
}
