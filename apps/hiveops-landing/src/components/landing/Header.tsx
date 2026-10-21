import Link from "next/link";
import { siteConfig } from "@/lib/site";
import { BrandLogo } from "./BrandLogo";
import { CTAButton } from "./CTAButton";

export function Header() {
  return (
    <header className="sticky top-0 z-40 border-b border-cyan-500/15 bg-[#050608]/80 backdrop-blur-xl">
      <div className="mx-auto flex w-full max-w-6xl items-center justify-between px-6 py-4">
        <Link href="/" aria-label="HiveOps by HIVE - Inicio">
          <BrandLogo size="md" showParentBadge />
        </Link>
        <nav className="hidden items-center gap-7 text-sm font-medium text-slate-300 md:flex">
          <Link href="/#como-funciona" className="transition-colors hover:text-cyan-300">
            Cómo funciona
          </Link>
          <Link href="/security" className="transition-colors hover:text-cyan-300">
            Seguridad
          </Link>
          <Link href="/pricing" className="transition-colors hover:text-cyan-300">
            Precios
          </Link>
          <Link href="/contact" className="transition-colors hover:text-cyan-300">
            Contacto
          </Link>
        </nav>
        <div className="flex items-center gap-3">
          <a
            href={siteConfig.appUrl}
            className="hidden text-sm font-medium text-slate-300 transition-colors hover:text-white sm:inline-block"
          >
            Ingresar
          </a>
          <CTAButton
            label="Solicitar demo"
            href="/contact"
            eventName="cta_click_primary"
            className="cyan-glow bg-[var(--cyan-primary)] text-slate-950 hover:bg-[var(--cyan-soft)]"
          />
        </div>
      </div>
    </header>
  );
}
