---
name: sizzle-capture
description: Render sizzle-reel clips of Freppuccino straight from Rhino 9 (scripted, frame by frame) and encode them with the brand background, supers and fades. Load before making or re-making any clip for the 1-minute sizzle reel, or any scripted video/still capture of the shader viewport.
---
# Sizzle reel capture

The plan (shot list, sign-up, viewport and colour standard, edit notes) lives in the team doc
**Freppuccino Sizzle Reel: Production Plan**: https://claude.ai/artifact/LkEvHTPQugrYsqcRJ4Ztfg . Read the clip's row
in the shot list and its "Per-clip setup" entry first. This skill is how to *produce* a clip by script.

Clips are rendered, not screen-recorded: a C# script inside Rhino builds the scene through the plugin's AI tool API,
steps the animation at 60 fps and saves a transparent capture per frame; ffmpeg adds the background, badge removal,
fade and super. Exact timing, no cursor, repeatable. Clip 01 took ~4 min to render and ~1 min to encode this way.

## Quick start (Windows, from the repo root, Git Bash)

```sh
S=.claude/skills/sizzle-capture/scripts
sh $S/setup-tools.sh                                             # once: ffmpeg into %LOCALAPPDATA%/freppuccino-sizzle/tools
powershell -ExecutionPolicy Bypass -File $S/launch-rhino.ps1     # build plugin, start Rhino 9 + script server, wait
# new clip: write only the SCENE + ANIMATE blocks (copy them from clip02..04 as a start), then assemble + compile-check:
sh $S/new-clip.sh c05_styles 4.5 my-body.cs $S/clip05-styles.cs "Blend styles switch on the beat (clip 5)."
W="$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle"; mkdir -p "$W/open3dm"
echo NEW > "$W/open3dm/request.txt" && sh $S/run-clip.sh $S/open-3dm.cs open3dm   # empty doc (clip scripts clear the scene)
sh $S/run-clip.sh $S/clip05-styles.cs c05_styles test           # 2 poses (first, last) -> check framing
sh $S/stills.sh c05_styles "$W/c05_styles/test" first last       # composite them to look at (one image per pose)
sh $S/run-clip.sh $S/clip05-styles.cs c05_styles full           # every frame
EDIT_START=0.5 EDIT_LEN=4 SUPER_TEXT="..." sh $S/encode.sh c05_styles   # -> ~/Downloads/freppuccino-sizzle/c05_styles/
```

Encode settings used so far (the edit window should catch the change, not the handles):

| Clip | Script | Take | Encode |
| --- | --- | --- | --- |
| 1 hook | `clip01-hook.cs` | 7 s | `FADE_IN=0.5 SUPER_TEXT="Implicit geometry. Live in Rhino."` (EDIT_START 0.5, EDIT_LEN 4) |
| 2 primitives pop | `clip02-pop.cs` | 4 s | `EDIT_START=0.25 EDIT_LEN=3` |
| 3 scoop | `clip03-scoop.cs` | 4.5 s | `EDIT_START=1.0 EDIT_LEN=3` |
| 4 colour seam | `clip04-seam.cs` | 4.5 s | `EDIT_START=0.9 EDIT_LEN=3` |
| 6 zebra | `clip06-zebra.cs` | 4.5 s | `EDIT_START=0.5 EDIT_LEN=3` |
| 7 twist | `clip07-twist.cs` | 5 s | `EDIT_START=0.5 EDIT_LEN=4 SUPER_TEXT="Twist. Bend. Repeat."` |
| 8 colonnade | `clip08-colonnade.cs` | 5 s | `EDIT_START=0.5 EDIT_LEN=4` |
| 11 gyroid (FFunction, Espresso) | `clip11-gyroid.cs` after `open-3dm` gyroid.3dm\|#e0954a | 5.5 s | `THEME=espresso EDIT_START=0.3 EDIT_LEN=5 SUPER_TEXT="Lattices, live." SUPER_IN=1.5` |
| 13 code (FFunction) | `clip13-code.cs` after `open-3dm` scherk_tower.3dm\|#e0954a | 4 s | `encode-code.sh` (encode + super + code card) |
| 13b gallery (FFunction) | `clip13b-nautilus/vase/tet.cs`, each after its `open-3dm` | 3 s each | each `EDIT_START=1.0 EDIT_LEN=1`, then concatenated |
| 16 mesh | `clip16-mesh.cs` | 4.5 s | `encode-mesh.sh c16_mesh` (two passes + wipe) |
| 17 end card | no Rhino | 3 s | `end-card.sh` |

