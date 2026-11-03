# Historique des versions

English version: [CHANGELOG.md](CHANGELOG.md)

Format inspiré de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/). Version la plus récente en haut.

Le plugin Emby et le worker Windows ont des numéros séparés depuis la 0.7.7 ; chaque entrée indique le composant concerné : **plugin** (DLL d'Emby : coordinateur et relais compris), **worker** (`EmbyRemoteWorker.exe`), **installeur** (`EmbyRemoteWorker-Setup-<version>.exe`), **banc** (banc de test local). Les dates sont celles de construction ou d'installation notées dans `REPRISE.txt` et les documents du projet ; quand une version n'est pas datable ou que son contenu exact n'est pas établi, les versions sont regroupées.

## Plugin 0.7.38 — 03/11/2026

Construit et publié par GitHub Actions (release v0.7.38). Worker inchangé (0.7.20).

### Corrigé

- **Plugin** — Un vrai AVI avec MP3 était rejeté parce que son AAC s'écartait de 3,3 ms de sa grille à 1079 s, comme dans la conversion d'Emby seul : ffmpeg suit l'horloge de la source avec des paquets plus courts d'un échantillon. Le contrôle cherche maintenant un saut de plus de 3 ms entre deux fenêtres ; une dérive lente par rapport à la source se voit aux emplacements du son et à la fin de la piste.

## Plugin 0.7.37 — 03/11/2026

Construit et publié par GitHub Actions (release v0.7.37). Worker inchangé (0.7.20).

### Corrigé

- **Plugin** — Une conversion en une seule tranche (AVI sans temps de présentation, MPEG-TS) s'arrêtait à 100 morceaux, soit 50 minutes : 3000 s sur un AVI de 3424 s, mesuré sur un vrai fichier. Le contrôle la rejetait et Emby convertissait le fichier. La dernière tranche n'a plus cette limite.

## Plugin 0.7.36 et worker 0.7.20 — 03/11/2026

Construit et publié par GitHub Actions (release v0.7.36). Il faut au moins un worker 0.7.19 pour recevoir des tranches de conversion. Le worker 0.7.20 est le premier capable de se mettre à jour seul : le passage d'un worker plus ancien à la 0.7.20 se fait à la main, une fois.

### Ajouté

- **Plugin** — Contrôle de la synchronisation audio/vidéo du résultat d'une conversion sur tout le fichier, qui rejette en cas de doute (`SyncCheck`). Le relais lit la source et le résultat avec le ffprobe d'Emby, paquets seulement, par fenêtres de 1,5 s : les jonctions des morceaux de l'assemblage (toutes, jusqu'à 1200), au moins les quarts du fichier, le début de chaque piste et la fin. Le contrôle ne décode lui-même que la première image de la source, à partir de ses premiers paquets. Une valeur absente ou illisible rejette le fichier, tout comme un ffprobe encore en cours après 180 s : le résultat est supprimé et la commande d'Emby convertit sur le serveur. Chaque comparaison compte depuis le début du fichier source :
  - Début de la vidéo : la première image du résultat là où la première image de la source sort du décodeur, à 6 ms près.
  - Vidéo dans le fichier : chaque image de la source dans une fenêtre a une image du résultat à 6 ms près quand les deux ont la même fréquence, que la source est sur sa propre grille et démarre sur celle du `-r` d'Emby ; sinon à une demi-image de sortie plus 3 ms près. Deux images distantes de moins d'un quart d'image sont rejetées comme un chevauchement.
  - Fin de la vidéo : à 6 ms près dans les mêmes conditions, sinon une image de sortie plus 3 ms ; pour une source sans temps de présentation, 3 images ou le retard du décodeur plus une image, le plus grand des deux. Le contrôle de la longueur de la vidéo reste : 1,5 image, 3 sans temps de présentation.
  - Audio, chaque piste à part, même nombre de pistes : le premier paquet au début de la piste source moins l'amorce de l'encodeur, à 3 ms près (échantillons : AAC 1024, MP3 1105, AC-3 et E-AC-3 256, Opus 312, MP2 481, Vorbis 128, FLAC, ALAC, DTS et PCM 0). Le premier paquet d'une piste recopiée est comparé au premier paquet de la source, et ses paquets un à un dans les fenêtres, à 3 ms près. Une piste encodée a d'autres paquets : du son aux mêmes endroits (à 2 paquets plus 3 ms près) et des paquets sur la grille fixée par le premier (à 3 ms près), sauf là où la grille de la source elle-même se déplace.
  - Fin de l'audio : 6 ms pour une piste encodée ; une piste recopiée à un paquet plus 3 ms près ; l'AAC et un encodeur qui complète son dernier paquet (MP3, par exemple) peuvent finir jusqu'à un paquet plus 3 ms après.
