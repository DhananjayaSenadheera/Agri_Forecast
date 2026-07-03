"""Security regression tests for the SSRF outbound-fetch guard (ClickUp
86cahef8f / S1) in ``agriforecast_ml.netguard`` and its wiring into the HARTI
downloader and the news fetcher.

What S1 added
-------------
The HARTI listing scraper and the news RSS fetcher follow URLs that ultimately
come from remote content. ``netguard`` is the single choke-point enforcing:

  1. Scheme allowlist (http/https only — never file:/ftp:/gopher:).
  2. Host allowlist (exact host or parent-domain suffix; nothing else).
  3. Private/loopback/link-local IP block (resolve-then-check, so a DNS answer
     of 127.0.0.1 / 169.254.169.254 / 10.x for an allowlisted host is rejected).

Redirects are NOT auto-followed; ``guarded_get`` re-validates every ``Location``
through the same three controls before following it manually.

All tests are network-free: ``socket.getaddrinfo`` and ``session.get`` are
mocked. No real DNS or HTTP happens.
"""
from __future__ import annotations

import sys
from pathlib import Path
from unittest.mock import MagicMock

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import netguard  # noqa: E402
from agriforecast_ml.netguard import (  # noqa: E402
    DisallowedUrlError,
    assert_host_allowed,
    assert_url_allowed,
    guarded_get,
)


# ---------------------------------------------------------------------------
# Helpers: mock DNS resolution so tests never touch the network.
# ---------------------------------------------------------------------------

def _patch_resolve(monkeypatch, ip: str):
    """Make every host resolve to ``ip`` (single A record)."""
    monkeypatch.setattr(netguard, "_resolve_ips", lambda host: [ip])


def _make_response(*, redirect_to=None, status=200):
    resp = MagicMock()
    resp.is_redirect = redirect_to is not None
    resp.is_permanent_redirect = False
    resp.headers = {"Location": redirect_to} if redirect_to else {}
    resp.close = MagicMock()
    resp.status_code = status
    return resp


# ===========================================================================
# assert_host_allowed — offline scheme + host allowlist (no DNS)
# ===========================================================================

class TestHostAllowlist:
    def test_allowlisted_exact_host_passes(self):
        assert_host_allowed("https://harti.gov.lk/daily-price.php")

    def test_allowlisted_subdomain_passes(self):
        assert_host_allowed("https://www.harti.gov.lk/assets/x.pdf")

    def test_offlist_host_blocked(self):
        with pytest.raises(DisallowedUrlError):
            assert_host_allowed("https://evil.example.com/x")

    def test_lookalike_suffix_not_confused_for_allowlisted(self):
        # "notharti.gov.lk" must NOT match allowlist entry "harti.gov.lk".
        with pytest.raises(DisallowedUrlError):
            assert_host_allowed("https://evilharti.gov.lk/x")

    def test_allowlisted_host_as_prefix_of_attacker_domain_blocked(self):
        with pytest.raises(DisallowedUrlError):
            assert_host_allowed("https://harti.gov.lk.attacker.com/x")

    def test_non_http_scheme_blocked(self):
        for url in (
            "file:///etc/passwd",
            "ftp://harti.gov.lk/x",
            "gopher://harti.gov.lk/x",
        ):
            with pytest.raises(DisallowedUrlError):
                assert_host_allowed(url)

    def test_url_without_host_blocked(self):
        with pytest.raises(DisallowedUrlError):
            assert_host_allowed("https:///no-host")


# ===========================================================================
# assert_url_allowed — private/loopback/link-local IP block (resolve-then-check)
# ===========================================================================

class TestPrivateIpBlock:
    def test_public_ip_for_allowlisted_host_passes(self, monkeypatch):
        _patch_resolve(monkeypatch, "8.8.8.8")
        assert_url_allowed("https://harti.gov.lk/x")

    @pytest.mark.parametrize("ip", [
        "127.0.0.1",          # loopback
        "10.0.0.5",           # RFC1918 private
        "192.168.1.10",       # RFC1918 private
        "172.16.5.4",         # RFC1918 private
        "169.254.169.254",    # link-local — cloud metadata
        "0.0.0.0",            # unspecified
    ])
    def test_allowlisted_host_resolving_to_private_ip_is_blocked(self, monkeypatch, ip):
        # DNS rebinding / poisoned answer: host is on the allowlist but resolves
        # to an internal address — must still be rejected.
        _patch_resolve(monkeypatch, ip)
        with pytest.raises(DisallowedUrlError):
            assert_url_allowed("https://harti.gov.lk/x")

    def test_unresolvable_host_blocked(self, monkeypatch):
        monkeypatch.setattr(netguard, "_resolve_ips", lambda host: [])
        with pytest.raises(DisallowedUrlError):
            assert_url_allowed("https://harti.gov.lk/x")

    def test_allow_private_ips_escape_hatch(self, monkeypatch):
        # Local-test override: private IPs permitted only when explicitly enabled.
        monkeypatch.setenv("AGRI_INGEST_ALLOW_PRIVATE_IPS", "1")
        _patch_resolve(monkeypatch, "127.0.0.1")
        assert_url_allowed("https://harti.gov.lk/x")  # no raise


# ===========================================================================
# Host allowlist override via env
# ===========================================================================

