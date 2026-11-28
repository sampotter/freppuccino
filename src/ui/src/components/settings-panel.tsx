import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import { send, useHost } from '@/bridge/host'
import type { GeometryDisplay, ShapeDisplay } from '@/bridge/protocol'

const SHAPES: ShapeDisplay[] = ['Silhouette', 'Wireframe', 'Hidden']
const GEOMETRY: GeometryDisplay[] = ['Ghosted', 'Shaded', 'Wireframe', 'Arctic', 'Rendered']

export function SettingsPanel() {
  const display = useHost((s) => s.settings.display)
  const faint = display.hiddenLines === 'Faint'
  return (
    <section className="grid gap-4 px-4 py-4">
      <h2 className="type-heading">Display</h2>
      <ShapeRow label="Cutters" value={display.cutters} onChange={(v) => send({ type: 'setDisplay', cutters: v })} />
      <ShapeRow label="Other shapes" value={display.otherShapes} onChange={(v) => send({ type: 'setDisplay', otherShapes: v })} />
      <div className="grid gap-2">
        <Label htmlFor="settings-geometry" className="type-label text-muted-foreground">Rhino geometry</Label>
        <select
          id="settings-geometry"
          value={display.geometry}
          onChange={(e) => send({ type: 'setDisplay', geometry: e.target.value as GeometryDisplay })}
          className="type-small h-8 rounded-md border border-input bg-card px-2 outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {GEOMETRY.map((g) => (
            <option key={g} value={g}>{g}</option>
          ))}
        </select>
      </div>
      <div className="flex items-start gap-3">
        <button
          type="button"
          role="switch"
          aria-checked={faint}
          aria-labelledby="settings-hidden-label"
          onClick={() => send({ type: 'setDisplay', hiddenLines: faint ? 'Off' : 'Faint' })}
          className={cn(
            'relative mt-0.5 h-5 w-9 shrink-0 rounded-full border border-input outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring',
            faint ? 'bg-primary' : 'bg-muted',
          )}
        >
          <span className={cn('absolute top-0.5 size-3.5 rounded-full bg-card shadow transition-[left]', faint ? 'left-[18px]' : 'left-0.5')} />
        </button>
        <div className="grid gap-0.5">
          <span id="settings-hidden-label" className="type-small font-bold">Show hidden lines faintly</span>
          <span className="type-small text-muted-foreground">Parts of cutter outlines behind the F-rep. Off hides them completely.</span>
        </div>
      </div>
    </section>
  )
}

function ShapeRow(props: { label: string; value: ShapeDisplay; onChange: (v: ShapeDisplay) => void }) {
  return (
    <div className="grid gap-2">
      <span className="type-label text-muted-foreground">{props.label}</span>
      <ToggleGroup
        type="single"
        variant="outline"
        spacing={0}
        value={props.value}
        onValueChange={(v) => v && v !== props.value && props.onChange(v as ShapeDisplay)}
        className="w-full"
        aria-label={props.label}
      >
        {SHAPES.map((s) => (
          <ToggleGroupItem key={s} value={s} className="flex-1 px-1">{s}</ToggleGroupItem>
        ))}
      </ToggleGroup>
    </div>
  )
}