- **Plugin** — Les commandes dont le résultat ne pourrait pas être comparé restent sur le ffmpeg d'Emby dès le départ, avant tout encodage (`WhyResultCannotBeVerified`) : `-async`, `-ss`, `-sseof`, `-to`, `-t`, `-itsoffset`, `-output_ts_offset`, `-itsscale` ; `-copyts` sans `-start_at_zero` ; `-avoid_negative_ts` autre que `disabled`, ou absent pour une sortie Matroska ; filtres audio autres que `volume`, `anull`, `aformat`, `pan` ; encodeurs audio dont l'amorce n'a pas été mesurée ; sources autres que Matroska/WebM, MP4/MOV, AVI et MPEG-TS ; sorties autres que MP4, MOV et Matroska ; pistes source non écrites `0:N`. Le planificateur refuse aussi les filtres vidéo `setpts`, `trim`, `select`, `tpad`, `loop`, `reverse`. Une telle conversion n'apparaît pas dans l'historique du plugin ; la raison est dans le journal ffmpeg d'Emby.
- **Plugin** — Contrôle du décalage des tranches avant l'assemblage (`SliceShiftCheck`) : le coordinateur lit les premières images d'une exécution de worker dans son premier morceau. Une exécution à plus de 3/4 d'image de sortie de son point de départ rejette la conversion, tout comme une exécution commençant à 10 s ou plus dans le fichier dont la première image ne peut pas être située. Rapport d'échec avec le verdict « images décalées », dossier de travail gardé, commande d'Emby sur le serveur.
- **Plugin** — Page de suivi dans le tableau de bord, « Suivi du transcodage » : workers, conversions en cours, historique avec la raison de chaque échec (les 100 plus récentes, gardées 7 jours), longueur de la file d'Emby, rafraîchie toutes les 5 s tant qu'elle est affichée. La ligne de contrôle du relais et un assemblage rejeté avec l'audio converti à l'avance remontent désormais dans le statut.
- **Plugin** — Case « Audio converti à l'avance » sur cette page, désactivée par défaut : `EncodeAudioAhead` est enregistré et s'applique aux conversions capturées ensuite, sans redémarrage.
- **Plugin** — Diagnostic de la bibliothèque : une tâche planifiée manuelle et sa page, « Diagnostic de la bibliothèque », avec `GET /RemoteTranscode/LibraryDiagnostic` (administrateurs seulement). Il liste les vidéos à risque pour une conversion par tranches : trous dans les temps, paquets sans temps de présentation, cadence variable, cadence enregistrée différente de la réelle, vidéo plus courte que le son, vidéo commençant après le son, début de fichier lu différemment. Un fichier à la fois, priorité minimale, en pause pendant les lectures et les conversions, reprise possible. Les fichiers ne sont jamais modifiés.
- **Worker** — Mise à jour automatique depuis les releases GitHub du projet, activée par défaut, avec une case dans les réglages sous l'association et un avis unique. Le worker lit la dernière release du dépôt inscrit dans son code au démarrage, puis environ toutes les 6 h, ne prend un installeur que s'il porte le nom attendu et une version strictement plus récente, vérifie son SHA-256 et le lance avec `--quiet` après 2 minutes sans tâche, en refusant les nouvelles entre-temps. Une version en échec attend 24 h. Case décochée : aucun contact avec GitHub. Le coordinateur peut seulement demander une vérification plus tôt : ni adresse, ni chemin, ni empreinte.
- **Plugin** — Version minimale des workers pour les conversions : les workers antérieurs à la 0.7.19, ou de version inconnue, ne reçoivent aucune tranche, et une conversion les traite comme non connectés. Les lectures ne changent pas. Le refus apparaît dans l'API de statut (`conversionsRefused`, `conversionsRefusedLabel`), les pages du plugin et de suivi, la note et l'erreur d'abandon de la conversion, et un avertissement du journal par connexion.

### Corrigé

