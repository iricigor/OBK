# OBK – Offline Bakaláři Komens

[![Latest release](https://img.shields.io/github/v/release/iricigor/OBK?label=release)](https://github.com/iricigor/OBK/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/iricigor/OBK/total?label=downloads)](https://github.com/iricigor/OBK/releases)
[![Release build](https://github.com/iricigor/OBK/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/iricigor/OBK/actions/workflows/release.yml)
[![Validation](https://github.com/iricigor/OBK/actions/workflows/validate.yml/badge.svg?event=pull_request)](https://github.com/iricigor/OBK/actions/workflows/validate.yml)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)](https://github.com/iricigor/OBK/releases/latest)

OBK downloads your children's **Bakaláři Komens** messages and attachments to
your Windows PC so you can read, search, and organize them offline.

![OBK showing an inbox grouped by date, with a selected message and a possibly related reply](docs/images/obk-main.png)

<sub>Screenshot uses fictional sample data.</sub>

**Privacy and security:** Your Bakaláři login details, messages, and attachments
travel **only between your PC and your school's Bakaláři server**, over HTTPS.
OBK does not send this data to its developer or GitHub, and there is no OBK
cloud service. Separate connections to GitHub are used for app updates, not
for your school data. Cached messages, attachments, and optionally saved passwords
are encrypted for your Windows account. Some database metadata, including your
school address and username, is not encrypted, and this protection does not
stop software running under your Windows account. Opening or exporting an
attachment leaves an unencrypted copy on disk, so only open files you trust
and delete copies when no longer needed. When reporting problems, never share
passwords or private messages. See [Privacy](#privacy) and the
[security details](docs/TECHNICAL.md#windows-desktop-client) for more information.

## Features

- **All your children in one place** – switch between children and schools with
  one click in the sidebar; **Refresh** updates all of them at once.
- **Works offline** – messages and attachments are stored on your PC, encrypted
  for your Windows account.
- **Noticeboard, Inbox, Sent, and Excuses** – sent excuses (omluvenky) get their
  own folder.
- **Search** subject, text, sender, and date; **sort** and **group** messages by
  date, sender, or subject, and collapse groups you do not need.
- **Possibly related messages** – quick links to likely earlier messages and
  replies in the same conversation.
- **Clickable links** in messages, and attachments that open with one click.
- **Light and dark themes**, a resizable sidebar that can shrink to icons, and
  **automatic updates**.
- **Settings** at the bottom of the sidebar – choose the theme, rename
  children, save, change, or clear saved passwords, and check for updates.

OBK only reads messages. It cannot send messages, replies, excuses, or
confirmations, and it never marks messages as read on the server.

## Install

1. Download the latest `OBK-…-windows-x64-setup.exe` from
   [Releases](https://github.com/iricigor/OBK/releases/latest).
2. Run it. No administrator rights or separate .NET installation are needed.
3. Start **OBK** from the Start menu.

OBK checks for new versions while it runs and offers **Update and restart**
when one is ready.

## Getting started

1. On first start, search for your child's school (or enter its Bakaláři web
   address), then sign in with your Komens username and password.
2. Optionally enter the child's name and choose **Remember credentials** so
   **Refresh** can update this child without asking again.
3. Use **Add child** in the sidebar to add more children or schools.
4. Click **Refresh** to download new messages for every child.

OBK downloads messages for the current school year (September–August).

## Privacy

- Your password is stored only if you choose **Remember credentials** (or
  **Save password** in Settings, after OBK verifies it with your school), and is
  encrypted with Windows for your user account, like your messages.
- OBK talks only to your school's Bakaláři server and to GitHub for updates;
  there is no OBK cloud service.
- OBK uses the same interface as the official Bakaláři mobile app. It is not an
  official Bakaláři product, and some schools may disable this access.

## Code signing policy

SignPath Foundation enrollment is pending; current releases remain unsigned.
See the [code signing policy](docs/CODE_SIGNING.md) for signing responsibilities,
privacy details, enrollment instructions, and release verification.

## License

OBK-owned code is available under the [MIT license](LICENSE). Bundled dependencies
retain their own licenses and notices. This license does not grant rights to
Bakaláři software, school data, or API access; the service's applicable terms
still apply. OBK is not affiliated with or endorsed by Bakaláři.

## More information

- [Technical documentation](docs/TECHNICAL.md) – building from source, data
  storage, security details, synchronization behavior, and release process.
- [Deferred ideas](DEFERRED_IDEAS.md) – possible future work.
- [Report a problem](https://github.com/iricigor/OBK/issues) – please do not
  include passwords or private message content.
