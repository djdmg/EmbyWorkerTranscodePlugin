#!/bin/sh
# HISTORIQUE : retour a l installation manuelle d avant le plugin 0.6. Pour revenir a une version precedente du plugin : sudo sh install-plugin.sh <installed-before-...dll>.
#   sudo sh /volume4/docker/emby-remote/rollback-plugin.sh /volume4/docker/emby-remote/backup-AAAAMMJJ-HHMMSS
set -e
OLD=/volume4/docker/emby-remote
VAR=/var/packages/EmbyServer/var
BACKUP=$1
[ "$(id -u)" = 0 ] || { echo "à lancer avec sudo"; exit 1; }
[ -n "$BACKUP" ] && [ -f "$BACKUP/RemoteTranscode.EmbyPlugin.dll" ] || { echo "dossier de sauvegarde invalide"; exit 1; }

cp "$BACKUP/RemoteTranscode.EmbyPlugin.dll" "$VAR/plugins/RemoteTranscode.EmbyPlugin.dll"
chown emby:emby "$VAR/plugins/RemoteTranscode.EmbyPlugin.dll"
if [ -f "$BACKUP/RemoteTranscode.EmbyPlugin.xml" ]; then
  cp "$BACKUP/RemoteTranscode.EmbyPlugin.xml" "$VAR/plugins/configurations/"
else
  rm -f "$VAR/plugins/configurations/RemoteTranscode.EmbyPlugin.xml"
fi
/usr/syno/bin/synopkg stop EmbyServer
su adminSSH -s /bin/sh -c "$OLD/coordinator.sh start" || "$OLD/coordinator.sh" start
/usr/syno/bin/synopkg start EmbyServer
echo "Installation manuelle rétablie. Emby démarre avec son ffmpeg d'origine : réactivez si besoin depuis la page du plugin."
