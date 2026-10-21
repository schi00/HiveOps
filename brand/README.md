# HIVE brand marks (editable source)

SVG sources for the HIVE product family identity — linear "honeycomb cell" system.

| File | Mark | Accent |
|---|---|---|
| `hive-mark.svg` | Parent: cell ring + linear cube nucleus | Amber `#F5B323` |
| `hivecrew-mark.svg` | HiveCrew: cell ring + headset nucleus | Amber `#F5B323` |
| `hiveops-mark.svg` | HiveOps: cell ring + crossed wrench/screwdriver | Cyan `#00F0FF` |

## System

All marks share the same shell:

- Outer hexagon filled with the brand/product color (`M32 10 51.05 21v22L32 54 12.95 43V21Z`, R=22).
- Inner chamber hexagon (`R=12`, same center `32,32`).
- 6 radial strokes vertex-to-vertex → the ring divides into 6 identical trapezoids
  (rotational symmetry, 60°).

The nucleus inside the chamber is what distinguishes each brand:

- **HIVE**: the cube Y (`M32 32V20` + two diagonals) — the parent technology mark.
- **HiveCrew**: headset (headband arc + earpads + mic boom).
- **HiveOps**: wrench + screwdriver crossed (Heroicons `wrench-screwdriver`, MIT,
  `translate(23 22.6) scale(0.75)`, stroke-width 2.7 in glyph space).

## Editing notes

- Recolor: `fill` of the outer hexagon path (single color per mark).
- Linework color: `stroke` on the shell `<g>` and glyph elements (`#000000` on brand fills).
- Line weight: `stroke-width` (shell = 2, tools glyph = 2.7 pre-scale ≈ 2 effective).
- Monochrome: fill `#FFFFFF` (dark bg) or `#171B22` (light bg); keep strokes contrasting.
- Keep everything inside the inner chamber (~24×24 px, centered on `32,32`) when
  swapping product glyphs.
