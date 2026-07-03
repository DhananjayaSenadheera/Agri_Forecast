"""HARTI price backfill subpackage.

Components:
  downloader  -- scrape + cache PDFs from harti.gov.lk
  parser      -- dual-format pdfplumber parser (auto-detects English veg page)
  loader      -- CropId resolution + splice rule + idempotent DB upsert
  qa          -- row counts, gap report, zero-duplicate assertion

Crop-alias resolution (source-agnostic) and unit-quarantine constants live in
``..canonical`` (agriforecast_ml/canonical.py), not here -- loader.py
consumes that module rather than re-implementing alias matching, since
future non-HARTI source writers need the same resolver.
"""
