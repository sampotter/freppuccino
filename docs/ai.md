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
  agent.ts        loop: prompt → model → tool calls → results (+ screenshots) → model … until it answers without tools
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
  AiDisplace.cs   texture tool: perlin / voronoi / knurl Displace nodes (+ optional mask, knurl axis line)
  AiShapes.cs     LLM-friendly shape specs ⇄ SdPrimitive (cylinder = start/end/radius, box = center/size, …)
  AiCapture.cs    offscreen screenshots in the hidden "Freppuccino Capture" display mode (proxy Breps invisible)
  AiRunLog.cs     saves each run (transcript + PNGs)
src/Freppuccino/Scene/FrepPrimitives.cs   SdPrimitive → SDF.* user text + proxy Brep, add / replace in the doc
```

Tools: `get_scene`, `create_primitive`, `update_primitive`, `transform`, `combine`, `deform`, `texture`, `edit_operation`, `delete`,
`set_visible`, `clear_scene`, `capture_views`, `load_skill`. Objects are addressed by name (unique names are enforced), GUID, or node
name (`N3`). Primitives take a `color`.

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
