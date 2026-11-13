# ReelGuo operations runbook

For whoever is on call, and for the legal and moderation leads. It covers what to check, what each alert means, how
to take content down, how payments and refunds are handled, how app versions are released, and how backups are restored. Admin console pages are named as they appear (Chinese) with a
translation. The system design is in [ARCHITECTURE.md](ARCHITECTURE.md).
Setting up and updating the staging server is in [STAGING.md](STAGING.md).

## 1. Every day

1. Open **系统状态** (system status). It should say **一切正常** (all normal). Each alert is explained in §2.
2. Open **举报** (reports). Handle anything marked **紧急** (urgent) first. Urgent means a report of minors, or of a
   real person shown without consent; it turns the nav badge red.
3. Clear **评论审核** (comment review). Every comment waits for review before other viewers see it.

## 2. Alerts on 系统状态 and what to do

| Alert | Meaning | Action |
|---|---|---|
| 没有在线的转码 Worker | No media worker has reported in 90 s. New uploads won't be encoded; playback of existing episodes is unaffected. | Restart the `worker` service and check its logs. `/health/worker` returns 503 while this lasts. |
| 有 N 条紧急举报待处理 | Urgent reports are open. | Review them now (§3). |
| 转码队列积压 | The oldest queued encode has waited more than 10 minutes. | Check that the worker is alive and not stuck on one job (Worker table: 当前任务, current job). Add workers if uploads are heavy. |
| 最近 24 小时有 N 个转码任务失败 | Encodes failed after their retries. | 内容管理 (content) → the series → the failed episode shows the reason. Fix the source file and press retry. |
| 榜单超过 30 分钟未更新 | Rankings aren't being recomputed. | The worker computes them every 10 minutes; restart it. |
| 最近 1 小时播放失败偏多 | More than 5% of plays failed in the last hour. | Look at **数据概览** (data overview) → by country / browser to see where. Check the CDN and storage status pages. |
| 待审核评论积压 | More than 200 comments are waiting. | Add reviewers, or temporarily turn on `Moderation__AutoApproveTrustedUsers`. |
| 未配置监控数据导出 | No monitoring backend is configured, so errors can't be traced. | Set the OpenTelemetry variables (§6). |
| 数据库无法连接 / 媒体存储无法访问 | The database or media storage can't be reached. | Major incident: check the provider's status page and the API logs. |
| 支付处于模拟模式 | The sandbox payment provider is active outside development: anyone can "pay" without paying. | Switch `Billing__Provider` to the real provider at once (§8). |
| 付费已开启，但支付渠道不可用 | Paid episodes are locked but nothing can be bought. | Fix the provider settings, or turn `Billing__Enabled` off until it works (everything becomes free). |
| 支付通知未通过校验或与订单不符 | Provider notices were rejected: a bad signature, an unknown order, or an amount that doesn't match. | 订单与营收 (orders and revenue): look up the order. A wrong signing secret after a key rotation is the usual cause; a real mismatch needs the provider's support. |
| 近 30 天拒付率 … | Chargebacks are approaching the level at which card networks put merchants in monitoring programmes. | See §8, chargebacks. |

## 3. Takedowns

A takedown makes content unavailable everywhere, **including through play links viewers already have**. Those
links normally stay valid for hours, so the media servers check a revoked-series list:

| Where | When it stops working |
|---|---|
| The app (feed, search, the series page, new play links) | Immediately |
| Media through the API (`/media/…`) | Immediately on the instance that handled the takedown; within 30 s on the others |
| Media through the CDN edge (production) | Within about 60 s (the edge refreshes its list every minute) |
| Episodes already downloaded into a viewer's browser cache | Can't be reached; the next segment the player asks for fails |

### How to take something down

- **A reported item:** **举报** → the report → **下架剧集** / **下架文章** / **删除评论** / **封禁会员** (take down the series /
  post, delete the comment, ban the member). This closes every open report on that item. Add a note: it goes into
  the audit log.
- **No report (a legal request, a rights holder, our own finding):** **内容管理** → the series → **下架** (hide).
- **A member:** **会员** (members) → **封禁** (ban). They are signed out on every device at once.

Hidden series stay in the database, and can be published again once cleared. Every action is in **操作日志** (audit log).

**Child sexual abuse material:** take it down at once. Do not download, copy or forward it. Preserve the audit-log
entries and escalate to the legal lead for the mandatory reporting route in the relevant country.

