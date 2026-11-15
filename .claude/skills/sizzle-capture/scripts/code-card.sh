#!/bin/sh
# Render the clip 13 code card (code-card.html) for each wings value as transparent 1920x1080 PNGs:
#   code-card.sh <out-dir>     -> <out-dir>/card_w2.png, card_w3.png, card_w4.png
set -e
OUT="$1"; [ -n "$OUT" ] || { echo "usage: code-card.sh <out-dir>"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
mkdir -p "$OUT" "$WORK/_card"
cp "$HERE/code-card.html" "$REPO/brand-kit/fonts/JetBrainsMono-Variable.woff2" "$REPO/brand-kit/fonts/Fredoka.ttf" "$WORK/_card/"
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
for w in 2 3 4; do
  png="$(cygpath -m "$OUT")/card_w$w.png"; rm -f "$png"
  for attempt in 1 2 3; do   # judge by the file; Edge's first launch with a fresh profile can be flaky
    timeout 60 "$EDGE" --headless=new --disable-gpu --user-data-dir="$WORK/_edge-profile" --no-first-run --hide-scrollbars \
      --allow-file-access-from-files --default-background-color=00000000 --window-size=1920,1080 --virtual-time-budget=5000 \
      --screenshot="$png" "file:///$WORK/_card/code-card.html#wings=$w" > /dev/null 2>&1 || true
    [ -s "$png" ] && break
  done
  [ -s "$png" ] || { echo "Edge did not write $png"; exit 1; }
done
echo "cards -> $OUT"
