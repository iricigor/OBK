# Children and folders

[Back to README](../../README.md#features)

OBK keeps several children and schools in one archive. Choose one child or
**All children**, then move between Inbox, Noticeboard, Sent, and Excuses.
Refreshing from the main view updates only the selected child; in **All children**
it updates every configured account. On both Windows and Android, **Settings >
Refresh > Always refresh all children** makes manual refresh update every child
regardless of the current view. This setting is off by default and is remembered
across restarts. Missing-password prompts and refresh results apply only to the
children being refreshed.

On Android, optional hourly checks still check all children independently of
this setting and can notify you about new messages; Android asks for notification
permission only when you turn notifications on.

Planned in #95: a saved automatic-refresh interval in Settings, with Off,
15 minutes, 30 minutes, 1 hour, 4 hours, and 24 hours. Windows will check only
while running and follow the selected refresh scope; Android will retain its
all-children background checks with approximate OS-controlled timing. Changing
the interval will apply without restarting, while manual refresh stays unchanged.

Account controls let you add or rename a child, save or forget a password, and
transfer a saved account securely from Windows to Android.

| Windows light | Android light |
|:--:|:--:|
| ![Windows inbox with two children in the sidebar](../images/obk-main.png) | ![Android all-children message list](../images/android-light-messages.png) |

OBK only reads Komens data. Folders are local views and do not change read or
delivery status on the school server.
