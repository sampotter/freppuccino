using System.Reflection;
using Eto.Forms;
using Microsoft.Web.WebView2.Core;
using Rhino;

namespace Freppuccino.Ui;

/// <summary>
/// Windows: Rhino's Eto WebView is WebView2, so we reach <see cref="CoreWebView2"/> through the native control
/// (ControlObject is the WPF WebView2) by reflection. Compiles against Microsoft.Web.WebView2.Core only (no WPF),
/// so the project still builds on macOS; this type is never touched there.
/// Page API: window.chrome.webview.postMessage(object) / addEventListener("message").
/// </summary>
sealed class WebView2Transport : IWebTransport
{
    const string VirtualHost = "freppuccino.ui";

    readonly WebView control;
    readonly NavigationFilter allowNavigation;
    readonly Action<string> openNewWindow;
    CoreWebView2? core;
    Uri? target;
    string? uiFolder;
    object? native;
    EventInfo? initEvent;
    EventHandler<CoreWebView2InitializationCompletedEventArgs>? initHandler;
    bool disposed;

    public event Action<string, string?>? Received;
    public event Action? Lost;

    public WebView2Transport(WebView control, NavigationFilter allowNavigation, Action<string> openNewWindow)
    {
        this.control = control;
        this.allowNavigation = allowNavigation;
        this.openNewWindow = openNewWindow;
    }

    public Uri PageUri(string uiFolder)
    {
        this.uiFolder = uiFolder;
        return new Uri($"https://{VirtualHost}/index.html");
    }

    public void Load(Uri uri)
    {
        target = uri;
        if (core != null) Navigate(core);
        else HookCore();
    }

    public void Post(string json)
    {
        if (core == null) return;
        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WebView torn down / navigating; the page re-syncs on its next "ready".
        }
    }

    void HookCore()
    {
        // The WPF control creates CoreWebView2 asynchronously once it is in the visual tree.
        if (initHandler != null || disposed) return;
        native = control.ControlObject;
        if (TryGetCore(native) is { } ready)
        {
            Attach(ready);
            return;
        }
        initEvent = native?.GetType().GetEvent("CoreWebView2InitializationCompleted");
        if (initEvent == null)
        {
            RhinoApp.WriteLine("Freppuccino: side panel needs WebView2 (Windows).");
            return;
        }
        initHandler = (_, e) =>
        {
            Unhook();
            if (disposed) return;
            if (!e.IsSuccess)
            {
                RhinoApp.WriteLine($"Freppuccino: WebView2 failed to start: {e.InitializationException?.Message}");
                Lost?.Invoke();
                return;
            }
            if (TryGetCore(native) is { } c) Attach(c);
        };
        initEvent.AddEventHandler(native, initHandler);
        if (TryGetCore(native) is { } late)
        {
            Unhook();
            Attach(late);
        }
    }

    void Unhook()
    {
        if (initHandler != null) initEvent?.RemoveEventHandler(native, initHandler);
        initHandler = null;
    }

    static CoreWebView2? TryGetCore(object? native)
    {
        try
        {
            return native?.GetType().GetProperty("CoreWebView2")?.GetValue(native) as CoreWebView2;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    void Attach(CoreWebView2 c)
    {
        if (disposed || core == c) return;
        core = c;
        c.Settings.AreDevToolsEnabled = WebViewHost.DevTools; // F12 / right-click → Inspect
        c.Settings.AreDefaultContextMenusEnabled = WebViewHost.DevTools;
        c.Settings.IsStatusBarEnabled = false;
        c.Settings.IsZoomControlEnabled = false;
        c.Settings.IsWebMessageEnabled = true;

        c.WebMessageReceived += OnWebMessageReceived;
        c.NavigationStarting += OnNavigationStarting;
        c.NewWindowRequested += OnNewWindowRequested;
        c.ProcessFailed += OnProcessFailed;
        Navigate(c);
    }

    void Navigate(CoreWebView2 c)
    {
        if (target == null) return;
        if (target.Host == VirtualHost && uiFolder != null)
            c.SetVirtualHostNameToFolderMapping(VirtualHost, uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        c.Navigate(target.AbsoluteUri);
    }

    void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                Detach();
                Lost?.Invoke();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                Lost?.Invoke();
                break;
        }
    }

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!allowNavigation(e.Uri)) e.Cancel = true;
    }

    void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        openNewWindow(e.Uri);
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) =>
        Received?.Invoke(e.WebMessageAsJson, e.Source);

    void Detach()
    {
        if (core == null) return;
        core.WebMessageReceived -= OnWebMessageReceived;
        core.NavigationStarting -= OnNavigationStarting;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.ProcessFailed -= OnProcessFailed;
        core = null;
    }

    public void Dispose()
    {
        disposed = true;
        Unhook();
        Detach();
        Received = null;
        Lost = null;
    }
}
