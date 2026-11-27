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

Automatic refresh offers **Off, 15 minutes, 30 minutes, 1 hour, 4 hours, and
24 hours**. The interval is remembered and changes apply without restarting.
Manual refresh remains available when automatic refresh is Off.

On Windows, choose **Settings > Refresh > Automatic refresh interval**.
It defaults to Off, checks only while OBK is running (including minimized),
and follows the selected child scope and **Always refresh all children**.
Checks do not overlap other downloads. Missing passwords are reported in the
status bar without repeatedly opening sign-in dialogs.

On Android, choose **Settings > Scheduled checks > Automatic refresh interval**.
Background checks still check all children with saved passwords independently
of the selected view and can notify you about new messages. Existing enabled
checks retain their 1-hour interval; otherwise checks default to Off. Android
may delay a check to save battery, so timing is approximate. Off also disables
both notification options; enabling checks again leaves notifications off.
Android asks for notification permission only when you turn notifications on.

Account controls let you add or rename a child, save or forget a password, and
transfer a saved account securely from Windows to Android.

| Windows light | Android light |
|:--:|:--:|
| ![Windows inbox with two children in the sidebar](../images/obk-main.png) | ![Android all-children message list](../images/android-light-messages.png) |

OBK only reads Komens data. Folders are local views and do not change read or
delivery status on the school server.
