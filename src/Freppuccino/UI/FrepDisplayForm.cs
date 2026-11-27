using Eto.Forms;
using Freppuccino.Display;
using Rhino.UI;

namespace Freppuccino.Ui;

public sealed class FrepDisplayForm : Form
{
    static FrepDisplayForm? open;

    public static void ShowOrFocus()
    {
        if (open != null)
        {
            open.BringToFront();
            return;
        }
        open = new FrepDisplayForm();
        open.Show();
    }

    readonly DropDown cutters = Choices<CutterDisplay>();
    readonly DropDown geometry = Choices<GeometryDisplay>();

    FrepDisplayForm()
    {
        Title = "Freppuccino Display";
        Resizable = false;
        Minimizable = false;
        Maximizable = false;
        ShowInTaskbar = false;
        Owner = RhinoEtoApp.MainWindow;
        Padding = new Eto.Drawing.Padding(10);

        Sync();
        cutters.SelectedIndexChanged += (_, _) => { if (cutters.SelectedIndex >= 0) FrepDisplaySettings.Cutters = (CutterDisplay)cutters.SelectedIndex; };
        geometry.SelectedIndexChanged += (_, _) => { if (geometry.SelectedIndex >= 0) FrepDisplaySettings.Geometry = (GeometryDisplay)geometry.SelectedIndex; };
        FrepDisplaySettings.Changed += Sync;

        var close = new Button { Text = "Close" };
        close.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Escape) return;
            e.Handled = true;
            Close();
        };

        Content = new TableLayout
        {
            Spacing = new Eto.Drawing.Size(8, 8),
            Rows =
            {
                new TableRow(new Label { Text = "Cutters", VerticalAlignment = VerticalAlignment.Center }, new TableCell(cutters, true)),
                new TableRow(new Label { Text = "Rhino geometry", VerticalAlignment = VerticalAlignment.Center }, new TableCell(geometry, true)),
                new TableRow(null, new TableLayout(new TableRow(new TableCell(null, true), close))),
            },
        };
    }

    void Sync()
    {
        cutters.SelectedIndex = (int)FrepDisplaySettings.Cutters;
        geometry.SelectedIndex = (int)FrepDisplaySettings.Geometry;
    }

    protected override void OnClosed(EventArgs e)
    {
        FrepDisplaySettings.Changed -= Sync;
        open = null;
        base.OnClosed(e);
    }

    static DropDown Choices<T>() where T : struct, Enum
    {
        var d = new DropDown();
        foreach (var v in Enum.GetValues<T>()) d.Items.Add(v.ToString());
        return d;
    }
}
