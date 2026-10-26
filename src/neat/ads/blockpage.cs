using System.Net;
using System.Text.Json;

namespace neat;

/// <summary>
/// The page shown in place of a page that a page rule ($document, $all) stops. It has a way back
/// and a way on; "continue" asks the window, with the window's secret, to open the address anyway.
/// </summary>
internal static class BlockPage
{
    public static string Build(string url, string host, string token)
    {
        var shown = url.Length > 200 ? url[..200] + "…" : url;
        // JsonSerializer escapes < > & ' so the values cannot close the script or the string.
        var jsUrl = JsonSerializer.Serialize(url);
        var jsHead = JsonSerializer.Serialize("neat-ads:" + token + ":");

        var html = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Page blocked</title>
<style>
:root{color-scheme:light dark;--bg:#fff;--fg:#1d1d1f;--mut:#6b6b70;--acc:#c62828;--card:#f5f5f7;--line:#dcdce0}
@media (prefers-color-scheme:dark){:root{--bg:#1b1b1d;--fg:#ececee;--mut:#9a9aa0;--acc:#ef5350;--card:#26262a;--line:#3a3a40}}
html,body{height:100%;margin:0}
body{background:var(--bg);color:var(--fg);font:15px/1.5 "Segoe UI",system-ui,sans-serif;display:flex;align-items:center;justify-content:center}
main{max-width:520px;padding:0 24px}
h1{font-size:24px;margin:0 0 8px;color:var(--acc)}
p{margin:0 0 14px;color:var(--mut)}
code{display:block;word-break:break-all;background:var(--card);border:1px solid var(--line);border-radius:6px;padding:8px 10px;color:var(--fg);font:13px Consolas,monospace;margin:0 0 20px}
button{font:inherit;border-radius:6px;padding:8px 18px;cursor:pointer;margin-right:8px;border:1px solid var(--line)}
#back{background:var(--acc);color:#fff;border-color:var(--acc)}
#go{background:transparent;color:var(--mut)}
</style></head>
<body><main>
<h1>This page was blocked</h1>
<p>NEAT's blocker stopped <b>@HOST@</b> because the filter lists mark the whole site as unsafe or unwanted (malware, scams, or aggressive ads).</p>
<code>@SHOWN@</code>
<button id="back">Go back</button><button id="go">Continue anyway</button>
<script>
document.getElementById('back').onclick=function(){if(history.length>1)history.back();else location.replace('about:blank');};
document.getElementById('go').onclick=function(){
  var w=window.chrome&&window.chrome.webview;
  if(w)w.postMessage(@HEAD@+JSON.stringify({proceed:@URL@}));
};
</script>
</main></body></html>
""";

        // One pass, so text that came from the address is never searched for placeholders.
        return System.Text.RegularExpressions.Regex.Replace(html, "@(HOST|SHOWN|HEAD|URL)@", m => m.Groups[1].Value switch
        {
            "HOST" => WebUtility.HtmlEncode(host),
            "SHOWN" => WebUtility.HtmlEncode(shown),
            "HEAD" => jsHead,
            _ => jsUrl,
        });
    }
}
