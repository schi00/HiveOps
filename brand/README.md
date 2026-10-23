# HIVE brand marks (editable source)

SVG sources for the HIVE product family identity — **filled honeycomb** system:
color-filled hexagon + inner hexagon + 6 radial lines + product glyph.

| File | Mark |
|---|---|
| `hive-mark.svg` / `hive-mark_dark.svg` | Parent: amber `#F5B323` filled honeycomb, dark lines |
| `hivecrew-mark.svg` / `hivecrew-mark-dark.svg` | HiveCrew: amber filled honeycomb + dark headset glyph |
| `hiveops-mark.svg` / `hiveops-mark-dark.svg` | HiveOps: cyan `#25D9E8` filled honeycomb + transparent gear cutout (accounts-style gear) |

## Variants

The filled hexagon provides its own contrast, so `*-mark.svg` and `*-mark-dark.svg`
are currently identical — the same asset works on light and dark backgrounds.
Keep the pair anyway so consumers can swap per theme if a variant ever diverges.

## Editing notes

- All marks share the outer hexagon `M32 8 52.78 20v24L32 56 11.22 44V20Z`
  (R=24, center 32,32), the inner hexagon (R=13) and the 6 radial spokes —
  so glyphs can be swapped inside a consistent honeycomb container.
- HiveOps gear is a **mask** (`gear-cut`): dashed ring + center disc are cut out
  (transparent — the page background shows through); the small center hole stays
  in the accent color. Gear size = ring r≈10 / disc r≈7.6 / hole r=3.
- HiveCrew glyph is a scaled `translate/scale` group — adjust `scale()` to resize.
- Recolor by editing the hexagon `fill` and the linework `stroke` (`#0B0F17`).
- Consumers embed the geometry cropped (`viewBox="8 8 48 48"`) in favicons and
  dashboard marks.
