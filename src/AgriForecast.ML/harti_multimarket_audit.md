# HARTI Multi-Market Parse Audit — R1.1 P1 (ClickUp 86cahef3e)

**Date of audit:** 2026-07-02
**Scope:** Pettah + Narahenpita + arrivals extraction added to the HARTI daily
bulletin parser (`agriforecast_ml/harti/parser.py`), alongside Dambulla.
**PDF source:** all PDFs used in this audit came from the pre-existing local
cache at `src/AgriForecast.ML/harti_cache/` (2,972 files spanning
2015-06-22 → 2026-06-27, populated by prior downloader runs). The live
`https://www.harti.gov.lk/daily-price.php` listing was also reachable and
was queried directly (see "Site investigation" below) to confirm there is no
separate retail/arrivals bulletin series being missed — no new PDFs needed
downloading for this audit.

**Price column convention (for the feature layer):** HARTI publishes a
min/max wholesale range per market per crop — never a single point figure —
for every market including Pettah and Narahenpita. Rows written to
`PriceObservations` for `Source='HARTI'` therefore always populate
`MinPrice`/`MaxPrice` and always leave `WholesalePrice`/`RetailPrice` NULL.
`WholesalePrice`/`RetailPrice` are reserved for other future sources (e.g.
CBSL) that publish a single point figure per series. Downstream feature
code must read `MinPrice`/`MaxPrice` (or their midpoint) for HARTI rows, not
`WholesalePrice`, which will be NULL.

---

## 1. Site / corpus investigation (why Narahenpita and arrivals come back empty)

Before writing tests against assumed data, the daily bulletin PDF format was
inspected directly:

- The daily English wholesale-price table (the one this parser reads) has a
  **fixed set of up-country/low-country vegetable wholesale markets**:
  Pettah/Peliyagoda, Kandy, Dambulla, Meegoda, Norochchole, Thambuththegama,
  Keppetipola, Nuwaraeliya, and (from ~2022) Bandarawela, Veyangoda. Column
  count varies over time (7 → 9 → 10 observed), but the *set* of markets in
  the daily bulletin does not include Narahenpita, and no column in this
  table is an arrivals/volume figure — every column pair is `Min`/`Max`
  price.
- A full-text search across the local cache (see §2) and a live check of
  `harti.gov.lk`'s navigation (`daily-price.php`, `weekly-price.php`,
  `monthly-price.php`) found:
  - **No page or PDF filename anywhere on the site mentions "Narahenpita".**
    Narahenpita is the seeded `Markets` row labelled `"Narahenpita (HARTI
    retail)"` — it is a *retail* market. This daily bulletin is exclusively
    wholesale prices at Dedicated Economic Centres / assembly markets.
  - HARTI's **weekly bulletin** (`weekly-price.php`, e.g. *"Final English
    bulletin - week 22.pdf"*) is a completely different, 38-page narrative
    PDF format ("Weekly Food Commodities Bulletin", prose + charts, RETAIL
    MARKET AT A GLANCE section) — not a parseable table in the shape this
    package's `parse_pdf()` expects. Fetched and inspected one 2026-week-22
    sample directly (`GET
    assets/pdf/food_price/weekly/eng/2026/Final English bulletin - week
    22.pdf`, 2.7 MB, 38 pages) — confirmed it is out of scope for this
    parser and does not itself contain "Narahenpita" or an arrivals column
    on inspection of its first pages either.
  - Arrivals/volume data was not found in the daily table under any of the
    header spellings searched (`Arrival`, `arrival`, `Volume`, `volume`).

**Conclusion:** for the daily bulletin series this parser targets,
Pettah is present (and successfully extracted — see §3), but Narahenpita and
arrivals genuinely do not exist in this data source today. Per the task's
detect-don't-hardcode / fail-loud contract, the code still implements
`_locate_market_column(table, "Narahenpita")` and
`_locate_arrivals_column(table)` as real header-text lookups (not stubs) —
they correctly and legitimately return `None` on every PDF in this series,
producing a WARN log per PDF (Narahenpita) and a silent NULL arrivals value
(no column found — not an error) rather than fabricating data. The moment
either column appears in a future bulletin layout, both will activate
automatically with no code change.

---

## 2. Full-corpus text scan (all 2,972 cached PDFs)

A first-two-pages full-text scan (`"Narahenpita" in text`, `"rrival" in
text`, `"Pettah" in text`) was run in the background across the entire local
cache to corroborate the sample-based finding at full scale.

