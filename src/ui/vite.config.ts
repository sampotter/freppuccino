import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig, searchForWorkspaceRoot, type Plugin } from 'vite'

// The built app is served by the Rhino side panel: https://freppuccino.ui/ on Windows (WebView2),
// file:// on macOS (WKWebView). See src/Freppuccino/Ui/WebViewHost.cs. `npm run dev` serves the same app in a
// plain browser against a mock host, or inside Rhino with FREPPUCCINO_UI_DEV_URL=http://localhost:5173 for hot reload.

// WKWebView refuses CORS-mode loads from file:// (module scripts, `crossorigin` assets), so the build is one
// classic IIFE script loaded with `defer` (same timing as a module).
function classicScripts(): Plugin {
  return {
    name: 'classic-scripts',
    apply: 'build',
    transformIndexHtml: {
      order: 'post',
      handler: (html) =>
        html.replace(/<script type="module" crossorigin/g, '<script defer').replace(/ crossorigin(?=[\s>])/g, ''),
    },
  }
}

export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss(), classicScripts()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, './src') },
  },
  build: {
    modulePreload: false,
    rollupOptions: { output: { format: 'iife' } },
  },
  server: {
    port: 5173,
    strictPort: true,
    // skills.ts also loads the repo's .claude/skills/freppuccino-*/SKILL.md (outside this root).
    fs: { allow: [searchForWorkspaceRoot(process.cwd()), path.resolve(import.meta.dirname, '../../.claude/skills')] },
  },
})
