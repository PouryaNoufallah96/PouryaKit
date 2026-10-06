"""Bounded authenticated requests. Response bodies and signed URLs are never diagnostics."""

import base64
import http.client
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = "PouryaNoufallah96/PouryaKit"
OWNER = REPOSITORY.split("/")[0]
API = "https://api.github.com"
FEED = f"https://nuget.pkg.github.com/{OWNER}/index.json"
MAX_BYTES = 100 * 1024 * 1024
MAX_REDIRECTS = 3


class PublicationError(ValueError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


class Http:
    def __init__(self, token):
        if not token:
            raise PublicationError("Publication requires the hosted repository token.")
        self.token = token
        self.opener = urllib.request.build_opener(NoRedirect())
        self.redirects = []

    def checked_url(self, url, auth):
        try:
            parsed = urllib.parse.urlsplit(url)
        except ValueError:
            raise PublicationError("Service returned an unsafe URL.") from None
        if parsed.scheme != "https" or parsed.username or parsed.password or parsed.fragment:
            raise PublicationError("Service returned an unsafe URL.")
        host = parsed.netloc.lower()
        allowed = host == ("api.github.com" if auth == "api" else "nuget.pkg.github.com")
        if auth == "none":
            allowed = host.endswith((".blob.core.windows.net", ".githubusercontent.com", ".github.com"))
        if not allowed:
            raise PublicationError("Service returned an unsupported host.")
        if auth == "feed" and not (parsed.path.lower() == f"/{OWNER.lower()}" or parsed.path.lower().startswith(f"/{OWNER.lower()}/")):
            raise PublicationError("Service resource is outside the fixed private feed.")
        if auth == "feed" and parsed.query:
            raise PublicationError("Service resource contains an unsupported query.")

    @staticmethod
    def hostname(url):
        try:
            host = urllib.parse.urlsplit(url).hostname or ""
        except ValueError:
            host = ""
        return host.lower() if re.fullmatch(r"[A-Za-z0-9.-]+", host) else "[invalid]"

    def request(self, method, url, *, auth="api", data=None, headers=None, missing=False, raw_package=False):
        self.checked_url(url, auth)
        if raw_package and (method != "GET" or auth != "feed" or not urllib.parse.urlsplit(url).path.endswith(".nupkg")):
            raise PublicationError("Only a raw private package GET may opt into storage redirects.")
        follow = method == "GET" and ((auth == "api" and url.endswith("/zip")) or raw_package)
        request_headers = {"User-Agent": "PouryaKit-publication", **(headers or {})}
        if auth == "api":
            request_headers.update({"Authorization": f"Bearer {self.token}",
                                    "Accept": "application/vnd.github+json",
                                    "X-GitHub-Api-Version": "2022-11-28"})
        elif auth == "feed":
            credential = base64.b64encode(f"{OWNER}:{self.token}".encode()).decode()
            request_headers["Authorization"] = f"Basic {credential}"
        seen = {url}
        hops = 0
        while True:
            for attempt in range(3):
                request = urllib.request.Request(url, data=data, method=method, headers=request_headers)
                try:
                    response = self.opener.open(request, timeout=30)
                except urllib.error.HTTPError as error:
                    response = error
                except (urllib.error.URLError, TimeoutError, OSError):
                    if method != "GET" or attempt == 2:
                        raise PublicationError("Publication network request failed.") from None
                    time.sleep(attempt + 1)
                    continue
                with response:
                    status = response.code
                    response_headers = response.headers
                    if status in (301, 302, 303, 307, 308):
                        target = response_headers.get("Location", "")
                        diagnostic = {"status": status, "source_host": self.hostname(url),
                                      "target_host": self.hostname(target), "result": "refused"}
                        if len(self.redirects) >= 200:
                            raise PublicationError("Publication redirect diagnostics exceed the limit.")
                        self.redirects.append(diagnostic)
                        if not follow:
                            raise PublicationError("Unexpected authenticated redirect.")
                        try:
                            self.checked_url(target, "none")
                        except PublicationError as error:
                            raise PublicationError(f"{error} Redirect HTTP {status} to host {diagnostic['target_host']}.") from None
                        if hops >= MAX_REDIRECTS or target in seen:
                            raise PublicationError("Publication storage redirect chain exceeds the limit or cycles.")
                        diagnostic["result"] = "followed"
                        seen.add(target)
                        hops += 1
                        url, auth, data = target, "none", None
                        # Signed storage URLs authorize themselves; forward no initial headers or credentials.
                        request_headers = {"User-Agent": "PouryaKit-publication"}
                        break
                    try:
                        body = response.read(MAX_BYTES + 1)
                    except (http.client.HTTPException, OSError):
                        raise PublicationError("Publication response was interrupted.") from None
                if len(body) > MAX_BYTES:
                    raise PublicationError("Publication response exceeds the size limit.")
                if method == "GET" and status in (429, 500, 502, 503, 504) and attempt < 2:
                    time.sleep(attempt + 1)
                    continue
                if 200 <= status < 300 or (missing and status == 404) or (method == "PUT" and status == 409):
                    return status, body, response_headers
                raise PublicationError(f"Publication service rejected {method} with HTTP {status}.")

    def document(self, url, *, auth="api", missing=False):
        status, body, headers = self.request("GET", url, auth=auth, missing=missing)
        if status == 404:
            return None, headers
        try:
            return json.loads(body), headers
        except (ValueError, UnicodeError):
            raise PublicationError("Publication service returned invalid JSON.") from None

    def pages(self, url, key=None):
        values, seen = [], set()
        for _ in range(1000):
            if url in seen:
                raise PublicationError("Publication listing contains a pagination cycle.")
            seen.add(url)
            document, headers = self.document(url)
            page = document.get(key) if key and isinstance(document, dict) else document
            if not isinstance(page, list):
                raise PublicationError("Publication listing has an unsupported shape.")
            values.extend(page)
            links = re.findall(r'<([^>]+)>;\s*rel="next"', headers.get("Link", ""))
            if not links:
                return values
            if len(links) != 1:
                raise PublicationError("Publication listing has ambiguous pagination.")
            url = links[0]
        raise PublicationError("Publication listing exceeds the page limit.")
