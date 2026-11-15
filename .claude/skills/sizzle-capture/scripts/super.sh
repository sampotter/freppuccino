#!/bin/sh
# Render a super as a transparent 1920x1080 PNG with headless Edge (ffmpeg's drawtext can't pick the variable
# font's SemiBold weight, the browser can).
#   super.sh "<text>" <latte|espresso> <out.png>
# Edge notes: an isolated --user-data-dir is required (with the user's Edge open, headless otherwise hangs forever);
# --disable-gpu avoids a GPU-process crash that silently skips the screenshot.
set -e
TEXT="$1"; THEME="${2:-latte}"; OUT="$3"
[ -n "$TEXT" ] && [ -n "$OUT" ] || { echo 'usage: super.sh "<text>" <latte|espresso> <out.png>'; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
mkdir -p "$WORK/_super"
cp "$HERE/super.html" "$WORK/_super/"
cp "$REPO/brand-kit/fonts/Fredoka.ttf" "$WORK/_super/"
ENC=$(node -e "console.log(encodeURIComponent(process.argv[1]))" "$TEXT")
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
rm -f "${OUT:?}"
# Judge success by the file, not Edge's console output; the first launch with a fresh profile can be flaky, so retry.
for attempt in 1 2 3; do
  timeout 60 "$EDGE" --headless=new --disable-gpu --user-data-dir="$WORK/_edge-profile" --no-first-run --hide-scrollbars \
    --allow-file-access-from-files --default-background-color=00000000 --window-size=1920,1080 --virtual-time-budget=5000 \
    --screenshot="$(cygpath -m "$OUT")" "file:///$WORK/_super/super.html#text=$ENC&theme=$THEME" > /dev/null 2>&1 || true
  [ -s "$OUT" ] && { echo "wrote $OUT"; exit 0; }
done
echo "Edge did not write $OUT"; exit 1
