import io
import json
import os
import sqlite3
import tempfile
import unittest
from contextlib import closing
from datetime import date
from pathlib import Path

from api_probe import ProbeError
from obk_core import (
    ATTACHMENT_CHUNK_SIZE, FOLDERS, KomensClient, LocalStore, SchoolYear,
    WindowsProtection, attachment_filename, render_message,
)
from test_api_probe import FakeOpener


def message(identifier, sent="2026-09-01T00:00:00+02:00", **extra):
    return {
        "Id": str(identifier), "Title": "A message", "Text": "<p>Content</p>",
        "SentDate": sent, "Attachments": [], **extra,
    }


class TestProtection:
    def protect(self, value):
        return b"TEST:" + bytes(byte ^ 0xA5 for byte in value)

    def unprotect(self, value):
        if not value.startswith(b"TEST:"):
            raise ProbeError("Cannot decrypt test data.")
        return bytes(byte ^ 0xA5 for byte in value[5:])


class SchoolYearTests(unittest.TestCase):
    def test_september_rollover_and_august_inclusion(self):
        self.assertEqual(SchoolYear.current(date(2026, 8, 31)).label, "2025/2026")
        self.assertEqual(SchoolYear.current(date(2026, 9, 1)).label, "2026/2027")
        self.assertEqual(SchoolYear.current(date(2027, 1, 1)).label, "2026/2027")

    def test_pagination_keeps_loading_short_pages_beyond_ten_messages(self):
        first = [message(index) for index in range(10)]
        second = [message(index) for index in range(10, 23)]
        opener = FakeOpener(
            [{"Messages": first}, {"Messages": second}, {"Messages": []}]
        )
        progress = []
        result = KomensClient("https://school.example", opener).fetch_folder(
            "token", "received", SchoolYear(2026),
            lambda *args: progress.append(args),
        )
        self.assertEqual(len(result), 23)
        bodies = [json.loads(request.data) for request in opener.requests]
        self.assertEqual([body["Pagination"]["PageNumber"] for body in bodies], [1, 2, 3])
        self.assertEqual(bodies[0]["DateFrom"], "2026-09-01T00:00:00.000")
        self.assertEqual(bodies[0]["DateTo"], "2027-08-31T23:59:59.999")
        self.assertEqual(progress[-1], ("received", 2, 23))

    def test_server_filter_is_also_enforced_locally_with_inclusive_august_31(self):
        opener = FakeOpener([
            {"Messages": [
                message(1, "2026-08-31T23:59:59+02:00"),
                message(2, "2026-09-01T00:00:00+02:00"),
                message(3, "2027-08-31T23:59:59.999+02:00"),
                message(4, "2027-09-01T00:00:00+02:00"),
            ]},
            {"Messages": []},
        ])
        result = KomensClient("https://school.example", opener).fetch_folder(
            "token", "received", SchoolYear(2026)
        )
        self.assertEqual([item["Id"] for item in result], ["3", "2"])

    def test_overlapping_pages_deduplicate_but_repeated_page_fails(self):
        opener = FakeOpener([
            {"Messages": [message(1), message(2)]},
            {"Messages": [message(2), message(3)]},
            {"Messages": []},
        ])
        client = KomensClient("https://school.example", opener)
        self.assertEqual(len(client.fetch_folder("t", "sent", SchoolYear(2026))), 3)
        repeated = FakeOpener([
            {"Messages": [message(1)]}, {"Messages": [message(1)]},
        ])
        with self.assertRaisesRegex(ProbeError, "repeated a page"):
            KomensClient("https://school.example", repeated).fetch_folder(
                "t", "received", SchoolYear(2026)
            )

    def test_all_three_folders_use_read_only_list_endpoints(self):
        opener = FakeOpener([{"Messages": []}] * 3)
        result = KomensClient("https://school.example", opener).fetch_year(
            "t", SchoolYear(2026)
        )
        self.assertEqual(set(result), set(FOLDERS))
        self.assertEqual(
            [request.full_url.rsplit("/", 1)[-1] for request in opener.requests],
            ["received", "sent", "noticeboard"],
        )
        self.assertTrue(all(request.get_method() == "POST" for request in opener.requests))

    def test_invalid_date_and_later_page_failure_are_not_partial_success(self):
        for second in ({"Messages": [message(2, "invalid")]}, {"NotMessages": []}):
            opener = FakeOpener([{"Messages": [message(1)]}, second])
            with self.subTest(second=second), self.assertRaises(ProbeError):
                KomensClient("https://school.example", opener).fetch_folder(
                    "t", "received", SchoolYear(2026)
                )

    def test_incremental_fetch_preserves_old_messages_and_overlaps_latest_timestamp(self):
        cached = [
            message("old", "2026-09-01T08:00:00+02:00"),
            message("latest", "2026-11-21T09:30:00+01:00", Text="Old content"),
        ]
        opener = FakeOpener([
            {"Messages": [
                message("latest", "2026-11-21T09:30:00+01:00", Text="Updated content"),
                message("same-time", "2026-11-21T09:30:00+01:00"),
                message("new", "2026-11-21T10:00:00+01:00"),
            ]},
            {"Messages": []},
        ])
        result = KomensClient("https://school.example", opener).fetch_folder(
            "t", "received", SchoolYear(2026), cached=cached
        )
        body = json.loads(opener.requests[0].data)
        self.assertEqual(body["DateFrom"], "2026-11-21T09:30:00.000")
        self.assertEqual({item["Id"] for item in result}, {"old", "latest", "same-time", "new"})
        latest = next(item for item in result if item["Id"] == "latest")
        self.assertEqual(latest["Text"], "Updated content")

    def test_empty_incremental_response_preserves_cache_and_new_year_starts_from_september(self):
        cached = [message("old", "2025-11-21T09:30:00+01:00")]
        opener = FakeOpener([{"Messages": []}])
        result = KomensClient("https://school.example", opener).fetch_folder(
            "t", "received", SchoolYear(2026), cached=cached
        )
        self.assertEqual(result, [])
        self.assertEqual(
            json.loads(opener.requests[0].data)["DateFrom"], "2026-09-01T00:00:00.000"
        )
        current = [message("latest")]
        client = KomensClient("https://school.example", FakeOpener([{"Messages": []}]))
        self.assertEqual(
            client.fetch_folder("t", "received", SchoolYear(2026), cached=current), current
        )


class LocalStoreTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "cache.sqlite3"
        self.store = LocalStore(self.path, TestProtection())
        self.year = SchoolYear(2026)

    def test_credentials_are_opt_in_and_can_be_removed_without_deleting_cache(self):
        self.store.save_login("https://school.example/", "u", "secret-password", False)
        self.assertIsNone(self.store.remembered_login())
        self.store.save_login("https://school.example/", "u", "secret-password", True)
        self.assertEqual(self.store.remembered_login()["password"], "secret-password")
        self.assertNotIn(b"secret-password", self.path.read_bytes())
        self.store.save_login("https://school.example/", "u", "secret-password", False)
        self.assertIsNone(self.store.remembered_login())
        self.assertEqual(
            self.store.last_account(), {"url": "https://school.example", "username": "u"}
        )

    def test_offline_snapshot_survives_reopen_and_is_account_and_year_scoped(self):
        folders = {folder: [] for folder in FOLDERS}
        folders["received"] = [message(1, Text="<p>Private message body</p>")]
        self.store.save_archive("https://school.example", "u", self.year, "School", folders)
        reopened = LocalStore(self.path, TestProtection())
        self.assertEqual(
            reopened.load_archive("https://school.example/", "u", self.year)[1], folders
        )
        for url, username, year in (
            ("https://school.example", "different-user", self.year),
            ("https://another.example", "u", self.year),
            ("https://school.example", "u", SchoolYear(2025)),
        ):
            self.assertIsNone(reopened.load_archive(url, username, year))
        self.assertNotIn(b"Private message body", self.path.read_bytes())
        reopened.forget_credentials()
        self.assertIsNotNone(reopened.load_archive("https://school.example", "u", self.year))

    def test_incomplete_or_invalid_sync_cannot_replace_existing_snapshot(self):
        folders = {folder: [] for folder in FOLDERS}
        folders["received"] = [message(1)]
        self.store.save_archive("https://school.example", "u", self.year, "School", folders)
        for incomplete in ({"received": []}, {**folders, "sent": [{}]}):
            with self.subTest(incomplete=incomplete), self.assertRaises(ProbeError):
                self.store.save_archive(
                    "https://school.example", "u", self.year, "School", incomplete
                )
        cached = self.store.load_archive("https://school.example", "u", self.year)
        self.assertEqual(cached[1], folders)

    def test_decryption_failure_does_not_fall_back_to_plaintext(self):
        self.store.save_login("https://school.example", "u", "p", True)
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute(
                "UPDATE settings SET value = ? WHERE name = 'remembered_login'",
                (b'{"password":"plaintext"}',),
            )
        with self.assertRaises(ProbeError):
            self.store.remembered_login()

    @unittest.skipUnless(os.name == "nt", "Windows DPAPI is platform-specific")
    def test_real_windows_encryption_roundtrip_and_protected_database(self):
        protection = WindowsProtection()
        ciphertext = protection.protect(b"secret-password")
        self.assertNotIn(b"secret-password", ciphertext)
        self.assertEqual(protection.unprotect(ciphertext), b"secret-password")
        store = LocalStore(Path(self.directory.name) / "windows.sqlite3", protection)
        store.save_login("https://school.example", "u", "secret-password", True)
        self.assertEqual(store.remembered_login()["password"], "secret-password")
        self.assertNotIn(b"secret-password", store.path.read_bytes())


