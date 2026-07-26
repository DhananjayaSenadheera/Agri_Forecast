"""Outbound-fetch SSRF guard for the ingestion paths.

The HARTI listing scraper and the news RSS fetcher follow URLs that ultimately come from
remote, attacker-influenceable content, so an unguarded fetch could be pointed at cloud
metadata (169.254.169.254), a loopback admin port, or any other unexpected host.

Every ingestion fetch goes through this module, which enforces three controls in order:

  1. Scheme allowlist - https only, plus http for explicitly allowlisted hosts, since
     some government endpoints are http-only.
  2. Host allowlist - the host must match an entry exactly or as a parent-domain suffix.
  3. Private-IP block - every IP the host resolves to must be globally routable, so a DNS
     answer of 127.0.0.1 for an allowlisted host is still rejected.

Redirects are not auto-followed: a 3xx Location is re-validated through the same three
controls before being followed manually.

Known residual: the IP check resolves at check time while requests re-resolves at connect
time, so DNS rebinding could in principle land on a different IP. That needs control of
DNS for an already-allowlisted host plus a sub-second race. Pinning the validated IP into
the socket is deliberately out of scope and recorded here rather than overlooked.

Config, all optional: AGRI_INGEST_ALLOWED_HOSTS REPLACES the built-in host list,
AGRI_INGEST_ALLOW_PRIVATE_IPS skips the private-IP block (local tests only, never in
production), and AGRI_INGEST_MAX_REDIRECTS caps redirect hops (default 5).
"""
from __future__ import annotations

import ipaddress
import logging
import os
import socket
from urllib.parse import urlparse

import requests

logger = logging.getLogger(__name__)

# Built-in default allowlist. These are the only hosts P1 ingestion legitimately
# reaches. Overridable via AGRI_INGEST_ALLOWED_HOSTS (which REPLACES this list).
# Matched as exact host OR parent-domain suffix (see ``_host_allowed``).
_DEFAULT_ALLOWED_HOSTS: tuple[str, ...] = (
    # HARTI daily-price bulletins (listing page + PDF assets).
    "harti.gov.lk",
    # CBSL macro ingestion: CCPI press releases and the Monthly Economic Indicators packs,
    # all served under this one apex. statistics.gov.lk is deliberately not added - keeping
    # the allowlist single-apex is preferred over covering one more series.
    "cbsl.gov.lk",
    # News RSS feeds (see agriforecast_ml/news/feeds.py).
    "economynext.com",
    "lankabusinessonline.com",
    "adaderana.lk",
    "island.lk",
    "reliefweb.int",
    "agrifarming.in",
)

_ALLOWED_SCHEMES = ("http", "https")
_DEFAULT_MAX_REDIRECTS = 5


class DisallowedUrlError(Exception):
    """Raised when an outbound URL fails an SSRF control (scheme/host/IP)."""


# Log the active allowlist once, and again if it changes, so an operator who sets
# AGRI_INGEST_ALLOWED_HOSTS and thereby drops the built-in defaults can see it.
_last_logged_allowlist: tuple[str, ...] | None = None


def _allowed_hosts() -> tuple[str, ...]:
    """Return the active host allowlist (env override REPLACES the default).

    Logs the effective, sorted allowlist once (and again only if it changes), so
    a mis-set AGRI_INGEST_ALLOWED_HOSTS that drops the defaults is visible in the
    logs rather than silently narrowing what ingestion can reach.
    """
    global _last_logged_allowlist
    override = os.getenv("AGRI_INGEST_ALLOWED_HOSTS", "").strip()
    if not override:
        hosts = _DEFAULT_ALLOWED_HOSTS
        source = "built-in defaults"
    else:
        parsed = tuple(
            h.strip().lower() for h in override.split(",") if h.strip()
        )
        if parsed:
            hosts = parsed
            source = "AGRI_INGEST_ALLOWED_HOSTS override"
        else:
            hosts = _DEFAULT_ALLOWED_HOSTS
            source = "built-in defaults"

    if hosts != _last_logged_allowlist:
        logger.info(
            "SSRF allowlist active (%s): %s", source, ", ".join(sorted(hosts))
        )
        _last_logged_allowlist = hosts
    return hosts


def _allow_private_ips() -> bool:
    return os.getenv("AGRI_INGEST_ALLOW_PRIVATE_IPS", "").strip().lower() in (
        "1", "true", "yes",
    )


def _max_redirects() -> int:
    try:
        n = int(os.getenv("AGRI_INGEST_MAX_REDIRECTS", str(_DEFAULT_MAX_REDIRECTS)))
    except ValueError:
        return _DEFAULT_MAX_REDIRECTS
    return max(0, n)


