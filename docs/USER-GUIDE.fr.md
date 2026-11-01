# Guide utilisateur — Transcodage distribué pour Emby

English version: [USER-GUIDE.md](USER-GUIDE.md)

Ce guide s'adresse aux personnes qui font tourner un serveur Emby à la maison et veulent que leurs PC Windows l'aident pour le travail vidéo. Aucune connaissance en programmation n'est nécessaire.

Dans Emby, le plugin s'appelle « Transcodage distribué » ; le programme Windows s'appelle **Emby Remote Worker**.

## Sommaire

1. [Ce que ça fait](#1-ce-que-ça-fait)
2. [Prérequis](#2-prérequis)
3. [Installer le plugin sur le serveur Emby](#3-installer-le-plugin-sur-le-serveur-emby)
4. [Configurer le côté serveur](#4-configurer-le-côté-serveur)
5. [Installer et associer un PC worker](#5-installer-et-associer-un-pc-worker)
6. [Utilisation au quotidien](#6-utilisation-au-quotidien)
7. [Conversions (Convertir et téléchargements)](#7-conversions-convertir-et-téléchargements)
8. [Mise à jour, retour arrière et désinstallation](#8-mise-à-jour-retour-arrière-et-désinstallation)
9. [Dépannage et FAQ](#9-dépannage-et-faq)
10. [Limites connues](#10-limites-connues)

---

## 1. Ce que ça fait

Emby doit parfois réencoder une vidéo : quand un appareil ne sait pas lire le fichier tel quel, ou quand vous convertissez un film pour le télécharger. Sur un petit NAS sans carte graphique, c'est lent. Ce projet confie ce travail aux PC Windows de la maison. Chacun de ces PC s'appelle un **worker**.

Il y a deux parties :

- un **plugin** pour Emby Server (un seul fichier, `RemoteTranscode.EmbyPlugin.dll`) ;
- un petit **programme Windows** sur chaque PC worker (il vit dans la zone de notification, près de l'horloge).

### Le fonctionnement en une minute

**Lecture**

- Quand vous appuyez sur Lecture, le serveur commence en général à encoder lui-même. La première image arrive en une ou deux secondes.
- Quelques secondes plus tard, la lecture passe sur un PC worker. La bascule se fait exactement entre deux segments vidéo : pas de coupure, pas d'image perdue.
- Les vidéos lourdes (4K, conversion HDR, ou film pour lequel le serveur s'est déjà montré trop lent) démarrent directement sur un worker.
- Si le PC worker disparaît (vous lancez un jeu, vous l'éteignez, il se met en veille), le serveur reprend au point exact, et la lecture revient sur un worker dès qu'un PC est de nouveau libre.
- Les lectures sont prioritaires. Si une lecture peine sur un PC, une conversion qui tourne sur ce même PC est déplacée ailleurs.

**Conversions** (« Convertir » et téléchargements d'Emby)

- Le film est découpé en tranches d'environ 2 minutes. Chaque coupure tombe exactement sur une image.
- Tous les PC disponibles encodent des tranches en parallèle. Le serveur en encode aussi.
- À la fin, le serveur assemble le fichier avec les réglages d'Emby et le vérifie, à l'image près. Au moindre doute, le fichier est rejeté et Emby refait la conversion lui-même.
- Un PC qui disparaît au milieu d'une tranche n'est pas un problème : la tranche reprend ailleurs.

**Filet de sécurité**

- Sans worker disponible, Emby transcode simplement lui-même, exactement comme sans le plugin.
- Emby n'est pas modifié. Désactiver le plugin, ou le désinstaller, rend immédiatement à Emby son fonctionnement normal.
- La lecture directe (Direct Play, sans réencodage) n'est jamais touchée.

---

## 2. Prérequis

| Élément | Prérequis |
|---|---|
| Emby Server | Version 4.10. Testé en **4.10.2**. Rien d'autre à installer : le plugin utilise l'environnement .NET fourni avec Emby. |
| Emby Premiere | **Pas nécessaire pour le plugin**, ni pour déporter le transcodage des lectures. La fonction « Convertir » / téléchargement (synchronisation) d'Emby, que le plugin accélère, est une fonction Emby Premiere. |
| Système du serveur | Le plugin contient son programme relais pour **Linux x64**, **Linux arm64**, **Linux x64 musl** (images Docker basées sur Alpine) et **Windows x64**. Seul Emby sur **Synology (Linux x64)** a été testé à ce jour (voir [Limites connues](#10-limites-connues)). |
| PC workers | **Windows 10 ou 11, 64 bits.** L'installeur contient tout (le programme et FFmpeg, environ 178 Mo). Pas besoin de droits administrateur. |
| Carte graphique (facultatif) | Les workers encodent par défaut avec le processeur. Ils peuvent aussi utiliser la carte graphique : **NVIDIA (NVENC)**, **Intel (Quick Sync / QSV)** ou **AMD (AMF)**. Chaque encodeur est testé sur le PC avant d'être utilisé. |
| Réseau | Les workers se connectent **au** serveur ; rien n'est ouvert sur les PC. Le serveur a besoin d'un port joignable par les workers (TCP **8097** par défaut). Pour des workers hors de la maison, il faut un nom de domaine et un reverse proxy HTTPS (voir [section 4.3](#43-workers-hors-du-réseau-local)). |

---

## 3. Installer le plugin sur le serveur Emby

### 3.1 Télécharger et vérifier le fichier

1. Ouvrez la page **Releases** du dépôt GitHub (`djdmg/EmbyWorkerTranscodePlugin`).
2. Téléchargez `RemoteTranscode.EmbyPlugin.dll` et `RemoteTranscode.EmbyPlugin.dll.sha256` dans la dernière version publiée.
3. Vérifiez que le fichier est intact. Calculez son empreinte SHA-256 et comparez-la à la valeur du fichier `.sha256` :
   - sous Windows (PowerShell) : `Get-FileHash .\RemoteTranscode.EmbyPlugin.dll -Algorithm SHA256`
   - sous Linux ou sur un NAS en SSH : `sha256sum RemoteTranscode.EmbyPlugin.dll`

   Si les valeurs diffèrent, téléchargez de nouveau le fichier. Ne l'installez pas.

### 3.2 Le copier dans le dossier des plugins d'Emby

Copiez la DLL dans le dossier `plugins` des données d'Emby :

| Installation d'Emby | Dossier des plugins |
|---|---|
| Synology | `/var/packages/EmbyServer/var/plugins` (le fichier doit appartenir à l'utilisateur `emby`, groupe `emby`) |
| Docker | `/config/plugins` dans le conteneur (c'est-à-dire le dossier `plugins` du dossier de l'hôte monté sur `/config`) |
| Windows | `%AppData%\Emby-Server\programdata\plugins` |
| Autres installations Linux | le dossier `plugins` du dossier de données d'Emby |

Puis **redémarrez Emby**.

#### Synology : installation sans outil supplémentaire

Le dossier des plugins de Synology est un dossier système : File Station ne peut pas y écrire. La méthode ci-dessous n'utilise que DSM (File Station et le Planificateur de tâches) ; rien d'autre n'est à télécharger. Elle vérifie l'empreinte du fichier, garde une copie du plugin déjà installé (pour un retour arrière), installe le nouveau avec le bon propriétaire et redémarre Emby.

1. **Déposez les fichiers dans un dossier partagé.** Avec File Station, créez un dossier, par exemple `/volume1/docker/emby-plugin/`, et copiez-y les deux fichiers téléchargés depuis la release : `RemoteTranscode.EmbyPlugin.dll` et `RemoteTranscode.EmbyPlugin.dll.sha256`. (Adaptez `/volume1/docker/emby-plugin` dans le script ci-dessous si vous choisissez un autre dossier.)
2. **Créez la tâche d'installation, une seule fois.** Panneau de configuration → **Planificateur de tâches** → **Créer** → **Tâche déclenchée** → **Script défini par l'utilisateur**.
   - Général : nommez-la, par exemple « Installer le plugin Emby » ; Utilisateur : **root** ; Événement : **Démarrage** ; **décochez « Activé »** (la tâche ne s'exécute alors que quand vous cliquez sur Exécuter).
   - Paramètres de tâche → Exécuter la commande → Script défini par l'utilisateur, collez :
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
   - DSM demande votre mot de passe pour enregistrer une tâche qui s'exécute en root.
3. **Exécutez-la.** Sélectionnez la tâche et cliquez sur **Exécuter**, une fois. Emby est indisponible pendant environ une minute.
4. **Vérifiez le résultat** dans le journal `install-<date>.log` écrit dans le même dossier : « Plugin installed, Emby restarted » signifie que c'est réussi ; « Wrong fingerprint: nothing changed » signifie que la DLL ne correspond pas à son fichier `.sha256` (retéléchargez les deux).

Pour chaque nouvelle version, remplacez les deux fichiers dans le dossier et relancez la tâche : la tâche elle-même ne change jamais. Pour **revenir en arrière**, renommez la copie `RemoteTranscode.EmbyPlugin.dll.before-<date>` en `RemoteTranscode.EmbyPlugin.dll`, écrivez son empreinte dans le fichier `.sha256` (`sha256sum RemoteTranscode.EmbyPlugin.dll > RemoteTranscode.EmbyPlugin.dll.sha256`, ou `Get-FileHash` dans PowerShell sur un PC) et relancez la tâche.

**Avec SSH** (si vous l'avez activé dans Panneau de configuration → Terminal et SNMP), les mêmes commandes fonctionnent depuis un terminal : connectez-vous, tapez `sudo -i`, puis collez le script ci-dessus.

### 3.3 Ouvrir la page du plugin

1. Après le redémarrage, rechargez la page d'Emby dans votre navigateur (F5).
2. Ouvrez **Tableau de bord** → **« Transcodage distribué »**. Le plugin figure aussi dans Tableau de bord → Plugins.

> Utilisez un **navigateur web** (ordinateur ou téléphone). La page du plugin ne s'ouvre pas dans l'appli Android d'Emby, qui n'exécute pas les pages des plugins tiers.

En haut de la page, vous voyez :

- l'état, « activé » ou « désactivé », et le ffmpeg utilisé actuellement par Emby ;
- « Coordinateur intégré à Emby : en service ». C'est la partie du plugin qui dialogue avec les workers. S'il est indiqué « arrêté », la raison est affichée à côté (voir [section 9](#9-dépannage-et-faq)).

N'activez pas encore le plugin : configurez d'abord la connexion (section 4) et associez au moins un worker (section 5). Vous pouvez aussi l'activer tout de suite : sans worker, Emby continue simplement de transcoder lui-même.

---

## 4. Configurer le côté serveur

Tous les réglages sont sur la page du plugin, section « Connexion des workers ». Cliquez sur « Enregistrer » après une modification. Les réglages s'appliquent tout de suite ; si les nouveaux réglages ne fonctionnent pas, les précédents sont rétablis.

### 4.1 Les réglages

| Champ | Défaut | Rôle |
|---|---|---|
| « Adresse d'écoute des workers » | `https://0.0.0.0:8097` | Là où le serveur attend les workers. Par défaut : toutes les interfaces réseau, port 8097, chiffré (TLS) avec un certificat créé par le plugin. |
| « Adresse publique (reverse proxy, facultatif) » | vide | Seulement si les workers passent par un reverse proxy HTTPS, par exemple `https://coordinator.example.com`. |
| « Adresse du serveur pour les workers (facultatif) » | vide (détectée) | Nom ou adresse IP par lesquels les PC joignent le serveur, par exemple `192.168.1.10`. Elle est inscrite dans le code d'association. Laissez vide sauf si l'adresse détectée est mauvaise (Docker, plusieurs cartes réseau). |

Le serveur utilise aussi un second port, **8098**, mais seulement en interne (il n'accepte jamais de connexion venant du réseau). Inutile de l'ouvrir.

### 4.2 Workers sur le réseau de la maison (le plus simple)

- Gardez `https://0.0.0.0:8097`.
- Si le serveur a un pare-feu, autorisez le port TCP 8097 depuis le réseau local.
- Avec Emby sous Docker, publiez le port (`-p 8097:8097`) et renseignez « Adresse du serveur pour les workers » avec l'adresse du NAS, par exemple `192.168.1.10`.

La connexion est chiffrée. Chaque worker retient le certificat du serveur au moment de l'association et refuse tout autre certificat.

### 4.3 Workers hors du réseau local

Pour un PC qui n'est pas à la maison (portable en déplacement, PC d'un proche), utilisez un reverse proxy HTTPS. **N'exposez pas le port 8097 directement sur internet sans TLS.**

1. Sur la page du plugin, réglez « Adresse d'écoute des workers » sur `http://127.0.0.1:8097` (le plugin n'écoute alors qu'en local ; le proxy apporte le chiffrement).
2. Réglez « Adresse publique » sur l'adresse du proxy, par exemple `https://coordinator.example.com`.
3. Configurez le reverse proxy. Sur Synology : Panneau de configuration → Portail de connexion → Avancé → Proxy inversé :
   - source : HTTPS, `coordinator.example.com`, port 443 ;
   - destination : HTTP, `localhost`, port 8097 ;
   - en-tête personnalisé : **WebSocket** (indispensable) ;
   - délais de connexion et de lecture : 300 secondes ;
   - attribuez un certificat public valide à ce nom (Panneau de configuration → Sécurité → Certificat).
4. Associez les workers **après** avoir réglé l'adresse publique. Si l'adresse publique change plus tard, associez-les de nouveau.

À savoir :

- L'adresse d'écoute doit être une adresse IP, et son port doit être différent de celui d'Emby.
- Sans TLS sur l'adresse d'écoute, l'adresse publique est obligatoire.
- Dix échecs d'authentification depuis une même adresse en 10 minutes bloquent cette adresse pendant 15 minutes. Les PC de la maison partagent en général la même adresse publique : des erreurs répétées peuvent donc bloquer brièvement tous les workers distants du foyer.
- Un worker joint par internet est limité par le débit montant de votre connexion.

### 4.4 Les boutons « Activer » et « Désactiver »

| Bouton | Effet |
|---|---|
| « Activer » | Les transcodages de lecture et les conversions d'Emby sont confiés aux workers. Sans worker disponible, Emby fait le travail lui-même, comme d'habitude. |
| « Désactiver » | Emby fonctionne exactement comme sans le plugin. Effet immédiat, sans redémarrage. |

Le plugin refuse l'activation tant que le coordinateur ne tourne pas, et en affiche la raison.

Rien à faire après une **mise à jour d'Emby** : au démarrage, le plugin retrouve le ffmpeg d'Emby et se reconfigure seul.

---

## 5. Installer et associer un PC worker

### 5.1 Télécharger et vérifier

1. Sur la page **Releases** de GitHub, téléchargez `EmbyRemoteWorker-Setup-<version>.exe` et son fichier `.sha256`.
2. Vérifiez-le dans PowerShell : `Get-FileHash .\EmbyRemoteWorker-Setup-<version>.exe -Algorithm SHA256`, et comparez avec le fichier `.sha256`.

### 5.2 SmartScreen et antivirus

L'installeur et le programme **ne sont pas signés** (pas de certificat payant pour un projet personnel). Windows peut donc vous avertir :

- SmartScreen « Windows a protégé votre ordinateur » : cliquez sur **« Informations complémentaires »**, puis sur **« Exécuter quand même »** ;
- ou, avant de le lancer : clic droit sur le fichier → Propriétés → cochez **« Débloquer »** → OK.

Copier le fichier depuis une clé USB ou un partage réseau évite en général l'avertissement. Si votre antivirus met le fichier en quarantaine, restaurez-le et ajoutez une exclusion pour le dossier d'installation. Sur un PC d'entreprise, adressez-vous au service informatique.

### 5.3 Lancer l'installeur

1. Double-cliquez sur `EmbyRemoteWorker-Setup-<version>.exe`. Il ne demande **pas** de droits administrateur : il installe pour l'utilisateur Windows courant uniquement.
2. La fenêtre « Installation de Emby Remote Worker » propose :
   - « Dossier d'installation » : `%LOCALAPPDATA%\Programs\EmbyRemoteWorker` par défaut ;
   - « Démarrer avec Windows » ;
   - « Créer un raccourci sur le Bureau » ;
   - « Lancer Emby Remote Worker à la fin ».

   La licence de FFmpeg (GNU GPL v3) est affichée : FFmpeg est inclus en tant que programme distinct.
3. Cliquez sur « Installer », puis sur « Fermer ».

Le worker démarre et son icône apparaît dans la zone de notification (cliquez sur la petite flèche près de l'horloge si vous ne la voyez pas).

**Installation silencieuse** (pour plusieurs PC) :

```
EmbyRemoteWorker-Setup-<version>.exe --quiet [--dir <dossier>] [--no-startup] [--no-launch]
```

Les erreurs sont écrites dans `%TEMP%\EmbyRemoteWorker-Setup.log`.

### 5.4 Associer le worker au serveur

L'association indique au worker où se trouve le serveur et lui donne une clé personnelle.

1. Sur la **page du plugin** (dans un navigateur), section « Workers » : saisissez un nom dans « Nom du nouveau worker », par exemple `PC-A`, puis cliquez sur « Associer un worker ».
2. Un long « Code d'association » apparaît. **Copiez-le tout de suite : il n'est affiché qu'une fois.** Il contient un secret : ne le collez que dans ce worker.
3. Sur le PC, ouvrez la fenêtre du worker (double-clic sur l'icône) → onglet « Paramètres ».
4. Collez le code dans « Code d'association » et cliquez sur « Associer ».
5. Ajustez au besoin « Nom du worker » (le nom affiché dans Emby), puis cliquez sur « Enregistrer ».

L'association est aussi possible en ligne de commande : `EmbyRemoteWorker.exe pair <code>` (le programme est dans le dossier d'installation).

### 5.5 Vérifier que tout fonctionne

Dans la fenêtre du worker, onglet « État » :

- « Serveur : … — connecté » ;
- « Encodeurs validés » : par exemple `libx264`, plus `h264_nvenc`, `h264_qsv` ou `h264_amf` si votre carte graphique fonctionne. Les encodeurs sont testés par de vrais encodages courts la première fois ; « Refaire la détection du matériel » (dans Paramètres) recommence le test après un changement de pilote ;
- l'état : « disponible » quand le PC accepte du travail.

Sur la page du plugin, le worker apparaît dans le tableau « Workers » avec son état, sa charge, ses capacités validées et l'heure de son dernier contact.

Cliquez maintenant sur « Activer » sur la page du plugin si ce n'est pas déjà fait.

---

## 6. Utilisation au quotidien

### 6.1 États du worker

| État (worker / page du plugin) | Couleur de l'icône | Signification |
|---|---|---|
| « disponible » | vert | Accepte du travail. |
| « occupé » | bleu | Tous ses emplacements sont pris (une lecture peut encore prendre la place d'une conversion). |
| « en drainage » | orange | N'accepte rien de nouveau ; les lectures en cours se terminent sur ce PC. |
| « indisponible » | gris | Vous n'autorisez pas le travail en ce moment, ou le PC est occupé à autre chose. La raison est affichée. |
| « hors ligne » | gris | Pas connecté au serveur. |
| « en erreur » | rouge | Un problème est survenu ; voir les journaux. |

L'infobulle de l'icône indique l'état et le nombre de tâches.

### 6.2 L'icône de la zone de notification

Clic droit sur l'icône :

| Élément du menu | Effet |
|---|---|
| État et motif, tâches en cours | Information seulement. |
| « Disponible » | Cochez ou décochez pour autoriser ou interdire le travail sur ce PC. Ce choix est conservé après un redémarrage. |
| « Libérer le PC maintenant » | Arrête toutes les tâches tout de suite (après confirmation) et décoche « Disponible ». Les lectures continuent sur un autre worker ou sur le serveur, avec une courte interruption possible. |
| « Ouvrir… » | Ouvre la fenêtre (un double-clic sur l'icône fait de même). |
| « Paramètres… », « Journaux… » | Ouvre la fenêtre sur cet onglet. |
| « À propos (version …) » | Version et détails. |
| « Quitter » | Ferme proprement le worker. Ses tâches reprennent ailleurs. |

### 6.3 La fenêtre du worker

Le titre indique la version. Il y a trois onglets et un menu « Aide ».

**Onglet « État »**

- « Autoriser l'utilisation de ce PC » : le même interrupteur que « Disponible » dans le menu de l'icône.
- « Libérer le PC maintenant ».
- Motif de l'état actuel, serveur et connexion.
- « Charge » : processeur total et part des tâches du worker, GPU, mémoire.
- « File de conversion d'Emby » : nombre d'éléments qu'Emby doit encore convertir (en attente ou en cours), actualisé toutes les 30 secondes. « — » si votre version d'Emby ne permet pas de le lire.
- « Encodeurs validés » et cartes graphiques.
- « Tâches en cours », une ligne par tâche :

| Colonne | Signification |
|---|---|
| « Fichier » | Film ou épisode. |
| « Type » | « Lecture » ou « Conversion ». |
| « Encodeur » | Encodeur utilisé, par exemple `libx264` ou `h264_qsv`. |
| « Tranche » | Pour une conversion : numéro de la tranche sur le total, par exemple `4/18`. |
| « Position » | Où en est la tâche dans le film. |
| « Tranche % » | Avancement de la tranche en cours. |
| « Total » | Avancement de toute la conversion, toutes machines confondues, avec le temps restant. |
| « Vitesse » | Vitesse d'encodage (`2.0x` = deux fois le temps réel). |
| « Durée » | Depuis combien de temps la tâche tourne. |
| « État » | En cours, ou motif de l'arrêt. |

Survolez une colonne pour afficher une infobulle.

**Onglet « Paramètres »** — voir la [section 6.5](#65-paramètres-du-worker).

**Onglet « Journaux »** — journal en direct, « Exporter (7 derniers jours)… » (archive zip, secrets masqués) et « Ouvrir le dossier ».

**Menu « Aide »** — « Journaux… », « Ouvrir le dossier de configuration », « À propos d'Emby Remote Worker… » (version, nom et identifiant du worker, serveur, version de FFmpeg, dossier des données).

### 6.4 Rendre le PC disponible ou non

Vous gardez la main sur votre PC :

- **Manuellement** : décochez « Disponible » quand vous voulez le PC pour vous, recochez-le plus tard.
- **Automatiquement** : dans Paramètres, choisissez « Automatique : disponible après une inactivité de … minutes sans clavier ni souris ». Par défaut : 10 minutes.
- **Plages horaires** : n'autoriser le travail que certains jours et à certaines heures.
- **Batterie** : bloquer le travail quand un portable est sur batterie.
- **Charge des autres programmes** : quand d'autres programmes (un jeu, une visioconférence…) utilisent trop le processeur, le GPU ou la mémoire, le PC devient « indisponible » de lui-même, puis de nouveau disponible quand la charge retombe. L'encodage du worker lui-même n'est pas compté.

Quand vous reprenez votre PC, vous choisissez ce qui se passe dans « Quand j'utilise de nouveau le PC » :

- « Terminer les lectures en cours » (par défaut) : le PC n'accepte plus rien de nouveau, les lectures en cours s'y terminent, les conversions partent ailleurs.
- « Libérer immédiatement le PC » : tout s'arrête aussitôt ; les lectures continuent sur un autre worker ou sur le serveur, avec une courte interruption possible.

Une pointe de charge due aux autres programmes n'interrompt jamais une lecture en cours : le worker passe en drainage.

### 6.5 Paramètres du worker

Onglet « Paramètres ». Cliquez sur « Enregistrer » pour appliquer, « Annuler » pour abandonner les changements.

| Réglage | Défaut | Remarques |
|---|---|---|
| « Nom du worker » | nom du PC | Nom affiché sur la page du plugin. |
| « Code d'association » + « Associer » | — | Voir section 5.4. |
| « FFmpeg » | vide | Vide = le FFmpeg fourni avec le worker (recommandé). |
| « Conversions : encodage par le GPU » | désactivé | Plus rapide. Si une tranche est ensuite reprise par le serveur, un même fichier mélange deux encodeurs (voir les limites). |
| « Lectures : encodage par le GPU » | désactivé | Bien plus rapide sur un portable. L'image du worker et celle du serveur diffèrent un peu. Désactivé = même encodeur que le serveur, donc bascules invisibles. |
| « Décodage vidéo par le GPU quand l'encodage s'y fait » | activé | Soulage le processeur ; image identique. Ne sert que si l'une des deux options précédentes est cochée. Repli automatique sur le processeur si besoin. |
| « Tâches simultanées maximum » | 2 | Une conversion n'occupe qu'un emplacement libre. |
| « Refaire la détection du matériel » | — | Teste de nouveau les encodeurs, par exemple après une mise à jour de pilote. |
| « Manuel » / « Automatique » (mode de disponibilité) | Manuel, 10 min | Voir 6.4. |
| « Bloquer les traitements sur batterie » | désactivé | |
| « Bloquer si les autres applications utilisent plus de » CPU / GPU / « mémoire totale » | 50 % / 50 % / 90 % | Charge laissée par les autres programmes. |
| « Temporisation des seuils » | 30 s | Un seuil doit être dépassé pendant cette durée pour bloquer, et la charge doit rester basse pendant cette durée pour débloquer. |
| « Stabilisation avant disponibilité » | 20 s | Toutes les conditions doivent être réunies pendant cette durée avant que le PC redevienne disponible. |
| « Quand j'utilise de nouveau le PC » | Terminer les lectures | Voir 6.4. |
| « Plages horaires autorisées » | aucune | Une ligne par plage : cochez les jours (Lu, Ma, Me, Je, Ve, Sa, Di), « Début » et « Fin » au format HH:mm. Aucune ligne = à toute heure. Une fin avant le début (22:00 → 07:00) passe minuit. |
| « Démarrer avec Windows » | selon le choix à l'installation | |

### 6.6 Encodage et décodage par le GPU

- Par défaut, les workers encodent avec le **processeur**, avec le même encodeur qu'Emby. Les bascules entre un worker et le serveur sont ainsi invisibles.
- Activez « Lectures : encodage par le GPU » sur un portable trop lent en logiciel (lecture saccadée).
- Activez « Conversions : encodage par le GPU » pour convertir beaucoup plus vite. La qualité reste constante (mode à qualité constante de chaque encodeur).
- Seuls les encodeurs qui ont réussi le test sur ce PC sont utilisés. Un GPU listé par FFmpeg mais inutilisable est ignoré.

### 6.7 La page du plugin au quotidien

La page s'actualise d'elle-même.

- **« Workers »** : pour chaque worker associé : nom et adresse, état et motif (par exemple « indisponible — PC utilisé »), « conversions suspendues pendant les lectures » le cas échéant, tâches en cours sur le maximum, charge, « Capacités validées », « Vu » (dernier contact), et un bouton « Révoquer ». La révocation déconnecte le worker sur-le-champ ; ses tâches reprennent ailleurs ; il ne pourra plus jamais se reconnecter avec ce code.
- **« Tâches en cours »** : type, fichier, worker, position, segments, vitesse. Quand il n'y en a pas : « Aucune tâche sur les workers. Sans worker, Emby transcode sur le serveur comme d'habitude. »
- **« Conversions »** :
  - un encadré **« Conversion en cours : <fichier> »** pour chaque conversion en cours : barre de progression, pourcentage, vitesse cumulée, « reste » (temps restant), « tranches » (terminées / total), et « En cours sur » avec chaque machine, sa tranche et sa vitesse ;
  - « File de conversion d'Emby : N élément(s) à convertir (en attente ou en cours) » ;
  - l'historique (gardé 7 jours) : fichier, état (« en attente », « en cours », « assemblage », « terminé », « échec », « annulé »), note (par exemple « assemblé et vérifié par le serveur »), progression, workers, tranches et morceaux.

---

## 7. Conversions (Convertir et téléchargements)

### 7.1 Lancer une conversion

Les conversions se lancent **dans Emby**, comme d'habitude : action « Convertir » sur un film, un épisode ou une série, ou téléchargement vers un appareil, avec les profils de qualité d'Emby. (Les fonctions « Convertir » et téléchargement d'Emby demandent un abonnement **Emby Premiere** : c'est une règle d'Emby, pas du plugin.) La page du plugin n'affiche que le suivi. Emby convertit sa file un élément à la fois ; le plugin accélère chaque élément.

### 7.2 Ce qui part sur les workers et ce qui reste sur le serveur

| Type de conversion | Où elle se fait |
|---|---|
| Vidéo réencodée (la plupart des conversions) | Découpée en tranches, encodées par tous les workers disponibles **et** par le serveur, puis assemblée sur le serveur. |
| Remux (vidéo recopiée, seul l'audio est converti) | **Reste sur le serveur** : il n'y a rien de lourd à calculer, et envoyer le fichier à un PC prendrait plus de temps. |
| Audio seul | Serveur (ffmpeg d'Emby). |
| Sous-titres incrustés | Serveur. |
| Ni les workers ni le serveur ne savent l'encoder | Conversion faite par Emby lui-même, comme sans le plugin. |

Une conversion est acceptée dès qu'un worker capable est connecté **ou** que le serveur sait encoder les tranches. Un PC allumé au milieu d'un épisode s'y joint en une dizaine de secondes. Si au moins un worker est associé mais qu'aucun n'est encore connecté (par exemple juste après un redémarrage du serveur), la conversion continue d'essayer de placer ses tranches sur les workers pendant 30 minutes avant de revenir à Emby, si le serveur ne peut pas encoder les tranches lui-même. Un worker fermé ou mis à jour en pleine tranche n'est pas considéré en échec : sa tranche reprend ailleurs et il reste accepté. Un worker écarté après un vrai échec est réessayé au bout de 5 minutes si aucune autre machine ne peut prendre le travail.

### 7.3 HDR vers SDR

Quand Emby convertit un film HDR en SDR, la conversion des couleurs se fait avec des filtres FFmpeg standard, de la même façon sur les workers et sur le serveur : toutes les tranches ont le même rendu. Si le serveur ne sait pas le faire, il laisse ces tranches aux workers. Les conversions HDR sont lourdes : un PC avec carte graphique aide beaucoup.

### 7.4 Et si un PC s'éteint pendant une conversion ?

Rien n'est perdu. La tranche interrompue reprend à son dernier morceau terminé (environ 30 secondes de vidéo) sur un autre worker, ou sur le serveur si aucun worker n'est libre pendant 20 secondes, puis revient plus tard sur un worker. Le fichier final est toujours vérifié à l'image près.

### 7.5 La vérification finale

Le serveur assemble les tranches avec la commande d'Emby (audio et sous-titres d'Emby), puis vérifie la durée totale et la durée de la vidéo à l'image près. Si la vérification échoue, le fichier est rejeté et Emby refait la conversion lui-même. Vous n'obtenez jamais, sans le savoir, un fichier auquel il manque des images.

### 7.6 Signalement automatique des échecs (anonyme)

Quand une conversion échoue (fichier rejeté par la vérification finale, worker en échec sur une tranche, conversion rendue à Emby), le plugin la signale par une issue sur le [dépôt GitHub](https://github.com/djdmg/EmbyWorkerTranscodePlugin/issues) du projet, pour que la cause soit trouvée et corrigée. Si le même problème est déjà signalé (même signature), il y ajoute un commentaire au lieu d'ouvrir une nouvelle issue.

- **Ce qui est envoyé** : les versions du plugin, d'Emby et des workers ; le système et l'architecture du serveur (ex. « Linux x64, Emby 4.10.2 ») ; les workers concernés, décrits par leur version et leur modèle de GPU (ex. « worker 0.7.14, NVIDIA GeForce RTX 4080 ») ; l'erreur exacte, sans aucun chemin ; les caractéristiques techniques de la source (conteneur, codec et profil vidéo, profondeur de couleur, résolution, images/s, durée, HDR, nombre et codecs des pistes audio) ; l'encodeur et les noms des filtres demandés par Emby ; le type de machine qui a encodé chaque tranche ; le résumé de l'assemblage et la ligne de contrôle.
- **Ce qui n'est jamais envoyé** : chemins, noms de fichiers, titres, noms de domaine, adresses IP, noms d'hôte ou de PC, noms des workers, noms d'utilisateur, identifiants Emby. La source apparaît sous un code anonyme (8 caractères) ; la ligne qui relie ce code au fichier n'est écrite que dans votre propre journal d'Emby (« failure reported to GitHub (anonymous source code … = … ) »), pour que vous sachiez de quel épisode il s'agit.
- **Limites** : au plus 5 envois par jour, jamais deux fois pour la même conversion. Les situations normales ne sont pas signalées (aucun worker capable connecté, conversion annulée dans Emby, redémarrage). L'envoi se fait en arrière-plan ; un problème de réseau est simplement ignoré et ne gêne jamais une conversion.
- **Désactiver** : décochez « Signaler automatiquement les échecs de conversion sur GitHub (anonyme) » sur la page du plugin (rubrique « Signalement des échecs »). L'état est affiché sous la case : « actif », « désactivé », ou « inactif : pas de jeton dans cette version » pour un plugin que vous avez compilé vous-même (seules les versions publiées sur GitHub peuvent envoyer).

---

## 8. Mise à jour, retour arrière et désinstallation

### 8.1 Mettre à jour le plugin

1. Téléchargez la nouvelle DLL et vérifiez son SHA-256 (section 3.1).
2. **Gardez une copie de la DLL actuelle** avant de la remplacer (sur Synology, la tâche d'installation le fait pour vous : `RemoteTranscode.EmbyPlugin.dll.before-<date>`).
3. Remplacez le fichier dans le dossier des plugins et redémarrez Emby (section 3.2).
4. Rechargez la page d'Emby (F5). Les workers associés sont conservés.

### 8.2 Revenir à la version précédente du plugin

Remettez l'ancienne DLL dans le dossier des plugins, à la place de la nouvelle, et redémarrez Emby. Sur Synology, voir le retour arrière décrit à la [section 3.2](#32-le-copier-dans-le-dossier-des-plugins-demby).

### 8.3 Mettre à jour un worker

Lancez le nouvel installeur sur le PC. Comme le worker est déjà installé, une petite fenêtre « Mise à jour de Emby Remote Worker » affiche la version installée et la nouvelle, avec un seul bouton « Mettre à jour » (« Réinstaller » pour une version identique). Le worker est fermé proprement (ses tâches reprennent ailleurs), les fichiers sont remplacés, puis le worker redémarre. Le dossier, les réglages, le démarrage avec Windows et l'association sont conservés. Cela prend quelques secondes. `--quiet` met à jour sans fenêtre.

Le plugin et le worker ont des numéros de version séparés. Installez si possible les deux depuis la même version publiée.

### 8.4 Arrêter temporairement le plugin

Cliquez sur « Désactiver » sur la page du plugin : effet immédiat, sans redémarrage.

### 8.5 Désinstaller le plugin

Tableau de bord → Plugins → « Transcodage distribué » → Désinstaller. Emby reprend immédiatement son propre ffmpeg (ou le ffmpeg personnalisé que vous aviez choisi auparavant).

Nettoyage complet, si vous ne comptez pas réinstaller : supprimez le dossier `remote-transcode` des données d'Emby (sous Synology `/var/packages/EmbyServer/var/remote-transcode`) et le fichier `plugins/configurations/RemoteTranscode.EmbyPlugin.xml`. Les workers devront alors être associés de nouveau.

Si vous avez supprimé la DLL à la main au lieu de désinstaller, Emby continue de fonctionner (tout part vers son propre ffmpeg). Pour revenir entièrement à l'état d'origine : Tableau de bord → Transcodage → décochez le chemin ffmpeg personnalisé.

### 8.6 Désinstaller un worker

Paramètres Windows → Applications → **Emby Remote Worker** → Désinstaller. Le programme se ferme, retire son démarrage automatique et ses raccourcis, puis supprime uniquement les fichiers qu'il a installés. Il propose aussi de supprimer ses données (`%LOCALAPPDATA%\EmbyRemoteWorker` : réglages, association, journaux) ; elles sont conservées par défaut pour qu'une réinstallation ne demande pas de nouvelle association. Révoquez ensuite le worker sur la page du plugin si vous ne comptez plus l'utiliser.

---

## 9. Dépannage et FAQ

### 9.1 Où sont les journaux ?

| Journal | Emplacement | Contenu |
|---|---|---|
| Worker, journal du jour | `%LOCALAPPDATA%\EmbyRemoteWorker\logs\worker-AAAAMMJJ.log` (onglet « Journaux », ou Aide → « Journaux… ») | Connexion, changements d'état et leur motif, tâches reçues ou refusées, encodeur utilisé, erreurs. |
| Worker, un par tâche | `%LOCALAPPDATA%\EmbyRemoteWorker\logs\job-<id>.log` | Sortie complète de FFmpeg pour cette tâche. |
| Installeur du worker | `%TEMP%\EmbyRemoteWorker-Setup.log` | Installations et mises à jour silencieuses, échecs. |
| Journal du serveur Emby | Tableau de bord → Journaux, fichier `embyserver.txt` (Synology : `/var/packages/EmbyServer/var/logs`) | Messages du plugin et du coordinateur (catégorie « RemoteTranscode ») : connexions et départs des workers, tâches, conversions. |
| Journaux de transcodage d'Emby | Tableau de bord → Journaux, fichiers `ffmpeg-transcode-*.txt` | Un par transcodage ; les lignes qui commencent par `remote-transcode:` expliquent chaque décision et chaque bascule. |

Pour partager les journaux d'un worker, utilisez « Exporter (7 derniers jours)… » : les secrets sont masqués. Les journaux d'Emby contiennent les jetons d'accès des clients : relisez-les avant de les partager.

### 9.2 Situations fréquentes

| Symptôme ou message | Signification | Que faire |
|---|---|---|
| Worker « indisponible » avec « CPU occupé par d'autres applications (…%) » / « GPU occupé… » / « mémoire saturée… » | D'autres programmes du PC dépassent les seuils. | Normal pendant que vous utilisez le PC. Sinon, relevez les seuils dans Paramètres. |
| « charge élevée (retour en cours) » | La charge est repassée sous les seuils ; le worker attend la temporisation (30 s). | Patienter. |
| « stabilisation avant disponibilité » | Toutes les conditions sont réunies ; période de stabilisation de 20 secondes. | Patienter. |
| « PC utilisé » | Mode automatique : clavier ou souris utilisés récemment. | Normal. |
| « hors plage horaire », « sur batterie », « désactivé par l'utilisateur » | Vos propres règles. Un PC libéré par « Libérer le PC » reste désactivé. | Recochez « Disponible », ou modifiez les règles. |
| Worker « hors ligne » | Pas connecté : code d'association invalide ou révoqué, certificat du serveur changé, proxy sans en-tête WebSocket, ou adresse bloquée après 10 échecs. | Lisez le journal du worker. Associez-le de nouveau. Vérifiez le proxy. Attendez 15 minutes en cas de blocage. |
| Association : « Code d'association invalide. » | Le code est incomplet ou mal copié. | Créez un nouveau code sur la page du plugin et collez-le en entier. |
| Note de conversion « conversion sur le serveur (aucun worker connecté capable de cette conversion) » | Aucun worker ne pouvait la faire et le serveur ne savait pas encoder les tranches lui-même : Emby l'a convertie seul. | Vérifiez qu'un worker est connecté et disponible, et ses « Encodeurs validés ». |
| « conversion sur le serveur (pas de réencodage vidéo (remux)) » | Un remux : il reste sur le serveur par principe. | Rien. Emby peut convertir au rythme du temps réel ; le réglage de conversion à pleine vitesse d'Emby l'accélère. |
| « résultat rejeté (…), conversion sur le serveur » | La vérification finale a échoué ; Emby refait la conversion. | Rien ; le fichier obtenu est correct. Si cela se répète, voir la ligne suivante. |
| Une conversion échoue à l'assemblage, avec ou sans worker | La fiche d'Emby est périmée (par exemple une piste audio qui n'existe plus après un remplacement du fichier). | Actualisez les métadonnées du média dans Emby (ou analysez la bibliothèque), puis relancez la conversion. |
| La page du plugin indique « Coordinateur intégré à Emby : arrêté (…) » ou « activé mais non appliqué » | Port déjà utilisé, programme relais non autorisé à s'exécuter sur ce serveur, ou réglage d'Emby introuvable. | Lisez la raison sur la page et dans `embyserver.txt`. Changez de port s'il est occupé. |
| La page du plugin ne s'ouvre pas dans l'appli Android d'Emby (« veuillez vous connecter directement à votre serveur local ») | Les applis n'exécutent pas les pages des plugins tiers. | Ouvrez Emby dans un navigateur web (fonctionne aussi sur téléphone). |
| Le lecteur web indique « aucun flux compatible », ou la vidéo est minuscule | Le lecteur web d'Emby a abaissé et mémorisé sa qualité maximale après des échecs. Sans rapport avec le plugin. | Dans Emby sur ce navigateur : Paramètres → Lecture → « Qualité de streaming maximale » sur Automatique, ou pendant la lecture : engrenage → Qualité → Automatique. |
| Lecture saccadée sur un worker | Worker trop lent en logiciel (portable basse consommation), ou connexion internet du PC trop lente. | Branchez le portable sur secteur, activez « Lectures : encodage par le GPU », ou réservez ce PC aux conversions. |
| SmartScreen ou l'antivirus bloque l'installeur | Programme non signé. | Voir section 5.2. |
| La page du plugin paraît ancienne après une mise à jour | Cache du navigateur. | Rechargez avec F5. |

### 9.3 Le problème vient-il du plugin ?

1. Sur la page du plugin : le coordinateur est-il « en service », le plugin « activé » ? Les workers sont-ils connectés et disponibles ?
2. Cliquez sur « Désactiver ». Si le problème persiste, il ne vient pas du plugin.

### 9.4 FAQ

**Faut-il un worker pour que le plugin serve ?** Sans worker, Emby fonctionne comme d'habitude. Le plugin n'aide que si au moins un PC est disponible.

**Une lecture utilise-t-elle plusieurs PC à la fois ?** Non. Une lecture tourne sur un seul PC à la fois (ou sur le serveur). Les conversions utilisent tous les PC.

**Un port est-il ouvert sur mes PC ?** Non. Les workers se connectent au serveur ; rien n'écoute sur le PC.

**Mon PC peut-il servir pendant que je joue ?** En mode automatique, ou avec les seuils de charge, le PC s'efface de lui-même. Vous pouvez aussi décocher « Disponible » ou utiliser « Libérer le PC maintenant » à tout moment.

**Que se passe-t-il quand le PC se met en veille ?** Le serveur s'en aperçoit en 15 secondes environ et reprend au point exact. La lecture peut marquer une courte pause si le lecteur avait peu d'avance.

---

## 10. Limites connues

- **Plateforme testée** : seul Emby 4.10.2 sur Synology (Linux x64). Emby pour Windows, Emby sous Docker et Linux arm64 sont prévus mais pas encore testés. Au démarrage, le plugin vérifie que son programme relais s'exécute ; sinon, Emby garde son propre ffmpeg et la page du plugin en donne la raison.
- **Réglage non officiel d'Emby** : le plugin utilise le réglage « chemin ffmpeg personnalisé » d'Emby, présenté par Emby comme non pris en charge. Une future version d'Emby pourrait le modifier. Le plugin le détecte et ne touche alors à rien.
- **Page du plugin** : uniquement dans un navigateur web, pas dans les applis Emby (Android…).
- **Non signé** : l'installeur et le worker ne sont pas signés. SmartScreen avertit, et certains antivirus (surtout en entreprise) peuvent les bloquer.
- **Encodeurs mélangés** : quand des PC d'encodeurs différents (processeur, Intel, NVIDIA…) se partagent une conversion, le fichier contient des parties issues d'encodeurs différents. FFmpeg le décode sans erreur et une télévision l'a lu sans défaut, mais un lecteur inhabituel pourrait, en théorie, mal le supporter.
- **Workers par internet** : le débit dépend du débit montant de votre connexion et du débit descendant du PC.
- **Un PC par lecture** : les segments d'une lecture ne sont pas répartis entre plusieurs PC.
- **Sous-titres incrustés** : les conversions avec sous-titres incrustés sont faites par le serveur.
- **Remux** : ils restent sur le serveur et suivent la vitesse d'Emby.
- **Début de film** : quand une lecture démarrée tout au début passe sur une autre machine, les horodatages reculent de 80 ms au raccord. Aucun effet constaté dans Chrome.
