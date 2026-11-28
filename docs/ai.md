# AI assistant (side panel → AI tab)

Type "make a coffee cup" and an LLM (via OpenRouter) builds it from F-rep primitives and CSG operations, takes screenshots of the
shader result, looks at them, and fixes what's wrong. Every edit is a normal Rhino undo step ("AI: create cup_body", …).

## Setup

In Rhino: `FrepPanel` → **AI** tab → paste your OpenRouter key (from https://openrouter.ai/keys) into the key field → **Save**.
The field is a password field. The key is kept in the panel's web view storage (localStorage) on this computer, and the key
button in the AI toolbar opens the field again to replace or remove it. It is sent only to OpenRouter, straight from the panel.

Optional, for developers: a build-time key in `src/ui/.env.local` (gitignored; template: `src/ui/.env.example`) is used when no
key has been pasted. Rebuild after changing it (`dotnet build` rebuilds the UI when `.env*` changes). That key is baked into the
built bundle in plain text, so don't distribute a build made with one.
```
VITE_OPENROUTER_API_KEY=sk-or-v1-...
VITE_OPENROUTER_MODEL=anthropic/claude-sonnet-5.5   # optional default; switchable in the AI tab
```
Any model with image input + tool calling works.

## How it works

```
src/ui/src/ai/
  agent.ts        loop: prompt (+ reference images) → model → tool calls → results (+ screenshots) → model … until it answers without tools
  images.ts       reads attached reference images (downscale + JPEG data URL)
  openrouter.ts   fetch client for /chat/completions (no SDK: see the comment there)
  tools.ts        tool schemas for the model + runTool (load_skill local, everything else → Rhino)
  prompt.ts       fixed system prompt (role + loop protocol); modeling know-how lives in skills
  skills.ts       loads skills/*.md
  skills/*.md     the guidance: edit these to tune behaviour
  config.ts       key, model, step limit, screenshot retention
src/ui/src/components/ai-panel.tsx   chat UI
src/Freppuccino/Ai/
  AiTools.cs      tool implementations ("aiTool" message → AiToolResultMessage)
  AiWarps.cs      deform tool: twist / bend / repeat nodes + the axis lines / boxes that drive them
  AiShapes.cs     LLM-friendly shape specs ⇄ SdPrimitive (cylinder = start/end/radius, box = center/size, …)
  AiCapture.cs    offscreen screenshots in the hidden "Freppuccino Capture" display mode (proxy Breps invisible); while
                  capturing (AiCapture.Active) the conduit draws just the model: no FREP MODE badge, no control outlines.
                  AiCapture.CleanCapture() gives other capture code (the sizzle-capture skill's scripts) the same scope
  AiRunLog.cs     saves each run (transcript + PNGs)
src/Freppuccino/Scene/FrepPrimitives.cs   SdPrimitive → SDF.* user text + proxy Brep, add / replace in the doc
```

Tools: `get_scene`, `create_primitive`, `update_primitive`, `transform`, `combine`, `deform`, `edit_operation`, `add_operand`,
`remove_operand`, `move`, `set_mode`, `delete`, `wrap`, `unwrap`, `set_color`, `set_visible`, `select`, `clear_scene`, `capture_views`, `mesh`,
`set_zebra`, the FFunction tools below, `load_skill` and `get_function_example` (the last two run in the panel, not in Rhino).
Objects are addressed by name (unique names are enforced), GUID, or node name (`N3`). `get_scene` also reports the user's
selection, so "make this taller" works.

## Coverage: app features vs. AI tools

Every feature a user can reach in Rhino or the panel, and how the agent reaches it. Keep this table current when adding a
command or a panel action.

| Feature | User (command / panel) | AI tool |
|---|---|---|
| Primitives: box, sphere, cylinder, cone, pyramid, ellipsoid, torus | `FBox` … `FTorus`, Create tiles | `create_primitive`, `update_primitive` |
| Primitive parameters, color, visibility | inspector sliders / color / eye | `update_primitive`, `set_color`, `set_visible` |
| Move / rotate / scale | gumball | `transform` |
| FFunction (user SDF code) | `FFunction`, Create tile, **Script tab** | `create_function`, `set_function_code`, `get_function`, `check_function_code` |
| FFunction params, Lipschitz | inspector / Script tab sliders | `set_function_params` |
| FFunction example library (`examples/ffunction/`) | Script tab → Examples | `get_function_example` |
| Group with union / subtract / intersect preset + blend k | `FUnion` `FDifference` `FIntersect`, Combine section | `combine`, `edit_operation` |
| Add / subtract / intersect per row | Tree mode button | `set_mode`, `add_operand mode=`, `move mode=` |
| Restructure the tree | Tree drag and drop | `add_operand`, `remove_operand`, `move` |
| Twist / bend / repeat | `FTwist` `FBend` `FRepeat`, Deform tiles | `deform`, `edit_operation` |
| Blend a repeat's copies | Deform → Blend | `wrap op=blend`, `edit_operation k` |
| Offset a group (signed) | Deform → Offset | `wrap op=offset`, `edit_operation k` (may be negative) |
| Unwrap a node / delete a subtree | Unwrap / Delete buttons | `unwrap` / `delete recursive` |
| Selection | click in viewport / browser | read: `get_scene.selection`; write: `select` |
| Bake to mesh | `FrepMesh` | `mesh` |
| Zebra analysis | `FZebra` / `FZebraOff` | `set_zebra` (stripes show in `capture_views`) |
| Look at the result | viewport | `capture_views` (offscreen, Freppuccino shading) |
| Freppuccino display mode | `Freppuccino` | not needed: captures render in their own hidden mode |

Not exposed on purpose: arbitrary Rhino commands (the panel whitelist stays a whitelist) and the zebra colors.

Script tab (side panel): an editor for FFunction code. It follows the selection (one selected function = edit it, Ctrl+Enter
applies with `set_function_code`), otherwise it makes a new function (box size, Lipschitz, name; Create = `create_function`,
Y-up box on the ground at the origin). It compiles as you type (`check_function_code`), lists diagnostics (click → line),
loads examples, and hands the code plus diagnostics to the AI tab ("Ask" / "Fix"). It calls the same tools as the agent,
through `aiTool` with `source: "script"`, so its edits are labelled "Script: …" in Undo instead of "AI: …".

Colors: the AI writes `SDF.Color`, the same per-primitive shader color as the inspector's color picker (not the Rhino object
color). `create_primitive` / `update_primitive` take a `color`; `set_color` recolors without changing shape, either
`refs` + `color` or `colors: [{ref, color}, …]` for many parts in one undo step, and a node ref colors every primitive under it.
`get_scene` reports each primitive's color. Colors are `"#RRGGBB"` or a CSS / .NET name.

