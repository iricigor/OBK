"""A local, memory-only probe of the community-documented Bakalari API."""

import json
import queue
import threading
import tkinter as tk
from dataclasses import dataclass
from http.client import HTTPException
from tkinter import ttk
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


DEFAULT_URL = "https://zsbitovska.bakalari.cz/"
MAX_RESPONSE_BYTES = 2 * 1024 * 1024


class ProbeError(Exception):
    """An actionable error safe to display without disclosing response bodies."""


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def normalize_url(value):
    try:
        parts = urlsplit(value.strip())
        port = parts.port
    except ValueError:
        raise ProbeError("Enter a valid HTTPS school URL.") from None
    if (
        parts.scheme != "https"
        or not parts.hostname
        or parts.username is not None
        or parts.password is not None
        or parts.query
        or parts.fragment
        or (port is not None and not 1 <= port <= 65535)
        or any(character.isspace() or ord(character) < 32 for character in value)
        or "\\" in value
    ):
        raise ProbeError("Use an HTTPS school URL without credentials, query, or fragment.")
    path = parts.path.rstrip("/")
    if path.lower().endswith("/login"):
        raise ProbeError("Enter the school's base URL, not its /login page.")
    return urlunsplit((parts.scheme, parts.netloc, path, "", ""))


@dataclass(frozen=True)
class ProbeResult:
    api_version: str
    messages_on_page: int
    attachments_on_page: int
    expires_in: int | None


class ApiProbe:
    def __init__(self, base_url, opener=None):
        self.base_url = normalize_url(base_url)
        self.opener = opener if opener is not None else build_opener(NoRedirects())

    def open_response(self, path, *, body=None, token=None, form=False, accept="application/json"):
        headers = {"Accept": accept}
        data = None
        if body is not None:
            if form:
                data = urlencode(body).encode("utf-8")
                headers["Content-Type"] = "application/x-www-form-urlencoded"
            else:
                data = json.dumps(body).encode("utf-8")
                headers["Content-Type"] = "application/json"
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        request = Request(self.base_url + path, data=data, headers=headers)
        try:
            return self.opener.open(request, timeout=20)
        except HTTPError as error:
            status = error.code
            error.close()
            if 300 <= status < 400:
                raise ProbeError(
                    "The school redirected the request. Check the base URL; "
                    "redirects are blocked to protect credentials."
                ) from None
            if path == "/api/login" and status in (400, 401, 403):
                raise ProbeError(
                    f"Sign-in rejected (HTTP {status}). Check credentials. "
                    "The documented mobile login may also be disabled or incompatible."
                ) from None
            if status in (401, 403):
                raise ProbeError(
                    f"API access rejected (HTTP {status}). "
                    "The account or token may not have Komens access."
                ) from None
            raise ProbeError(
                f"API request failed (HTTP {status}). "
                "Check school availability and API compatibility."
            ) from None
        except (URLError, TimeoutError, OSError, ValueError):
            raise ProbeError(
                "Connection failed. Check the URL, internet connection, and the "
                "school's HTTPS certificate. No certificate checks were bypassed."
            ) from None

    def request_json(self, path, *, body=None, token=None, form=False):
        try:
            with self.open_response(path, body=body, token=token, form=form) as response:
                content = response.read(MAX_RESPONSE_BYTES + 1)
        except (URLError, HTTPException, TimeoutError, OSError):
            raise ProbeError("The API response was interrupted. Check the connection and retry.") from None
        if len(content) > MAX_RESPONSE_BYTES:
            raise ProbeError("The API response exceeded the tester's 2 MB limit.")
        try:
            return json.loads(content)
        except (ValueError, UnicodeError):
            raise ProbeError(
                "The school returned an invalid JSON response. Check API compatibility."
            ) from None

    def sign_in(self, username, password):
        if not username.strip() or not password:
            raise ProbeError("Enter both a username and password.")
        auth = self.request_json(
            "/api/login",
            body={
                "client_id": "ANDR",
                "grant_type": "password",
                "username": username.strip(),
                "password": password,
            },
            form=True,
        )
        token = auth.get("access_token") if isinstance(auth, dict) else None
        if (
            not isinstance(token, str)
            or not token
            or any(character.isspace() or ord(character) < 32 for character in token)
        ):
            raise ProbeError("Sign-in returned no usable access token.")
        expires = auth.get("expires_in")
        if type(expires) is not int or expires <= 0:
            expires = None
        return token, expires

    def run(self, username, password):
        if not username.strip() or not password:
            raise ProbeError("Enter both a username and password.")
        info = self.request_json("/api/3")
        if not isinstance(info, dict) or not isinstance(info.get("ApiVersion"), str):
            raise ProbeError("API discovery did not return a recognized version.")
        token, expires = self.sign_in(username, password)

        received = self.request_json(
            "/api/3/komens/messages/received",
            body={"Pagination": {"PageNumber": 1, "PageSize": 10}},
            token=token,
        )
        messages = received.get("Messages") if isinstance(received, dict) else None
        if not isinstance(messages, list):
            raise ProbeError("Komens returned an unrecognized message-list format.")
        attachments = 0
        for message in messages:
            if not isinstance(message, dict) or not isinstance(
                message.get("Attachments"), list
            ):
                raise ProbeError("Komens returned an unrecognized message format.")
            attachments += len(message["Attachments"])
        return ProbeResult(info["ApiVersion"], len(messages), attachments, expires)