def _host_allowed(host: str, allowed: tuple[str, ...]) -> bool:
    """True if ``host`` is an allowlisted host or a subdomain of one.

    ``www.harti.gov.lk`` matches allowlist entry ``harti.gov.lk`` (suffix on a
    dot boundary), but ``notharti.gov.lk`` does NOT match ``harti.gov.lk`` and
    ``evil-harti.gov.lk.attacker.com`` does NOT match either.
    """
    host = host.lower().rstrip(".")
    for entry in allowed:
        entry = entry.lower().rstrip(".")
        if host == entry or host.endswith("." + entry):
            return True
    return False


def _ip_is_public(ip_str: str) -> bool:
    """True only for globally-routable IPs.

    Rejects loopback, private (RFC1918), link-local (incl. 169.254.0.0/16 cloud
    metadata), unique-local IPv6, multicast, and reserved ranges.
    """
    try:
        ip = ipaddress.ip_address(ip_str)
    except ValueError:
        return False
    return not (
        ip.is_private
        or ip.is_loopback
        or ip.is_link_local
        or ip.is_multicast
        or ip.is_reserved
        or ip.is_unspecified
    )


def _resolve_ips(host: str) -> list[str]:
    """Resolve ``host`` to all its A/AAAA addresses (empty list on failure)."""
    try:
        infos = socket.getaddrinfo(host, None)
    except OSError:
        return []
    return sorted({info[4][0] for info in infos})


def assert_host_allowed(url: str) -> None:
    """Validate ``url``'s scheme + host against the allowlist (NO DNS lookup).

    Offline, network-free check: rejects a bad scheme or a host that is not on
    the allowlist. Used as a cheap per-item guard when the private-IP resolution
    has already been done at the entry fetch (e.g. every PDF URL in a HARTI
    download batch shares the already-validated listing host). Raises
    ``DisallowedUrlError`` on violation.
    """
    parsed = urlparse(url)
    scheme = (parsed.scheme or "").lower()
    if scheme not in _ALLOWED_SCHEMES:
        raise DisallowedUrlError(f"scheme not permitted: {scheme!r}")

    host = parsed.hostname
    if not host:
        raise DisallowedUrlError("URL has no host")

    if not _host_allowed(host, _allowed_hosts()):
        raise DisallowedUrlError(f"host not on allowlist: {host!r}")


def assert_url_allowed(url: str) -> None:
    """Validate ``url`` against scheme + host allowlist + private-IP block.

    Raises ``DisallowedUrlError`` on any violation. This is the function every
    outbound ingestion fetch (and every redirect target) is routed through.
    Performs a DNS resolution (resolve-then-check) so a DNS answer pointing an
    allowlisted host at a private/loopback IP is still rejected.
    """
    assert_host_allowed(url)

    if _allow_private_ips():
        return  # local-test escape hatch; never enabled in prod.

    host = urlparse(url).hostname or ""

    ips = _resolve_ips(host)
    if not ips:
        raise DisallowedUrlError(f"host did not resolve: {host!r}")
    for ip in ips:
        if not _ip_is_public(ip):
            raise DisallowedUrlError(
                f"host {host!r} resolves to non-public IP {ip!r} (SSRF blocked)"
            )


def guarded_get(
    session: requests.Session,
    url: str,
    *,
    headers: dict | None = None,
    timeout: float | tuple = 30,
    stream: bool = False,
) -> requests.Response:
    """SSRF-safe ``session.get`` with manual, re-validated redirect following.

    Every hop (initial URL and each ``Location``) passes ``assert_url_allowed``
    before the request is issued, so a redirect can never escape the allowlist
    or land on a private/loopback/link-local address. Auto-redirects are
    disabled at the requests layer (``allow_redirects=False``); we follow them
    ourselves up to ``AGRI_INGEST_MAX_REDIRECTS`` hops.

    Returns the final non-redirect ``Response``. Raises ``DisallowedUrlError``
    if any hop is disallowed, or ``requests`` errors as usual for transport
    failures.
    """
    max_hops = _max_redirects()
    current = url
    for _hop in range(max_hops + 1):
        assert_url_allowed(current)
        resp = session.get(
            current,
            headers=headers,
            timeout=timeout,
            stream=stream,
            allow_redirects=False,
        )
        if resp.is_redirect or resp.is_permanent_redirect:
            location = resp.headers.get("Location")
            resp.close()
            if not location:
                raise DisallowedUrlError("redirect response missing Location")
            # Resolve relative redirects against the current URL, then re-validate.
            from urllib.parse import urljoin
            current = urljoin(current, location)
            continue
        return resp
    raise DisallowedUrlError(f"too many redirects (> {max_hops})")
