# ReelGuo architecture

ReelGuo is a vertical short-drama platform for adults: a C# backend, a web app installable as a PWA, and an admin console. The app's UX follows the reference product closely. Content comes from our own licensed source.

This document is the reference for the whole build. It covers the decisions, the system design, every flow, and the phase plan. [UI-REFERENCE.md](UI-REFERENCE.md) is the screen-by-screen spec.

## Contents

1. [Decisions](#1-decisions)
2. [Boundaries](#2-boundaries)
3. [System overview](#3-system-overview)
4. [Repository layout](#4-repository-layout)
5. [Running locally](#5-running-locally)
6. [Data model](#6-data-model)
7. [API conventions](#7-api-conventions)
8. [Country blocking](#8-country-blocking)
9. [Flows](#9-flows)
10. [Web / PWA specifics](#10-web--pwa-specifics)
11. [Compliance checklist](#11-compliance-checklist)
12. [Roadmap](#12-roadmap)
13. [Engineering notes](#13-engineering-notes)

---

## 1. Decisions

| Topic | Decision |
|---|---|
| Audience | Adults (18+). Compliance work is part of every phase (§11). |
| Markets | Outside mainland China. Countries are blocked through an admin-managed list (§8); the legal team sets it before launch. |
| Language | Simplified Chinese (`zh-CN`). Every string lives in locale files. |
| Content | Our own licensed source. Intake accepts any common video format (§9.1). |
| AI换脸 channel | Placeholder only, with status `coming_soon` and no actions (§2). |
| Platforms | Web/PWA, plus a Windows/macOS/Linux desktop app and an Android APK (Phase 6). The apps are a thin Tauri 2 shell around the same web app, loaded from the server (§9.11). |
| Backend | C# on .NET 10 (LTS, supported until Nov 2028). |
| Admin console | React, sharing the app's design tokens and generated API client. |
| UI fidelity | As close as possible to the reference product in layout, flows and interaction, with our own brand (§2). Page layout follows the reference's live web app (a portal: header, home sections, series pages); player behaviour follows HGhub. See [UI-REFERENCE.md](UI-REFERENCE.md). |

## 2. Boundaries

- **No face-swap functionality, ever.** AI换脸 exists only as a channel row with `status = coming_soon`, and the API refuses to create, import or publish content in channels that aren't open. If the channel is ever switched to `active`, everything published in it must pass the consent rule (§11). The admin console asks for confirmation before opening it.
- **Own brand.** We match the reference's layout, navigation, interaction thresholds and generic Chinese UI labels. We do not copy its name, logo, icons, images, slogans, or anything implying affiliation. The accent colour matches the reference's yellow by the product owner's choice (2026-11-12); the play-mark icon is a placeholder until the ReelGuo brand is designed.
- **Clean-room code.** HGhub (the open-source client of the reference) is read for behaviour only. Its code from v1.1.0 onward is PolyForm Noncommercial, so we never paste its code, CSS or SVG icons. ReelGuo uses lucide icons and its own implementation.
- **No connection to the reference site.** We never fetch its content or covers, and never call its API.

## 3. System overview

```
          ┌────────────────────────── Clients ───────────────────────────┐
          │  Web / PWA (React + hls.js)        Admin console (React)      │
          └──────┬──────────────────────┬──────────────────────┬─────────┘
     /api/v1/*   │ same origin           │ HLS + images         │ /api/admin/* (session cookie)
                 ▼                       ▼                      ▼
     ┌──────────────── ASP.NET Core API ───────────┐    ┌──────────── Media edge ─────────────┐
     │ GeoBlocking → endpoints (public / admin)     │    │ dev: API /media/*   prod: CF Worker │
     │ signs playback URLs · OpenAPI → TS client    │    │ token check + country block list    │
     └────┬───────────────────────────┬────────────┘    └──────────────┬──────────────────────┘
          │                           │ uploads (dev) / presigned (prod)│ reads
     PostgreSQL ◀── media_jobs ──┐    ▼                                  ▼
     (catalog, users, audit,     │  raw bucket (originals, incoming/)   media bucket (HLS, posters, covers)
      blocked countries)         │        │                                  ▲
                                 └── Worker (.NET) ── ffprobe → ffmpeg ──────┘
```

| Piece | Choice | Notes |
|---|---|---|
| API | ASP.NET Core minimal APIs, one folder per feature | Built-in OpenAPI, health checks, rate limiting |
| Data | EF Core 10 + Npgsql on PostgreSQL 17 | snake_case tables; migrations in the repo |
| Jobs | Postgres job table (`media_jobs`) claimed with `FOR UPDATE SKIP LOCKED`; rankings on a 10-minute timer with an advisory lock | Used instead of Hangfire; see §13 |
| Worker | .NET Worker Service running ffprobe/ffmpeg | Retries with back-off; orphaned jobs are reclaimed after 30 min |
| Storage | `IObjectStorage` with filesystem (dev/tests) and S3 (R2/S3/MinIO/Garage) implementations | Two buckets: `raw` (private) and `media` (served by the edge) |
| Media edge | API `/media/*` in development; Cloudflare Worker (`edge/`) in front of R2 in production | Same URL scheme, signing and blocking rules; shared test vector |
| Admin auth | Admin accounts (role Admin) with an httpOnly cookie session, CSRF header, rate-limited login | The first admin is created from configuration |
| App | React 19, Vite 8, Tailwind 4, TanStack Query, Zustand, i18next, hls.js 1.7, vite-plugin-pwa | |
| Contract | OpenAPI → `openapi-typescript` → `openapi-fetch` | CI fails if the generated types are stale |
| Search | Postgres `pg_trgm` GIN index on titles plus ILIKE on tags, descriptions and authors | Meilisearch later if the catalog outgrows it |
| Posts | Markdown (Markdig) → HTML sanitised by HtmlSanitizer on save, and by DOMPurify again on display | Images only from our own media host |
| Native apps | Tauri 2 shell (Rust) that loads the web app from the server, with mirror fallback, window fullscreen, external links, and updates (signed in-place updates on desktop, an APK download prompt on Android) | `native/`; builds in GitHub Actions (`native.yml`) |
| Payments | `IPaymentProvider` adapters (hosted checkout + signed server notices + refunds); a sandbox provider for development | The real adult-friendly provider is a business decision (§9.10) |
| Tests | xUnit v3 (API, worker, real ffmpeg, real Postgres), Vitest (gestures, formatting, play events, edge signing, money rules) | |

Libraries we deliberately avoid: MediatR, AutoMapper, MassTransit v9+ and FluentAssertions v8+ all moved to paid commercial licences in 2025, and this design doesn't need them.

## 4. Repository layout

```
ReelGuo/
  backend/
    ReelGuo.slnx, Directory.Build.props, Directory.Packages.props
    openapi/ReelGuo.Api.json     generated at build, committed
    src/ReelGuo.Api              public API, admin API, auth, geo blocking, media edge (dev)
    src/ReelGuo.Data             entities, DbContext, migrations, storage, media URL signing, job queue,
                                 publish rules, CSV parser
    src/ReelGuo.Worker           job runner, ffprobe parsing, encode plan, transcoder
    tests/ReelGuo.Api.Tests      unit, API, database and end-to-end pipeline tests
  web/
    apps/client                  the PWA: portal pages (src/portal), players (src/feed, src/player), account, billing
    apps/admin                   admin console
    packages/api-client          generated types + typed fetch client
    packages/ui                  design tokens (tokens.css) and shared primitives
  edge/                          Cloudflare Worker for the media host (+ tests)
  native/                        Tauri 2 desktop and Android shell: boot/ (bundled start page), src-tauri/ (Rust)
  scripts/seed-demo.mjs          generates labelled demo episodes and runs them through the real pipeline
  infra/                         docker-compose, Caddyfile, web Dockerfile
  docs/                          this file, UI-REFERENCE.md
  .tools/ (git-ignored)          local ffmpeg, Rust toolchain and the updater signing key for development
  .data/  (git-ignored)          local media storage for development
```

## 5. Running locally

You need:
- the .NET 10 SDK;
- Node 22.12+ with pnpm 10;
- Docker Desktop (Postgres);
- ffmpeg: on PATH, set via `REELGUO_FFMPEG_DIR`, or unpacked to `.tools/ffmpeg/bin`. The worker and tests look in that folder first.

```bash
pnpm install
pnpm db:up          # Postgres + Redis in Docker
pnpm dev            # API :5080, worker, app :5173, admin :5174
pnpm seed:demo      # optional: 4 labelled demo series through the real upload → encode → publish pipeline
```

| URL | What |
|---|---|
| http://localhost:5173 | App (Vite proxies `/api` and `/media` to the API) |
| http://localhost:5174 | Admin console: account `admin`, password `reelguo-dev-admin` (Development only) |
| http://localhost:5080/scalar/v1 | Interactive API docs (Development only) |
| http://localhost:5080/health/ready | Readiness, including the database |

- **Development migrations.** In Development the API applies migrations on startup and creates the first admin.
- **Simulating a country.** Send `X-Debug-Country: CN`. This works only in Development.
- **API changes.** After changing an endpoint, run `pnpm gen:api`.
- **Native apps.** You need Rust plus the MSVC C++ build tools on Windows (WebKitGTK on Linux; Xcode on macOS); the
  Android SDK and NDK are needed only for the APK. Run `pnpm --filter @reelguo/native app:dev` to open the desktop app
  against the local preview servers (:4173, then :5173), or set `REELGUO_ORIGINS=https://…` for other servers.
  `app:build` builds the installer; set `TAURI_SIGNING_PRIVATE_KEY` to sign it for the updater. On the development PC,
  Rust lives in the git-ignored `.tools/` folder: set `RUSTUP_HOME=.tools/rustup`, `CARGO_HOME=.tools/cargo` and put
  `.tools/cargo/bin` on `PATH` first.
- **Purchases in development.** The sandbox payment provider is on: 充值 (top up) in the app leads to a 模拟支付 (simulated payment) page with 支付成功 / 支付失败 / 取消支付 (paid / failed / cancelled) buttons. `pnpm seed:demo` makes the first demo series paid (episodes 3+ cost 30 coins).
- **Pointing the dev servers at another API.** Set `REELGUO_API_URL` (default `http://localhost:5080`), for example `REELGUO_API_URL=http://localhost:5090 npx vite --port 5175` in `web/apps/client`. This is useful for running a second API build next to one that's already running.
- **Database tests without Docker.** Set `REELGUO_TEST_PG` to a server connection string (for example `Host=localhost;Username=reelguo;Password=reelguo`). Each test class creates and drops its own database. Without it, the tests use Testcontainers, and skip themselves when Docker is missing.
- **Running everything in containers.** Use `docker compose -f infra/docker-compose.yml --profile app up --build` with `REELGUO_MEDIA_KEY`, `REELGUO_JWT_KEY` and `REELGUO_ADMIN_PASSWORD` set. The app is then on :8080 and the admin console on :8081.

## 6. Data model

Tables are snake_case. Enums are stored as text. ✓ marks tables that exist.

```
✓ users            id, username, normalized_username (unique), password_hash, nickname, avatar_url,
                   avatar_color, sign, role(User|Creator|Admin), status, age_verified_at, terms_accepted_at,
                   last_login_at — viewers (User) sign in to the app, admins to the console; creators are
                   author profiles (no login)
✓ categories       id, slug (unique), name, description, status(Active|ComingSoon|Hidden), sort_order
✓ series           id, category_id, author_id?, title, description, cover_key, tags text[], score, status,
                   is_finished, is_original, total_episodes, episode_count, published_at, last_episode_at,
                   like/collect/comment/play counts, hot_score, recommend_weight (editor pick),
                   external_id (unique), rights_holder, contract_ref, licence_start/end, territories text[] (GIN),
                   ai_generated?, has_real_person?, consent_ref
✓ episodes         id, series_id, episode_number (unique per series), title, duration_seconds, status,
                   source_key, hls_prefix, media_version, renditions_json, width, height, poster_key,
                   processing_progress, processing_error, is_free, published_at
✓ media_jobs       id, kind, state(Queued|Running|Succeeded|Failed), episode_id, attempts, max_attempts,
                   run_after, locked_by, locked_at, error
✓ play_events      id, series_id, episode_number, kind(Start|Complete), session_id, country, at — anonymous, kept 30 days
✓ ranks            kind(Hot|Recommend|Potential), position, series_id, metric_value, computed_at
✓ posts            id, title, summary, content_markdown, content_html (sanitised), cover_key, tags, series_id?,
                   status(Draft|Published|Hidden), published_at, view/like/collect/comment counts
✓ blocked_countries country_code (PK), note, created_at, updated_at, updated_by
✓ audit_log        id, at, actor, action, target_type, target_id, details_json — append-only

✓ refresh_tokens   id, user_id, family_id, token_hash (SHA-256, unique), created_at, expires_at, revoked_at,
                   replaced_by_id, user_agent — one family per sign-in
✓ series_likes, series_collects, post_likes, post_collects, comment_likes
                   (user_id, target id) primary key, created_at. Likes are per series (the reference likes per
                   episode; one heart per show is simpler in the feed and matches the counts on every card).
✓ follows          (follower_id, followee_id) primary key, created_at
✓ watch_history    (user_id, series_id) primary key, episode_number, position_seconds, duration_seconds,
                   updated_at — the newest entry wins
✓ comments         id, target_type(Series|Post), target_id, episode_number (0 for posts), user_id, parent_id?
                   (replies one level deep), text ≤ 500, status(Pending|Approved|Rejected), like_count,
                   reply_count, pinned, country, reject_reason, moderated_by ("system" = term filter), moderated_at
✓ reports          id, target_type(Series|Post|Comment|User), target_id, reason, urgent, detail, reporter_id?,
                   country, status(Open|Resolved|Dismissed), resolution_note, resolved_by, resolved_at
✓ moderation_terms id, term (unique), created_at, created_by
✓ age_rules        country_code (PK), mode(Declaration|Strict), note, created_at, updated_at, updated_by
✓ worker_heartbeats worker_id (PK), version, started_at, seen_at, jobs_succeeded, jobs_failed, current_job_id
✓ playback_samples id, session_id, series_id, episode_number, country, device(Mobile|Desktop), browser,
                   startup_ms?, rebuffer_count, rebuffer_ms, watched_seconds, max_height?, error?, at
                   — anonymous playback quality, one row per play, kept 30 days
✓ series + episodes (Phase 5) series.episode_price (coins per paid episode; 0 = free series), series.free_episodes (leading
                   free episodes, default 3); episodes.is_free now means "free even in a paid series"
✓ products         id, kind(Coins|Vip), name, price_cents, currency, coins, bonus_coins, vip_days, badge, active, sort_order
✓ orders           id, order_no (unique, random), user_id, product_id?, kind, product_name, price_cents, currency, coins,
                   bonus_coins, vip_days (a snapshot of the product), status(Pending|Paid|Failed|Canceled|Expired|Refunded|
                   ChargedBack), provider, provider_ref, country, status_reason, created_at, paid_at, reversed_at
✓ wallets          user_id (PK), coins (purchased; negative after a reversal of spent coins), bonus_coins (CHECK ≥ 0),
                   vip_until, auto_unlock, updated_at
✓ wallet_entries   id, user_id, kind(Purchase|Unlock|Refund|Chargeback|Adjustment), coins_delta, bonus_delta, coins_after,
                   bonus_after, vip_days, vip_until_after, order_id?, series_id?, episode_number?, description, actor, note, at
                   — append-only ledger; the viewer's 消费记录 (records)
✓ episode_unlocks  (user_id, series_id, episode_number) PK, coins, at — bought episodes, kept for good
✓ payment_events   id, provider, event_id (unique per provider), type, order_no, amount_cents, currency,
                   outcome(Applied|Duplicate|Ignored|Rejected), detail, payload (≤ 8 KB), received_at
✓ app_releases     id, platform (windows-x86_64 | darwin-aarch64 | darwin-x86_64 | linux-x86_64 | android), version,
                   url, signature (desktop updater), sha256, notes, mandatory, published, published_at, updated_by
✓ topics           id, title ≤ 40, description ≤ 500, published, sort_order, created_at, updated_at — 专题
✓ topic_series     (topic_id, series_id) PK, position — the topic's series, in order
✓ announcements    id, text ≤ 200, url? ("/…" or https), active, sort_order, updated_at — the home-page 公告 line
✓ series_ratings   (user_id, series_id) PK, score 1–10 (CHECK), updated_at — viewer ratings
```

**Seed data** (in the initial migration):
- The four channels: AI成人短剧, AI成人漫剧, AI换脸 (`ComingSoon`) and AI魔改.
- `CN` on the block list.
- The starter store (Phase 5): four coin packs and three VIP passes at placeholder prices, to be reviewed in **Admin → 商品** (products).

## 7. API conventions

- **Base paths:**
  - `/api/v1/*` for the app (public, country-checked).
  - `/api/admin/*` for the admin console (session cookie plus the `X-ReelGuo-Admin` CSRF header on state changes; not country-checked).
  - Viewers send `Authorization: Bearer <access token>`. The cookie-based `/api/v1/auth/*` endpoints also require the `X-ReelGuo-App` header (CSRF).
  - `/api/edge/config` for the CDN worker (requires `X-Edge-Secret`).
  - `/api/payments/{provider}/webhook` for payment provider notices (signature-checked; not country-blocked or age-gated, because providers call from anywhere).
  - `/media/*` for the development media edge.
- **JSON:** field names and enum values are snake_case, and numbers are strict (no numeric strings). Response shapes follow the reference client (`VideoCard`, `Detail`, `PlayInfo`, `Paged`) so the copied screens bind field for field.
- **Errors:** every error body is `{ "error": { "code": "...", "message": "...", "details": [...]? } }`. `details` lists every unmet requirement when a publish is blocked.

| Status | Codes |
|---|---|
| 400 | `bad_request` (unparseable query/route/body), `invalid_*`, `publish_blocked`, `category_not_open`, `source_missing`, `terms_required`, `weak_password`, `cannot_follow_self`, `invalid_parent`, … |
| 401 | `unauthorized`, `invalid_credentials`, `session_expired` (refresh cookie missing, expired, revoked or replayed) |
| 402 | `episode_locked` (`/play` of a paid episode the viewer hasn't unlocked), `insufficient_coins` (unlock with too few coins) |
| 403 | `csrf_header_missing`, `app_header_missing`, `media_token_invalid`, `age_verification_required` (strict-age country, §9.7) |
| 404 | `not_found` (also returned when content isn't licensed for the visitor's country, so nothing leaks), `store_unavailable` (purchases switched off) |
| 409 | `episode_exists`, `username_taken`, `external_id_taken`, `already_resolved`, `not_refundable`, `provider_unavailable` |
| 410 | `media_revoked` (the series was taken down after the play link was issued, §9.8) |
| 429 | `too_many_attempts` (per IP: admin login and viewer register/login 10 per minute, refresh 60 per minute, play events 120 per minute, posting comments 30 per minute, reports 20 per hour, unlocks 60 per minute, new orders 20 per 10 minutes, provider notices 600 per minute); `comment_rate_limited` (per account: 10 comments per 10 minutes) |
| 451 | `region_blocked` |
| 500 | `internal_error`, with a `trace_id` to find the request in logs and traces |
| 502 | `payment_unavailable` (the provider refused to open a checkout), `refund_failed` |

### Endpoints

| Phase | Endpoint |
|---|---|
| ✓ 0 | `GET /api/v1/config`, `GET /api/v1/categories`; admin geo block list + audit log |
| ✓ 1 | `GET /api/v1/feed?page=&size=`, `GET /api/v1/categories/{slug}/series?sort=latest\|hot\|score`, `GET /api/v1/series/{id}`, `GET /api/v1/series/{id}/episodes/{ep}/play` |
| ✓ 1 | Admin: `auth/login`, `auth/logout`, `session`, `users` (create, enable/disable, reset password), `categories`, `series` (CRUD, cover, publish, unpublish, episodes), `episodes/{id}` (update, upload, uploaded, retry, delete), `import` (incoming uploads, template, dry run, run), `uploads/raw/{key}` (dev uploads) |
| ✓ 1 | `GET /api/edge/config` (block list for the CDN worker) |
| ✓ 2 | `POST /api/v1/events` (batched, 120/min per IP), `GET /api/v1/ranks/{hot\|recommend\|potential}`, `GET /api/v1/search?q=`, `GET /api/v1/search/suggest?seed=`, `GET /api/v1/posts`, `GET /api/v1/posts/{id}` |
| ✓ 2 | Admin: `posts` (CRUD, Markdown preview, cover, inline images, publish/unpublish), `ranks/{kind}`, `ranks/recompute`; series `recommend_weight` |
| ✓ 3 | `POST /api/v1/auth/register\|login\|refresh\|logout`; `GET\|PUT /api/v1/me`, `GET\|PUT\|DELETE /api/v1/me/history`, `POST /api/v1/me/merge`, `GET /api/v1/me/favorites` |
| ✓ 3 | `PUT\|DELETE` `/api/v1/series/{id}/like\|collect`, `/posts/{id}/like\|collect`, `/users/{id}/follow`, `/comments/{id}/like` (each returns `{ on, count }`); `GET\|POST /api/v1/series/{id}/episodes/{ep}/comments`, `GET\|POST /api/v1/posts/{id}/comments`, `GET /api/v1/comments/{id}/replies`, `DELETE /api/v1/comments/{id}`; `POST /api/v1/reports` |
| ✓ 3 | Admin: `comments` (queue, bulk moderate, pin), `moderation/terms`, `moderation/summary` (nav badges), `reports` (list, resolve), `members` (list, enable/disable) |
| ✓ 4 | `/api/v1/config` adds `country` and `age_check`; `POST /api/v1/events` also takes playback-quality `samples`; `GET /health/worker` |
| ✓ 4 | Admin: `geo/age-rules`, `members/{id}/age-verification`, `system/status`, `analytics/overview?days=`; `/api/edge/config` adds `revoked_series` |
| ✓ 5 | `/play` answers 402 `episode_locked` for paid episodes; series episodes carry `is_free` and `price`; `/config` adds `monetization`; `GET /api/v1/store`; `GET /api/v1/me/wallet`, `PUT /me/wallet/settings`, `GET /me/wallet/entries`; `GET /series/{id}/access`; `POST /series/{id}/episodes/{ep}/unlock`; `POST /orders`, `GET /orders/{orderNo}` |
| ✓ 5 | `POST /api/payments/{provider}/webhook`; sandbox checkout page `GET\|POST /api/payments/sandbox/checkout/{orderNo}` (development) |
| ✓ 5 | Admin: `products`, `orders` (list, `refund`), `members/{id}/wallet` (+ `adjust`), `analytics/revenue?days=`; series `episode_price` / `free_episodes`; `system/status` adds `payments` |
| ✓ 6 | `GET /api/v1/app/bootstrap?platform=&version=` (mirrors + update status), `GET /api/v1/app/releases` (download page), `GET /api/v1/app/updates/{target}/{arch}/{current}` (Tauri updater feed: 200 or 204). Not age-gated (the shell calls them before the web app loads); country-blocked like all of `/api/v1` |
| ✓ 6 | Admin: `app-releases` (list, create, edit / publish / withdraw, delete) |
| ✓ Portal | `GET /api/v1/site` (tagline, active announcements, mirrors, permanent domain, Telegram, email), `GET /api/v1/topics`, `GET /api/v1/topics/{id}`, `GET\|PUT /api/v1/series/{id}/rating`; `/feed?sort=latest\|updated\|hot\|picks\|random`; channel `sort=latest\|hot\|picks\|original\|random\|score`; cards add `last_episode_at` and the channel; series detail adds `rating`; rank items add `description` |
| ✓ Portal | Admin: `topics` and `announcements` (CRUD, audited as `topic.*` / `announcement.*`) |

## 8. Country blocking

**Purpose:** the legal team decides where ReelGuo may operate, and enforces that without code changes.

### How it works

1. **The list.** The `blocked_countries` table holds the list. It ships with `CN` (mainland China), marked "待法务审核确认" (pending legal confirmation).
2. **What is blocked.** Every request to `/api/v1/*` **and every media request** (the API's `/media/*` and the CDN worker) from a listed country gets **451** `region_blocked`.
3. **What the user sees.** The app shows a full-screen "当前地区暂不提供服务" (service not available in your region) page. Legal pages stay reachable.
4. **What is never blocked.** `/api/admin/*` and `/health/*`, so the team can manage the list from anywhere.
5. **When changes take effect.** The list is cached for 60 seconds: in each API instance, and in each CDN worker isolate, which reads it from `/api/edge/config`. An edit applies immediately on the instance that made it.
6. **Failures.** If the database can't be reached, the API keeps enforcing the last known list. A CDN worker that has never loaded the list refuses media rather than serving it.
7. **Audit trail.** Every change is written to `audit_log` under the signed-in admin's account name.

### Licensed territories (per series)

Separately from the block list, each series lists the countries it's licensed for, or `*` for worldwide. A series is hidden from the feed, channel lists, detail and play for any other country, and outside its licence dates.

### Where the country comes from

| Source | Used when |
|---|---|
| CDN header (`CF-IPCountry`, setting `Geo:CountryHeader`) | `Geo:TrustCountryHeader = true`. Turn this on only when every request reaches the API through the CDN. |
| `request.cf.country` | In the CDN worker (always) |
| `X-Debug-Country` header | Development environment only |
| Neither | `XX` (unknown) |

Besides ISO codes, the list accepts these special codes:
- `XX`: unknown country.
- `T1`: Tor exit traffic, as reported by Cloudflare.
- `XK`: Kosovo, which has no ISO code but is used by Cloudflare and MaxMind.

### Production checklist

- [ ] The legal team reviews the default list and the target markets, then sets the list in **Admin → 地区屏蔽** (country blocking).
- [ ] The API sits behind the CDN with `Geo__TrustCountryHeader=true`, `Network__ClientIpHeader=CF-Connecting-IP` and `Network__TrustedProxies` set to the proxy network. With trusted proxies configured, the CDN's country header is believed only on requests that came through them.
- [ ] The origin accepts traffic only from the CDN.
- [ ] Decide whether `XX` and `T1` are blocked.
- [x] The media edge enforces the same list (API `/media/*`; CDN worker in `edge/`).
- [x] Per-country age-verification rules (**Admin → 年龄验证**, §9.7).

## 9. Flows

### 9.1 Ingest

**Single upload (admin console):**
```
Series editor → "上传分集" (upload episodes, multi-select; files are numbered in name order)
 → POST /series/{id}/episodes → upload ticket (presigned PUT in production; API upload endpoint in dev)
 → browser uploads directly, with progress → POST /episodes/{id}/uploaded → media_jobs row queued
```

**Bulk import:**
```
Files into raw incoming/ (admin page, or rclone/S3 tools for large batches)
 → CSV manifest (one row per episode; template downloadable) → dry run report → import
 → series created/updated by external_id, new episodes queued, existing episodes skipped
```

**Worker, for each job:**
1. Downloads the source.
2. Runs ffprobe. Files with no video stream, shorter than 1 s, or unreadable are rejected permanently, with the reason shown in admin.
3. Normalises the video:
   - applies rotation metadata;
   - converts to a constant frame rate, capped at 30 fps;
   - tone-maps HDR to SDR;
   - normalises loudness to -16 LUFS;
   - adds a silent track if the source has no audio.
4. Encodes the ladder (by short side, never upscaling) in one ffmpeg run:

   | Short side | Bitrate |
   |---|---|
   | 360 | 0.6 Mbps |
   | 540 | 1.2 Mbps |
   | 720 | 2.2 Mbps |
   | 1080 | 4.5 Mbps |

   Output is H.264 + AAC, HLS fMP4, 4 s segments with a 2 s first segment, and keyframes every 2 s.
5. Writes a poster JPEG.
6. Uploads segments first and playlists last, to `s/{series}/e/{ep}/v{n}/`, plus the poster to `p/posters/`.
7. Marks the episode ready and records its size, duration and renditions. If the series has no cover, the first episode's poster becomes the cover.

A failed job retries with back-off (3 attempts). A ready episode whose replacement fails keeps playing its current version.

**Publish rules** (`PublishRules`), all listed at once in admin:
- the channel is open;
- at least one episode is ready;
- the rights holder, contract ref and licence start are filled in, and the licence hasn't expired;
- territories are set;
- AI-generated and real-person are both explicitly declared;
- a consent reference is present if a real person appears.

### 9.2 Playback

```
Home → GET /api/v1/feed (10 per page; next page 3 items before the end)
Active item → GET /series/{id} (±1 neighbours prefetched) → GET …/episodes/{ep}/play
           → https://media/m/{exp}/{sig}/s/{id}/e/{ep}/v{n}/master.m3u8  (valid 4 h)
hls.js loads master → variant → init + segments with the one token (relative URIs keep the signed prefix)
Edge: HMAC over "{exp}:{prefix}", expiry, safe key, country → 200 / 403 / 451
While playing: next item's master, variant, init and first segment are fetched ahead (browser cache)
Near end (duration − 0.35 s) → next episode
Progress saved on the device every 5 s and when leaving an item/episode
Token expired (403) → fetch a fresh /play URL and resume at the same position
```

### 9.3 Accounts ✅

- **Guest mode.** Guests can browse and watch all free content. Series likes, favourites and watch history are kept on the device.
- **Sign-up.** Username of 3-20 letters, digits or `_`; password of at least 8 characters; optional nickname. The 18+ and terms confirmation is required and recorded (`terms_accepted_at`). No email or phone is collected.
- **Access tokens.**
  - A JWT (HS256, 15 minutes, signed with `Auth:JwtKey`) held only in memory.
  - The client refreshes it about a minute before it expires. After a 401 it refreshes once and retries the request.
- **Refresh tokens.**
  - A 30-day token in the httpOnly, `SameSite=Strict` cookie `rg_refresh`, scoped to `/api/v1/auth`.
  - Stored only as a SHA-256 hash and rotated on every use.
  - Replaying an already-rotated token revokes that whole sign-in, since it was probably stolen.
- **Bans.** Every authenticated request re-checks that the account is active, so disabling a member takes effect at once and also revokes their refresh tokens.
- **Sign-in and sync.**
  - On an explicit login or registration, `POST /api/v1/me/merge` adds what the device saved while signed out to the account.
  - On later app starts, the account's copy replaces the device copy, so something removed on another device doesn't come back.
  - Watch history syncs every 15 s and when the app goes to the background. The newer entry wins.
- **Sign-out.** Clears the device lists so the next person on the device doesn't inherit them.
- **Session hint.** The client keeps a non-secret `rg.signedIn.v1` flag so visitors who never signed in don't make a refresh call on every start.

### 9.4 Social ✅

- **Likes, favourites and follows.**
  - Idempotent `PUT`/`DELETE` calls that return `{ on, count }`. Each relation row and its counter change in one transaction.
  - Series likes and favourites also work signed out (on the device). Follows and post interactions open the login sheet.
  - Changes are optimistic and are undone, with a toast, if the server refuses them.
- **Comments.**
  - Per episode, or per post. Replies are one level deep, with three shown inline and "展开 N 条回复" (show N replies) for the rest.
  - Review pipeline:

    ```
    post ─► sensitive-term match? ─yes─► Rejected silently (the author still sees it as 审核中, "under review")
                    │no
                    ▼
      Moderation:AutoApproveTrustedUsers (off by default) and account > 7 days and no links? ─yes─► Approved
                    │no
                    ▼
                 Pending ─► admin 评论审核 queue (bulk approve / reject with reason) ─► Approved / Rejected
    ```

  - The author sees their own pending comments with an "审核中" badge. Everyone else sees approved comments only.
  - Counters are recounted on every status change. Authors can delete their own comments, and admins can pin top-level ones.
  - Limits: 10 comments per account per 10 minutes (`Moderation:MaxCommentsPer10Minutes`) and 30 posts per minute per IP.
- **Reports.**
  - Anyone can report, signed in or not: a series (from the detail sheet), a post, a comment or an account.
  - "Involves minors" and "real person without consent (including face swap)" reports are **urgent**: they go to the top of the queue and turn the nav badge red.
  - A takedown hides the series or post, rejects the comment, or disables the account (signing it out everywhere), and closes every open report on that target.
  - A dismissal closes only that report. Every decision is audited.

### 9.5 Discovery

**Play events:**
- The app reports `start` (first frame) and `complete` (90% watched, or the end) once per series and episode per tab session.
- Events are keyed by a random per-tab id, never by user.
- Batches are sent every 10 s, and flushed with `keepalive` when the page is hidden.

**Rankings** are recomputed every 10 minutes by the worker, or on demand from admin. All counts are of distinct sessions, so one viewer replaying doesn't inflate them.

| Ranking | Score |
|---|---|
| 热播 (hot) | 7-day plays + 3·likes + 5·favourites + 4·comments + 10·completions, ÷ √(1 + age/14 days) |
| 推荐 (recommended) | Editor pick weight × 1000 + completion rate (0-100). Picks always rank first. |
| 潜力 (rising) | Published in the last 14 days: plays over 3 days + 2 × growth versus the 3 days before, + a freshness bonus |

- The same job writes `hot_score` and `play_count` back to `series`, keeps the top 50 per ranking, and deletes events older than 30 days.
- Rankings are filtered per visitor (territory and licence dates) and renumbered when read.

**Search:**
- Matches titles, exact tags, descriptions and author names, with LIKE wildcards escaped.
- Title matches rank first, then hot score.
- The search page's suggestions show popular tags, weighted by series popularity and padded with common genres. 换一批 (refresh) shows a different deterministic sample each time.

**Listings** (`/feed` and channel pages) take a `sort`:

| Sort | Order | Used by |
|---|---|---|
| `picks` | Editor picks (`recommend_weight > 0`) by weight, padded with hot series (channels: by weight, then hot) | 精选推荐, the home hero, channel 精选推荐 |
| `updated` | Latest episode first | 最近上新, channel 最新更新 |
| `hot` | Hot score | 热播短剧, channel 当前热播 |
| `original` | Original series only, latest episode first | ReelGuo原创 (`/c/all?sort=original`) |
| `random` | Random order, no paging | API only |
| `score` / `latest` | Editorial score / newest published | API only |

### 9.6 Posts / 吃瓜

- **Writing:** admins write in Markdown with a live preview rendered by the server, so it matches exactly what gets published.
- **Sanitising:** Markdown's raw HTML is shown as text, never as markup. Only basic formatting, http(s) links (new tab, `noopener`) and images hosted on our own media host survive. The app sanitises again with DOMPurify.
- **Publishing:** needs a cover and body text. A post can link a series, which plays inline on the article and adds a "观看正片" (watch the series) button. The link is hidden for visitors outside that series' territories.

### 9.7 Age verification by country ✅

```
/api/v1/config ─► country + age_check ─► declaration: the 18+ confirmation (default)
                                      └► strict: "需要年龄验证" (age verification required) screen until a signed-in
                                         account with a verified age is used
API: in strict countries every /api/v1 content endpoint answers 403 age_verification_required, except boot
     (config), sign-in (auth/*), the account itself (/me), reports and play events.
```

- The legal team lists strict countries in **Admin → 年龄验证** (age verification), like the block list.
- Until an online verification provider is chosen, the team verifies members by hand (**会员** → **标记已验证**,
  "mark verified"). That sets `users.age_verified_at`, which a provider will later set the same way.
- Media files aren't checked per request. They are reachable only through short-lived signed links, which only
  `/play` hands out, and `/play` is covered.

### 9.8 Takedowns stop issued links ✅

- **The problem.** Play links stay valid for hours (`Media:PlaybackUrlMinutes`), so hiding a series alone would leave
  links that viewers already have still playing.
- **The fix.** Both media edges check a **revoked-series list** after the signature: series that were published and
  are now hidden, removed or past their licence end. A match returns **410 `media_revoked`**.
  - API `/media`: cached 30 s and reset at once by publish, unpublish and report takedowns.
  - CDN worker: gets the list from `/api/edge/config` with the block list, refreshed every 60 s.
- **The drill.** `scripts/takedown-drill.mjs` measures it end to end (see [RUNBOOK.md](RUNBOOK.md) §3).

### 9.9 Operations ✅

- **Real client IPs.** `Network:TrustedProxies` (CIDRs) and `Network:ClientIpHeader`. Forwarded headers are honoured
  only from those proxies, and only when some are configured: with none, ASP.NET would trust every peer. Rate limits,
  logs and cookies depend on this.
- **Cookies.** Secure outside development, because behind a TLS-terminating proxy the request itself looks like HTTP.
- **Telemetry.**
  - OpenTelemetry traces, metrics and logs, from ASP.NET Core, HttpClient, Npgsql and the runtime.
  - Business counters: `reelguo.media_jobs`, `reelguo.transcode.duration`, `reelguo.play_events`,
    `reelguo.playback_errors`, `reelguo.comments`, `reelguo.reports`.
  - Exported over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
  - JSON console logs outside development.
- **Worker heartbeat.** Every 30 s into `worker_heartbeats`. `/health/worker` is 503 when none is newer than 90 s.
- **系统状态 (system status).** One admin page: database latency and size, storage, workers, queue, failed jobs,
  rankings age, moderation queues, plays and playback errors in the last hour, revoked / blocked / strict counts,
  and prioritised alerts.
- **Playback analytics.**
  - The app measures each play: startup time, stalls (excluding stalls caused by seeking), watched time, best
    quality and fatal errors.
  - It sends one sample with the anonymous play events when the viewer leaves the episode or the app.
  - **数据概览 (data overview)** shows plays, completion rate, watch time, startup p50/p95, stall ratio and error rate,
    per day and by country, device and browser, plus the top series (UTC days, 30-day retention).
- **Security headers.**
  - Caddy and `vite preview` serve the web apps with a strict CSP from `web/security-headers.json`: own scripts only;
    media, images and fetches from `https:`; `blob:` for hls.js.
  - Also `no-referrer`, `nosniff`, no framing and a restrictive Permissions-Policy.
  - The API adds `nosniff`, `DENY` and `no-referrer` to every response.
- **Backups, load tests, drills.** `infra/backup/`, `scripts/loadtest.mjs` and `scripts/takedown-drill.mjs`, with
  procedures in [RUNBOOK.md](RUNBOOK.md).

### 9.10 Monetization ✅

**Pricing.** Each series has a coin price per episode (`episode_price`, 0 = free) and a number of free leading episodes
(`free_episodes`, default 3). An episode marked 始终免费 (always free) is free regardless. With `Billing:Enabled` off,
everything is free and the store is closed, so the platform can launch before payments are ready.

```
Watching a paid episode
  /play ─► 402 episode_locked ─► paywall over the cover: 解锁本集 (unlock) · 充值 (top up) · 开通 VIP · 登录后解锁 (sign in)
  POST …/unlock ─► wallet row locked FOR UPDATE ─► bonus coins first, then purchased ─► episode_unlocks + ledger entry
                   (one transaction; an episode already owned or covered by VIP costs nothing)

Buying coins or VIP
  充值中心 ─► POST /orders (price snapshot, random order no) ─► provider's hosted payment page
  provider ─► POST /api/payments/{provider}/webhook (signed) ─► payment_events (unique per event id)
           ─► order Paid ─► wallet credited (coins + bonus, or VIP days), all in one transaction
  payment page ─► /pay/result?order=… ─► the app polls the order ─► 支付成功 · 继续观看 (continue watching)
```

- **Money moves only on the provider's signed notice**, never because the browser came back to the result page.
- **Coins.** Purchased coins and bonus coins are kept apart. Unlocks spend bonus coins first; bonus coins never go
  negative.
- **VIP** is a pass, not a subscription: it never renews by itself. Buying again extends it. While it lasts, every paid
  episode plays without unlocking.
- **Unlocks are permanent.** A refund doesn't take back episodes already bought.
- **Refunds and chargebacks** take back what the order granted:
  - the order's bonus coins, as far as they are left, and the rest from purchased coins, which can go negative (the next
    top-up fills the gap before anything can be spent);
  - VIP days.
- **Notices are idempotent.** Each provider event id is recorded once, so a re-sent notice changes nothing.
  - A payment is credited only if amount and currency match the order exactly.
  - A late payment for an expired or cancelled order is still credited.
  - Partial refunds are rejected for manual handling.
  - Notices that fail verification are stored as Rejected (kept 30 days) and raised on 系统状态 (system status).
- **Abandoned checkouts** stay Pending; the worker marks them Expired after 24 hours.
- **Auto-unlock** (自动解锁) is the viewer's opt-in. It spends coins only when the viewer picked the episode or played on
  into it, never when they merely swipe onto a series.
- **Who can buy.** Purchases need a signed-in account; in strict-age countries a verified one (the age gate covers
  `/api/v1`). Blocked countries can't reach the store at all.

**Payment providers.** Adult content needs a specialist processor, usually a merchant of record that also handles card-network
registration for high-risk merchants, VAT/GST on digital goods, and the billing descriptor. Examples are CCBill, Segpay,
Verotel and Epoch; crypto processors are an option too. They work the same way: hosted checkout, signed
server-to-server notices, refunds. ReelGuo models that as `IPaymentProvider`:

- `CreateCheckoutAsync`: returns the payment page URL;
- `ReadNotice`: verifies and reads a notice;
- `RefundAsync`.

Connecting the chosen provider means writing one adapter class plus its settings. Everything else is provider-neutral.
The sandbox provider implements the same contract with HMAC-signed notices. It runs only in Development, or with
`Billing:AllowSandboxOutsideDevelopment` for a closed staging site, and 系统状态 raises a critical alert if it is active
outside Development.

| Setting | Meaning |
|---|---|
| `Billing:Enabled` | Paid episodes and the store on/off (off by default) |
| `Billing:Provider` | Provider for new orders (`sandbox`, or the real adapter's name) |
| `Billing:Currency` | Store currency (USD) |
| `Billing:BillingDescriptor` | What appears on the buyer's card statement; shown in the store before paying |
| `Billing:AppBaseUrl` | Public app URL, for the links the provider's page returns to |
| `Billing:SandboxSecret` | Signing key of the sandbox provider (development) |

### 9.11 Native apps ✅

The desktop and Android apps are a thin native shell around the web app, which they load from the server:

- **Same experience as the web.** Sign-in cookies, payments, media links and the content security policy are all
  same-origin, as on the web.
- **Instant web updates.** A web deploy reaches app users at once; the shell itself changes rarely.
- **Shell features.** The shell adds only what a browser tab can't do.

```
start ─► bundled start page (native/boot) ─► probe every candidate address at once:
           the server's last mirror list (cached), then the compiled-in REELGUO_ORIGINS
           a candidate counts only if /api/v1/config names the app (or answers 451: the app then shows the region screen)
       ─► first reachable one in order of preference ─► load the web app there
       ─► GET /api/v1/app/bootstrap ─► cache the mirror list for next time
                                    ─► desktop: Tauri updater ─► "发现新版本" (new version) ─► signed install ─► restart
                                    ─► Android: "发现新版本" ─► open the APK download in the browser
       none reachable ─► "无法连接服务器" (can't reach the server) + 重试 (retry)
```

| Concern | Handling |
|---|---|
| Blocked domains | `App:Mirrors` lists the current addresses. Every app caches it, so moving to a new domain only needs the new domain added (and served) before the old one is lost. |
| What remote pages may call | Exactly two commands: `set_fullscreen` and `open_external`, granted at runtime to the origin that was loaded. The start page may call only `boot_status` and `retry`. |
| Navigation | https anywhere (the web app and hosted payment pages), plain http only for local development, the bundled start page. `mailto:`/`tel:` open in the system; other schemes are refused. |
| New tabs | Links with `target=_blank` and `window.open` open in the system browser (legal pages, for example). |
| Fullscreen | Element fullscreen in the web app makes the window fullscreen (desktop). On Android, wry handles fullscreen video. |
| Knowing it's the app | The shell appends `ReelGuoApp/<version> (<desktop\|android>)` to `navigator.userAgent`. In the app, 设置 (settings) shows the app version instead of the 下载 App (download app) link. |
| Autoplay | wry starts WebView2 with `--autoplay-policy=no-user-gesture-required`; on Android it turns off the media gesture requirement. |
| Updates | Desktop: the Tauri updater with its own signing key (`.tools/signing/reelguo-updater.key`; the public key is in `tauri.conf.json`). It installs only bundles signed with that key, served over https. Android: a prompt that opens the newer APK; "mandatory" releases close the app until it is updated. |
| Single instance | A second launch focuses the running window; window size and position are remembered. |
| HTTPS | rustls on the ring backend with Mozilla's root certificates (webpki-roots), the same on every platform, and no Android JNI set-up needed. |

**Verified on Windows (2026-11-11), with the installed app:**
- **Connection:** it connected through the cached mirror list and refreshed that list from the server.
- **App detection:** 设置 showed "桌面版 0.6.0" (desktop 0.6.0), so the web app knew it was inside the app.
- **Fullscreen:** the player's fullscreen button took the window to 1920×1080 and back.
- **Updates:** publishing 0.6.1 in Admin → App 版本 (app versions) produced the update prompt, and accepting it installed
  the signed 0.6.1 and restarted the app within a second.

**Not yet verified:** the Android APK has not been built or run on a device yet (this machine lacks the disk space for
the Android SDK); GitHub Actions builds it.

### 9.12 Portal content ✅

The home page and footer carry content the team edits without a release:

- **专题 (topics):** hand-picked series lists (Admin → 专题). Only published topics are listed; series that aren't
  visible to the visitor (unpublished, out of territory) are left out of a topic and of its count.
- **公告 (announcements):** the scrolling line under the hero (Admin → 公告). All active entries show, in sort order; with
  none active the bar is hidden.
- **Ratings:** signed-in viewers rate a series 1–10 (5 stars). The page shows the viewers' average once 5 people have
  rated, and the editorial score before that.
- **Site info:** `Site:Tagline`, `Site:Telegram`, `Site:Email`, `Site:PermanentDomain` (configuration), plus the
  `App:Mirrors` list, feed the footer's 官方入口, the 回家的路 page, the series page's 最新地址 and the daily
  地址收藏提醒 dialog. Empty values are simply not shown.

### 9.13 Search engines ✅

The web app is built in the browser, so on its own a search engine sees an empty page. The API therefore answers every
page path with the built `index.html` plus that page's content (`backend/src/ReelGuo.Api/Seo/`):

- **Routing.** Caddy serves files as they are and sends page paths (and `/`) to the API. The API reads `index.html`
  from the web container (`Seo:TemplateUrl`) or from disk (`Seo:TemplatePath`), keeps it for `Seo:CacheSeconds`
  and keeps the last good copy if a refresh fails. Without a template configured, page paths are not answered
  (development uses Vite).
- **What a page gets.** `<title>`, description, canonical link, robots, Open Graph, the RTA adult label and JSON-LD
  (WebSite, TVSeries with its episodes, Article, BreadcrumbList), plus the page's readable content in `<div id="seo">`
  next to the app's `#root`: navigation, breadcrumb, headings, text and links (series, episodes, channels, topics,
  rankings, posts, addresses). Text from the catalogue is HTML-escaped; post bodies are the server-sanitised HTML.
- **Pages.** `/`, `/c/{slug}` (`?sort=original`, `?page=`), `/video/{id}` and one URL per episode
  `/video/{id}/ep-{n}` (old `?ep=` links canonicalise to it), `/ranks/{kind}`, `/topics`, `/topics/{id}`, `/posts`
  (`?page=`), `/post/{id}`, `/go-home`, `/download`, the legal pages. Account, store, payment, settings and search
  pages are never indexed. Unknown paths get the app's not-found page with a 404; unknown series, episodes, topics
  and posts too.
- **Same rules as the API.** Licence territories and dates per visitor; blocked countries get the app with no content
  (451); strict age-verification countries get no content. Rendered pages are reused for `Seo:CacheSeconds` per
  country and URL.
- **The 18+ gate.** A same-origin `/boot.js` (the CSP allows no inline scripts) marks the page as running
  JavaScript before it paints; then `#seo` is hidden, except beneath the 18+ gate, which covers it, as search engines
  expect of an age interstitial. Visitors who confirmed never see it; visitors without JavaScript see the plain page
  with the 18+ notice.
- **Indexing switch.** `Seo:AllowIndexing` is off by default: robots.txt disallows everything, every page says
  `noindex` and responses carry `X-Robots-Tag: noindex`. Production turns it on; staging and test sites never do.
- **robots.txt and sitemaps.** `/robots.txt`; `/sitemap.xml` (index) → `/sitemaps/pages.xml`, `/sitemaps/posts.xml`
  and `/sitemaps/series-{n}.xml` (400 series with their episode URLs per file). The sitemap lists series licensed
  in `Seo:SitemapCountry` (default US). Absolute URLs use `Seo:PublicBaseUrl`.

## 10. Web / PWA specifics

| Topic | Handling |
|---|---|
| Hosting | One origin: static app files plus `/api/*` and `/media/*` routed to ASP.NET (Caddy in `infra/`). With a CDN, media moves to its own host (`Media:PublicBaseUrl`). |
| PWA | Manifest with PNG icons (192, 512, maskable, Apple touch icon) and a service worker. The service worker caches only the app files and never caches `/api` or `/media`. |
| HLS | hls.js where MediaSource or ManagedMediaSource exists; the browser's native HLS otherwise. Fatal network and media errors get up to 4 recoveries. |
| Autoplay | Starts muted and inline; unmuting is remembered. |
| Fullscreen | Mobile uses a fixed full-viewport stage. Landscape video tries the device's orientation lock and otherwise rotates the stage with CSS. Desktop uses element fullscreen and picture-in-picture. |
| Routing | Every page has a real URL (History API, `src/router.ts`); back and forward move between pages. Sheets, fullscreen and dialogs take their own history entry and close first on back. |
| iOS edge swipe | Scrubbing never starts within 24 px of the screen edge. |
| Shareable links | Every route is shareable: `/video/{id}/ep-{n}` (old `?ep=` links still work), `/c/{slug}?sort=`, `/topics/{id}`, `/ranks/{kind}`, `/post/{id}`, `/search?q=`, `/go-home`, … (full list in UI-REFERENCE §2). |
| Settings | 启动时静音 (mute on start; on by default, as in the reference), clear caches and on-device records, version, legal links. The site is dark only, like the reference. |
| Age gate | Asked on the first visit and remembered on the device. Nothing is visible behind it: the server-rendered page text (§9.13) sits beneath it, covered, for search engines. |
| App stores | Apple and Google don't accept adult apps. Distribution is the web/PWA plus the 下载 App page (`/download`): the APK, the desktop installers, and how to add the site to the iPhone home screen. |
| Wake lock | The screen stays on while a video plays (Screen Wake Lock API, taken again when the page comes back). |

## 11. Compliance checklist

- [ ] Legal review per target market; the block list is set accordingly (§8).
- [x] 18+ confirmation on first visit.
- [x] Per-country strict age verification, enforced by the API, with manual verification of members (§9.7).
- [ ] An online age-verification provider for strict countries (vendor decision pending).
- [x] Rights and consent data required before publishing; licence territories and dates enforced on every public request.
- [x] No content can be created, imported or published in closed channels (AI换脸).
- [ ] Moderation rules:
  - every character is clearly an adult, including AI-generated and 漫剧 (comic drama) characters;
  - no real person's likeness without consent;
  - records on file for real performers.
- [ ] Hash-match scan of every incoming file for known child-abuse material, with a reporting route. The worker step that will run it is in place but isn't wired to a scanning service yet.
- [x] Report button on series, posts and comments (signed-out visitors too), and a takedown queue with urgent reports first (§9.4).
- [x] Comment review before publication, with a sensitive-term filter.
- [x] Append-only audit log of admin actions, under named admin accounts.
- [x] Takedowns stop links already handed out, with a repeatable drill (§9.8, RUNBOOK §3).
- [ ] Backups restored successfully in a restore test on the real deployment (RUNBOOK §5).
- [ ] Terms, privacy and content policy written by legal (placeholders at `/terms`, `/privacy`, `/content-policy`).
- [ ] Written confirmation from the host, CDN, storage and payment providers that adult content is allowed.
- [ ] Windows installer signed with a code-signing certificate (otherwise SmartScreen warns on first run); macOS build
  notarised if macOS is offered.
- [ ] The updater signing key and the Android keystore stored in a password manager and as CI secrets (losing either
  means existing installs can't be updated).
- [ ] A payment provider for adult content under contract (ideally a merchant of record: card-network registration,
  VAT/GST, chargeback handling), and its `IPaymentProvider` adapter written (§9.10).
- [ ] 充值协议 (recharge terms) and refund policy written by legal (placeholder at `/recharge-terms`).
- [x] The billing descriptor is shown in the store before paying; prices include the currency.
- [x] Purchases need a signed-in account (a verified one in strict-age countries); VIP never renews by itself.
- [x] Chargeback rate monitored on 系统状态 (warning from 0.5 % over 30 days).

## 12. Roadmap

| Phase | Scope | Status |
|---|---|---|
| **0 Foundations** | Solution and monorepo, OpenAPI → TS pipeline, CI, compose, country blocking with admin UI and audit log, age gate, region-blocked screen, legal placeholders, app shell | ✅ Done |
| **1 Watchable web MVP** | Admin accounts; storage (filesystem/S3); upload, bulk import, rights data and publish rules; worker with ffmpeg ladder; signed playback and media edge (API + CDN worker) with country blocking; licensed territories; Home feed with full player, gestures, fullscreen, episodes and detail sheets, desktop player and shortcuts; PWA icons; demo seed | ✅ Done |
| **2 Discovery** | Play events → rankings (worker, 10 min); 热榜 page; search (history, popular tags, recommendations, results); 吃瓜 posts (admin Markdown editor, posts row/list/article with inline playback); settings and light theme; all shareable links; admin rankings page and editor picks | ✅ Done |
| **3 Accounts & social** | Viewer accounts (JWT + rotating refresh cookie), merge and sync of likes / favourites / history, follows, post likes and favourites, comments with replies and review (term filter, admin queue, pinning), reports with takedowns, member bans, admin 评论审核 / 举报 / 会员 pages with badges | ✅ Done |
| **4 Web launch hardening** | Real client IPs behind proxies, secure cookies, takedowns that revoke issued links (API + CDN edge) and a takedown drill, per-country age verification (admin rules, API enforcement, manual member verification), OpenTelemetry, worker heartbeat, 系统状态 page, playback-quality analytics and 数据概览 page, backup and restore-test scripts, load-test script, security headers, ops runbook | ✅ Done (restore test still to run on real infrastructure) |
| **5 Monetization** | Per-series pricing (free leading episodes, coins per episode), coin packs and VIP passes, wallet with an append-only ledger, race-safe unlocks, paywall in the player, 充值中心 (store), payment result and wallet pages, auto-unlock, provider abstraction with signed and idempotent notices, refunds and chargebacks, sandbox provider, admin 商品 / 订单与营收 / member wallets / series pricing, payment health on 系统状态 | ✅ Done (real payment provider still to be chosen and connected) |
| **6 Native apps** | Tauri 2 shell for Windows / macOS / Linux / Android around the web app: bundled start page, mirror fallback with a cached list, narrow command surface, navigation policy, window fullscreen, external links, signed desktop auto-update, Android update prompt (mandatory releases), user-agent marker; API release service (bootstrap, updater feed, download list) and admin App 版本 page; 下载 App page; screen wake lock; GitHub Actions builds | ✅ Done (Windows verified end to end; Android built in CI, still to be tried on devices) |
| **Portal layout** | Web app rebuilt to the reference's live site layout, re-matched after the reference's redesign of 2026-11-12 by measuring both sites element by element: real URL routing; frosted desktop header over the page, fixed phone header; curved hero with a thumbnail strip, 公告, hot tags, 7-column card sections, topics and the 官方入口 block; paged channel lists with pill tabs and FAQ; desktop series page (title bar, player, joined action bar, 选集/评论 side panel, info card with ratings, synopsis, 猜你喜欢 / 最新地址) and the full-screen phone player; rankings, topics, 博客, 回家的路, footer and floating dock; yellow accent and Arial font; topics, announcements, ratings and site info in the API; admin 专题 and 公告 pages | ✅ Done |

## 13. Engineering notes

- **Own job queue instead of Hangfire.** There is a single job type, and progress and errors live on the episode rows the admin console already reads. A 60-line queue on Postgres (`FOR UPDATE SKIP LOCKED`, back-off, stale-lock reclaim) is simpler than adding Hangfire. Revisit if many recurring job types appear (rankings in Phase 2 can use the same pattern or a timer).
- **ffmpeg runs in the output folder with relative paths.** Otherwise Windows builds write backslashes into `master.m3u8`, and init segments land outside the variant folders their playlists reference. The end-to-end test checks both.
- **hls.js 1.7:** call `startLoad()` only after `MANIFEST_PARSED`. Calling it earlier puts the stream controllers into an error state and the player never loads a level.
- **Every proxy must route `/media`.** That covers Vite, Caddy and the service worker deny list. Otherwise playlist requests get `index.html` back, and hls.js reports "no EXTM3U delimiter".
- **Query enums use their JSON spelling.** Bind them as strings and parse case-insensitively: ASP.NET binds enum query values case-sensitively, so `?status=published` failed. Malformed requests return 400 `bad_request`, not 500.
- **The worker waits 20 s before its first rankings run,** so a fresh deployment's API has applied migrations first.
- **The database must be UTF-8**, because the seed data is Chinese.
- **Testing Chinese input with curl on Windows:** send request bodies from a UTF-8 file, because inline arguments go through the ANSI code page.
- **Local object storage:** filesystem storage covers development and tests. Production needs S3-compatible storage (R2 recommended). Browser uploads to it use presigned PUTs, so the bucket's CORS must allow `PUT` from the admin origin.
- **TypeScript is pinned to 5.9**, because `openapi-typescript` does not support TypeScript 7 yet.
- **Retrying after a 401 needs a copy of the request.** The client's auth middleware clones each authenticated request (keyed by openapi-fetch's request id) before it is sent, because a sent body can't be read again.
- **ForwardedHeadersOptions with empty lists trusts everyone.** Clearing `KnownProxies`/`KnownIPNetworks` (and the obsolete `KnownNetworks`, which also holds the loopback default) makes the middleware accept forwarded headers from any peer. So with no trusted proxies configured, ReelGuo doesn't add the middleware at all.
- **Raw SQL rows map by snake_case names.** `SqlQuery<T>` with an unmapped type goes through the naming convention too, so column aliases must be snake_case (`AS plays`, not `AS "Plays"`).
- **The scratch Postgres refuses to start from an elevated shell.** `postgres.exe` won't run as administrator; start it through `pg_ctl`, which drops the rights itself. Initialise it as UTF-8 (`initdb -E UTF8`).
- **WebView2 ignores `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` when the app passes its own arguments** (wry always does), so the desktop app can't be driven over DevTools by an environment variable. Smoke tests use Windows UI Automation instead (Chromium builds the accessibility tree after the first query). The update dialog's buttons aren't invokable through UI Automation; `TDM_CLICK_BUTTON` with rfd's id (1000 for the first custom button) presses one.
- **The Tauri updater refuses plain http in release builds.** Local update tests pass `--config '{"plugins":{"updater":{"dangerousInsecureTransportProtocol":true}}}'` on the command line; the committed config never sets it.
- **reqwest 0.13's default TLS (`rustls`) pulls in aws-lc** (CMake and NASM on Windows) and its platform verifier needs JNI initialisation on Android. The shell uses `rustls-no-provider` with a preconfigured ring + webpki-roots client instead.
- **The app version has one source:** `version` in `tauri.conf.json`, which the installer, the updater and the shell (via `package_info`) all read; keep `Cargo.toml` and `native/package.json` in step.
- **Wallet rows are locked, never compared-and-swapped.** Every wallet change runs in a transaction that first takes `SELECT … FOR UPDATE` on the wallet (creating it with `INSERT … ON CONFLICT DO NOTHING`). Payment notices lock the order row before the wallet, and nothing locks them the other way round, so they can't deadlock. `FromSql … FOR UPDATE` is materialised as is (`ToListAsync`, no LINQ on top), and a tracked copy of the row is detached first, or EF would hand back the stale tracked values.
- **snake_case JSON doesn't split before digits.** `RejectedNotices24h` is serialised as `rejected_notices24h` (like Phase 4's `jobs_failed24h`).
- **Refresh-token tests replay old cookies.** They use `WebApplicationFactoryClientOptions { HandleCookies = false }` and set the `Cookie` header by hand.
