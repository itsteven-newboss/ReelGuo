# UI reference

Screen-by-screen spec for the ReelGuo web app. Build every screen to match it.

## Where the spec comes from

| Part | Source |
|---|---|
| Page layout: the site frame (header, footer, floating dock), the home page, channel, series, ranking, topic, 博客, 回家的路, account and dialog layouts | The reference product's live web app (huangguoai.com), measured element by element on 2026-11-12 after its redesign, at 390 × 844 (phone), 1280, 1440 and 1920 px wide (desktop). Only layout and behaviour were taken: no media, text or code. The measuring script (`measure.mjs`) runs the same probe on the reference and on ReelGuo, so the two can be compared number by number when the reference changes again. |
| Player behaviour: gestures, thresholds, fullscreen, controls, progress saving, the episode and comment sheets | HGhub, the reference's open-source client, at commit `f01f494`. References such as `Feed.tsx:1045` point into its `src/`. Read them for behaviour only; never copy code from them (see [ARCHITECTURE.md §2](ARCHITECTURE.md#2-boundaries)). |

Rules:
- **Copy exactly:** layout, sizes, spacing, interaction thresholds and timings, states, and generic Chinese labels.
- **Keep ReelGuo's own:** name, logo, icons (lucide), images, slogans and copy. Strings that carry the reference's brand (黄果…, HGhub) are replaced. The accent is the reference's yellow, by the product owner's decision (2026-11-12).
- **Leave out:** the reference's ad slots, gambling and partner links, and anything else in §16.

Code lives in `web/apps/client/src/portal/` unless another path is given.

---

## 1. Global

| Element | Spec | Code |
|---|---|---|
| Theme | Dark only. Page `#0b0b0d`; accent `#ffd321` (badges and buttons `#ffd21c`, scores `#ffd900`); the gold gradient `#ffd21c → #f5a623` on primary actions. Text white at 90 / 65 / 60 / 50 / 35 %. | `tokens.css`, `index.css` |
| Font | `Arial, Helvetica`, then PingFang SC / Microsoft YaHei / Noto Sans SC for Chinese | `tokens.css` |
| Root size | 14 px, 16 px from 1680 px wide. Headings and hero text are in rem, so they grow on wide screens; the header does not. | `index.css` |
| Content column | Phones: 100 % − 26 px (13 px gutters). Desktop (≥ 1024 px): `min(100% − 192px, 1440px)`, so 1238 px at 1440 and 1440 px at 1920. | `.rg-wrap` |
| Page scrollbar | 10 px, dark | `index.css` |
| Breadcrumb | "首页 / 频道 / 剧名", 14 px (12 px phones), white 60 %, the current page in the accent colour | `kit.tsx: Breadcrumb` |
| Section title | Plain semibold, 1.625 rem (16 px phones), no bar; "more" pill on the right: 87 × 36 (99 × 32 phones), white 10 % fill and border, 12 px text with › | `kit.tsx: SectionTitle, MorePill` |
| Pill tabs | 72 × 36, radius 7.5; active filled with the accent and black text, others white 10 % | `kit.tsx: PillTabs` |
| Underline tabs | 37 px high, 1 px rule under the row; active text and underline in the accent | `kit.tsx: UnderlineTabs` |
| Pagination | ‹ 1 2 3 4 … 39 ›: 28 × 28 cells, radius 4.5, the current page accent-filled; then "跳转至" (accent button) · 57 px input · "页" | `kit.tsx: Pager` |
| FAQ | "FAQ" 1.75 rem; rows white 5 % with a white 10 % border, radius 10.5; question 14/500 with a chevron; answer under a 1 px rule, white 50 % | `kit.tsx: Faq` |
| Dialog | Backdrop `black/70` without blur; `#141414` panel, 1 px white 10 % border, radius 20 | `ui/Overlays.tsx: Modal` |

### Drama card

Used by every grid (home, channel, topic, search, 猜你喜欢, favourites).