FFunctions (user SDF code in a clip box): `create_function`, `set_function_code`, `get_function`, `set_function_params`,
`check_function_code` (`Ai/AiFunctions.cs`); `help` returns the FFunction guide + tool list. `get_function_example` serves
`examples/ffunction/*.glsl` (bundled into the UI by `src/ui/src/lib/examples.ts`, which also holds each example's clip box and
Lipschitz scale: add a row there when adding an example). The `lattices` panel skill has TPMS / infill recipes.
`wrap`, `unwrap`, `select`, `mesh`, `set_zebra` live in `Ai/AiSceneTools.cs`; `mesh` calls `FrepMeshCommand.Bake`, the
command's own mesher without its prompts. The guide is one file,
`.claude/skills/freppuccino-ffunction/SKILL.md`: a Claude Code skill for external sessions (which call
`Freppuccino.Ai.AiTools.RunJson(tool, json)` through Rhino MCP `run_csharp`), a panel skill (skills.ts also globs
`.claude/skills/freppuccino-*/SKILL.md`), and the embedded resource `help` returns.

Deforms: `deform` takes LLM-friendly fields (twist: axis + angle; bend: axis + angle + `toward`; repeat: `count` + `spacing` +
`centered`) and creates the Rhino line / box that drives the warp, tagged with the user string `Freppuccino.AiRef` and named
`<target>_<op>_axis|box`. The user can move those to adjust the deform; `delete` / `clear_scene` remove them with their node, and
`transform` moves them with the part. `deform` / `edit_operation` return a `warning` when a twist or bend is too steep to render well
(thresholds in `AiWarps.cs`, calibrated in Rhino).
Tool errors go back to the model as messages, so it can correct itself.

Screenshots: `capture_views` renders the active view's camera from standard angles (perspective, front, right, top, …) at 512 px,
framed on the objects, without moving the user's view. Tool results are text-only in the chat API, so the images follow as a
user message. Only the newest 2 screenshot sets stay in the context. The system prompt and last message carry
`cache_control` (prompt caching on Anthropic / Gemini via OpenRouter).

Reference images: the image button next to the prompt box (or paste / drag and drop onto the AI tab) attaches up to 4 photos or
sketches to a message; the text is optional ("Model the object shown…" is sent when it's empty). `ai/images.ts` downscales
each to ≤ 1568 px and re-encodes it as JPEG. They go to the model as image parts of the user message, labelled
`[reference images attached by the user: …]`, and are never pruned like old screenshots, so the model can compare its
captures with them for the whole chat. The `reference-images` skill tells it how to turn a picture into parts and proportions.
The chosen model needs image input.

## Tuning it: skills + run logs

Skills are markdown files in `src/ui/src/ai/skills/`:

```
---
name: vessels
description: One line telling the model when to load it.
always: true      # optional: inlined in every system prompt instead of loaded on demand
---
Guidance, rules, proven recipes with real numbers...
```

`always` skills go into the system prompt; the others are listed by name + description and the model loads them with `load_skill`.
Concrete recipes with numbers that are known to work (like the mug in `vessels.md`) help most.

Every run is saved to `ai-runs/<timestamp>-<prompt>/` (`transcript.json` + `01-perspective.png`, …). The VS Code launch configs and
`dotnet run` point `FREPPUCCINO_AI_RUNS_DIR` at the repo's gitignored `ai-runs/`; otherwise it's `Documents/Freppuccino/ai-runs`.
Loop: try prompts → read the failed runs (what the model planned, which numbers it used, what the screenshots showed) → add or fix a
skill → rebuild the UI → try again.

## Testing without a key

`npm run dev` in a browser uses the mock host, which fakes the scene tools (no geometry, no screenshots). Any OpenAI-compatible server
works via `VITE_OPENROUTER_BASE_URL`, e.g. a local stub that replays scripted tool calls.

## Limits

- ~80 non-sphere primitives fit the shader's uniform budget (fewer with many operations); `get_scene` warns when some aren't drawn.
- Each tool call is its own undo step, so undoing a whole AI build takes several Undos.
- The model sees only the shader render. Non-F-rep Rhino geometry is invisible to it.
