type BrandLogoProps = {
  size?: "sm" | "md" | "lg";
  showParentBadge?: boolean;
};

export function BrandLogo({ size = "md", showParentBadge = true }: BrandLogoProps) {
  const iconSize = size === "sm" ? 32 : size === "lg" ? 50 : 40;
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
        viewBox="9 8 46 46"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
        className="shrink-0"
      >
        {/* HiveOps mark - filled honeycomb + transparent gear (source: /brand/hiveops-mark.svg) */}
        <defs>
          <mask id="hiveops-gear-cut">
            <rect width="64" height="64" fill="#fff" />
            <circle cx="32" cy="32" r="10.05" fill="none" stroke="#000" strokeWidth={4.5} strokeDasharray="3.95 3.95" />
            <circle cx="32" cy="32" r="7.65" fill="#000" />
            <circle cx="32" cy="32" r="3" fill="#fff" />
          </mask>
        </defs>
        <g mask="url(#hiveops-gear-cut)">
          <path d="M32 8 52.78 20v24L32 56 11.22 44V20Z" fill="#25D9E8" />
          <g fill="none" stroke="#0B0F17" strokeWidth={1.8}>
            <path d="M32 19 43.26 25.5v13L32 45 20.74 38.5V25.5Z" />
            <path d="M32 8v11 M52.78 20l-9.52 5.5 M52.78 44l-9.52-5.5 M32 56V45 M11.22 44l9.52-5.5 M11.22 20l9.52 5.5" />
          </g>
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
