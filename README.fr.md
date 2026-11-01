# Transcodage distribué pour Emby

English version: [README.md](README.md)

**Vous découvrez le projet ? Lisez le [Guide utilisateur](docs/USER-GUIDE.fr.md).**

Déporte les transcodages de lecture et les conversions d'Emby vers des PC Windows (« workers »). Sans worker disponible, Emby transcode lui-même, exactement comme sans le plugin. Les bascules entre un worker et le serveur se font au segment près, sans interruption de la lecture.

Documentation complète (fonctionnement détaillé, installation, exploitation, dépannage, tests) : `final/Documentation-Transcodage-distribue.pdf`.

## Composants

| Projet (`src/`) | Rôle | Livrable |
|---|---|---|
| `RemoteTranscode.EmbyPlugin` | Plugin Emby : page d'administration, API, détection de l'installation d'Emby, hébergement du coordinateur, interception du ffmpeg d'Emby | `RemoteTranscode.EmbyPlugin.dll` (DLL unique, environ 46 Mo) |
| `RemoteTranscode.Coordinator` | Répartit les tâches entre workers, sert les médias et reçoit les segments ; tourne dans le processus d'Emby (ou en autonome pour le développement) | compilé dans la DLL du plugin |
| `RemoteTranscode.Shim` | Relais `remote-ffmpeg` qu'Emby lance à la place de son ffmpeg ; envoie le travail au coordinateur et reprend localement en cas de besoin | embarqué dans la DLL pour linux-x64, linux-arm64, linux-musl-x64 et win-x64 |
| `RemoteTranscode.Worker` | Application Windows (zone de notification) : disponibilité, exécution de ffmpeg, cache local des sources, envoi des segments, désinstallation (`--uninstall`) | `EmbyRemoteWorker.exe` |
| `RemoteTranscode.Installer` | Installeur du worker, sans droits administrateur ; simple mise à jour quand le worker est déjà installé | `EmbyRemoteWorker-Setup-<version>.exe` |
| `RemoteTranscode.Common` | Code partagé : protocole, analyse des arguments d'Emby, découpage des conversions, liste blanche des arguments, moteur de disponibilité | — |
| `RemoteTranscode.Tests` | Tests unitaires (xUnit) | — |

## Fonctionnement

### Interception

Le plugin règle Emby pour qu'il lance un **aiguillage** à la place de son ffmpeg (réglage « chemin ffmpeg personnalisé », celui de l'administrateur est sauvegardé puis restauré). L'aiguillage démarre le relais `remote-ffmpeg`, qui lit la commande d'Emby :

- **sondes, vignettes, remux, conversions audio seules** : le ffmpeg d'Emby, tout de suite, sans rien changer ;
- **lecture avec réencodage vidéo** (HLS) et **conversion avec réencodage vidéo** : le coordinateur décide où le travail se fait.

Garde-fous : auto-test du relais au démarrage d'Emby, coupe-circuit (fichier `disabled`), liste blanche des arguments ffmpeg, commandes inconnues laissées au serveur. Si quoi que ce soit échoue, Emby retombe sur son propre ffmpeg.

### Lecture

