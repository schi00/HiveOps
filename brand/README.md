# HIVE brand marks (editable source)

SVG sources for the HIVE product family identity (board v2 — isometric cube).

| File | Mark | Accent |
|---|---|---|
| `hive-mark.svg` | Parent: isometric cube (hexagon + inner Y) | Amber `#F5B323` |
| `hivecrew-mark.svg` | HiveCrew: hexagon + headset | Amber `#F5B323` |
| `hiveops-mark.svg` | HiveOps: hexagon + crossed wrench/screwdriver | Cyan `#00F0FF` |

## Editing notes

- All marks share the same outer hexagon path (`M32 8 52.8 20v24L32 56 11.2 44V20Z`),
  so product glyphs can be swapped inside a consistent container.
- Parent keeps the inner Y (`M32 8v24M32 32 11.2 44M32 32l20.8 12`) — the cube faces.
  Product marks drop the inner Y so the glyph reads cleanly.
- Recolor by editing `stroke`/`fill` attributes; weight via `stroke-width`.
- Works monochrome: set all strokes/fills to `#FFFFFF` (dark bg) or `#171B22` (light bg).
- The crossed-tools glyph embeds the Heroicons `wrench-screwdriver` outline (MIT licensed),
  scaled `1.25x` and centered — replace the `<g>` block to iterate on the pictogram.
