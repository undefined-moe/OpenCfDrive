# User guide

This guide covers setting up OpenCfDrive, using it every day, and what it does
when something goes wrong. The [README](../README.md) has the short version.

- [Requirements](#requirements)
- [Installing](#installing)
- [Connecting your remote](#connecting-your-remote)
- [The app](#the-app)
- [Settings](#settings)
- [How syncing works](#how-syncing-works)
- [Offline](#offline)
- [Conflicts](#conflicts)
- [Updates](#updates)
- [Uninstalling](#uninstalling)
- [Where things are kept](#where-things-are-kept)
- [Good to know](#good-to-know)

## Requirements

- 64-bit Windows 10 version 2004 (build 19041) or later, or Windows 11.
- [rclone](https://rclone.org), plus a remote configured in it. OpenCfDrive
  runs rclone but doesn't include it. You can install rclone yourself, or have
  OpenCfDrive download it for you.

## Installing

Each [release](https://github.com/dkgs2000/OpenCfDrive/releases/latest) comes
with two installers:

- `OpenCfDrive-<version>-x64-setup.exe`, the recommended one, which updates
  itself later.
- `OpenCfDrive-<version>-x64.msi`, a plain Windows Installer package. The `.exe`
  installs this same MSI.

Both install for your user only, into `%LOCALAPPDATA%\Programs\OpenCfDrive`.
They need no administrator rights, add an **OpenCfDrive** entry to the Start
Menu, and include .NET.

The installers aren't code-signed yet, so Windows SmartScreen may warn before
running them. Choose **More info → Run anyway**.

## Connecting your remote

### 1. Get rclone

OpenCfDrive looks for rclone in this order and uses the first one it finds:

1. the `rclone.exe` chosen in Settings;
2. the one on `PATH`, for example after `winget install Rclone.Rclone`;
3. a copy it downloaded from rclone.org when you pressed **Download rclone
   from rclone.org** in Settings. The download is checked against the
   release's published SHA-256 checksums and saved in
   `%LOCALAPPDATA%\OpenCfDrive\rclone`.

### 2. Create a remote

Set up the remote with rclone's own wizard. It asks for the server's details
and, for services like Google Drive or OneDrive, opens the browser so you can
sign in. rclone's documentation has a page for each provider.

```powershell
rclone config
```

If you're using the copy OpenCfDrive downloaded, run that one instead:

```powershell
& "$env:LOCALAPPDATA\OpenCfDrive\rclone\rclone.exe" config
```

### 3. Choose it in OpenCfDrive

OpenCfDrive opens after it's installed. On the **Settings** page, choose the
remote from the list, which comes from rclone's config. A path inside the
remote works too, as in `myremote:backup`. If you added a remote after opening
the app, press **Refresh list**.

Press **Save**. The sync service starts rclone, then the drive. The drive
appears in Explorer's navigation pane, at `%USERPROFILE%\OpenCfDrive` unless you
chose another folder.

### Using an rclone you run yourself

By default OpenCfDrive runs rclone itself. To use an `rclone rcd` you run
yourself, choose **I run rclone rcd myself**, and enter its address and the
login you start it with. Start it with `--rc-serve` and a login. rclone reads
the login from `RCLONE_RC_USER` and `RCLONE_RC_PASS` too, which keeps the
password out of the process list:

```powershell
$env:RCLONE_RC_USER = "opencfdrive"
$env:RCLONE_RC_PASS = "<a password of your own>"
rclone rcd --rc-serve --rc-addr 127.0.0.1:5572
```

Don't start it with `--rc-no-auth`. It lets any program on your PC use every
remote in your rclone config, and read the config's secrets. OpenCfDrive saves
the password encrypted for your Windows user.

## The app

The app lives in the notification area, behind the arrow next to the clock.
Drag its icon onto the taskbar to keep it in view.

The icon shows how the drive is doing, as OneDrive's does. Point at it to see
the state and what it's doing, for example "Sending 3 changes and downloading
1 file."

| Icon | Means |
|---|---|
| The plain icon | Up to date: connected, with nothing to send or download. |
| A blue arrow | Syncing: starting, sending your changes, or downloading. |
| An amber "!" | Some files couldn't be synced. Explorer marks them **Couldn't sync**, and Recent activity says why. |
| A red cross | Nothing syncs: a problem with the settings, rclone or the sync service. |
| Grey, with a line through | Offline: the server can't be reached. Your changes wait for it. |
| Grey, with a pause sign | The sync service isn't running. |
| Grey | Not set up yet: choose your remote in Settings. |

- **Click the icon** to open the app.
- **The icon's menu** opens the drive folder, starts, stops or restarts
  syncing, and shows the log.
- **Closing the window** only hides it. **Exit** in the icon's menu closes the
  app, and syncing carries on in the background.
- The installed app **starts when you sign in** to Windows. You can turn that
  off in Settings.
- **A notification tells you about every problem:** a file or folder that
  couldn't be synced (with the server's answer, for example "413 Payload Too
  Large" from a server that refuses big uploads), a download that failed, and
  the drive going offline or stopping. Several at once come as one. A file
  that keeps failing isn't announced again for 30 minutes. Click the
  notification to open the app, whose Recent activity has the details.

The window has three pages, listed on its left. The drive's state icon and
remote sit at the top of the list. The window opens as big as its pages need
on the screen where you clicked the icon. You can make it smaller: text wraps
and pages scroll.

**Status** shows:

- **How the drive is doing,** with the icon's picture, a sentence on what
  it's doing, and buttons to open the drive folder and to start, stop or
  restart syncing. Before a remote is chosen, or when rclone can't be found,
  a button takes you to Settings. An update ready to install shows above it.
- **Drive:** the folder, the remote, what kind of storage it is and where it
  points (for example `s3 (AWS), region eu-west-1` or
  `crypt over gdrive:secret`), and the space used and free, if the server
  reports it. It never shows passwords, keys or tokens.
- **Recent activity:** uploads, downloads, deletes and warnings, newest first.
  Point at a line to read all of it.

**Settings** holds the settings below. Changes take effect when you press
**Save**; **Discard changes** puts back what's saved.

**About** shows:

- **The version,** whether it's up to date, and the buttons to check for
  updates and install one.
- **Sync service:** whether it's running and since when, its version, whether
  the drive is registered with Explorer and connected, how many items it
  tracks, and how much it has downloaded.
- **rclone:** who runs it, which `rclone.exe` and version, where it listens,
  whether it's running (or being restarted, and why), and whether the chosen
  remote is in rclone's config.
- **Logs and settings:** where the service's log, rclone's log and the
  settings file are, with buttons to open them.

## Settings

| Setting | What it does |
| --- | --- |
| Drives | Every drive: its folder and its remote. **Add drive** starts another one; each gets its own place in Explorer. |
| rclone remote | The remote the chosen drive shows, such as `myremote:` or `myremote:backup`. |
| rclone | **OpenCfDrive runs it** (the default), or **I run rclone rcd myself** at the address you give. |
| rclone program | A specific `rclone.exe`. Leave it empty to use the one on `PATH`, or else the downloaded copy. |
| rclone config file | A specific `rclone.conf`. Leave it empty for rclone's own default (`rclone config file` tells you where that is). |
| Start OpenCfDrive when I sign in | On by default for the installed app. |
| Check for new versions | Checks GitHub every 10 hours and downloads new versions. On by default. |

Settings are saved in `%LOCALAPPDATA%\OpenCfDrive\settings.json`. Developers
and scripts can override some of them with environment variables (see
[Development](development.md#configuration)). The app tells you when one is
set.

**Several drives.** Each drive has its own folder, remote, sync state and
place in Explorer's navigation pane; they share one rclone. The notification
icon stands for the drive that is doing worst: its headline says which one
when something is wrong. With several drives, the Status page and the icon's
menu show the first; removing or moving a drive restarts them all for a moment.

**Changing the drive folder or the remote.** A drive folder belongs to one
remote, because its placeholders describe that remote's files. So the app only
moves the drive to a folder that is new or empty, or that was this remote's
drive before. To switch to another remote you also need such a folder. The old
folder is unregistered and stays as a normal folder with the files you'd
already downloaded.

## How syncing works

- **The server is the source of truth.** The first time you open a folder,
  OpenCfDrive lists it from the server. A folder that holds something local
  (a downloaded, kept or edited file, or a pinned folder) is then kept up to
  date in the background, every 30 seconds and right after the server comes
  back from being offline. An online-only folder - one whose files have not
  been downloaded - is not re-listed every 30 seconds. Once 30 seconds have
  passed since it was last listed, the next time you open it OpenCfDrive
  lists it from the server, so it shows what changed there meanwhile.
- **Folders you have never opened are not read** until you open them. The drive
  is never crawled.
- **Your changes go to the server.** That includes new files, edits, deletes,
  renames, moves and new folders, empty ones too.
- **Changes made while OpenCfDrive wasn't running** are found and sent when it
  starts. Edited and new files are uploaded, and new folders are created.
  Deletes made while it was off are not sent: the missing files come back from
  the server instead.
- **Files are downloaded when opened.** **Free up space** removes the local copy
  and keeps the file online. **Always keep on this device** downloads it and
  keeps it local. On a folder, both apply to everything inside. Freeing up
  space on a file with an edit that hasn't been uploaded yet uploads the edit
  first, so it isn't lost.

## Offline

When the server can't be reached, the drive goes offline and the app shows
**Offline**. While it's offline:

- folders show what is already on this computer;
- files you downloaded open as usual, and files that were never downloaded
  can't be opened;
- edits, new files, new folders, deletes and renames are kept, even across a
  restart. They're sent once the server answers again.

OpenCfDrive checks for the server again when Windows reports a network change,
when you use the drive, and on a schedule: about a minute after going offline,
then less often the longer it stays offline, up to about every 8 minutes.

## Conflicts

**OpenCfDrive never throws away a local change.** A conflict happens when a
file has changes here that haven't reached the server yet, and the server's
copy changed in the meantime. In that case:

| On the server | Here | What happens |
| --- | --- | --- |
| changed | edited, not sent yet | Both are kept. The server's version keeps the name. Your version is saved as `name (conflicted copy 2026-11-03 175504).ext`, using the time of your edit, and that copy is uploaded too. If the remote keeps content hashes and both sides now hold the same content, it's not a conflict: the file is simply in sync. |
| deleted | edited, not sent yet | Your file is kept and uploaded again. |
| folder deleted | files inside edited, not sent yet | Those files and their folders are kept and uploaded again. Everything else in the folder is removed. |
| changed | deleted | Your delete isn't sent, and the server's version comes back. |
| a new file | a different new file at the same path | Both are kept, yours as a conflict copy. Files with the same content hash, or the same size and time, count as the same file. (On a remote that can't set file times, such as plain WebDAV, the times never match.) |
| unchanged | edited | Not a conflict: your edit is uploaded. |

To resolve a conflict, delete whichever version you don't want. The copy is on
the server too, so other devices see it.

Where the remote keeps content hashes (S3, Google Drive, OneDrive, Dropbox, B2
and others), OpenCfDrive uses them to tell whether a file really changed. On
remotes without them (plain WebDAV, FTP, SMB, SFTP and others) it goes by size
and modification time, so a server change that keeps both the same isn't
noticed there. And if the server copy changes in the split second between
OpenCfDrive's last check and its upload, the upload overwrites it.

## Updates

The installed app updates itself from this project's GitHub releases:

1. It checks for a new release 3 seconds after it starts, then every 10 hours,
   unless **Check for new versions** is off. **Check for updates** on the
   About page checks right away.
2. A new version downloads in the background into
   `%LOCALAPPDATA%\OpenCfDrive\updates`, and is checked against the size and
   SHA-256 that GitHub lists for it.
3. Once it's ready, the app tells you with a notification, at the top of the
   Status page, on the About page and in the icon's menu. It tells you again each time it starts until you
   install it.
4. **Restart and update** stops syncing, installs the update with a progress
   bar, and reopens the app, which starts syncing again.

If a new version is out but can't be downloaded, a notification says so once
for that version. Click it to open the release page and install it from there.

If an update fails to install, the app offers it again with the installer's log
(`updates\install.log`).

Only the installed copy updates itself. A copy you built yourself shows why it
doesn't. Version 0.1.0 has no updater, so install the next version by hand
once.

## Uninstalling

Uninstall from **Settings → Apps**. That:

- closes the app and stops the sync service and rclone;
- removes the sign-in start and the rclone that OpenCfDrive downloaded;
- unregisters the drive, which removes it from Explorer.

The drive folder stays as a normal folder with everything you'd downloaded.
Your settings and the sync state in `%LOCALAPPDATA%\OpenCfDrive` stay too.
Sign out or restart Explorer if the drive's entry stays in the navigation pane.

Upgrading keeps the drive's registration and your settings.

## Where things are kept

| What | Where |
| --- | --- |
| The drive | `%USERPROFILE%\OpenCfDrive`, or the folder you chose |
| The app | `%LOCALAPPDATA%\Programs\OpenCfDrive` |
| Settings | `%LOCALAPPDATA%\OpenCfDrive\settings.json` |
| Sync state (one database per drive) | `%LOCALAPPDATA%\OpenCfDrive\state\opencfdrive-<hash>.db` |
| Sync service log | `%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.log` |
| rclone's log | `%LOCALAPPDATA%\OpenCfDrive\logs\rclone.log` |
| App error log | `%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.App.log` |
| Downloaded rclone | `%LOCALAPPDATA%\OpenCfDrive\rclone` |
| Downloaded updates | `%LOCALAPPDATA%\OpenCfDrive\updates` |

OpenCfDrive never writes its own state inside the drive folder, and never puts
it on the server.

The rclone that OpenCfDrive runs listens only on `127.0.0.1`, on a free port,
and needs a login that is made up for each run. Windows ends it whenever the
sync service ends, and OpenCfDrive restarts it if it stops on its own.

## Good to know

- **Files that were already in the folder** before the drive was set up stay
  normal local files. Their edits are still uploaded. Windows can't turn an
  existing file into an online-only one.
- **Pressing F5 in Explorer.** In a folder with something local in it, it
  doesn't ask the server sooner: changes from the server arrive within 30
  seconds anyway. In an online-only folder, F5 lists the folder from the
  server once 30 seconds have passed since it was last listed; before that, it
  shows the same listing.
- **Many opened folders do not mean many listings.** Only folders that hold
  something local (a downloaded, kept or edited file, or a pinned folder) are
  listed every 30 seconds. Online-only folders are listed when you open them,
  so opening many folders does not drive up per-folder polling.
- **On S3 and other bucket stores**, an empty folder exists only on this
  computer until you put a file in it, because those stores have no real
  folders.
- **"Being used by another process."** A program that opens a file at the
  exact moment OpenCfDrive applies a server change to it can see this error.
  Opening the file again works.
- **Big files on a plain WebDAV server** are uploaded in one request each
  (rclone uploads in parts only to Nextcloud and ownCloud). A proxy in front
  of the server may refuse large requests: Cloudflare's free plan refuses
  anything over 100 MB with "413 Payload Too Large". OpenCfDrive tells you
  which file it couldn't upload. Raise the proxy's limit, or let uploads to
  the server bypass it.
- **Sync states in Explorer's Status column.** OpenCfDrive shows a per-file
  sync state in Explorer's Status column:
  - **Waiting to upload** - the file has a local edit that has not reached the
    server yet; cleared once the upload succeeds.
  - **Conflict copy** - the file is a conflict copy kept beside a server
    version that changed at the same time. It stays marked, also after it is
    uploaded, until you rename or delete it.
  - **Couldn't sync** - the last sync attempt for the file failed; the reason
    is shown, and the state clears on the next success for that file.
- **No cloud badge on file icons.** OpenCfDrive shows the online/offline state
  and the sync state in Explorer's Status column, not as an overlay on each
  icon. The Status column is what tells you whether a file is online-only,
  downloaded, or waiting on the server.

Something not working? Open an
[issue](https://github.com/dkgs2000/OpenCfDrive/issues) and attach the sync
service log and rclone's log.