| Part | Desktop | Phone |
|---|---|---|
| Cover | 3:4, radius 0.9 rem (12.6 px), `#242424` behind the image | 3:4, radius 12 |
| Top-left | One pill: "原创" (original, `#ffd21c`) and / or "新片" (new in the last 7 days, `#89eed3`), 23 px, 12/500 black; split in two when both show | 26 px |
| Top-right | Score "9.1分", 12/700 `#ffd900` on black 70 % with blur, radius 4.5 | 26 px, radius 6 |
| Bottom | Left: ▶ play count "44.4w" on black 80 %. Right: "14小时前 更新至3集", or "全21集" when finished, black 80 %, truncated to fit | 11 px text |
| Below | Title 14/500 `#e7e6e6`, one line; up to 4 tag chips 21 px, 12 px white 60 % on white 5 %, radius 3, wrapping | Title white 90 %; chips 20 px |
| Grid | 7 columns, 18 px column gap | 2 columns, 10 px gap |

## 2. Site frame

### Desktop header (≥ 1024 px)

| Element | Spec |
|---|---|
| Bar | 76 px, full width, `black/30` with an 18.75 px backdrop blur. It lies over the page (absolute, not sticky): the home hero runs under it, other pages start 76 px down, and it scrolls away. |
| Left | Logo block 112 × 41 |
| Nav | 16 px after the logo; links 41 px high, 14/600, `#d9d9d9`, the active one plain accent text (no pill); 4 px apart, side padding 3 / 5.5 / 9 px at < 1366 / ≥ 1366 / ≥ 1680 px. Items: 首页 · ReelGuo原创 · each open channel by short name (the "AI成人" prefix dropped) · 专题 · 排行榜 · 博客 ⌄ · 回家的路 · ReelGuo APP. On a series page its channel is active. |
| Search | 210 × 38 frosted box (`rgba(221,221,221,.15)`, white 9.4 % border, radius 24, blur), 14/700 input, 16 px icon at the right; below 1366 px a 36 px round icon button. Focus opens the history / 热搜榜 / 猜你喜欢 dropdown. |
| Account | "登录/注册" pill 97 × 38, accent fill, `#14130e` 14/600 text, 16 px after the search; signed in, the avatar and nickname |

### Phone header (< 1024 px)

| Element | Spec |
|---|---|
| Bar | 56 px, fixed, `#0a0a0a`: 28 px menu icon at 18 px from the left, the logo centred, a 28 px search icon at 18 px from the right. No bottom tab bar. |
| Menu | Drawer from the left: account row, then the nav items as 48 px rows |
| Search | Drawer from the top: input with 取消 (cancel), then the search panel |

### Footer

| Element | Spec |
|---|---|
| Bar | `#0d0c0c` with a white 10 % top rule; 23 px top padding |
| Left | Logo, then 12/500 address lines (`#999` label, accent value): 备用入口 (backup entry), 最新地址 (latest address), 永久地址发布页 (address page); the © line in white 35 % |
| Right | Three 110 px columns 73 px apart, ending at the column edge: 关于 (about) · 政策 (policies) · 其他 (other); headings 12.25/500 white, links 12.25 white 35 %, 25 px apart |

### Floating dock

Fixed at the right edge (19 px from the edge, 30 px from the bottom; scaled to 82 % on phones):

| Part | Spec |
|---|---|
| 下载 APP card | 88 × 92, radius 10, dark gold gradient with a white 10 % border: 60 px app icon, then a "下载 APP" gold-gradient pill; a small × hides it for the session. Not shown inside the apps. |
| Buttons | 68 px column, white 5 % with blur, radius 12: "最近浏览" (recently viewed, accent-filled, opens 个人中心) and "回到顶部" (back to top, grey); each 56 × 56, radius 9, 20 px icon over 12/500 text |

### Routes

Every page has a real URL, and back / forward work normally. Overlays (sheets, fullscreen, dialogs) take their own history entry and close first on back.

| Path | Page |
|---|---|
| `/` | Home (§3) |
| `/c/{slug}?sort=&page=` | Channel (§4); `/c/all` lists every channel, `/c/all?sort=original` is ReelGuo原创 |
| `/video/{id}`, `/video/{id}/ep-{n}` | Series (§5 desktop, §6 phone); one URL per episode (old `?ep=` links still open the episode) |
| `/ranks`, `/ranks/{hot\|recommend\|potential}` | Rankings (§8) |
| `/topics`, `/topics/{id}` | Topics (§9) |
| `/posts?tab=&page=`, `/post/{id}` | 博客 (§10) |
| `/go-home` | 回家的路 (§11) |
| `/search?q=` | Search (§12) |
| `/me`, `/me/wallet`, `/settings` | Account, wallet, settings (§13, §14) |
| `/store`, `/pay/result?order=` | Store and payment result (§14) |
| `/download` | 下载 App (§15) |