Not scriptable: clips 14 and 15 (the AI agent and Image to AI) need the AI panel on screen and an OpenRouter key;
screen-record them. Clips 5, 9, 10, 12 need the gyroid / displacements branches (`BADGE_MASK=1` there).

Look at `%LOCALAPPDATA%/freppuccino-sizzle/<clip>/frames/f0000_0.png` after a test run and at `<clip>/contact.png`
after encoding (6 frames of the edit) before calling a clip done. Clip 01 is the worked example:
`FADE_IN=0.5 SUPER_TEXT="Implicit geometry. Live in Rhino." sh $S/encode.sh c01_hook`.

## Scripts (`scripts/`)

| File | What it does |
| --- | --- |
| `launch-rhino.ps1` | `dotnet build -c Debug`, starts Rhino 9 with `RHINO_PACKAGE_DIRS` + `FREPPUCCINO_AUTOSTART=1` and `/runscript="_ScriptEditor"`, waits for `rhinocode list`. `-NoBuild` to skip the build. Refuses to build while Rhino runs (it locks the .rhp). |
| `clip01-hook.cs` | Clip 01, and the template: HARNESS (leave alone) + SCENE + ANIMATE. Refuses to run on a saved document (it clears the scene). |
| `clip02-pop.cs`, `clip03-scoop.cs`, `clip04-seam.cs` | Clips 2-4 (see the table above), built with `new-clip.sh`. |
| `new-clip.sh` | Assembles a clip script: template HARNESS (found by its marker comments) + your SCENE/ANIMATE body, sets ClipId and Seconds, compile-checks. `CLEARS_SCENE=0` for bodies that work on a file opened by `open-3dm.cs`. |
| `clip06..clip16-*.cs` | The rest of the main-branch clips (table above). |
| `code-card.sh` + `code-card.html`, `encode-code.sh` | Clip 13's brand-styled code panel (the real `param` lines, the animated one highlighted) laid over the left 40%, switching with the param. Stands in for the panel's code view. |
| `encode-mesh.sh` | Clip 16: shader pass + wireframe pass, wiped left to right. |
| `end-card.sh` + `end-card.html` | Clip 17: lockup on Latte, cross-fade to Espresso, slow push in. No Rhino. |
| `open-3dm.cs` | Opens `$SIZZLE_WORK/open3dm/request.txt` (`<path>[|#colour]`, or `NEW` for an empty document) in its own run. |
| `check-script.sh` | Compiles a clip script against Rhino's RhinoCommon. `run-clip.sh` calls it. |
| `run-clip.sh` | check, `rhinocode script <abs path>`, nudge Rhino to the foreground, wait for `done.txt` / `error.txt`. |
| `nudge-rhino.ps1` | Brings Rhino forward ~4 s so it picks the script up, then gives focus back. |
| `encode.sh` | Averages the 4 jittered captures, clears the badge, lays over the gradient; writes `_t01.mp4` (whole take), `_edit.mp4` (the cut, fade + super), `_still.png`, and a contact sheet. Options are env vars, see its header. |
| `super.sh` + `super.html` | A super as a transparent 1920x1080 PNG (Fredoka SemiBold, Caramel bar) via headless Edge. |
| `bg.mjs` | Dithered 2-colour gradient PNG (Latte / Espresso). |
| `gallery.sh` (+ `gallery-stills.cs`, `stills.sh`) | One composited still per saved .3dm: `gallery.sh <out-dir> "<file.3dm>|#e0954a" ...` (the colour goes on every F-rep primitive). Opens each file in one run, captures it in the next. Made the FFunction examples preview in the plan doc (7 files, ~4.5 min). |
| `mock-frame.mjs` | Rhino-free mock-up frame with the shader's shading maths, for previews and the doc's reference frames. |
| `setup-tools.sh` | `npm install ffmpeg-static@5.2.0` into the work folder (no system install). |

