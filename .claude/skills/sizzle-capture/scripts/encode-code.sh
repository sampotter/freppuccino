#!/bin/sh
# Clip 13 encode: the normal encode (with the super), then the code card over the left 40%, switching
# card_w2 -> card_w3 -> card_w4 when wings steps (take 1.0 s and 2.0 s, i.e. edit 0.5 s and 1.5 s at EDIT_START 0.5).
#   encode-code.sh [clip-id] [out-dir]    (default c13_code). Cards: code-card.sh <work>/<clip>/cards
set -e
CLIP="${1:-c13_code}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
OUT="${2:-$(cygpath -m "$USERPROFILE")/Downloads/freppuccino-sizzle/$CLIP}"
FF="${FFMPEG:-$WORK/tools/node_modules/ffmpeg-static/ffmpeg.exe}"
CARDS="$WORK/$CLIP/cards"
[ -s "$CARDS/card_w4.png" ] || sh "$HERE/code-card.sh" "$CARDS"
EDIT_START=0.5 EDIT_LEN=3 SUPER_TEXT="Write your own." SUPER_IN=0.4 SUPER_OUT=2.75 sh "$HERE/encode.sh" "$CLIP" "$OUT" > /dev/null
E="$OUT/sizzle_${CLIP}_edit.mp4"
"$FF" -hide_banner -loglevel error -y -i "$E" -loop 1 -t 3 -i "$CARDS/card_w2.png" -loop 1 -t 3 -i "$CARDS/card_w3.png" -loop 1 -t 3 -i "$CARDS/card_w4.png" \
  -filter_complex "[0:v][1:v]overlay=enable='lt(t,0.5)'[a];[a][2:v]overlay=enable='between(t,0.5,1.5)'[b];[b][3:v]overlay=enable='gt(t,1.5)',format=yuv420p[v]" \
  -map "[v]" -frames:v 180 -c:v libx264 -preset slow -crf 16 -r 60 -movflags +faststart "$OUT/sizzle_${CLIP}_edit_card.mp4"
mv -f "$OUT/sizzle_${CLIP}_edit_card.mp4" "$E"
"$FF" -hide_banner -loglevel error -y -i "$E" -vf "select='not(mod(n\,30))',scale=640:360,tile=3x2" -frames:v 1 -fps_mode vfr "$WORK/$CLIP/contact.png"
echo "edit (with code card) -> $E ; contact sheet: $WORK/$CLIP/contact.png"
