#!/bin/sh
# Clip 16 encode: pass A (shader, frames/) over the background, then a left-to-right wipe to the same shot with the
# mesh wireframe (frames_mesh/) laid on top, so the wires sweep across the cup.
#   encode-mesh.sh <clip-id> [out-dir]      env: EDIT_START=0.5 EDIT_LEN=3 WIPE_AT=0.9 WIPE_LEN=1.3 (seconds in the edit)
set -e
CLIP="$1"; [ -n "$CLIP" ] || { echo "usage: encode-mesh.sh <clip-id> [out-dir]"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${2:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/$CLIP}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
FPS=60; EDIT_START="${EDIT_START:-0.5}"; EDIT_LEN="${EDIT_LEN:-3}"; WIPE_AT="${WIPE_AT:-0.9}"; WIPE_LEN="${WIPE_LEN:-1.3}"
A="$WORK/$CLIP/frames"; B="$WORK/$CLIP/frames_mesh"
BGPNG="$WORK/bg-latte-1080.png"
[ -f "$BGPNG" ] || node "$HERE/bg.mjs" 1920 1080 '#fff8ef' '#ead8c3' "$BGPNG" > /dev/null
mkdir -p "$OUT"
START=$(node -e "console.log(Math.round($EDIT_START * $FPS))")
NF=$(node -e "console.log(Math.round($EDIT_LEN * $FPS))")
inputs() { for j in 0 1 2 3; do printf -- '-framerate %s -start_number %s -i %s/f%%04d_%s.png ' "$FPS" "$START" "$1" "$j"; done; }
avg() { # $1..$4 = input labels, $5 = output label
  printf '%s' "[$1]format=rgba,premultiply=inplace=1[$5a];[$2]format=rgba,premultiply=inplace=1[$5b];[$3]format=rgba,premultiply=inplace=1[$5c];[$4]format=rgba,premultiply=inplace=1[$5d];[$5a][$5b][$5c][$5d]mix=inputs=4,unpremultiply=inplace=1[$5]"
}
G="$(avg 0:v 1:v 2:v 3:v sh);$(avg 4:v 5:v 6:v 7:v wi)"
G="$G;[8:v][sh]overlay=shortest=1:format=auto,split[plain][p2];[p2][wi]overlay=format=auto,trim=start=$WIPE_AT,setpts=PTS-STARTPTS[wired]"
G="$G;[plain][wired]xfade=transition=wiperight:duration=$WIPE_LEN:offset=$WIPE_AT,format=yuv420p[v]"
"$FF" -hide_banner -loglevel error -y $(inputs "$A") $(inputs "$B") -loop 1 -framerate $FPS -t $((EDIT_LEN + 2)) -i "$BGPNG" \
  -filter_complex "$G" -map "[v]" -frames:v "$NF" -c:v libx264 -preset slow -crf 16 -r $FPS -movflags +faststart "$OUT/sizzle_${CLIP}_edit.mp4"
"$FF" -hide_banner -loglevel error -y -i "$OUT/sizzle_${CLIP}_edit.mp4" -vf "select='not(mod(n\,$((NF / 6))))',scale=640:360,tile=3x2" -frames:v 1 -fps_mode vfr "$WORK/$CLIP/contact.png"
echo "edit -> $OUT/sizzle_${CLIP}_edit.mp4 ; contact sheet: $WORK/$CLIP/contact.png"