- **Sample-based finding (28-PDF stratified sample, §3):** 0/28 Narahenpita
  hits, 0/28 arrivals hits, 27/28 Pettah/Peliyagoda hits (1 PDF had no
  parseable English table at all — a pre-existing, unrelated parser
  limitation, see §4).
- **Full-corpus scan (2,972 PDFs, completed):** a first-two-pages
  (English + Sinhala) full-text scan ran end-to-end across every cached PDF.
  Final result:

  ```
  {
    "narahenpita": [],          # 0 hits across all 2,972 PDFs
    "arrival":     [],          # 0 hits across all 2,972 PDFs
    "pettah_count": 3735        # Pettah/Peliyagoda found on 3,735 of the
  }                              # ~5,944 checked pages (2 pages x 2,972 PDFs)
  ```

  **Zero Narahenpita hits and zero arrivals hits across the entire local
  corpus, both languages, both pages, all 2,972 files, 2015-06-22 through
  2026-06-27.** Pettah/Peliyagoda is present on the large majority of PDFs
  (the count exceeds 2,972 because both the English and Sinhala page of most
  2-page bulletins were checked, and "Pettah" also appears in surrounding
  prose/labels on some pages, not just the header).

This is a definitive, whole-corpus result, not a sampling inference:
Narahenpita and arrivals genuinely do not appear anywhere in the daily
bulletin series this parser reads. The detect-don't-hardcode lookups
(`_locate_market_column(table, "Narahenpita")`, `_locate_arrivals_column`)
remain live, real header-text searches in the code — not stubs — so this
conclusion self-corrects automatically the day HARTI changes its bulletin
layout to include either.

---

## 3. Sample audit — 28 PDFs, 2015-2026

Deterministic sample (2-3 PDFs per calendar year, 2015→2026) parsed with the
new multi-market `parse_pdf()`. No network access — pure parse from the
local cache.

| PDF | Rows | Markets found | Arrivals found | WARN (reason) |
|---|---|---|---|---|
| harti_2015-06-22.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2015-12-09.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2015-12-21.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2016-02-10.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2016-05-19.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2016-05-25.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2017-03-27.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2017-04-06.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2019-01-01.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2020-07-01.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2020-07-15.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2020-09-04.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2021-06-22.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2022-06-02.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2022-06-16.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| **harti_2022-11-26.pdf** | **0** | **NONE** | 0 | **No English veg page found** (single-page malformed-table layout — see §4, pre-existing issue) |
| harti_2023-07-21.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2023-09-18.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2023-10-05.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2023-10-11.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2024-03-09.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2024-03-21.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2024-04-28.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2025-02-02.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2025-02-16.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2025-05-23.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2026-06-24.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |
| harti_2026-06-25.pdf | 12 | Dambulla, Pettah | 0 | Narahenpita column not located |

**Totals:** 28 PDFs sampled → 27 yielded rows (96.4%), 324 total
`ParsedPrice` rows (162 Dambulla + 162 Pettah, 6 crops × 27 PDFs each), 0
Narahenpita rows, 0 arrivals populated, 28/28 PDFs logged exactly one WARN
(the expected Narahenpita-not-located warning; the one zero-row PDF logged
its own distinct WARN instead — see §4). **Zero exceptions, zero silent
column-misreads.**

A full 2019-calendar-year dry run (`ingest_harti.py --no-download --dry-run
--slice-year 2019`) was also exercised end-to-end as a smoke test outside
this 28-PDF sample: 239 PDFs → 235 yielded prices (4 skipped, same
"No English veg page found" cause as §4) → 2,814 `ParsedPrice` rows (469 per
crop × 6 crops = Dambulla + Pettah combined) → every one of the 235 PDFs
logged the same single expected Narahenpita WARN. Consistent with the
28-PDF sample at 10x the volume.

---

## 4. Anomaly: `harti_2022-11-26.pdf` — pre-existing, not a regression

This single PDF (and 3 more within the full 2019 slice-year run) produces
zero rows because `_find_english_veg_page()` cannot locate the English
vegetable table at all — logged as `"No English veg page found"`. Inspecting
its raw text:

```
Vegetable wholesale price in main markets on 26/11/2022 (Rs./kg)
Serial Item Peliyagoda Norochchole Kandy Nuwara Eliya DambullaThambuththegamaKappetipola Meegoda* Bandarawela Veyangod*
```

