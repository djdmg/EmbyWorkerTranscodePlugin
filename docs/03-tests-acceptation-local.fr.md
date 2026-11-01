# Tests d'acceptation en local (sans Emby, sans NAS) — 28/10/2026

English version: [03-tests-acceptation-local.md](03-tests-acceptation-local.md)

Banc de test : `C:\emby\test\local\` (mode d'emploi dans `README.md`). Il reproduit toute la chaîne sur un seul PC :
le coordinateur autonome, deux workers de test (trois pour T12) et le relais `remote-ffmpeg` lancé avec des
arguments copiés des commandes réelles d'Emby 4.10. Le relais joue le rôle d'Emby : il reçoit les commandes, et le banc
lit sur son stderr ce qu'Emby lirait. Les verdicts ci-dessous viennent de la dernière exécution complète
(`run-tests.ps1`, 13 min, 10:20–10:33). Les exécutions précédentes de la même journée donnent les mêmes résultats.

## 1. Environnement

| Élément | Valeur |
|---|---|
| OS | Windows 11 Entreprise 10.0.26200 (Intel Core Ultra 5 225U, iGPU Intel) |
| .NET | SDK .NET 8, publication `win-x64` autonome (coordinator.exe, remote-ffmpeg.exe, EmbyRemoteWorker.exe) |
| Sources | `C:\emby\src` au 28/10/2026 (pas de dépôt git, donc pas de commit de référence) ; tests unitaires : 115/115 réussis (129/129 au 29/10/2026) |
| ffmpeg des workers | `C:\emby\artifacts\worker\EmbyRemoteWorker\ffmpeg\ffmpeg.exe` 9.0.2-essentials (gyan.dev) ; encodeurs validés : h264_qsv, hevc_qsv, av1_qsv, libx264, libx265 |
| ffmpeg/ffprobe « serveur » | Gyan 9.0.2 full build (winget), **build amont** à la place du ffmpeg modifié d'Emby |
| Ports | 38097 (workers, HTTPS, certificat auto-signé épinglé), 38098 (relais/administration, boucle locale) |
| Médias | `film_h264.mkv` : H.264 1080p25, 150 s, audio 0:1 fre (sinus 440 Hz) et 0:2 eng (880 Hz), SRT intégré, plus un `.srt` externe ; `film_hdr10.mkv` : HEVC Main10 BT.2020/PQ avec métadonnées HDR10, 120 s |
| Worker de production | PID 13308, jamais touché (vérification après chaque test) ; workers de test isolés par `EMBY_WORKER_HOME` |

## 2. Résultats

| # | Test | Verdict | En bref |
|---|---|---|---|
| T01 | Conversion par un worker disponible | **PASS** | w2, 47 s, 5 morceaux, assemblée et vérifiée ; 150,02 s, h264 1280x720 + aac, décodage complet sans erreur |
| T02 | Répartition sur deux workers | **PASS** | 4 lectures simultanées : w1, w2, w1, w2 ; 2 conversions simultanées : une sur chaque worker, toutes deux valides |
| T03 | Aucun worker disponible | **PASS** (avec limite) | Refus du coordinateur, puis repli sur le ffmpeg local. Conversion faite sur le serveur et valide. Lecture : voir la limite L1 |
| T04 | Un worker devenu indisponible refuse les nouvelles tâches | **PASS** | Après `release` : Unavailable, nouvelles lectures et conversions refusées |
| T05a | Reprise du PC : « Libérer » (immédiat) | **FAIL** (partiel) | Reprise immédiate sur w2 au segment exact, indices continus, aucun doublon. Mais la jonction a un **recul de PTS de 80 ms** (D1) |
| T05b | Reprise du PC : drainage (FinishPlayback) | **PASS** | État Draining, la lecture continue sur le même job, la nouvelle lecture est refusée ; Unavailable une fois la lecture finie |
| T05c | Reprise du PC : `ReleaseImmediately` (fin de plage horaire) | **FAIL** (partiel) | Libération et reprise sur w2 correctes, même recul de 80 ms à la jonction (D1) |
| T06 | Lecture distante : démarrage, `q`, déplacement | **PASS** | 1er segment en 1,9 s ; sortie 0,7 s après `q` ; déplacement `-ss 60` / segment 20 sur le même worker (affinité de session) |
| T07 | ffmpeg distant arrêté après `q` | **PASS** | ffmpeg du job disparu 0,9 s après `q` (job de déplacement compris) |
| T08 | Déconnexion / reconnexion d'un worker | **FAIL** (partiel) | Worker tué : reprise sur l'autre en 0,1 s, pas de ffmpeg orphelin ; redémarré, il est de nouveau Available en 4,9 s. Même recul de 80 ms (D1) |
| T09a | Worker tué pendant une conversion (un autre est libre) | **PASS** | Reprise sur l'autre worker au morceau suivant ; fichier final complet et valide ; aucun fichier avant l'assemblage |
| T09b | Seul worker tué pendant une conversion | **PASS** | Repli sur le serveur au morceau 1, retour au worker dès qu'il revient ; fichier final complet et valide |
| T10a | Lecture : sous-titres SRT incrustés | **PASS** | Filtre traduit (`subtitles@f2=filename='aux/…'`) ; PSNR du bas d'image contre une référence sans sous-titres : 21,4 dB, donc le texte est bien rendu |
| T10b | Lecture : choix de la piste audio | **PASS** | `-map 0:2` donne 880 Hz (la référence `0:1` donne 440 Hz) |
| T10c | Conversion avec deux pistes audio | **PASS** | Faite à distance ; 2 pistes aac à 440 et 880 Hz ; 150,02 s |
| T10d | Conversion avec sous-titres incrustés | **PASS** (sur le serveur) | Le planificateur la laisse au serveur (D3) ; sortie valide |
| T10e | Conversion HDR10 vers SDR | **PASS** | Faite à distance, `supertonemap` traduit en zscale+tonemap ; h264 yuv420p bt709 ; PSNR Y contre la chaîne amont de référence : 45,3 dB |
| T10f | Lecture HDR10 vers SDR | **PASS** | Segments h264 yuv420p/bt709 1280x720 |
| T11 | Direct Play | **NON TESTABLE** | C'est Emby qui décide, aucun ffmpeg n'est lancé. Le remux reste local (tests unitaires) |
| T12 | Accès non autorisés refusés | **PASS** | 31 contrôles conformes (détail au §3) |

Bilan : 16 PASS, 3 FAIL partiels qui ont tous la même cause (D1), 1 NON TESTABLE.

**Mise à jour du 29/10/2026.**

- **D1** a été vérifié sur le serveur réel dans Chrome (Emby Web, hls.js) : lecture démarrée à 0, libération à 114 s puis retour sur le worker à 324 s, sans aucune pause ni trou dans le tampon. Le recul de 80 ms est donc sans effet pour ce lecteur ; les trois FAIL partiels sont conservés dans le banc comme indicateur, pas comme défaut bloquant.
- **D2** est corrigé (relais 0.7.9) : la durée annoncée d'un segment se calcule depuis la fin du segment précédent, et non depuis le `Start=0` du premier segment après une reprise.
- **Conversions** : elles sont maintenant découpées en **tranches** d'environ 2 minutes (puis 60, 60, 30, 30 s en fin), encodées en parallèle par les workers, bornes posées sur des images. Scénario ajouté : conversion d'un média de 30 min en 18 tranches avec un worker tué en cours de route, **45 000 images sur 45 000**. Les morceaux de 30 s décrits ci-dessous existent toujours, à l'intérieur de chaque tranche.
- **Démarrage des lectures sur le serveur** (plugin 0.7.7 et suivants) : les tests T gardent `LocalFirstSeconds = 0`. Un **faux ffmpeg d'Emby** (`test\local\fake-emby-ffmpeg`) retire les options du fork et écrit les lignes `SegmentComplete` ; il sert aux tests **LF** (`run-tests.ps1 -Only LF`, voir le README du banc).

**Mise à jour du 29/10/2026 (plugin 0.7.10).**

Tests LF, première série : 4 défauts réels, corrigés en 0.7.10.

| Défaut | Correction |
|---|---|
| Avis du coordinateur après plus de 2 s (1re lecture d'un film, ffprobe lent) : la lecture restait entièrement sur le serveur, sans trace | Avis en 1,2 s au plus (sonde mise en cache, poursuivie en arrière-plan) ; un avis en retard fait reconnecter le relais, qui démarre sur le serveur |
| Règle des 4 s : un serveur à 1,5x déclaré trop lent, 0.00x mémorisé 30 jours | Délai de 3 s + un segment à 1,2x (5,5 s) ; seule une vitesse mesurée est retenue (segments, ou progression de ffmpeg), pendant 7 jours |
| Serveur bloqué : 10 s d'attente d'un segment qui ne viendra pas | Sans aucun segment produit, arrêt immédiat |
| Préchargement arrivé après la bascule | Sonde réutilisée, pas de recherche d'image clé depuis 0, pas d'indice si un worker a déjà la lecture |

Résultats après correction : **LFa, LFb, LFb2, LFb3, LFb4, LFc, LFd, LFd2, LFe, LFf0, LFf : 11 PASS**. Serveur à 0,8x : vitesse 0,70x retenue, la 2e lecture part directement sur le worker ; serveur bloqué : worker 6,7 s après le lancement (15 à 19 s avant) ; sonde du coordinateur de 5,6 s : la lecture passe quand même au worker.

Conversions : un dernier morceau de 0,12 s (3 images) perdu sur le NAS en 0.7.4 a conduit à deux garde-fous : pas de morceau final de moins de 2 s, et vérification de la durée vidéo du fichier assemblé à 1,5 image près. **T01, T02, T09a, T09b : PASS** ; sur les 5 conversions, vidéo source et résultat identiques (0,000–150,000 s), aucun rejet à tort. La capture des journaux du banc est en UTF-8 (les accents des messages du relais faisaient échouer à tort des recherches).

Mise en veille simulée (T13, `tests-sleep.ps1`, `run-tests.ps1 -Only T13`) : le worker et ses ffmpeg sont gelés 40 s (NtSuspendProcess), puis dégelés. **T13a, lecture** : perte constatée en 16 s, suite sur l'autre worker au segment exact, aucun doublon ; au réveil, le ffmpeg périmé s'arrête et le worker redevient disponible. FAIL partiel pour la seule raison D1 (−80 ms au raccord, comme T08). **T13b, conversion** : PASS, perte constatée par le battement de cœur, tranche reprise ailleurs, fichier complet, vidéo identique à la source à l'image près ; au réveil, aucun ffmpeg restant. Une vraie veille coupe aussi le réseau ; ce cas (coupure Wi-Fi) avait été vérifié sur le serveur réel le 28/10.

## 3. Détails par test

**Méthode générale.** Chaque test lance `remote-ffmpeg.exe` avec une commande au format Emby. Le stderr du relais est
journalisé (`work\logs\shim-*.log`), ainsi que le coordinateur (`coordinator.log`) et chaque worker
(`worker-*.log`). L'état des workers est lu sur `/status`, les sorties sont vérifiées avec ffprobe (flux, durée) et
décodées entièrement (`ffmpeg -v error -f null`). Pour les lectures, deux mesures de continuité sont faites :
1. sur les lignes `SegmentComplete` : indices contigus, aucun doublon ;
2. sur les fichiers `.ts` eux-mêmes : écart entre le premier PTS vidéo du segment repris et la fin du segment précédent.

**T01 / T02.** `New-ConversionArgs` produit `-y -copyts -start_at_zero -f matroska,webm -c:v:0 h264 -noautorotate -i
/emby/.../film_h264.mkv -map 0:0 -map 0:1 -sn -c:v:0 libx264 -vf scale=… -crf:v:0 23 … -f mp4 <sortie>`.
Extrait du relais : `remote-transcode: conversion … confiée aux workers`, puis
`vidéo encodée par les workers, assemblage du fichier demandé par Emby`. Sur `/status`, l'encodage est en
`Completed` avec la note « assemblé et vérifié par le serveur ». Pour T02, les quatre relais ont été lancés à 0,7 s
d'intervalle. Le score (`WorkerMatching.Score`, emplacements libres) alterne bien w1/w2.

**T03.** w1 et w2 sont arrêtés et apparaissent Offline.
- a) Commande réelle d'Emby : `not offloaded (no worker connected), using local ffmpeg`, puis le ffmpeg amont
  s'arrête sur `Invalid loglevel "timing"` (limite L1).
- b) La même commande sans `-loglevel +timing`, `-print_graphs_file` ni `-segment_write_temp` : le ffmpeg local
  écrit les segments, et `q` le termine avec le code 0.
- c) Conversion : `conversion sur le serveur (aucun worker disponible pour cette conversion)`. La commande d'Emby
  s'exécute en local et la sortie est valide (150,02 s).

**T04.** w1 tourne en mode barre des tâches avec son propre `EMBY_WORKER_HOME`. `EmbyRemoteWorker.exe release`
renvoie 0, le worker passe en Unavailable « libéré par l'utilisateur », et `Available=false` est enregistré.
Extrait du relais : `not offloaded (no available worker: w1: Unavailable (désactivé par l'utilisateur))`. La
conversion est refusée de la même façon. Le refus a lieu dans le coordinateur (`PickWorker`) : aucune offre n'est
envoyée au worker, donc le contrôle côté worker (`CheckOffer`) n'intervient pas ici.

**T05.**
- a) La lecture démarre sur w1 (1080p, preset medium), puis w2 est démarré. `release` sur w1 : le ffmpeg du job
  s'arrête en moins de 1,8 s. Extraits du relais :
  `resuming at segment 16 (47.995s)` puis `job ca23784f… continues on worker w2`.
  Les segments 0 à 19 sont contigus et sans doublon.
- b) La plage horaire de w1 se termine 25 s après le démarrage, avec `Reclaim=FinishPlayback`. w1 passe en
  `Draining (hors plage horaire)`. La lecture continue sur le même job (26 puis 50 segments, aucune reprise) et une
  nouvelle lecture est refusée : `no available worker: w1: Draining (hors plage horaire)`. Après `q`, w1 est
  Unavailable.
