"""School-year synchronization, safe message rendering, and Windows local storage."""

import ctypes
import hashlib
import json
import os
import re
import sqlite3
import tempfile
from contextlib import closing
from ctypes import wintypes
from dataclasses import dataclass
from datetime import date, datetime, timezone
from html.parser import HTMLParser
from http.client import HTTPException
from pathlib import Path
from urllib.error import URLError
from urllib.parse import quote

from api_probe import ApiProbe, ProbeError, normalize_url


FOLDERS = {"received": "Inbox", "sent": "Sent", "noticeboard": "Noticeboard"}
PAGE_SIZE = 50
ATTACHMENT_CHUNK_SIZE = 64 * 1024
OPENABLE_EXTENSIONS = {
    ".pdf", ".txt", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
    ".rtf", ".zip",
}


def attachment_id(attachment):
    value = attachment.get("Id")
    if not isinstance(value, str) or not value:
        raise ProbeError("An attachment has no valid ID; it cannot be downloaded.")
    return value


def attachment_filename(attachment):
    name = attachment.get("Name") or attachment.get("FileName") or "attachment.bin"
    if not isinstance(name, str):
        raise ProbeError("An attachment has an invalid filename.")
    name = name.replace("\\", "/").rsplit("/", 1)[-1]
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).rstrip(" .") or "attachment.bin"
    stem = name.split(".", 1)[0].upper().rstrip(" .")
    if stem in {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"} or re.fullmatch(
        r"(COM|LPT)[1-9\u00b9\u00b2\u00b3]", stem
    ):
        name = "_" + name
    if len(name) > 180:
        suffix = Path(name).suffix[:20]
        name = name[:180 - len(suffix)] + suffix
    return name


def collect_attachments(folders):
    result = {}
    for messages in folders.values():
        for message in messages:
            for attachment in message["Attachments"]:
                identifier = attachment_id(attachment)
                attachment_filename(attachment)
                result.setdefault(identifier, attachment)
    return list(result.values())


@dataclass(frozen=True)
class SchoolYear:
    start_year: int

    @property
    def label(self):
        return f"{self.start_year}/{self.start_year + 1}"

    @property
    def start(self):
        return date(self.start_year, 9, 1)

    @property
    def end(self):
        return date(self.start_year + 1, 9, 1)

    @classmethod
    def current(cls, today=None):
        today = today if today is not None else date.today()
        return cls(today.year if today.month >= 9 else today.year - 1)


def message_date(message):
    try:
        value = message["SentDate"]
        if not isinstance(value, str):
            raise ValueError
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (KeyError, ValueError):
        raise ProbeError("A message has an invalid sent date; synchronization stopped.") from None


def validate_message(message):
    if (
        not isinstance(message, dict)
        or not isinstance(message.get("Id"), str)
        or not message["Id"]
        or not isinstance(message.get("Title"), str)
        or not isinstance(message.get("Text"), str)
        or not isinstance(message.get("Attachments"), list)
        or any(not isinstance(item, dict) for item in message["Attachments"])
    ):
        raise ProbeError("Komens returned an unrecognized message format.")
    message_date(message)


