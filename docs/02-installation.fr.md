# Installation, mise à jour et désinstallation

English version: [02-installation.md](02-installation.md)

## Serveur Emby : le plugin seul

Le plugin `RemoteTranscode.EmbyPlugin.dll` contient tout ce qu'il faut côté serveur :

- le **coordinateur**, qui tourne dans le processus d'Emby (Emby 4.10 embarque ASP.NET Core) ;
- le **relais ffmpeg** pour Linux x64, Linux arm64, Linux Alpine/Docker (musl) x64 et Windows x64, extrait au démarrage.

Aucun chemin n'est écrit en dur. À chaque démarrage d'Emby, le plugin demande à Emby :

- son ffmpeg et son ffprobe : ligne de commande d'Emby, sinon sa configuration ;
- les variables d'environnement qu'Emby donne à ffmpeg ;
- le dossier de transcodage ;
- les dossiers des bibliothèques, relus toutes les 5 minutes : ce sont les seuls fichiers que les workers peuvent lire ;
- son dossier de données.

### Installer

1. Copier `RemoteTranscode.EmbyPlugin.dll` dans le dossier `plugins` du dossier de données d'Emby :
   - Synology : `/var/packages/EmbyServer/var/plugins` ;
   - Docker : `/config/plugins` ;
   - Windows : `%AppData%\Emby-Server\programdata\plugins`.
2. Redémarrer Emby.
3. Recharger la page d'Emby (F5), puis ouvrir Tableau de bord → **Transcodage distribué** :
   - **Connexion des workers** : par défaut `https://0.0.0.0:8097` (TLS direct, certificat généré et vérifié par les workers). Derrière un reverse proxy HTTPS : `http://127.0.0.1:8097` et l'adresse publique `https://…` du proxy. Avec Docker, publier le port choisi (`-p 8097:8097`).
   - **Adresse annoncée** (facultatif, TLS direct) : nom ou IP que les codes d'association donnent aux workers. Par défaut, c'est l'adresse réseau du serveur ; les interfaces Docker et VPN sont ignorées.
   - **Associer un worker** : le code affiché se colle dans le worker (Paramètres → Code d'association).
   - **Activer**.

Les données du plugin (certificat, workers associés, secrets, morceaux de conversion) sont dans `<données Emby>/remote-transcode`. Les secrets ne sont lisibles que par l'utilisateur qui fait tourner Emby.

Au démarrage, le plugin vérifie que le relais s'exécute vraiment sur ce serveur (par exemple sur un dossier monté sans droit d'exécution). Sinon, Emby garde son ffmpeg et la page du plugin affiche la raison.

#### Synology : script d'installation

Le dépôt fournit `tools/nas/install-plugin.sh`. Avec SSH :

```
sudo sh /volume4/docker/emby-remote/plugin/install-plugin.sh /volume4/docker/emby-remote/plugin/RemoteTranscode.EmbyPlugin-<version>.dll <sha256>
```

- l'empreinte SHA-256 (facultative) est vérifiée avant toute modification ;
- le plugin installé avant est gardé à côté (`installed-before-<date>.dll`) : pour revenir en arrière, relancer le script avec ce fichier ;
- **une seule exécution à la fois** (verrou) : lancer deux fois le script, ou deux redémarrages de DSM en même temps, les bloquait mutuellement ;
- l'arrêt d'Emby est borné à 3 minutes et le démarrage réessayé trois fois ; en dernier recours : `sudo /usr/syno/bin/synopkg start EmbyServer` ou le Centre de paquets.

#### Synology sans SSH : Planificateur de tâches DSM

Une tâche DSM **générique**, créée une fois pour toutes, installe n'importe quelle version : elle recolle la DLL à partir de morceaux, vérifie son empreinte et lance `install-plugin.sh`. Les versions suivantes ne demandent pas de modifier la tâche, donc pas de mot de passe DSM.

**Une seule fois :**

