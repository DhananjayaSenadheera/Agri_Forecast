"""HARTI price backfill subpackage.

Components:
  downloader  -- scrape + cache PDFs from harti.gov.lk
  parser      -- dual-format pdfplumber parser (auto-detects English veg page)
  loader      -- CropId resolution + splice rule + idempotent DB upsert
  qa          -- row counts, gap report, zero-duplicate assertion
"""
