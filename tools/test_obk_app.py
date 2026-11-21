import tempfile
import tkinter as tk
import unittest
from pathlib import Path
from unittest.mock import patch

from api_probe import ProbeError
from obk_app import KomensWindow
from obk_core import FOLDERS, LocalStore, SchoolYear
from test_obk_core import TestProtection, message


class KomensWindowTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.store = LocalStore(
            Path(self.directory.name) / "cache.sqlite3", TestProtection()
        )
        self.root = tk.Tk()
        self.root.withdraw()
        self.addCleanup(self.close_root)
        self.app = KomensWindow(self.root, self.store, show_login=False)
        self.app.year = SchoolYear(2026)
        self.url = "https://school.example"
        self.folders = {folder: [] for folder in FOLDERS}
        self.folders["received"] = [
            message(
                index, Title=f"Message {index}",
                Text="<p>Hello <b>reader</b>.</p>", Read=False,
                Sender={"Name": "Example Teacher"},
            ) for index in range(23)
        ]
        self.folders["sent"] = [message("sent-1", Title="Previously sent message")]
        self.store.save_archive(
            self.url, "user", self.app.year, "Example School", self.folders
        )

    def close_root(self):
        for callback in self.root.tk.call("after", "info"):
            self.root.after_cancel(callback)
        self.root.destroy()

    def test_tree_list_and_selected_content_are_offline_and_read_only(self):
        self.app.activate_account(self.url, "user")
        self.assertEqual(self.app.tree.item("school", "text"), "Example School")
        self.assertEqual(self.app.tree.get_children("school"), tuple(FOLDERS))
        self.assertEqual(len(self.app.messages.get_children()), 23)
        self.app.messages.selection_set("0")
        self.app.select_message()
        self.assertIn("Hello reader.", self.app.content.get("1.0", "end"))
        self.assertEqual(self.app.subject.get(), "Message 0")
        self.assertEqual(str(self.app.content["state"]), "disabled")
        self.assertFalse(self.app.folders["received"][0]["Read"])
        self.app.tree.selection_set("sent")
        self.app.select_folder()
        self.assertEqual(self.app.list_title.get(), "Sent")
        self.assertEqual(len(self.app.messages.get_children()), 1)
        self.assertEqual(self.app.subject.get(), "Select a message")

    def test_account_switch_clears_previous_content_even_if_new_cache_fails(self):
        self.app.activate_account(self.url, "user")
        self.app.messages.selection_set("0")
        self.app.select_message()
        with patch.object(self.store, "load_archive", side_effect=ProbeError("Damaged cache")):
            with self.assertRaises(ProbeError):
                self.app.activate_account(self.url, "another-user")
        self.assertEqual(self.app.account, (self.url, "another-user"))
        self.assertEqual(self.app.messages.get_children(), ())
        self.assertEqual(self.app.content.get("1.0", "end").strip(), "")

    def test_status_and_first_messages_remain_visible_at_minimum_window_size(self):
        self.root.deiconify()
        self.root.geometry("880x620")
        self.root.update()
        self.app.activate_account(self.url, "user")
        self.root.update()
        self.assertTrue(self.app.status_label.winfo_ismapped())
        self.assertLessEqual(
            self.app.status_label.winfo_rooty() + self.app.status_label.winfo_height(),
            self.root.winfo_rooty() + self.root.winfo_height(),
        )
        self.assertEqual(self.app.messages.yview()[0], 0)

    def test_login_remembers_only_when_opted_in_and_clears_password_on_submit(self):
        self.app.open_login()
        dialog = self.app.login
        self.assertFalse(dialog.remember.get())
        dialog.url.set(self.url)
        dialog.username.set("user")
        dialog.password.set("local-password")
        with patch.object(self.app, "synchronize") as synchronize:
            dialog.sign_in()
        self.assertEqual(dialog.password.get(), "")
        synchronize.assert_called_once_with(self.url, "user", "local-password", False, False)
        self.assertIsNone(self.store.remembered_login())
        dialog.set_busy(False)
        dialog.close()

    def test_remembered_login_prefills_popup_and_switch_off_removes_saved_password(self):
        self.store.save_login(self.url, "user", "local-password", True)
        self.app.open_login()
        dialog = self.app.login
        self.assertTrue(dialog.remember.get())
        self.assertEqual(dialog.password.get(), "local-password")
        dialog.remember.set(False)
        dialog.remember_changed()
        self.assertIsNone(self.store.remembered_login())
        dialog.offline()
        self.assertIsNone(self.app.login)
        self.assertEqual(len(self.app.messages.get_children()), 23)

    def test_sync_success_saves_all_folders_and_returns_to_main_window(self):
        self.app.open_login()
        with patch("obk_app.KomensClient") as constructor:
            client = constructor.return_value
            client.sign_in.return_value = ("token", 3600)
            client.school_name.return_value = "Example School"
            client.fetch_year.return_value = self.folders
            client.download_attachments.return_value = 0
            self.app.sync_worker(self.url, "user", "local-password", True, self.app.year)
        self.app.poll()
        self.assertIsNone(self.app.login)
        self.assertEqual(len(self.app.messages.get_children()), 23)
        self.assertEqual(self.store.remembered_login()["password"], "local-password")
        self.assertIn("Synchronized 24 messages", self.app.status.get())

    def test_repeated_sync_passes_cached_messages_unless_full_refresh_is_requested(self):
        for full_refresh in (False, True):
            with self.subTest(full_refresh=full_refresh), patch("obk_app.KomensClient") as constructor:
                client = constructor.return_value
                client.sign_in.return_value = ("token", 3600)
                client.school_name.return_value = "Example School"
                client.fetch_year.return_value = self.folders
                client.download_attachments.return_value = 0
                self.app.sync_worker(
                    self.url, "user", "password", False, self.app.year, full_refresh
                )
                self.assertEqual(
                    client.fetch_year.call_args.kwargs["cached"],
                    None if full_refresh else self.folders,
                )

    def test_attachment_open_uses_default_windows_viewer_without_network(self):
        attachment = {"Id": "a", "Name": "agenda.pdf"}
        self.folders["received"][0]["Attachments"] = [attachment]
        self.store.save_attachment(self.url, "user", attachment, [b"%PDF-example"])
        self.store.save_archive(
            self.url, "user", self.app.year, "Example School", self.folders
        )
        self.app.activate_account(self.url, "user")
        self.app.messages.selection_set("0")
        self.app.select_message()
        self.assertFalse(self.app.open_button.instate(["disabled"]))
        self.assertIn("Ready offline", self.app.attachment_status.get())
        with patch("obk_app.messagebox.askyesno", return_value=True), patch(
            "obk_app.threading.Thread"
        ) as thread:
            thread.return_value.start.side_effect = lambda: thread.call_args.kwargs["target"](
                *thread.call_args.kwargs["args"]
            )
            self.app.open_button.invoke()
        copy = thread.call_args.kwargs["args"][3]
        with patch("obk_app.os.startfile", create=True) as startfile:
            self.app.poll()
        startfile.assert_called_once_with(copy)
        self.assertEqual(copy.name, "agenda.pdf")
        self.assertEqual(copy.read_bytes(), b"%PDF-example")

    def test_missing_attachment_is_disabled_and_executable_can_only_be_saved(self):
        for name, cached in (("agenda.pdf", False), ("program.exe", True)):
            with self.subTest(name=name):
                attachment = {"Id": name, "Name": name}
                self.folders["received"][0]["Attachments"] = [attachment]
                if cached:
                    self.store.save_attachment(self.url, "user", attachment, [b"content"])
                self.store.save_archive(
                    self.url, "user", self.app.year, "Example School", self.folders
                )
                self.app.activate_account(self.url, "user")
                self.app.messages.selection_set("0")
                self.app.select_message()
                self.assertTrue(self.app.open_button.instate(["disabled"]))
                self.assertEqual(self.app.save_button.instate(["disabled"]), not cached)

    def test_attachment_controls_and_content_remain_visible_at_minimum_size(self):
        attachment = {"Id": "a", "Name": "agenda.pdf"}
        self.folders["received"][0]["Attachments"] = [attachment]
        self.store.save_attachment(self.url, "user", attachment, [b"file"])
        self.store.save_archive(
            self.url, "user", self.app.year, "Example School", self.folders
        )
        self.root.deiconify()
        self.root.geometry("880x620")
        self.root.update()
        self.app.activate_account(self.url, "user")
        self.app.messages.selection_set("0")
        self.app.select_message()
        self.root.update()
        self.assertTrue(self.app.open_button.winfo_ismapped())
        self.assertGreaterEqual(self.app.content.winfo_height(), 100)
        self.assertLessEqual(
            self.app.save_button.winfo_rootx() + self.app.save_button.winfo_width(),
            self.root.winfo_rootx() + self.root.winfo_width(),
        )
    def test_sync_failure_keeps_previous_snapshot_and_reports_error(self):
        self.app.activate_account(self.url, "user")
        with patch("obk_app.KomensClient") as constructor:
            client = constructor.return_value
            client.sign_in.return_value = ("token", 3600)
            client.school_name.return_value = "Example School"
            client.fetch_year.side_effect = ProbeError("Noticeboard: access rejected")
            self.app.sync_worker(self.url, "user", "password", False, self.app.year)
        self.app.poll()
        self.assertIn("Noticeboard: access rejected", self.app.status.get())
        self.assertEqual(
            self.store.load_archive(self.url, "user", self.app.year)[1], self.folders
        )
        self.assertEqual(len(self.app.messages.get_children()), 23)

    def test_attachment_failure_does_not_replace_previous_message_archive(self):
        new_folders = {folder: [] for folder in FOLDERS}
        new_folders["received"] = [message("new")]
        with patch("obk_app.KomensClient") as constructor:
            client = constructor.return_value
            client.sign_in.return_value = ("token", 3600)
            client.school_name.return_value = "Example School"
            client.fetch_year.return_value = new_folders
            client.download_attachments.side_effect = ProbeError("Attachment 1: interrupted")
            self.app.sync_worker(self.url, "user", "password", False, self.app.year)
        self.app.poll()
        self.assertIn("Attachment 1: interrupted", self.app.status.get())
        self.assertEqual(
            self.store.load_archive(self.url, "user", self.app.year)[1], self.folders
        )


if __name__ == "__main__":
    unittest.main()
