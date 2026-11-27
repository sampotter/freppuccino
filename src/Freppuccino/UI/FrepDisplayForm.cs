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
        AutoSize = true;
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

        var ok = new Button { Text = "OK", Width = ButtonWidth };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", Width = ButtonWidth };
        cancel.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Escape) Close();
            else if (e.Key == Keys.Enter) Accept();
            else return;
            e.Handled = true;
        };

        var settings = new TableLayout
        {
            Spacing = new Eto.Drawing.Size(8, 8),
            Rows =
            {
                Row(FrepDisplaySettings.CuttersLabel, cutters),
                Row(FrepDisplaySettings.OtherShapesLabel, others),
                Row(FrepDisplaySettings.GeometryLabel, geometry),
            },
        };
        var buttons = new TableLayout { Padding = new Eto.Drawing.Padding(0, Gap, 0, 0), Rows = { new TableRow(ok, new TableCell(null, true), cancel) } };
        Content = new StackLayout
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { settings, failure, buttons },
        };
        Shown += (_, _) => ok.Focus();
        Closing += (_, e) =>
        {
            if (keep || Restore()) return;
            e.Cancel = true;
            failure.Visible = true;
            Sync();
        };
    }

    const int ButtonWidth = 90, Gap = 12;

    readonly Panel failure = new()
    {
        Padding = new Eto.Drawing.Padding(0, Gap, 0, 0),
        Content = new Label { Text = "Couldn't restore the previous settings.\nChoose Cancel to try again, or OK to keep what's shown." },
        Visible = false,
    };

    bool Restore()
    {
        try
        {
            FrepDisplaySettings.Geometry = atOpen.Geometry;
            if (FrepDisplaySettings.Geometry != atOpen.Geometry) return false;
            FrepDisplaySettings.Cutters = atOpen.Cutters;
            FrepDisplaySettings.OtherShapes = atOpen.Others;
            return FrepDisplaySettings.Cutters == atOpen.Cutters && FrepDisplaySettings.OtherShapes == atOpen.Others;
        }
        catch (Exception)
        {
            return false;
        }
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
        base.OnClosed(e);
    }

    static DropDown Choices<T>() where T : struct, Enum
    {
        var d = new DropDown();
        foreach (var v in Enum.GetValues<T>()) d.Items.Add(v.ToString());
        return d;
    }
}