- **Plugin** — AVI à images B empaquetées : le premier morceau sortait de 40 à 120 ms en avance, suivi d'un trou (ffmpeg 9.0.2). La vidéo commence désormais là où le décodeur donne sa première image.
- **Plugin** — Dernière tranche sans `-r` : tout ce qui suit le premier morceau pouvait être jusqu'à une demi-image en avance. Les longueurs des morceaux viennent désormais des images, comptées ou mesurées dans les morceaux.
- **Plugin** — Les sources MPEG-TS sont désormais converties en une seule tranche : un seek se pose sur le PCR, jusqu'à 0,7 s après l'image demandée, et perd des images à chaque tranche (18 par tranche avec ffmpeg 9.0.2).

### Modifié

- **Plugin** — Fin de conversion : une machine passe après les autres quand les machines nettement plus rapides qu'elle (en moins de 80 % de son temps) finiraient tout ce qui attend avant qu'elle finisse cette tranche (`SliceEndgame`). Si la plus rapide est libre, elle vient en second, dans le même passage si personne n'a pris la tranche ; si elle est occupée, elle attend, et c'est redécidé à chaque passage. Un worker sans vitesse mesurée ne passe après les workers mesurés libres que si ceux-ci peuvent prendre toutes les tranches en attente.
- **Installeur** — La vérification avant l'installation et la copie réessaient un fichier verrouillé pendant 20 s au plus. Une mise à jour silencieuse qui échoue relance le worker qu'elle a fermé.

### Limites

- Le contrôle de synchronisation lit des temps, pas le contenu de l'image ni du son : une image ou un paquet au bon moment avec le mauvais contenu n'est pas vu. Seules les fenêtres sont comparées : un défaut qui commence et finit entre deux d'entre elles n'est pas vu non plus.
- Avec le `-r` d'Emby à une autre fréquence que la source, ou là où la source sort de sa propre grille, un décalage de la vidéo jusqu'à une demi-image de sortie (plus 3 ms : 23 ms à 25 i/s) ne se distingue pas de ce que fait exprès la grille d'images de ffmpeg, et il est accepté.
- Un AVI à images B empaquetées n'a pas de temps de présentation sur beaucoup de paquets : sa vidéo est comparée sur les temps de décodage, et sa fin à environ 3 images près.
- L'audio encodé est comparé sur les temps des paquets : une fraction de paquet se voit sur la grille, des paquets entiers à la fin de la piste. Un décalage de paquets entiers qui n'atteint pas la fin ne se voit que s'il ouvre dans une fenêtre un trou plus grand que la tolérance.
- Le contrôle du décalage des tranches ne juge ni les exécutions du serveur, ni celles sans seek, ni celles commençant à moins de 10 s du début du fichier ou sur un temps qui n'est pas une image connue, ni celles dont le worker n'a pas indiqué le début de son fichier. Il peut manquer une exécution en retard que l'arrondi ramène de plus d'un quart d'image.
- Une exécution interrompue d'une tranche unique (MPEG-TS, AVI à images B empaquetées) reprend quand même avec un seek. Seuls les contrôles ci-dessus couvrent ce cas : il n'a pas été mesuré.
- Un fichier converti par la commande d'Emby, après un refus ou un rejet, ne passe pas par le contrôle de synchronisation.
- Les tolérances ont été mesurées avec ffmpeg 9.0.2. `test/local/nas-sync-probe.sh` mesure sur le serveur, avec le ffprobe d'Emby, ce sur quoi le contrôle s'appuie. Les conversions de vrais fichiers AVI et MPEG-TS n'ont pas été lancées avec cette version.
- Le diagnostic de la bibliothèque lit environ 40 s de chaque fichier (début, cinq passages, fin) : un fichier absent de la liste peut quand même avoir un défaut ailleurs.
- Les installeurs ne sont pas signés, et le SHA-256 vient de la même release : la mise à jour automatique fait confiance au compte et au dépôt GitHub du projet. Décocher la case pour installer chaque version soi-même. Elle ne fonctionne qu'en zone de notification, pour une copie dans son dossier d'installation.
- Les deux versions minimales se relèvent à la main. À 0.7.19, un worker doté du mécanisme de mise à jour n'est jamais en dessous : la demande de vérification plus tôt ne part donc encore vers aucun d'eux.

### Banc

- T27a attend le rejet pour images décalées.
- T30, ajouté : un assemblage volontairement désynchronisé (+12 ms, puis +200 ms sur un morceau) doit être rejeté et le fichier livré par la commande d'Emby correct ; des conversions propres d'un AVI à images B empaquetées avec MP3 et d'un MPEG-TS de 300 s ne doivent pas être rejetées.

