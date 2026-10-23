import { AnimatedSection } from "./AnimatedSection";

export function TrustSection() {
  return (
    <AnimatedSection className="px-6 py-14">
      <div id="seguridad" className="mx-auto grid w-full max-w-6xl gap-5 scroll-mt-24 md:grid-cols-2">
        <article className="surface-card surface-card-hover rounded-2xl p-6 sm:p-7">
          <span className="mb-3 inline-block font-mono text-xs uppercase tracking-widest text-cyan-300">
            Bajo tu control
          </span>
          <h3 className="mb-3 text-2xl font-bold text-white">Autonomía con frenos de verdad</h3>
          <ul className="space-y-2.5 text-sm text-slate-300 sm:text-base">
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Investiga tu base de datos con acceso de solo lectura</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Toda corrección se valida y se simula antes de ejecutarse</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Vos decidís qué puede aplicarse solo y qué necesita tu OK</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Copia de seguridad de cada dato para revertir en segundos</span>
            </li>
          </ul>
        </article>
        <article className="surface-card surface-card-hover rounded-2xl p-6 sm:p-7">
          <span className="mb-3 inline-block font-mono text-xs uppercase tracking-widest text-cyan-300">
            Registro verificable
          </span>
          <h3 className="mb-3 text-2xl font-bold text-white">Todo queda probado y registrado</h3>
          <ul className="space-y-2.5 text-sm text-slate-300 sm:text-base">
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Registro sellado e inalterable de cada acción del agente</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Cada corrección de código se compila y prueba antes de proponerse</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Cada cuenta totalmente aislada de las demás</span>
            </li>
            <li className="flex items-start gap-2">
              <span className="text-cyan-400">✓</span>
              <span>Credenciales cifradas en el servidor — nunca expuestas al agente ni al navegador</span>
            </li>
          </ul>
        </article>
      </div>
    </AnimatedSection>
  );
}
