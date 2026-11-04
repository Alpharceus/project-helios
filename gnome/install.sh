#!/bin/sh
# Install/update the GNOME edition of Helios for this user.
#   gnome/install.sh            install + enable
#   gnome/install.sh --remove   disable + uninstall
set -e
UUID=helios@alpharceus.github.io
DEST="$HOME/.local/share/gnome-shell/extensions/$UUID"
SRC="$(cd "$(dirname "$0")" && pwd)"

if [ "$1" = "--remove" ]; then
  gnome-extensions disable "$UUID" 2>/dev/null || true
  rm -rf "$DEST"
  echo "removed $UUID"
  exit 0
fi

mkdir -p "$DEST"
cp "$SRC/metadata.json" "$SRC/extension.js" "$SRC/sensors.js" "$SRC/stylesheet.css" "$DEST/"
echo "installed -> $DEST"
if gnome-extensions info "$UUID" >/dev/null 2>&1; then
  gnome-extensions enable "$UUID"
  echo "enabled. If an older version was already running, log out and back in to load this one."
else
  # Not yet known to the running shell: mark enabled so it loads at next login.
  cur=$(gsettings get org.gnome.shell enabled-extensions)
  case "$cur" in *"$UUID"*) ;; "@as []") gsettings set org.gnome.shell enabled-extensions "['$UUID']" ;;
    *) gsettings set org.gnome.shell enabled-extensions "$(echo "$cur" | sed "s/]\$/, '$UUID']/")" ;; esac
  echo "marked enabled; log out and back in for GNOME Shell to load it."
fi
