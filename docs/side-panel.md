# Side panel (web view + React)

A dockable Rhino panel (`FrepPanel`) that hosts a React app in Eto's WebView (WebView2 on Windows, WKWebView on macOS). It has a browser tree of F-rep objects at the top and a selection-driven inspector below it.

```
src/Freppuccino/Ui/
  FrepPanel.cs       Eto panel, registered in FreppuccinoPlugin.OnLoad; command FrepPanel opens it
  WebViewHost.cs     shared: start URL, origin check, JSON both ways, blocks foreign navigation
  IWebTransport.cs   per-OS part: WebView2Transport.cs (Windows), EtoWebTransport.cs (macOS)
  UiController.cs    Rhino events → snapshots for the page; page requests → Rhino (whitelisted)
  UiProtocol.cs      message types  ◄── keep in sync ──►  src/ui/src/bridge/protocol.ts
src/ui/              Vite + React 19 + TypeScript + Tailwind 4 + shadcn/ui (radix), styled with the FRepuccino brand kit
  src/brand/         fonts, command icons, wordmark copied from brand-kit/ (icons recoloured: ink → currentColor)
  src/index.css      brand tokens (brand-kit/tokens.json) mapped onto shadcn's CSS variables; Latte / Espresso themes
  src/bridge/        host.ts (transport + store + useHost), mock-host.ts (fake Rhino for the browser)
  src/components/    scene-tree.tsx (browser), inspector.tsx (create / primitive / combine), param-slider.tsx
  src/lib/scene.ts   display names, icons, labels per SDF type / operation
  src/ai/            AI assistant (AI tab): agent loop, tools, skills; see docs/ai.md
```

## Why Eto's WebView and not a raw WebView2 control

On Windows, Rhino 9's Eto `WebView` *is* WebView2 (`Eto.Wpf` `WebView2Handler`, runtime 1.0.1938.49 ships in `Rhino 9\System`). `WebViewHost` gets the `CoreWebView2` through the native control by reflection and compiles only against `Microsoft.Web.WebView2.Core` (compile-only package reference). So:

- the project stays `net10.0` with no WPF dependency, and still builds on macOS
- the `postMessage` / `SetVirtualHostNameToFolderMapping` APIs are available, so no URL-scheme hacks and no `file://` module/CORS issues

macOS: Eto's WebView is WKWebView, and Eto (2.13) already bridges it: a script message handler that injects `window.eto.postMessage` into every frame and raises `WebView.MessageReceived`. So the Mac side is plain Eto (`EtoWebTransport.cs`), no Microsoft.macOS / Eto.macOS reference, and builds anywhere. Page → host: `window.eto.postMessage(JSON string)`; host → page: `ExecuteScriptAsync` calling `window.__freppuccino.receive(msg)`. Eto gives no sender URL, so every frame navigation is checked in `DocumentLoading` and the main frame's URL is the origin check. The UI loads from `file://<plugin dir>/ui/index.html`; WKWebView blocks CORS-mode loads from `file://` (module scripts, `crossorigin`), so `vite.config.ts` builds one classic IIFE script loaded with `defer` (CSS inlined into it). Dev tools: Debug builds set `WKWebView.isInspectable` (macOS 13.3+); right-click → Inspect Element, or Safari → Develop.

Layout: `WebViewHost.cs` is shared (start URL, missing-UI placeholder, origin check, parse/dispatch, serialization, external links); `IWebTransport` is the per-OS part: `WebView2Transport.cs` (Windows), `EtoWebTransport.cs` (macOS).

## Dev workflow

| Goal | Do |
|---|---|
| Work on the UI without Rhino | `cd src/ui && npm install && npm run dev`, then open http://localhost:5173. A **mock host** stands in for Rhino; its sample scene includes nested operations. |
| Hot reload inside Rhino | Run `npm run dev`, then start the **"Rhino 9 + Freppuccino (UI dev server)"** launch config, which sets `FREPPUCCINO_UI_DEV_URL=http://localhost:5173`. |
| Normal plugin build | `dotnet build` copies `src/ui/dist` to `bin/.../ui/`. It runs `npm run build` itself only for Release builds or when `dist` is missing; otherwise run `npm run build` after UI changes. Pass `-p:SkipUiBuild=true` to skip. |
| Debug the page in Rhino | Debug builds: Windows F12 / right-click → Inspect (WebView2 DevTools); macOS right-click → Inspect Element (Web Inspector, 13.3+). |

In Rhino, run `FrepPanel`, or open the Freppuccino panel from the panels menu.

## Brand

The UI follows `brand-kit/` (tokens.json, fonts, command icons). To refresh after the kit changes: re-copy the
woff2 fonts and the `light` command icons into `src/ui/src/brand/`, replacing `#3B2620` with `currentColor` in the
icons (the dark set differs only in that colour), and update the colour values in `src/ui/src/index.css`.
The kit has no cone / pyramid / ellipsoid icons yet; `src/brand/icons/{cone,pyramid,ellipsoid}.svg` are stand-ins
drawn in its style, so replace them when the kit gets official ones. Primitive names, icons, parameter labels and
creating commands live in one table, `PRIMITIVES` in `src/ui/src/lib/scene.ts`.