## 3. Home

| Section | Spec |
|---|---|
| Hero (desktop) | Full width under the header, 37.8 vw tall. The cover fills the top 35.2 vw; its bottom edge is cut by a wide ellipse (`50% / 2.45vw`) with a thin light line along the curve. Overlays: `linear-gradient(90deg, black 60 % → transparent 60 %)` and a page-colour fade over the bottom 30 %. Text at the column's left, its bottom at 23 % of the image height: "ReelGuo · tagline" 1.125 rem accent; title 3.0625 rem/600; one line of synopsis 1.0625 rem white 80 %; "🏆 热播总榜No. N │ 9.3分 │ 534.7w次播放" 1.1875 rem; chips (two tags + progress) 2 rem high, white 20 %, radius 4.5. Six thumbnails centred on the curve, 7.13 vw wide (3:4), 0.73 vw apart, radius 0.63 vw, the current one outlined in the accent. Auto-advances every 5 s, pauses on hover. |
| Hero (phone) | Full width, 40 vw tall, bottom fade: "ReelGuo · tagline" 12/500 accent, title 24/600, "9.2分 44.1w次播放" 12/500; dots at the bottom right (5 px, the current one an 11 px accent bar) |
| 公告 (announcements) | 30 px under the hero (10 px on phones); 36 px high (37 px phones), radius 4.5 (8): a 67 px `#242321` label with a megaphone icon and "公告", then the active announcements scrolling (38 s loop), 12 px `#bbb` (white on phones), each with an accent "查看详情" (details) link when it has one. Hidden with none active. |
| 热门标签 (hot tags) | Title, then pills 29 px, 12.25 px `#bbb`, white 5 % fill, radius 18, 7.5 px apart, wrapping. Phones: 68 × 32 pills, 14/500 white 80 %, light gradient fill, scrolling sideways. |
| Card sections | 29 px above each title: 精选推荐 (editor picks, one row; 6 on phones) · 最近上新 (recently updated, 2 rows) · 热播短剧 (hot dramas, 2 rows), each with its "more" pill (更多推荐 / 全部上新 / 更多热播) |
| 专题推荐 (topics) | "查看全部" (see all), then three 16:9 tiles (§9), one per row on phones |
| 官方入口 (official entry) | Under a white 10 % rule: "ReelGuo 官方入口" 1.25 rem/600 `#e6e6e6`, then 14/500 `#999` lines with accent links: the official domain, the Telegram group and email when configured, 回家的路 and 下载 App |

## 4. Channel page

| Element | Spec |
|---|---|
| Header | Breadcrumb, then one 20 px line (16 px phones): "【频道名】" white + the channel description in white 65 % |
| Tabs | Pill tabs 最近更新 (latest) · 当前热播 (hot) · 精选推荐 (editor picks), kept in `?sort=`; ReelGuo原创 has no tabs |
| Grid | 42 cards per page (7 × 6), 22 px under the tabs; numbered pagination with "跳转至"; page in `?page=` |
| FAQ | Four questions about the channel, closed |
| Coming soon | "即将上线" (coming soon) panel, no actions |

## 5. Series page (desktop, ≥ 1024 px)