- c) Même scénario avec `Reclaim=ReleaseImmediately`. Journal de w1 :
  `PC repris (hors plage horaire) : libération immédiate de 1 tâche(s)`. Reprise sur w2 au segment 30 (89,995 s).
- Pour a et c, le verdict FAIL tient uniquement à la jonction des fichiers : `fin 15=58,080s, début 16=58,000s,
  écart -80 ms` (voir D1). Dans `SegmentComplete`, le segment repris affiche `Start=0.000000` (voir D2). Le critère
  « Start du segment repris = End du précédent à 10 ms près » n'est donc pas vérifiable sur ces lignes. Il a été
  vérifié sur les fichiers, où il échoue de 80 ms.

**T06 / T07.**
- Premier segment 1,9 s après le lancement du relais. Après `q`, le relais affiche `[q] command received. Exiting.`,
  se termine avec le code 0 en 0,7 s, et le worker journalise `job …: ended (exit 0), 7 segments uploaded`.
- Le ffmpeg du job (repéré par `/jobs/<id>/` dans sa ligne de commande) disparaît en 0,9 s.
- Déplacement : nouveau relais dans le même dossier de session avec `-ss 00:01:00.000 -segment_start_number 20`. Le
  premier segment a l'index 20, `End=63.000` ; le segment 21 couvre [73,000 ; 76,000] dans le fichier (décalage
  mpegts constant de +10 s).

