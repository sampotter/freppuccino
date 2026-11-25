using Eto.Forms;

namespace Freppuccino.Ui;

/// <summary>
/// macOS (WKWebView), plain Eto: no Microsoft.macOS / Eto.macOS reference, so it builds anywhere.
/// Eto's WKWebViewHandler registers a script message handler and injects window.eto.postMessage into every
/// frame; it surfaces as <see cref="WebView.MessageReceived"/> (string body, no sender URL). Host → page is
/// <see cref="WebView.ExecuteScriptAsync"/> calling window.__freppuccino.receive(msg).
/// The UI loads from file:// (the Vite build is a classic script for that; see src/ui/vite.config.ts).
/// </summary>
sealed class EtoWebTransport : IWebTransport
{
    readonly WebView control;
    readonly NavigationFilter allowNavigation;
    readonly Action<string> openNewWindow;

    public event Action<string, string?>? Received;
    public event Action? Lost { add { } remove { } }

    public EtoWebTransport(WebView control, NavigationFilter allowNavigation, Action<string> openNewWindow)
    {
        this.control = control;
        this.allowNavigation = allowNavigation;
        this.openNewWindow = openNewWindow;
        control.DocumentLoading += OnDocumentLoading;
        control.OpenNewWindow += OnOpenNewWindow;
        control.MessageReceived += OnMessageReceived;
        if (WebViewHost.DevTools) EnableInspector();
    }

    public Uri PageUri(string uiFolder) => new(Path.Combine(uiFolder, "index.html"));

    public void Load(Uri uri) => control.Url = uri;

    public void Post(string json)
    {
        if (control.IsDisposed) return;
        // JSON is a valid JS expression.
        control.ExecuteScriptAsync($"window.__freppuccino && window.__freppuccino.receive({json});")
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // not loaded yet: dropped, page re-syncs on "ready"
    }

    // Every frame, main or not: a foreign iframe would also get window.eto.
    void OnDocumentLoading(object? sender, WebViewLoadingEventArgs e)
    {
        if (!allowNavigation(e.Uri.AbsoluteUri)) e.Cancel = true;
    }

    void OnOpenNewWindow(object? sender, WebViewNewWindowEventArgs e)
    {
        e.Cancel = true; // else Eto opens any scheme
        openNewWindow(e.Uri.AbsoluteUri);
    }

    // No sender URL from Eto; frames are policed in OnDocumentLoading, so the main frame's URL stands in.
    void OnMessageReceived(object? sender, WebViewMessageEventArgs e)
    {
        string? source = null;
        try { source = control.Url?.AbsoluteUri; } catch { }
        Received?.Invoke(e.Message, source);
    }

    // WKWebView.isInspectable (macOS 13.3+): Safari → Develop menu, or right-click → Inspect Element.
    void EnableInspector()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(13, 3)) return;
        try
        {
            var native = control.ControlObject;
            native?.GetType().GetProperty("Inspectable")?.SetValue(native, true);
        }
        catch { }
    }

    public void Dispose()
    {
        control.DocumentLoading -= OnDocumentLoading;
        control.OpenNewWindow -= OnOpenNewWindow;
        control.MessageReceived -= OnMessageReceived;
        Received = null;
    }
}