Work files go to `%LOCALAPPDATA%/freppuccino-sizzle/<clip>/` (override with `SIZZLE_WORK`); deliverables to
`~/Downloads/freppuccino-sizzle/<clip>/`. Name clips `cNN_slug` (c01_hook, c03_scoop, ...).

## Writing a clip script

- Scene: `Tool("create_primitive" | "combine" | "deform" | "edit_operation" | "set_color" | ..., json)` is
  `Freppuccino.Ai.AiTools.Run` called by reflection; argument shapes are in `src/Freppuccino/Ai/AiShapes.cs` and
  `AiTools.cs` (sphere = center, radius; cylinder = start, end, radius; combine = op, operands, k; ...). Every call is
  an undo step; that's fine in a throwaway document. Animate a value by calling `edit_operation` (k) or the matching
  tool every frame. Colours: brand hex values from the plan doc, set at create time (`"color":"#3b2620"`).
- Camera: set `target` and the camera point per frame and call `Capture(i, target, camera)`. Ease every move with
  `Smooth()` (smootherstep). One move per clip, orbits clockwise (plan doc).
- `test` mode renders only the first and last pose (`TimeOf`), so write the animation as a function of `t` seconds.
- Shading is flat: the shader lights by surface facing only (78% to 100%), no lamps or shadows. Dark colours read as
  silhouettes, so form comes from the outline and colour blends. Camera 10 to 30 degrees above.
- Framing used in clip 01 (objects about 2.6 units wide): 50 mm lens, camera 6.1 -> 5.35 units away. 85 mm was far
  too tight at that distance. Put the subject right of centre when the clip has a super.
- Make the change visible: clip 01 starts the spheres 0.09 apart so the blend pulls them together.
- Things appearing: create the primitive on the frame it should appear (clip 02 creates each one when its pop starts),
  then resize it per frame with `update_primitive` (same spec keys as create). Before that frame there's nothing.
- Moving things: `update_primitive` with a new `center` per frame (clip 03 moves its cutter along a Bezier).
- A difference's cutter is invisible (the result keeps the base's colour), so a moving cutter reads as a pocket
  travelling through the base, with no visible tool: good for "scoop" shots.
- Blend sizes: a union's k grows the shape a lot. Two spheres of radius ~0.65 swell past a tight frame at k = 0.8;
  clip 04 stops at 0.6. Test the LAST pose, where the shape is biggest.
