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
        viewBox="0 0 36 36"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
        className="shrink-0"
      >
        <defs>
          <linearGradient id="hive-cyan-top" x1="18" y1="2" x2="18" y2="18" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#A5F3FC" />
            <stop offset="100%" stopColor="#22D3EE" />
          </linearGradient>
          <linearGradient id="hive-cyan-left" x1="4" y1="14" x2="18" y2="33" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#22D3EE" />
            <stop offset="100%" stopColor="#0891B2" />
          </linearGradient>
          <linearGradient id="hive-cyan-right" x1="18" y1="14" x2="32" y2="33" gradientUnits="userSpaceOnUse">
            <stop offset="0%" stopColor="#67E8F9" />
            <stop offset="100%" stopColor="#155E75" />
          </linearGradient>
        </defs>
        {/* Top Hexagon Cell */}
        <polygon
          points="18,2.5 25.5,6.8 25.5,15.4 18,19.7 10.5,15.4 10.5,6.8"
          fill="url(#hive-cyan-top)"
          stroke="#CFFAFE"
          strokeWidth="0.75"
          strokeOpacity="0.6"
        />
        {/* Bottom-Left Hexagon Cell */}
        <polygon
          points="10.5,15.4 18,19.7 18,28.3 10.5,32.6 3,28.3 3,19.7"
          fill="url(#hive-cyan-left)"
          stroke="#A5F3FC"
          strokeWidth="0.75"
          strokeOpacity="0.45"
        />
        {/* Bottom-Right Hexagon Cell */}
        <polygon
          points="25.5,15.4 33,19.7 33,28.3 25.5,32.6 18,28.3 18,19.7"
          fill="url(#hive-cyan-right)"
          stroke="#A5F3FC"
          strokeWidth="0.75"
          strokeOpacity="0.45"
        />
        {/* Central Core Node */}
        <circle cx="18" cy="19.7" r="2.3" fill="#ECFEFF" />
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
