namespace neat;

/// <summary>Turns WebView2's resource context into the request type names the engine knows.</summary>
internal static class ReqType
{
    /// <param name="context">The name of the CoreWebView2WebResourceContext value.</param>
    /// <param name="mainFrame">True when the request is the page itself, not a frame inside it.</param>
    public static string From(string context, bool mainFrame) => context switch
    {
        "Document" => mainFrame ? "document" : "subdocument",
        "Stylesheet" => "stylesheet",
        "Image" => "image",
        "Media" => "media",
        "Font" => "font",
        "Script" => "script",
        "XmlHttpRequest" or "Fetch" => "xmlhttprequest",
        "Websocket" => "websocket",
        "Ping" or "CspViolationReport" => "ping",
        _ => "other",
    };
}
