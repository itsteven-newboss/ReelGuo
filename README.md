# ReelGuo

Vertical short-drama platform for adults: a C# (.NET 10) API and media worker, a React web app installable as a PWA, a React admin console, and a Cloudflare Worker for the media CDN.

Visitors see it as **黄月短剧** (huangyueai.com). The name is set in two places, `web/packages/ui/src/brand/name.ts` and `backend/src/ReelGuo.Api/Brand.cs`. "ReelGuo" is the code name. It stays wherever software reads it: package and service names, headers, the app's user agent, and `app_name` in `/api/v1/config`, which the desktop and Android apps check.

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): decisions, system design, flows, country blocking, compliance, roadmap
- [docs/UI-REFERENCE.md](docs/UI-REFERENCE.md): screen-by-screen UI spec and build status
- [docs/RUNBOOK.md](docs/RUNBOOK.md): operations, including alerts, takedowns, country rules, payments and refunds, backups and monitoring
- [docs/STAGING.md](docs/STAGING.md): setting up and updating the staging server (native Windows services, behind Cloudflare)

## Prerequisites

- .NET 10 SDK
- Node 22.12+ and pnpm 10 (`npm i -g pnpm@10`)
- Docker Desktop (Postgres)
- ffmpeg: on PATH, or unpacked to `.tools/ffmpeg/bin` (e.g. the gyan.dev "essentials" build on Windows)
- Native apps only: Rust (stable) and, on Windows, the MSVC C++ build tools; the Android SDK and NDK for the APK

## Quick start

```bash
pnpm install
pnpm db:up        # Postgres + Redis in Docker
pnpm dev          # API :5080 · worker · app http://localhost:5173 · admin http://localhost:5174
pnpm seed:demo    # optional: labelled demo series through upload → encode → publish
```

Admin sign-in in development is `admin` / `reelguo-dev-admin`. To simulate a visitor's country, send the `X-Debug-Country: CN` header (works in Development only).

Purchases in development use a sandbox payment provider: 充值 (top up) opens a 模拟支付 (simulated payment) page, and no money moves. The first demo series is paid from episode 3.

## Common tasks

| Task | Command |
|---|---|
| All tests | `pnpm test` (set `REELGUO_TEST_PG` to run database tests without Docker) |
| Backend tests only | `cd backend && dotnet test` |
| Typecheck / build web | `pnpm typecheck` / `pnpm build` |
| Regenerate the TS client after API changes | `pnpm gen:api` |
| Add a migration | `dotnet ef migrations add <Name> --project backend/src/ReelGuo.Data --startup-project backend/src/ReelGuo.Data` |
| Everything in containers | `REELGUO_MEDIA_KEY=… REELGUO_JWT_KEY=… REELGUO_ADMIN_PASSWORD=… docker compose -f infra/docker-compose.yml --profile app up --build` |
| Prepare videos for the folder upload | `powershell -ExecutionPolicy Bypass -File scripts\prepare-videos.ps1 -Source <folder>`: H.264/AAC, faststart, about 2 Mbit/s, into `<folder>-upload` (docs/STAGING.md §6) |
| Change a site's icon | Replace its picture (`"icon"` in `web/sites/<key>/site.json`: `Assets/huangyueai-moon-icon.png` for 黄月短剧, any transparent PNG, 1024 px or more keeps the app icons sharp; `Assets/sites/<key>.jpg` for the country sites, a square app icon), run `pnpm brand:icons` (or `--site <key>`), commit what it writes. It makes the page mark (lossless WebP), favicons, install icons, the iPhone profile icon and 黄月短剧's app icons. |
| Build a country site | `REELGUO_SITE=yelrama pnpm --filter @reelguo/client build` (also `ypinedrama`, `apinedrama`; the admin console the same way). Staging does it for every site (docs/STAGING.md §6b). |
| Deploy the media edge | see `edge/wrangler.toml` |
| Takedown drill | `node scripts/takedown-drill.mjs --series <test id> --restore` (RUNBOOK §3) |
| Staging: first setup / update / rollback | `scripts\staging\setup.ps1` / `update.ps1` / `rollback.ps1` (docs/STAGING.md) |
| Load test (staging) | `node scripts/loadtest.mjs --app <url> --vus 100 --duration 300` |
| Restore test | `docker compose -f infra/docker-compose.yml --profile app run --rm backup "sh /scripts/restore-test.sh"` |
| Desktop app against the local servers | `pnpm --filter @reelguo/native app:dev` |
| Desktop installer | `REELGUO_ORIGINS=https://… TAURI_SIGNING_PRIVATE_KEY=<key file> pnpm --filter @reelguo/native app:build` |
| Desktop + Android release builds | GitHub Actions → **Native apps** (RUNBOOK §9) |
| Test the production build locally | `pnpm build`, then `npx vite preview` in `web/apps/client` (serves the production security headers) |