class MessageRenderingTests(unittest.TestCase):
    def test_content_formatting_entities_and_links_without_active_html(self):
        parts = render_message(
            '<p>Hello &amp; <strong>school</strong><br>Next line</p>'
            '<a href="https://school.example/info">Details</a>'
            '<script>alert("do not execute")</script>'
            '<img src="https://tracker.example/pixel" alt="Diagram">'
        )
        text = "".join(value for value, tags in parts)
        self.assertIn("Hello & school\nNext line", text)
        self.assertIn(("school", ("bold",)), parts)
        self.assertIn("Details (https://school.example/info)", text)
        self.assertNotIn("alert", text)
        self.assertNotIn("tracker.example", text)
        self.assertIn("[Image: Diagram]", text)

    def test_nested_lists_table_and_preformatted_text_remain_readable(self):
        parts = render_message(
            "<ul><li>First</li><li>Second</li></ul>"
            "<table><tr><td>A</td><td>B</td></tr></table>"
            "<pre>line 1\n  line 2</pre>"
        )
        text = "".join(value for value, tags in parts)
        self.assertIn("- First\n- Second", text)
        self.assertIn("\tA\tB", text)
        self.assertIn("line 1\n  line 2", text)


class AttachmentResponse(io.BytesIO):
    def __init__(self, payload, content_length=None):
        super().__init__(payload)
        self.headers = {"Content-Type": "application/octet-stream"}
        if content_length is not None:
            self.headers["Content-Length"] = str(content_length)
        self.read_sizes = []

    def read(self, size=-1):
        self.read_sizes.append(size)
        return super().read(size)


class AttachmentOpener:
    def __init__(self, responses):
        self.responses = iter(responses)
        self.requests = []

    def open(self, request, timeout):
        self.requests.append(request)
        return next(self.responses)


class AttachmentTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "cache.sqlite3"
        self.store = LocalStore(self.path, TestProtection())
        self.url = "https://school.example"
        self.attachment = {"Id": "file/one?x", "Name": "agenda.pdf"}
        self.folders = {folder: [] for folder in FOLDERS}
        self.folders["received"] = [message("1", Attachments=[self.attachment])]
        self.folders["sent"] = [message("2", Attachments=[self.attachment])]

    def test_streamed_download_is_encrypted_deduplicated_and_reused_offline(self):
        payload = b"private-file-content\n" * 150000
        response = AttachmentResponse(payload, len(payload))
        opener = AttachmentOpener([response])
        client = KomensClient(self.url, opener)
        self.assertEqual(
            client.download_attachments("token", self.folders, self.store, "user"), 1
        )
        self.assertEqual(len(opener.requests), 1)
        request = opener.requests[0]
        self.assertEqual(request.get_method(), "GET")
        self.assertEqual(
            request.full_url, self.url + "/api/3/komens/attachment/file%2Fone%3Fx"
        )
        self.assertEqual(request.get_header("Authorization"), "Bearer token")
        self.assertTrue(all(size == ATTACHMENT_CHUNK_SIZE for size in response.read_sizes))
        self.assertNotIn(b"private-file-content", self.path.read_bytes())
        no_requests = AttachmentOpener([])
        self.assertEqual(
            KomensClient(self.url, no_requests).download_attachments(
                "another-token", self.folders, self.store, "user"
            ), 1,
        )
        self.assertEqual(no_requests.requests, [])
        destination = Path(self.directory.name) / "export.pdf"
        self.store.export_attachment(self.url, "user", self.attachment["Id"], destination)
        self.assertEqual(destination.read_bytes(), payload)
        if os.name == "nt":
            self.assertIn("ZoneId=3", Path(str(destination) + ":Zone.Identifier").read_text())
        self.assertIsNone(self.store.attachment_info(self.url, "other-user", self.attachment["Id"]))

    def test_truncated_download_does_not_become_cached_and_retry_downloads_again(self):
        broken = KomensClient(
            self.url, AttachmentOpener([AttachmentResponse(b"short", 100)])
        )
        with self.assertRaisesRegex(ProbeError, "incomplete"):
            broken.download_attachments("t", self.folders, self.store, "user")
        self.assertIsNone(
            self.store.attachment_info(self.url, "user", self.attachment["Id"])
        )
        opener = AttachmentOpener([AttachmentResponse(b"complete", 8)])
        KomensClient(self.url, opener).download_attachments("t", self.folders, self.store, "user")
        self.assertEqual(len(opener.requests), 1)

    def test_stream_error_rolls_back_chunks_and_preserves_existing_file(self):
        self.store.save_attachment(self.url, "user", self.attachment, [b"original"])
        def broken_chunks():
            yield b"replacement"
            raise OSError("interrupted")
        with self.assertRaises(OSError):
            self.store.save_attachment(self.url, "user", self.attachment, broken_chunks())
        destination = Path(self.directory.name) / "original.pdf"
        self.store.export_attachment(self.url, "user", self.attachment["Id"], destination)
        self.assertEqual(destination.read_bytes(), b"original")

    def test_corrupt_cache_cannot_overwrite_destination_with_partial_data(self):
        self.store.save_attachment(self.url, "user", self.attachment, [b"original"])
        with closing(sqlite3.connect(self.path)) as connection, connection:
            connection.execute(
                "UPDATE attachment_chunks SET payload = ?", (self.store.protection.protect(b"bad"),)
            )
        destination = Path(self.directory.name) / "existing.pdf"
        destination.write_bytes(b"keep this file")
        with self.assertRaisesRegex(ProbeError, "integrity"):
            self.store.export_attachment(self.url, "user", self.attachment["Id"], destination)
        self.assertEqual(destination.read_bytes(), b"keep this file")
        self.assertEqual(list(Path(self.directory.name).glob(".obk-*")), [])

    def test_safe_filenames_do_not_escape_folder_or_use_windows_device_names(self):
        for original, expected in (
            ("../../agenda.pdf", "agenda.pdf"),
            ("..\\..\\agenda.pdf", "agenda.pdf"),
            ("CON.pdf", "_CON.pdf"),
            ("LPT1.txt", "_LPT1.txt"),
            ("bad:name?.pdf", "bad_name_.pdf"),
            ("file.pdf. ", "file.pdf"),
        ):
            with self.subTest(original=original):
                self.assertEqual(attachment_filename({"Name": original}), expected)
        self.assertLessEqual(len(attachment_filename({"Name": "x" * 200 + ".pdf"})), 180)


if __name__ == "__main__":
    unittest.main()
