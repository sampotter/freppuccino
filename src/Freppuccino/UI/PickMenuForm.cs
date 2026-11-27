using Eto.Drawing;
using Eto.Forms;
using Freppuccino.Selection;
using Rhino;
using Rhino.Display;
using Rhino.UI;

namespace Freppuccino.Ui;

/// The overlap menu: a list at the cursor, nearest first. Hovering a row previews that candidate in the view without
/// touching the selection; a click picks it; Esc or a click elsewhere closes it and leaves everything as it was.
public sealed class PickMenuForm : Form
{
    static PickMenuForm? open;

    /// <paramref name="at"/>: the click, in screen pixels.
    public static void Show(RhinoDoc doc, System.Drawing.Point at, IReadOnlyList<FrepPicker.Candidate> candidates, Action<FrepPicker.Candidate> pick)
    {
        open?.Close();
        open = new PickMenuForm(doc, candidates, pick);
        float scale = Screen.FromPoint(new PointF(at.X, at.Y)).LogicalPixelSize;
        open.Location = new Point((int)(at.X / scale), (int)(at.Y / scale));
        open.Show();
        open.Focus();
    }

    readonly RhinoDoc doc;
    readonly Preview preview = new();
    bool done;

    PickMenuForm(RhinoDoc doc, IReadOnlyList<FrepPicker.Candidate> candidates, Action<FrepPicker.Candidate> pick)
    {
        this.doc = doc;
        WindowStyle = WindowStyle.None;
        Resizable = false;
        Minimizable = false;
        Maximizable = false;
        ShowInTaskbar = false;
        Topmost = true;
        Owner = RhinoEtoApp.MainWindow;
        Padding = new Padding(1);
        BackgroundColor = SystemColors.WindowBackground;

        var rows = new StackLayout { Spacing = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var c in candidates)
        {
            var label = new Label { Text = c.Label, TextColor = SystemColors.ControlText };
            var row = new Panel { Padding = new Padding(10, 4), Content = label };
            row.MouseEnter += (_, _) =>
            {
                row.BackgroundColor = SystemColors.Highlight;
                label.TextColor = SystemColors.HighlightText;
                Hover(c);
            };
            row.MouseLeave += (_, _) =>
            {
                row.BackgroundColor = Colors.Transparent;
                label.TextColor = SystemColors.ControlText;
                Hover(null);
            };
            row.MouseDown += (_, e) =>
            {
                e.Handled = true;
                Finish();
                pick(c);
            };
            rows.Items.Add(row);
        }
        Content = rows;

        KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Escape) return;
            e.Handled = true;
            Finish();
        };
        LostFocus += (_, _) => Finish();
    }

    void Hover(FrepPicker.Candidate? c)
    {
        FrepSelection.PreviewRoot = c?.Leaf == null ? c?.Root : null;
        FrepSelection.PreviewLeaf = c?.Leaf;
        preview.Object = c is { IsRhino: true } ? doc.Objects.FindId(c.Object) : null;
        preview.Enabled = preview.Object != null;
        doc.Views.Redraw();
    }

    void Finish()
    {
        if (done) return;
        done = true;
        Hover(null);
        preview.Enabled = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        Finish();
        if (open == this) open = null;
        base.OnClosed(e);
    }

    sealed class Preview : DisplayConduit
    {
        public Rhino.DocObjects.RhinoObject? Object;

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (Object?.Geometry is not { } g) return;
            var color = Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
            switch (g)
            {
                case Rhino.Geometry.Brep b: e.Display.DrawBrepWires(b, color, -1); break;
                case Rhino.Geometry.Extrusion x when x.ToBrep() is { } xb: e.Display.DrawBrepWires(xb, color, -1); break;
                case Rhino.Geometry.Mesh m: e.Display.DrawMeshWires(m, color); break;
                case Rhino.Geometry.SubD s when s.ToBrep() is { } sb: e.Display.DrawBrepWires(sb, color, -1); break;
                case Rhino.Geometry.Curve c: e.Display.DrawCurve(c, color, 2); break;
                case Rhino.Geometry.Point p: e.Display.DrawPoint(p.Location, color); break;
            }
        }
    }
}
