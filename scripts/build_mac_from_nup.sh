#!/bin/bash
# Build a NUP for the Mac host and write <build folder>/run_game.sh.
# Usage: build_mac_from_nup.sh <game.nup> [build_ipa_from_nsp.py options]
# Example: build_mac_from_nup.sh /Volumes/Games/game.nup --work-dir ~/Games/game_build --allow-other-metal
set -eu

if [ "$#" -lt 1 ]; then
    echo "usage: $0 <game.nup> [--work-dir FOLDER] [build options]" >&2
    exit 2
fi

nup=$1
shift
if [ ! -f "$nup" ]; then
    echo "not a NUP file: $nup" >&2
    exit 2
fi

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "${PYTHON:-python3}" "$here/build_ipa_from_nsp.py" "$nup" "$@" --platform mac --mac-shortcut