## Plugin 0.7.35 — 02/11/2026

Construit et publié par GitHub Actions (release v0.7.35). Worker inchangé (0.7.19).

### Corrigé

- **Plugin** — Une vidéo commençant après le début du fichier sortait d'autant en avance sur son audio, de la première à la dernière image : le démultiplexeur concat fait démarrer les morceaux à 0. Mesuré sur de vrais fichiers : 79 ms et 21 ms. L'assemblage remet désormais la vidéo à sa place avec `-itsoffset` sur la liste des morceaux.

### Ajouté

- **Plugin** — Le contrôle d'une conversion compare aussi l'endroit où la vidéo commence par rapport au début du fichier, à une demi-image près (5 ms au moins). Un résultat décalé par rapport à son audio est rejeté et converti sur le serveur. Non contrôlé sur les sources sans temps de présentation exacts (certains AVI).

### Banc

- T29 : vidéo commençant 79 ms après l'audio.

## Plugin 0.7.34 et worker 0.7.19 — 02/11/2026

Construit et publié par GitHub Actions (release v0.7.34).

### Corrigé

- **Plugin et worker** — Quand le ffmpeg du serveur et celui d'un worker lisent le début d'un fichier à un écart supérieur à la marge de seek (0 et 0,021 s à 25 i/s, marge de 20 ms), chaque tranche d'un worker commençait une image trop tard, avec une image répétée ou sautée à la rencontre d'une tranche du serveur, et une dernière tranche sur un worker perdait sa dernière image. Le contrôle de la conversion acceptait ces fichiers. Le coordinateur envoie désormais le début qu'il lit (`ServerFileStart`), et le worker déplace son `-ss` de la différence et la signale (`SeekCorrection`), ce qui ramène les temps des morceaux à ceux du serveur. Les workers plus anciens l'ignorent et se comportent comme avant. Test de banc T27 : 7499 images sur 7500 sans la correction, 7500 avec.
- **Plugin** — `EncodeAudioAhead` : le MP4 préparé écrivait son décalage dans l'échelle de temps par défaut du film (1000), arrondie à la milliseconde, et tout l'audio sortait 0,667 ms en avance. Il utilise désormais `-movie_timescale 28224000`. Mesuré avec le ffmpeg 5.1 d'Emby sur 3 minutes d'audio : 0,000000 s d'écart avec la conversion classique, paquets de mêmes tailles.
- **Plugin** — Une dernière tranche dont l'exécution était arrêtée une fois tout encodé (un worker qui se libère juste à la fin de l'exécution du serveur) reprenait sur sa dernière image, présente ensuite deux fois dans la vidéo assemblée. La tranche se termine désormais là.
- **Plugin** — Dernière tranche : les longueurs des morceaux sont arrondies à un nombre entier d'images quand la commande d'Emby fixe la fréquence (`-r`). Un décalage de liste restait dans la longueur du premier morceau d'une exécution (54 ms entre deux images à 25 i/s, le reste de la tranche 14 ms en retard).

### Banc

- T27 : début de fichier lu à 21 ms d'écart à 25 i/s, sans et avec la correction.
- T28 : `EncodeAudioAhead` comparé à l'assemblage classique sur 10 types de fichiers (AC-3, E-AC-3, DTS, FLAC, Opus, MP3, deux pistes à 48 et 44,1 kHz, HEVC 10 bits, audio commençant 0,5 s plus tard, MPEG-TS, de 23,976 à 29,97 i/s). Aucun paquet ne diffère. Les sources MPEG-TS restent sur l'assemblage classique, par conception.

`EncodeAudioAhead` reste désactivé par défaut.

## Plugin 0.7.33 — 02/11/2026

Construit et publié par GitHub Actions (release v0.7.33). Worker inchangé (0.7.18).

### Ajouté

- **Plugin** — `POST /RemoteTranscode/AudioAhead` active ou désactive `EncodeAudioAhead` sur un vrai serveur, pour les conversions capturées ensuite, sans redémarrage. L'option reste désactivée par défaut, sans bouton sur la page : jusqu'ici, elle n'était réglable qu'au banc et ne pouvait donc pas être validée avec le ffmpeg 5.1 d'Emby.

## Plugin 0.7.32 — 02/11/2026

Construit et publié par GitHub Actions (release v0.7.32). Worker inchangé (0.7.18).

### Corrigé