This is a **single-page** bulletin (not the usual 2-page English/Sinhala
pair) whose header cells are concatenated without whitespace
(`"DambullaThambuththegamaKappetipola"` — a pdfplumber table-extraction
artifact from missing cell borders in that particular PDF's underlying
Excel-to-PDF export). `_find_english_veg_page()`'s page-detection heuristic
(look for `"Dambulla"` in `row[3]` of the first 3 rows of `extract_tables()`)
does not match this layout, so the whole PDF is skipped — **exactly the
pre-existing fail-loud behaviour from before this task** (verified: this
heuristic and its skip path are untouched by the multi-market changes; only
`_locate_market_column`/`_dambulla_col_index`, which are downstream of page
detection, were modified). This is not a regression introduced by Pettah/
Narahenpita/arrivals support — it is a known, pre-existing corpus gap
(4/239 PDFs in the 2019 slice-year run, ~1.7%) that this task did not
change and was not in scope to fix.

---

## 5. Hand spot-checks (5+) — parsed value vs. raw PDF table cell

For each PDF below: `_locate_market_column()` result for Dambulla and
Pettah, the raw `Beans` row as extracted by pdfplumber, and the resulting
`ParsedPrice` values, so a human can directly verify no column swap
occurred.

### 5.1 — harti_2015-06-22.pdf (oldest format, 7 columns, "Pettah" spelling)
```
Header row 2: [None, 'Pettah\nMarket', 'Kandy\nMarket', 'Dambulla\nMarket', 'Meegoda\nMarket', 'Norochchole\nMarket', 'Thambuththegama\nMarket']
Beans raw row: ['Beans', '220.00 - 240.00', '190.00- 200.00', '190.00 - 210.00', '230.00 - 240.00', '-', '210.00 - 220.00']
```
`_locate_market_column(table, "Dambulla")` → col **3** → cell `'190.00 - 210.00'`
`_locate_market_column(table, "Pettah")`   → col **1** → cell `'220.00 - 240.00'`

Parsed: `ParsedPrice(market_name='Dambulla', min_price=190.0, max_price=210.0)`,
`ParsedPrice(market_name='Pettah', min_price=220.0, max_price=240.0)`
**Match: exact.** Dambulla ≠ Pettah (no collapse/swap).

### 5.2 — harti_2020-07-01.pdf (9 columns, "Pettah" spelling)
```
Header row 2: [None, 'Pettah\nMarket', 'Kandy\nMarket', 'Dambulla\nMarket', 'Meegoda\nMarket', ...]
Beans raw row: ['Beans', '100.00- 120.00', '90.00- 100.00', '90.00- 110.00', '140.00- 150.00', '-', '130.00- 150.00', '120.00 - 130.00', '']
```
Dambulla col **3** → `'90.00- 110.00'` → parsed (90.0, 110.0)
Pettah col **1** → `'100.00- 120.00'` → parsed (100.0, 120.0)
**Match: exact.**

### 5.3 — harti_2022-06-02.pdf (10 columns, renamed to "Peliyagoda", header-merge artifacts in later columns)
```
Header row 2: [None, 'Peliyagoda\nMarket', 'Kandy\nMarket', 'Dambulla\nMarket', 'Meegoda\nMarket', 'NorochcholeT\nMarket', 'hambuththegam\nMarket', 'aKappetipola\nMarket', ...]
Beans raw row: ['Beans', '500 - 550', '450- 480', '460- 500', '600- 600', '-', '540- 580', '500- 520', '480 530', '450 - 480', '530- 580']
```
Dambulla col **3** → `'460- 500'` → parsed (460.0, 500.0)
Pettah (via "Peliyagoda" alias) col **1** → `'500 - 550'` → parsed (500.0, 550.0)
**Match: exact.** Note the header-merge artifact ("NorochcholeT" / "hambuththegam" /
"aKappetipola" — a table-extraction glitch that bleeds one market's name into
the next column) sits in columns 4-7, well clear of the Dambulla/Pettah
columns being tested — proves the alias-matching approach is robust to this
noise because it only needs a substring match, not an exact-token match.