class KomensClient(ApiProbe):
    def school_name(self, token):
        profile = self.request_json("/api/3/user", token=token)
        name = profile.get("SchoolOrganizationName") if isinstance(profile, dict) else None
        if not isinstance(name, str) or not name.strip():
            raise ProbeError("The school profile did not return a school name.")
        return name.strip()

    def fetch_folder(self, token, folder, year, progress=None, cached=None):
        if folder not in FOLDERS:
            raise ProbeError("Unknown Komens folder.")
        seen = set()
        messages = {}
        for message in cached or []:
            validate_message(message)
            if year.start <= message_date(message).date() < year.end:
                messages[message["Id"]] = message
        since = (
            message_date(max(
                messages.values(), key=lambda message: message_date(message).timestamp()
            )).replace(tzinfo=None)
            if messages else datetime.combine(year.start, datetime.min.time())
        )
        # Continue to an empty page: the server can cap pages below our requested size.
        for page in range(1, 10001):
            result = self.request_json(
                f"/api/3/komens/messages/{folder}",
                body={
                    "DateFrom": since.isoformat(timespec="milliseconds"),
                    "DateTo": f"{year.end.year}-08-31T23:59:59.999",
                    "IncludeHidden": False,
                    "Pagination": {"PageNumber": page, "PageSize": PAGE_SIZE},
                },
                token=token,
            )
            batch = result.get("Messages") if isinstance(result, dict) else None
            if not isinstance(batch, list):
                raise ProbeError("Komens returned an unrecognized message-list format.")
            if not batch:
                return sorted(
                    messages.values(),
                    key=lambda message: message_date(message).replace(tzinfo=None),
                    reverse=True,
                )
            new_ids = set()
            for message in batch:
                validate_message(message)
                identifier = message["Id"]
                if identifier not in seen:
                    new_ids.add(identifier)
                if year.start <= message_date(message).date() < year.end:
                    messages[identifier] = message
            if not new_ids:
                raise ProbeError(
                    "The school repeated a page without new messages. "
                    "Synchronization stopped rather than saving an incomplete archive."
                )
            seen.update(new_ids)
            if progress is not None:
                progress(folder, page, len(messages))
        raise ProbeError(
            "The school did not finish pagination after 10,000 pages. "
            "No incomplete archive was saved."
        )

    def fetch_year(self, token, year, progress=None, cached=None):
        result = {}
        for folder in FOLDERS:
            try:
                result[folder] = self.fetch_folder(
                    token, folder, year, progress,
                    cached=cached.get(folder) if cached is not None else None,
                )
            except ProbeError as error:
                raise ProbeError(f"{FOLDERS[folder]}: {error}") from None
        return result

    def download_attachments(self, token, folders, store, username, progress=None):
        attachments = collect_attachments(folders)
        for index, attachment in enumerate(attachments, 1):
            identifier = attachment_id(attachment)
            if progress is not None:
                progress(index - 1, len(attachments), attachment_filename(attachment))
            try:
                if store.attachment_info(self.base_url, username, identifier) is None:
                    path = "/api/3/komens/attachment/" + quote(identifier, safe="")
                    with self.open_response(path, token=token, accept="*/*") as response:
                        length = response.headers.get("Content-Length")
                        try:
                            expected_size = int(length) if length is not None else None
                            if expected_size is not None and expected_size < 0:
                                raise ValueError
                        except ValueError:
                            raise ProbeError("The attachment has an invalid download size.") from None
                        store.save_attachment(
                            self.base_url, username, attachment,
                            iter(lambda: response.read(ATTACHMENT_CHUNK_SIZE), b""),
                            expected_size=expected_size,
                            content_type=response.headers.get("Content-Type", ""),
                        )
            except (URLError, HTTPException, TimeoutError, OSError):
                raise ProbeError(
                    f"Attachment {index} of {len(attachments)}: download interrupted. "
                    "Retry sync; completed downloads will be reused."
                ) from None
            except ProbeError as error:
                raise ProbeError(f"Attachment {index} of {len(attachments)}: {error}") from None
            if progress is not None:
                progress(index, len(attachments), attachment_filename(attachment))
        return len(attachments)


class DataBlob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]


class WindowsProtection:
    def __init__(self):
        if os.name != "nt":
            raise ProbeError("This version requires Windows user-data protection.")
        self.crypt = ctypes.WinDLL("crypt32", use_last_error=True)
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        arguments = [
            ctypes.POINTER(DataBlob), ctypes.c_void_p, ctypes.c_void_p,
            ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(DataBlob),
        ]
        for name in ("CryptProtectData", "CryptUnprotectData"):
            function = getattr(self.crypt, name)
            function.argtypes = arguments
            function.restype = wintypes.BOOL
        self.kernel.LocalFree.argtypes = [ctypes.c_void_p]
        self.kernel.LocalFree.restype = ctypes.c_void_p

    def transform(self, value, decrypt):
        buffer = (ctypes.c_ubyte * len(value)).from_buffer_copy(value)
        source = DataBlob(len(value), buffer)
        output = DataBlob()
        function = self.crypt.CryptUnprotectData if decrypt else self.crypt.CryptProtectData
        # UI_FORBIDDEN; protection is scoped to the current Windows user, not the machine.
        if not function(
            ctypes.byref(source), None, None, None, None, 1, ctypes.byref(output)
        ):
            action = "decrypt" if decrypt else "encrypt"
            raise ProbeError(
                f"Windows could not {action} local data. "
                "Use the Windows account that created it; no plaintext fallback is used."
            )
        try:
            return ctypes.string_at(output.data, output.size)
        finally:
            self.kernel.LocalFree(ctypes.cast(output.data, ctypes.c_void_p))

    def protect(self, value):
        return self.transform(value, False)

    def unprotect(self, value):
        return self.transform(value, True)