**T08.** Seul `EmbyRemoteWorker.exe` de w2 est tué, par PID (`Process.Kill`), pas son arbre de processus.
- Son ffmpeg disparaît aussitôt (Job Object), sans orphelin.
- Le coordinateur journalise `Worker w2 dropped: connection closed`.
- Le relais journalise `continues on worker w1` 0,1 s après.
- Au redémarrage, w2 est Available en 4,9 s et journalise `dossier de tâche abandonné supprimé : d5952413…`.
- FAIL partiel pour la même jonction de -80 ms (D1).

**T09.** Le fichier de sortie n'existe pas avant l'assemblage. Les morceaux de 30 s sont repris à partir du premier
morceau manquant, sans doublon ni troncature : durée 150,02 s identique à la source, décodage complet sans erreur.
- a) Coordinateur : `Job e0fad559… lost: worker w1 lost: connection closed`, puis reprise sur w2.
- b) Sans aucun worker : `NAS (local) : aucun worker : encodage sur le serveur à partir du morceau 1`. Quand w1
  revient : `a worker is available, stopping the local encode to hand over`.

**T10.** Les mesures sont objectives :
- incrustation : PSNR du quart inférieur de l'image contre une lecture identique sans sous-titres ;
- piste audio : fréquence du sinus, calculée à partir du taux de passage par zéro d'`astats` ;
- tone mapping : PSNR contre `zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable,zscale=t=bt709…`
  appliqué directement à la source.

