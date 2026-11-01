# Historique des versions

English version: [CHANGELOG.md](CHANGELOG.md)

Format inspiré de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/). Version la plus récente en haut.

Le plugin Emby et le worker Windows ont des numéros séparés depuis la 0.7.7 ; chaque entrée indique le composant concerné : **plugin** (DLL d'Emby : coordinateur et relais compris), **worker** (`EmbyRemoteWorker.exe`), **installeur** (`EmbyRemoteWorker-Setup-<version>.exe`), **banc** (banc de test local). Les dates sont celles de construction ou d'installation notées dans `REPRISE.txt` et les documents du projet ; quand une version n'est pas datable ou que son contenu exact n'est pas établi, les versions sont regroupées.

## Plugin 0.7.27 — 01/11/2026

Construit et publié par GitHub Actions (release v0.7.27). Worker inchangé (0.7.15).

### Corrigé

- **Plugin** — Une conversion dont la vidéo s'arrête avant la fin du fichier (l'audio continue) ne tourne plus en boucle sur sa dernière tranche. Depuis la 0.7.25, une exécution qui se termine sans erreur de ffmpeg compte comme une interruption : une dernière tranche reprise là où la vidéo s'arrête était relancée toutes les 2 secondes sans jamais produire de morceau (constaté le 01/11 : un épisode 4K Dolby Vision dont la vidéo s'arrête 34 s avant l'audio, une heure de relances sur les deux workers). Désormais la dernière tranche est terminée dès que deux exécutions de suite, reprises après ses morceaux, se terminent normalement sans aucun morceau : la vidéo s'arrête là. Le fichier assemblé garde l'audio de la source, et le contrôle de la conversion compare toujours les durées de la vidéo.
- **Plugin** — Une tranche dont les exécutions se terminent trois fois de suite sans erreur mais sans aucun morceau compte désormais comme un échec (worker écarté, réessayé après 5 minutes, conversion rendue à Emby après 30 minutes) au lieu d'être relancée indéfiniment.

## Plugin 0.7.26 et worker 0.7.15 — 01/11/2026

### Ajouté

