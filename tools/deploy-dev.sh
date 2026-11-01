#!/bin/sh
# Rebuilds and redeploys the prototype. Usage: deploy-dev.sh [shim] [coordinator] [worker]
set -e
DOTNET="/c/Program Files/dotnet/dotnet.exe"
SRC=/c/emby/src; OUT=/c/emby/artifacts/nas; BASE=/volume4/docker/emby-remote
upload() { # local file, remote path
  ssh -o BatchMode=yes nas "cat > $2.new && chmod 755 $2.new && mv $2.new $2" < "$1" 2>/dev/null
}
for target in "$@"; do
  case "$target" in
    shim)
      # Fresh output folder each time: antivirus scans sometimes keep the previous bundle locked.
      D="$OUT/shim-$(date +%s)"
      "$DOTNET" publish "$SRC/RemoteTranscode.Shim" -c Release -r linux-x64 -o "$D" >/dev/null
      upload "$D/remote-ffmpeg" "$BASE/shim/remote-ffmpeg"; echo "shim deployed from $D" ;;
    coordinator)
      D="$OUT/coordinator-$(date +%s)"
      "$DOTNET" publish "$SRC/RemoteTranscode.Coordinator" -c Release -r linux-x64 -o "$D" >/dev/null
      ssh -o BatchMode=yes nas "$BASE/coordinator.sh stop" 2>/dev/null
      upload "$D/coordinator" "$BASE/coordinator/coordinator"
      ssh -o BatchMode=yes nas "$BASE/coordinator.sh start" 2>/dev/null; echo "coordinator deployed from $D" ;;
    worker)
      taskkill //F //IM EmbyRemoteWorker.exe >/dev/null 2>&1 || true
      "$DOTNET" build "$SRC/RemoteTranscode.Worker" -c Release | grep -E " error " || true
      echo "worker built" ;;
  esac
done