### 5.4 — harti_2024-03-09.pdf (10 columns, date-embedded headers, "Peliyagoda")
```
Header row 1: ['Variety', '2024-03-09', '2024-03-09', '2024-03-09', '2024-03-08', ...]
Header row 2: [None, 'Peliyagoda\n2024-03-05', 'Kandy\n2024-03-05', 'Dambulla\nMarket', 'Meegoda\nMarket', ...]
Beans raw row: ['Beans', '250- 300', '280- 300', '250- 280', '300 340', '-', '320 - 350', '230- 250', '330 - 340', '-', '280- 320']
```
Dambulla col **3** → `'250- 280'` → parsed (250.0, 280.0)
Pettah col **1** → `'250- 300'` → parsed (250.0, 300.0)
**Match: exact.** Note the Peliyagoda header cell here carries a stray date
(`'Peliyagoda\n2024-03-05'` instead of `'...\nMarket'`) — the substring match
on `"Peliyagoda"` still locates it correctly; this is another real-world
header-noise case the alias/substring approach tolerates without a false
match on Kandy or any other column.

### 5.5 — harti_2026-06-24.pdf (newest cached format, "Peliyagoda")
```
Header row 2: [None, 'Peliyagoda\nMarket', 'Kandy\nMarket', 'Dambulla\nMarket', 'Meegoda\nMarket', ...]
Beans raw row: ['Beans', '400- 500', '400- 450', '350- 400', '450- 490', '-', '400 - 480', '380- 420', '-', '350 - 420', '-']
```
Dambulla col **3** → `'350- 400'` → parsed (350.0, 400.0)
Pettah col **1** → `'400- 500'` → parsed (400.0, 500.0)
**Match: exact.**

### 5.6 — R1 regression: column-order shuffle (synthetic, mirrors `tests/test_harti_multimarket.py::TestColumnOrderShuffle`)
A synthetic table with columns deliberately reordered to
`[Variety, Meegoda, Dambulla, Narahenpita, Kandy, Pettah]` (i.e. Dambulla
moved to column 2, Pettah moved to the last column, Narahenpita inserted in
the middle) was parsed:
```
Beans raw row: ['Beans', '160.00 -165.00', '100.00 -120.00', '90.00 -110.00', '-', '80.00- 100.00']
```
`_locate_market_column` still finds: Dambulla → col 2 (100.0, 120.0),
Narahenpita → col 3 (90.0, 110.0), Pettah → col 5 (80.0, 100.0) — three
mutually distinct prices, correctly assigned regardless of position. This
proves the R1 risk (layout drift silently loading the wrong market's
numbers) cannot occur: column identity always comes from the header text
match, never from a fixed index.

---

## 6. AsOfUtc (point-in-time vintage) spot-check

