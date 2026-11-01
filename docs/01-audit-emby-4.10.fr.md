# Audit de l'intégration Emby — jalon 1

English version: [01-audit-emby-4.10.md](01-audit-emby-4.10.md)

Date : 2026-10-26. Toutes les observations ci-dessous ont été faites sur le serveur de test, sans modification.

## Environnement testé

| Élément | Valeur constatée |
|---|---|
| Emby Server | 4.10.2.0 (paquet Synology `emby-server-synology72`, 4.10.2.0-724100200) |
| NAS | Synology DS1522+ « le NAS de test », AMD Ryzen Embedded R1600 (4 threads), 8 Go RAM, DSM 7.4.1 |
| GPU NAS | aucun (`/dev/dri` absent) → tout transcodage local est logiciel |
| ffmpeg Emby | fork `5.1-emby_2023_06_25_p6` (softworkz / Emby LLC), `/var/packages/EmbyServer/target/bin/ffmpeg` |
| Lancement | `target/bin/emby-server` → `EmbyServer -ffmpeg … -ffprobe … -ffdetect …`, utilisateur `emby` |
| Données | `/var/packages/EmbyServer/var` → `/volume4/@appdata/EmbyServer` |
| Dossier transcodage | `/var/packages/EmbyServer/var/transcoding-temp` |
| Port Emby interne | 18096 (HTTP), exposé via reverse proxy HTTPS `emby.example.com` |
| Bibliothèques | `/volume4/Film`, `/volume4/Series`, `/volume4/Emissions`, `/volume4/Documentaire` (partagées aussi en SMB) |
| Worker de dev | Windows 11 Enterprise 26200, Intel Core Ultra 5 225U (iGPU Intel), FFmpeg 9.0.2 gyan.dev |

## 1. Interception de la demande de transcodage

**Aucune API du SDK ne permet de remplacer le transcodeur.** `IMediaEncoder`, `IFfmpegManager` exposent la configuration et quelques opérations, pas de point d'extension pour exécuter un transcodage ailleurs.

**Point d'extension retenu : le chemin ffmpeg personnalisé d'Emby.**
Emby 4.10 contient une configuration `ffmpeg` (`FfmpegConfiguration`) avec `UseCustomEncoderPath` / `CustomEncoderPath`, modifiable par l'API admin :

- `GET /Encoding/FfmpegOptions` et `POST /Encoding/FfmpegOptions` (rôle Admin), stockée dans `config/ffmpeg.xml`.
- Prise en compte **à chaud** (`FfmpegManager.FfmpegConfiguration` est remplacé immédiatement) : **pas de redémarrage**.
- **Ne modifie aucun fichier du paquet** : survit aux mises à jour d'Emby ; retour arrière = remettre `UseCustomEncoderPath=false`.
- ffprobe et ffdetect ont des réglages séparés : ils restent sur les binaires d'Emby.

Limites et sensibilité aux mises à jour :

- Emby affiche pour ce réglage : « temporary settings for development. Using alternate ffmpeg versions is not supported ». Ce n'est donc **pas une extension documentée** ; elle peut disparaître ou changer.
- Le relais doit imiter le protocole du ffmpeg d'Emby (voir ci-dessous). Ce protocole est interne et peut évoluer à chaque version d'Emby → test de non-régression à refaire à chaque mise à jour, et repli automatique sur le ffmpeg d'origine en cas d'anomalie.

## 2. Protocole entre Emby et son ffmpeg (observé et décompilé)

Commande réelle d'un transcodage HLS (session de test, film « Tanguy, le retour ») :

```
ffmpeg -loglevel +timing -y -print_graphs_file ".../logs/ffmpeg-transcode-<id>_1graph.txt"
  -copyts -start_at_zero -f mp4 -c:v:0 h264 -noautorotate -i "/volume4/Film/Tanguy, le retour.mp4"
  -filter_complex "[0:0]scale@f1=width=720:height=388,subtitles@f2=filename='/volume4/Film/Tanguy, le retour.fre.forced.vtt':fontsdir='/var/packages/EmbyServer/var/fonts':charenc=utf-8[f2_out0]"
  -map [f2_out0] -map 0:1 -sn -c:v:0 libx264 … -c:a:0 aac …
  -f segment -segment_format mpegts -segment_list ".../transcoding-temp/F55394/F55394.m3u8" -segment_list_type m3u8
  -segment_time 00:00:03.000 -segment_start_number 0 -individual_header_trailer 0 -write_header_trailer 0
  -segment_write_temp 1 ".../transcoding-temp/F55394/F55394_%d.ts"
```

Ce qu'Emby attend du processus :

| Canal | Contenu | Conséquence pour le relais |
|---|---|---|
| stderr | lignes horodatées `HH:mm:ss.fff …` (`-loglevel +timing`, propre au fork) | le relais réécrit les lignes avec ce préfixe |
| stderr | `SegmentComplete=video:0 Index=N Start=… End=… Duration=… … filename=X_N.ts` après renommage du `.tmp` | **indispensable** : Emby ne sert un segment qu'après ce message (`TranscodingStateReader`) |
| stderr | progression `elapsed=… frame=… fps=… time=… throttle=off speed=…` | réémise à partir de la progression du worker |
| stdin | `?` → aide listant les commandes ; `t` + valeur → limitation de débit ; `q` → arrêt | `q` relayé au worker ; `t` non annoncé dans le prototype (Emby désactive alors la limitation) |
| fichiers | segments `.ts` écrits en `.tmp` puis renommés (`-segment_write_temp`, propre au fork) | le relais écrit en `.tmp` puis renomme |
| code retour | 0 si arrêt par `q` | relayé |