### Takedown drill (monthly, and after any change to media delivery)

Use a test series, never a real one, in production:

```bash
node scripts/takedown-drill.mjs --app https://app.example.com --admin https://admin.example.com \
  --user <your admin account> --password <…> --series <test series id> --restore
```

It requests a play link the way a viewer would, takes the series down, and measures how long until the series page,
new play links, the issued playlist and a media segment all stop working, then restores the series. Paste the
output into the takedown log. **PASS** means everything went dark; through the CDN, expect the media lines at about
60 s. Measured in development on 2026-11-11: all four checks went dark in under 0.1 s.

## 4. Country rules (for the legal team)

Both lists take effect immediately (within 60 s on other servers), and every change is in the audit log with who
made it.

- **地区屏蔽 (country blocking):** visitors from listed countries see "当前地区暂不提供服务" (not available in your
  region) and get no content or media. `CN` is listed by default.
- **年龄验证 (age verification):** countries listed here are *strict*. Content opens only for a signed-in account whose
  age has been verified; everyone else sees "需要年龄验证" (age verification required). Countries not listed use
  the 18+ confirmation on first visit.
- **Verifying a member by hand (until an online provider is connected):** after checking their ID through your
  agreed process, **会员** → the member → **标记已验证** (mark verified), noting how it was checked. **撤销验证**
  (withdraw verification) removes it.

Online verification needs a provider decision (for example Yoti or Veriff). When one is chosen, its result will set
the same verified flag.

## 5. Backups and restore

| What | How | Where |
|---|---|---|
| Database | Daily `pg_dump` by the `backup` service (`infra/backup/backup.sh`), checksummed, kept 14 days locally | `backups` volume, plus off-site when `REELGUO_BACKUP_REMOTE` is set |
| Source videos (raw bucket) | Copy to a second bucket or provider (e.g. a nightly `rclone sync`), with versioning or object lock if available | Off-site |
| Encoded media (media bucket) | Not backed up: every episode can be encoded again from its source | — |

**Targets (proposed; confirm with the business):** lose at most 24 hours of data (one nightly backup), and be
restored within 4 hours.

**Off-site copy:**

1. Create a bucket that the app's own keys cannot write to.
2. Put the rclone settings (`RCLONE_CONFIG_<NAME>_*`, see the header of `backup.sh`) in `infra/backup.env`. That file
   is git-ignored.
3. Set `REELGUO_BACKUP_REMOTE`, for example `r2:reelguo-backups/db`.
4. Add a lifecycle rule on the bucket, for example delete after 35 days.

**Alerting:** set `REELGUO_BACKUP_PING_URL` to a dead man's switch (for example a healthchecks.io check that expects
a ping every 24 h). The backup pings it only after a successful run, so silence means backups stopped.

**Restore test (monthly):** restores the newest backup into a scratch database, checks it, compares row counts with
the live database and reports the time it took. The live database is only read.

```bash
docker compose -f infra/docker-compose.yml --profile app run --rm backup "sh /scripts/restore-test.sh"
```

> The backup and restore scripts are syntax-checked, but haven't been run end to end yet: the development machine
> has no Docker or `pg_dump`. **Run the restore test on the first real deployment**, and record the result below.

**Real restore (after data loss):**

1. Stop `api` and `worker`.
2. `pg_restore --clean --if-exists --no-owner -d reelguo <dump>` from the `backup` container.
3. Start `api` (it applies any newer migrations).
4. Start `worker`.
5. Run the takedown drill on a test series, as a smoke test.
6. Re-hide anything taken down after the backup was made: check the audit log after the backup's timestamp.

## 6. Monitoring setup

- **Uptime checks** (any external monitor), every minute:
  - `/health/live` (the process is up);
  - `/health/ready` (the database is reachable);
  - `/health/worker` (at least one media worker is alive).