`PriceObservation.AsOfUtc` is sourced from the PDF's own `/CreationDate`
metadata (the bulletin's real publication timestamp), not
`datetime.now()` and not `ObservedDate` at midnight. Spot-checked against
5 real cached PDFs:

| Observed date (bulletin "as of") | PDF `/CreationDate` (raw) | Resolved `AsOfUtc` |
|---|---|---|
| 2015-06-22 | `D:20150623101458+05'30'` | `2015-06-23T04:44:58Z` |
| 2020-07-01 | `D:20200702091748+05'30'` | `2020-07-02T03:47:48Z` |
| 2022-06-16 | `D:20220616021402-07'00'` | `2022-06-16T09:14:02Z` |
| 2025-02-02 | `D:20250202142417+05'30'` | `2025-02-02T08:54:17Z` |
| 2026-06-24 | `D:20260624141348+05'30'` | `2026-06-24T08:43:48Z` |

Notes:
- The 2015-06-22 and 2020-07-01 bulletins were both created **the day
  after** their observed date — confirms `/CreationDate` really is
  publication vintage, not a copy of the observed date (this is the whole
  point of the point-in-time contract: a naive `ObservedDate`-at-midnight
  `AsOfUtc` would have made these observations look "known" a day earlier
  than they actually were).
- 2022-06-16's PDF was created on a machine in a `-07'00'` timezone (US
  Pacific, not Sri Lanka's `+05'30'`) — real-world creator-machine variance;
  the parser's UTC conversion handles both positive and negative offsets
  correctly (unit-tested in `TestAsOfUtc::test_negative_offset_handled`).
- A 40-PDF random spot-check across the full corpus (separate from the
  28-PDF audit sample) found `/CreationDate` present and parseable on
  **40/40 (100%)** — the "missing metadata" fallback path
  (`_resolve_as_of_utc` → end-of-day UTC on `ObservedDate`, WARN-logged) is
  implemented and unit-tested but was not observed to fire on any real PDF
  in this corpus.

---

## 7. Test coverage added

`src/AgriForecast.ML/tests/test_harti_multimarket.py` (42 tests, all
hermetic — synthetic in-memory table fixtures, no network, no DB):

- `TestLocateMarketColumn` (9) — header-located columns map to the right
  market by name/alias; missing header → `None` (never a positional guess).
- `TestColumnOrderShuffle` (2) — **R1 regression**: a column-order-shuffled
  synthetic table still maps every market to its own correct column with no
  cross-contamination.
- `TestLocateArrivalsColumn` (5) — arrivals column found when header
  present (`Arrival`/`Volume` variants); `None` (not an error) when absent;
  cell parsing.
- `TestParsePdfMultiMarket` (9) — `parse_pdf()` end-to-end against synthetic
  tables (pdfplumber calls monkeypatched out, fully hermetic): all
  locatable markets emit rows; a missing market WARN-skips without killing
  the PDF; zero locatable markets skips the whole PDF; missing Dambulla
  specifically produces no positional fallback; arrivals populate/null
  correctly; `/CreationDate` is carried through; Dambulla's own values are
  bit-identical whether or not Narahenpita/arrivals columns exist.
- `TestMarketNameResolution` (6) — loader resolves `market_name` → DB
  `MarketId` by name (never a hardcoded GUID); an unresolved name is
  WARN-skipped, never invented; market map is built once per run, not per
  row; pins the exact seeded DB `Markets.Name` strings.
- `TestAsOfUtc` (8) — `/CreationDate` parses to the correct UTC instant
  (including negative offsets and `Z`-suffixed dates); missing/garbage
  input falls back to end-of-day UTC on `ObservedDate`, which is proven to
  never be earlier than the observed date (the leakage-guard invariant).
- `TestDambullaBackCompat` (3) — the legacy `upsert_harti_prices()` →
  `MarketPrices` path filters to Dambulla-only and is otherwise byte-for-byte
  unaffected by the new multi-market rows.

`src/AgriForecast.ML/tests/test_harti_splice.py` (extended, +6 net tests,
41 total in the file) — the pre-existing `TestParserOnCachedPDF` class
(parses the real cached `harti_2019-01-01.pdf`) was updated for the new
multi-market contract: added `test_dambulla_six_crops_returned` (R1
regression: the Dambulla-only slice is still exactly 6 rows, bit-for-bit
matching pre-multi-market behaviour), `test_pettah_beans_differs_from_
dambulla_no_column_swap` (R1 regression: real-PDF proof that Dambulla and
Pettah Beans prices differ), and `test_narahenpita_absent_in_2019_format_
warn_skip_not_guessed`; the two assertions that assumed Dambulla-only
output (`test_six_crops_returned`, `test_no_duplicate_canonical_labels_per_
pdf`) were updated to the new (correct) 12-row / per-market-dedup contract.

**Full suite:** `.venv/bin/python -m pytest -q` → **233 passed, 17 failed**
(all 17 failures are the pre-existing `tests/test_phase3.py` test-isolation
flake noted in the task brief — unrelated to this change; baseline was 188
passed + the same 17 flakes). Net new passing tests from this task: **45**
(42 in the new file + 3 net additions in the extended splice file, after
accounting for 2 assertions that were corrected rather than merely added
to).

---

## 8. R1.1 P2 (ClickUp 86cahef44) — Thambuttegama and Keppetipola added

**Date of this extension:** 2026-07-02 (same day, follow-up task).
**Scope:** implement the HARTI-PDF fallback recommendation from
`DEC_portal_probe_2026-07.md` — wire Thambuttegama and Keppetipola into
`_TARGET_MARKETS` / `_MARKET_HEADER_ALIASES` (`parser.py`) and
`_PARSER_MARKET_TO_DB_NAME` (`loader.py`), extend test coverage, and confirm
against the live DB.

### 8.1 DB name verification (live query)

Queried the live `Markets` table directly (creds via
`src/AgriForecast.ML/.env`) rather than assuming the migration file is
still what's actually deployed:

```
Id=b2a20001-...-000000000002  MarketCode=MKT00000002  Name='Keppetipola Dedicated Economic Centre'   District='Badulla'       MarketType=2  IsActive=True
Id=b2a20001-...-000000000003  MarketCode=MKT00000003  Name='Thambuttegama Dedicated Economic Centre'  District='Anuradhapura'  MarketType=2  IsActive=True
```

Both rows use their plain DEC name (no `"(HARTI wholesale)"`-style suffix,
matching Dambulla's own pattern — they are first-class DEC markets, not
HARTI-only aliases of a market with other sources) — confirmed and pinned
in `loader._PARSER_MARKET_TO_DB_NAME` and a dedicated regression test
(`test_seeded_market_names_for_new_markets_match_migration`).

### 8.2 Header alias evidence — spelling variants across the corpus

Re-scanned the raw header row (`table[1]`) of all 28 PDFs in the original
stratified sample (§3) plus a handful of extra binary-search PDFs used to
pin the exact Keppetipola introduction date. Findings:

**Thambuttegama** — column exists in the table from the very first cached
PDF (`harti_2015-06-22.pdf`, the original 7-column format). Observed
spellings over 11 years, all now in `_MARKET_HEADER_ALIASES["Thambuttegama"]`:

| Spelling | Observed window (sample) |
|---|---|
| `Thambuththegama` | 2015-06-22 → (at least) 2017-01-31, then again 2025-02-02 → 2026-06-25 |
| `T'thegama` | 2017-03-08 → 2017-04-06 only (abbreviated header format) |
| `Thambuththegam` | 2019-01-01 (cell-split artifact: the trailing "a" bleeds into the NEXT cell, producing "a Kappetipola") |
| `hambuththegam` | 2022-06-02 → 2024-04-28 (cell-bleed artifact: the leading "T" bleeds BACKWARD into the Norochchole cell, producing "NorochcholeT" — a pdfplumber table-extraction quirk from missing cell borders in that era's PDF export, not a HARTI relabelling) |

**Keppetipola** — column does NOT exist in the original 7-column format.
Binary-searched the exact introduction date using cached PDFs:
`harti_2017-03-01.pdf` (7 columns, no Keppetipola) →
`harti_2017-03-07.pdf` (7 columns, no Keppetipola) →
**`harti_2017-03-08.pdf` (9 columns, "Kappetipola" present)**. So the
column is introduced precisely between **2017-03-07 and 2017-03-08**.
Observed spellings from that point on, all now in
`_MARKET_HEADER_ALIASES["Keppetipola"]`:

| Spelling | Observed window (sample) |
|---|---|
| `Kappetipola` | 2017-03-08 → 2022-06-16 (missing leading "e" — this was HARTI's own spelling for ~5 years, not a parse error) |
| `aKappetipola` | 2022-06-02 → 2022-06-16 (cell-bleed variant of the above; the "a" bled in from the Thambuttegama cell, same NorochcholeT-family artifact) |
| `aKeppetipola` | 2023-07-21 → 2024-04-28 (cell-bleed variant; HARTI corrected the spelling to "Keppetipola" mid-corpus but the bleed-through "a" persisted) |
| `Keppetipola` | 2025-02-02 → 2026-06-25 (clean, corrected spelling, bleed artifact gone) |

### 8.3 Substring-safety verification

Re-ran the pairwise substring-safety script (same method as the original
probe) across the FULL alias set (existing + new) and against every other
market's header text seen in the bulletin (`Kandy`, `Meegoda`,
`Norochchole`/`NorochcholeT`, `Nuwaraeliya`, `Bandarawela`, `Veyangoda`):

- **Zero cross-market collisions.** Every substring relationship found is
  within the same market's own alias family (e.g. `"Kappetipola"` is a
  substring of `"aKappetipola"` — both map to Keppetipola, which is safe by
  construction since `_locate_market_column` only needs ONE alias per
  market to hit).
- No new alias matches any non-target market header (`Kandy`, `Meegoda`,
  `Norochchole`, `NorochcholeT`, `Nuwaraeliya`, `Bandarawela`, `Veyangoda`).
- This is now a permanent regression test
  (`test_no_new_alias_is_substring_of_a_different_markets_alias`,
  `test_new_aliases_do_not_match_other_bulletin_headers` in
  `test_harti_multimarket.py`), not just a one-off check.

### 8.4 28-PDF stratified sample re-run (same PDFs as §3) — per-market row counts

Re-ran the exact same 28-PDF sample through the extended `parse_pdf()`:

| Market | Total rows (28-PDF sample) | Notes |
|---|---|---|
| Dambulla | 162 (6/PDF × 27 parseable PDFs) | unchanged from §3 |
| Pettah | 162 (6/PDF × 27 parseable PDFs) | unchanged from §3 |
| Narahenpita | 0 | unchanged — column absent corpus-wide (§1/§2) |
| **Thambuttegama** | **162** (6/PDF × 27 parseable PDFs) | **full 6/6 crop coverage on every single sampled PDF** — this market has data for every target crop whenever its column exists |
| **Keppetipola** | **36** (2/PDF on 18 of 27 PDFs, 0/PDF on 9 PDFs — see below) | column located from 2017-03-08 onward, but only **Beans** and **Capsicum** consistently carry price data in this 28-PDF sample; the other 4 target crops (Ladies Fingers, Bitter Gourd, Snake Gourd, Luffa) show `-` (market closed / no data) at Keppetipola on every sampled date, including in the pre-2017 PDFs where the column doesn't exist at all |

`harti_2022-11-26.pdf` still contributes 0 rows to every market (the
pre-existing, unrelated "No English veg page found" issue from §4 — not
touched by this change).

**Important distinction proven by this re-run (and now test-covered, see
§8.6):** a market's column can be **located but empty** (Keppetipola on
most crops/dates: header found, cell is `-`) versus **not located at all**
(Keppetipola on any pre-2017-03-08 PDF: header genuinely absent). Both
correctly produce zero output rows for that (market, crop, date), but only
the second logs a WARN — the first is legitimate "market closed that day,"
not a parsing failure, and does not need a WARN (mirrors the existing
zero-price/`-`-cell handling for every other market).

