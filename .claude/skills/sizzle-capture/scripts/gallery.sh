#!/bin/sh
# Stills of saved .3dm files, one per file, composited on the brand background.
#   gallery.sh <out-dir> <file.3dm>[|#rrggbb] ...
# Each file is opened by one script run (open-3dm.cs) and captured by the next (gallery-stills.cs): a capture in the
# same run as the open shows bare wireframes. Every run briefly brings Rhino to the front.
set -e
OUT="$1"; shift
[ -n "$OUT" ] && [ $# -gt 0 ] || { echo "usage: gallery.sh <out-dir> <file.3dm>[|#rrggbb] ..."; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
mkdir -p "$WORK/open3dm" "$WORK/gallery/frames"
rm -f "$WORK"/gallery/frames/*.png
NAMES=""
i=0
for spec in "$@"; do
  file="${spec%%|*}"
  colour=""; case "$spec" in *"|"*) colour="|${spec#*|}" ;; esac
  printf '%s%s\n' "$(cygpath -aw "$file")" "$colour" > "$WORK/open3dm/request.txt"
  sh "$HERE/run-clip.sh" "$HERE/open-3dm.cs" open3dm test
  echo "$i" > "$WORK/gallery/request.txt"
  sh "$HERE/run-clip.sh" "$HERE/gallery-stills.cs" gallery test
  NAMES="$NAMES $(basename "$file" .3dm)"
  i=$((i + 1))
done
# shellcheck disable=SC2086
sh "$HERE/stills.sh" gallery "$OUT" $NAMES