- **Traces, metrics and logs:** set `OTEL_EXPORTER_OTLP_ENDPOINT` (and `OTEL_EXPORTER_OTLP_HEADERS` for the vendor's key)
  on `api` and `worker`. Any OTLP backend works.
- **Metrics worth alerting on:**
  - `reelguo.media_jobs{outcome="failed"}`;
  - `reelguo.playback_errors`;
  - `reelguo.reports{urgent="true"}`;
  - plus HTTP 5xx rate and p95 latency.
- **Support:** a 500 error carries a `trace_id`. Ask the user for it, then search the logs and traces for it.
- **Logs:** outside development, logs are one JSON object per line.

## 7. Load testing

```bash
node scripts/loadtest.mjs --app https://staging.example.com --vus 100 --duration 300
```

The script simulates viewers browsing the feed, opening a series, getting a play link and fetching media. It fails
if API p95 latency exceeds 300 ms, media p95 exceeds 800 ms, or more than 1% of requests fail.

- **Run it against staging only.**
- **Run it before launch, and after major changes.**
- **The 429s are expected.** From one machine every request shares an IP, so the per-IP rate limits answer them.

Baseline (development machine, with the API, Postgres and the load generator all on one PC, so a lower bound):

| Viewers | Throughput | p95 | Failures |
|---|---|---|---|
| 40 (≤300 ms pauses) | ≈650 req/s | 8 ms | 0 |
| 150 (no pauses) | ≈1,700 req/s | 116 ms | 0 |

## 8. Payments

The admin pages are **订单与营收** (orders and revenue), **商品** (products) and, per member, **会员 → 钱包** (members → wallet).

**How money flows.** The viewer buys a coin pack or a VIP pass on the provider's payment page. The provider then sends a
signed notice to `/api/payments/{provider}/webhook`, and only that notice credits the wallet. The app's result page
just waits for it. Every notice is kept, and re-sent notices change nothing.

**Prices.** **商品** sets what the store sells; changes apply to new orders only. Each series' price is set in
**内容管理 → 付费设置** (content → pricing): coins per episode, and how many episodes are free at the start.

**"I paid but got nothing."**
1. Ask for the 订单号 (order number). It is shown on the payment result page and in the app's 消费记录 (records).
2. Search for it in **订单与营收**.
3. If the order is 待支付 (pending), check the provider's dashboard. If the provider shows it paid, ask the provider to
   **re-send the notice**; it will credit the wallet once.
4. Don't also credit the coins by hand: a late notice would credit them a second time.

**Refunds.** **订单与营收** → the order → **退款** (refund), with a reason.
- The money goes back through the provider.
- The order's coins (or VIP days) are taken back; coins already spent leave the balance negative.
- Episodes already unlocked stay unlocked.
- It's recorded in the audit log.

**Chargebacks** arrive from the provider automatically: the order becomes 已拒付 (charged back) and what it granted is
taken back.
- Keep the rate below about 0.5 %. Card networks start monitoring merchants at about 0.9 %, and the status page warns
  from 0.5 %.
- For a dispute, the evidence is:
  - the order (time, country, product);
  - the member's 钱包 ledger, showing what the coins were spent on;
  - the audit log.
- Repeat offenders can be banned in **会员**.

**Goodwill credits and corrections.** **会员 → 钱包 → 客服调整** (members → wallet → support adjustment). It needs a reason;
the member sees it as 客服调整 in their records, and it is in the audit log.

**Turning payments on in production** (after the provider is contracted and its adapter is written):

| Setting | Value |
|---|---|
| `Billing__Enabled` | `true` |
| `Billing__Provider` | The provider's adapter name |
| `Billing__BillingDescriptor` | The descriptor the provider assigned (shown to buyers before paying) |
| `Billing__AppBaseUrl` | The app's public URL (e.g. `https://app.example.com`) |

Plus the provider's own keys, then a test purchase and a refund on a real card. Never use `sandbox` outside
development.

## 9. App releases

The desktop and Android apps load the web app from the server, so most changes reach app users with a normal web
deploy. A new app build is needed only when `native/` changes.

**Keys (keep both in a password manager and as GitHub secrets; losing one means existing installs can't update):**

| Key | Used for | Where it lives |
|---|---|---|
| Updater signing key | Desktop apps install only updates signed with it | Generated in development as `.tools/signing/reelguo-updater.key`; public key in `native/src-tauri/tauri.conf.json`. For production, generate a new key with a password (`pnpm --filter @reelguo/native tauri signer generate -w <file>`) and put its public key in the config |
| Android keystore | Android accepts an update only from the same key | Create once (`keytool -genkey -v -keystore release.jks -keyalg RSA -keysize 2048 -validity 10000 -alias reelguo`), never replace |

**Building.** Run the **Native apps** workflow in GitHub Actions (or push a tag `app-v0.6.0`). It needs the
`REELGUO_ORIGINS` repository variable (for example `https://app.example.com,https://app2.example.net`) and the secrets
listed at the top of `.github/workflows/native.yml`. Bump `version` in `native/src-tauri/tauri.conf.json`,
`Cargo.toml` and `native/package.json` first.

**Publishing:**
1. Upload the files to the download host over https. Use a host outside the main domain, so a blocked domain doesn't
   take the downloads with it.
2. In **App 版本** (app versions), add one release per platform: version, download URL and SHA-256.
   - **Windows / macOS / Linux:** also paste the `.sig` file's content into 更新签名 (update signature). macOS uses
     the `.app.tar.gz`.
   - **Android:** the signed APK.
3. Tick 发布 (publish). Desktop apps update on their next start; Android apps show a download prompt; the 下载 App
   page shows the new version.
4. Use 强制更新 (mandatory) only when old versions must stop working: older apps then can't be used until they update.

**Mirrors.** `App__Mirrors__0`, `App__Mirrors__1`, … list the addresses the apps may use, preferred first. Apps keep
the latest list, so add a new domain (with TLS and the same app) **before** the current one is blocked.

The same list is shown to web visitors: the footer, the 回家的路 page (`/go-home`), the series page's 最新地址 and the
daily 地址收藏提醒 dialog. Set `Site__PermanentDomain`, `Site__Telegram` and `Site__Email` to show those too (empty
values are hidden). Home-page 公告 (announcements) and 专题 (topics) are edited in admin and need no release.

## 9a. Search engines

Page paths are answered by the API with the page's content for search engines (docs/ARCHITECTURE.md §9.13).

| Setting | Staging | Production |
|---|---|---|
| `Seo__TemplateUrl` | `http://web:8080/index.html` (set in compose) | same |
| `Seo__AllowIndexing` (`REELGUO_ALLOW_INDEXING`) | **false** (the default): robots.txt disallows everything, pages say noindex | `true` |
| `Seo__PublicBaseUrl` (`REELGUO_APP_URL`) | the staging address | the main domain, e.g. `https://reelguo.com` |

Checks after a deploy: `curl -s https://<host>/robots.txt` (staging must say `Disallow: /`), and
`curl -s https://<host>/video/<id> | grep -E "<title>|canonical|noindex"`. In production, submit
`https://<domain>/sitemap.xml` in the search engines' webmaster tools. When the main domain changes, update
`REELGUO_APP_URL` so canonical links and the sitemap follow.

## 10. Before launch

- [ ] Secrets set and stored safely:
  - `REELGUO_MEDIA_KEY`, `REELGUO_JWT_KEY`, `REELGUO_ADMIN_PASSWORD`;
  - `Edge__Secret`, the same value as the edge worker's `EDGE_SECRET`;
  - remove the bootstrap admin password after the first sign-in.
- [ ] Behind Cloudflare:
  - `Geo__TrustCountryHeader=true`;
  - `Network__ClientIpHeader=CF-Connecting-IP`;
  - `Network__TrustedProxies` set to the proxy network (the compose file sets the Docker ranges);
  - the origin reachable only through the CDN.
- [ ] The admin console behind an access gate (for example Cloudflare Access or an IP allowlist).
- [ ] Legal review done, with **地区屏蔽** and **年龄验证** set accordingly.
- [ ] Monitoring (§6), backups with off-site copy and ping (§5), and a first restore test, all recorded.
- [ ] A takedown drill through the CDN, recorded.
- [ ] Payments (if selling at launch): provider contracted and connected, descriptor set, 商品 prices reviewed, series
  prices set, 充值协议 (recharge terms) published, and a real test purchase and refund done (§8).
- [ ] A load test against staging at the expected peak, recorded.
- [ ] Search engines (§9a): `REELGUO_ALLOW_INDEXING=true` and `REELGUO_APP_URL` set in production only; robots.txt and a series page checked; sitemap submitted.
- [ ] Apps (if offered at launch): production updater key and Android keystore created and stored, `REELGUO_ORIGINS`
  and `App__Mirrors__*` set, the Windows installer code-signed, builds tested on real devices, releases published (§9).

## 11. Logs

### Takedown drills

| Date | Environment | Result | Media dark after | By |
|---|---|---|---|---|
| 2026-11-11 | development | PASS | < 0.1 s (API edge) | setup |

### Restore tests

| Date | Backup | Restore time | Result | By |
|---|---|---|---|---|
| | | | | |