- Framing from size: at 50 mm the vertical half-angle is about atan(0.24), so a bounding sphere of radius r fills
  the frame height at a distance of ~4.2 r. The FFunction clips use 3.8-4 r (`get_scene`'s `bounding_box`).
- Warps (`deform`): `twist` / `bend` take `axis_start`, `axis_end`, `angle_degrees`; animate with `edit_operation`
  and the same keys. Bend `toward` is a VECTOR (`[0,1,0]` curls a row along x in plan); a number is silently
  ignored and the default direction fans the row up vertically. `repeat` needs `count` > 1 on some axis: build the
  repeat (and anything wrapped around it) on the frame the second copy appears (clip 08).
- Repeat blend (copies filleted together) has no AI tool: `FrepTree.BlendOver(repeatId)` + `FrepTrees.Set` by
  reflection (clip 08), then animate its k with `edit_operation`.
- Zebra: set `Freppuccino.Display.FrepSettings.Zebra*` fields by reflection, and switch it off in a `finally`
  (clip 06). Meshing: `FrepFieldBuilder.Build` + `DualContour.Mesh` by reflection, then Rhino's `QuadRemesh` (clip 16).
- A second pass (e.g. wireframe): reassign `framesDir` (the harness's `Capture()` writes wherever it points), hide the
  F-rep objects, switch the viewport's display mode, capture the same camera path again (clip 16).
- Names the harness already uses (a clash is a compile error, caught by `check-script.sh`): `dir`, `work`,
  `framesDir`, `frames`, `doc`, `asm`, `run`, `vp`, `view`, `mode`, `clean`, `jitter`, `Tool`, `N`, `Smooth`,
  `Capture`, `TimeOf`, `shaderError`.
- Reading test frames: `stills.sh <clip> <dir> first last` writes one image per pose. Put a border between tiles when
  you sheet them (`pad=iw+40:ih+40:20:20:white`): an empty first frame next to a filled one looks like a framing error.
- FFunction pieces (`examples/ffunction/*.3dm`, used by clips 11, 13, 13b): open the file with `open-3dm.cs` in its
  own run (NOT inside the clip script, see Gotchas), drop the template's clear_scene/create calls and its saved-document
  guard, then animate any `param` with
  `Tool("set_function_params", "{\"target\":\"<ref>\",\"params\":{\"cellTop\":4.2}}")` per frame (`get_scene` lists refs).
  Whole-number params (`wings`, `cells`, `seed`) only change in steps: cut on them. The pieces are 45-70 units across.
- Colour: the example files are default white (invisible on Latte); Espresso flattens detailed pieces into blobs.
  Use Caramel `#e0954a` or Mocha `#8a5a3c` (`FrepPrimitives.SetColor`, see `gallery-stills.cs`).

## Gotchas (each one cost time on clip 01)

| Symptom | Cause | Fix (already in the scripts) |
| --- | --- | --- |
| `rhinocode list` is empty although Rhino is open | The RhinoCode server starts only when the Script Editor loads | Launch with `/runscript="_ScriptEditor"` (`launch-rhino.ps1`) |
| `rhinocode script` returns, nothing happens | Relative script path: silently ignored | Always pass an absolute Windows path (`cygpath -aw`) |
| Script runs minutes later, or only after clicking Rhino | Rhino starts queued scripts only while its window is in the foreground | `nudge-rhino.ps1` (focus ~4 s, then back) |
| The nudge "works" but Rhino stays behind | Windows blocks `SetForegroundWindow` from background processes | `nudge-rhino.ps1` uses the ALT-key + `AttachThreadInput` tricks and checks the foreground window, exits 1 if it failed |
| `run-clip.sh` dies instantly, no output (intermittent) | The nudge still occasionally fails, and under `set -e` its exit 1 aborted the run | `run-clip.sh` retries the nudge, never aborts on it, and nudges again at 20 / 40 / 60 s |
| A black or background-coloured notch in the top-left corner | The old badge mask (clear 160x80 px) cut into a subject that reaches the corner | Off by default now; `BADGE_MASK=1` only for captures from builds without `CleanCapture` |
| `run-clip.sh` waits forever, script did run | `<clip-id>` differed from the script's `ClipId`, so it watched the wrong folder (and the script wiped the other clip's frames) | `run-clip.sh` refuses a mismatch |
| Script never runs, no error anywhere | It failed to compile; Rhino reports compile errors nowhere (no log, no command line) | `check-script.sh`. Known trap: `CaptureToBitmap(view) ?? throw ...` needs `System.Private.Windows.*` refs (CS0012); use `?.` |
| Runtime errors invisible | `rhinocode` gives no output | The harness catches everything into `error.txt`; `run-clip.sh` prints it |
| Extra FREP MODE badges, one on the subject | Captures larger than the on-screen viewport render in tiles; the badge is drawn in each tile | Capture at 1920x1080 or less with Perspective maximised (the harness checks). The plan's 2560x1440 minimum doesn't apply to scripted clips |
| Jagged silhouettes, crawling on camera moves | One capture per frame has no anti-aliasing | 4 captures per frame with rotated-grid sub-pixel camera shifts, averaged (`Capture()` + `encode.sh`) |
| Dark or light fringes after averaging | Averaging straight alpha | `premultiply` -> `mix` -> `unpremultiply` in `encode.sh` |
| FREP MODE badge and grey control outlines in captures (outlines very visible on FFunction clip boxes) | The conduit draws both in every capture, unless `AiCapture.Active` | The harness holds `AiCapture.CleanCapture()` (plugin, main since 2026-11-14) while capturing: just the model. Dispose it in `finally`, or the user's views lose badge + outlines. Older builds (gyroid / displacements branches) lack it: there `encode.sh` still clears the badge corner, and those builds have no outlines |
| Captures right after opening a .3dm show bare wireframes (sometimes one file works) | Rhino finishes setting up a freshly opened document's views only after the opening script ends; redraws / `RhinoApp.Wait()` / sleeps inside the same run don't help | Open in one run (`open-3dm.cs`), capture in the next (`gallery.sh` does this) |
| Same, on the first frames after a restart | The shader cache is cold; FFunction code takes seconds to compile | The harness calls `ShaderConduit.ShaderError(doc)` before every capture: compiles now (cached), throws on a build error |
| Stills of a saved .3dm framed in a hidden view | Files save their own view layout; `Views.Find("Perspective")` can return a hidden view | Use `doc.Views.ActiveView` |
| `dotnet build`: MSB3027 "locked by Rhino 9" | A running Rhino holds `Freppuccino.rhp`; it compiles but the old plugin stays loaded | Close Rhino first (`launch-rhino.ps1` refuses to build while it runs); after a force-close, delete the `.3dm.rhl` lock file it leaves (read-only: `Remove-Item -Force`) |
| A clip script wipes an example or someone's model | The template's SCENE block starts with `clear_scene` | The harness refuses a document with a path; `open-3dm.cs` with `NEW` makes an empty one |
| ffmpeg "Cannot allocate memory" | A looped still `split` inside the graph buffers thousands of frames | Bound looped inputs with `-t`, no split of the background |
| Super in the wrong weight | ffmpeg `drawtext` can't choose a variable font's weight | `super.sh` renders it in Edge |
| Headless Edge hangs forever | It attaches to the user's running Edge profile | Isolated `--user-data-dir` (in `super.sh`); `--disable-gpu` avoids a silent GPU crash |
| Super PNG sometimes missing on the first run | Edge's first launch with a fresh profile is flaky | `super.sh` checks the file, retries up to 3 times |
| No ffmpeg on the machine | | `setup-tools.sh` (npm `ffmpeg-static`) |
| Banding in gradients | 8-bit gradient through H.264 | `bg.mjs` dithers +-0.5 LSB |

Also: the black coffee cup in `ai-runs/` is not a capture bug; colours render correctly in captures on Windows (D3D11).
Shell `rm` on a variable path is blocked by the agent safety check; use `"${DIR:?}/file"`. In Git Bash,
`VAR=x time cmd` fails (`time` is a keyword): put the env vars right before `sh`, without `time`.
Never take full-screen screenshots to debug Rhino: they capture the user's other apps. Capture only Rhino's own
windows (`PrintWindow` on its handles); its command line shows script output and progress.
Timings on the dev laptop: test run ~8 s; full 7 s take (1680 captures) ~1.5-4 min; encode ~1 min.

## After a clip

1. Check `contact.png` and the edges at 100% (`ffmpeg -i still.png -vf crop=480:300:X:Y,scale=960:600:flags=neighbor`).
2. Copy the clip script next to the deliverables if it isn't in the repo yet, and keep both.
3. Update the clip's batch status in the plan doc; add any new gotcha to the table above and to the doc's
   "Scripted capture" section.