1. **Au clic**, le coordinateur choisit où démarrer :
   - **directement sur un worker** quand le serveur ne suivrait pas : tone mapping HDR, source au-delà du 1080p (plus de 1088 lignes), ou film pour lequel le serveur a déjà été mesuré trop lent (moins de 1,2x, retenu film par film pendant 7 jours) ;
   - le coordinateur répond en 1,2 s au plus (l'analyse du film se poursuit en arrière-plan pour les lectures suivantes) ;
   - **sinon sur le serveur** : premier segment en une à deux secondes.
2. **Démarrage sur le serveur** : le worker qui prendra la suite reçoit l'ordre de **précharger** dans son cache l'en-tête et l'index du film et les données depuis le point de départ jusqu'après le point de bascule (autorisation temporaire sur ce seul fichier). Quand le serveur a encodé environ **20 s d'avance**, ou dès qu'il s'avère trop lent (moins de 1,2x, ou pas de premier segment en 5,5 s : 3 s de démarrage de ffmpeg plus un segment à 1,2x), la lecture passe au worker. Un serveur qui n'a encore produit aucun segment est arrêté sur-le-champ.
3. **Bascules au segment exact** : le relais arrête le ffmpeg en cours après un segment complet et la suite reprend à la fin exacte de ce segment, sur un worker ou sur le serveur. Cela vaut pour le démarrage rapide, la libération ou la perte d'un worker (détectée en 15 à 17 s), et le retour sur un worker quand il redevient disponible. Un worker dont ffmpeg échoue est écarté de cette lecture pendant 10 minutes.
4. **Même encodeur que le serveur** par défaut, pour des raccords invisibles ; l'encodage par le GPU du worker est une option.
5. Les **sous-titres texte** demandés par le lecteur web (sorties WebVTT) sont extraits sur le serveur, la vidéo sur le worker.
6. Les lectures sont **prioritaires** sur les conversions : quand une lecture n'avance pas assez vite sur un worker, une conversion qui tourne sur ce worker est déplacée pour lui laisser la place (la lecture, elle, reste sur ce worker).

### Conversions

1. La source est découpée en **tranches** d'environ 2 minutes, plus courtes à la fin (60, 60, 30, 30 s). Chaque borne est posée exactement sur une image, lue par ffprobe : aucune image perdue ni doublée. Exception : une source dont les paquets n'ont pas d'horodatage d'affichage (AVI DivX/Xvid à images B empaquetées) n'est pas découpée, car des exécutions qui s'y positionnent ne se rejoignent pas sur la même image ; elle est convertie en une seule tranche.
   - Une conversion est **acceptée dès qu'un worker capable est connecté ou que le serveur sait encoder ses tranches** : le serveur commence seul si besoin, et toutes les 10 s les tranches en attente sont de nouveau proposées aux workers ; un PC allumé en cours d'épisode y participe aussitôt. Emby ne convertit lui-même, comme sans plugin, que si ni les workers ni le serveur ne peuvent encoder les tranches.
   - HDR → SDR : le filtre `supertonemap` d'Emby est traduit en filtres standard (`zscale` + `tonemap`), de la même façon sur les workers et sur le serveur.
   - Les **remux** (vidéo recopiée, seul l'audio converti) restent sur le serveur : il n'y a rien à calculer, et le transfert vers un worker coûterait plus que le remux. Avec le réglage d'Emby « conversion à pleine vitesse » (`EnableFullSpeedTranscoding`), ils prennent quelques minutes au lieu du temps réel.
2. Chaque worker disponible prend **une tranche à la fois**. Pendant qu'il l'encode, la **tranche suivante lui est réservée** et ses données sont **préchargées** dans son cache. Environ 6 s avant la fin de sa tranche, il reçoit la suivante et **lance déjà son ffmpeg** (`EncodeOverlapSeconds`) : le démarrage (environ 4 s) ne laisse pas l'encodeur inactif.
   - Le **serveur encode aussi des tranches en parallèle** des workers (`EncodeLocalAlongsideWorkers`), une à la fois, jamais une tranche réservée à un worker. Il ne passe sa tranche à un worker libre qu'une fois qu'il ne reste plus rien en attente.
   - La fenêtre du worker (0.7.10, mise à jour toutes les 30 s) et la page du plugin affichent le **nombre d'éléments restant dans la file de conversion d'Emby**, lu par la DLL dans Emby lui-même.
   - La page du plugin s'ouvre dans un navigateur (ordinateur ou mobile), pas dans l'appli Android d'Emby, qui n'exécute pas le JavaScript des plugins tiers.
3. En fin de conversion, le coordinateur tient compte de la **vitesse mesurée** de chaque worker : il ne donne pas une tranche à un worker lent (ni au serveur) si un plus rapide la finirait clairement avant.
4. Une tranche interrompue (worker libéré ou perdu) reprend à son premier morceau manquant (à l'image de la source, lue par ffprobe, où le run précédent s'est arrêté : ffmpeg liste les heures des morceaux sur sa propre grille d'images, décalée jusqu'à une demi-image quand la vidéo ne commence pas dessus), sur un autre worker, ou sur le serveur si aucun worker n'est libre depuis 20 s, puis revient sur un worker.
5. Quand toutes les tranches sont prêtes, le serveur **assemble** le fichier avec la commande d'Emby (vidéo recopiée, audio et sous-titres d'Emby) et le **vérifie** : durée totale, puis durée de la vidéo **à l'image près** (une image et demie de tolérance, trois pour une source sans horodatages d'affichage, dont la fin n'est connue qu'à deux images près) ; si la fin de la vidéo ne peut pas être lue, la mesure est refaite puis le fichier est rejeté ; en cas de doute, le fichier est rejeté et la conversion est refaite par le ffmpeg d'Emby.
   - La liste d'assemblage `concat.txt` donne la **durée exacte de chaque morceau** (directive `duration` du format concat, tirée des listes de segments : un morceau dure jusqu'au début du suivant, et le dernier morceau d'une tranche jusqu'à la fin de la tranche quand sa fin listée en est à moins de 20 ms, si bien que chaque tranche dure exactement sa longueur). Sans elle, le ffmpeg 5.1 d'Emby estimait parfois un morceau MPEG-TS une image trop court : le morceau suivant chevauchait et la vidéo finissait des images trop tôt (constaté sur un épisode 4K : 10 images, 0,42 s, rejeté par le contrôle à l'image près).
   - Une tranche ne se termine jamais par un morceau de moins de 2 s (ses morceaux sont allongés).
   - Le journal du coordinateur note les morceaux assemblés (`assembled from chunks`).
