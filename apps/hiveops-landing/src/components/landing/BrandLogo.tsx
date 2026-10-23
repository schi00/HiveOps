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
        viewBox="8 8 48 48"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
        className="shrink-0"
      >
        {/* HiveOps mark - filled hexagon + transparent gear cutout (source: /brand/hiveops-mark.svg) */}
        <defs>
          <mask id="hiveops-gear-cut">
            <rect width="64" height="64" fill="#fff" />
            <path d="M 28.67 23.96 L 30.19 20.54 L 33.81 20.54 L 35.33 23.96 L 35.33 23.96 L 38.82 22.62 L 41.38 25.18 L 40.04 28.67 L 40.04 28.67 L 43.46 30.19 L 43.46 33.81 L 40.04 35.33 L 40.04 35.33 L 41.38 38.82 L 38.82 41.38 L 35.33 40.04 L 35.33 40.04 L 33.81 43.46 L 30.19 43.46 L 28.67 40.04 L 28.67 40.04 L 25.18 41.38 L 22.62 38.82 L 23.96 35.33 L 23.96 35.33 L 20.54 33.81 L 20.54 30.19 L 23.96 28.67 L 23.96 28.67 L 22.62 25.18 L 25.18 22.62 L 28.67 23.96 Z M 36.90 32.00 A 4.9 4.9 0 1 0 27.10 32.00 A 4.9 4.9 0 1 0 36.90 32.00 Z" fill="#000" fillRule="evenodd" />
          </mask>
        </defs>
        <path d="M32 8 52.78 20v24L32 56 11.22 44V20Z" fill="#25D9E8" mask="url(#hiveops-gear-cut)" />
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
