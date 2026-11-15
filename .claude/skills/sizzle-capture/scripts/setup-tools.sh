#!/bin/sh
# One-time: install ffmpeg (static build, via npm) into the sizzle work folder. No system install, no admin.
set -e
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
mkdir -p "$WORK/tools"
cd "$WORK/tools"
[ -f package.json ] || npm init -y > /dev/null
npm install ffmpeg-static@5.2.0 --no-audit --no-fund 2>&1 | tail -1
node -e "console.log('ffmpeg:', require('ffmpeg-static'))"
