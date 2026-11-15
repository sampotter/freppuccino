#!/bin/sh
# Clip 17, the end card: the brand lockup on Latte, a slow cross-fade to the Espresso version, a gentle push in.
#   end-card.sh [out-dir]     (default ~/Downloads/freppuccino-sizzle/c17_end) -> sizzle_c17_end_edit.mp4 (3 s, 60 fps)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../../.." && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${1:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/c17_end}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
mkdir -p "$OUT" "$WORK/_endcard"
cp "$HERE/end-card.html" "$REPO/brand-kit/logo/frepuccino-lockup.svg" "$REPO/brand-kit/logo/frepuccino-lockup-dark.svg" "$WORK/_endcard/"
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
for th in latte espresso; do
  png="$WORK/_endcard/$th.png"; rm -f "$png"
  for attempt in 1 2 3; do
    timeout 60 "$EDGE" --headless=new --disable-gpu --user-data-dir="$WORK/_edge-profile" --no-first-run --hide-scrollbars \
      --allow-file-access-from-files --window-size=1920,1080 --virtual-time-budget=5000 \
      --screenshot="$png" "file:///$WORK/_endcard/end-card.html#theme=$th" > /dev/null 2>&1 || true
    [ -s "$png" ] && break
  done
  [ -s "$png" ] || { echo "Edge did not write $png"; exit 1; }
done
# 0-1.0 s Latte, 1.0-2.2 s cross-fade to Espresso, hold; whole card scales 1.00 -> 1.04 (rendered at 2x, then down).
"$FF" -hide_banner -loglevel error -y -loop 1 -framerate 60 -t 3 -i "$WORK/_endcard/latte.png" -loop 1 -framerate 60 -t 3 -i "$WORK/_endcard/espresso.png" \
  -filter_complex "[0][1]xfade=transition=fade:duration=1.2:offset=1.0,scale=3840:2160,zoompan=z='1+0.04*on/180':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s=1920x1080:fps=60,format=yuv420p[v]" \
  -map "[v]" -frames:v 180 -c:v libx264 -preset slow -crf 16 -r 60 -movflags +faststart "$OUT/sizzle_c17_end_edit.mp4"
"$FF" -hide_banner -loglevel error -y -i "$OUT/sizzle_c17_end_edit.mp4" -vf "select='eq(n\,0)+eq(n\,95)+eq(n\,179)',scale=640:-1,tile=3x1" -frames:v 1 -fps_mode vfr "$WORK/_endcard/contact.png"
echo "end card -> $OUT/sizzle_c17_end_edit.mp4"
