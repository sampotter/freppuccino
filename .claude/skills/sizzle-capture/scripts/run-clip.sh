#!/bin/sh
# Run a clip capture script inside the running Rhino and wait for it to finish.
#   run-clip.sh <clip-script.cs> <clip-id> [test|full]
# test = 2 frames (first and last pose) for framing checks; full = every frame, 4 jittered captures each.
# `rhinocode script` returns immediately and never reports errors, so the clip script writes done.txt or error.txt
# into $SIZZLE_WORK/<clip-id>/ and this waits for one of them.
set -e
SCRIPT="$1"; CLIP="$2"; MODE="${3:-full}"
[ -f "$SCRIPT" ] && [ -n "$CLIP" ] || { echo "usage: run-clip.sh <clip-script.cs> <clip-id> [test|full]"; exit 2; }
# The script writes to the folder named by its own `const string ClipId`; a different <clip-id> here means waiting on
# the wrong folder forever (and the script wiping another clip's frames).
SCRIPT_ID=$(grep -oE 'const string ClipId = "[^"]+"' "$SCRIPT" | sed 's/.*= "//; s/"$//')
[ "$SCRIPT_ID" = "$CLIP" ] || { echo "clip-id '$CLIP' doesn't match ClipId \"$SCRIPT_ID\" in $SCRIPT"; exit 2; }
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
DIR="$WORK/$CLIP"
mkdir -p "$DIR"
printf '{"mode":"%s"}\n' "$MODE" > "$DIR/config.json"
rm -f "${DIR:?}/done.txt" "${DIR:?}/error.txt"
HERE="$(cd "$(dirname "$0")" && pwd)"
sh "$HERE/check-script.sh" "$SCRIPT"   # a compile error inside Rhino is silent; catch it here
RHINOCODE="/c/Program Files/Rhino 9/System/RhinoCode.exe"
"$RHINOCODE" list | grep -q rhinocode_remotepipe || { echo "No Rhino with a script server; run launch-rhino.ps1 first."; exit 1; }
"$RHINOCODE" script "$(cygpath -aw "$SCRIPT")"   # must be an ABSOLUTE path: a relative one is silently ignored
# The script only starts once Rhino's window is in the foreground: bring it forward briefly, then give focus back.
# Windows sometimes refuses the focus change (the nudge then exits 1), so never abort on it: retry, and nudge again
# at 20, 40 and 60 s in case the script still hasn't started (harmless if it's already running).
nudge() { powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -aw "$HERE/nudge-rhino.ps1")" > /dev/null 2>&1 || { sleep 2; powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -aw "$HERE/nudge-rhino.ps1")" > /dev/null 2>&1; } || true; }
nudge
# Full 7 s clips (420 frames x 4 captures) take ~3-4 min.
i=0
while [ $i -lt 600 ]; do
  [ -f "$DIR/done.txt" ] && { cat "$DIR/done.txt"; exit 0; }
  [ -f "$DIR/error.txt" ] && { echo "clip script failed:"; cat "$DIR/error.txt"; exit 1; }
  sleep 2; i=$((i + 1))
  case $i in 10|20|30) nudge ;; esac
done
echo "timed out after 20 min; check Rhino's command history"; exit 1
