#!/bin/sh
# Installe (ou met à jour) le plugin RemoteTranscode sur un Synology, puis redémarre Emby.
#   sudo sh install-plugin.sh /volume4/docker/emby-remote/plugin/RemoteTranscode.EmbyPlugin-0.7.8.dll [sha256]
# - Une seule exécution à la fois (verrou) : deux redémarrages simultanés de DSM se bloquent mutuellement.
# - Avec l'empreinte SHA-256 en second argument, rien n'est modifié si la DLL ne correspond pas.
# - Le plugin installé avant est gardé à côté de la nouvelle DLL (installed-before-<date>.dll) pour un retour arrière.
# - Le redémarrage ne peut pas rester bloqué : arrêt borné dans le temps, puis démarrage réessayé.
SRC="$1"
EXPECTED="$2"
DST=/var/packages/EmbyServer/var/plugins/RemoteTranscode.EmbyPlugin.dll
SYNOPKG=/usr/syno/bin/synopkg

[ "$(id -u)" = 0 ] || { echo "A lancer avec sudo"; exit 1; }
[ -n "$SRC" ] && [ -f "$SRC" ] || { echo "Usage : sudo sh $0 <chemin de la DLL> [sha256]"; exit 1; }
exec 9>/tmp/remote-transcode-install.lock
flock -n 9 || { echo "Une installation est deja en cours : attendre sa fin."; exit 1; }
if [ -n "$EXPECTED" ] && [ "$(sha256sum "$SRC" | cut -d' ' -f1)" != "$EXPECTED" ]; then
    echo "Empreinte incorrecte, rien n'est modifie"
    exit 1
fi

if [ -f "$DST" ]; then
    cp -p "$DST" "$(dirname "$SRC")/installed-before-$(date +%Y%m%d-%H%M%S).dll"
fi
cp "$SRC" "$DST"
chown emby:emby "$DST"
echo "Plugin copie depuis $SRC, redemarrage d'Emby..."

if ! timeout 180 $SYNOPKG stop EmbyServer; then
    echo "Arret d'Emby bloque : arret force de la commande."
    pkill -f "systemctl stop pkgctl-EmbyServer" 2>/dev/null
fi
for i in 1 2 3; do
    if timeout 180 $SYNOPKG start EmbyServer | grep -q '"success":true'; then
        echo "Plugin installe, Emby redemarre."
        exit 0
    fi
    echo "Demarrage refuse, nouvel essai dans 10 s..."
    sleep 10
done
echo "Emby n'a pas redemarre : lancer '$SYNOPKG start EmbyServer' ou le Centre de paquets."
exit 1
