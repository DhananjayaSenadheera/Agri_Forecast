# Keppetipola & Thambuttegama DEC Portal Probe — Phase 1 (PROBE ONLY)

**ClickUp task:** 86cahef44
**Date:** 2026-07-02
**Author:** agri-data-engineer
**Scope:** Network/repo investigation only. No ingestion code written. This
document is the input to a build/no-build decision, not an implementation.

**Note on file location:** no `Documents/` folder exists anywhere in this
repo (`find` for `*.pdf` / `*prd*` outside `harti_cache/` returned nothing),
so this probe doc is placed at the repo root as the task instructed as the
fallback.

---

## 0. Baseline: the existing Dambulla contract (for comparison)

Read from `src/AgriForecast.Infrastructure/ExternalSources/Clients/DambullaApiClient.cs`,
`IDambullaApiClient.cs`, `DTOs/DambullaChartItemDto.cs`,
`Services/MarketPriceIngestion/MarketPriceIngestionService.cs`, and
`Dependency_Injection/InfsDependencyInjection.cs`:

- **Base URL:** `https://api.dambulladec.com/` (config key
  `MarketPriceSources:DambullaDec:BaseUrl`, `appsettings.json`)
- **Endpoint:** `GET api/prices/product/{productId}/chart` — one call per
  product ID, looped `productId = 1..MaxProductId` (config key
  `MarketPriceSources:DambullaDec:MaxProductId`, currently `101`)
- **Auth:** none. Plain unauthenticated `HttpClient`, 30s timeout.
- **Response shape:** JSON array of chart items:
  ```json
  [{"id": 1, "product_id": 5, "min_price": 190.0, "max_price": 210.0,
    "date": "2026-06-24", "Product": {"name": "Beans"}}]
  ```
  Fields: `id`, `product_id`, `min_price`, `max_price`, `date`
  (`YYYY-MM-DD` string), nested `Product.name`. **No arrivals/volume/quantity
  field anywhere in the DTO or the response.**
- **Product-ID namespace:** flat integers 1–101, opaque (no documented
  catalog; the .NET side self-heals by auto-creating a `Crop` row the first
  time a product ID appears with data, `CropCode = DMB{productId:D6}`).
- **History depth:** the `chart` endpoint appears to return each product's
  full available series in one call (multi-date array), not just "today" —
  confirmed by the ingestion service's per-date de-dup logic
  (`GetExistingDatesAsync` / `existingDates.Contains(date)`) operating over
  a returned list, not a single point.
- **Zero-price handling:** `MinPrice <= 0 && MaxPrice <= 0` rows are treated
  as "market closed" and skipped at ingestion (not stored) — see line 99 of
  `MarketPriceIngestionService.cs`. (Per this agent's own standing
  discipline, zero-price rows should ideally be *kept* and filtered at
  feature time rather than dropped at ingestion; that's a pre-existing
  Dambulla-specific choice, noted here for completeness, not something this
  probe is asked to fix.)
- **Stability signal:** this is a real, purpose-built, versioned JSON API
  behind a custom domain — backed by a login-gated React/Vite SPA
  (`dambulladec.com`, bundle `assets/index-oFG8PC1e.js`) used for internal
  price data-entry (`axios` `baseURL:"https://api.dambulladec.com"`, token/
  role-based auth context, `/login` route). This is an **operational
  internal tool that happens to expose a public read API**, not a
  publish-only PR project — a reasonably strong stability signal.

---

## 1. Does `api.dambulladec.com`'s API family serve other DECs?

**No.** Findings:

- The Dambulla SPA JS bundle was fully downloaded (424 KB) and grepped for
  `baseURL`, `api.`, and market-selector logic. Only one `baseURL` constant
  exists: `https://api.dambulladec.com`. No `centre=`/`market=` query
  parameter, no alternate base URL, no market-switcher component.
- The string `"Keppetipola"` **does** appear in the bundle, but only inside a
  static i18n/translation dictionary listing the display names of Sri
  Lanka's various Dedicated Economic Centres (Veyangoda, Ratmalana,
  Meegoda, Kandeketiya, **`kepptipola: "Keppetipola Economic Center"`**,
  ...) — almost certainly copy for an "our sister centres" mention on the
  marketing site, not a functioning multi-tenant selector. No corresponding
  API call or route was found tied to it.
- No sibling subdomains resolve: `api.keppetipoladec.com` and
  `api.thambuttegamadec.com` both return **no DNS record at all** (empty
  `dig` output — NXDOMAIN).

