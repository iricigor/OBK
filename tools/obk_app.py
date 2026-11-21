"""Windows-first, read-only Komens desktop client."""

import argparse
import json
import os
import queue
import tempfile
import threading
import tkinter as tk
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from api_probe import DEFAULT_URL, ProbeError, normalize_url
from obk_core import (
    FOLDERS, OPENABLE_EXTENSIONS, KomensClient, LocalStore, SchoolYear,
    attachment_filename, attachment_id, message_date, render_message,
)


def one_line(value):
    return " ".join(value.split()) if isinstance(value, str) else ""


class LoginDialog:
    def __init__(self, app, initial, remembered=False, error=""):
        self.app = app
        self.window = tk.Toplevel(app.root)
        self.window.title("Sign in to your school")
        self.window.transient(app.root)
        self.window.resizable(False, False)
        self.window.protocol("WM_DELETE_WINDOW", self.offline)
        self.window.grab_set()
        frame = ttk.Frame(self.window, padding=24)
        frame.pack(fill="both", expand=True)
        ttk.Label(frame, text="Sign in to Komens", style="Heading.TLabel").grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 8)
        )
        ttk.Label(
            frame, text="Download the current school year for offline reading.",
            wraplength=440,
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(0, 18))
        self.url = tk.StringVar(value=initial.get("url", DEFAULT_URL))
        self.username = tk.StringVar(value=initial.get("username", ""))
        self.password = tk.StringVar(value=initial.get("password", ""))
        self.remember = tk.BooleanVar(value=remembered)
        self.full_refresh = tk.BooleanVar(value=False)
        self.entries = []
        for index, (label, variable, hidden) in enumerate(
            [
                ("School base URL (HTTPS)", self.url, False),
                ("Username", self.username, False),
                ("Password", self.password, True),
            ]
        ):
            row = 2 + index * 2
            ttk.Label(frame, text=label).grid(row=row, column=0, columnspan=2, sticky="w")
            entry = ttk.Entry(
                frame, textvariable=variable, show="*" if hidden else "", width=56
            )
            entry.grid(row=row + 1, column=0, columnspan=2, sticky="ew", pady=(4, 12))
            self.entries.append(entry)
        self.toggle = ttk.Checkbutton(
            frame, text="Remember credentials on this Windows account",
            variable=self.remember, command=self.remember_changed,
        )
        self.toggle.grid(row=8, column=0, columnspan=2, sticky="w", pady=(4, 6))
        self.refresh_toggle = ttk.Checkbutton(
            frame, text="Full school-year refresh (otherwise fetch new messages)",
            variable=self.full_refresh,
        )
        self.refresh_toggle.grid(row=9, column=0, columnspan=2, sticky="w", pady=(0, 10))
        ttk.Label(
            frame,
            text="Saved credentials and message content are encrypted in the local "
            "database using Windows user protection. No sending or confirmations.",
            wraplength=440,
        ).grid(row=10, column=0, columnspan=2, sticky="w", pady=(0, 16))
        self.status = tk.StringVar(value=error)
        ttk.Label(frame, textvariable=self.status, wraplength=440).grid(
            row=11, column=0, columnspan=2, sticky="ew", pady=(0, 12)
        )
        self.offline_button = ttk.Button(
            frame, text="Read offline", command=self.offline
        )
        self.offline_button.grid(row=12, column=0, sticky="w")
        self.sign_in_button = ttk.Button(
            frame, text="Sign in and sync", command=self.sign_in
        )
        self.sign_in_button.grid(row=12, column=1, sticky="e")
        self.window.bind("<Return>", lambda event: self.sign_in())
        self.window.bind("<Escape>", lambda event: self.offline())
        self.window.update_idletasks()
        x = app.root.winfo_rootx() + (app.root.winfo_width() - self.window.winfo_width()) // 2
        y = app.root.winfo_rooty() + (app.root.winfo_height() - self.window.winfo_height()) // 2
        self.window.geometry(f"+{max(0, x)}+{max(0, y)}")
        self.entries[2 if self.username.get() else 1].focus_set()

    def remember_changed(self):
        if not self.remember.get():
            try:
                self.app.store.forget_credentials()
            except ProbeError as error:
                self.status.set(str(error))
                self.remember.set(True)

    def sign_in(self):
        if self.app.busy:
            return
        try:
            url = normalize_url(self.url.get())
            username, password = self.username.get().strip(), self.password.get()
            if not username or not password:
                raise ProbeError("Enter both a username and password.")
            if not self.remember.get():
                self.app.store.forget_credentials()
        except ProbeError as error:
            self.status.set(str(error))
            return
        self.password.set("")
        self.set_busy(True)
        self.status.set("Signing in, then downloading all three folders...")
        self.app.synchronize(
            url, username, password, self.remember.get(), self.full_refresh.get()
        )

    def set_busy(self, busy):
        state = ["disabled"] if busy else ["!disabled"]
        for entry in self.entries:
            entry.state(state)
        for widget in (self.toggle, self.refresh_toggle, self.sign_in_button, self.offline_button):
            widget.state(state)

    def offline(self):
        if self.app.busy:
            return
        try:
            username = self.username.get().strip()
            if username:
                self.app.activate_account(normalize_url(self.url.get()), username)
        except ProbeError as error:
            self.status.set(str(error))
            return
        self.app.status.set("Offline reading. No network requests were made.")
        self.close()

    def close(self):
        self.password.set("")
        self.window.grab_release()
        self.window.destroy()
        self.app.login = None


