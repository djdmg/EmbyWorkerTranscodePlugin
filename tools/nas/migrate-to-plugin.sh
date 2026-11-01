#!/bin/sh
# HISTORIQUE (migration unique faite le 27/10/2026) : passage de l installation manuelle (coordinateur lance a la main, relais copie par SSH) au plugin autonome 0.6. Pour installer ou mettre a jour le plugin aujourd hui : install-plugin.sh.
# À lancer avec sudo, à un moment où le redémarrage d'Emby ne gêne personne :
#   sudo sh /volume4/docker/emby-remote/migrate-to-plugin.sh
# Retour arrière : sudo sh /volume4/docker/emby-remote/rollback-plugin.sh <dossier de sauvegarde affiché ci-dessous>
set -e
OLD=/volume4/docker/emby-remote
VAR=/var/packages/EmbyServer/var
NEW=$VAR/remote-transcode
CONF=$VAR/plugins/configurations/RemoteTranscode.EmbyPlugin.xml
DLL=$VAR/plugins/RemoteTranscode.EmbyPlugin.dll
BACKUP=$OLD/backup-$(date +%Y%m%d-%H%M%S)

[ "$(id -u)" = 0 ] || { echo "à lancer avec sudo"; exit 1; }
[ -f "$OLD/plugin/RemoteTranscode.EmbyPlugin-0.6.dll" ] || { echo "nouvelle DLL absente"; exit 1; }

echo "1. Sauvegarde dans $BACKUP"
mkdir -p "$BACKUP"
cp -a "$DLL" "$BACKUP/" 2>/dev/null || true
cp -a "$CONF" "$BACKUP/" 2>/dev/null || true
cp -a "$OLD/data" "$BACKUP/data"

echo "2. Reprise des données (workers associés, certificat, secrets) dans $NEW"
mkdir -p "$NEW/encode"
for f in coordinator.pfx coordinator.pem workers.json shim.secret plugin.secret admin.secret encodes.json; do
  [ -f "$OLD/data/$f" ] && cp -a "$OLD/data/$f" "$NEW/"
done
chown -R emby:emby "$NEW"

echo "3. Réglages du plugin : workers via le reverse proxy https://coordinator.example.com -> http://127.0.0.1:8097"
mkdir -p "$(dirname "$CONF")"
cat > "$CONF" <<EOF
<?xml version="1.0"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <WorkerUrl>http://127.0.0.1:8097</WorkerUrl>
  <PublicUrl>https://coordinator.example.com</PublicUrl>
  <ShimPort>8098</ShimPort>
</PluginConfiguration>
EOF
chown emby:emby "$CONF"

echo "4. Arrêt du coordinateur lancé à la main (libère les ports 8097/8098)"
"$OLD/coordinator.sh" stop || true

echo "5. Installation du plugin 0.6 et redémarrage d'Emby"
cp "$OLD/plugin/RemoteTranscode.EmbyPlugin-0.6.dll" "$DLL"
chown emby:emby "$DLL"
/usr/syno/bin/synopkg restart EmbyServer

echo
echo "Terminé. Sauvegarde : $BACKUP"
echo "Retour arrière : sudo sh $OLD/rollback-plugin.sh $BACKUP"
