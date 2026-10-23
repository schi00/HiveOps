import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Seguridad y Gobernanza",
  description: "Separación total entre cuentas, diagnóstico de solo lectura, aprobaciones humanas, cambios reversibles y registro sellado en HiveOps by HIVE.",
};

const pillars = [
  {
    badge: "CUENTAS AISLADAS",
    title: "Datos y acciones 100% separados por cuenta",
    text: "Cada cliente opera en su propio entorno aislado: cada consulta y cada acción quedan confinadas a su cuenta. Las credenciales se guardan cifradas en el servidor y nunca se exponen al agente ni al navegador.",
  },
  {
    badge: "SOLO LECTURA",
    title: "El agente mira antes de tocar",
    text: "La investigación corre con acceso de solo lectura a tu base de datos. Toda corrección se valida y se simula primero — si algo no cierra, nunca llega a ejecutarse de verdad.",
  },
  {
    badge: "TU APROBACIÓN",
    title: "Vos aprobás, y siempre se puede volver atrás",
    text: "Configurás qué acciones requieren aprobación y quién puede darla. Cada cambio de datos genera su copia de seguridad, de modo que revertir es una operación de segundos. Ninguna automatización sensible corre sin tu control.",
  },
  {
    badge: "REGISTRO SELLADO",
    title: "Cada acción queda registrada y firmada",
    text: "Análisis, aprobación, ejecución y verificación quedan firmados en un registro inalterable. Podés auditar qué hizo el agente, con qué evidencia y quién lo autorizó — incluso meses después.",
  },
  {
    badge: "ENTORNO DE PRUEBAS",
    title: "Las correcciones se prueban antes de producción",
    text: "Los arreglos de código se compilan y testean en un entorno aislado antes de proponerse. Solo lo que sale verde llega a tu repositorio — y la publicación sigue esperando tu OK.",
  },
  {
    badge: "DÓNDE CORRE",
    title: "Nube, dedicado o tu propia infraestructura",
    text: "Elegí dónde corre el agente: nube compartida con separación estricta, instancia dedicada, o instalación asistida dentro de tu propia infraestructura.",
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
          HiveOps puede diagnosticar, proponer y resolver — pero cada decisión sensible pasa por validación,
          aprobación humana y registro. Así se automatiza el soporte sin resignar control.
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
