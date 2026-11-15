#!/bin/sh
# Build a clip script from the template's HARNESS plus your own SCENE + ANIMATE body, then compile-check it.
#   new-clip.sh <clip-id> <seconds> <body.cs> <out.cs> "<one-line description>"
# <body.cs> holds only the indented SCENE and ANIMATE blocks (from "// ───── SCENE" up to the end of the frame loop);
# see the SCENE / ANIMATE blocks in clip01-hook.cs .. clip04-seam.cs. Everything else is copied from clip01-hook.cs,
# so harness fixes there carry over when a clip is rebuilt.
set -e
# CLEARS_SCENE=0: the body works on a file opened by open-3dm.cs (no clear_scene), so the saved-document guard is off.
ID="$1"; SECS="$2"; BODY="$3"; OUT="$4"; DESC="$5"
[ -n "$ID" ] && [ -n "$SECS" ] && [ -f "$BODY" ] && [ -n "$OUT" ] || { echo 'usage: new-clip.sh <clip-id> <seconds> <body.cs> <out.cs> "<description>"'; exit 2; }
HERE="$(cd "$(dirname "$0")" && pwd)"
T="$HERE/clip01-hook.cs"
CS=true; [ "${CLEARS_SCENE:-1}" = "0" ] && CS=false
grep -q 'SCENE (per clip)' "$T" && grep -q 'HARNESS (end)' "$T" || { echo "template markers missing in $T"; exit 1; }
{
  printf '%s\n' "// Sizzle reel clip $ID. $DESC" "// Built by new-clip.sh from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh."
  # template from its first `using` line up to (not including) the SCENE marker, with this clip's id and length
  awk '/^using /{on=1} /SCENE \(per clip\)/{exit} on' "$T" |
    sed "s/^const string ClipId = \"[^\"]*\";/const string ClipId = \"$ID\";/; s/^const double Seconds = [0-9.]*; /const double Seconds = $SECS; /; s/^const bool ClearsScene = true; /const bool ClearsScene = $CS; /"
  cat "$BODY"
  # template from the HARNESS (end) marker to the end of the file
  awk '/HARNESS \(end\)/{on=1} on' "$T"
} > "$OUT"
grep -q "const string ClipId = \"$ID\";" "$OUT" || { echo "ClipId not set in $OUT"; exit 1; }
sh "$HERE/check-script.sh" "$OUT"