class ProbeWindow:
    def __init__(self, root):
        self.root = root
        self.results = queue.Queue()
        root.title("OBK - API connection tester")
        root.geometry("620x680")
        root.minsize(540, 650)
        style = ttk.Style(root)
        if "vista" in style.theme_names():
            style.theme_use("vista")
        style.configure("Title.TLabel", font=("Segoe UI", 18, "bold"))
        style.configure("TLabel", font=("Segoe UI", 10))
        style.configure("TButton", font=("Segoe UI", 10))
        panel = ttk.Frame(root, padding=24)
        panel.pack(fill="both", expand=True)
        panel.columnconfigure(0, weight=1)
        ttk.Label(panel, text="Test Komens API access", style="Title.TLabel").grid(
            row=0, column=0, sticky="w", pady=(0, 8)
        )
        intro = ttk.Label(
            panel,
            text="Sign in directly to your school. This tester only requests a "
            "message list; it does not send messages or call read-receipt endpoints.",
            wraplength=560,
        )
        intro.grid(row=1, column=0, sticky="ew", pady=(0, 18))
        self.url = tk.StringVar(value=DEFAULT_URL)
        self.username = tk.StringVar()
        self.password = tk.StringVar()
        self.entries = []
        for index, (label, variable, hidden) in enumerate(
            [
                ("School base URL (HTTPS)", self.url, False),
                ("Username", self.username, False),
                ("Password", self.password, True),
            ]
        ):
            row = 2 + index * 2
            ttk.Label(panel, text=label).grid(row=row, column=0, sticky="w")
            entry = ttk.Entry(panel, textvariable=variable, show="*" if hidden else "")
            entry.grid(row=row + 1, column=0, sticky="ew", pady=(4, 12))
            self.entries.append(entry)
        self.button = ttk.Button(panel, text="Sign in and test API", command=self.start)
        self.button.grid(row=8, column=0, sticky="w", pady=(4, 14))
        self.status = tk.StringVar(value="Ready. Nothing has been sent.")
        status = ttk.Label(panel, textvariable=self.status, wraplength=560)
        status.grid(row=9, column=0, sticky="ew")
        panel.rowconfigure(10, weight=1)
        privacy = ttk.Label(
            panel,
            text="Credentials and tokens are never saved or logged. The password "
            "field is cleared when testing starts. No message content is displayed "
            "or stored. Python cannot guarantee immediate erasure of memory.",
            wraplength=560,
        )
        privacy.grid(row=11, column=0, sticky="ew", pady=(18, 0))
        panel.bind(
            "<Configure>",
            lambda event: [
                label.configure(wraplength=max(250, event.width - 48))
                for label in (intro, status, privacy)
            ],
        )
        root.bind("<Return>", lambda event: self.start())
        self.entries[1].focus_set()
        self.busy = False
        root.after(100, self.poll)

    def start(self):
        if self.busy:
            return
        try:
            probe = ApiProbe(self.url.get())
            username, password = self.username.get(), self.password.get()
            if not username.strip() or not password:
                raise ProbeError("Enter both a username and password.")
        except ProbeError as error:
            self.status.set(str(error))
            return
        self.password.set("")
        self.busy = True
        self.button.state(["disabled"])
        for entry in self.entries:
            entry.state(["disabled"])
        self.status.set("Checking API version, signing in, and requesting one page...")
        threading.Thread(
            target=self.worker, args=(probe, username, password), daemon=True
        ).start()

    def worker(self, probe, username, password):
        try:
            self.results.put(probe.run(username, password))
        except ProbeError as error:
            self.results.put(str(error))
        except Exception as error:
            # Do not expose an exception's potentially credential-bearing message.
            self.results.put(
                f"Unexpected tester error ({type(error).__name__}). "
                "Close and reopen the tester; review the implementation if it persists."
            )

    def poll(self):
        try:
            result = self.results.get_nowait()
        except queue.Empty:
            pass
        else:
            self.busy = False
            self.button.state(["!disabled"])
            for entry in self.entries:
                entry.state(["!disabled"])
            if isinstance(result, ProbeResult):
                expiry = (
                    f" Token lifetime: {result.expires_in} seconds."
                    if result.expires_in is not None
                    else ""
                )
                self.status.set(
                    f"Success: API {result.api_version}; sign-in and Komens retrieval "
                    f"worked.\nFirst page: {result.messages_on_page} messages, "
                    f"{result.attachments_on_page} attachment references.{expiry}\n"
                    "Attachment downloads, full pagination, and server-side "
                    "read-state behavior have not been verified."
                )
            else:
                self.status.set(result)
            self.entries[2].focus_set()
        self.root.after(100, self.poll)


def main():
    root = tk.Tk()
    ProbeWindow(root)
    root.mainloop()


if __name__ == "__main__":
    main()
