"""Regression tests for the HARTI PDF download size cap.

_download_capped() streams the response in 64 KB chunks, rejects early when the declared
Content-Length exceeds the 25 MB cap, and also enforces the cap against the actual
streamed bytes, since Content-Length can be absent, wrong or hostile. Exceeding it raises
PdfTooLargeError, which the download loop treats like any other per-URL failure.

Covered: a normal PDF, an oversized declared length (body never read), an oversized stream
with no header, a lying header, a garbage header, the exact boundary either side, empty
keep-alive chunks, and loop resilience.

Network-free: the session and its response are mocked.
"""
from __future__ import annotations

import sys
from datetime import date
from pathlib import Path
from unittest.mock import MagicMock

import pytest

# Path setup -- allow `import agriforecast_ml` from the ML project root.
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml.harti.downloader import (  # noqa: E402
    _download_capped,
    _MAX_PDF_BYTES,
    _STREAM_CHUNK_BYTES,
    PdfTooLargeError,
    download_pdfs,
)
from agriforecast_ml import netguard  # noqa: E402


@pytest.fixture(autouse=True)
def _no_dns(monkeypatch):
    """Keep this suite network-free.

    The download path now routes through the SSRF guard (guarded_get /
    assert_url_allowed), whose private-IP check resolves the host via DNS. All
    URLs in this file use the allowlisted ``www.harti.gov.lk`` host; stub its
    resolution to a public IP so no real DNS lookup happens and the size-cap
    behaviour under test is exercised exactly as before.
    """
    monkeypatch.setattr(netguard, "_resolve_ips", lambda host: ["8.8.8.8"])


# Test helpers

def _chunks_from_bytes(data: bytes, chunk_size: int = _STREAM_CHUNK_BYTES):
    """Split ``data`` into a list of chunks the way requests' iter_content would."""
    return [data[i:i + chunk_size] for i in range(0, len(data), chunk_size)] or [b""]


def _make_mock_response(body: bytes, content_length=None, chunk_size=_STREAM_CHUNK_BYTES,
                          extra_chunks=None):
    """Build a MagicMock standing in for a `requests.Response` used as a
    context manager (``with session.get(...) as resp:``), with a streaming
    ``iter_content`` that yields chunks of ``body`` (plus optional extra
    keep-alive/empty chunks interleaved at the front).
    """
    resp = MagicMock()
    resp.__enter__ = MagicMock(return_value=resp)
    resp.__exit__ = MagicMock(return_value=False)
    resp.raise_for_status = MagicMock(return_value=None)
    # guarded_get inspects these to decide whether to follow a redirect; every
    # response in this suite is a final (non-redirect) 2xx.
    resp.is_redirect = False
    resp.is_permanent_redirect = False

    headers = {}
    if content_length is not None:
        headers["Content-Length"] = content_length
    resp.headers = headers

    chunks = list(extra_chunks or []) + _chunks_from_bytes(body, chunk_size)
    resp.iter_content = MagicMock(return_value=iter(chunks))
    return resp


def _make_session(mock_response):
    session = MagicMock()
    session.get = MagicMock(return_value=mock_response)
    return session


# 1. Normal small PDF -- unchanged behaviour

class TestNormalDownload:
    def test_small_pdf_returns_full_bytes_unchanged(self):
        body = b"%PDF-1.4 fake pdf content" * 100  # a few KB
        resp = _make_mock_response(body, content_length=str(len(body)))
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/x.pdf")

        assert result == body, "Returned bytes must exactly match the streamed body"
        assert isinstance(result, bytes)

    def test_session_get_called_with_stream_true_and_timeout(self):
        body = b"small body"
        resp = _make_mock_response(body, content_length=str(len(body)))
        session = _make_session(resp)

        _download_capped(session, "https://www.harti.gov.lk/eng/x.pdf")

        assert session.get.called
        _, kwargs = session.get.call_args
        assert kwargs.get("stream") is True, "Must stream the response, not buffer it whole"
        assert kwargs.get("timeout") == 60

    def test_raise_for_status_called(self):
        body = b"small body"
        resp = _make_mock_response(body, content_length=str(len(body)))
        session = _make_session(resp)

        _download_capped(session, "https://www.harti.gov.lk/eng/x.pdf")

        resp.raise_for_status.assert_called_once()


# 2. Content-Length header exceeds cap -- reject WITHOUT reading body

