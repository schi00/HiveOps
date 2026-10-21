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
        {/* HIVE mark: three interlocking bent bands forming a hexagonal ring */}
        <path
          d="M15.9 49.1 6 32 15.9 14.9"
          stroke="#00F0FF"
          strokeWidth="8"
          strokeLinejoin="miter"
          strokeMiterlimit="10"
        />
        <path
          d="M25.2 9.5 45 9.5 54.9 26.6"
          stroke="#00F0FF"
          strokeWidth="8"
          strokeLinejoin="miter"
          strokeMiterlimit="10"
        />
        <path
          d="M54.9 37.4 45 54.5 25.2 54.5"
          stroke="#00F0FF"
          strokeWidth="8"
          strokeLinejoin="miter"
          strokeMiterlimit="10"
        />
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
