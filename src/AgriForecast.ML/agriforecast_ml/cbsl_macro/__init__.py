"""CBSL macro-indicator ingestion subpackage.

downloader scrapes and caches PDFs from cbsl.gov.lk (CCPI press releases and Monthly
Economic Indicators packs). parser is a labeled-line regex extractor rather than a table
extractor - see parser.py for why. loader upserts into MacroSeriesPoints, keyed on the
full (SeriesCode, ReferenceDate, PublishedAt) vintage triple.

PublishedAt is NEVER defaulted to ReferenceDate. Preferred order: the PDF /CreationDate,
then a listing-page date, then conservative-LATE imputation (ReferenceDate plus a
per-series lag prior) flagged with IsPublishedAtImputed. Erring late only delays a
downstream join; erring early leaks the future.

Series set: the CCPI headline index and official food inflation YoY from the press
releases, plus food-imports YoY and the policy rate from the MEI packs.
"""
