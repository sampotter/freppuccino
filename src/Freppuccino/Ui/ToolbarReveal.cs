using Rhino;
using Rhino.PlugIns;

namespace Freppuccino.Ui;

static class ToolbarReveal
{
    const string ShownKey = "ToolbarShown";
    const int MaxAttempts = 200;
    static readonly Guid RuiId = new("cb46b9ee-3bcf-d9a2-3617-1219543432b2");
    static readonly Guid GroupId = new("0993f4a2-d8bf-591d-498c-d50a5e4b938e");

    public static void Install(PlugIn plugin)
    {
        if (plugin.Settings.GetBool(ShownKey, false)) return;
        var attempts = 0;
        void OnIdle(object? sender, EventArgs e)
        {
            attempts++;
            bool shown;
            try
            {
                shown = Reveal();
            }
            catch (Exception)
            {
                shown = false;
                attempts = MaxAttempts;
            }
            if (shown) plugin.Settings.SetBool(ShownKey, true);
            if (shown || attempts >= MaxAttempts) RhinoApp.Idle -= OnIdle;
        }
        RhinoApp.Idle += OnIdle;
    }

    static bool Reveal()
    {
        foreach (var file in RhinoApp.ToolbarFiles)
        {
            if (file.Id != RuiId) continue;
            for (var i = 0; i < file.GroupCount; i++)
            {
                var group = file.GetGroup(i);
                if (group?.Id != GroupId) continue;
                if (!group.Visible) group.Visible = true;
                return group.Visible;
            }
        }
        return false;
    }
}
