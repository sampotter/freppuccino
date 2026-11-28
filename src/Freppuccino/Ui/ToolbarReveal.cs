using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace Freppuccino.Ui;

static class ToolbarReveal
{
    const string ShownKey = "ToolbarShown";
    const int OpenAfter = 10;
    const int MaxAttempts = 200;
    static readonly Guid RuiId = new("cb46b9ee-3bcf-d9a2-3617-1219543432b2");
    static readonly Guid GroupId = new("0993f4a2-d8bf-591d-498c-d50a5e4b938e");

    public static void Install(PlugIn plugin)
    {
        var reveal = !plugin.Settings.GetBool(ShownKey, false);
        var installed = InstalledRui(plugin);
        var attempts = 0;
        string? failure = installed == null ? "Freppuccino.rui is missing next to the plug-in" : null;
        void OnIdle(object? sender, EventArgs e)
        {
            attempts++;
            var done = false;
            try
            {
                var file = Find();
                if (file == null && installed != null && attempts % OpenAfter == 0)
                {
                    file = RhinoApp.ToolbarFiles.Open(installed);
                    if (file == null) failure = $"opening {installed} failed";
                    else RhinoApp.WriteLine("Freppuccino: toolbar file was not loaded; opened it.");
                }
                if (file != null)
                {
                    failure = null;
                    done = !reveal || Reveal(file);
                    if (reveal && done) plugin.Settings.SetBool(ShownKey, true);
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            if (!done && attempts >= MaxAttempts)
            {
                done = true;
                if (failure != null) RhinoApp.WriteLine($"Freppuccino: toolbar file not loaded ({failure}).");
            }
            if (done) RhinoApp.Idle -= OnIdle;
        }
        RhinoApp.Idle += OnIdle;
    }

    static string? InstalledRui(PlugIn plugin)
    {
        var dir = Path.GetDirectoryName(plugin.Assembly.Location);
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, "Freppuccino.rui");
        return File.Exists(path) ? path : null;
    }

    static ToolbarFile? Find()
    {
        foreach (var file in RhinoApp.ToolbarFiles)
            if (file.Id == RuiId) return file;
        return null;
    }

    static bool Reveal(ToolbarFile file)
    {
        for (var i = 0; i < file.GroupCount; i++)
        {
            var group = file.GetGroup(i);
            if (group?.Id != GroupId) continue;
            if (!group.Visible) group.Visible = true;
            return group.Visible;
        }
        return false;
    }
}