class TestContentLengthPreCheck:
    def test_oversized_content_length_raises_without_consuming_body(self):
        # iter_content would yield real data if consumed -- but it must never be read.
        oversized_len = _MAX_PDF_BYTES + 1
        resp = _make_mock_response(b"x" * 1000, content_length=str(oversized_len))
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError):
            _download_capped(session, "https://www.harti.gov.lk/eng/huge.pdf")

        resp.iter_content.assert_not_called(), (
            "Body must not be read once Content-Length already exceeds the cap"
        )

    def test_oversized_content_length_error_message_mentions_cap(self):
        oversized_len = _MAX_PDF_BYTES + 1
        resp = _make_mock_response(b"x", content_length=str(oversized_len))
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError, match=str(_MAX_PDF_BYTES)):
            _download_capped(session, "https://www.harti.gov.lk/eng/huge.pdf")


# 3. Content-Length absent, streamed body exceeds cap

class TestStreamedGuardNoHeader:
    def test_no_content_length_large_stream_raises(self):
        body = b"y" * (_MAX_PDF_BYTES + _STREAM_CHUNK_BYTES)
        resp = _make_mock_response(body, content_length=None)
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError):
            _download_capped(session, "https://www.harti.gov.lk/eng/nolen.pdf")

    def test_no_content_length_small_stream_succeeds(self):
        body = b"z" * 1000
        resp = _make_mock_response(body, content_length=None)
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/small.pdf")
        assert result == body


# 4. Content-Length LIES (small header, oversized actual stream)

class TestContentLengthLies:
    def test_small_header_large_actual_stream_raises(self):
        body = b"a" * (_MAX_PDF_BYTES + _STREAM_CHUNK_BYTES)
        # Header claims a tiny, well-under-cap size -- must not be trusted.
        resp = _make_mock_response(body, content_length="1000")
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError):
            _download_capped(session, "https://www.harti.gov.lk/eng/lying.pdf")

    def test_lying_header_does_not_prevent_the_streamed_guard_from_firing(self):
        """Even a header of '0' (as small as it gets) must not bypass the
        streamed-byte enforcement."""
        body = b"b" * (_MAX_PDF_BYTES + 1)
        resp = _make_mock_response(body, content_length="0")
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError):
            _download_capped(session, "https://www.harti.gov.lk/eng/zero-header.pdf")


# 5. Garbage Content-Length header -- ignored, falls through to stream guard

class TestGarbageContentLength:
    def test_non_numeric_header_ignored_normal_download_succeeds(self):
        body = b"c" * 2000
        resp = _make_mock_response(body, content_length="not-a-number")
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/garbage.pdf")

        assert result == body, (
            "Malformed Content-Length must be ignored, not crash or reject a normal body"
        )

    def test_non_numeric_header_still_enforces_streamed_cap(self):
        """A garbage header must not disable enforcement -- oversized bodies are
        still rejected via the streamed-byte guard."""
        body = b"d" * (_MAX_PDF_BYTES + 1)
        resp = _make_mock_response(body, content_length="NaN")
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError):
            _download_capped(session, "https://www.harti.gov.lk/eng/garbage-big.pdf")

    def test_empty_string_header_ignored(self):
        body = b"e" * 500
        resp = _make_mock_response(body, content_length="")
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/empty-header.pdf")
        assert result == body


# 6. Exact boundary behaviour

class TestBoundary:
    def test_body_exactly_at_cap_is_allowed(self):
        body = b"f" * _MAX_PDF_BYTES
        resp = _make_mock_response(body, content_length=str(_MAX_PDF_BYTES))
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/exact.pdf")
        assert len(result) == _MAX_PDF_BYTES
        assert result == body

    def test_body_one_byte_over_cap_is_rejected(self):
        body = b"g" * (_MAX_PDF_BYTES + 1)
        # Omit Content-Length so only the streamed guard is exercised at the boundary.
        resp = _make_mock_response(body, content_length=None)
        session = _make_session(resp)

        with pytest.raises(PdfTooLargeError):
            _download_capped(session, "https://www.harti.gov.lk/eng/over-by-one.pdf")

    def test_content_length_exactly_at_cap_is_allowed(self):
        """Content-Length header exactly equal to the cap must pass the
        pre-check (only strictly-greater-than is rejected)."""
        body = b"h" * 1000
        resp = _make_mock_response(body, content_length=str(_MAX_PDF_BYTES))
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/hdr-exact.pdf")
        assert result == body


# 7. Empty keep-alive chunks skipped without error

class TestEmptyChunks:
    def test_empty_chunks_interleaved_are_skipped(self):
        body = b"i" * 500
        resp = _make_mock_response(
            body, content_length=None,
            extra_chunks=[b"", b"", b""],
        )
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/keepalive.pdf")
        assert result == body, "Empty chunks must not corrupt or truncate the assembled body"

    def test_all_empty_chunks_yields_empty_bytes(self):
        resp = _make_mock_response(b"", content_length=None, extra_chunks=[b"", b""])
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/allempty.pdf")
        assert result == b""

    def test_empty_chunks_do_not_count_toward_the_cap(self):
        """A stream of many empty chunks followed by a body just at the cap
        must not falsely trip the guard by miscounting empties."""
        body = b"j" * _MAX_PDF_BYTES
        resp = _make_mock_response(
            body, content_length=None,
            extra_chunks=[b""] * 50,
        )
        session = _make_session(resp)

        result = _download_capped(session, "https://www.harti.gov.lk/eng/empties-then-exact.pdf")
        assert len(result) == _MAX_PDF_BYTES


