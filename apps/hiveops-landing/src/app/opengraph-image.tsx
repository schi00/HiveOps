import { ImageResponse } from "next/og";

export const runtime = "edge";
export const alt = "HiveOps by HIVE — Agente de soporte técnico autónomo con IA";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

export default function OpengraphImage() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "center",
          background: "linear-gradient(135deg, #08090b 0%, #0a1417 55%, #082028 100%)",
          padding: "72px",
          fontFamily: "sans-serif",
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 24 }}>
          <div
            style={{
              width: 88,
              height: 88,
              borderRadius: 20,
              border: "4px solid #22d3ee",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              fontSize: 44,
              color: "#22d3ee",
            }}
          >
            ⬡
          </div>
          <div
            style={{
              fontSize: 76,
              fontWeight: 800,
              letterSpacing: "0.14em",
              color: "#67e8f9",
            }}
          >
            HiveOps
          </div>
        </div>
        <div
          style={{
            marginTop: 36,
            fontSize: 38,
            fontWeight: 600,
            color: "#eceff2",
            lineHeight: 1.25,
            maxWidth: 920,
          }}
        >
          Agente de soporte técnico autónomo — detecta, diagnostica y remedia incidentes
        </div>
        <div
          style={{
            marginTop: 28,
            display: "flex",
            gap: 14,
            fontSize: 22,
            color: "#9aa4ae",
          }}
        >
          <span>Detección proactiva</span>
          <span>·</span>
          <span>Root cause</span>
          <span>·</span>
          <span>Remediación con aprobación</span>
          <span style={{ marginLeft: 24 }}>ops.hivecorp.com.ar</span>
        </div>
      </div>
    ),
    size
  );
}