### 8.5 Hand spot-checks — 4 total, 2 per new market, different years

**Thambuttegama — 2015 (7-column format) vs 2026 (10-column format):**

`harti_2015-06-22.pdf`, header `[..., 'Dambulla\nMarket', 'Meegoda\nMarket', 'Norochchole\nMarket', 'Thambuththegama\nMarket']` → Thambuttegama col **6**:
```
Beans raw row:     [..., '190.00 - 210.00' (Dambulla, col 3), ..., '210.00 - 220.00' (Thambuttegama, col 6)]
Capsicum raw row:  [..., '160.00 - 180.00' (Dambulla, col 3), ..., '170.00 - 190.00' (Thambuttegama, col 6)]
```
Parsed: `Thambuttegama/Beans (210.0, 220.0)`, `Thambuttegama/Capsicum (170.0, 190.0)`. **Match: exact.** Both differ from Dambulla's own values in the same row — no column swap.

`harti_2026-06-24.pdf`, header `[..., 'Dambulla\nMarket', 'Meegoda\nMarket', 'Norochchole\nMarket', 'Thambuththegama\nMarket', 'Keppetipola\nMarket', ...]` → Thambuttegama col **6**:
```
Beans raw row:     [..., '350- 400' (Dambulla, col 3), ..., '400 - 480' (Thambuttegama, col 6), '380- 420' (Keppetipola, col 7), ...]
Capsicum raw row:  [..., '280- 320' (Dambulla, col 3), ..., '280 - 350' (Thambuttegama, col 6), '250- 300' (Keppetipola, col 7), ...]
```
Parsed: `Thambuttegama/Beans (400.0, 480.0)`, `Thambuttegama/Capsicum (280.0, 350.0)`. **Match: exact.** Distinct from both Dambulla and Keppetipola in the same row.

