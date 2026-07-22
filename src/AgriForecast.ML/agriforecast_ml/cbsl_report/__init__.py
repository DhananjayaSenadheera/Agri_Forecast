"""CBSL Daily Price Report ingestion (capture-only, 2026-07-22).

Mirrors the ``harti`` package layout: downloader -> parser -> loader, with the
PDF parse living HERE (Python single-source-of-truth rule) and the .NET
CbslPriceReportIngestionService orchestrating over the /admin/ingest-cbsl
FastAPI seam.
"""