**Conclusion: the Dambulla API is Dambulla-only. There is no shared/
multi-DEC API family to plug into.**

---

## 2. Official portals — DNS + GET results

| Candidate host | DNS | HTTP result | Notes |
|---|---|---|---|
| `api.dambulladec.com` | resolves (143.110.181.88) | 200, JSON | baseline, documented above |
| `dambulladec.com` | resolves → GitHub Pages (`rashenthemiya.github.io`, 185.199.x.x) | 200, HTML | React/Vite SPA (admin data-entry tool + marketing site), not a data feed |
| `api.keppetipoladec.com` | **NXDOMAIN** | — | does not exist |
| `keppetipoladec.com` | **NXDOMAIN** | — | does not exist |
| `www.keppetipoladec.com` | **NXDOMAIN** | — | does not exist |
| `keppetipoladec.lk` | **NXDOMAIN** | — | does not exist |
| `keppetipola.gov.lk` | **NXDOMAIN** | — | does not exist |
| `api.thambuttegamadec.com` | **NXDOMAIN** | — | does not exist |
| `thambuttegamadec.com` | **NXDOMAIN** (referenced only as *links* on the .lk site — see below) | — | domain not actually registered/resolving despite being linked from thambuttegamadec.lk's own menu |
| `www.thambuttegamadec.com` | **NXDOMAIN** | — | does not exist |
| `thambuttegamadec.lk` | resolves (Cloudflare, 104.21.x / 172.67.x) | 200, HTML | **Real official WordPress site** — see §2.1 |
| `app.thambuttegamadec.lk` | resolves (same Cloudflare IPs) | 200, HTML | Custom PHP app subdomain — see §2.2 — **stale/dead, not a data source** |
| `agrimin.gov.lk` / `www.agrimin.gov.lk` | resolves | 301 → `https://agrimin.gov.lk/web/` → **404** | Ministry of Agriculture site; no working DEC directory page found to confirm/deny a Keppetipola listing |
| `harti.gov.lk` | resolves | (not re-probed live; PDF cache + prior audit already exhaustively covers it) | see §3 |

### 2.1 `thambuttegamadec.lk` — official WordPress marketing site

- `GET https://thambuttegamadec.lk/wp-json/` returns a standard WordPress
  REST API discovery document. Registered namespaces: `oembed/1.0`,
  `contact-form-7/v1`, `rrj-advanced-charts/v1`, `yoast/v1`, `wp-rocket/v1`,
  `google-site-kit/v1`, `wp/v2`, `wp-site-health/v1`,
  `wp-block-editor/v1`. **No custom price-data REST namespace.**
  `rrj-advanced-charts/v1` is a generic third-party WP charting plugin, not
  a bespoke price API.
- This is a content/marketing site (About Us, contact form, news posts,
  vacancy postings, Sinhala/Tamil/English via TranslatePress). It links to
  a separate `app.thambuttegamadec.lk` subdomain and to a
  `thambuttegamadec.com` alternate domain that does **not** currently
  resolve — evidence of a partially-migrated or abandoned secondary domain.
- **No arrivals/volume mention anywhere on the page.**

### 2.2 `app.thambuttegamadec.lk/public/` — the "app" portal (closest thing to a data source)

This looked the most promising at first (date-in-URL price display,
`?date=YYYY-MM-DD`), so it got the deepest probe:

