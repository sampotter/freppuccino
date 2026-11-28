namespace Freppuccino.Ui;

/// <summary>
/// Per-OS part of <see cref="WebViewHost"/>: loading, raw JSON in/out, and enforcing the navigation policy.
/// Everything else (start URL, origin check, parsing, serialization) is shared.
/// </summary>
interface IWebTransport : IDisposable
{
    /// URL that serves &lt;uiFolder&gt;/index.html.
    Uri PageUri(string uiFolder);

    void Load(Uri uri);

    /// <paramref name="json"/> is one serialized HostMessage.
    void Post(string json);

    /// (message JSON, URL of the sending document or null if unknown). Raised on the UI thread.
    event Action<string, string?>? Received;

    event Action? Lost;
}

/// Navigation policy from <see cref="WebViewHost"/>: return false to cancel (the host then opens the link externally).
delegate bool NavigationFilter(string uri);
