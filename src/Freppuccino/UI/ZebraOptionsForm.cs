using Eto.Drawing;
using Eto.Forms;
using Freppuccino.Csg;
using Freppuccino.Display;
using Rhino;
using Rhino.UI;

namespace Freppuccino.Ui;

/// <summary>
/// Modeless "Zebra Options" window for FZebra, modelled on Rhino's Zebra dialog: stripe direction, stripe size and
/// stripe colors, applied live. Zebra analysis is on while the window is open; closing it turns zebra off (like Rhino).
/// One instance at a time (<see cref="ShowOrFocus"/>).
/// </summary>
public sealed class ZebraOptionsForm : Form
{
    /// Stripe size slider: thin (left) = many stripes, thick (right) = few.
    const int MinStripes = 2, MaxStripes = 60;

    static ZebraOptionsForm? open;

    public static bool IsOpen => open != null;

    /// Opens the window (zebra on) or brings the open one to the front.
    public static void ShowOrFocus(RhinoDoc doc)
    {
        if (open != null)
        {
            open.doc = doc;
            open.UpdateStatus();
            open.BringToFront();
            return;
        }
        open = new ZebraOptionsForm(doc);
        open.Show();
    }

    /// Closes the window if open (zebra off). Returns false if it wasn't open.
    public static bool CloseIfOpen()
    {
        if (open == null) return false;
        open.Close();
        return true;
    }

    RhinoDoc doc;
    readonly Label status = new() { Wrap = WrapMode.Word, TextColor = Colors.Gray };

    ZebraOptionsForm(RhinoDoc doc)
    {
        this.doc = doc;
        Title = "Zebra Options";
        Resizable = false;
        Minimizable = false;
        Maximizable = false;
        ShowInTaskbar = false;
        Topmost = false;
        Owner = RhinoEtoApp.MainWindow;
        Padding = new Padding(10);

        var vertical = new RadioButton { Text = "Vertical", Checked = FrepSettings.ZebraVertical };
        var horizontal = new RadioButton(vertical) { Text = "Horizontal", Checked = !FrepSettings.ZebraVertical };
        vertical.CheckedChanged += (_, _) => Apply(() => FrepSettings.ZebraVertical = vertical.Checked);

        var size = new Slider
        {
            MinValue = 0,
            MaxValue = MaxStripes - MinStripes,
            Value = MaxStripes - Math.Clamp(FrepSettings.ZebraStripes, MinStripes, MaxStripes),
            Width = 200,
        };
        size.ValueChanged += (_, _) => Apply(() => FrepSettings.ZebraStripes = MaxStripes - size.Value);

        var color1 = new ColorPicker { Value = ToEto(FrepSettings.ZebraColor1) };
        var color2 = new ColorPicker { Value = ToEto(FrepSettings.ZebraColor2) };
        color1.ValueChanged += (_, _) => Apply(() => FrepSettings.ZebraColor1 = ToDrawing(color1.Value));
        color2.ValueChanged += (_, _) => Apply(() => FrepSettings.ZebraColor2 = ToDrawing(color2.Value));

        var freppuccinoMode = new Button { Text = "Show in Freppuccino mode" };
        freppuccinoMode.Click += (_, _) =>
        {
            RhinoApp.RunScript("_Freppuccino", false);
            UpdateStatus();
        };

        var close = new Button { Text = "Close" };
        close.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Escape) return;
            e.Handled = true;
            Close();
        };

        static GroupBox Group(string text, Control content) => new() { Text = text, Padding = new Padding(8), Content = content };

        Content = new StackLayout
        {
            Spacing = 8,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                Group("Stripe direction", new StackLayout { Orientation = Orientation.Horizontal, Spacing = 16, Items = { vertical, horizontal } }),
                Group("Stripe size", new TableLayout
                {
                    Spacing = new Size(6, 0),
                    Rows = { new TableRow(new Label { Text = "Thin", VerticalAlignment = VerticalAlignment.Center }, new TableCell(size, true),
                        new Label { Text = "Thick", VerticalAlignment = VerticalAlignment.Center }) },
                }),
                Group("Stripe colors", new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { color1, color2 } }),
                status,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Items = { freppuccinoMode, new StackLayoutItem(null, true), close },
                },
            },
        };

        FrepTrees.Changed += OnTreeChanged;
        FrepSettings.Zebra = true;
        UpdateStatus();
        RedrawAll();
    }

    void Apply(Action change)
    {
        change();
        RedrawAll();
    }

    void OnTreeChanged(RhinoDoc changed)
    {
        if (changed == doc) UpdateStatus();
    }

    void UpdateStatus()
    {
        int unions = FrepTrees.Get(doc).Nodes.Count(n => n.IsGroup && !n.Hidden && Enumerable.Range(1, Math.Max(0, n.Operands.Length - 1)).Any(i => n.ModeAt(i) == FrepMode.Add));
        var view = doc.Views.ActiveView;
        bool inMode = view?.ActiveViewport.DisplayMode?.Id == FreppuccinoDisplayMode.Id;
        status.Text = (unions == 0 ? "No unions yet: create one with FUnion." : $"Showing zebra on {unions} union(s).")
            + (inMode ? "" : "\nStripes are drawn in the Freppuccino display mode only.");
    }

    void RedrawAll()
    {
        doc.Views.Redraw();
    }

    protected override void OnClosed(EventArgs e)
    {
        FrepTrees.Changed -= OnTreeChanged;
        FrepSettings.Zebra = false;
        open = null;
        RedrawAll();
        base.OnClosed(e);
    }

    static Color ToEto(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B, 255);
    static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(c.Rb, c.Gb, c.Bb);
}