def database_path():
    folder = os.environ.get("LOCALAPPDATA")
    if not folder:
        raise ProbeError("Windows LOCALAPPDATA is unavailable; no local database was opened.")
    return Path(folder) / "OBK" / "obk.sqlite3"


class LocalStore:
    def __init__(self, path=None, protection=None):
        self.path = Path(path) if path is not None else database_path()
        self.protection = protection if protection is not None else WindowsProtection()
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with closing(sqlite3.connect(self.path)) as connection, connection:
                connection.executescript("""
                    CREATE TABLE IF NOT EXISTS settings (
                        name TEXT PRIMARY KEY, value BLOB NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS archives (
                        url TEXT NOT NULL, username TEXT NOT NULL, year INTEGER NOT NULL,
                        school_name TEXT NOT NULL, messages BLOB NOT NULL,
                        synced_at TEXT NOT NULL,
                        PRIMARY KEY (url, username, year)
                    );
                    CREATE TABLE IF NOT EXISTS attachments (
                        url TEXT NOT NULL, username TEXT NOT NULL, id TEXT NOT NULL,
                        metadata BLOB NOT NULL,
                        PRIMARY KEY (url, username, id)
                    );
                    CREATE TABLE IF NOT EXISTS attachment_chunks (
                        url TEXT NOT NULL, username TEXT NOT NULL, id TEXT NOT NULL,
                        chunk_index INTEGER NOT NULL, payload BLOB NOT NULL,
                        PRIMARY KEY (url, username, id, chunk_index)
                    );
                """)
        except (OSError, sqlite3.Error):
            raise ProbeError("Cannot create the local OBK database. Check disk access.") from None

    def encode(self, value):
        return self.protection.protect(json.dumps(value, ensure_ascii=False).encode("utf-8"))

    def decode(self, value):
        try:
            return json.loads(self.protection.unprotect(value))
        except (ValueError, UnicodeError):
            raise ProbeError("Local OBK data is damaged and could not be decoded.") from None

    def setting(self, name):
        try:
            with closing(sqlite3.connect(self.path)) as connection:
                row = connection.execute(
                    "SELECT value FROM settings WHERE name = ?", (name,)
                ).fetchone()
            return self.decode(row[0]) if row else None
        except sqlite3.Error:
            raise ProbeError("Cannot read local OBK settings. Check disk access.") from None

    def remembered_login(self):
        result = self.setting("remembered_login")
        if result is not None and (
            not isinstance(result, dict)
            or any(not isinstance(result.get(key), str) for key in ("url", "username", "password"))
        ):
            raise ProbeError("The remembered login is damaged. Sign in again.")
        return result

    def last_account(self):
        result = self.setting("last_account")
        if result is not None and (
            not isinstance(result, dict)
            or any(not isinstance(result.get(key), str) for key in ("url", "username"))
        ):
            raise ProbeError("The last account setting is damaged. Sign in again.")
        return result

    def forget_credentials(self):
        try:
            with closing(sqlite3.connect(self.path)) as connection, connection:
                connection.execute("DELETE FROM settings WHERE name = 'remembered_login'")
        except sqlite3.Error:
            raise ProbeError("Could not remove remembered credentials. Check disk access.") from None

    def save_login(self, url, username, password, remember):
        url = normalize_url(url)
        account = self.encode({"url": url, "username": username})
        credentials = (
            self.encode({"url": url, "username": username, "password": password})
            if remember else None
        )
        try:
            with closing(sqlite3.connect(self.path)) as connection, connection:
                connection.execute(
                    "INSERT OR REPLACE INTO settings VALUES ('last_account', ?)", (account,)
                )
                if credentials is None:
                    connection.execute("DELETE FROM settings WHERE name = 'remembered_login'")
                else:
                    connection.execute(
                        "INSERT OR REPLACE INTO settings VALUES ('remembered_login', ?)",
                        (credentials,),
                    )
        except sqlite3.Error:
            raise ProbeError("Cannot save sign-in preferences. Check disk access.") from None

    def save_archive(self, url, username, year, school_name, folders):
        if set(folders) != set(FOLDERS):
            raise ProbeError("An incomplete folder set cannot replace the offline archive.")
        for messages in folders.values():
            for message in messages:
                validate_message(message)
        payload = self.encode(folders)
        synced_at = datetime.now(timezone.utc).isoformat()
        try:
            with closing(sqlite3.connect(self.path)) as connection, connection:
                connection.execute(
                    "INSERT OR REPLACE INTO archives VALUES (?, ?, ?, ?, ?, ?)",
                    (normalize_url(url), username, year.start_year, school_name, payload, synced_at),
                )
        except sqlite3.Error:
            raise ProbeError("Cannot save messages offline. Check disk space and access.") from None
        return synced_at

    def load_archive(self, url, username, year):
        try:
            with closing(sqlite3.connect(self.path)) as connection:
                row = connection.execute(
                    "SELECT school_name, messages, synced_at FROM archives "
                    "WHERE url = ? AND username = ? AND year = ?",
                    (normalize_url(url), username, year.start_year),
                ).fetchone()
        except sqlite3.Error:
            raise ProbeError("Cannot read offline messages. Check disk access.") from None
        if row is None:
            return None
        folders = self.decode(row[1])
        if not isinstance(folders, dict) or set(folders) != set(FOLDERS):
            raise ProbeError("The offline message archive is damaged. Synchronize again.")
        for messages in folders.values():
            if not isinstance(messages, list):
                raise ProbeError("The offline message archive is damaged. Synchronize again.")
            for message in messages:
                validate_message(message)
        return row[0], folders, row[2]

    def attachment_info(self, url, username, identifier):
        try:
            with closing(sqlite3.connect(self.path)) as connection:
                row = connection.execute(
                    "SELECT metadata FROM attachments WHERE url = ? AND username = ? AND id = ?",
                    (normalize_url(url), username, identifier),
                ).fetchone()
        except sqlite3.Error:
            raise ProbeError("Cannot read cached attachment status. Check disk access.") from None
        if row is None:
            return None
        info = self.decode(row[0])
        if (
            not isinstance(info, dict)
            or not isinstance(info.get("name"), str)
            or info["name"] != attachment_filename({"Name": info["name"]})
            or type(info.get("size")) is not int
            or info["size"] < 0
            or not isinstance(info.get("sha256"), str)
        ):
            raise ProbeError("Cached attachment metadata is damaged.")
        return info

    def save_attachment(self, url, username, attachment, chunks, *, expected_size=None, content_type=""):
        key = (normalize_url(url), username, attachment_id(attachment))
        size = 0
        digest = hashlib.sha256()
        try:
            with closing(sqlite3.connect(self.path)) as connection, connection:
                connection.execute(
                    "DELETE FROM attachment_chunks WHERE url = ? AND username = ? AND id = ?", key
                )
                for index, chunk in enumerate(chunks):
                    size += len(chunk)
                    digest.update(chunk)
                    connection.execute(
                        "INSERT INTO attachment_chunks VALUES (?, ?, ?, ?, ?)",
                        (*key, index, self.protection.protect(chunk)),
                    )
                if expected_size is not None and size != expected_size:
                    raise ProbeError(
                        "The attachment download was incomplete. Retry sync; "
                        "no incomplete copy was saved."
                    )
                metadata = self.encode({
                    "name": attachment_filename(attachment), "size": size,
                    "sha256": digest.hexdigest(), "content_type": content_type,
                })
                connection.execute(
                    "INSERT OR REPLACE INTO attachments VALUES (?, ?, ?, ?)", (*key, metadata)
                )
        except sqlite3.Error:
            raise ProbeError("Cannot cache an attachment. Check disk space and access.") from None
        return size

    def export_attachment(self, url, username, identifier, destination):
        info = self.attachment_info(url, username, identifier)
        if info is None:
            raise ProbeError("This attachment is not downloaded. Sign in and sync first.")
        destination = Path(destination)
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(
                prefix=".obk-", suffix=".tmp", dir=destination.parent, delete=False
            ) as output:
                temporary = Path(output.name)
                with closing(sqlite3.connect(self.path)) as connection:
                    digest = hashlib.sha256()
                    size = 0
                    rows = connection.execute(
                        "SELECT chunk_index, payload FROM attachment_chunks "
                        "WHERE url = ? AND username = ? AND id = ? ORDER BY chunk_index",
                        (normalize_url(url), username, identifier),
                    )
                    for index, (stored_index, payload) in enumerate(rows):
                        if stored_index != index:
                            raise ProbeError("The cached attachment is incomplete.")
                        chunk = self.protection.unprotect(payload)
                        digest.update(chunk)
                        size += len(chunk)
                        output.write(chunk)
                    if size != info["size"] or digest.hexdigest() != info["sha256"]:
                        raise ProbeError("The cached attachment failed its integrity check.")
            if os.name == "nt":
                # Preserve Windows' downloaded-file security prompts on exported copies.
                Path(str(temporary) + ":Zone.Identifier").write_text(
                    "[ZoneTransfer]\nZoneId=3\n", encoding="ascii"
                )
            os.replace(temporary, destination)
            temporary = None
        except (OSError, sqlite3.Error):
            raise ProbeError(
                "Cannot write the attachment copy. Check disk access and that "
                "Windows downloaded-file protection is supported by the destination."
            ) from None
        finally:
            if temporary is not None:
                try:
                    temporary.unlink(missing_ok=True)
                except OSError:
                    raise ProbeError(
                        "Could not remove a temporary decrypted attachment copy "
                        "from the destination folder."
                    ) from None
        return destination


