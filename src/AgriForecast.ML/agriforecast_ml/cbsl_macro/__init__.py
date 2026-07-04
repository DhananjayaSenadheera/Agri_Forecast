"""CBSL macro-indicator ingestion subpackage (ClickUp 86cahefbh, P3).

Components:
  downloader  -- scrape + cache PDFs from cbsl.gov.lk (CCPI press releases +
                 Monthly Economic Indicators packs), harti/downloader.py-style
  parser      -- pdfplumber labeled-line regex extractor (NOT table
                 extraction -- see parser.py docstring for why)
  loader      -- idempotent upsert into MacroSeriesPoints, keyed on the full
                 (SeriesCode, ReferenceDate, PublishedAt) vintage triple

Vintage/leakage discipline (2026-07-04 P3 decision, DECISIONS.md):
  PublishedAt is NEVER defaulted to ReferenceDate. Preferred order per series:
  PDF /CreationDate -> listing-page date -> conservative-LATE imputation
  (ReferenceDate + a per-series lag prior), flagged IsPublishedAtImputed=True.
  Erring late only delays a downstream join; erring early leaks the future.

Scope (2026-07-04 P3 user-approved cuts):
  NCPI dropped (DCS host stays off the SSRF allowlist). DIESEL_PRICE_LKR
  dropped (CPC-owned, not CBSL). agri_production_idx dropped (no monthly
  national index exists in either corpus -- probe-conditional, FAILED).
  Final series set: CCPI headline index + official food inflation Y-o-Y
  (press releases), food-imports Y-o-Y (MEI), policy rate (MEI interest-rate
  section -- adjudicated IN, see parser.py for why it is clean-labeled).
"""