## Protocol

JSON messages, each with a `type`. Host → page messages are snapshots (simple, and fine at this scale). Page → host messages are requests. Selection order is tracked on the page, because Rhino doesn't report pick order; it matters for subtract (target vs tool).

Ids are strings: a primitive's Rhino object id (GUID), or a CSG node's name (`N3`, same as typed at the F-CSG command prompts).

| Host → page | When |
|---|---|
| `state {scene, selection, settings: {units}, theme}` | reply to `ready`; active document changed |
| `scene {scene}` | object added/deleted/replaced/attributes changed, CSG tree changed (coalesced on Idle) |
| `selection {ids}` | Rhino selection or panel node selection changed |
| `notice {level, text}` | feedback, including "not implemented yet" from stubs |
| `aiToolResult {callId, ok, result?, error?}` | reply to `aiTool` ([ai.md](ai.md)) |

| Page → host | Status |
|---|---|
| `ready` | ✅ |
| `select {ids, mode: replace\|add\|toggle}` | ✅ primitives → Rhino selection; nodes → panel node selection. While FUnion/FIntersect/FDifference wait for operands, a node click is sent to the command line instead |
| `setVisible {id, visible}` | ✅ primitive: Rhino show/hide; node: `FrepNode.Hidden`, dropped from the shader like a hidden primitive (undoable) |
| `runCommand {command}` | ✅ whitelist only: `FBox`, `FSphere`, `FCylinder`, `FCone`, `FPyramid`, `FEllipsoid`, `FTorus`, `Freppuccino` |
| `applyOperation {operands, op, k}` | ✅ new CSG node; selects it |
| `setOperation {id, op, k, preview?}` | ✅ edit a node live. `preview: true` = drag tick, applied without undo; the next non-preview one ends the gesture as one undo step |
| `setNodeParam {id, name, value, preview?}` | ✅ edit a twist/bend/repeat param; same preview/commit gesture as `setOperation` |
| `deleteNodes {ids}` | ✅ nodes still used by other nodes are kept |
| `setColor {id, color, preview?}` | ✅ write `SDF.Color` (`"#RRGGBB"`) on a primitive; same preview/commit gesture as `setOperation` (one "F-rep color" undo step per picker drag) |
| `setParam {id, name, value}` | **stub**: edit `SDF.<name>` on a primitive |
| `aiTool {callId, tool, args}` | ✅ AI agent tool call (`Ai/AiTools.cs`), see [ai.md](ai.md) |
| `aiSaveRun {title, transcript, images}` | ✅ writes an AI run log (`Ai/AiRunLog.cs`) |

Live edits: the inspector has no Update button. An op click sends one `setOperation`; a Blend drag sends `preview` ticks (≤ ~30 Hz, trailing) then a final one on release. The host (`FrepTrees.Preview`/`Commit`) remembers the tree from before the gesture and records one undo step back to it. A gesture whose final message never arrives is committed by any other page message, any Rhino command (incl. Undo), a page reload / doc switch, or panel close. The page ignores host snapshots for the node while dragging and until the host echoes its last sent value, so stale snapshots can't yank the slider.

`op` ∈ `union | subtract | intersect` (`FrepOp.Difference` = subtract). Every op takes a blend `k` (0 = sharp, > 0 = smooth). For subtract, operands[0] / children[0] is the target and the rest are tools.

## Browser tree

`SceneNode` is recursive:

```
SceneNode { id, kind: primitive|operation, name?, visible, locked, missing?,
            sdfType?, params?, color?,  // primitive; color = SDF.Color "#RRGGBB" (white if missing)
            op?, k?, children?,         // operation: children are operands, in order
            ref?, refMissing? }         // twist|bend|repeat: params = {angle} / {angle, direction} / {x, y, z}; ref = line/box id
```

`UiController.BuildScene` walks the doc's `FrepTree` (Csg/FrepTree.cs, stored as plugin document data): roots are nodes nothing references, then primitives no node uses. The tree is a DAG, so a shared operand is listed under each parent. Deleted operands show as `missing`.

Operation nodes aren't Rhino objects, so their selection is kept by `UiController` (per document). FUnion/FIntersect (and FTwist/FBend/FRepeat/FDisplace, single node) take it as preselection (`UiController.TakeSelectedNodes`); Rhino deselect-all clears it.

Inspector: one primitive → its color (`SDF.Color`: current-color swatch / picker + hex field; new primitives start white `#FCFCFC`) and parameters; one node → edit op / k live (no Update button), delete; ≥ 2 selected → combine them (pick order; first = subtract target).