**Keppetipola — 2017 (first appearance, "Kappetipola" spelling) vs 2026 (clean "Keppetipola" spelling):**

`harti_2017-03-27.pdf`, header `[..., 'Dambulla\nMarket', 'Meegoda\nMarket', 'Norochchole\nMarket', "T'thegama\nMarket", 'Kappetipola\nMarket', 'Nuwaraeliya\nMarket']` → Keppetipola col **7**:
```
Beans raw row:     [..., '130.00 -150.00' (Dambulla, col 3), ..., '150.00 -170.00' (Keppetipola, col 7)]
Capsicum raw row:  [..., '160.00 -180.00' (Dambulla, col 3), ..., '160.00 -170.00' (Keppetipola, col 7)]
```
Parsed: `Keppetipola/Beans (150.0, 170.0)`, `Keppetipola/Capsicum (160.0, 170.0)`. **Match: exact.** Note Capsicum's Dambulla (160-180) and Keppetipola (160-170) overlap but are not identical ranges — correctly read from two distinct cells, not a coincidental swap.

`harti_2026-06-24.pdf` (same PDF as the Thambuttegama 2026 spot-check above), header `[..., 'Thambuththegama\nMarket', 'Keppetipola\nMarket', ...]` → Keppetipola col **7**:
```
Beans raw row:     [..., '350- 400' (Dambulla, col 3), '400 - 480' (Thambuttegama, col 6), '380- 420' (Keppetipola, col 7), ...]
Capsicum raw row:  [..., '280- 320' (Dambulla, col 3), '280 - 350' (Thambuttegama, col 6), '250- 300' (Keppetipola, col 7), ...]
```
Parsed: `Keppetipola/Beans (380.0, 420.0)`, `Keppetipola/Capsicum (250.0, 300.0)`. **Match: exact.** All three markets (Dambulla/Thambuttegama/Keppetipola) give three distinct Beans prices and three distinct Capsicum prices from the same row — proves no pairwise column collision on a real, current-format PDF.