class TestAllowlistOverride:
    def test_env_override_replaces_default_list(self, monkeypatch):
        monkeypatch.setenv("AGRI_INGEST_ALLOWED_HOSTS", "only-this.example")
        # The default HARTI host is no longer allowed once overridden.
        with pytest.raises(DisallowedUrlError):
            assert_host_allowed("https://harti.gov.lk/x")
        assert_host_allowed("https://only-this.example/x")


# ===========================================================================
# guarded_get — redirects are not auto-followed and are re-validated
# ===========================================================================

class TestGuardedGetRedirects:
    def test_no_redirect_returns_response(self, monkeypatch):
        _patch_resolve(monkeypatch, "8.8.8.8")
        session = MagicMock()
        final = _make_response(status=200)
        session.get = MagicMock(return_value=final)

        result = guarded_get(session, "https://harti.gov.lk/x")

        assert result is final
        _, kwargs = session.get.call_args
        assert kwargs.get("allow_redirects") is False, (
            "auto-redirects must be disabled; guarded_get follows them manually"
        )

    def test_redirect_to_allowlisted_host_is_followed(self, monkeypatch):
        _patch_resolve(monkeypatch, "8.8.8.8")
        session = MagicMock()
        hop1 = _make_response(redirect_to="https://www.harti.gov.lk/final")
        final = _make_response(status=200)
        session.get = MagicMock(side_effect=[hop1, final])

        result = guarded_get(session, "https://harti.gov.lk/x")

        assert result is final
        assert session.get.call_count == 2

    def test_redirect_to_offlist_host_is_blocked(self, monkeypatch):
        _patch_resolve(monkeypatch, "8.8.8.8")
        session = MagicMock()
        hop1 = _make_response(redirect_to="https://evil.example.com/steal")
        session.get = MagicMock(return_value=hop1)

        with pytest.raises(DisallowedUrlError):
            guarded_get(session, "https://harti.gov.lk/x")

    def test_redirect_to_private_ip_host_is_blocked(self, monkeypatch):
        # Redirect Location points back at an allowlisted host that resolves to a
        # private IP — the private-IP block on the redirect target must fire.
        def _resolve(host):
            return ["169.254.169.254"] if host == "harti.gov.lk" else ["8.8.8.8"]
        monkeypatch.setattr(netguard, "_resolve_ips", _resolve)
        session = MagicMock()
        hop1 = _make_response(redirect_to="https://harti.gov.lk/meta")
        # First hop host resolves public; but we want the FIRST url public and the
        # redirect target private. Use a different first host.
        session.get = MagicMock(return_value=hop1)

        with pytest.raises(DisallowedUrlError):
            guarded_get(session, "https://www.harti.gov.lk/x")

    def test_redirect_loop_bounded(self, monkeypatch):
        _patch_resolve(monkeypatch, "8.8.8.8")
        monkeypatch.setenv("AGRI_INGEST_MAX_REDIRECTS", "2")
        session = MagicMock()
        # Always redirect to another allowlisted URL -> should hit the hop cap.
        looping = _make_response(redirect_to="https://harti.gov.lk/again")
        session.get = MagicMock(return_value=looping)

        with pytest.raises(DisallowedUrlError):
            guarded_get(session, "https://harti.gov.lk/start")


# ===========================================================================
# Wiring: the HARTI download loop rejects an off-allowlist URL as a failure
# ===========================================================================

class TestDownloaderWiring:
    def test_download_pdfs_blocks_offlist_url_without_aborting(self, tmp_path, monkeypatch):
        from agriforecast_ml.harti import downloader
        from datetime import date

        # Force the built HARTI_BASE-joined URL to be off-allowlist by overriding
        # the allowlist to exclude harti.gov.lk. The loop must skip (n_fail) and
        # never call the network.
        monkeypatch.setenv("AGRI_INGEST_ALLOWED_HOSTS", "only-other.example")
        session = MagicMock()
        session.get = MagicMock(side_effect=AssertionError("network must not be reached"))

        links = [(date(2024, 1, 1), "assets/pdf/food_price/daily/eng/x.pdf")]
        result = downloader.download_pdfs(tmp_path / "c", links, session=session, delay=0)

        assert result == []
        session.get.assert_not_called()


# ===========================================================================
# Wiring: the news fetcher skips a disallowed feed URL without fetching (N5)
# ===========================================================================

class TestNewsFetcherWiring:
    def test_fetch_feed_skips_metadata_ip_url_and_never_parses(self):
        from agriforecast_ml.news import fetcher
        from unittest.mock import patch

        feed_spec = {
            "source": "poisoned",
            "url": "http://169.254.169.254/latest/meta-data/",
            "category": "test",
        }

        with patch("feedparser.parse") as mock_parse:
            articles = fetcher.fetch_feed(feed_spec)

        assert articles == [], "A disallowed feed URL must be skipped (empty result)"
        mock_parse.assert_not_called()  # guard fires BEFORE feedparser fetches

    def test_fetch_feed_skips_offlist_host_and_never_parses(self):
        from agriforecast_ml.news import fetcher
        from unittest.mock import patch

        feed_spec = {
            "source": "offlist",
            "url": "https://evil.example.com/feed/",
            "category": "test",
        }

        with patch("feedparser.parse") as mock_parse:
            articles = fetcher.fetch_feed(feed_spec)

        assert articles == []
        mock_parse.assert_not_called()
