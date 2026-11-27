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

    readonly DropDown cutters = Choices<ShapeDisplay>();
    readonly DropDown others = Choices<ShapeDisplay>();
    readonly DropDown geometry = Choices<GeometryDisplay>();
    readonly (ShapeDisplay Cutters, ShapeDisplay Others, GeometryDisplay Geometry) atOpen =
        (FrepDisplaySettings.Cutters, FrepDisplaySettings.OtherShapes, FrepDisplaySettings.Geometry);
    bool keep;

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
        cutters.SelectedIndexChanged += (_, _) => { if (cutters.SelectedIndex >= 0) FrepDisplaySettings.Cutters = (ShapeDisplay)cutters.SelectedIndex; };
        others.SelectedIndexChanged += (_, _) => { if (others.SelectedIndex >= 0) FrepDisplaySettings.OtherShapes = (ShapeDisplay)others.SelectedIndex; };
        geometry.SelectedIndexChanged += (_, _) => { if (geometry.SelectedIndex >= 0) FrepDisplaySettings.Geometry = (GeometryDisplay)geometry.SelectedIndex; };
        FrepDisplaySettings.Changed += Sync;

        var ok = new Button { Text = "OK" };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel" };
        cancel.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Escape) Close();
            else if (e.Key == Keys.Enter) Accept();
            else return;
            e.Handled = true;
        };

        Content = new TableLayout
        {
            Spacing = new Eto.Drawing.Size(8, 8),
            Rows =
            {
                Row(FrepDisplaySettings.CuttersLabel, cutters),
                Row(FrepDisplaySettings.OtherShapesLabel, others),
                Row(FrepDisplaySettings.GeometryLabel, geometry),
                new TableRow(null, new TableLayout { Spacing = new Eto.Drawing.Size(8, 0), Rows = { new TableRow(new TableCell(null, true), ok, cancel) } }),
            },
        };
        Shown += (_, _) => ok.Focus();
    }

    static TableRow Row(string label, DropDown choices) =>
        new(new Label { Text = label, VerticalAlignment = VerticalAlignment.Center }, new TableCell(choices, true));

    void Accept()
    {
        keep = true;
        Close();
    }

    void Sync()
    {
        cutters.SelectedIndex = (int)FrepDisplaySettings.Cutters;
        others.SelectedIndex = (int)FrepDisplaySettings.OtherShapes;
        geometry.SelectedIndex = (int)FrepDisplaySettings.Geometry;
    }

    protected override void OnClosed(EventArgs e)
    {
        FrepDisplaySettings.Changed -= Sync;
        open = null;
        if (!keep)
        {
            FrepDisplaySettings.Cutters = atOpen.Cutters;
            FrepDisplaySettings.OtherShapes = atOpen.Others;
            FrepDisplaySettings.Geometry = atOpen.Geometry;
        }
        base.OnClosed(e);
    }

    static DropDown Choices<T>() where T : struct, Enum
    {
        var d = new DropDown();
        foreach (var v in Enum.GetValues<T>()) d.Items.Add(v.ToString());
        return d;
    }
}