Options propres au fork, refusées par FFmpeg standard : `-loglevel +timing`, `-segment_write_temp`. Le reste de la commande a été exécuté tel quel par FFmpeg 9.0.2 sur le worker (test local réussi, 4 segments produits).

## 3. Déplacement dans la vidéo et arrêt

Observé : à l'arrêt (`DELETE /Videos/ActiveEncodings`), Emby envoie `q` sur stdin (« Stopping ffmpeg process with q command »), puis tue le processus si besoin. Un déplacement relance un nouveau ffmpeg avec `-ss` et `-segment_start_number`. Le relais n'a donc qu'à traduire « fin de mon processus / `q` » en « annulation du job distant » ; chaque relance est un nouveau job.

## 4. Choix pour les médias et les flux

- **Entrée** : le worker lit les fichiers par **HTTP authentifié servi par le coordinateur**, limité aux fichiers cités dans la commande du job (jeton par job). Cela couvre aussi les fichiers hors partages SMB (sous-titres extraits dans `cache/temp`, polices dans `var/fonts`). SMB avec correspondance de chemins reste une optimisation possible.
- **Sortie** : le worker produit les segments localement, les envoie au coordinateur qui les transmet au relais ; le relais (qui tourne sous l'utilisateur `emby`) les écrit dans `transcoding-temp`. **Emby continue de servir les segments lui-même** : contrôles d'accès, HTTPS, reverse proxy et clients inchangés.
- **Direct Play / Direct Stream** : n'appellent pas ffmpeg pour la vidéo, donc ne passent pas par le relais.

## 5. Contrôles d'accès

Le client ne parle jamais au worker. Les autorisations restent celles d'Emby (PlaybackInfo, URL HLS avec jeton). Le worker n'a accès qu'aux fichiers du job en cours, avec un jeton éphémère, et ne reçoit jamais de commande arbitraire : le coordinateur transmet une liste d'arguments validée (encodeurs, filtres, chemins) et le worker la revalide.

## 6. Points de vigilance identifiés

- Le NAS n'a que 8 Go de RAM, déjà très utilisés (T-Pot, etc.) : le coordinateur doit rester léger.
- Les journaux d'Emby contiennent les jetons d'accès en clair (comportement d'Emby, à garder en tête pour l'export des journaux).
- Les arguments d'Emby sont générés pour un transcodage **logiciel** (le NAS n'a pas de GPU). Par défaut, le worker utilise **le même encodeur logiciel qu'Emby**, pour que les bascules entre worker et serveur restent invisibles. L'encodage par le GPU du worker est une **option** de ses paramètres, séparée pour les conversions et pour les lectures : le worker traduit alors l'encodeur (`libx264` → `h264_nvenc`/`h264_qsv`/`h264_amf`) en conservant le débit maximal et la taille de GOP ; le profil d'Emby est laissé au choix de l'encodeur matériel (Emby demande `main10` à x265 même pour une image 8 bits).

  *Mise à jour du 29/10/2026 : ce document décrit l'audit initial. Le fonctionnement actuel (démarrage des lectures sur le serveur, préchargement, conversions en tranches) est décrit dans le `README.md` et dans `final/Documentation-Transcodage-distribue.pdf`.*
- Un worker qui disparaît en cours de lecture : le relais relance le job sur un autre worker ou en local à partir du dernier segment complet (`-ss` + `-segment_start_number`). Le résultat côté client est à valider (micro-coupure probable, pas de migration transparente promise).

## 7. Résultats du prototype (2026-10-26)

| Test | Résultat |
|---|---|
| Lecture via Emby (session API dédiée, film « Tanguy, le retour ») | ✅ segments servis par Emby, produits sur le worker (h264_qsv, sous-titres incrustés) |
| Déplacement à 10:00 | ✅ Emby arrête le job (`q`) et en relance un au segment 200 sur le worker |
| Arrêt de la lecture | ✅ `q` relayé, ffmpeg du worker arrêté (code 0), dossier du job supprimé |
| Média non retenu pour le déport | ✅ ffmpeg local d'Emby |
| Coordinateur arrêté | ✅ ffmpeg local d'Emby |
| Worker tué en pleine lecture | ✅ reprise locale au segment suivant, segments continus (200→244) |
| Horodatages des segments | ✅ identiques au ffmpeg natif d'Emby |
| Détection matérielle | ✅ NVENC/AMF listés par FFmpeg mais refusés au test réel, QSV validé |

Latence du premier segment : 11 à 17 s sur le PC de développement, dont environ 5 s pour le seul lancement de
`ffmpeg.exe` (logiciel de sécurité d'entreprise). À mesurer sur un PC personnel.

Emby a été remis dans sa configuration d'origine après les tests (`UseCustomEncoderPath=false`).