### 8.6 Coverage window summary (precise)

| Market | Column exists from | Column exists to (end of corpus) | Crop-level data coverage |
|---|---|---|---|
| Thambuttegama | 2015-06-22 (start of cached corpus — may exist earlier, not verifiable from this cache) | 2026-06-27 (end of cached corpus) | **Full — all 6 target crops populated on every sampled PDF** where the column exists (162/162 in the 28-PDF sample) |
| Keppetipola | 2017-03-08 (pinned exactly: absent 2017-03-07, present 2017-03-08) | 2026-06-27 (end of cached corpus) | **Partial — only Beans and Capsicum consistently populated** in the 28-PDF sample (36 rows = 2 crops × 18 of 27 parseable post-2017 PDFs; the other 4 target crops show `-`/no-data at Keppetipola on every sampled date). This is a genuine market-activity gap (few crops trade through Keppetipola), not a parsing limitation — the column is correctly located every time it exists. |

**Implication for the ML/feature layer:** Keppetipola should be treated as
thin/cold-start for 4 of the 6 target crops even after this extension — the
column exists but rarely carries data for Ladies Fingers, Bitter Gourd,
Snake Gourd, or Luffa in the sample checked here. A full-corpus (not just
28-PDF sample) crop-level coverage count for Keppetipola would be needed
before deciding whether to route those crop×market combinations to a
fallback — flagged here for whoever picks up the feature-store wiring, not
resolved in this task.

### 8.7 Test coverage added (this extension)

`tests/test_harti_multimarket.py` — extended from 42 to **68 tests**
(+26 net): `TestColumnOrderShuffle` extended with a 5-market shuffled-table
case (`test_five_markets_map_to_distinct_columns_regardless_of_order`,
`test_five_markets_prices_are_pairwise_distinct_no_cross_contamination`);
new `TestThambuttegamaKeppetipola` class (24 tests) covering every real
header spelling variant from §8.2 for both markets, substring-safety
(§8.3) as permanent regression tests, the pre-2017 Keppetipola
missing-column WARN-skip behaviour via the real `parse_pdf()` entrypoint,
end-to-end parsing against the current clean 2025-format header, and
loader market-name resolution (happy path + unresolved-market WARN-skip +
the live-DB-verified name pin from §8.1).

`tests/test_harti_splice.py` — the real-PDF `TestParserOnCachedPDF` class
against `harti_2019-01-01.pdf` updated: `test_six_crops_returned` corrected
from 12 rows (2 markets) to **18 rows (3 markets: Dambulla, Pettah,
Thambuttegama)** — this specific PDF's Keppetipola column IS located (col
7, "a Kappetipola" spelling) but every one of the 6 target crops shows `-`
at that column on 2019-01-01, so it legitimately contributes zero rows;
added `test_keppetipola_column_located_but_empty_this_pdf` to test-cover
that exact located-but-empty distinction explicitly. Net: **41 → 42 tests**
in the file (one existing assertion corrected in place for the new
expected row/market count, one new test added).

**Full suite after this extension:**
`.venv/bin/python -m pytest -q` → **260 passed, 17 failed** (same 17
pre-existing `tests/test_phase3.py` test-isolation flakes, reproduced in
isolation and confirmed unrelated to HARTI/market code). Net new passing
tests from this extension: **27** (26 in `test_harti_multimarket.py` + 1
net in `test_harti_splice.py`), on top of the 233 passing baseline from
§7 (233 + 27 = 260, matches exactly).

### 8.8 Live-DB sanity check

See the ClickUp task report for the full live-DB upsert/idempotency/cleanup
run (2-3 real cached PDFs upserted via `upsert_harti_price_observations()`
against the live SQL Server instance, verified landing under the correct
new `MarketId`s, re-run for idempotency, then deleted and confirmed
`PriceObservations` back to the pre-run row count) — not duplicated here to
avoid this audit file drifting from the actual DB state; the procedure and
evidence are recorded in the task's own report output.
