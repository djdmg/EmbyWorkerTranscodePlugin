# User guide — Emby Distributed Transcoding

Version française : [USER-GUIDE.fr.md](USER-GUIDE.fr.md)

This guide is for people who run an Emby Server at home and want their Windows PCs to help with video work. No programming knowledge is needed.

The product's interface is in French. In this guide, each French label is quoted as it appears on screen, followed by its English translation in parentheses the first time it is used. In Emby, the plugin is called « Transcodage distribué » (Distributed transcoding); the Windows program is called **Emby Remote Worker**.

## Contents

1. [What it does](#1-what-it-does)
2. [Requirements](#2-requirements)
3. [Install the plugin on the Emby server](#3-install-the-plugin-on-the-emby-server)
4. [Configure the server side](#4-configure-the-server-side)
5. [Install and pair a worker PC](#5-install-and-pair-a-worker-pc)
6. [Daily use](#6-daily-use)
7. [Conversions (Convert and downloads)](#7-conversions-convert-and-downloads)
8. [Update, roll back and uninstall](#8-update-roll-back-and-uninstall)
9. [Troubleshooting and FAQ](#9-troubleshooting-and-faq)
10. [Known limitations](#10-known-limitations)

---

## 1. What it does

Emby sometimes has to re-encode a video: when a device cannot play the file as it is, or when you convert a movie for download. On a small NAS without a graphics card, this is slow. This project lets the Windows PCs of your home do that work instead. Each such PC is called a **worker**.

There are two parts:

- a **plugin** for Emby Server (a single file, `RemoteTranscode.EmbyPlugin.dll`);
- a small **Windows program** for each worker PC (it lives in the notification area, next to the clock).

### How it works in one minute

**Playback**

- When you press Play, the server usually starts encoding itself. The first image appears in one or two seconds.
- A few seconds later, the playback moves to a worker PC. The switch happens exactly between two video segments: there is no stall and no lost image.
- Heavy videos (4K, HDR tone mapping, or a movie the server already proved too slow for) start directly on a worker.
- If the worker PC goes away (you start a game, shut it down, it falls asleep), the server takes over at the exact point, and the playback goes back to a worker as soon as one is free again.
- Playbacks have priority. If a playback is struggling on a PC, a conversion running on that same PC is moved elsewhere.

**Conversions** (Emby's "Convert" and downloads)

- The movie is cut into slices of about 2 minutes. Each cut is placed exactly on an image.
- All available PCs encode slices in parallel. The server encodes slices too.
- At the end, the server assembles the file with Emby's own settings and checks it, down to the image. If anything looks wrong, the file is rejected and Emby redoes the conversion on its own.
- A PC that disappears in the middle of a slice is not a problem: the slice resumes elsewhere.

**Safety net**

- When no worker is available, Emby simply transcodes by itself, exactly as it would without the plugin.
- Emby itself is not modified. Turning the plugin off, or uninstalling it, brings Emby back to its normal behaviour immediately.
- Direct Play (no re-encoding) is never touched.

---

## 2. Requirements

| Item | Requirement |
|---|---|
| Emby Server | Version 4.10. Tested with **4.10.2**. Nothing else to install: the plugin uses the .NET runtime that ships with Emby. |
| Emby Premiere | **Not needed by the plugin**, nor for offloading playback transcodes. Emby's own « Convert » / download (sync) feature, which the plugin speeds up, is an Emby Premiere feature. |
| Server operating system | The plugin contains its helper program for **Linux x64**, **Linux arm64**, **Linux x64 musl** (Alpine-based Docker images) and **Windows x64**. Only Emby on **Synology (Linux x64)** has been tested so far (see [Known limitations](#10-known-limitations)). |
| Worker PCs | **Windows 10 or 11, 64-bit.** The installer includes everything (the program and FFmpeg, about 178 MB). No administrator rights needed. |
| Graphics card (optional) | Workers encode with the processor by default. They can also use the graphics card: **NVIDIA (NVENC)**, **Intel (Quick Sync / QSV)** or **AMD (AMF)**. Each encoder is tested on the PC before it is used. |
| Network | Workers connect **to** the server; nothing is opened on the PCs. The server needs one port reachable by the workers (TCP **8097** by default). For workers outside your home, you need a domain name and an HTTPS reverse proxy (see [section 4.3](#43-workers-outside-your-home-network)). |

---

## 3. Install the plugin on the Emby server

### 3.1 Download and check the file

1. Open the **Releases** page of the GitHub repository (`djdmg/EmbyWorkerTranscodePlugin`).
2. Download `RemoteTranscode.EmbyPlugin.dll` and `RemoteTranscode.EmbyPlugin.dll.sha256` from the latest release.
3. Check that the file is intact. Compute its SHA-256 fingerprint and compare it with the value in the `.sha256` file:
   - on Windows (PowerShell): `Get-FileHash .\RemoteTranscode.EmbyPlugin.dll -Algorithm SHA256`
   - on Linux or a NAS over SSH: `sha256sum RemoteTranscode.EmbyPlugin.dll`

   If the values differ, download the file again. Do not install it.

### 3.2 Copy it into Emby's plugins folder

Copy the DLL into the `plugins` folder of Emby's data folder:

| Emby installation | Plugins folder |
|---|---|
| Synology | `/var/packages/EmbyServer/var/plugins` (the file must belong to the user `emby`, group `emby`) |
| Docker | `/config/plugins` inside the container (that is, the `plugins` folder of the host folder you mapped to `/config`) |
| Windows | `%AppData%\Emby-Server\programdata\plugins` |
| Other Linux installations | the `plugins` folder of Emby's data folder |

Then **restart Emby**.

#### Synology: installing without any extra tool

The Synology plugins folder is a system folder: File Station cannot write to it. The steps below only use DSM itself (File Station and Task Scheduler); nothing else has to be downloaded. They check the file's fingerprint, keep a copy of the plugin already installed (for a roll back), install the new one with the right owner and restart Emby.

1. **Put the files in a shared folder.** With File Station, create a folder, for example `/volume1/docker/emby-plugin/`, and copy into it the two files downloaded from the release: `RemoteTranscode.EmbyPlugin.dll` and `RemoteTranscode.EmbyPlugin.dll.sha256`. (Adjust `/volume1/docker/emby-plugin` in the script below if you choose another folder.)
2. **Create the install task, once.** Control Panel → **Task Scheduler** → **Create** → **Triggered Task** → **User-defined script**.
   - General: name it, for example, "Install Emby plugin"; User: **root**; Event: **Boot-up**; **uncheck "Enabled"** (the task then only runs when you click Run).
   - Task Settings → Run command → User-defined script, paste:
     ```sh
     D=/volume1/docker/emby-plugin; P=/var/packages/EmbyServer/var/plugins; F=RemoteTranscode.EmbyPlugin.dll
     cd "$D" || exit 1
     exec > "$D/install-$(date +%Y%m%d-%H%M%S).log" 2>&1
     sha256sum -c "$F.sha256" || { echo "Wrong fingerprint: nothing changed"; exit 1; }
     [ -f "$P/$F" ] && cp -p "$P/$F" "$D/$F.before-$(date +%Y%m%d-%H%M%S)"
     synopkg stop EmbyServer
     cp "$F" "$P/$F" && chown emby:emby "$P/$F" && chmod 644 "$P/$F"
     synopkg start EmbyServer
     echo "Plugin installed, Emby restarted"
     ```
   - DSM asks for your password to save a task that runs as root.
3. **Run it.** Select the task and click **Run**, once. Emby is unavailable for about a minute.
4. **Check the result** in the log file `install-<date>.log` written in the same folder: "Plugin installed, Emby restarted" means success; "Wrong fingerprint: nothing changed" means the DLL does not match its `.sha256` file (download both again).

For each new version, replace the two files in the folder and run the task again: the task itself never changes. To **roll back**, rename the copy `RemoteTranscode.EmbyPlugin.dll.before-<date>` to `RemoteTranscode.EmbyPlugin.dll`, write its fingerprint into the `.sha256` file (`sha256sum RemoteTranscode.EmbyPlugin.dll > RemoteTranscode.EmbyPlugin.dll.sha256`, or with PowerShell `Get-FileHash` on a PC) and run the task again.

**With SSH** (if you enabled it in Control Panel → Terminal & SNMP), the same commands work from a terminal: connect, run `sudo -i`, then paste the script above.

### 3.3 Open the plugin page

1. After the restart, reload the Emby web page in your browser (F5).
2. Open **Dashboard** (« Tableau de bord » in French) → **« Transcodage distribué »** (Distributed transcoding). The plugin is also listed under Dashboard → Plugins.

> Use a **web browser** (computer or phone). The plugin page does not open in the Emby Android app, which cannot run third-party plugin pages.

At the top of the page you see:

- the state, « activé » (enabled) or « désactivé » (disabled), and the ffmpeg currently used by Emby;
- « Coordinateur intégré à Emby : en service » (Coordinator built into Emby: running). This is the part of the plugin that talks to the workers. If it says « arrêté » (stopped), the reason is shown next to it (see [section 9](#9-troubleshooting-and-faq)).

Do not enable the plugin yet: first configure the connection (section 4) and pair at least one worker (section 5). You can also enable it now; without workers, Emby simply keeps transcoding by itself.

---

## 4. Configure the server side

All settings are on the plugin page, in the section « Connexion des workers » (Worker connection). Click « Enregistrer » (Save) after a change. Settings apply immediately; if the new settings do not work, the previous ones are restored.

### 4.1 The settings

| Field | Default | What it is |
|---|---|---|
| « Adresse d'écoute des workers » (Address the workers connect to) | `https://0.0.0.0:8097` | Where the server waits for workers. The default means: all network interfaces, port 8097, encrypted (TLS) with a certificate created by the plugin. |
| « Adresse publique (reverse proxy, facultatif) » (Public address, optional) | empty | Only when workers come through an HTTPS reverse proxy, for example `https://coordinator.example.com`. |
| « Adresse du serveur pour les workers (facultatif) » (Server address for the workers, optional) | empty (detected) | Name or IP address the PCs use to reach the server, for example `192.168.1.10`. It is written into the pairing code. Leave it empty unless the detected address is wrong (Docker, several network cards). |

The server also uses a second port, **8098**, but only internally (it never accepts connections from the network). It does not need to be opened.

### 4.2 Workers on your home network (simplest)

- Keep `https://0.0.0.0:8097`.
- If the server has a firewall, allow TCP port 8097 from your local network.
- With Emby in Docker, publish the port (`-p 8097:8097`) and fill in « Adresse du serveur pour les workers » with the NAS address, for example `192.168.1.10`.

The connection is encrypted. Each worker remembers the server's certificate when it is paired and refuses any other.

### 4.3 Workers outside your home network

For a PC that is not at home (a laptop on the road, a relative's PC), use an HTTPS reverse proxy. **Do not expose port 8097 directly to the internet without TLS.**

1. In the plugin page, set « Adresse d'écoute des workers » to `http://127.0.0.1:8097` (the plugin then only listens locally, the proxy brings the encryption).
2. Set « Adresse publique » to the proxy address, for example `https://coordinator.example.com`.
3. Configure the reverse proxy. On Synology: Control Panel → Login Portal → Advanced → Reverse Proxy:
   - source: HTTPS, `coordinator.example.com`, port 443;
   - destination: HTTP, `localhost`, port 8097;
   - custom header: **WebSocket** (required);
   - connection and read timeouts: 300 seconds;
   - assign a valid public certificate to that name (Control Panel → Security → Certificate).
4. Pair the workers **after** setting the public address. If the public address changes later, pair the workers again.

Notes:

- The listening address must be an IP address, and its port must differ from Emby's port.
- Without TLS on the listening address, the public address is mandatory.
- Ten failed authentication attempts from the same address within 10 minutes block that address for 15 minutes. All PCs at home usually share the same public address, so repeated mistakes can briefly block every remote worker of the household.
- A worker reached over the internet is limited by the upload speed of your home connection.

### 4.4 The « Activer » / « Désactiver » switch

| Button | Effect |
|---|---|
| « Activer » (Enable) | Emby's playback transcodes and conversions are handed to the workers. Without an available worker, Emby does the work itself, as usual. |
| « Désactiver » (Disable) | Emby works exactly as without the plugin. Takes effect immediately, no restart. |

The plugin refuses to enable as long as the coordinator is not running, and shows why.

Nothing to do after an **Emby update**: at startup, the plugin finds Emby's ffmpeg again and sets itself up again.

---

## 5. Install and pair a worker PC

### 5.1 Download and check

1. From the GitHub **Releases** page, download `EmbyRemoteWorker-Setup-<version>.exe` and its `.sha256` file.
2. Check it in PowerShell: `Get-FileHash .\EmbyRemoteWorker-Setup-<version>.exe -Algorithm SHA256`, and compare with the `.sha256` file.

### 5.2 Windows SmartScreen and antivirus

The installer and the program are **not code-signed** (no paid certificate for a personal project). Windows may therefore warn you:

- SmartScreen « Windows a protégé votre ordinateur » / "Windows protected your PC": click **"More info"** (« Informations complémentaires »), then **"Run anyway"** (« Exécuter quand même »);
- or before running it: right-click the file → Properties → check **Unblock** (« Débloquer ») → OK.

Copying the file from a USB key or a network share usually avoids the warning. If your antivirus quarantines the file, restore it and add an exclusion for the installation folder. On a company PC, ask your IT department.

### 5.3 Run the installer

1. Double-click `EmbyRemoteWorker-Setup-<version>.exe`. It does **not** ask for administrator rights: it installs for the current Windows user only.
2. The window « Installation de Emby Remote Worker » offers:
   - « Dossier d'installation » (Installation folder): `%LOCALAPPDATA%\Programs\EmbyRemoteWorker` by default;
   - « Démarrer avec Windows » (Start with Windows);
   - « Créer un raccourci sur le Bureau » (Create a desktop shortcut);
   - « Lancer Emby Remote Worker à la fin » (Launch Emby Remote Worker at the end).

   The FFmpeg licence (GNU GPL v3) is displayed: FFmpeg is included as a separate program.
3. Click « Installer » (Install), then « Fermer » (Close).

The worker starts and its icon appears in the notification area (click the small arrow next to the clock if you do not see it).

**Silent installation** (for several PCs):

```
EmbyRemoteWorker-Setup-<version>.exe --quiet [--dir <folder>] [--no-startup] [--no-launch]
```

Errors are written to `%TEMP%\EmbyRemoteWorker-Setup.log`.

### 5.4 Pair the worker with the server

Pairing tells the worker where the server is and gives it a personal key.

1. On the **plugin page** (in a browser), section « Workers »: type a name in « Nom du nouveau worker » (Name of the new worker), for example `PC-A`, then click « Associer un worker » (Pair a worker).
2. A long « Code d'association » (Pairing code) appears. **Copy it now: it is shown only once.** It contains a secret: paste it only into that worker.
3. On the PC, open the worker window (double-click the icon) → tab « Paramètres » (Settings).
4. Paste the code into « Code d'association » and click « Associer » (Pair).
5. Optionally adjust « Nom du worker » (Worker name), the name shown in Emby, then click « Enregistrer » (Save).

Pairing from the command line is also possible: `EmbyRemoteWorker.exe pair <code>` (the program is in the installation folder).

### 5.5 Check that it works

In the worker window, tab « État » (Status):

- « Serveur : … — connecté » (Server: … — connected);
- « Encodeurs validés » (Validated encoders): for example `libx264`, plus `h264_nvenc`, `h264_qsv` or `h264_amf` if your graphics card works. Encoders are tested with real short encodes the first time; « Refaire la détection du matériel » (Detect hardware again, in Settings) repeats the test after a driver change;
- the state: « disponible » (available) when the PC accepts work.

On the plugin page, the worker appears in the « Workers » table with its state, load, validated capabilities and last contact time.

Now click « Activer » on the plugin page if it is not enabled yet.

---

## 6. Daily use

### 6.1 Worker states

| State (worker / plugin page) | Icon colour | Meaning |
|---|---|---|
| « disponible » (available) | green | Accepts work. |
| « occupé » (busy) | blue | All its job slots are used (a playback can still take the place of a conversion). |
| « en drainage » (draining) | orange | Accepts nothing new; playbacks already running finish on this PC. |
| « indisponible » (unavailable) | grey | You do not allow work right now, or the PC is busy with other things. The reason is displayed. |
| « hors ligne » (offline) | grey | Not connected to the server. |
| « en erreur » (error) | red | Something went wrong; see the logs. |

The tooltip of the icon shows the state and the number of jobs.

### 6.2 The notification-area icon

Right-click the icon:

| Menu item | What it does |
|---|---|
| State and reason, jobs in progress | Information only. |
| « Disponible » (Available) | Check/uncheck to allow or forbid work on this PC. This choice is remembered after a restart. |
| « Libérer le PC maintenant » (Release the PC now) | Stops all jobs immediately (after a confirmation) and unchecks « Disponible ». Playbacks continue on another worker or on the server, possibly with a short interruption. |
| « Ouvrir… » (Open) | Opens the window (double-click on the icon does the same). |
| « Paramètres… » (Settings), « Journaux… » (Logs) | Opens the window on that tab. |
| « À propos (version …) » (About) | Version and details. |
| « Quitter » (Quit) | Closes the worker cleanly. Its jobs resume elsewhere. |

### 6.3 The worker window

The title shows the version. There are three tabs and a menu « Aide » (Help).

**Tab « État » (Status)**

- « Autoriser l'utilisation de ce PC » (Allow this PC to be used): the same switch as « Disponible » in the icon menu.
- « Libérer le PC maintenant » (Release the PC now).
- Reason for the current state, server and connection.
- « Charge » (Load): total CPU and the part used by the worker's jobs, GPU, memory.
- « File de conversion d'Emby » (Emby's conversion queue): how many items Emby still has to convert (pending or in progress), refreshed every 30 seconds. "—" if your Emby version does not allow reading it.
- « Encodeurs validés » (Validated encoders) and graphics cards.
- « Tâches en cours » (Jobs in progress), one line per job:

| Column | Meaning |
|---|---|
| « Fichier » (File) | Movie or episode. |
| « Type » | « Lecture » (Playback) or « Conversion ». |
| « Encodeur » (Encoder) | Encoder used, for example `libx264` or `h264_qsv`. |
| « Tranche » (Slice) | For a conversion: slice number out of the total, for example `4/18`. |
| « Position » | Where the job is in the movie. |
| « Tranche % » (Slice %) | Progress of the current slice. |
| « Total » | Progress of the whole conversion, all machines together, with the remaining time. |
| « Vitesse » (Speed) | Encoding speed (`2.0x` = twice real time). |
| « Durée » (Duration) | How long the job has been running. |
| « État » (Status) | In progress, or the reason it stopped. |

Hover a column for a tooltip.

**Tab « Paramètres » (Settings)** — see [section 6.5](#65-worker-settings).

**Tab « Journaux » (Logs)** — live log, « Exporter (7 derniers jours)… » (Export the last 7 days, as a zip file with secrets masked) and « Ouvrir le dossier » (Open the folder).

**Menu « Aide » (Help)** — « Journaux… », « Ouvrir le dossier de configuration » (Open the configuration folder), « À propos d'Emby Remote Worker… » (version, worker name and ID, server, FFmpeg version, data folder).

### 6.4 Making the PC available or not

You stay in control of your PC:

- **Manually**: uncheck « Disponible » when you want the PC for yourself, check it again later.
- **Automatically**: in Settings, choose « Automatique : disponible après une inactivité de … minutes sans clavier ni souris » (Automatic: available after … minutes without keyboard or mouse). Default: 10 minutes.
- **Time windows**: allow work only on certain days and hours.
- **Battery**: block work when a laptop runs on battery.
- **Load of other programs**: when other programs (a game, a video call…) use too much CPU, GPU or memory, the PC becomes « indisponible » on its own, then available again when the load drops. The worker's own encoding is not counted.

When you take your PC back, you choose what happens in « Quand j'utilise de nouveau le PC » (When I use the PC again):

- « Terminer les lectures en cours » (Finish playbacks in progress, default): the PC accepts nothing new, running playbacks finish on it, conversions move elsewhere.
- « Libérer immédiatement le PC » (Release the PC immediately): everything stops at once; playbacks continue on another worker or the server and may have a short interruption.

A load spike from other programs never interrupts a running playback: the worker switches to draining.

### 6.5 Worker settings

Tab « Paramètres ». Click « Enregistrer » (Save) to apply, « Annuler » (Cancel) to discard.

| Setting | Default | Notes |
|---|---|---|
| « Nom du worker » (Worker name) | PC name | Name shown on the plugin page. |
| « Code d'association » (Pairing code) + « Associer » | — | See section 5.4. |
| « FFmpeg » | empty | Empty = the FFmpeg included with the worker (recommended). |
| « Conversions : encodage par le GPU » (Conversions: encoding on the GPU) | off | Faster. If a slice is then resumed on the server, one file mixes two encoders (see limitations). |
| « Lectures : encodage par le GPU » (Playbacks: encoding on the GPU) | off | Much faster on a laptop. The picture from the worker and from the server is slightly different. Off = same encoder as the server, so hand-overs are invisible. |
| « Décodage vidéo par le GPU quand l'encodage s'y fait » (GPU video decoding when encoding on the GPU) | on | Relieves the processor; identical picture. Only used when one of the two options above is on. Falls back to the processor automatically if needed. |
| « Tâches simultanées maximum » (Maximum simultaneous jobs) | 2 | A conversion only uses a free slot. |
| « Refaire la détection du matériel » (Detect hardware again) | — | Re-tests the encoders, for example after a driver update. |
| « Manuel » / « Automatique » (Availability mode) | Manual, 10 min | See 6.4. |
| « Bloquer les traitements sur batterie » (Block work on battery) | off | |
| « Bloquer si les autres applications utilisent plus de » (Block if other applications use more than) CPU / GPU / « mémoire totale » (total memory) | 50 % / 50 % / 90 % | Load left by other programs. |
| « Temporisation des seuils » (Threshold delay) | 30 s | A threshold must be exceeded this long to block, and the load must stay low this long to unblock. |
| « Stabilisation avant disponibilité » (Settling time before available) | 20 s | All conditions must be met this long before the PC becomes available again. |
| « Quand j'utilise de nouveau le PC » (When I use the PC again) | Finish playbacks | See 6.4. |
| « Plages horaires autorisées » (Allowed time windows) | none | One line per window: tick the days (Lu, Ma, Me, Je, Ve, Sa, Di = Mon…Sun), « Début » (Start) and « Fin » (End) as HH:mm. No line = any time. An end before the start (22:00 → 07:00) crosses midnight. |
| « Démarrer avec Windows » (Start with Windows) | as chosen at install | |

### 6.6 GPU encoding and decoding

- By default, workers encode with the **processor**, with the same encoder as Emby. This gives invisible hand-overs between a worker and the server.
- Turn on « Lectures : encodage par le GPU » on a laptop that is too slow in software (playback stutters).
- Turn on « Conversions : encodage par le GPU » to convert much faster. Quality is kept constant (constant-quality mode of each encoder).
- Only encoders that passed the test on this PC are used. A GPU listed by FFmpeg but not working is ignored.

### 6.7 The plugin page, day to day

The page refreshes by itself.

- **« Workers »**: for each paired worker: name and address, state and reason (for example « indisponible — PC utilisé », unavailable — PC in use), « conversions suspendues pendant les lectures » (conversions paused during playbacks) when relevant, jobs in progress out of the maximum, load, « Capacités validées » (validated capabilities), « Vu » (last seen), and a « Révoquer » (Revoke) button. Revoking disconnects the worker at once; its jobs resume elsewhere; it can never reconnect with that code.
- **« Tâches en cours »** (Jobs in progress): type, file, worker, position, segments, speed. When empty: « Aucune tâche sur les workers. Sans worker, Emby transcode sur le serveur comme d'habitude. » (No job on the workers. Without a worker, Emby transcodes on the server as usual.)
- **« Conversions »**:
  - a box **« Conversion en cours : <file> »** (Conversion in progress) for each running conversion: progress bar, percentage, combined speed, « reste » (time remaining), « tranches » (slices done / total), and « En cours sur » (Running on) with each machine, its slice and its speed;
  - « File de conversion d'Emby : N élément(s) à convertir (en attente ou en cours) » (Emby's conversion queue: N item(s) to convert);
  - the history (kept 7 days): file, state (« en attente », « en cours », « assemblage », « terminé », « échec », « annulé » = pending, running, assembling, completed, failed, cancelled), note (for example « assemblé et vérifié par le serveur », assembled and verified by the server), progress, workers, slices and chunks.

---

## 7. Conversions (Convert and downloads)

### 7.1 Starting a conversion

You start conversions **in Emby itself**, as usual: the « Convert » action on a movie, episode or series, or a download to a device, with Emby's quality profiles. (Emby's « Convert » and download features require an **Emby Premiere** subscription; that is Emby's rule, not the plugin's.) The plugin page only shows the progress. Emby converts its queue one item at a time; the plugin speeds up each item.

### 7.2 What goes to the workers and what stays on the server

| Kind of conversion | Where it is done |
|---|---|
| Video re-encoded (most conversions) | Split into slices, encoded by all available workers **and** by the server, then assembled on the server. |
| Remux (video copied, only the audio converted) | **Stays on the server**: there is nothing heavy to compute, and sending the file to a PC would take longer. |
| Audio only | Server (Emby's own ffmpeg). |
| Burned-in subtitles | Server. |
| Neither the workers nor the server can encode it | Emby's own conversion, as without the plugin. |

A conversion is accepted as soon as a capable worker is connected **or** the server can encode the slices. A PC switched on in the middle of an episode joins in within about 10 seconds. If at least one worker is paired but none is connected yet (for example right after the server restarts), the conversion waits up to 10 minutes for one before going back to Emby, if the server cannot do the slices itself.

### 7.3 HDR to SDR

When Emby converts an HDR movie to SDR, the tone mapping is done with standard FFmpeg filters, identically on workers and on the server, so every slice looks the same. If the server cannot do it, it leaves those slices to the workers. HDR conversions are heavy: a PC with a graphics card helps a lot.

### 7.4 What if a PC is switched off during a conversion?

Nothing is lost. The interrupted slice resumes from its last finished chunk (about 30 seconds of video) on another worker, or on the server if no worker is free for 20 seconds, and goes back to a worker later. The final file is still checked to the image.

### 7.5 The final check

The server assembles the slices with Emby's own command (Emby's audio and subtitles), then checks the total duration and the video duration to the image. If the check fails, the file is rejected and Emby redoes the conversion itself. You never get a file with missing images without knowing it.

---

## 8. Update, roll back and uninstall

### 8.1 Update the plugin

1. Download the new DLL and check its SHA-256 (section 3.1).
2. **Keep a copy of the current DLL** before replacing it (on Synology the install task does this for you: `RemoteTranscode.EmbyPlugin.dll.before-<date>`).
3. Replace the file in the plugins folder and restart Emby (section 3.2).
4. Reload the Emby page (F5). Paired workers are kept.

### 8.2 Roll back the plugin

Put the old DLL back in the plugins folder, in place of the new one, and restart Emby. On Synology, see the roll back steps in [section 3.2](#32-copy-it-into-embys-plugins-folder).

### 8.3 Update a worker

Run the new installer on the PC. Because the worker is already installed, a small window « Mise à jour de Emby Remote Worker » (Update) shows the installed version and the new one, with one button « Mettre à jour » (Update) (« Réinstaller » (Reinstall) for the same version). The worker is closed cleanly (its jobs resume elsewhere), files are replaced, and the worker restarts. Folder, settings, start-with-Windows option and pairing are kept. It takes a few seconds. `--quiet` updates without a window.

Workers and the plugin have separate version numbers. Install both from the same release when possible.

### 8.4 Turn the plugin off temporarily

Click « Désactiver » on the plugin page: immediate, no restart.

### 8.5 Uninstall the plugin

Dashboard → Plugins → « Transcodage distribué » → Uninstall. Emby goes back to its own ffmpeg (or to the custom ffmpeg you had set before) immediately.

Full cleanup, if you do not plan to reinstall: delete the folder `remote-transcode` in Emby's data folder (on Synology `/var/packages/EmbyServer/var/remote-transcode`) and the file `plugins/configurations/RemoteTranscode.EmbyPlugin.xml`. Workers will then have to be paired again.

If you deleted the DLL by hand instead of uninstalling, Emby keeps working (everything goes to its own ffmpeg). To go back fully to the original state: Dashboard → Transcoding → uncheck the custom ffmpeg path.

### 8.6 Uninstall a worker

Windows Settings → Apps → **Emby Remote Worker** → Uninstall. The program closes, removes its start-up entry and shortcuts, then deletes only the files it installed. It offers to also delete its data (`%LOCALAPPDATA%\EmbyRemoteWorker`: settings, pairing, logs); they are kept by default so that a reinstall does not need a new pairing. Then revoke the worker on the plugin page if you do not plan to use it again.

---

## 9. Troubleshooting and FAQ

### 9.1 Where are the logs?

| Log | Where | What you find |
|---|---|---|
| Worker, daily log | `%LOCALAPPDATA%\EmbyRemoteWorker\logs\worker-YYYYMMDD.log` (tab « Journaux », or Help → « Journaux… ») | Connection, state changes and reasons, jobs received or refused, encoder used, errors. |
| Worker, one per job | `%LOCALAPPDATA%\EmbyRemoteWorker\logs\job-<id>.log` | Full FFmpeg output of that job. |
| Worker installer | `%TEMP%\EmbyRemoteWorker-Setup.log` | Silent installs and updates, failures. |
| Emby server log | Dashboard → Logs, file `embyserver.txt` (Synology: `/var/packages/EmbyServer/var/logs`) | Plugin and coordinator messages (category "RemoteTranscode"): workers connecting and leaving, jobs, conversions. |
| Emby transcode logs | Dashboard → Logs, files `ffmpeg-transcode-*.txt` | One per transcode; lines starting with `remote-transcode:` explain each decision and hand-over. |

To share worker logs, use « Exporter (7 derniers jours)… »: secrets are masked. Emby's own logs contain client access tokens: check them before sharing.

### 9.2 Common situations

| Symptom or message | Meaning | What to do |
|---|---|---|
| Worker « indisponible » with « CPU occupé par d'autres applications (…%) » / « GPU occupé… » / « mémoire saturée… » (CPU/GPU busy with other applications, memory full) | Other programs on the PC use more than the thresholds. | Normal while you use the PC. Otherwise raise the thresholds in Settings. |
| « charge élevée (retour en cours) » (high load, returning) | The load is back under the thresholds; the worker waits for the threshold delay (30 s). | Wait. |
| « stabilisation avant disponibilité » (settling before available) | All conditions are met; 20-second settling time. | Wait. |
| « PC utilisé » (PC in use) | Automatic mode: keyboard or mouse used recently. | Normal. |
| « hors plage horaire » (outside allowed hours), « sur batterie » (on battery), « désactivé par l'utilisateur » (disabled by the user) | Your own rules. A PC released with « Libérer le PC » stays disabled. | Check « Disponible » again, or change the rules. |
| Worker « hors ligne » (offline) | Not connected: invalid or revoked pairing code, server certificate changed, proxy without WebSocket header, or address blocked after 10 failures. | Read the worker log. Pair again. Check the proxy. Wait 15 minutes if blocked. |
| Pairing: « Code d'association invalide. » (Invalid pairing code) | The code was incomplete or mistyped. | Create a new code on the plugin page and paste it in full. |
| Conversion note « conversion sur le serveur (aucun worker connecté capable de cette conversion) » (conversion on the server: no connected worker capable of it) | No worker could do it and the server could not encode the slices itself, so Emby converted it alone. | Make sure a worker is connected and available; check its « Encodeurs validés ». |
| « conversion sur le serveur (pas de réencodage vidéo (remux)) » (conversion on the server: no video re-encoding) | A remux: it stays on the server by design. | Nothing. Emby may convert at real-time speed; Emby's "full speed conversion" setting makes it faster. |
| « résultat rejeté (…), conversion sur le serveur » (result rejected) | The final check failed; Emby redoes the conversion. | Nothing; the file you get is correct. If it repeats, see the next line. |
| A conversion fails at assembly, with or without workers | Emby's information about the file is outdated (for example an audio track that no longer exists after the file was replaced). | Refresh the item's metadata in Emby (or scan the library), then convert again. |
| The plugin page shows « Coordinateur intégré à Emby : arrêté (…) » or « activé mais non appliqué » | Port already in use, helper program not allowed to run on this server, or Emby setting not found. | Read the reason on the page and in `embyserver.txt`. Change the port if it is in use. |
| The plugin page does not open in the Emby Android app (« veuillez vous connecter directement à votre serveur local ») | The apps do not run third-party plugin pages. | Open Emby in a web browser (also works on a phone). |
| The web player says no compatible stream, or the video is tiny | Emby's web player lowered and remembered its maximum quality after earlier failures. Not related to the plugin. | In Emby on that browser: Settings → Playback → maximum streaming quality on Auto, or during playback: gear icon → Quality → Auto. |
| Playback stutters on a worker | The worker is too slow in software (low-power laptop), or the PC's internet connection is too slow. | Plug the laptop in, enable « Lectures : encodage par le GPU », or keep that PC for conversions. |
| SmartScreen or antivirus blocks the installer | Unsigned program. | See section 5.2. |
| The plugin page looks old after an update | Browser cache. | Reload with F5. |

### 9.3 Is the problem caused by the plugin?

1. On the plugin page: is the coordinator « en service », is the plugin « activé »? Are workers connected and available?
2. Click « Désactiver ». If the problem remains, it does not come from the plugin.

### 9.4 FAQ

**Do I need a worker for the plugin to be useful?** No worker means Emby works as usual. The plugin only helps when at least one PC is available.

**Does a playback use several PCs at once?** No. A playback runs on one PC at a time (or on the server). Conversions use all PCs.

**Is a port opened on my PCs?** No. Workers connect to the server; nothing listens on the PC.

**Can my PC be used while I play a game?** In automatic mode, or with load thresholds, the PC steps aside by itself. You can also uncheck « Disponible » or use « Libérer le PC maintenant » at any time.

**What happens when the PC goes to sleep?** The server notices within about 15 seconds and takes over at the exact point. The playback may pause briefly if the player had little video in advance.

---

## 10. Known limitations

- **Tested platform**: only Emby 4.10.2 on Synology (Linux x64). Emby for Windows, Emby in Docker and Linux arm64 are supported by design but not tested yet. At startup, the plugin checks that its helper program runs; if not, Emby keeps its own ffmpeg and the plugin page shows why.
- **Unofficial Emby setting**: the plugin uses Emby's "custom ffmpeg path" setting, which Emby marks as unsupported. A future Emby version could change it. The plugin detects this and then leaves Emby untouched.
- **Plugin page**: works in a web browser only, not in the Emby apps (Android…).
- **Not signed**: the installer and the worker are not code-signed. SmartScreen warns, and some antivirus products (especially in companies) may block them.
- **Mixed encoders**: when PCs with different encoders (processor, Intel, NVIDIA…) share one conversion, the file contains parts from different encoders. FFmpeg decodes it without errors and a TV played it without problems, but an unusual player could, in theory, dislike it.
- **Internet workers**: throughput depends on the upload speed of your home connection and the download speed of the PC.
- **One PC per playback**: a playback's segments are not spread over several PCs.
- **Burned-in subtitles**: conversions with burned-in subtitles are done by the server.
- **Remuxes** stay on the server and follow Emby's own speed.
- **Start of a movie**: when a playback started at the very beginning is moved to another machine, timestamps step back by 80 ms at the join. No effect was seen in Chrome.