**T12.** Contrôles faits avec `curl -k`. La requête de mise à niveau WebSocket est vérifiée avant le `101`. Chaque
cas d'échec utilise un `X-Real-IP` différent pour ne pas mélanger les compteurs de limitation.

| Contrôle | Obtenu (attendu) |
|---|---|
| `/ws/worker` avec jeton invalide, puis sans jeton | 401, 401 |
| Révocation de w3 par `POST /admin/workers/{id}/revoke` | 200. w3 est déconnecté puis passe en Revoked ; sa reconnexion reçoit 401 (`Refused worker connection from 127.0.0.1` ; w3 : `status code '401' when status code '101' was expected`) ; son jeton reçoit 401 |
| `/jobs/{id}/files/0` : mauvais jeton, sans jeton, job inexistant | 401, 401, 401 |
| `/jobs/{id}/files/0` avec le bon jeton (plage d'octets) | 206 |
| Fichier qui n'est pas dans le plan | 404 |
| PUT de segment avec un mauvais jeton | 410 |
| Bon jeton une fois le job terminé | 401 |
| Entrée hors racines, chemin avec `..`, sous-titres hors racines, conversion hors racines | Jamais confiés à un worker : `input outside allowed roots`, `filename outside allowed roots`, `source hors des bibliothèques autorisées` |
| `/ws/shim` sans secret, puis avec un mauvais secret | 401, 401 |
| Relais sans fichier secret | `secret unavailable …, using local ffmpeg` |
| `/status` sur le port workers ; `/ws/worker` sur le port local | 404, 404 |
| `/admin/status` sans secret, puis avec le secret admin | 401, 200 |
| `POST /admin/workers/pair` sans secret | 401 |
| Limitation des échecs | Tentatives 1 à 10 : 401 ; 11e : 429. Le même client reste bloqué sur toutes les routes worker (429), un autre client ne l'est pas (401). w1 reste connecté |

## 4. Défauts et constats

**D1. Recul de 80 ms des PTS à la jonction d'une reprise (T05a, T05c, T08).** Lorsqu'une lecture démarrée au
début (sans `-ss`) est reprise ailleurs, le segment repris recommence 80 ms avant la fin du précédent. Exemple :
fin du segment 15 = 58,080 s, début du segment 16 = 58,000 s.
- Le contenu lui-même est continu : aucune image n'est perdue ni dupliquée en temps source. Seule la base de temps
  diffère de 2 images.
- Reproduit avec le ffmpeg 9.0.2 seul, avec `-copyts -start_at_zero` et libx264 avec B-frames :
  - run sans `-ss` (ou avec `-ss 0`) : PTS = temps source + 10,08 s ;
  - run avec `-ss` supérieur à 0 : PTS = temps source + 10,00 s ;
  - deux runs avec `-ss` supérieur à 0 se raccordent à 0 ms.
- La reprise est construite par `ResumePoint` / `ResumeArgs.Build`
  (`src\RemoteTranscode.Shim\Program.cs:662-667` et `:846`). Elle suppose une base de temps identique dans les deux
  runs, ce qui est faux quand le premier run a démarré sans `-ss`.
- Impact sur hls.js non mesuré ici. La note de conception « Seamless hand-over » indique que de petits
  recouvrements peuvent bloquer le lecteur. **À vérifier en navigateur** avant de corriger : par exemple, forcer une
  base de temps commune ou décaler le point de reprise du délai de réordonnancement.
- Le même écart est probable entre un worker et le ffmpeg d'Emby, à vérifier sur le NAS.

**D2. `Start=0` dans `SegmentComplete` pour le premier segment de tout run lancé avec `-ss` (déplacement ou
reprise).**
- Cause : le muxer segment amont écrit `start=0` dans sa liste CSV, ce que j'ai reproduit. Le worker transmet cette
  valeur telle quelle (`src\RemoteTranscode.Worker\JobRunner.cs:358`).
- Conséquence : le relais écrit `#EXTINF:63.000000` et `#EXT-X-TARGETDURATION:63` dans la liste `-segment_list`
  (`src\RemoteTranscode.Shim\Program.cs:593`, durée calculée comme `End - Start`), et la ligne affiche `Duration=63`.
- `End` est juste, et la logique de reprise ne s'appuie que sur lui. D'après les notes du projet, le fork d'Emby
  peut lui aussi afficher `Start=0`, d'où une gravité faible.
- Correction simple possible (non faite, je préfère la signaler) : quand `Start` vaut 0 sur un index supérieur au
  premier, prendre `lastCompleteEnd` ou la durée nominale.

**D3. Les conversions avec sous-titres incrustés restent sur le serveur** (« filter subtitles filename must use a
placeholder »). `EmbyConversionPlanner` (`src\RemoteTranscode.Common\EmbyConversionPlanner.cs:160`) ne remplace pas
les fichiers des filtres par des fichiers auxiliaires comme le fait `EmbyJobPlanner`. Le comportement est sûr, il
s'agit d'une limite fonctionnelle.

**D4. Constats mineurs.**
- `ServerFiles.Canonical` ne résout pas les liens symboliques sous Windows (`ServerFiles.cs:22`). Cela ne concerne
  que le développement : Linux utilise `realpath`.
- La clé de limitation se fonde sur `X-Real-IP` pour toute connexion en boucle locale (`CoordinatorHost.cs:341`).
  C'est voulu pour le reverse proxy, mais un processus local peut ainsi choisir la clé.
- `/status` vide le champ `Worker` d'un encodage terminé (affichage seulement).

## 5. Limites du banc

- **L1. Repli local d'une lecture avec la vraie commande d'Emby.** Les options propres au fork (`-loglevel +timing`,
  `-segment_write_temp`) sont refusées par le ffmpeg amont : `Invalid loglevel "timing"` et `Unrecognized option
  'segment_write_temp'`. Ce repli échoue donc ici alors qu'il fonctionne sur le NAS. Sans ces options, le ffmpeg
  amont produit bien les segments, mais n'émet pas de lignes `SegmentComplete`. Les cas suivants restent donc à
  valider sur le NAS :
  - la reprise worker vers serveur au segment exact ;
  - le retour du serveur vers un worker pendant une lecture.
  Le chemin distant (traduction par `EmbyJobPlanner` et `WorkerCommandBuilder`) a, lui, été exercé avec les vraies
  commandes.
- Les conversions sont envoyées sans options du fork pour que l'assemblage et le repli s'exécutent avec le ffmpeg
  amont.
- Tous les processus tournent sur un seul PC, en boucle locale : pas de latence réseau. Les seuils de charge des
  workers de test sont neutralisés (ils partagent le processeur avec le worker de production).
- Direct Play (T11), le comportement d'Emby lui-même (lecture de la liste, envoi de `q` quand un client ne lit plus,
  limitation de débit `t`) et la lecture en navigateur à la jonction ne sont pas testables ici.
- Le décalage mpegts de +10 s est constant et sans effet sur les comparaisons.

## 6. Nettoyage

Tous les processus lancés par le banc (coordinateur, workers de test, relais, ffmpeg) ont été arrêtés par PID.
Seul le worker de production (PID 13308) tourne encore, et `%LOCALAPPDATA%\EmbyRemoteWorker` n'a pas été touché.
Les données de test restent dans `C:\emby\test\local\work\` : médias, sorties, journaux, `results.md` et
`results.jsonl`.