- **Plugin** — Conversions rejetées pour images manquantes alors qu'aucune image ne manquait (issue #4 : -12 images sur un MP4 dont le fichier commence à 0,062 s, à 23,976 images/s). Le `-r` d'Emby cale les temps listés sur sa propre grille, jusqu'à une demi-image des bornes des tranches : ici 20,3 ms, juste au-dessus des 20 ms fixes sous lesquels un décalage de liste était corrigé, et chaque tranche perdait 20,3 ms dans la liste d'assemblage. La tolérance suit désormais le fichier (un peu plus d'une demi-image, au moins 20 ms) ; une image entière manquante reste bien au-dessus et se voit toujours au contrôle. Présent depuis la 0.7.21. Test de banc T26 (source générée avec la recette du rapport) : échoue sans le correctif, 10070 images sur 10070 avec.
- **Plugin** — Rapports d'échec : la recette signale que l'encodeur aac avance le début de l'audio de 1024 échantillons, ce qui avait masqué ce défaut au premier essai.

## Plugin 0.7.31 et worker 0.7.18 — 02/11/2026

Construit et publié par GitHub Actions (release v0.7.31).

### Corrigé

- **Plugin** — Conversions rejetées pour quelques images en trop (+3 à +22) sur les sources dont la vidéo commence quelques millisecondes après 0 (issue #3). Pour le même fichier, le ffmpeg 5.1 d'Emby lit un début de fichier à 0 et le ffmpeg 9 des workers à 0,005 s. Les bornes des tranches sont calculées sur la première horloge et le `-ss` du worker compte sur la seconde : avec une marge de 1 ms, il sautait la première image de sa tranche et prenait la première de la suivante. Les exécutions visent désormais entre deux images (la moitié du plus petit écart autour de la borne, entre 1 et 50 ms) : un désaccord de quelques millisecondes tombe sur la même image. Test de banc T24 (faux ffprobe imitant celui d'Emby, source commençant à 5 ms, workers seuls, aussi avec un worker tué) : 11509 images sur 11509.
- **Plugin** — Les rapports d'échec ne masquent plus les numéros de version qui ont la forme d'une adresse IPv4 (« worker <ip> » pour un worker 0.7.15.0, un pilote GPU 11.1.45.729 affiché « <ip> » ; issue #2) : seuls quatre nombres jusqu'à 255, le premier différent de 0, sont masqués.

### Ajouté

- **Plugin, worker** — Les rapports d'échec contiennent désormais de quoi résoudre une issue sans le fichier. Avant l'envoi, le plugin mesure l'échec en arrière-plan (priorité la plus basse, 150 s au plus, sans retarder la conversion d'Emby ni la suivante de la file) : empreinte temporelle de la source, temps listés et mesurés de chaque tranche, images comptées et attendues, décalage de la première image, début du fichier tel que le ffmpeg de chaque machine l'a lu, verdict automatique (`shifted-frames`, `file-start-disagreement`, `listing-late`...) et commande ffmpeg qui génère un fichier de test aux mêmes caractéristiques (mire et audio silencieux). Les chemins, noms, titres et tailles des paquets de la source ne sont jamais envoyés ; les commandes ne gardent que leurs réglages techniques. Le worker 0.7.18 transmet le début de fichier qu'il a lu. Le guide utilisateur liste ce qui est envoyé.

### Banc

- T24 : le défaut de l'issue #3 (échoue sans le correctif, +3 images). T25 : un vrai rejet provoqué avec l'ancienne marge, le rapport complet écrit dans un fichier (`IssueReportFile`) et vérifié : verdict, recette, tableau des tranches, rien de personnel. `fake-emby-ffprobe` imite le ffprobe d'Emby, dont le début de conteneur vaut 0.

## Plugin 0.7.30 — 01/11/2026

Construit et publié par GitHub Actions (release v0.7.30). Worker inchangé (0.7.17).

### Corrigé

- **Plugin** — Une tranche réservée à un worker (préchargée pendant qu'il encode sa tranche en cours) ne bloque plus la conversion quand ce worker devient indisponible sans se déconnecter (son PC est occupé). La tranche réservée n'était proposée qu'à ce worker et, refusée, arrêtait le passage du planificateur : les tranches suivantes n'étaient jamais proposées aux autres workers, pourtant libres. Constaté le 01/11 : un épisode 4K HDR a attendu 30 minutes avec un worker inactif, puis a été rendu à Emby avec 4 tranches restantes, qui l'a converti sur le processeur du serveur. Une tranche réservée refusée est désormais ouverte à toutes les machines, et le passage continue avec les tranches suivantes.

## Plugin 0.7.29 et worker 0.7.17 — 01/11/2026

Construit et publié par GitHub Actions (release v0.7.29).

### Modifié

- **Plugin** — Le tone mapper HDR → SDR d'une conversion n'est plus figé à sa capture mais au démarrage de sa première tranche (`toneMappingDecided` dans l'état). Jusque-là, il est réévalué à chaque passage du planificateur selon les workers connectés. Pendant `EncodeToneMapperGraceSeconds` (60 s) après le démarrage du coordinateur, une conversion HDR qu'aucun worker connecté ne sait tone mapper sur le GPU attend au lieu de partir sur le processeur, tant qu'un worker appairé n'est pas encore connecté : Emby relance ses conversions dans la seconde qui suit son démarrage, avant le retour des workers, et tout l'épisode restait figé sur le processeur (constaté le 01/11, deux fois plus lent). zscale et libplacebo ne sont toujours jamais mélangés dans un même fichier.

### Corrigé

- **Installeur** — Une mise à jour lancée depuis une autre session Windows du même utilisateur (par exemple par SSH, en session 0, pendant que le worker tourne sur le bureau) ferme désormais le worker : les processus sont reconnus par leur propriétaire et leur dossier d'installation dans toutes les sessions, le worker écoute aussi un événement de sortie `Global\`, et ce qui tourne encore après 20 s est fermé. Depuis une session sans bureau, le worker est relancé dans la session de bureau de l'utilisateur par une tâche planifiée ponctuelle, ou, si aucune n'est ouverte, à la prochaine ouverture de session quand le démarrage avec Windows est activé. Auparavant, la mise à jour échouait (« fichier utilisé »).

### Banc

- T23 : synchronisation mesurée avec 40 repères flash + bip sur 10 minutes, tranches encodées par deux workers et le serveur : même écart avec et sans `EncodeAudioAhead` (−0,2 ms). T22A : vidéo plus courte que l'audio avec `EncodeAudioAhead`. `EncodeAudioAhead` reste désactivé jusqu'à sa vérification avec le ffmpeg 5.1 d'Emby sur un vrai serveur.

## Plugin 0.7.28 et worker 0.7.16 — 01/11/2026

Construit et publié par GitHub Actions (release v0.7.28).

### Corrigé

- **Plugin** — La dernière tranche d'une vidéo qui s'arrête avant la fin du fichier se termine désormais vraiment. En 0.7.27, un worker repris sur la toute dernière image rendait à chaque exécution un morceau d'une image, déjà encodée : chaque exécution comptait comme un progrès, la tranche était relancée sans fin et une copie de plus de cette image était gardée à chaque fois. Une exécution qui ne termine pas sa tranche et fait avancer sa fin encodée de moins d'une demi-seconde est désormais une exécution vide, et ses morceaux sont abandonnés ; deux exécutions vides de suite sur la dernière tranche la terminent là où la vidéo s'arrête, trois sur une autre tranche comptent comme un échec. Test de banc T22 (vidéo HDR arrêtée 34 s avant son audio) : 6000 images en entrée et en sortie, audio complet, contrôle de la conversion réussi.
- **Worker** — Un refus du tone mapping GPU n'est plus gardé dans le cache des capacités : la sonde est refaite à chaque démarrage (quelques secondes) jusqu'à ce qu'elle réussisse, car un réglage du BIOS ou la mémoire tenue par d'autres programmes peut changer le résultat sans que ffmpeg ni les pilotes changent. Le cache est renouvelé une fois par cette version.

### Ajouté

- **Worker** — Les journaux du worker et des tâches de plus de 7 jours sont effacés automatiquement (à la première ligne écrite chaque jour). Un worker très sollicité avait écrit plus de 5 700 journaux de tâches en quelques jours.
- **Worker** — Quand la sonde du tone mapping GPU échoue parce que la mémoire Vulkan est refusée (`VK_ERROR_OUT_OF_DEVICE_MEMORY`), le journal le dit et conseille d'activer Resizable BAR. Constaté sur un PC avec une RTX 4080 dont Resizable BAR était désactivé : fenêtre BAR de 256 Mo, presque pleine, où libplacebo crée ses tables ; avec Resizable BAR, la sonde donne 28,9 dB comme sur un iGPU Intel.

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