1. Avec File Station, déposer le script `install-plugin.sh` du dépôt (`tools/nas`) dans un dossier partagé, par exemple `docker/emby-remote/plugin/`.
2. Panneau de configuration → Planificateur de tâches → Créer → Tâche déclenchée → Script défini par l'utilisateur. Nom « Installer plugin RemoteTranscode », utilisateur **root**, événement Démarrage, case **Activé décochée** (la tâche ne s'exécute qu'à la demande). Le script recolle `plugin.part0` à `plugin.part5` dans l'ordre, lance `install-plugin.sh` avec l'empreinte lue dans `plugin.sha256` et écrit le résultat dans un journal `install-<date>.log`. Script de la tâche en service sur NAS de test (une seule ligne) :
   ```
   D=/volume4/docker/emby-remote/plugin; cat $D/plugin.part0 $D/plugin.part1 $D/plugin.part2 $D/plugin.part3 $D/plugin.part4 $D/plugin.part5 > $D/RemoteTranscode.EmbyPlugin-new.dll && sh $D/install-plugin.sh $D/RemoteTranscode.EmbyPlugin-new.dll $(cat $D/plugin.sha256) > $D/install-$(date +%Y%m%d-%H%M%S).log 2>&1
   ```
   Il attend exactement 6 morceaux : avec des morceaux de 9 Mo, cela couvre une DLL de 46 à 54 Mo (elle fait environ 48 Mo). Si la DLL change de taille au point d'en demander un nombre différent, adapter la liste des `plugin.partN` dans la tâche (ce qui redemande le mot de passe DSM).

**À chaque version :**

1. Découper la DLL en 6 morceaux `plugin.part0`..`plugin.part5` de 9 Mo (le dernier plus petit ; ils passent par File Station ou son API) et écrire son empreinte SHA-256 dans `plugin.sha256`.
2. Déposer ces fichiers dans `docker/emby-remote/plugin/`, à la place des précédents (si un morceau manque ou ne correspond pas, l'empreinte le détecte et rien n'est installé).
3. Sélectionner la tâche, puis **Exécuter** une seule fois, et attendre son résultat avant de la relancer. Emby est indisponible environ 45 s. Lire le journal `install-<date>.log` : « Plugin installe, Emby redemarre. » en cas de succès, « Empreinte incorrecte, rien n'est modifie » si les morceaux ne correspondent pas.

#### Ouverture vers internet

Les workers qui ne sont pas sur le réseau local passent par un reverse proxy HTTPS (DSM : Portail de connexion → Proxy inversé). Les tentatives d'authentification ratées sont limitées par adresse : 10 échecs en 10 minutes bloquent l'adresse pendant 15 minutes. Derrière le proxy, l'adresse réelle est lue dans `X-Real-IP`, que le proxy de DSM renseigne.

### Mise à jour d'Emby

Rien à faire. Au démarrage, le plugin redétecte le ffmpeg d'Emby, réécrit l'aiguillage et le relais, puis réapplique l'interception.

Emby enregistre le réglage « ffmpeg personnalisé » dans sa base. Si une version future d'Emby ne chargeait plus le plugin, Emby continuerait donc d'appeler l'aiguillage :

- l'aiguillage reste fonctionnel sans plugin : tout part vers le ffmpeg d'Emby, puisque le relais ne trouve plus de coordinateur ;
- si la mise à jour a déplacé le ffmpeg d'Emby, l'aiguillage et le relais le retrouvent dans la ligne de commande d'Emby (`-ffmpeg`, Linux) ;
- pour revenir entièrement à l'origine : Tableau de bord → Transcodage → chemin ffmpeg personnalisé décoché, ou désinstaller le plugin.

Si le coordinateur ne peut pas démarrer (port occupé…), le plugin laisse Emby sur son ffmpeg et affiche la raison sur sa page.

Garde-fous en fonctionnement :

- Une lecture ou une conversion qu'aucun worker ne peut prendre est faite par le ffmpeg d'Emby, comme sans plugin.
- Des arguments inconnus d'une nouvelle version d'Emby font refuser la tâche, qui est alors faite localement.
- Coupe-circuit : un fichier `disabled` dans `<données Emby>/remote-transcode/bin` renvoie tout vers le ffmpeg d'Emby.

### Mise à jour du plugin

Remplacer la DLL et redémarrer Emby. Les workers associés sont conservés.

### Désinstaller

Tableau de bord → Plugins → Transcodage distribué → Désinstaller. Emby reprend immédiatement son propre ffmpeg, sans redémarrage. Le dossier `<données Emby>/remote-transcode` reste, et peut être supprimé.

## Worker Windows

Lancer `EmbyRemoteWorker-Setup-<version>.exe` : installation sans droits administrateur, icône dans la zone de notification. Coller ensuite le code d'association obtenu sur la page du plugin. La version installée est affichée dans le titre de la fenêtre et dans **Aide → À propos**.

Les fichiers vont dans un dossier `EmbyRemoteWorker` dédié. La liste des fichiers installés est notée dans `install-manifest.txt`.

