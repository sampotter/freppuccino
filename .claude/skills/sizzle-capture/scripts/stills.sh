#!/bin/sh
# Composite single stills from jittered captures ($SIZZLE_WORK/<clip-id>/frames/fNNNN_0..3.png) onto the brand
# background, one PNG per frame index, plus a contact sheet. Used after gallery-stills.cs.
#   stills.sh <clip-id> <out-dir> [name1 name2 ...]     (names default to still_0000 ...)
#   THEME=latte|espresso
set -e
CLIP="$1"; OUT="$2"; shift 2 || { echo "usage: stills.sh <clip-id> <out-dir> [names...]"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
FR="$WORK/$CLIP/frames"
THEME="${THEME:-latte}"
case "$THEME" in espresso) TOP='#2a1e19'; BOT='#160f0d' ;; *) TOP='#fff8ef'; BOT='#ead8c3' ;; esac
BGPNG="$WORK/bg-$THEME-1080.png"
[ -f "$BGPNG" ] || node "$HERE/bg.mjs" 1920 1080 "$TOP" "$BOT" "$BGPNG" > /dev/null
mkdir -p "$OUT"
# Clean captures (AiCapture.CleanCapture) have no badge. BADGE_MASK=1 clears the top-left corner for older builds
# (gyroid / displacements branches); off by default because it punches a hole in a subject that reaches the corner.
MASK=""; [ "${BADGE_MASK:-0}" = "1" ] && MASK=",drawbox=x=0:y=0:w=160:h=80:color=black@0.0:t=fill:replace=1"
AVG="[0:v]format=rgba,premultiply=inplace=1[a];[1:v]format=rgba,premultiply=inplace=1[b];[2:v]format=rgba,premultiply=inplace=1[c];[3:v]format=rgba,premultiply=inplace=1[d];[a][b][c][d]mix=inputs=4,unpremultiply=inplace=1${MASK}[fg];[4:v][fg]overlay=format=auto"
N=$(ls "$FR" | grep -cE '^f[0-9]{4}_0\.png$')
LIST=""
i=0
while [ $i -lt "$N" ]; do
  NAME="$1"; [ -n "$NAME" ] && shift || NAME=$(printf 'still_%04d' $i)
  IDX=$(printf '%04d' $i)
  "$FF" -hide_banner -loglevel error -y -i "$FR/f${IDX}_0.png" -i "$FR/f${IDX}_1.png" -i "$FR/f${IDX}_2.png" -i "$FR/f${IDX}_3.png" \
    -i "$BGPNG" -filter_complex "$AVG" -frames:v 1 "$OUT/$NAME.png"
  LIST="$LIST -i $OUT/$NAME.png"
  i=$((i + 1))
done
echo "$N stills -> $OUT"