# 8. Loop resilience -- oversize PDF is a logged failure, crawl continues

class TestDownloadLoopResilience:
    """Exercise download_pdfs() end-to-end (with a fake session) to prove
    PdfTooLargeError is caught by the existing per-URL try/except and does
    not abort the crawl."""

    def _links(self):
        return [
            (date(2024, 1, 1), "assets/pdf/food_price/daily/eng/oversize.pdf"),
            (date(2024, 1, 2), "assets/pdf/food_price/daily/eng/ok.pdf"),
        ]

    def _make_multi_url_session(self, responses_by_marker):
        """responses_by_marker: dict[str marker substr -> MagicMock response].
        Returns a session whose .get(url, ...) picks a response based on which
        marker substring appears in the URL.
        """
        session = MagicMock()

        def _get(url, headers=None, timeout=None, stream=None, allow_redirects=None):
            for marker, resp in responses_by_marker.items():
                if marker in url:
                    return resp
            raise AssertionError(f"Unexpected URL in test: {url}")

        session.get = MagicMock(side_effect=_get)
        return session

    def test_oversize_pdf_logged_as_failure_crawl_continues(self, tmp_path, caplog):
        oversized_body = b"x" * (_MAX_PDF_BYTES + 1)
        ok_body = b"%PDF-1.4 real content" * 100  # > 500 bytes, passes size-sanity check

        oversize_resp = _make_mock_response(oversized_body, content_length=None)
        ok_resp = _make_mock_response(ok_body, content_length=str(len(ok_body)))

        session = self._make_multi_url_session({
            "oversize.pdf": oversize_resp,
            "ok.pdf": ok_resp,
        })

        cache_dir = tmp_path / "harti_cache"
        result = download_pdfs(cache_dir, self._links(), session=session, delay=0)

        # The oversize URL must NOT be in the successfully cached results...
        cached_dates = {d for d, _ in result}
        assert "2024-01-01" not in cached_dates, (
            "Oversize PDF must not be recorded as successfully cached"
        )
        # ...while the second, normal URL still succeeds -- the loop did not abort.
        assert "2024-01-02" in cached_dates, (
            "Loop must continue past an oversize failure and still download the next URL"
        )

    def test_oversize_pdf_does_not_write_a_partial_file(self, tmp_path):
        oversized_body = b"x" * (_MAX_PDF_BYTES + 1)
        oversize_resp = _make_mock_response(oversized_body, content_length=None)
        session = self._make_multi_url_session({"oversize.pdf": oversize_resp})

        cache_dir = tmp_path / "harti_cache"
        links = [(date(2024, 1, 1), "assets/pdf/food_price/daily/eng/oversize.pdf")]
        download_pdfs(cache_dir, links, session=session, delay=0)

        expected_path = cache_dir / "harti_2024-01-01.pdf"
        assert not expected_path.exists(), (
            "No file should be written to disk for a download that exceeded the cap"
        )

    def test_no_exception_escapes_download_pdfs(self, tmp_path):
        """The overall guarantee: PdfTooLargeError raised inside the try block
        must never propagate out of download_pdfs()."""
        oversized_body = b"x" * (_MAX_PDF_BYTES + 1)
        oversize_resp = _make_mock_response(oversized_body, content_length=None)
        session = self._make_multi_url_session({"oversize.pdf": oversize_resp})

        cache_dir = tmp_path / "harti_cache"
        links = [(date(2024, 1, 1), "assets/pdf/food_price/daily/eng/oversize.pdf")]

        try:
            result = download_pdfs(cache_dir, links, session=session, delay=0)
        except PdfTooLargeError:
            pytest.fail("PdfTooLargeError must be caught inside download_pdfs(), not escape it")

        assert result == [], "No PDFs should be cached when the only link is oversized"

    def test_all_links_oversize_returns_empty_list_not_error(self, tmp_path):
        oversized_body = b"x" * (_MAX_PDF_BYTES + 1)
        resp1 = _make_mock_response(oversized_body, content_length=None)
        resp2 = _make_mock_response(oversized_body, content_length=None)
        session = self._make_multi_url_session({
            "oversize.pdf": resp1,
            "ok.pdf": resp2,
        })
        # Re-point "ok.pdf" marker to also be oversized for this test.
        links = self._links()

        cache_dir = tmp_path / "harti_cache"
        result = download_pdfs(cache_dir, links, session=session, delay=0)

        assert result == []