**Mise à jour.** Relancer simplement le nouvel installeur : s'il trouve une installation, il propose une **mise à jour** (une seule fenêtre, version installée → nouvelle version) qui garde le dossier, le démarrage avec Windows, le raccourci, la configuration et l'association. Il demande au worker en cours de **se fermer proprement** (comme « Quitter » : ses tâches reprennent sur le serveur ou un autre worker) et ne force l'arrêt qu'en dernier recours (version antérieure à 0.7.5, ou pas de fermeture en 20 s). Il s'arrête sans rien modifier si un fichier reste verrouillé. `--full` affiche l'assistant complet, `--quiet` installe sans fenêtre.

**Mise à jour automatique.** Option « Mises à jour » dans les paramètres du worker, active par défaut et affichée juste sous l'association. Le worker consulte la dernière publication du dépôt GitHub du projet (fixé dans son code) au démarrage puis environ toutes les 6 h, et ne retient un installeur que si son nom a exactement la forme attendue et si sa version est strictement plus récente que la sienne. Il le télécharge avec son `.sha256` depuis cette publication, vérifie l'empreinte et le lance avec `--quiet` quand il n'a eu aucune tâche depuis 2 minutes, en refusant les nouvelles pendant ce temps ; le worker redémarre ensuite tout seul. Une version en échec n'est pas retentée avant 24 h. Décochée, le worker ne contacte plus du tout GitHub. Le serveur Emby ne peut pas choisir ce qu'un worker installe : il peut seulement lui demander de vérifier plus tôt. Les installeurs ne sont pas signés, la confiance repose donc sur le compte et le dépôt GitHub du projet : qui les contrôlerait pourrait faire exécuter son programme par tous les workers ayant cette option. Décochez-la si vous préférez installer vous-même chaque version. Un worker antérieur à cette fonction doit être mis à jour une fois à la main. Inactive pour une copie lancée ailleurs que depuis son dossier d'installation.

**Désinstallation** : paramètres Windows → Applications → Emby Remote Worker. C'est le worker lui-même qui désinstalle (`EmbyRemoteWorker.exe --uninstall`, `--quiet` sans fenêtre) : il ne supprime que les fichiers du manifeste. Les versions avant 0.7.5 laissaient un `Uninstall.exe` (copie de l'installeur) : il est supprimé à la mise à jour.

**Antivirus.** L'installeur et le worker ne sont pas signés. Depuis 0.7.5, l'installeur évite les gestes que les antivirus jugent suspects (copie de lui-même, arrêt forcé d'un processus) ; Kaspersky avait détecté l'installeur 0.7.4 (« PDM:Trojan.Win32.Generic », détection comportementale). Seule une signature de code donne une garantie ; sur un poste d'entreprise, le service informatique peut aussi l'approuver.

**Signer.** `tools\build-installer.ps1` signe et horodate (SHA-256, DigiCert par défaut) le worker puis l'installeur dès qu'on lui donne un certificat de signature de code : `-CertThumbprint <empreinte>` (magasin de l'utilisateur, par exemple un certificat sur clé matérielle) ou `-PfxPath <fichier.pfx>`. Une signature non valide arrête la construction. Le mécanisme a été vérifié avec un certificat de test (signature et horodatage posés, seule la confiance manque). Il faut un certificat émis par une autorité reconnue : certificat OV ou EV d'un fournisseur commercial, ou service de signature dans le nuage (Azure Trusted Signing, par exemple), dont l'outil signe les mêmes fichiers. C'est une possibilité, pas une étape prévue : pour un usage personnel, aucun certificat n'a été acheté et les livrables ne sont pas signés.

La configuration et les journaux sont dans `%LOCALAPPDATA%\EmbyRemoteWorker`. `worker.json` est écrit de façon atomique, avec une copie `worker.json.bak` utilisée si le fichier principal est illisible.

## Construire

- `tools\build-plugin.ps1` : relais pour chaque plateforme, puis plugin. Sortie : `artifacts\plugin`.
- `tools\package-worker.ps1` : worker et FFmpeg. Sortie : `artifacts\worker-package` (jamais le dossier d'où tourne un worker : son exécutable verrouillé resterait à l'ancienne version ; le script vérifie la version produite).
- `tools\build-installer.ps1` : installeur du worker, signé si un certificat est donné (`-CertThumbprint` ou `-PfxPath`). Sortie : `artifacts\installer`.
