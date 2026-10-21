# HIVE brand marks (editable source)

SVG sources for the HIVE product family identity — honeycomb-cube system:
filled hexagon + inner hexagon/cube frame + 6 radial trapezoids + product glyph.

| File | Mark | Background |
|---|---|---|
| `hive-mark.svg` | Parent: honeycomb cube, amber fill | Light |
| `hive-mark_dark.svg` | Parent: black cube, amber internals | Dark |
| `hivecrew-mark.svg` | HiveCrew: cube + glyph, amber fill | Light |
| `hivecrew-mark-dark.svg` | HiveCrew: black cube, amber internals | Dark |
| `hiveops-mark.svg` | HiveOps: cube + gears, cyan fill `#00CCFF` | Light |
| `hiveops-mark-dark.svg` | HiveOps: black cube, cyan internals | Dark |

## Variants

- **Common (`*-mark.svg`)**: colored hexagon fill + black internal geometry — for
  light/white backgrounds.
- **Dark (`*-mark-dark.svg`)**: black hexagon fill + internals in the product
  accent — for dark backgrounds (landing, dashboard, favicon).

## Editing notes

- All marks share the same outer hexagon (`M32 10 51.05 21v22L32 54 12.95 43V21Z`,
  R=22, center 32,32) and inner hexagon (R=12), so glyphs can be swapped inside a
  consistent container.
- Internal linework lives in the `<g stroke-width="2">` group (inner hexagon +
  6 radii) plus the `g50` wireframe-cube group and the per-product glyph path.
- Recolor by editing `fill`/`stroke` in the `style` attributes; keep dark/common
  pairs in sync.
- Files keep Inkscape metadata (`sodipodi:*`, `inkscape:*`) so they round-trip
  cleanly in Inkscape; consumers embed the geometry without that metadata.