- **Worker** — Tone mapping HDR → SDR par le GPU : libplacebo via Vulkan, 2 à 2,4 fois plus rapide que la chaîne processeur avec environ 8 % de processeur au lieu d'environ 50 % (HDR10 4K et 1080p, mesuré sur un iGPU Intel), mêmes images et mêmes horodatages. Validé au démarrage par une sonde qui tone mappe un clip PQ généré avec la chaîne exacte des tâches et la compare à la chaîne processeur (PSNR d'au moins 20 dB ; un code de sortie 0 ne suffit pas : le device Vulkan par défaut d'un iGPU Intel rend des images rose/vert sans aucune erreur). Chaque device Vulkan (le GPU des encodeurs d'abord, puis celui par défaut) est essayé tel quel, puis avec `disable_multiplane=1,linear_images=1`, qui corrige Intel. La méthode et le device validés sont annoncés au coordinateur (`WorkerCapabilities.GpuToneMapping`) ; le cache des capacités est renouvelé.
- **Worker** — Réglage « HDR → SDR (tone mapping) par le GPU quand il a été validé au démarrage » (activé par défaut) ; la fenêtre du worker indique comment le HDR est traité. Après 3 échecs, le tone mapping GPU est suspendu jusqu'au prochain démarrage, comme le décodage par le GPU : une lecture repasse aussitôt sur le processeur (mêmes images), les tranches de conversion tone mappées sur le GPU sont refusées.
- **Plugin** — Le coordinateur choisit le tone mapper de chaque conversion HDR à sa capture et le fige : libplacebo si au moins un worker connecté peut faire la conversion ainsi, zscale sinon. Il traduit lui-même chaque tranche dans ce mode ; les tranches d'une conversion libplacebo ne vont qu'aux workers qui en ont la capacité, jamais au serveur (le ffmpeg d'Emby n'a pas libplacebo) ; sans aucun de ces workers connecté pendant `EncodeNoWorkerMinutes`, la conversion est rendue à Emby. zscale et libplacebo ne sont jamais mélangés dans un même épisode. L'état indique le `ToneMapping` de la conversion.
- **Plugin, worker** — Traduction de `supertonemap` en mode libplacebo : `hwupload`, `libplacebo` (`tonemapping` selon l'algorithme d'Emby, `bt2390` → `bt.2390`, par défaut ; `peak_detect=0`, sans quoi la luminosité changerait d'une tranche à l'autre ; `yuv420p` BT.709 en plage limitée), `hwdownload`, `format`. Le worker nomme son device Vulkan validé (`-init_hw_device … -filter_hw_device vk`) avant les entrées. La traduction zscale (`desat=0`) reste le repli.
- **Worker** — Une lecture est tone mappée par le worker qui la fait, sur son GPU s'il l'a validé. Une lecture qui passe d'un worker à un autre de capacité différente peut changer légèrement de rendu à partir de ce moment.
- **Installeur** — Le worker embarque désormais le build FFmpeg 9.0.2 « full » de gyan.dev (le build « essentials » n'a pas libplacebo) : installeur de 218 Mo au lieu de 170 Mo.

### Sécurité

- **Worker** — Le contrôle des tâches accepte `libplacebo` mais refuse ses options de shaders utilisateur (`custom_shader_path`, `custom_shader_bin`).

### Banc

- Les workers de test utilisent le build FFmpeg « full ». T21 : conversion HDR par w1 seul, tone mappée sur son GPU (libplacebo dans chaque graphe envoyé, device Vulkan sur la ligne de commande, aucune tranche sur le serveur, nombre d'images identique à la source). 215 tests unitaires.

## Plugin 0.7.25 — 01/11/2026

Construit et publié par GitHub Actions (release v0.7.25).

### Corrigé

- **Plugin** — Un worker fermé ou mis à jour en pleine tranche (sa tâche se termine sans erreur de ffmpeg) n'est plus considéré en échec : la tranche reprend ailleurs depuis son dernier morceau et le worker reste accepté pour cette conversion. Les vrais échecs portent toujours les lignes d'erreur de ffmpeg et écartent encore le worker. Constaté le 01/11 : les deux workers ont été mis à jour pendant un épisode 4K HDR, tous deux ont été écartés, et la dernière des 30 tranches attendait indéfiniment (le serveur ne sait pas faire le HDR → SDR, et les workers connectés empêchaient le délai d'abandon de démarrer).
- **Plugin** — Une conversion que le serveur ne sait pas encoder continue d'essayer de placer ses tranches sur les workers pendant 30 minutes (`EncodeNoWorkerMinutes`, 10 auparavant) avant d'être rendue à Emby, y compris quand des workers sont connectés mais qu'aucun ne peut prendre la tranche. Les workers écartés après un vrai échec sont réessayés au bout de 5 minutes (`EncodeSetAsideRetryMinutes`) quand eux seuls pourraient faire le travail.

## Plugin 0.7.24 — 01/11/2026

### Ajouté

- **Plugin** — Signalement automatique et anonyme des échecs : quand une conversion échoue (assemblage rejeté par le contrôle, worker en échec sur une tranche et écarté, conversion rendue à Emby), le plugin ouvre une issue sur le dépôt GitHub du projet avec les labels `auto-report` et `bug`, ou commente celle déjà ouverte avec la même signature (événement + erreur normalisée + codec vidéo). Le rapport contient ce qu'il faut pour corriger — versions (plugin, Emby, système/architecture du serveur, workers par version et modèle de GPU), l'erreur sans aucun chemin, les dernières lignes d'erreur de ffmpeg du worker, les caractéristiques techniques de la source (conteneur, codec, profil, profondeur de couleur, résolution, images/s, start_time vidéo, durée, HDR, pistes audio), l'encodeur et les noms des filtres demandés par Emby, le type de machine de chaque tranche, le résumé `assembled from chunks` et la ligne de contrôle — et rien de personnel : ni chemin, nom de fichier, titre, domaine, adresse IP, nom d'hôte, de PC, de worker ou d'utilisateur, ni identifiant Emby. La source est désignée par un code anonyme, relié au fichier dans le seul journal local d'Emby ; l'installation par un identifiant aléatoire tronqué. Au plus 5 envois par jour et par installation, jamais deux fois pour la même conversion, envoi en arrière-plan avec un délai court (erreurs ignorées). Non signalé : aucun worker capable connecté, annulation par Emby, redémarrage, source supprimée entre-temps.
- **Plugin** — Page du plugin : « Signaler automatiquement les échecs de conversion sur GitHub (anonyme) » (activé par défaut) avec son état (« actif », « désactivé », « inactif : pas de jeton dans cette version »).
- **Compilation** — Le jeton GitHub n'est jamais dans le dépôt : GitHub Actions passe le secret `ISSUE_REPORT_TOKEN` à `tools/build-plugin.ps1`, qui l'inscrit dans les métadonnées de la DLL. Une compilation locale ou un fork n'a pas de jeton et ne signale jamais rien.
- **Plugin** — Le verdict du relais sur un assemblage transmet maintenant sa ligne de contrôle (étendue vidéo de la source et du résultat), pour les rapports.

## Plugin 0.7.23 et worker 0.7.14 — 01/11/2026

Construits et publiés par GitHub Actions (release v0.7.23).

### Corrigé

- **Plugin, worker** — Les conversions HDR → SDR délavaient les hautes lumières jusqu'au blanc : le filtre standard `tonemap` qui remplace le `supertonemap` d'Emby désature les hautes lumières par défaut (`desat=2`). La traduction fixe maintenant `desat=0` sauf si Emby demande une valeur (constaté sur une mire HDR10 comparée à libplacebo ; moins marqué sur un vrai film, mais réel).
- **Plugin** — Toutes les tranches d'une conversion reçoivent maintenant le même graphe de filtres, quelle que soit la machine qui les encode : le coordinateur traduit les filtres propres à Emby avant d'envoyer une tranche, au lieu de laisser chaque worker le faire (un worker plus ancien aurait gardé le rendu délavé pour ses tranches).

## Plugin 0.7.22 — 01/11/2026

Construit et publié par GitHub Actions (release v0.7.22).

### Corrigé

- **Plugin** — Image en double à la reprise d'une tranche, sur les sources dont la vidéo ne commence pas à 0 (Mr. Robot S03E09 : vidéo à 0,007 s, assemblée trop longue de 12 puis 14 images et rendue à Emby). ffmpeg liste les heures des morceaux sur sa propre grille d'images (multiples de 1/23,976 s depuis 0), à quelques millisecondes des vraies heures des images de la source ; une tranche reprise (passage du serveur à un worker, worker perdu, run arrêté pile à sa fin) repartait de cette heure listée et son `-t` dépassait la fin de la tranche, réencodant la première image de la tranche suivante. Une tranche reprise repart maintenant de l'image de la source la plus proche de la fin listée (lue par ffprobe, à 0,05 s au plus), et un run arrêté à la fin de sa tranche marque la tranche terminée au lieu d'encoder une image de plus.
- **Plugin** — Durées de la liste d'assemblage : chaque morceau dure jusqu'au début du suivant, et le dernier morceau d'une tranche s'arrête à la fin de la tranche quand sa fin listée en est à 0,02 s au plus ; chaque tranche dure donc exactement sa longueur, quelle que soit la base de temps des listes ou le chevauchement des petits morceaux du serveur (un écart plus grand n'est pas étiré : des images manquantes restent visibles au contrôle). La ligne de journal « assembled from chunks » donne maintenant `tranche:nombre@fin/longueur listée`, et le `chunks.csv` gardé d'un assemblage rejeté a une colonne `concat_duration`.

### Banc

- T20 : source AV1 10 bits dont la vidéo commence à 7 ms, encodée par un worker (logiciel, puis GPU) avec le serveur, avec un passage serveur → worker : 11509/11509 images, aucune en double. 175 tests unitaires.

## Worker 0.7.13 — 01/11/2026

### Corrigé

- **Installeur** — Une mise à jour (ou une désinstallation) ferme aussi les `ffmpeg.exe` / `ffprobe.exe` fournis avec le worker qui tournent encore depuis le dossier d'installation après la fermeture du worker : un encodage qui survivait à son worker gardait `ffmpeg.exe` verrouillé et la mise à jour s'arrêtait. Seuls les programmes lancés depuis ce dossier, dans la session de l'utilisateur, sont fermés.

## Plugin 0.7.21 — 01/11/2026

Installé sur le serveur de test le 01/11/2026 à 12:32.

### Ajouté

- **Plugin** — La page du plugin affiche la conversion en cours dans un encadré au-dessus de l'historique : épisode, barre de progression, pourcentage, vitesse, temps restant, tranches faites, et quelles machines travaillent sur quelle tranche.
- **Plugin** — Une conversion dont l'assemblage est rejeté par le contrôle garde ses morceaux, sa liste d'assemblage et une table des morceaux (`chunks.csv` : tranche, bornes, morceau, début, fin) dans son dossier de travail, et le journal indique où : seule la plus récente est gardée. Jusqu'ici tout était effacé, ce qui rendait une durée fausse (ex. +12 images sur un épisode AV1 encodé par un worker et le serveur) impossible à analyser.

### Corrigé

- **Plugin** — Une conversion pouvait finir « annulée » alors que son fichier était assemblé et vérifié : pendant l'assemblage, le relais ne lit pas sa connexion pendant plusieurs minutes, elle peut tomber, et le coordinateur prenait cela pour un arrêt par Emby avant que le verdict du relais n'arrive sur une nouvelle connexion. Pendant l'assemblage, une connexion fermée attend maintenant le verdict jusqu'à 2 minutes.

### Problèmes connus

- Un épisode AV1 (Mr. Robot S03E09) encodé par un worker et le serveur ensemble est sorti trop long de 12 à 14 images et a été rendu à Emby ; en cours d'analyse.

## Plugin 0.7.20 et worker 0.7.12 — 01/11/2026

Plugin installé sur le serveur de test le 01/11/2026 à 12:08.

### Corrigé

- **Plugin** — Conversions encodées par le serveur : vidéo assemblée trop courte de 3 à 4 images, donc rejetée et refaite par Emby. Le ffmpeg d'Emby, avec `-start_at_zero`, liste les heures des morceaux décalées du début de la vidéo source (5 ms sur Ted Lasso S03E09, 7 ms sur Mr. Robot S03E04) ; chaque tranche perdait ce décalage dans les durées de la liste d'assemblage (26 tranches × 5 ms = 3 images). Une tranche terminée, dont la fin est connue à l'image près, a maintenant ses heures recalées sur cette fin ; un écart de plus d'une demi-image n'est pas corrigé (ce seraient des images manquantes, que le contrôle doit voir). Constaté le 01/11 : toutes les conversions faites par le NAS seul (Ted Lasso, Mr. Robot) étaient rendues à Emby.
- **Plugin** — Le serveur qui encode seul n'attend plus 20 s entre deux tranches quand aucun worker capable n'est connecté (environ 20 % de temps perdu).

### Ajouté

- **Worker** — Décodage vidéo par le GPU quand l'encodage s'y fait : NVDEC (`-hwaccel cuda`) avec NVENC, D3D11VA avec QSV (Intel) ou AMF (AMD), validé au démarrage du worker pour chaque codec ; images ramenées en mémoire pour les filtres d'Emby. Repli automatique sur le décodage logiciel si le GPU ne lit pas le flux (et relance immédiate sans GPU si ffmpeg échoue avant le premier morceau). Case « Décodage vidéo par le GPU quand l'encodage s'y fait » dans les paramètres (activée par défaut). Mesuré sur PC-A (Intel) : images décodées identiques au décodage logiciel, segment identique à l'octet ; 4K HDR → SDR 0,37x → 0,40x avec 25 % de processeur en moins, 4K → 1080p 0,80x → 0,91x (−38 %). Le goulot des conversions HDR reste la conversion HDR → SDR (`zscale` + `tonemap`) sur le processeur.
- **Worker** — Icône (version 0.7.11, jamais installée, incluse ici).

### Banc

- T19 : conversion faite entièrement par le serveur, sur plusieurs tranches.

## Plugin 0.7.19 — 31/10/2026

Installé sur le serveur de test le 31/10/2026 à 22:18 (banc complet PASS, 12 verdicts ; preuve de synchro de l'audio converti à l'avance : 0 paquet audio différent, MD5 identiques). Constaté aussitôt : Narcos S02E02 (4K HDR) découpé en 34 tranches et confié à PC-B.

### Corrigé

- **Plugin** — Une conversion n'est plus rendue à Emby parce que les workers ne sont pas encore reconnectés : si au moins un worker est associé (même déconnecté sur le moment), elle est acceptée et attend un worker capable jusqu'à `EncodeNoWorkerMinutes` (10 min). Constaté le 31/10 à 21:36 : Emby a relancé Narcos S02E02 8 s après son redémarrage, avant le retour des workers ; le serveur ne sachant pas faire seul la traduction HDR → SDR (pas de `zscale`), l'épisode est reparti entier sur le NAS à 0,36x.
- **Plugin** — Image en double à une frontière de tranche : quand le serveur passait la main à un worker juste à la fin de sa tranche, le worker relançait la tranche pour quelques millisecondes et encodait la première image de la tranche suivante (vidéo en retard d'une image, 42 ms, sur le son pour la suite de l'épisode ; le contrôle à l'image près, à 1,5 image de tolérance, la laissait passer). Une tranche dont les morceaux atteignent déjà la fin est maintenant marquée terminée. Défaut antérieur à la 0.7.18, trouvé au banc (T16b).

## Plugin 0.7.18 et worker 0.7.11 — 31/10/2026

Plugin installé sur le serveur de test le 31/10/2026 à 21:36.

### Corrigé

- **Plugin** — Une borne de tranche illisible ne met plus tout l'épisode en une seule tranche : elle est sautée et ses deux tranches voisines n'en font qu'une ; la tranche unique ne reste que si la plupart des bornes sont illisibles. Constaté sur Narcos S02E02 (4K HDR, image à 3831,5 s illisible, 64 min en une seule tranche sur PC-B à 0,6x).

### Modifié

- **Plugin** — Le choix workers / serveur n'est plus figé au début d'une conversion. Une conversion est acceptée dès qu'un worker capable est connecté **ou** que le serveur sait encoder ses tranches ; le serveur commence alors seul et, à chaque passage du planificateur (toutes les 10 s), les tranches en attente sont de nouveau proposées aux workers : un PC allumé en cours d'épisode y participe aussitôt. Constaté le 30–31/10 : PC-B éteint la nuit, toutes les conversions démarrées sans worker étaient laissées en entier au NAS (« aucun worker connecté capable de cette conversion »), même après le retour du PC. Seul cas rendu à Emby : le serveur ne sait pas encoder les tranches et aucun worker capable n'est connecté pendant `EncodeNoWorkerMinutes` (10 min).
- **Plugin** — Conversions HDR → SDR (Emby demande son filtre `supertonemap`, absent du FFmpeg standard) : le serveur applique désormais aux tranches qu'il encode **la même traduction que les workers** (`zscale` + `tonemap`), pour que toutes les tranches d'un film aient le même rendu ; s'il ne dispose pas de ces filtres, il laisse ces tranches aux workers. Avant, il gardait `supertonemap` : rendu possiblement différent d'une tranche à l'autre quand le serveur encodait en parallèle des workers.

### Ajouté

- **Worker, installeur** — Icône du worker (disque vert, triangle « lecture » en tranches) pour l'exécutable (Explorateur, barre des tâches, raccourcis, applications installées), la fenêtre, la zone de notification (même dessin, couleur selon l'état) et l'installeur (fichier et bandeau en haut de ses fenêtres). Dessinée par `tools\make-worker-icon.py`.
- **Plugin** — Conversion de l'audio pendant l'encodage de la vidéo (`EncodeAudioAhead`), pour raccourcir l'assemblage (~3 min 30 par épisode sur le NAS) : présente mais **désactivée par défaut**, en attente de la validation de la synchro audio/vidéo.

### Banc

- T17 : conversion lancée sans aucun worker connecté, acceptée et commencée par le serveur, puis un worker démarré en cours de route prend des tranches. T18 : graphe d'Emby avec `supertonemap` encodé par le serveur (FFmpeg standard du banc) grâce à la traduction, seul puis avec un worker.

## Plugin 0.7.17 et worker 0.7.10 — 30/10/2026

Plugin installé sur le serveur de test le 30/10/2026 à 17:27 (compteur constaté : 150 éléments) ; worker 0.7.10 installé sur PC-A à 16:36.

### Modifié

- **Plugin, worker** — Le nombre d'éléments de la file de conversion d'Emby est envoyé à **tous les workers connectés toutes les 30 s**, qu'ils aient une tâche ou non. Avant, il n'arrivait qu'avec l'avancement d'une tranche encodée par le worker : un worker indisponible (CPU occupé par d'autres applications) affichait « — ». Les workers 0.7.8 et 0.7.9 ignorent ces envois.

### Ajouté

- **Plugin** — La page du plugin affiche aussi la file de conversion d'Emby (section Conversions).

### Limite connue

- La page du plugin ne s'ouvre pas dans l'appli Android d'Emby (« veuillez vous connecter directement à votre serveur local ») : les applis n'exécutent pas le JavaScript des plugins tiers. Elle fonctionne dans un navigateur, y compris sur mobile. Une page compatible avec les applis demanderait l'interface déclarative d'Emby (IHasUIPages).

## Plugin 0.7.16 — 30/10/2026

Installé sur le serveur de test le 30/10/2026 à 16:17.

### Corrigé

- **Plugin** — Compteur de la file de conversion d'Emby : la 0.7.15 cherchait le gestionnaire de synchronisation dans le conteneur d'Emby, où Emby 4.10 ne l'enregistre pas (« SyncManager is not registered » dans le journal, compteur « — » sur les workers). La DLL prend maintenant l'instance qu'Emby utilise lui-même (`SyncManager.Current`) ; si elle n'existe pas encore (Emby en cours de démarrage), elle redemande plus tard au lieu d'abandonner.

## Plugin 0.7.15 et worker 0.7.9 — 30/10/2026

Plugin installé sur le serveur de test le 30/10/2026 à 16:03 ; worker 0.7.9 installé sur PC-A à 15:57 ; installeur déposé dans `un dossier partagé` pour PC-C. Constaté sur le serveur réel (Grey's Anatomy S05E13) : chaque tranche de PC-A démarre 4 à 7 s avant la fin de la précédente, et le serveur encode ses propres tranches (3, 7…) en même temps.

### Modifié

- **Plugin** — Le serveur encode aussi des tranches **en parallèle des workers** (`EncodeLocalAlongsideWorkers`, activé par défaut) : un seul ffmpeg à la fois sur le serveur, jamais une tranche réservée à un worker (déjà préchargée), et pas une tranche de fin de conversion qu'un worker occupé finirait nettement avant (la vitesse du serveur est maintenant mesurée comme celle des workers). Le serveur ne passe sa tranche à un worker libre qu'une fois qu'il ne reste plus rien en attente. Avant, il n'encodait que si aucun worker ne travaillait sur la conversion.
- **Plugin** — **Tranche suivante démarrée en avance** (`EncodeOverlapSeconds`, 6 s) : quelques secondes avant la fin de sa tranche (d'après sa vitesse mesurée), le worker reçoit sa tranche suivante et la lance aussitôt ; il compte alors brièvement deux tâches. Le démarrage de ffmpeg (ouverture de la source, positionnement, initialisation de l'encodeur : environ 4 s sur 26 s par tranche avec QSV, mesuré sur PC-A) ne laisse plus l'encodeur inactif. Fonctionne avec tous les workers qui acceptent 2 tâches simultanées, quelle que soit leur version.

### Ajouté

- **Plugin, worker** — La fenêtre du worker affiche le **nombre d'éléments restant dans la file de conversion d'Emby** (en attente ou en cours, toutes tâches de conversion confondues). La DLL le lit dans Emby lui-même (gestionnaire de synchronisation, au plus toutes les 30 s), sans clé API ni programme externe ; si une version d'Emby ne le permet plus, le compteur affiche « — ». Nécessite le worker 0.7.9 ; les workers 0.7.7 et 0.7.8 ignorent cette information.

### Banc

- Test T15 : un seul worker ; le serveur encode une tranche en parallèle et le worker a deux tranches simultanées ; contrôle à l'image près réussi. T14, T09a et T09b PASS ; 134 tests unitaires OK.

## Plugin 0.7.14 et worker 0.7.8 — 30/10/2026

Plugin installé sur le serveur de test le 30/10/2026 à 12:49 ; worker 0.7.8 installé sur PC-A à 12:55 ; installeur déposé dans `un dossier partagé` pour PC-C.

### Ajouté

- **Worker** — La liste des tâches montre l'avancement de chaque conversion : colonnes **Fichier** (titre de l'épisode), **Tranche** (« 7/24 »), **Position** (début et fin de la tranche dans l'épisode), **Tranche %**, **Total** (avancement de l'épisode entier, toutes machines confondues, avec le temps restant estimé), **Vitesse** et **Durée**, avec des infobulles.
- **Plugin** — L'offre de tâche transmet la tranche (titre, numéro, nombre, début, fin), et le coordinateur envoie l'avancement global de la conversion (`ConversionProgress`) aux workers qui y participent. Ce message n'est envoyé qu'aux workers 0.7.8 ou plus récents : un worker 0.7.7 continue de fonctionner, sans les nouvelles colonnes.

### Banc

- T01, T02, T09a, T09b et T14 PASS ; 134 tests unitaires OK.

## Plugin 0.7.13 — 30/10/2026

Installé sur le serveur de test le 30/10/2026 à 10:57.

### Corrigé

- **Plugin** — Un worker qui échoue sur une tranche est écarté de **cette conversion seulement** ; la tranche est reprise par un autre worker, ou par le serveur s'il n'en reste aucun. Seuls les échecs du serveur lui-même rendent la conversion à Emby (après 3 tentatives). Avant, 3 échecs d'un même worker suffisaient à rendre toute la conversion à Emby, qui la refaisait seul sur le NAS : constaté le 30/10 sur Grey's Anatomy S07E19, quand PC-C a été éteint pendant un encodage NVENC (GPU indisponible pendant l'arrêt de Windows, « Invalid argument » sur 3 tranches).

### Banc

- Test T14 : le ffmpeg d'un worker est arrêté en pleine tranche ; le worker est écarté de la conversion, qui se termine sur l'autre worker, sans être rendue à Emby.

## Plugin 0.7.12 — 30/10/2026

### Corrigé

- **Plugin** — Fichiers AVI sans horodatage d'affichage sur leurs paquets (DivX/Xvid à images B empaquetées) : ils ne sont plus découpés en tranches, car les exécutions qui s'y positionnent ne se rejoignent pas sur la même image. Ils sont convertis en une seule tranche. Constaté sur Monk S07E06 : 22 tranches donnaient 34 images de trop ; en une tranche, le résultat est identique à la conversion d'Emby (57 072 images).
- **Plugin** — Contrôle à l'image près : le décodage tient compte des paquets sans horodatage d'affichage (horodatage de décodage à la place). Pour ces sources, dont la dernière image n'a pas d'horodatage, la fin n'est connue qu'à deux images près : tolérance de 3 images au lieu de 1,5.
- **Plugin** — Contrôle à l'image près : une fin de vidéo illisible n'est plus prise pour un succès. La mesure est refaite sur les 10 dernières minutes ; si elle échoue encore, le fichier est rejeté et Emby refait la conversion. Les erreurs de ffprobe sont écrites dans le journal.
- **Plugin** — Une conversion est acceptée dès qu'un worker capable est connecté, même s'il est momentanément occupé. Le serveur encode des tranches en attendant (après 20 s) et rend la main au worker dès qu'il se libère. Avant, la conversion entière restait au NAS (constaté sur un épisode HEVC pendant que PC-A était chargé). Sans aucun worker capable connecté, rien ne change : Emby convertit lui-même.

### Banc

- Test T04 adapté : un worker connecté mais indisponible ne refuse plus la conversion ; le serveur la commence par tranches.

## Plugin 0.7.11 — 29/10/2026

Installé sur le serveur de test le 29/10/2026 à 16:37.

### Corrigé

- **Plugin** — Assemblage des conversions : la liste `concat.txt` donne la durée exacte de chaque morceau (directive `duration`, tirée de la liste de segments du worker).
- **Plugin** — Sans cette durée, le ffmpeg 5.1 d'Emby estimait parfois un morceau MPEG-TS une image trop court ; le morceau suivant chevauchait et la vidéo finissait des images trop tôt. Constaté sur un épisode 4K de 104 morceaux : 10 images manquantes (0,42 s), fichier rejeté par le contrôle à l'image près.
- **Validé** sur le serveur réel le 29/10/2026 : le même épisode (104 morceaux, encodés par PC-A puis par le NAS) s'assemble sans aucun avertissement « Non-monotonous DTS » ; vidéo de 2837,879 s pour 2837,876 s à la source, acceptée par le contrôle.

### Modifié

- **Plugin** — Installation sur Synology sans SSH : tâche DSM générique « Installer plugin RemoteTranscode » (root, désactivée) qui recolle `plugin.part0`..`plugin.part5`, vérifie l'empreinte de `plugin.sha256` et lance `install-plugin.sh` ; journal `install-<date>.log`. Une nouvelle version ne demande plus de modifier la tâche (outil d'exploitation, pas de changement dans la DLL).

## Plugin 0.7.10 — 29/10/2026

Installé sur le serveur de test le 29/10/2026 à 12:05, sans SSH.

### Corrigé

- **Plugin** — Démarrage rapide : l'avis du coordinateur arrive en 1,2 s au plus (sonde ffprobe mise en cache, poursuivie en arrière-plan). Avant, un avis de plus de 2 s laissait toute la lecture sur le serveur.
- **Plugin** — Avis en retard (après 2 s) : le relais se reconnecte et démarre sur le serveur.
- **Plugin** — Délai du 1er segment sur le serveur : 3 s plus un segment à la vitesse minimale (5,5 s) au lieu de 4 s.
- **Plugin** — Seule une vitesse mesurée du serveur est mémorisée (sur les segments, ou lue dans la progression de ffmpeg), pendant 7 jours au lieu de 30, jamais 0.00x.
- **Plugin** — Un serveur qui n'a produit aucun segment est arrêté sans attendre (avant : 10 s d'attente).
- **Plugin** — Préchargement : sonde réutilisée, pas de recherche d'image clé depuis 0, pas d'indice quand un worker a déjà la lecture.
- **Plugin** — Conversions : plus de morceau final de moins de 2 s (`-segment_time` allongé). Origine : 3 images manquantes en fin d'une conversion 0.7.4 (dernier morceau de 0,12 s perdu côté serveur).

### Ajouté

- **Plugin** — Conversions : contrôle de la durée vidéo du fichier assemblé à 1,5 image près ; sinon le fichier est rejeté et la conversion refaite par Emby.
- **Plugin** — Journal de l'assemblage (`assembled from chunks`).
- **Banc** — Faux ffmpeg d'Emby (`test\local\fake-emby-ffmpeg`) et tests LF du démarrage rapide (`run-tests.ps1 -Only LF`, 11 réussis).
- **Banc** — Test T13 : mise en veille simulée d'un worker (processus gelés 40 s), en lecture et en conversion.
- **Banc** — Capture des journaux en UTF-8.

## Worker et installeur 0.7.7 — 29/10/2026

### Ajouté

- **Worker** — Cache local des sources (`InputCache`) : blocs de 4 Mo, au plus 4 Go et un quart de l'espace libre, vidé au démarrage.
- **Worker** — Désinstallation par `EmbyRemoteWorker.exe --uninstall` (`--quiet` sans fenêtre), limitée aux fichiers du manifeste.

### Modifié

- **Worker** — Toutes les entrées de ffmpeg passent par le cache, ce qui permet le préchargement et évite de relire l'en-tête et l'index à chaque reprise.

## Plugin 0.7.7 – 0.7.9 — 28–29/10/2026

Versions construites mais pas installées sur le serveur de test ; la 0.7.10 les remplace. Dates exactes non établies.

### Ajouté

- **Plugin** — Démarrage des lectures sur le serveur (0.7.7) : premier segment par le ffmpeg d'Emby, puis bascule sur un worker au segment exact après environ 20 s d'avance (`LocalFirstSeconds`), ou dès que le serveur s'avère trop lent (moins de 1,2x).
- **Plugin** — Décision au clic : directement sur un worker pour le tone mapping HDR, une source de plus de 1088 lignes, ou un film pour lequel le serveur a déjà été mesuré trop lent.
- **Plugin** — Préchargement pendant le démarrage sur le serveur : le worker qui prendra la suite charge l'en-tête, la fin du fichier et les données jusqu'après le point de bascule (autorisation de 3 minutes limitée à ce fichier).

### Corrigé

- **Plugin** — D2 (0.7.9) : la durée `EXTINF` d'un segment se calcule depuis la fin du segment précédent, et non depuis le `Start=0` du premier segment après une reprise.

## Worker et installeur 0.7.5 – 0.7.6 — 28/10/2026

Contenu propre à la 0.7.6 non établi.

### Modifié

- **Installeur** — Ne se recopie plus en `Uninstall.exe` ; un `Uninstall.exe` laissé par une version antérieure est supprimé à la mise à jour.
- **Installeur** — Ferme le worker proprement (événement « -exit », comme « Quitter ») et ne force l'arrêt qu'après 20 s ou pour une version qui ne connaît pas ce signal.

### Corrigé

- **Installeur** — L'installeur 0.7.4 avait été détecté par Kaspersky (« PDM:Trojan.Win32.Generic », détection comportementale). Ne pas utiliser la 0.7.4.

## Plugin et worker 0.7.1 – 0.7.4 — 28/10/2026

Versions successives d'une même journée ; la version exacte de chaque changement n'est pas établie par les sources.

### Ajouté

- **Plugin** — Conversions en **tranches** d'environ 2 minutes (puis 60, 60, 30, 30 s en fin), bornes posées sur des images lues par ffprobe, encodées en parallèle par les workers ; une tranche par worker, la suivante réservée.
- **Plugin** — Fin de conversion selon la vitesse mesurée des workers ; repli sur le serveur après 20 s sans worker libre, puis retour sur un worker.
- **Plugin** — Lectures prioritaires : une conversion est déplacée d'un worker sur lequel une lecture n'avance pas assez vite.
- **Plugin** — Un worker dont ffmpeg échoue pour une lecture en est écarté pendant 10 minutes.
- **Worker** — Encodage GPU en option, séparé pour les conversions et les lectures, désactivé par défaut (QVBR pour Intel QSV, `-cq` pour NVIDIA NVENC). Une correction du profil main10 en lecture GPU est notée, sans version établie.

### Corrigé

- **Plugin** — 0.7.3 : fichier dont la vidéo commence après l'audio (6 s) ; chaque tranche s'arrête désormais sur sa borne (21 579 images sur 21 579).

### Problèmes connus

- **Plugin** — 0.7.4 : une conversion GPU a perdu ses 3 dernières images (corrigé en 0.7.10).
- **Installeur** — 0.7.4 : détecté par Kaspersky (corrigé en 0.7.5).

## Plugin et worker 0.7.0 — 28/10/2026

### Modifié

- **Plugin, worker, installeur** — Revue complète du code (environ 55 constats corrigés), 111 tests unitaires.

### Corrigé

- **Worker** — La boucle d'envoi des segments ne s'arrête plus sur une erreur (antivirus, coupure réseau), qui bloquait la lecture.
- **Plugin** — L'arrêt d'un worker n'est plus pris pour la fin de la vidéo ; plus de tâches « ressuscitées » à l'état Running.

### Ajouté

- **Plugin** — Auto-test du relais avant activation ; le ffmpeg personnalisé de l'administrateur est sauvegardé puis restauré.
- **Plugin** — Liste blanche des arguments et des filtres ffmpeg durcie ; secrets en droits 0600.
- **Plugin** — Limitation des tentatives d'authentification des workers : 10 échecs en 10 minutes bloquent l'adresse 15 minutes (`X-Real-IP` lu seulement derrière le proxy local).
- **Installeur** — Désinstallation par manifeste (`install-manifest.txt`).
- **Worker** — Menu Aide → À propos (version, FFmpeg, identifiant), version dans le titre.

## Plugin 0.6 — 27/10/2026

### Ajouté

- **Plugin** — Plugin autonome : la DLL seule suffit. Le coordinateur est hébergé dans le processus d'Emby.
- **Plugin** — Relais embarqué pour linux-x64, linux-arm64, linux-musl-x64 et win-x64, extrait au démarrage, aiguillage généré.
- **Plugin** — Tout est détecté depuis Emby à chaque démarrage : ffmpeg, ffprobe, variables d'environnement, dossier de transcodage, bibliothèques.
- **Plugin** — Désinstallation : retour au ffmpeg d'Emby à chaud, sans redémarrage.

## Plugin et installeur 0.5.0 — 27/10/2026

### Ajouté

- **Plugin** — Premier plugin Emby : page du tableau de bord (interrupteur, workers, tâches, conversions), API réservée aux administrateurs, relayée vers un coordinateur séparé.
- **Plugin** — Interception des lectures **et** des conversions d'Emby, avec ses propres réglages (plus de profils propres au projet). Remux laissé au ffmpeg d'Emby.
- **Plugin** — Traduction du filtre HDR `supertonemap` d'Emby en zscale + tonemap pour les workers.
- **Plugin** — Bascule au segment exact : reprise à la fin exacte du dernier segment livré, retour sur un worker quand il redevient disponible.
- **Installeur** — Premier installeur tout-en-un du worker, sans droits administrateur.

### Corrigé

- **Worker** — Un segment tronqué par l'arrêt d'une tâche n'est plus livré (il laissait un trou dans le tampon du lecteur).

## Avant 0.5.0 — prototype, 26–27/10/2026

Pas de numéro de version.

- Interception d'Emby 4.10 par le réglage « chemin ffmpeg personnalisé » et imitation des lignes `SegmentComplete` (26/10).
- Worker : disponibilité manuelle ou automatique, plages horaires, seuils CPU/GPU/mémoire, icône de zone de notification, paquet autonome avec FFmpeg 9.0.2 (27/10).
- Plusieurs workers : choix par score, reprise sur un autre worker au segment suivant, ffmpeg du worker arrêtés avec lui (Job Object) (27/10).
- File de conversions en arrière-plan en morceaux de 30 s avec reprise au premier morceau manquant (27/10).