| Element | Spec |
|---|---|
| Layout | Breadcrumb, then the player column and a 327 px side panel, 15 px apart. In theatre mode the player takes the full width and the panel moves below it. |
| Title bar | 40 px, `#262423`, white 10 % border, radius 7.5: title 1.125 rem/600 and "第N集" 14 px |
| Player | 9 px below, 16:9, black, radius 9. Controls: seek bar, play, time, next, speed, volume, picture-in-picture, theatre, fullscreen; they hide after 2.8 s idle. Paid episodes show the paywall (§14). |
| Action bar | 9 px below: one joined bar, `#1d1c1b`, radius 9, of seven 44 px `#262423` cells: ♥ count · ☆ 收藏 (favourite) · 分享 (share) · 下载APP · 上一集 (previous) · 下一集 (next) · 反馈 (feedback: the report sheet). 14/500 white; disabled cells dimmed. |
| Side panel | Full height of the player column, `#161616`, radius 9: tabs 选集 (episodes) / 评论（N）(comments) over a 1 px rule; episodes as 52 × 36 buttons, five per row, 7 px apart, radius 6 (`#1d1d1e`, `#8a8a93` text; the current one accent-filled, bold black; paid ones with a lock); the comments tab shows the comment list and composer inside the panel. |
| Info card | 12 px below, `#161616`, radius 12, 17 px padding: cover 160 × 213; facts in three columns — 上线日期 (release date), 更新状态 (status), 单集片长 (episode length) / 出品 (studio, with a "+关注" pill), 播放次数 (plays), 题材类型 (genre) — labels `#737373`, values white 14 px; tag chips 26 px `#1c191e` with a `#2d2831` border at the bottom. |
| Rating box | 263 × 213, gradient `#171611 → #211e13`, radius 15: "综合评分" 1.25 rem `#737373`, the score 3.75 rem/600 accent + "分"; "点我评分" (tap to rate) with five stars (2–10 points); "提交评分" (submit) 38 px, a faded gold gradient that turns solid once a score is picked. The viewers' average shows once 5 people have rated, the editorial score before that. |
| 剧情简介 (synopsis) | 12 px below, `#161616`, radius 9: title 1.125 rem/600, text 14 px `#a29fa6` |
| Tabs | 猜你喜欢 (you may like) · 最新地址 (latest addresses), underline tabs that jump to their section |
| 猜你喜欢 | 1.625 rem title, then two rows of cards from the same channel |
| 最新地址 | Title, then address cards (当前地址 · 可用, 永久地址 · 长期保存 when configured, 地址发布页 · 备用访问), each with the address, a hint and a 复制 (copy) pill; then the address lines, Telegram group and email |

Keyboard: `Space` play/pause · `←/→` ∓5 s · `F` fullscreen · `T` theatre · `M` mute.

## 6. Series page (phone)

The phone series page is a fixed, full-screen vertical player, as on the reference. It is the feed player from HGhub, opened on one series.

### Layers (bottom to top)

