import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Seguridad y Gobernanza",
  description: "Aislamiento por tenant, diagnóstico read-only, aprobaciones humanas por umbral, snapshots reversibles y audit trail SHA-256 en HiveOps by HIVE.",
};

const pillars = [
  {
    badge: "AISLAMIENTO MULTI-TENANT",
    title: "Datos y acciones 100% separados por tenant",
    text: "Cada cliente opera en su propio entorno aislado: query filters de EF Core sobre cada consulta y el header X-Tenant-Id en cada request. Las credenciales de conexión se guardan cifradas en el servidor y nunca se exponen al agente ni al navegador.",
  },
  {
    badge: "DIAGNÓSTICO READ-ONLY",
    title: "El agente mira antes de tocar",
    text: "La investigación de causa raíz corre con acceso de solo lectura a la base de datos del tenant. Todo SQL generado pasa por el SafetyValidator — que rechaza escrituras fuera de alcance — y por un dry-run antes de cualquier ejecución real.",
  },
  {
    badge: "REMEDIACIÓN GOBERNADA",
    title: "Aprobación humana por umbral, rollback siempre listo",
    text: "Configurás qué acciones requieren aprobación y quién puede darla. Cada fix de datos genera un snapshot fila a fila con su SQL inverso, de modo que revertir es una operación de segundos. Ninguna automatización destructiva corre sin gobierno.",
  },
  {
    badge: "AUDIT TRAIL CRIPTOGRÁFICO",
    title: "Ledger SHA-256 encadenado sobre cada acción",
    text: "Triage, diagnóstico, aprobación, ejecución y verificación quedan firmados en una cadena de hashes inmutable. Podés auditar qué hizo el agente, con qué evidencia y quién lo autorizó — incluso meses después.",
  },
  {
    badge: "PROVING GROUND",
    title: "HiveArena valida antes de producción",
    text: "Los fixes de código se compilan y se testean en HiveArena — un sandbox con build y suite de regresión — antes de convertirse en PR. Solo lo que sale verde llega a tu repositorio, y el deploy sigue esperando aprobación humana.",
  },
  {
    badge: "MODOS DE DESPLIEGUE",
    title: "SaaS multi-tenant, dedicado o self-hosted",
    text: "Elegí dónde corre el agente: SaaS compartido con aislamiento estricto, instancia dedicada, o self-hosted asistido dentro de tu infraestructura con conexión de base de datos por tenant.",
  },
];

export default function SecurityPage() {
  return (
    <div className="px-6 py-16">
      <div className="mx-auto w-full max-w-5xl">
        <span className="hive-badge-cyan mb-3">HIVE · ARQUITECTURA CONFIABLE</span>
        <h1 className="mb-3 text-4xl font-extrabold md:text-5xl">
          Autonomía de soporte con <span className="cyan-text">gobierno en cada acción</span>
        </h1>
        <p className="mb-10 max-w-3xl text-slate-300">
          HiveOps puede diagnosticar, proponer y remediar — pero cada decisión sensible pasa por validación,
          aprobación humana y registro criptográfico. Así se automatiza soporte L1/L2/L3 sin resignar control.
        </p>

        <div className="grid gap-5 md:grid-cols-2">
          {pillars.map((pillar) => (
            <article key={pillar.title} className="surface-card surface-card-hover rounded-2xl p-6 sm:p-7">
              <span className="mb-3 inline-block rounded-md border border-cyan-500/30 bg-cyan-500/10 px-2.5 py-1 font-mono text-[11px] font-semibold uppercase tracking-wider text-cyan-300">
                {pillar.badge}
              </span>
              <h2 className="mb-2 text-xl font-bold text-white">{pillar.title}</h2>
              <p className="text-sm leading-relaxed text-slate-300 sm:text-base">{pillar.text}</p>
            </article>
          ))}
        </div>
      </div>
    </div>
  );
}
