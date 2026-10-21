import Link from "next/link";
import { siteConfig } from "@/lib/site";
import { BrandLogo } from "./BrandLogo";

export function Footer() {
  return (
    <footer className="border-t border-cyan-500/15 bg-[#070B11] px-6 py-12">
      <div className="mx-auto w-full max-w-6xl">
        <div className="grid gap-8 border-b border-white/10 pb-10 md:grid-cols-4">
          <div className="space-y-3 md:col-span-2">
            <BrandLogo size="md" showParentBadge />
            <p className="max-w-md text-sm text-slate-400">
              AI Support Engineer autónomo: recibe incidentes por WhatsApp, ticketing o API, investiga la causa
              raíz en tu stack y remedia con gobierno humano, snapshots reversibles y audit trail SHA-256.
            </p>
            <p className="font-mono text-xs text-cyan-400/80">
              HIVE · Suite de software para comercio e inteligencia operativa
            </p>
          </div>

          <div>
            <p className="mb-3 font-display text-xs font-semibold uppercase tracking-wider text-slate-200">
              Producto
            </p>
            <ul className="space-y-2 text-sm text-slate-400">
              <li>
                <Link href="/#como-funciona" className="transition-colors hover:text-cyan-300">
                  Cómo funciona
                </Link>
              </li>
              <li>
                <Link href="/pricing" className="transition-colors hover:text-cyan-300">
                  Modelo por resultado
                </Link>
              </li>
              <li>
                <Link href="/security" className="transition-colors hover:text-cyan-300">
                  Seguridad y aislamiento
                </Link>
              </li>
              <li>
                <Link href="/contact" className="transition-colors hover:text-cyan-300">
                  Solicitar demo
                </Link>
              </li>
            </ul>
          </div>

          <div>
            <p className="mb-3 font-display text-xs font-semibold uppercase tracking-wider text-slate-200">
              Ecosistema HIVE
            </p>
            <ul className="space-y-2 text-sm text-slate-400">
              <li className="flex items-center gap-2">
                <span className="h-1.5 w-1.5 rounded-full bg-cyan-400/60" />
                HiveCrew · Ventas IA
              </li>
              <li className="flex items-center gap-2 text-cyan-300">
                <span className="h-1.5 w-1.5 rounded-full bg-cyan-400" />
                HiveOps · Soporte e Incidentes IA
              </li>
              <li className="flex items-center gap-2">
                <span className="h-1.5 w-1.5 rounded-full bg-emerald-400/60" />
                HiveGathers · Logística (Próximamente)
              </li>
            </ul>
          </div>
        </div>

        <div className="mt-6 flex flex-col gap-3 text-xs text-slate-400 md:flex-row md:items-center md:justify-between">
          <p>
            HiveOps es un software propietario del ecosistema <strong className="text-slate-200">HIVE</strong>. © 2026 HIVE. Todos los derechos reservados.
          </p>
          <div className="flex items-center gap-5">
            <a href={`mailto:${siteConfig.supportEmail}`} className="transition-colors hover:text-cyan-300">
              {siteConfig.supportEmail}
            </a>
            <Link href="/security" className="transition-colors hover:text-cyan-300">
              Privacidad y Seguridad
            </Link>
          </div>
        </div>
      </div>
    </footer>
  );
}
