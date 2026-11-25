using System.Runtime.InteropServices;
using Eto.Forms;

namespace Freppuccino.Ui;

/// <summary>
/// Freppuccino side panel: hosts the React app from src/ui in a web view (WebView2 / WKWebView, see <see cref="WebViewHost"/>).
/// Registered in FreppuccinoPlugin.OnLoad; open with the FrepPanel command or Rhino's panel menu.
/// </summary>
[Guid("3f6c1b52-8e4a-4d7f-b0a9-6d2e91c47a38")]
public class FrepPanel : Panel
{
    public const string Caption = "Freppuccino";

    readonly WebViewHost web;
    readonly UiController controller;

    public FrepPanel()
    {
        Padding = 0;
        web = new WebViewHost();
        controller = new UiController(web);
        Content = web.Control;
        web.ControlReplaced += () => Content = web.Control;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            controller.Dispose();
            web.Dispose();
        }
        base.Dispose(disposing);
    }
}
