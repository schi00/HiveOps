# HiveOps Landing

Landing page de **HiveOps by HIVE** — el AI Support Engineer del ecosistema HIVE: soporte autónomo L1/L2/L3 con investigación de causa raíz, remediación gobernada y audit trail verificable.

Built with Next.js 16 (App Router), React 19, Tailwind CSS 4 y framer-motion.

## Getting Started

```bash
npm install
npm run dev
```

Open [http://localhost:3000](http://localhost:3000) with your browser to see the result.

## Environment variables

- `NEXT_PUBLIC_GA_ID` — Google Analytics 4 measurement ID (optional; scripts are skipped when unset).
- `NEXT_PUBLIC_DASHBOARD_URL` — URL of the HiveOps dashboard served by the .NET API (defaults to `http://localhost:5198/dashboard/` for local development).

## Scripts

- `npm run dev` — development server
- `npm run build` — production build (standalone output for Docker)
- `npm run start` — serve the production build
- `npm run lint` — ESLint

## Docker

```bash
docker build -t hiveops-landing .
docker run -p 3000:3000 hiveops-landing
```

The image uses Next.js standalone output and runs as a non-root user.