class KomensWindow:
    def __init__(self, root, store=None, *, show_login=True):
        self.root = root
        self.store = store if store is not None else LocalStore()
        self.year = SchoolYear.current()
        self.results = queue.Queue()
        self.busy = False
        self.exporting = False
        self.login = None
        self.account = None
        self.folders = {folder: [] for folder in FOLDERS}
        self.folder = "received"
        self.school_name = "Your school"
        self.synced_at = None
        self.reading_pane_positioned = False
        root.title("OBK - Offline Komens")
        root.geometry("1180x800")
        root.minsize(880, 620)
        style = ttk.Style(root)
        if "vista" in style.theme_names():
            style.theme_use("vista")
        style.configure("TLabel", font=("Segoe UI", 10))
        style.configure("TButton", font=("Segoe UI", 10))
        style.configure("Heading.TLabel", font=("Segoe UI", 16, "bold"))
        style.configure("Section.TLabel", font=("Segoe UI", 12, "bold"))
        style.configure("Treeview", font=("Segoe UI", 10), rowheight=28)
        style.configure("Treeview.Heading", font=("Segoe UI", 10, "bold"))
        self.build_ui()
        startup_error = ""
        try:
            last = self.store.last_account()
            if last:
                self.activate_account(last["url"], last["username"])
        except ProbeError as error:
            startup_error = str(error)
            self.status.set(startup_error)
        if show_login:
            root.after(150, lambda: self.open_login(startup_error))
        root.after(100, self.poll)

    def build_ui(self):
        header = ttk.Frame(self.root, padding=(18, 14))
        header.pack(fill="x")
        ttk.Label(header, text="OBK", style="Heading.TLabel").pack(side="left")
        self.year_label = tk.StringVar()
        ttk.Label(header, textvariable=self.year_label).pack(side="left", padx=24)
        self.sync_button = ttk.Button(header, text="Sign in / Sync", command=self.open_login)
        self.sync_button.pack(side="right")
        self.forget_button = ttk.Button(
            header, text="Forget credentials", command=self.forget_credentials
        )
        self.forget_button.pack(side="right", padx=(0, 8))
        ttk.Separator(self.root).pack(fill="x")
        panes = ttk.Panedwindow(self.root, orient="horizontal")
        panes.pack(fill="both", expand=True, padx=12, pady=12)
        sidebar = ttk.Frame(panes, width=235)
        panes.add(sidebar, weight=0)
        self.tree = ttk.Treeview(sidebar, show="tree", selectmode="browse")
        self.tree.pack(fill="both", expand=True)
        self.tree.insert("", "end", iid="school", text=self.school_name, open=True)
        for folder, title in FOLDERS.items():
            self.tree.insert("school", "end", iid=folder, text=title)
        self.tree.selection_set("received")
        self.tree.bind("<<TreeviewSelect>>", self.select_folder)
        right = ttk.Panedwindow(panes, orient="vertical")
        self.vertical_panes = right
        right.bind("<Configure>", self.position_reading_pane)
        panes.add(right, weight=1)
        upper = ttk.Frame(right, padding=(8, 0, 0, 0))
        right.add(upper, weight=1)
        self.list_title = tk.StringVar(value="Inbox")
        ttk.Label(upper, textvariable=self.list_title, style="Section.TLabel").pack(
            anchor="w", pady=(0, 10)
        )
        self.list_caption = tk.StringVar(value="Sign in to download messages, or read your cache offline.")
        ttk.Label(upper, textvariable=self.list_caption).pack(anchor="w", pady=(0, 8))
        table = ttk.Frame(upper)
        table.pack(fill="both", expand=True)
        table.rowconfigure(0, weight=1)
        table.columnconfigure(0, weight=1)
        self.messages = ttk.Treeview(
            table, columns=("state", "subject", "person", "date", "files"),
            show="headings", selectmode="browse", height=9,
        )
        for name, title, width, minimum, stretch in (
            ("state", "State", 65, 60, False),
            ("subject", "Subject", 330, 180, True),
            ("person", "From / To", 180, 100, True),
            ("date", "Sent", 145, 130, False),
            ("files", "Files", 45, 40, False),
        ):
            self.messages.heading(name, text=title)
            self.messages.column(name, width=width, minwidth=minimum, stretch=stretch)
        self.messages.tag_configure("unread", font=("Segoe UI", 10, "bold"))
        self.messages.grid(row=0, column=0, sticky="nsew")
        vertical = ttk.Scrollbar(table, orient="vertical", command=self.messages.yview)
        vertical.grid(row=0, column=1, sticky="ns")
        horizontal = ttk.Scrollbar(table, orient="horizontal", command=self.messages.xview)
        horizontal.grid(row=1, column=0, sticky="ew")
        self.messages.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        self.messages.bind("<<TreeviewSelect>>", self.select_message)
        lower = ttk.Frame(right, padding=(8, 16, 0, 0))
        right.add(lower, weight=2)
        self.subject = tk.StringVar(value="Select a message")
        self.subject_label = ttk.Label(
            lower, textvariable=self.subject, style="Section.TLabel", wraplength=850
        )
        self.subject_label.pack(anchor="w", fill="x", pady=(0, 8))
        self.metadata = tk.StringVar(value="Viewing cached messages makes no server requests.")
        self.metadata_label = ttk.Label(lower, textvariable=self.metadata, wraplength=850)
        self.metadata_label.pack(anchor="w", fill="x", pady=(0, 12))
        reader = ttk.Frame(lower)
        reader.pack(fill="both", expand=True)
        self.content = tk.Text(
            reader, wrap="word", font=("Segoe UI", 11), background="#ffffff",
            foreground="#18242e", padx=16, pady=12, borderwidth=1, relief="solid",
            state="disabled", cursor="arrow", height=12, undo=False,
            selectbackground="#005fb8", selectforeground="#ffffff",
        )
        self.content.pack(side="left", fill="both", expand=True)
        scrollbar = ttk.Scrollbar(reader, orient="vertical", command=self.content.yview)
        scrollbar.pack(side="right", fill="y")
        self.content.configure(yscrollcommand=scrollbar.set)
        self.content.tag_configure("bold", font=("Segoe UI", 11, "bold"))
        self.content.tag_configure("italic", font=("Segoe UI", 11, "italic"))
        self.content.tag_configure("underline", underline=True)
        self.content.tag_configure("heading", font=("Segoe UI", 13, "bold"), spacing1=8)
        self.content.tag_configure("muted", foreground="#465763", spacing1=12)
        self.attachment_panel = ttk.Frame(lower, padding=(0, 10, 0, 0))
        self.attachment_panel.columnconfigure(1, weight=1)
        self.attachment_title = ttk.Label(self.attachment_panel, text="Attachments")
        self.attachment_title.grid(row=0, column=0, sticky="w", padx=(0, 8))
        self.attachment_combo = ttk.Combobox(self.attachment_panel, state="readonly")
        self.attachment_combo.grid(row=0, column=1, sticky="ew")
        self.attachment_combo.bind("<<ComboboxSelected>>", self.attachment_selected)
        self.open_button = ttk.Button(
            self.attachment_panel, text="Open", command=lambda: self.export_selected(True)
        )
        self.open_button.grid(row=0, column=2, padx=(8, 0))
        self.save_button = ttk.Button(
            self.attachment_panel, text="Save a copy",
            command=lambda: self.export_selected(False),
        )
        self.save_button.grid(row=0, column=3, padx=(8, 0))
        self.attachment_status = tk.StringVar()
        self.attachment_status_label = ttk.Label(
            self.attachment_panel, textvariable=self.attachment_status, wraplength=600
        )
        self.attachment_status_label.grid(
            row=1, column=0, columnspan=4, sticky="w", pady=(4, 0)
        )
        self.attachment_reader = reader
        self.selected_attachments = []
        lower.bind("<Configure>", self.resize_details)
        footer = ttk.Frame(self.root, padding=(18, 8))
        footer.pack(side="bottom", fill="x", before=panes)
        self.status = tk.StringVar(value="Ready. Sign in to synchronize the current school year.")
        self.status_label = ttk.Label(footer, textvariable=self.status, wraplength=980)
        self.status_label.pack(side="left", fill="x", expand=True)
        self.progress = ttk.Progressbar(footer, mode="indeterminate", length=110)
        footer.bind(
            "<Configure>",
            lambda event: self.status_label.configure(wraplength=max(300, event.width - 180)),
        )
        self.show_folder()

    def resize_details(self, event):
        for label in (self.subject_label, self.metadata_label, self.attachment_status_label):
            label.configure(wraplength=max(200, event.width - 20))

    def position_reading_pane(self, event=None):
        height = self.vertical_panes.winfo_height()
        if not self.reading_pane_positioned and height > 1:
            self.reading_pane_positioned = True
            self.vertical_panes.sashpos(0, int(height * 0.4))

    def open_login(self, error=""):
        if self.busy:
            return
        if self.login is not None:
            self.login.window.lift()
            return
        initial = {"url": self.account[0], "username": self.account[1]} if self.account else {}
        remembered = False
        try:
            saved = self.store.remembered_login()
            if saved:
                initial = saved
                remembered = True
            elif not initial:
                initial = self.store.last_account() or {}
        except ProbeError as problem:
            error = str(problem)
        self.login = LoginDialog(self, initial, remembered, error)

    def forget_credentials(self):
        try:
            self.store.forget_credentials()
            self.status.set("Remembered credentials removed. Offline messages are unchanged.")
        except ProbeError as error:
            self.status.set(str(error))

    def activate_account(self, url, username):
        self.account = (normalize_url(url), username)
        self.school_name = "Your school"
        self.folders = {folder: [] for folder in FOLDERS}
        self.synced_at = None
        self.refresh_tree()
        self.show_folder()
        archive = self.store.load_archive(url, username, self.year)
        if archive:
            self.school_name, self.folders, self.synced_at = archive
        self.refresh_tree()
        self.show_folder()
        self.status.set(self.cache_status())

    def cache_status(self):
        if self.synced_at:
            stamp = datetime.fromisoformat(self.synced_at).astimezone().strftime("%d %b %Y %H:%M")
            return f"Offline archive | Last synchronized {stamp} | No server modification requests."
        return "No offline archive for this account and school year. Sign in to download messages."

    def refresh_tree(self):
        self.tree.item("school", text=self.school_name)
        for folder, title in FOLDERS.items():
            self.tree.item(folder, text=f"{title} ({len(self.folders[folder])})")

    def select_folder(self, event=None):
        selected = self.tree.selection()
        if selected and selected[0] in FOLDERS:
            self.folder = selected[0]
            self.show_folder()

    def show_folder(self):
        self.year_label.set(f"School year {self.year.label}  |  September 1 - August 31")
        self.list_title.set(FOLDERS[self.folder])
        rows = self.messages.get_children()
        if rows:
            self.messages.delete(*rows)
        self.by_id = {}
        for message in self.folders[self.folder]:
            identifier = message["Id"]
            self.by_id[identifier] = message
            sender = message.get("Sender")
            name = sender.get("Name", "") if isinstance(sender, dict) else ""
            if self.folder == "sent":
                name = message.get("RelevantName") or name
            unread = self.folder != "sent" and message.get("Read") is False
            self.messages.insert(
                "", "end", iid=identifier,
                values=(
                    "Unread" if unread else "",
                    one_line(message["Title"]) or "(No subject)",
                    one_line(name),
                    message_date(message).strftime("%d %b %Y %H:%M"),
                    len(message["Attachments"]) or "",
                ),
                tags=("unread",) if unread else (),
            )
        total = len(self.by_id)
        self.messages.yview_moveto(0)
        self.list_caption.set(
            f"{total} messages | School year {self.year.label}"
            if total else f"No cached messages in {FOLDERS[self.folder].lower()} "
            f"for {self.year.label}. Synchronize to check the school."
        )
        self.clear_message()

    def clear_message(self):
        self.subject.set("Select a message")
        self.metadata.set("Viewing cached messages makes no server requests.")
        self.content.configure(state="normal")
        self.content.delete("1.0", "end")
        self.content.configure(state="disabled")
        self.attachment_panel.pack_forget()
        self.selected_attachments = []

    def select_message(self, event=None):
        selected = self.messages.selection()
        if not selected or selected[0] not in self.by_id:
            return
        message = self.by_id[selected[0]]
        self.subject.set(one_line(message["Title"]) or "(No subject)")
        sender = message.get("Sender")
        name = sender.get("Name", "") if isinstance(sender, dict) else ""
        details = (
            f"From: {one_line(name) or '(Not supplied)'}\n"
            f"Sent: {message_date(message).strftime('%d %b %Y %H:%M')} "
            f"| Folder: {FOLDERS[self.folder]}"
        )
        if self.folder == "sent" and message.get("RelevantName"):
            details += f"\nTo: {one_line(message['RelevantName'])}"
        if message.get("CanConfirm") and not message.get("Confirmed"):
            details += "\nConfirmation requested. OBK does not send confirmations."
        self.metadata.set(details)
        self.content.configure(state="normal")
        self.content.delete("1.0", "end")
        parts = render_message(message["Text"])
        for text, tags in parts:
            self.content.insert("end", text, tags)
        if not parts:
            self.content.insert("end", "(No message content)")
        attachments = message["Attachments"]
        self.content.configure(state="disabled")
        self.content.yview_moveto(0)
        self.selected_attachments = attachments
        if attachments:
            self.attachment_title.configure(text=f"Attachments ({len(attachments)})")
            try:
                names = [attachment_filename(attachment) for attachment in attachments]
            except ProbeError as error:
                self.attachment_panel.pack_forget()
                self.selected_attachments = []
                self.status.set(str(error))
                return
            self.attachment_combo.configure(values=names)
            self.attachment_combo.current(0)
            self.attachment_panel.pack(
                side="bottom", fill="x", before=self.attachment_reader
            )
            self.attachment_selected()
        else:
            self.attachment_panel.pack_forget()

    def attachment_selected(self, event=None):
        self.open_button.state(["disabled"])
        self.save_button.state(["disabled"])
        index = self.attachment_combo.current()
        if not self.account or not 0 <= index < len(self.selected_attachments):
            return
        try:
            attachment = self.selected_attachments[index]
            info = self.store.attachment_info(*self.account, attachment_id(attachment))
            if info is None:
                self.attachment_status.set("Not downloaded. Sign in / Sync to cache attachments.")
            else:
                self.attachment_status.set(f"Ready offline | {info['size']:,} bytes")
                if not self.exporting:
                    self.save_button.state(["!disabled"])
                    if Path(info["name"]).suffix.lower() in OPENABLE_EXTENSIONS:
                        self.open_button.state(["!disabled"])
                    else:
                        self.attachment_status.set(
                            "Ready offline | This file type can be saved, but not opened directly."
                        )
        except ProbeError as error:
            self.attachment_status.set(str(error))

    def export_selected(self, open_file):
        if self.exporting or not self.account:
            return
        index = self.attachment_combo.current()
        if not 0 <= index < len(self.selected_attachments):
            return
        try:
            attachment = self.selected_attachments[index]
            identifier = attachment_id(attachment)
            info = self.store.attachment_info(*self.account, identifier)
            if info is None:
                raise ProbeError("Sign in and sync to download this attachment first.")
            if open_file:
                if Path(info["name"]).suffix.lower() not in OPENABLE_EXTENSIONS:
                    raise ProbeError("This file type cannot be opened directly. Use Save a copy.")
                if not messagebox.askyesno(
                    "Open attachment?",
                    "Only open files you trust. Documents can contain active content.\n\n"
                    "OBK will create a decrypted copy in its local 'opened' folder "
                    "and launch your default Windows viewer. That copy remains on disk.",
                    parent=self.root, default="no",
                ):
                    return
                directory = self.store.path.parent / "opened"
                directory.mkdir(exist_ok=True)
                destination = Path(tempfile.mkdtemp(prefix="attachment-", dir=directory)) / info["name"]
            else:
                chosen = filedialog.asksaveasfilename(
                    parent=self.root, title="Save a decrypted attachment copy",
                    initialfile=info["name"], defaultextension=Path(info["name"]).suffix,
                )
                if not chosen:
                    return
                destination = Path(chosen)
        except (ProbeError, OSError) as error:
            self.status.set(
                str(error) if isinstance(error, ProbeError)
                else "Cannot create an attachment copy folder. Check disk access."
            )
            return
        self.exporting = True
        self.attachment_selected()
        self.status.set("Preparing the attachment copy...")
        threading.Thread(
            target=self.export_worker,
            args=(*self.account, identifier, destination, open_file), daemon=True,
        ).start()

    def export_worker(self, url, username, identifier, destination, open_file):
        try:
            path = self.store.export_attachment(url, username, identifier, destination)
            self.results.put(("export_success", (path, open_file)))
        except ProbeError as error:
            self.results.put(("export_error", str(error)))
        except Exception as error:
            self.results.put((
                "export_error", f"Unexpected attachment error ({type(error).__name__})."
            ))

    def synchronize(self, url, username, password, remember, full_refresh=False):
        self.busy = True
        self.sync_button.state(["disabled"])
        self.forget_button.state(["disabled"])
        self.progress.pack(side="right", padx=(12, 0))
        self.progress.start(12)
        year = SchoolYear.current()
        self.status.set(f"Signing in and synchronizing school year {year.label}...")
        threading.Thread(
            target=self.sync_worker, args=(url, username, password, remember, year, full_refresh),
            daemon=True,
        ).start()

    def sync_worker(self, url, username, password, remember, year, full_refresh=False):
        try:
            client = KomensClient(url)
            token, _ = client.sign_in(username, password)
            self.store.save_login(url, username, password, remember)
            self.results.put(("authenticated", (url, username, year)))
            school_name = client.school_name(token)
            archive = self.store.load_archive(url, username, year) if not full_refresh else None
            folders = client.fetch_year(
                token, year,
                lambda folder, page, count: self.results.put(
                    ("progress", f"{FOLDERS[folder]}: page {page}, {count} school-year messages...")
                ),
                cached=archive[1] if archive else None,
            )
            attachment_count = client.download_attachments(
                token, folders, self.store, username,
                lambda count, total, name: self.results.put((
                    "progress", f"Attachments: {count}/{total} cached | {one_line(name)}"
                )),
            )
            synced_at = self.store.save_archive(url, username, year, school_name, folders)
            self.results.put(("success", (school_name, folders, synced_at, attachment_count)))
        except ProbeError as error:
            self.results.put(("error", str(error)))
        except Exception as error:
            # Exception messages can contain credentials or private response content.
            self.results.put((
                "error",
                f"Unexpected client error ({type(error).__name__}). "
                "No incomplete archive was saved. Restart the app and try again.",
            ))

    def poll(self):
        while True:
            try:
                kind, value = self.results.get_nowait()
            except queue.Empty:
                break
            if kind == "progress":
                self.status.set(value)
                if self.login:
                    self.login.status.set(value)
            elif kind in ("export_success", "export_error"):
                self.exporting = False
                if kind == "export_error":
                    self.status.set(value)
                else:
                    path, open_file = value
                    if open_file:
                        try:
                            os.startfile(path)
                            self.status.set(f"Opened attachment copy: {path.name}")
                        except OSError:
                            self.status.set(
                                "The copy was created, but Windows could not open it. "
                                "Install a default viewer or use Save a copy."
                            )
                    else:
                        self.status.set(f"Saved attachment copy: {path.name}")
                self.attachment_selected()
            elif kind == "authenticated":
                url, username, self.year = value
                try:
                    self.activate_account(url, username)
                except ProbeError as error:
                    self.status.set(str(error))
            else:
                self.busy = False
                self.progress.stop()
                self.progress.pack_forget()
                self.sync_button.state(["!disabled"])
                self.forget_button.state(["!disabled"])
                if kind == "success":
                    self.school_name, self.folders, self.synced_at, attachment_count = value
                    self.refresh_tree()
                    self.show_folder()
                    total = sum(len(messages) for messages in self.folders.values())
                    self.status.set(
                        f"Synchronized {total} messages and {attachment_count} attachments. "
                        f"{self.cache_status()}"
                    )
                    if self.login:
                        self.login.close()
                else:
                    self.status.set(f"{value} Existing offline archive was not replaced.")
                    if self.login:
                        self.login.set_busy(False)
                        self.login.status.set(value + " Existing offline messages remain available.")
        self.root.after(100, self.poll)


