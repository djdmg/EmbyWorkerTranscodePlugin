# Banc de test local — transcodage distribué (sans Emby, sans NAS)

English version: [README.md](README.md)

Tests d'acceptation (cahier des charges §11) exécutés entièrement sur ce PC : un coordinateur autonome, deux ou
trois **workers de test** (instances séparées d'`EmbyRemoteWorker.exe`) et le relais `remote-ffmpeg` lancé avec des
arguments copiés des vraies commandes d'Emby 4.10. Rapport : `C:\emby\docs\03-tests-acceptation-local.md`.

## Sécurité du worker de production

* Le worker de production (`C:\emby\artifacts\worker\EmbyRemoteWorker\EmbyRemoteWorker.exe`, données dans
  `%LOCALAPPDATA%\EmbyRemoteWorker`) n'est **jamais** touché : chaque worker de test a son propre
  `EMBY_WORKER_HOME` (`work\workers\w1`, `w2`, `w3`), donc sa propre configuration, son propre mutex et sa propre
  commande `release`.
* Seuls les processus démarrés par le banc sont arrêtés, **par PID** (`work\pids.txt`, avec contrôle du nom et de
  l'heure de démarrage). Le banc vérifie après chaque test que le PID du worker de production est toujours là.
* Ports : 38097 (workers, HTTPS, boucle locale) et 38098 (relais/administration, boucle locale).

## Prérequis

* .NET 8 SDK (`C:\Program Files\dotnet\dotnet.exe`) et le `nuget.config` local de `C:\emby`.
* ffmpeg du worker : le build « full » 9.0.2 (libplacebo, celui du worker 0.7.15) : celui de winget s'il est « full », sinon `C:\emby\wip\gputonemap\ffmpeg-full\...\ffmpeg.exe`, sinon `C:\emby\artifacts\worker\EmbyRemoteWorker\ffmpeg\ffmpeg.exe` (génération des médias
  et encodage sur les workers de test).
* ffmpeg/ffprobe « serveur » : build amont Gyan 9.0.2 installé par winget
  (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\...\bin`), en remplacement du ffmpeg modifié d'Emby.
* `curl.exe` (fourni par Windows).

## Utilisation

```powershell
cd C:\emby\test\local
# 1. Compiler coordinateur, relais et worker (win-x64) dans work\bin
$d = 'C:\Program Files\dotnet\dotnet.exe'
foreach ($p in @(@('RemoteTranscode.Coordinator','coordinator'), @('RemoteTranscode.Shim','shim'), @('RemoteTranscode.Worker','worker'))) {
  & $d publish "C:\emby\src\$($p[0])\$($p[0]).csproj" -c Release -r win-x64 -o "work\bin\$($p[1])" --nologo -v q }
# 1b. Faux ffmpeg d'Emby (seulement pour les tests LF, voir plus bas)
& $d publish fake-emby-ffmpeg\FakeEmbyFfmpeg.csproj -c Release -r win-x64 --self-contained false -o work\bin\fake-emby-ffmpeg --nologo -v q
# 2. Générer les médias (≈ 3 min la première fois)
pwsh -File .\00-gen-media.ps1
# 3. Vérification rapide (coordinateur + w1 + une lecture)
pwsh -File .\smoke.ps1
# 4. Tous les tests (≈ 30–40 min) ou une sélection
pwsh -File .\run-tests.ps1
pwsh -File .\run-tests.ps1 -Only T06,T08
# En cas d'interruption brutale : arrêter tout ce que le banc a lancé (et rien d'autre)
pwsh -File .\cleanup.ps1
```

Chaque exécution de `run-tests.ps1` repart de zéro : nouvelles données du coordinateur (certificat, secrets,
appariements), nouveaux workers de test, sorties et journaux effacés.

## Démarrage rapide (LocalFirstSeconds) : faux ffmpeg d'Emby et tests LF

Depuis la version 0.7.7, le relais démarre une lecture HLS sur le ffmpeg du serveur puis la confie à un worker au
segment exact (`RunLocallyAsync` → `HandBackAsync`). Ce suivi repose sur les lignes stderr `SegmentComplete=` que seul
le ffmpeg modifié d'Emby écrit ; le ffmpeg amont du banc ne les écrit pas et refuse les options du fork. Les tests
T01–T12 gardent donc `LocalFirstSeconds = 0` et le ffmpeg amont (relais `work\bin\shim`, inchangé).

**Faux ffmpeg d'Emby** (`fake-emby-ffmpeg\`, projet .NET 8 autonome publié dans `work\bin\fake-emby-ffmpeg\`) :

* retire les options du fork avec leur valeur (`-loglevel +timing` → préfixe horaire sur ses propres lignes,
  `-segment_write_temp`, `-print_graphs_file`, `-min_frame_time`, `-write_empty_segments`) et lance le ffmpeg amont
  avec le reste ; stdin (touche `q`) et code de sortie transmis ; ffmpeg amont rattaché à un objet de travail Windows
  (il meurt avec le faux ffmpeg) ;
* sortie HLS (`-f segment … -segment_list x.m3u8`) : fait écrire au ffmpeg amont une liste CSV (`x.m3u8.fake.csv`,
  temps absolus comme le muxer du fork), tient lui-même la liste m3u8 et écrit pour chaque segment fermé
  `SegmentComplete=video:0 Index=N Start=… End=… Duration=… offset_pts=0 start_pts=… Frames=0 filename=…`
  (End absolu exact ; Start = 0 au 1er segment d'un run avec `-ss`, comme le muxer) ; les lignes de progression
  `frame=… time=… speed=…` du ffmpeg amont sont relayées ligne par ligne ;
* réglages relus à chaque lancement dans `work\bin\fake-emby-ffmpeg\fake-emby-ffmpeg.json` (écrit par
  `Set-FakeFfmpeg` de `lib.ps1`) ou variables d'environnement `FAKE_EMBY_FFMPEG_*` (prioritaires) :

| Réglage | Effet |
|---|---|
| `RealFfmpeg` / `FAKE_EMBY_FFMPEG_REAL` | ffmpeg amont lancé (le ffmpeg « serveur » du banc) |
| `ReadRate` / `FAKE_EMBY_FFMPEG_READRATE` | `-readrate x` ajouté avant le 1er `-i` : `0.8` simule un serveur trop lent (< 1,2x) |
| `ReadRateInitialBurst` / `FAKE_EMBY_FFMPEG_BURST` | `-readrate_initial_burst` (défaut ffmpeg 0,5 s) |
| `StartDelayMs` / `FAKE_EMBY_FFMPEG_START_DELAY_MS` | attente avant de lancer ffmpeg (serveur qui ne produit rien ; `q` reste pris en compte) |
| `StartZero` / `FAKE_EMBY_FFMPEG_START_ZERO` | `first` (défaut, comme le muxer), `all` (Start=0 partout), `none` (Start réel) |
| `ProbeDelayMs` / `FAKE_EMBY_FFMPEG_PROBE_DELAY_MS` | mode ffprobe (arguments `-show_*`) : ffprobe amont lancé après ce délai — sert de `ServerFfprobe` lent au coordinateur |
| `LogFile` / `FAKE_EMBY_FFMPEG_LOG` | journal des lancements (`work\logs\fake-emby-ffmpeg.log` : arguments reçus/transmis, code, nombre de segments) |

**Relais `work\bin\shim-localfirst\`** : copie de `shim\remote-ffmpeg.exe` dont le `shim.json` a
`RealFfmpeg` = faux ffmpeg et `LocalFirstSeconds = 20` (`Initialize-LocalFirstShim`, appelé par `run-tests.ps1` dès
qu'un test LF est demandé).

```powershell
pwsh -File .\run-tests.ps1 -Only LF          # LFa, LFc, LFd, LFe, LFb, LFb4, LFf (≈ 8 min)
pwsh -File .\run-tests.ps1 -Only LFa,LFb
```

Avant les tests, `Initialize-LocalFirstMedia` crée les copies de `film_h264.mkv` (`_lent`, `_bloque`, `_1x5`, `_q`, `_avis0`,
`_avis`), les lit une fois, puis fait une lecture « à blanc » de chacune (sauf `_avis*`) pour que le coordinateur ait
déjà sondé la source : sinon l'avis `ShimStartAdvice` peut dépasser les 2 s qu'attend le relais, qui part alors en
ffmpeg local pur, sans trace ni bascule (défaut du relais, démontré par LFf).

| Test | Scénario | Vérifications |
|---|---|---|
| LFa | w1 seul (redémarré, cache vide), lecture 720p | 1er segment par le serveur (délai clic → segment), bascule `back on worker` après ~20 s de vidéo, délai du 1er segment du worker, continuité (indices, jonction réelle des .ts, m3u8), `préchargement` dans le journal de w1 et blocs dans `cache\inputs` avant la bascule, vitesse retenue par le coordinateur, `q` : tout s'arrête |
| LFb / LFb2 / LFb3 | serveur à 0,8x (`_lent`) ; 2e lecture du même fichier ; serveur bloqué 30 s (`_bloque`) | bascule dès le 2e segment ou après 4 s ; avis « serveur trop lent pour ce film » et aucun lancement du ffmpeg serveur ; règle des 4 s (worker attendu ≤ 8 s après le lancement) |
| LFb4 | serveur à 1,5x (`_1x5`), au-dessus du seuil de 1,2x | reste sur le serveur jusqu'à ~20 s d'avance, vitesse retenue ≥ 1,2x |
| LFc | HDR10 avec tone mapping | directement sur le worker, aucun lancement du ffmpeg serveur |
| LFd / LFd2 | aucun worker, serveur à 3x ; puis w1 redémarré (cache vide) | tout sur le serveur sans erreur ; puis bascule sans préchargement (délai comparé à LFa) |
| LFe | `q` pendant la phase serveur (3x, `_q`) | sortie du relais, plus aucun ffmpeg / faux ffmpeg de la session, aucun job distant |
| LFf0 / LFf | coordinateur redémarré avec le faux ffmpeg comme ffprobe (durée des sondes journalisée) : sonde réelle (`_avis0`) puis ralentie de 3 s (`_avis`), fichiers non préchauffés ; coordinateur rétabli ensuite (journaux `coordinator-lff`, `coordinator-after-lff`) | le relais doit démarrer sur le serveur puis basculer même si l'avis arrive après 2 s |

Les copies de `film_h264.mkv` évitent que la vitesse « trop lente » retenue par le coordinateur (par fichier)
n'influence les autres scénarios. La jonction serveur (run sans `-ss`) → worker (run avec `-ss`) recule de ~80 ms
(D1, connu et sans effet sur Emby Web) : elle est tolérée entre −100 et +10 ms, sans image en double.

## Fichiers

| Fichier | Rôle |
|---|---|
| `lib.ps1` | Chemins, lancement/arrêt des processus (aide C# : stderr horodaté vers fichier, stdin ouvert pour `q`), configuration coordinateur/workers/relais, construction des arguments « à la Emby », vérifications ffprobe, segments et jonctions |
| `tests.ps1` | Les tests T01 à T12 |
| `tests-localfirst.ps1` | Les tests LF du démarrage rapide (relais `shim-localfirst` + faux ffmpeg d'Emby) |
| `fake-emby-ffmpeg\` | Faux ffmpeg d'Emby (projet .NET 8 autonome, aucune dépendance à `src`) |
| `run-tests.ps1` | Mise en place du banc, exécution, arrêt, tableau de résultats |
| `tests-sleep.ps1` | T13 : mise en veille simulée d'un worker (processus gelés 40 s) pendant une lecture et une conversion ; T14–T19 (conversions : worker en échec, serveur en parallèle d'un worker, audio à l'avance, serveur d'abord, HDR, serveur seul) ; T20 : source AV1 10 bits à départ vidéo de 7 ms (`media\film_av1_10bit.mkv`, générée au premier lancement), w1 (logiciel puis GPU) + serveur, nombre d'images de la sortie et durée listée contre durée réelle de chaque morceau (dossier de travail copié dans `out\spool-t20x`, table `chunks-measured.csv`) ; T21 : conversion HDR tone mappée sur le GPU (libplacebo) par w1 seul, graphes envoyés, device Vulkan, aucune tranche sur le serveur, nombre d'images ; T30 : contrôle de synchro sur tout le fichier : un assemblage désynchronisé exprès (`EncodeTestListedDurationShift`, +12 ms puis +200 ms sur un morceau) rejeté, le fichier livré par la commande d'Emby correct ; une conversion propre d'un AVI à B-frames compactées et MP3 et d'un MPEG-TS de 300 s non rejetée. T31 : source à cadence variable (images de 10 à 160 ms, vérifié à ffprobe) et `-r` d'Emby, chaque image portant son numéro en 16 cases : sans le filtre `fps` rejet (T31a), avec lui pas de rejet et le bon nombre d'images (T31b, c), y compris un worker arrêté en cours (T31d) ; première image hors de la grille (T31e, fichier à mesurer à part). `nas-sync-probe.sh`, à lancer sur le serveur : les valeurs dont dépend ce contrôle, mesurées avec le ffprobe d'Emby, et sa conclusion. |
| `00-gen-media.ps1` | Médias de test : H.264 1080p 150 s (2 pistes audio fre 440 Hz / eng 880 Hz, sous-titres SRT intégrés + `.srt` externe), HEVC 10 bits HDR10 (BT.2020/PQ) 120 s, police |
| `smoke.ps1` / `cleanup.ps1` | Vérification rapide / nettoyage |

## Données produites (`work\`, non versionné)

* `work\media\` : médias de test ; `work\out\` : segments HLS (un dossier par session) et conversions `conv-*.mp4`.
* `work\logs\` : `coordinator.log`, `worker-<nom>[-n].log` (n = redémarrage), `shim-<test>.log` (stderr du relais,
  c'est-à-dire exactement ce qu'Emby lirait : lignes `SegmentComplete=`, progression, messages `remote-transcode:`).
* `work\results.jsonl` / `work\results.md` : verdicts.
* `work\coord\data\` : certificat, `workers.json`, `shim.secret`, `admin.secret`, `encodes.json`.

## Réglages propres au banc

* Workers de test : seuils de charge neutralisés (`MaxForeign*` = 1000 %) car les workers de test et le worker de
  production partagent le même processeur et se verraient comme « autres applications » ; stabilisation 2 s.
* Les commandes de lecture reprennent la forme réelle d'Emby 4.10 (`-loglevel +timing`, `-segment_write_temp 1`,
  `-print_graphs_file`, filtres `scale@f1`, `subtitles@f2`, `supertonemap@f4`...). Les conversions sont envoyées
  **sans** les options propres au fork d'Emby, pour que l'assemblage et le repli sur le serveur puissent s'exécuter
  avec le ffmpeg amont.
* Chemins : les planificateurs attendent des chemins Unix ; le banc passe `C:\emby\...` sous la forme `/emby/...`
  (même lecteur), ce que le coordinateur accepte sous Windows (`ServerFiles.Canonical`).