6. Encodage GPU possible (QVBR pour Intel QSV, `-cq` pour NVIDIA NVENC), désactivé par défaut. Un même épisode peut ainsi mêler des morceaux d'encodeurs différents (QSV, NVENC, x264) : leurs paramètres de décodage diffèrent, mais les changements ont lieu aux images clés, comme en streaming adaptatif ; décodage complet sans erreur et lecture vérifiée sur une télévision Samsung.

### Signalement automatique des échecs

Quand une conversion échoue (assemblage rejeté par le contrôle, worker en échec sur une tranche, conversion rendue à Emby), le plugin ouvre une issue sur ce dépôt, ou complète celle déjà ouverte avec la même signature, pour que le problème soit corrigé. Anonyme : versions du plugin, d'Emby et des workers, système et architecture du serveur, workers décrits par leur version et leur modèle de GPU seulement, message d'erreur sans aucun chemin, caractéristiques techniques de la source (conteneur, codecs, profil, profondeur de couleur, résolution, images/s, start_time, durée, HDR, pistes audio), profil de conversion (encodeur, noms des filtres d'Emby), type de machine qui a encodé chaque tranche, résumé `assembled from chunks` et ligne de contrôle. Jamais envoyé : chemin, nom de fichier, titre, domaine, adresse IP, nom d'hôte, de PC, de worker ou d'utilisateur, identifiant Emby. La source est désignée par un code anonyme, relié au fichier dans le seul journal d'Emby de ce serveur ; l'installation par les 8 premiers caractères d'un identifiant aléatoire. Au plus 5 envois par jour et par installation, jamais deux fois pour la même conversion ; envoi en arrière-plan, toute erreur réseau est ignorée. Rien n'est envoyé quand aucun worker capable n'était connecté, quand Emby annule ou lors d'un redémarrage. Désactivable sur la page du plugin (« Signaler automatiquement les échecs de conversion sur GitHub (anonyme) »). Seules les versions publiées par GitHub Actions contiennent le jeton d'envoi (jeton à portée limitée aux issues de ce dépôt) ; une compilation locale ou un fork n'envoie jamais rien.

### Worker

- **Disponibilité** : manuelle ou automatique (PC inutilisé), plages horaires, batterie, charge laissée par les autres applications (CPU, GPU, mémoire) avec hystérésis ; « Libérer le PC » rend les tâches tout de suite, le drainage laisse finir les lectures en cours.
- **Cache local** des sources (blocs de 4 Mo, au plus 4 Go et un quart de l'espace libre) : ffmpeg lit le film à travers lui, ce qui permet le préchargement et évite de relire l'en-tête et l'index à chaque reprise.
- **Mise à jour** : l'installeur demande au worker de se fermer proprement (ses tâches reprennent ailleurs) et ne le force qu'en dernier recours (version ancienne sans ce signal, ou pas de fermeture en 20 s) ; il remplace les fichiers en gardant dossier, réglages et association, puis le relance.

### Sécurité

Chaque worker est associé par un code (jeton dont seule l'empreinte est conservée), chaque tâche a son propre jeton, le préchargement utilise une autorisation de trois minutes limitée à un fichier. Les workers passent par TLS (direct ou derrière le reverse proxy) ; 10 échecs d'authentification en 10 minutes bloquent l'adresse pendant 15 minutes. Les secrets du serveur ne sont lisibles que par l'utilisateur d'Emby.

## Installation

- **Plugin** : copier `RemoteTranscode.EmbyPlugin.dll` dans le dossier `plugins` d'Emby, redémarrer Emby, puis régler le plugin dans Tableau de bord → Transcodage distribué (détail dans `docs/02-installation.md`).
- **Synology sans SSH** : une tâche DSM générique « Installer plugin RemoteTranscode » (utilisateur root, désactivée, exécutée à la demande) recolle les morceaux `plugin.part0`..`plugin.part5` déposés avec un fichier `plugin.sha256` dans `docker/emby-remote/plugin`, puis lance `tools/nas/install-plugin.sh` avec cette empreinte ; journal `install-<date>.log` dans le même dossier. Pour une nouvelle version, il suffit de déposer les fichiers et d'exécuter la tâche : la tâche n'est pas modifiée, donc pas de mot de passe DSM à saisir.
- **Worker** : `EmbyRemoteWorker-Setup-<version>.exe`, sans droits administrateur ; relancé sur un PC déjà équipé, il fait une simple mise à jour.

## Documentation

- Historique des versions : `CHANGELOG.md`.
- Fonctionnement détaillé, installation, exploitation, dépannage : `final/Documentation-Transcodage-distribue.pdf`.
- Constats sur Emby 4.10 : `docs/01-audit-emby-4.10.md`.
- Installation, mise à jour, désinstallation : `docs/02-installation.md`.
- Banc de test local et résultats : `test/local/README.md` et `docs/03-tests-acceptation-local.md`.

## Compiler

Prérequis : Windows x64, SDK .NET 8 (`dotnet --list-sdks`). Les références Emby se trouvent dans `research/emby-4.10.2.0`, copiées depuis un serveur 4.10.2. Les paquets NuGet passent par le `nuget.config` du dépôt.

```powershell
# Tout compiler et lancer les tests
dotnet build src\RemoteTranscode.sln -c Release
dotnet test  src\RemoteTranscode.Tests -c Release

# Plugin Emby (relais publié pour chaque plateforme, compressé et embarqué)
pwsh tools\build-plugin.ps1                # -> artifacts\plugin\RemoteTranscode.EmbyPlugin.dll

# Worker Windows, avec FFmpeg 9.0.2 « essentials » de gyan.dev, vérifié par SHA-256
pwsh tools\package-worker.ps1              # -> artifacts\worker-package\EmbyRemoteWorker\ et .zip
pwsh tools\build-installer.ps1             # -> artifacts\installer\EmbyRemoteWorker-Setup-<version>.exe
```

`build-plugin.ps1` n'inscrit le jeton du signalement automatique des échecs que s'il est fourni (`-IssueReportToken`, ou la variable d'environnement `ISSUE_REPORT_TOKEN` que GitHub Actions remplit depuis le secret du dépôt du même nom) ; sans lui, le plugin compilé ne signale jamais rien. Le jeton n'est jamais enregistré dans le dépôt.

`package-worker.ps1` travaille dans `artifacts\worker-package` et refuse de produire un paquet dont l'exécutable n'a pas la version attendue (un worker qui tourne depuis le dossier de sortie garderait son ancien exécutable).

Signature de code (possible, pas prévue) : `build-installer.ps1` accepte `-CertThumbprint <empreinte>` ou `-PfxPath <fichier.pfx>` et signe puis horodate le worker et l'installeur. Faute de certificat reconnu (choix fait pour un usage personnel), les livrables ne sont pas signés.

Les versions se règlent dans `src/RemoteTranscode.EmbyPlugin/RemoteTranscode.EmbyPlugin.csproj`, `src/RemoteTranscode.Worker/RemoteTranscode.Worker.csproj` et `tools/build-installer.ps1`. Le nom des pages du plugin contient sa version, pour que les navigateurs ne gardent pas une ancienne page.

## Versions

| Élément | Version |
|---|---|
| Plugin | 0.7.25 |
| Worker et installeur | 0.7.14, avec icône et décodage par le GPU (les workers 0.7.7 à 0.7.13 restent compatibles : 0.7.9 n'affiche le compteur de la file d'Emby que pendant une tâche, 0.7.7 et 0.7.8 ne l'affichent pas, 0.7.7 sans l'avancement des conversions) |
| Emby Server testé | 4.10.2.0 (Synology DS1522+, DSM 7.4.1, AMD Ryzen R1600 sans GPU) ; ffmpeg d'Emby 5.1 (fork) |
| Client testé | Emby Web dans Chrome (lecteur hls.js 1.6.0-beta.2) |
| Workers testés | Windows 11 Entreprise, Intel Core Ultra 5 225U (x264 et QSV) ; PC avec RTX 4080 (x264 et NVENC, environ 10x sur un épisode 1080p) ; FFmpeg 9.0.2 |
| .NET | 8 (Emby embarque son propre runtime .NET 8 et ASP.NET Core) |

## Limitations connues

- Pas encore essayé sur Emby pour Windows, Emby sous Docker ni Linux arm64. Le relais est embarqué pour ces plateformes, et un auto-test au démarrage garde Emby sur son ffmpeg si le relais ne peut pas s'exécuter.
- Le plugin s'appuie sur le réglage « chemin ffmpeg personnalisé » d'Emby, modifié par réflexion. Emby indique ce réglage comme non pris en charge, et une future version d'Emby pourrait le changer. Le plugin le détecte et ne touche alors à rien.
- Lecture démarrée au tout début du film puis reprise ailleurs : les horodatages du segment repris reculent de 80 ms (comportement de ffmpeg) ; sans effet constaté dans Chrome.
- Le banc de test local ne peut pas exercer ce qui dépend du ffmpeg d'Emby (démarrage sur le serveur, sous-titres WebVTT) : ces parties sont validées sur le serveur réel.
- Les conversions avec sous-titres incrustés restent faites par le serveur.
- L'installeur et le worker ne sont pas signés : un antivirus d'entreprise peut les bloquer. L'installeur évite les comportements qu'ils jugent suspects (pas d'auto-copie, fermeture propre du worker), mais seule une signature de code donne une garantie. Le script de construction sait signer (voir « Compiler ») ; pour un usage personnel, aucun certificat n'a été acheté.
- Avec un worker joint par internet, le débit des segments dépend de la connexion du PC.
- Le cahier des charges demandait une mise en attente quand aucun worker n'est disponible ; à la demande de l'utilisateur, le serveur transcode alors lui-même tout de suite, puis rend la tâche à un worker dès qu'il revient.
