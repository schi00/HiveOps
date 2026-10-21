"use client";

import { trackEvent } from "@/lib/analytics";

type CTAButtonProps = {
  label: string;
  href: string;
  eventName: string;
  className?: string;
};

export function CTAButton({ label, href, eventName, className }: CTAButtonProps) {
  return (
    <a
      href={href}
      onClick={() => trackEvent(eventName)}
      className={`inline-flex items-center justify-center rounded-full px-6 py-3 text-sm font-semibold transition-transform hover:-translate-y-0.5 ${className ?? ""}`}
    >
      {label}
    </a>
  );
}