class MessageHTML(HTMLParser):
    """Render inert text with basic formatting; never execute HTML or fetch resources."""

    STYLES = {
        "b": "bold", "strong": "bold", "i": "italic", "em": "italic",
        "u": "underline", "h1": "heading", "h2": "heading", "h3": "heading",
    }
    BLOCKS = {"p", "div", "ul", "ol", "li", "tr", "h1", "h2", "h3", "blockquote"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []
        self.active = []
        self.hidden = 0
        self.preformatted = 0
        self.links = []

    def add(self, text):
        if text:
            self.parts.append((text, tuple(sorted(set(style for _, style in self.active)))))

    def newline(self):
        if self.parts and not self.parts[-1][0].endswith("\n"):
            self.add("\n")

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style", "iframe", "object"):
            self.hidden += 1
        if self.hidden:
            return
        if tag in self.BLOCKS:
            self.newline()
        if tag in self.STYLES:
            self.active.append((tag, self.STYLES[tag]))
        if tag == "pre":
            self.preformatted += 1
            self.newline()
        if tag == "a":
            self.links.append(dict(attrs).get("href", ""))
        if tag == "br":
            self.add("\n")
        elif tag == "li":
            self.add("- ")
        elif tag in ("td", "th"):
            self.add("\t")
        elif tag == "img":
            alt = dict(attrs).get("alt")
            self.add(f"[Image: {alt}]" if alt else "[Image not loaded]")

    def handle_endtag(self, tag):
        if tag in ("script", "style", "iframe", "object") and self.hidden:
            self.hidden -= 1
            return
        if self.hidden:
            return
        if tag == "a" and self.links:
            href = self.links.pop()
            if isinstance(href, str) and href.startswith(("https://", "http://", "mailto:")):
                self.add(f" ({href})")
        if tag in self.STYLES:
            for index in range(len(self.active) - 1, -1, -1):
                if self.active[index][0] == tag:
                    del self.active[index]
                    break
        if tag == "pre":
            self.preformatted = max(0, self.preformatted - 1)
        if tag in self.BLOCKS or tag == "pre":
            self.newline()

    def handle_data(self, data):
        if not self.hidden:
            self.add(data if self.preformatted else re.sub(r"\s+", " ", data))


def render_message(source):
    parser = MessageHTML()
    parser.feed(source)
    parser.close()
    return parser.parts
