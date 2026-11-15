#!/bin/sh
# Turn a clip's jittered transparent captures ($SIZZLE_WORK/<clip-id>/frames/f%04d_0..3.png) into deliverables:
#   <out>/sizzle_<clip-id>_t01.mp4    the whole take, 1920x1080, brand background, no super (the editor retimes it)
#   <out>/sizzle_<clip-id>_edit.mp4   the cut used in the reel: EDIT_LEN s from EDIT_START, optional fade + super
#   <out>/sizzle_<clip-id>_still.png  the last frame
# Usage: encode.sh <clip-id> [out-dir]      (out-dir default: ~/Downloads/freppuccino-sizzle/<clip-id>)
# Options (env vars):
#   THEME=latte|espresso   background gradient (default latte)
#   EDIT_START=0.5         seconds into the take where the edit starts
#   EDIT_LEN=4             edit length in seconds (shot list: 3, 4 or 5)
#   FADE_IN=0              fade up from black at the start of the edit, seconds (clip 1 uses 0.5)
#   SUPER_TEXT=""          super text; empty = no super. Shown SUPER_IN -> SUPER_OUT with 0.3 s fades.
#   SUPER_IN=1.0  SUPER_OUT=(EDIT_LEN - 0.5)
#   FPS=60
#   BADGE_MASK=0           1 = clear the FREP MODE badge corner (only for captures from builds without CleanCapture)
set -e
CLIP="$1"
[ -n "$CLIP" ] || { echo "usage: encode.sh <clip-id> [out-dir]"; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${2:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/$CLIP}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
[ -x "$FF" ] || { echo "ffmpeg not found at $FF; run setup-tools.sh"; exit 1; }
FR="$WORK/$CLIP/frames"
THEME="${THEME:-latte}"; FPS="${FPS:-60}"; EDIT_START="${EDIT_START:-0.5}"; EDIT_LEN="${EDIT_LEN:-4}"; FADE_IN="${FADE_IN:-0}"
SUPER_IN="${SUPER_IN:-1.0}"; SUPER_OUT="${SUPER_OUT:-$(node -e "console.log($EDIT_LEN - 0.5)")}"
mkdir -p "$OUT"

N=$(ls "$FR" | grep -cE '^f[0-9]{4}_0\.png$')
[ "$N" -gt 0 ] || { echo "no frames in $FR (expected f0000_0.png ...)"; exit 1; }
SECS=$(node -e "console.log(Math.ceil($N / $FPS) + 1)")
LAST=$((N - 1))
START=$(node -e "console.log(Math.round($EDIT_START * $FPS))")
EDIT_FRAMES=$(node -e "console.log(Math.round($EDIT_LEN * $FPS))")
[ $((START + EDIT_FRAMES)) -le "$N" ] || { echo "edit ($EDIT_START s + $EDIT_LEN s) runs past the take ($N frames)"; exit 1; }

case "$THEME" in
  espresso) TOP='#2a1e19'; BOT='#160f0d' ;;
  *)        TOP='#fff8ef'; BOT='#ead8c3' ;;
esac
BGPNG="$WORK/bg-$THEME-1080.png"
[ -f "$BGPNG" ] || node "$HERE/bg.mjs" 1920 1080 "$TOP" "$BOT" "$BGPNG" > /dev/null

seq_inputs() { # $1 = first frame
  for j in 0 1 2 3; do printf -- '-framerate %s -start_number %s -i %s/f%%04d_%s.png ' "$FPS" "$1" "$FR" "$j"; done
}
# Average the 4 jittered captures with PREMULTIPLIED alpha (straight-alpha averaging leaves dark/light fringes) and
# lay them over the gradient. Clean captures (AiCapture.CleanCapture) have no badge; BADGE_MASK=1 makes the top-left
# corner transparent for older builds (gyroid / displacements branches). It's off by default because it punches a
# hole in any subject that reaches that corner.
# Never `split` a looped still in this graph: it buffers frames until ffmpeg runs out of memory. Bound loops with -t.
MASK=""; [ "${BADGE_MASK:-0}" = "1" ] && MASK=",drawbox=x=0:y=0:w=160:h=80:color=black@0.0:t=fill:replace=1"
AVG="[0:v]format=rgba,premultiply=inplace=1[a];[1:v]format=rgba,premultiply=inplace=1[b];[2:v]format=rgba,premultiply=inplace=1[c];[3:v]format=rgba,premultiply=inplace=1[d];[a][b][c][d]mix=inputs=4,unpremultiply=inplace=1${MASK}[fg];[4:v][fg]overlay=shortest=1:format=auto"
BG="-loop 1 -framerate $FPS -t $SECS -i $BGPNG"
X264="-c:v libx264 -preset slow -r $FPS -movflags +faststart"

"$FF" -hide_banner -loglevel error -y $(seq_inputs 0) $BG -filter_complex "$AVG,format=yuv420p[v]" -map "[v]" \
  $X264 -crf 14 "$OUT/sizzle_${CLIP}_t01.mp4"

FADE=""; [ "$FADE_IN" != "0" ] && FADE=",fade=t=in:st=0:d=$FADE_IN"
if [ -n "$SUPER_TEXT" ]; then
  SUP="$WORK/$CLIP/super.png"
  sh "$HERE/super.sh" "$SUPER_TEXT" "$THEME" "$SUP" > /dev/null
  SO=$(node -e "console.log($SUPER_OUT - 0.3)")
  "$FF" -hide_banner -loglevel error -y $(seq_inputs "$START") $BG -loop 1 -framerate "$FPS" -t "$EDIT_LEN" -i "$SUP" \
    -filter_complex "$AVG$FADE[base];[5:v]format=rgba,fade=t=in:st=$SUPER_IN:d=0.3:alpha=1,fade=t=out:st=$SO:d=0.3:alpha=1[sup];[base][sup]overlay=0:0:shortest=1,format=yuv420p[v]" \
    -map "[v]" -frames:v "$EDIT_FRAMES" $X264 -crf 16 "$OUT/sizzle_${CLIP}_edit.mp4"
else
  "$FF" -hide_banner -loglevel error -y $(seq_inputs "$START") $BG -filter_complex "$AVG$FADE,format=yuv420p[v]" -map "[v]" \
    -frames:v "$EDIT_FRAMES" $X264 -crf 16 "$OUT/sizzle_${CLIP}_edit.mp4"
fi

"$FF" -hide_banner -loglevel error -y $(seq_inputs "$LAST") $BG -filter_complex "$AVG" -frames:v 1 "$OUT/sizzle_${CLIP}_still.png"

# Contact sheet of the edit (6 frames) for a quick visual check.
"$FF" -hide_banner -loglevel error -y -i "$OUT/sizzle_${CLIP}_edit.mp4" \
  -vf "select='not(mod(n\,$((EDIT_FRAMES / 6))))',scale=640:360,tile=3x2" -frames:v 1 -fps_mode vfr "$WORK/$CLIP/contact.png"
echo "take: $N frames -> $OUT/sizzle_${CLIP}_t01.mp4"
echo "edit: $EDIT_FRAMES frames from frame $START -> $OUT/sizzle_${CLIP}_edit.mp4"
echo "contact sheet: $WORK/$CLIP/contact.png"
