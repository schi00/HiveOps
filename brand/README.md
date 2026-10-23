# HIVE brand marks (editable source)

SVG sources for the HIVE product family identity — **filled honeycomb** system:
color-filled hexagon + inner hexagon + 6 radial spokes + product glyph cutout.

| File | Mark | Background |
|---|---|---|
| `hive-mark.svg` | Parent: honeycomb cube, amber fill | Light |
| `hive-mark_dark.svg` | Parent: black cube, amber internals | Dark |
| `hivecrew-mark.svg` | HiveCrew: filled honeycomb + headset glyph | Any |
| `hivecrew-mark-dark.svg` | HiveCrew: same (filled hexagon reads on both) | Any |
| `hiveops-mark.svg` | HiveOps: cyan `#25D9E8` filled honeycomb + **transparent gear cutout** | Any |
| `hiveops-mark-dark.svg` | HiveOps: same | Any |

## Variants

The filled hexagon provides its own contrast, so product marks (`hivecrew-*`,
`hiveops-*`) currently ship identical common/dark pairs — the same asset works
on light and dark backgrounds. The pair is kept so consumers can swap per theme
if a variant ever diverges.

## Editing notes

- All marks share the outer hexagon `M32 8 52.78 20v24L32 56 11.22 44V20Z`
  (R=24, center 32,32), the inner hexagon (R=13) and the 6 radial spokes —
  glyphs swap inside a consistent honeycomb container.
- **HiveOps gear** is an SVG `mask` (`gear-cut` / `hiveops-gear-cut`): the
  detailed gear path is drawn black inside the mask, so it knocks out
  **transparent** — the page background shows through the filled cyan honeycomb.
  Gear scale = `scale(1.9)` around its bbox center (33.38, 31.09) recentered at
  (32,32); ~25 px (~60 % of hexagon width).
- Honeycomb linework color: `#0B0F17` (graphite). Hexagon fill = product accent.
- Consumers (favicon, dashboard, `BrandLogo`) embed the geometry cropped
  (`viewBox="8 8 48 48"`); the `brand/` files keep the full `0 0 64 64` canvas
  for editing.