- Server-rendered HTML "price card" grid, one card per crop, e.g.
  `<h5 class="title">නාඩු/nadu/நாடு</h5> <a class="button">අද මිල<br>රු:221-223</a>`
  (min-max range, matches HARTI/Dambulla's convention). This confirms the
  *shape* of the data (per-crop wholesale min-max) would be usable if live.
- **Critical finding — the page is a frozen static snapshot, not a live
  query:** requested `?date=2026-07-01`, `?date=2023-11-11`,
  `?date=2020-01-15`, `?date=2015-06-01`, and the bare URL. All five
  responses are **byte-identical** (verified via MD5 checksum — all five
  hashed to `edcb43fdd94a4186bde904e6e6a112fc`), and every one displays the
  same fixed title: *"අද දින එළවළු හා පළතුරු මිළ **2025-04-23**"*
  ("today's vegetable and fruit prices, **2025-04-23**"). The `date` query
  parameter is silently ignored server-side. This page has not reflected
  real "today" data since at least April 2025, over a year stale as of
  this probe (2026-07-02).
- Probed for a JSON/API surface under the same host: `api/prices` → 301,
  `public/api` → 404, `prices.json` → 500. No working JSON endpoint found.
  `public/login` (200, distinct title) exists but is a farmer-facing auth
  wall, not a data endpoint.
- **No arrivals/volume/quantity/supply field anywhere in the rendered
  HTML** (grepped for `arrival|volume|quantity|supply|stock|kg|kilogram` —
  zero matches beyond generic page chrome).

**Conclusion for Thambuttegama: no live, queryable API or portal exists.**
The one candidate that superficially resembles a queryable system
(`app.thambuttegamadec.lk/public/`) is a dead, frozen HTML snapshot from
2025-04-23 that ignores its own date parameter — worse than useless for a
forecasting pipeline, since scraping it would silently backfill every date
with the same stale April-2025 numbers if not caught. This is exactly the
kind of fragile-HTML risk the standing "validate at ingestion" discipline
exists to catch.

### 2.3 Keppetipola — no web presence found at all

- Every DNS candidate tried (`keppetipoladec.com`, `.lk`, `api.` variants,
  `keppetipola.gov.lk`, `keppetipoladec.gov.lk`) returned **NXDOMAIN**.
- `www.agrimin.gov.lk` (Ministry of Agriculture, linked from the
  Thambuttegama site's footer) redirects to `agrimin.gov.lk/web/`, which
  404s — no working ministry DEC directory to check for a Keppetipola
  listing.
- The only place "Keppetipola" appears anywhere in this probe is the
  static translation-string list inside Dambulla's own SPA bundle (§1) —
  not a portal, not an API, not even a real link (no anchor tag using that
  string was found).

**Conclusion for Keppetipola: zero discoverable web presence. Nothing to
build against.**

---

## 3. HARTI PDF fallback — confirmed baseline (from the just-completed audit)

This reuses the exhaustive findings already produced by the sibling task
(`src/AgriForecast.ML/harti_multimarket_audit.md`, ClickUp 86cahef3e,
completed same day) rather than re-deriving them:

- **Corpus:** 2,972 cached daily bulletin PDFs,
  `src/AgriForecast.ML/harti_cache/`, spanning **2015-06-22 → 2026-06-27**
  (~11 years).
- **Both markets are present as column headers in the daily wholesale
  table**, confirmed by direct inspection of raw `pdfplumber` table extracts
  (audit §5.3, PDF `harti_2022-11-26.pdf`):
  ```
  Serial Item Peliyagoda Norochchole Kandy Nuwara Eliya
  DambullaThambuththegamaKappetipola Meegoda* Bandarawela Veyangod*
  ```
  Note the column header spellings actually used by HARTI:
  **`Thambuththegama`** (not "Thambuttegama" — the DEC's own domain spells
  it with one fewer "th") and **`Kappetipola`** (at least in this PDF;
  other years may use "Keppetipola" — not yet verified across the full
  corpus since these two markets were out of scope for the just-completed
  audit).
- **Format:** min/max wholesale price range per crop per market per day,
  same convention as Dambulla and Pettah (`MinPrice`/`MaxPrice`, never a
  single point figure).
- **Arrivals: confirmed absent, corpus-wide.** The completed audit ran a
  full-text scan for `"Arrival"` / `"arrival"` / `"Volume"` / `"volume"`
  across all 2,972 PDFs (both pages, both languages) and found **zero
  hits**. This applies to the whole daily bulletin table, i.e. it covers
  Thambuththegama and Keppetipola's columns too — arrivals simply do not
  exist in this data source for any market.
- **Parser status — important nuance for this decision:** the multi-market
  parser (`agriforecast_ml/harti/parser.py`) currently only has
  **Dambulla, Pettah, and Narahenpita** wired into its active
  `_TARGET_MARKETS` tuple and `_MARKET_HEADER_ALIASES` dict. Thambuththegama
  and Keppetipola are **not yet extracted**, even though the audit's own
  raw-table evidence shows their columns exist in the same PDFs already
  being parsed. This is a config-sized addition (extend two dict/tuple
  literals with alias spellings), not a new capability — the header-text
  detect-don't-hardcode machinery is fully generic and already
  regression-tested against column-order shuffling (audit §5.6). It does,
  however, mean "HARTI-PDF fallback" for these two markets is **available
  in the source data today but not yet implemented in the loader** — a
  follow-up ticket, not a probe finding that blocks the recommendation.
- **Known extraction risk specific to these two markets:** §5.3 of the
  audit shows a real header-merge artifact where cell-border loss during
  PDF export causes market names to bleed into each other —
  `"NorochcholeT"` / `"hambuththegam"` / `"aKappetipola"` — i.e.
  Thambuththegama and Keppetipola sit in the noisier tail columns (6th–7th
  of up to 10), unlike Dambulla/Pettah's cleaner early columns (3rd/1st).
  The alias/substring-match approach the audit proved robust to this
  (§5.3: substring match still finds the right column despite the
  bleed-through), so this is a manageable risk, not a blocker — but it
  means a few percent of PDFs may WARN-skip Thambuththegama/Keppetipola
  more often than they do Dambulla/Pettah. This should be re-verified with
  a dedicated 20-30 PDF spot-check once these two markets are actually
  wired in (out of scope for this probe).
- **DB readiness:** both markets are already seeded in
  `AgriForecastDbContext.cs` — `"Keppetipola Dedicated Economic Centre"`
  and `"Thambuttegama Dedicated Economic Centre"` (note: DB seed spells it
  "Thambuttegama", HARTI's PDF header spells it "Thambuththegama" — the
  loader's name-resolution step will need an alias/normalization mapping
  between these two spellings, same pattern already used for
  Pettah/Peliyagoda).

---

## 4. Arrivals/volume — answered for every source probed

| Source | Arrivals/volume present? |
|---|---|
| Dambulla API (`api.dambulladec.com`, re-checked this probe) | **No.** DTO fields are `id`, `product_id`, `min_price`, `max_price`, `date`, `Product.name` only. |
| Dambulla SPA marketing copy | No numeric arrivals data; "supply and demand" appears only as prose in an About Us paragraph. |
| `thambuttegamadec.lk` (WordPress site) | No. |
| `app.thambuttegamadec.lk/public/` (frozen HTML app) | No (and the page is stale/non-live regardless). |
| Keppetipola (any portal) | N/A — no portal exists. |
| HARTI daily bulletins (2,972 PDFs, all markets incl. Thambuththegama/Keppetipola) | **No — confirmed absent corpus-wide** (prior audit, full-text scan, zero hits). |

**No arrivals/volume/supply data was found in any source probed for these
two markets, live API or PDF.** The PRD's demand-proxy requirement is not
satisfiable from any currently-known Sri Lankan DEC source for
Thambuttegama or Keppetipola specifically (and, per the prior audit, not
for Dambulla/Pettah/Narahenpita either). This is a cross-cutting gap, not
specific to these two markets — worth flagging to product/PRD owners as an
open question rather than something either integration path solves.

---

## 5. History-depth comparison

| Source | Backfill depth | Update cadence | Format stability |
|---|---|---|---|
| Dambulla API | Full series per product in one call (exact depth not enumerated in this probe, but ingestion logic assumes multi-date, not "today only") | Presumed daily (matches HARTI's daily bulletin cadence and existing production ingestion cron) | Stable, versioned JSON API; internal tool, moderate confidence |
| Thambuttegama — any API/portal | **None usable.** `app.thambuttegamadec.lk` is frozen at 2025-04-23 regardless of query; no true backfill possible via this source (scraping it today would only ever yield one stale day, not history) | N/A — dead | Fragile HTML, plugin-based WordPress site, no JSON contract, evidence of domain migration abandonment (`thambuttegamadec.com` broken links) |
| Keppetipola — any API/portal | **None — no source exists** | N/A | N/A |
| HARTI PDFs (both markets, once parser is extended) | **2015-06-22 → 2026-06-27 (~11 years)**, subject to the same ~1.7% per-PDF page-detection skip rate documented for Dambulla/Pettah, plus a likely slightly higher WARN-skip rate for these two specific markets due to the tail-column header-merge artifact noted in §3 | Daily bulletins, historically produced; live site (`harti.gov.lk/daily-price.php`) was confirmed reachable and current in the prior audit | Same PDF-table-extraction fragility as Dambulla/Pettah, but already proven manageable (233 passing tests, detect-don't-hardcode column lookup, WARN-not-crash on drift) |

**HARTI PDFs offer roughly a decade more backfill depth than any live
portal candidate found for these two markets** (11 years vs. zero usable
years — the one "live-looking" portal is frozen and unusable for any date).

---

## 6. RECOMMENDATION

**(b) HARTI-PDF fallback for both Thambuttegama and Keppetipola. Do not
build .NET API clients for either.**

Reasoning:

1. **Keppetipola has no web presence to build against at all.** Every DNS
   candidate is NXDOMAIN. There is nothing to integrate.
2. **Thambuttegama's only candidate ("app.thambuttegamadec.lk/public/") is
   not a live data source.** It is a static HTML page frozen since
   2025-04-23 that silently ignores the `date` query parameter — building
   a client against it would either (a) require re-scraping a page that
   never changes, providing zero forecasting value, or (b) risk silently
   feeding stale April-2025 prices into the pipeline under today's date if
   the staleness isn't actively checked on every run. This fails this
   agent's "validate at ingestion" and "preserve temporal truth"
   disciplines outright — it's a worse foundation than the Dambulla API,
   not a comparable alternative to it.
3. **The HARTI-PDF path is already 90% built and proven.** The parser
   infrastructure (header-text detect-don't-hardcode column lookup,
   alias-spelling tolerance, WARN-skip-not-guess on missing columns,
   column-order-shuffle regression tests, `/CreationDate`-based point-in-time
   `AsOfUtc`) already exists, is unit-tested (233 passing tests), and has
   already successfully extracted Dambulla + Pettah with zero cross-market
   contamination across 11 years of layout drift. Adding Thambuththegama
   and Keppetipola is extending two dict/tuple literals
   (`_TARGET_MARKETS`, `_MARKET_HEADER_ALIASES`) with the correct alias
   spellings (`Thambuththegama`/`Thambuttegama`,
   `Keppetipola`/`Kappetipola`), not new engineering.
4. **HARTI gives ~11 years of backfill** (2015-06-22 → 2026-06-27) for both
   markets versus zero usable years from any live portal. For a
   forecasting model this depth advantage dominates any cadence advantage
   a live API might have offered.
5. **Neither path solves the arrivals/volume requirement** — that's a
   pre-existing gap across every Sri Lankan DEC source probed to date
   (this probe and the prior audit), not a reason to prefer one path over
   the other for these two markets specifically.

**Not a hybrid.** A hybrid only makes sense if a live portal offered
something the PDF corpus lacks (e.g. finer cadence, arrivals, retail
prices). Neither surviving candidate does — Thambuttegama's live portal is
actually *stale*, and Keppetipola's doesn't exist — so there is no
complementary value to combine. Building a .NET API client for either would
add ingestion surface area and maintenance burden for strictly worse data
than what the HARTI parser can already produce once extended.

**Suggested follow-up ticket (not in scope here):** extend
`_TARGET_MARKETS` / `_MARKET_HEADER_ALIASES` in
`agriforecast_ml/harti/parser.py` to include Thambuththegama (aliases:
`"Thambuththegama"`, `"Thambuttegama"`, and any header-merge-truncated
variants found on spot-check) and Keppetipola (aliases: `"Keppetipola"`,
`"Kappetipola"`), then run the same 25-30 PDF stratified spot-check the
prior audit used for Pettah/Narahenpita before trusting the extracted
values in `CropFeatureDaily`. Given the tail-column header-merge noise
observed in §3, budget extra time for hand-verifying a few PDFs where
these two markets' cells may need cleanup beyond straightforward
substring matching.

---

## 7. Anything surprising

- The `dambulladec.com` marketing domain resolves to **GitHub Pages**
  (`rashenthemiya.github.io`) rather than a government-hosted domain —
  suggests this whole system (including the `api.dambulladec.com` client
  the .NET side already depends on) may be a smaller, less
  institutionally-backed project than "Dedicated Economic Centre official
  API" implies. Worth keeping in mind for future dependency-risk
  assessment of the existing Dambulla integration, even though it's out of
  scope to act on here.
- `app.thambuttegamadec.lk/public/`'s complete indifference to its own
  `?date=` parameter (five different dates, one byte-identical response)
  was the most concrete "trap" found in this probe — it looks exactly like
  a working queryable history endpoint until you check more than one date.
  Good thing this was a probe-only task.
- Both target markets are already present as seeded `Markets` rows in
  `AgriForecastDbContext.cs`, and their column headers are already visible
  in the very PDFs the just-completed HARTI multi-market work parsed for
  Dambulla/Pettah/Narahenpita — this decision is closer to "flip a
  configuration switch and re-verify" than "build a new integration,"
  which is the main actionable takeaway for whoever picks up Phase 2.
