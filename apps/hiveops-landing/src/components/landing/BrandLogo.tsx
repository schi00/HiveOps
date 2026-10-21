type BrandLogoProps = {
  size?: "sm" | "md" | "lg";
  showParentBadge?: boolean;
};

export function BrandLogo({ size = "md", showParentBadge = true }: BrandLogoProps) {
  const iconSize = size === "sm" ? 26 : size === "lg" ? 36 : 30;
  const textClass =
    size === "sm"
      ? "text-lg"
      : size === "lg"
        ? "text-2xl"
        : "text-xl";

  return (
    <span className="inline-flex items-center gap-2.5 select-none">
      <svg
        width={iconSize}
        height={iconSize}
        viewBox="0 0 64 64"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
        className="shrink-0"
      >
        {/* HiveOps mark: cyan hexagon + trapezoid ring + inner chamber + crossed tools (see /brand/hiveops-mark.svg) */}
        <path fill="#00F0FF" d="M32 10 51.05 21v22L32 54 12.95 43V21Z" />
        <g fill="none" stroke="#000000" strokeWidth="2" strokeLinejoin="miter">
          <path d="M32 20l10.39 6v12L32 44 21.61 38V26Z" />
          <path d="M32 10v10M51.05 21 42.39 26M51.05 43l-8.66-5M32 54V44M12.95 43l8.66-5M12.95 21l8.66 5" />
        </g>
        <g
          transform="translate(23 22.6) scale(0.75)"
          fill="none"
          stroke="#000000"
          strokeWidth="2.7"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <path d="M11.42 15.17 17.25 21A2.652 2.652 0 0 0 21 17.25l-5.877-5.877M11.42 15.17l2.496-3.03c.317-.384.74-.626 1.208-.766M11.42 15.17l-4.655 5.653a2.548 2.548 0 1 1-3.586-3.586l6.837-5.63m5.108-.233c.55-.164 1.163-.188 1.743-.14a4.5 4.5 0 0 0 4.486-6.336l-3.276 3.277a3.004 3.004 0 0 1-2.25-2.25l3.276-3.276a4.5 4.5 0 0 0-6.336 4.486c.091 1.076-.071 2.264-.904 2.95l-.102.085m-1.415 8.416-.086-.102" />
        </g>
      </svg>
      <span className={`font-display font-bold tracking-tight text-white ${textClass}`}>
        Hive<span className="cyan-text">Ops</span>
      </span>
      {showParentBadge ? (
        <span className="rounded-full border border-cyan-500/30 bg-cyan-500/10 px-2 py-0.5 font-mono text-[10px] font-medium uppercase tracking-widest text-cyan-300/90">
          by HIVE
        </span>
      ) : null}
    </span>
  );
}
