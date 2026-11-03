# Installation, update and uninstallation

Version française : [02-installation.fr.md](02-installation.fr.md)

## Emby server: the plugin alone

The `RemoteTranscode.EmbyPlugin.dll` plugin contains everything needed on the server side:

- the **coordinator**, which runs inside the Emby process (Emby 4.10 ships ASP.NET Core);
- the **ffmpeg relay** for Linux x64, Linux arm64, Linux Alpine/Docker (musl) x64 and Windows x64, extracted at startup.

No path is hard-coded. At every Emby startup, the plugin asks Emby for:

- its ffmpeg and ffprobe: Emby's command line, otherwise its configuration;
- the environment variables Emby passes to ffmpeg;
- the transcoding folder;
- the library folders, re-read every 5 minutes: these are the only files the workers can read;
- its data folder.

### Installing

1. Copy `RemoteTranscode.EmbyPlugin.dll` into the `plugins` folder of Emby's data folder:
   - Synology: `/var/packages/EmbyServer/var/plugins`;
   - Docker: `/config/plugins`;
   - Windows: `%AppData%\Emby-Server\programdata\plugins`.
2. Restart Emby.
3. Reload the Emby page (F5), then open « Tableau de bord » (Dashboard) → **« Transcodage distribué »** (Distributed transcoding):
   - **« Connexion des workers »** (Worker connection): `https://0.0.0.0:8097` by default (direct TLS, certificate generated and verified by the workers). Behind an HTTPS reverse proxy: `http://127.0.0.1:8097` and the proxy's public `https://…` address. With Docker, publish the chosen port (`-p 8097:8097`).
   - **« Adresse annoncée »** (Advertised address) (optional, direct TLS): name or IP that the pairing codes give to the workers. By default, it is the server's network address; Docker and VPN interfaces are ignored.
   - **« Associer un worker »** (Pair a worker): the code shown is pasted into the worker (« Paramètres » (Settings) → « Code d'association » (Pairing code)).
   - **« Activer »** (Enable).

The plugin's data (certificate, paired workers, secrets, conversion chunks) lives in `<Emby data>/remote-transcode`. The secrets are readable only by the user running Emby.

At startup, the plugin checks that the relay really runs on this server (it may not, for example, on a folder mounted without execute permission). Otherwise, Emby keeps its own ffmpeg and the plugin page shows the reason.

#### Synology: installation script

The repository provides `tools/nas/install-plugin.sh`. With SSH:

```
sudo sh /volume4/docker/emby-remote/plugin/install-plugin.sh /volume4/docker/emby-remote/plugin/RemoteTranscode.EmbyPlugin-<version>.dll <sha256>
```

- the SHA-256 hash (optional) is verified before any change;
- the previously installed plugin is kept alongside (`installed-before-<date>.dll`): to roll back, run the script again with that file;
- **only one run at a time** (lock): running the script twice, or two DSM restarts at the same time, used to make them block each other;
- stopping Emby is capped at 3 minutes and starting it is retried three times; as a last resort: `sudo /usr/syno/bin/synopkg start EmbyServer` or the Package Center.

#### Synology without SSH: DSM Task Scheduler

A **generic** DSM task, created once and for all, installs any version: it stitches the DLL back together from chunks, verifies its hash and runs `install-plugin.sh`. Later versions do not require changing the task, hence no DSM password.

**Once:**

1. With File Station, drop the repository's `install-plugin.sh` script (`tools/nas`) into a shared folder, for example `docker/emby-remote/plugin/`.
2. Control Panel → Task Scheduler → Create → Triggered Task → User-defined script. Name "Installer plugin RemoteTranscode", user **root**, event Boot-up, **Enabled box unchecked** (the task only runs on demand). The script stitches `plugin.part0` to `plugin.part5` back together in order, runs `install-plugin.sh` with the hash read from `plugin.sha256` and writes the result to a log `install-<date>.log`. Script of the task in service on the test NAS (a single line):
   ```
   D=/volume4/docker/emby-remote/plugin; cat $D/plugin.part0 $D/plugin.part1 $D/plugin.part2 $D/plugin.part3 $D/plugin.part4 $D/plugin.part5 > $D/RemoteTranscode.EmbyPlugin-new.dll && sh $D/install-plugin.sh $D/RemoteTranscode.EmbyPlugin-new.dll $(cat $D/plugin.sha256) > $D/install-$(date +%Y%m%d-%H%M%S).log 2>&1
   ```
   It expects exactly 6 chunks: with 9 MB chunks, this covers a DLL of 46 to 54 MB (it is about 48 MB). If the DLL size changes enough to require a different number, adjust the list of `plugin.partN` in the task (which asks for the DSM password again).

**For each version:**

1. Split the DLL into 6 chunks `plugin.part0`..`plugin.part5` of 9 MB (the last one smaller; they go through File Station or its API) and write its SHA-256 hash into `plugin.sha256`.
2. Drop these files into `docker/emby-remote/plugin/`, replacing the previous ones (if a chunk is missing or does not match, the hash detects it and nothing is installed).
3. Select the task, then **Run** it once, and wait for its result before running it again. Emby is unavailable for about 45 s. Read the log `install-<date>.log`: "Plugin installe, Emby redemarre." (Plugin installed, Emby restarting.) on success, "Empreinte incorrecte, rien n'est modifie" (Wrong hash, nothing changed) if the chunks do not match.

#### Exposing to the internet

Workers that are not on the local network go through an HTTPS reverse proxy (DSM: Login Portal → Reverse Proxy). Failed authentication attempts are rate-limited per address: 10 failures within 10 minutes block the address for 15 minutes. Behind the proxy, the real address is read from `X-Real-IP`, which DSM's proxy fills in.

### Emby updates

Nothing to do. At startup, the plugin detects Emby's ffmpeg again, rewrites the dispatcher and the relay, then reapplies the interception.

Emby stores the "custom ffmpeg" setting in its database. If a future Emby version no longer loaded the plugin, Emby would therefore keep calling the dispatcher:

- the dispatcher keeps working without the plugin: everything goes to Emby's ffmpeg, since the relay no longer finds a coordinator;
- if the update moved Emby's ffmpeg, the dispatcher and the relay find it again in Emby's command line (`-ffmpeg`, Linux);
- to go back entirely to the original state: « Tableau de bord » (Dashboard) → « Transcodage » (Transcoding) → custom ffmpeg path unchecked, or uninstall the plugin.

If the coordinator cannot start (port in use…), the plugin leaves Emby on its own ffmpeg and shows the reason on its page.

Safeguards during operation:

- A playback or conversion that no worker can take is done by Emby's ffmpeg, as without the plugin.
- Arguments unknown to the plugin, coming from a new Emby version, cause the job to be refused, and it is then done locally.
- Kill switch: a `disabled` file in `<Emby data>/remote-transcode/bin` sends everything back to Emby's ffmpeg.

### Updating the plugin

Replace the DLL and restart Emby. Paired workers are kept.

### Uninstalling

« Tableau de bord » (Dashboard) → « Plugins » → « Transcodage distribué » → « Désinstaller » (Uninstall). Emby immediately goes back to its own ffmpeg, without a restart. The `<Emby data>/remote-transcode` folder remains and can be deleted.

## Windows worker

Run `EmbyRemoteWorker-Setup-<version>.exe`: installation without administrator rights, icon in the notification area. Then paste the pairing code obtained on the plugin page. The installed version is shown in the window title and in **« Aide » (Help) → « À propos » (About)**.

The files go into a dedicated `EmbyRemoteWorker` folder. The list of installed files is recorded in `install-manifest.txt`.

**Update.** Simply run the new installer: if it finds an existing installation, it offers an **update** (a single window, installed version → new version) that keeps the folder, the start-with-Windows option, the shortcut, the configuration and the pairing. It asks the running worker to **close cleanly** (like « Quitter » (Quit): its jobs resume on the server or another worker) and only forces it to stop as a last resort (version earlier than 0.7.5, or no exit within 20 s). It stops without changing anything if a file remains locked. `--full` shows the full wizard, `--quiet` installs without a window.

**Automatic update.** Option « Mises à jour » in the worker's settings, on by default, and shown right under the pairing. The worker reads the latest release of the project's GitHub repository (fixed in its code) at start, then about every 6 h, and takes a setup only if its name has the exact expected form and its version is strictly newer than its own. It downloads it with its `.sha256` from that release, checks the hash, and runs it with `--quiet` when it has had no job for 2 minutes, refusing new ones meanwhile; the worker then restarts by itself. A version that failed is not tried again for 24 h. Unticked, the worker does not contact GitHub at all. The Emby server cannot choose what a worker installs: at most it can ask it to check sooner. The setups are not code-signed, so this trusts the project's GitHub account and repository: someone in control of them could have every worker with this option run their program. Untick it if you prefer to install each version yourself. A worker older than this feature must be updated by hand once. Inactive for a copy run from anywhere but its installation folder.

**Uninstallation**: Windows Settings → Apps → Emby Remote Worker. The worker itself performs the uninstallation (`EmbyRemoteWorker.exe --uninstall`, `--quiet` for no window): it only deletes the files listed in the manifest. Versions before 0.7.5 left an `Uninstall.exe` (a copy of the installer): it is deleted during the update.

**Antivirus.** The installer and the worker are not signed. Since 0.7.5, the installer avoids the actions antivirus products consider suspicious (copying itself, forcibly killing a process); Kaspersky had flagged installer 0.7.4 ("PDM:Trojan.Win32.Generic", behavioural detection). Only code signing provides a guarantee; on a corporate machine, the IT department can also approve it.

**Signing.** `tools\build-installer.ps1` signs and timestamps (SHA-256, DigiCert by default) the worker and then the installer as soon as it is given a code-signing certificate: `-CertThumbprint <thumbprint>` (user store, for example a certificate on a hardware token) or `-PfxPath <file.pfx>`. An invalid signature stops the build. The mechanism was verified with a test certificate (signature and timestamp applied, only trust is missing). A certificate issued by a trusted authority is required: an OV or EV certificate from a commercial provider, or a cloud signing service (Azure Trusted Signing, for example), whose tool signs the same files. This is a possibility, not a planned step: for personal use, no certificate was purchased and the deliverables are not signed.

Configuration and logs are in `%LOCALAPPDATA%\EmbyRemoteWorker`. `worker.json` is written atomically, with a `worker.json.bak` copy used if the main file is unreadable.

## Building

- `tools\build-plugin.ps1`: relay for each platform, then the plugin. Output: `artifacts\plugin`.
- `tools\package-worker.ps1`: worker and FFmpeg. Output: `artifacts\worker-package` (never the folder a worker runs from: its locked executable would stay at the old version; the script checks the version produced).
- `tools\build-installer.ps1`: worker installer, signed if a certificate is given (`-CertThumbprint` or `-PfxPath`). Output: `artifacts\installer`.