def verify_installation():
    """Exercise the installed GUI and encrypted store without real account data."""
    with tempfile.TemporaryDirectory(prefix="obk-smoke-") as directory:
        store = LocalStore(Path(directory) / "smoke.sqlite3")
        value = {"installation_check": "OBK"}
        if store.decode(store.encode(value)) != value:
            raise ProbeError("Windows protection round-trip failed.")
        root = tk.Tk()
        try:
            app = KomensWindow(root, store, show_login=False)
            app.activate_account("https://school.example", "installation-check")
            root.update()
            if not root.winfo_ismapped() or app.login is not None:
                raise ProbeError("The offline application window did not start.")
        finally:
            for callback in root.tk.call("after", "info"):
                root.after_cancel(callback)
            root.destroy()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smoke-test", action="store_true")
    parser.add_argument("--smoke-result", type=Path)
    arguments = parser.parse_args()
    if arguments.smoke_result and not arguments.smoke_test:
        parser.error("--smoke-result requires --smoke-test")
    if arguments.smoke_test:
        try:
            verify_installation()
        except Exception as error:
            if arguments.smoke_result:
                arguments.smoke_result.write_text(
                    json.dumps({"ok": False, "error": str(error)}), encoding="utf-8"
                )
            raise
        if arguments.smoke_result:
            arguments.smoke_result.write_text(json.dumps({"ok": True}), encoding="utf-8")
        return
    root = tk.Tk()
    try:
        KomensWindow(root)
    except ProbeError as error:
        root.withdraw()
        messagebox.showerror("OBK cannot start", str(error), parent=root)
        root.destroy()
        return
    root.mainloop()


if __name__ == "__main__":
    main()