| Layer | Spec | Ref |
|---|---|---|
| Ambient background | Cover scaled 110 %, `blur-3xl`, 25 % opacity (hidden in fullscreen) | `Feed.tsx:785` |
| Video | `object-fit: cover` for portrait video on a portrait screen; `contain` for landscape video until the viewer switches | `Feed.tsx:773` |
| Top bar | Back chip 36 px and the series title on the left; on the right, a mute chip and a "反馈" (feedback) pill that opens the report sheet | `feed/overlays.tsx` |
| Right rail | The studio name, then round 36 px buttons with counts below: 点赞 (like) · 收藏 (favourite) · 评论 (comments) · 分享 (share), and the speed "1X" (taps cycle 1 → 1.25 → 1.5 → 2). White with a shadow over the video. | `feed/overlays.tsx: MobileRail` |
| Bottom info | Gradient 220 px; title 18 px bold, one line; synopsis 13 px, one line; the seek line; then a 44 px bar "选集 · 全集 N 集 · 第 N 集 ›" (episodes · N in total · episode N) that opens the episode sheet, with a fullscreen button at its right end | `feed/overlays.tsx: MobileInfo` |
| Seek line | Hit area 22 px; track 2.5 px at 30 % white with a white fill; 5 px while dragging, with a "0:42 / 2:10" chip; seeks on release | `Feed.tsx:1151-1165` |
| Pause indicator | 66 px circle `rgba(0,0,0,.55)` + blur, play glyph 28 px | `Feed.tsx:854` |
| Buffering / error | Centred 36 px spinner; error card "视频暂时无法加载" (video can't load right now) with "重新加载" (reload) | `Feed.tsx:862-865` |

### Gestures

| Gesture | Result | Thresholds | Ref |
|---|---|---|---|
| Single tap | Toggle clean mode (video only) | Under 260 ms and 16 px; acted on after 250 ms without a second tap | `Feed.tsx:634-648` |
| Double tap | Play / pause | Second tap within 280 ms | `Feed.tsx:636` |
| Long press | 2× speed while held; vibrates 25 ms | 380 ms | `Feed.tsx:541-551` |
| Long press, then slide up | Locks 2× on release: "上滑到此锁定倍速" (slide here to lock) → "松开锁定 2.0x" (release to lock); toast "已锁定 2.0x 倍速 · 点顶部标签解除" (2.0x locked · tap the top label to release) | Upward travel over 64 px | `Feed.tsx:569-614` |
| Horizontal drag | Scrub with a centred time overlay; seeks on release | After 16 px with dx > 1.2·dy; never within 24 px of the screen edge (iOS back swipe) | `Feed.tsx:580-629` |
| Pinch | Zoom 0.85×–3.5×, pan within 55 % of the overflow; snaps back below 1.08 | | `Feed.tsx:496-527` |
| Near the end | Next episode | `duration − 0.35 s` | `VideoSurface.tsx:364` |

### Fullscreen, lock and progress

| Element | Spec | Ref |
|---|---|---|
| Fullscreen | Fixed full-viewport stage, black; rotated 90° with CSS if the device stays portrait. Top right: lock and exit chips. Seek bar at `--sab + 52px`; bottom row: play/pause · time · speed · duration | `Feed.tsx:903-954` |
| Controls lock | Blocks every control except a faint unlock button | `PlaybackLock.tsx` |
| Progress | `{ id, ep, t, dur, at }` saved every 5 s (80 entries kept); reopening from history resumes there | `Feed.tsx:141` |

### Episode / detail sheet

Opened from the 选集 bar. Height 72 vh.

| Element | Spec | Ref |
|---|---|---|
| Header | Segmented **详情** (details) / **选集** (episodes) and a 32 px close button | `Feed.tsx:1232-1244` |
| Details | Cover 84 px (3:4), title, "8.6 分" (score), "共 40 集" (40 episodes), like / favourite / comment counts; studio card with "+ 关注" (follow); description; "举报该剧" (report this series) | `Feed.tsx:1252-1289` |
| Episodes | "共 N 集" with "倒序 / 正序" (newest / oldest first); 4-column grid of 40 px cells, the current one in the accent colour, paid ones with a corner lock | `Panels.tsx:10-47` |

## 7. Comments

| Where | Form |
|---|---|
| Desktop series page, post pages | Inline panel (§5) |
| Phone series page | Bottom sheet from the 评论 rail button: "评论" 13.5 px, "N 条评论 · 第 N 集", close button |

| Element | Spec | Ref |
|---|---|---|
| Item | Avatar 32 px (colour from the user id) · nickname · time · text 12.5 px · ♥ count · "N 条回复" (N replies) | `Panels.tsx:112-127` |
| Composer | Placeholder "请留下你对《剧名》的看法" (share your thoughts on 《title》), or "登录后参与评论" (log in to comment) when signed out (focus opens login); round send button | `Panels.tsx:143-155` |
| After sending | Toast "评论已提交，审核通过后展示" (submitted; shown after review) | `Panels.tsx:93` |
| ReelGuo additions | "审核中" (under review) badge on your own pending comments, "置顶" (pinned) badge, likes, replies one level deep with "展开 N 条回复" (show N replies), ⋯ menu with 举报 (report) or 删除 (delete) | `social/Comments.tsx` |

## 8. Rankings

| Element | Spec |
|---|---|
| Header | Breadcrumb; a 30 px accent-tinted icon tile, the board name 1.875 rem/600, and one line in white 65 % ("按站内播放热度排序 · 每 10 分钟更新 · TOP20") |
| Tabs | Underline tabs 热播榜 (hot) · 推荐榜 (recommended) · 飙升榜 (rising); the active one 15.75/600 accent |
| Row | White 5 % with a white 10 % border, radius 12, 13 px padding, 11 px apart: cover 123 × 165 with a "TOP / 01" ribbon badge at the top left and "更新至N集" at the bottom right; title 1.125 rem/600; meta 14/500 white 80 % (channel short name, tags, accent score with a star); synopsis up to 3 lines. Right: a 68 px divider, the metric 21/600 with a flame ("42W"), and its label (实时热度 / 推荐值 / 飙升指数). Phones: 90 × 120 cover, no metric column. |
| FAQ | Five questions, open |

## 9. Topics (专题)

Topics are hand-picked lists of series, managed in Admin → 专题. Only published topics are shown, and unpublished series inside a topic are hidden.

| Element | Spec |
|---|---|
| Topic tile | 16:9, radius 12, darkened at the bottom: "N部剧集" (N series) 12/500, title 1.125 rem/600, one line of description in white 60 % (two lines on phones) |
| `/topics` | Breadcrumb; "全部专题" (all topics) 1.875 rem/600 with an intro line and "共 N 部" on the right; tiles in 3 columns, 18 px apart (one column on phones); then 相关推荐 (related picks, 7 cards) and an open FAQ |
| `/topics/{id}` | Breadcrumb, the title and description with "共 N 部", then the topic's series as a card grid, in the admin's order |

## 10. 博客 (blog / posts)

| Element | Spec |
|---|---|
| List | Breadcrumb; "博客" 26/600 and one line in white 65 %; a 46 px split bar `#141416` with 全部文章 (all) / 热门文章 (popular), the active one accent; then article cards: `#171719`, radius 12, 16 px padding, a 348 px 16:9 cover on the left (full width on phones), then a meta row (avatar initial, "ReelGuo吃瓜 · date · tag", 14 px `#a9aebc`), the title 21/600 `#f1f2f5`, a two-line excerpt and the read count. Numbered pagination, then an FAQ. |
| Post | Breadcrumb; media area with the cover and a play button (plays the linked series inline); title, date and heat, tags, a "观看正片" (watch the series) card; the article; like, favourite, share, report; the inline comments panel |

## 11. 回家的路 and the address reminder

| Element | Spec |
|---|---|
| Hero | Centred: "官方权威发布 · 永久有效入口" pill (36 px, `#111`, accent 50 % border), the app icon, "ReelGuo · 官方最新地址发布页" 2.25 rem/600, a two-line explanation 1.25 rem white 50 %, then three 179 × 45 pills: 立即进入主站 (enter, gold gradient) · 复制分享链接 (copy share link, outlined) · 保存回家的路 (save this page, gold gradient) |
| Panels | White 4 % with a white 10 % border, radius 6, 22 px apart; each with an icon, a 1.625 rem title, an accent note and a 1.25 rem grey line. Inner cards `#0d0d0d`. |
| 最新可用地址 | Address cards in three columns: "备用地址 01" with 当前 (current) or 可用 (available), the address 1.625 rem/600, then 立即进入 (gold) and 复制地址 (outlined) pills |
| 官方域名与地址 | Two-column rows: label, address, "复制并保存" (copy) pill |
| 防失联方式 | Up to four cards: 邮箱自动回复 (email), TG 官方交流群 (Telegram) when configured, 收藏本页 (bookmark), 下载 App |
| 访问异常排查 / 设备使用与兼容性建议 | Three numbered tips; three device columns (iPhone / Android / desktop) with green browser names and orange warnings |
| 官方入口 | Text block: the official domain, about ReelGuo, supported platforms |
| Reminder dialog | "地址收藏提醒" (bookmark our addresses): the addresses and contacts, "复制全部地址" (copy all); once a day per device, closes itself after 30 s; not shown inside the apps or on `/go-home` |

## 12. Search

| Element | Spec |
|---|---|
| Box | 48 px pill input "搜索剧名、作者或题材" (search titles, authors or genres) with an accent "搜索" (search) button |
| No query | The header search panel (history, 热搜榜, 猜你喜欢) in a panel |
| Results | "搜索 “关键词”" with "共 N 条结果" (N results), then the poster grid with 加载更多. Empty: "没有找到相关内容" (nothing found) / "换个关键词试试，比如「都市」「逆袭」" (try another keyword) |

## 13. Account, settings, login, age gate

| Screen | Spec |
|---|---|
| 个人中心 (`/me`) | Account card: signed out, "登录 / 注册" with the reasons to sign in; signed in, avatar, nickname and counts. Wallet strip (§14), download card (hidden inside the apps), 设置 (settings), 退出登录 (log out, confirmed once). Tabs 我的收藏 (favourites) / 观看历史 (history) with poster grids; history shows "第 N 集 · 0:42" and resumes there. |
| 设置 (`/settings`) | Sections 播放 (playback: 启动时静音, mute on start), 本地数据 (local data: counts, clear caches with confirmation), 关于 (about: version, app version or 下载 App, legal pages). Toggle 46 × 26. |
| Login dialog | 400 px dialog (also on phones): app icon, "用户登录注册" (sign in / register), a pill switch 登录 / 注册, labelled 账号 (account) and 密码 (password) inputs (42 px pills), the hint "登录后可使用收藏、评论和个人中心功能。" (sign in to use favourites, comments and the account page), and a full-width accent button. Register adds 昵称（可选）(nickname, optional) and the 18+ / terms checkbox; no email is collected. |
| 18+ gate | Full screen, dark with a soft accent glow: logo + "ReelGuo / AI 短剧", "本站含有成人内容，仅限年满 18 周岁的成年人访问。" (adult content, 18+ only) in two lines, the primary pill "我已年满 18 周岁，进入网站" (I'm 18+, enter) with a white ring, the secondary pill "未满 18 周岁，离开" (under 18, leave), the footnote "进入即表示您确认已年满 18 周岁，并同意遵守当地法律法规。" (entering confirms you are 18+ and will follow local law), and the legal links. Nothing renders behind it. |

## 14. Paid content (ReelGuo addition) ✅

The reference product has no paid content. These screens follow common short-drama conventions in ReelGuo's own style.

| Screen | Spec | Code |
|---|---|---|
| Paywall | Over the player when the episode is locked. Card ≤ 300 px: lock icon, "第 N 集需要解锁" (episode N needs unlocking), "前 M 集免费 · 解锁后可永久观看" (first M free · yours for good once unlocked). Primary button "30 金币解锁本集" (unlock for 30 coins) / "金币不足，去充值" (not enough coins, top up) / "登录后解锁" (sign in to unlock). Balance line with 充值 (top up); "开通 VIP · 全部剧集免费看" (get VIP · every episode free); "自动解锁后续剧集" (auto-unlock next episodes) | `billing/Paywall.tsx` |
| 充值中心 (`/store`) | In the account frame. Balance card (coins, bonus, VIP). 金币礼包 (coin packs) in 2 columns (4 on desktop) with badge, bonus and price; VIP 会员 as rows; notes (payment method, billing descriptor, terms, "VIP 到期不会自动续费" (VIP doesn't renew)). A tap opens the 确认购买 (confirm purchase) dialog. | `billing/StorePage.tsx` |
| Payment result | `/pay/result?order=`: "正在确认支付结果…" (confirming…) → "支付成功" (paid) with what arrived and "继续观看《…》第 N 集" (continue watching) · 查看钱包 (wallet) · 完成 (done); "支付未完成" (not paid) with 重新充值 (try again); after 60 s "暂未收到支付结果" (no result yet) | `billing/PayResult.tsx` |
| 我的钱包 (`/me/wallet`) | Balance card with 充值, the auto-unlock switch, 消费记录 (records) with signed coin deltas | `billing/WalletPage.tsx` |

## 15. Apps (ReelGuo addition) ✅

| Screen | Spec | Code |
|---|---|---|
| Start page (in the app) | Black, the app icon, "ReelGuo", a spinner and "正在连接…" (connecting…); when nothing answers, "无法连接服务器" (can't reach the server) with 重试 (retry). Footer "本 App 仅面向年满 18 周岁的成年人" (adults 18+ only). | `native/boot/` |
| 下载 App (`/download`) | One card per platform (newest release): icon, name, version and date, download pill, notes, install tip, SHA-256; then the iPhone card ("添加到主屏幕", add to home screen) | `pages/Download.tsx` |
| Update prompt | System dialog "发现新版本" (new version available) with 立即更新 (update now) / 稍后 (later); mandatory releases offer only 立即更新 | `native/src/updates.rs` |

## 16. Not copied

| Reference element | Why | ReelGuo instead |
|---|---|---|
| 黄果 names, logo, slogans | Their brand | ReelGuo name, logo and copy. The yellow accent is kept by the product owner's decision (one token in `tokens.css` to change). |
| Ad slots: banner rows between home sections, on the series, channel, topic, blog and 回家的路 pages; the corner ad on the player; partner app tiles in the episode panel and under the info card; the notice bar's referral line | Advertising, much of it gambling | Nothing; the layout closes up around them |
| Their addresses, Telegram group and email | Theirs | Ours, from the `Site` settings and the mirror list (empty until configured) |
| Hardcoded channel list | Code | Channels are database rows managed in admin |
| Their icon SVGs, CSS and code | PolyForm Noncommercial (HGhub after v1.0.1) | lucide-react; our own CSS |
| Light theme | The reference is dark only | Dark only (the light tokens remain in `tokens.css`, unused) |
