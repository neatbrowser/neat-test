using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace neat;

/// <summary>
/// The ad blocker's part of the window: it asks App.Ads about every request a tab makes, stops
/// the ones that should be stopped, and gives each page its hiding rules and scriptlets before the
/// page's own scripts run. The decisions are made in ads/; this file only talks to WebView2.
/// </summary>
public sealed partial class Win
{
    /// <summary>Most scripts registered for one page: its own host plus the hosts of its frames.</summary>
    private const int MaxPageScripts = 40;

    /// <summary>
    /// The longest a request is held back to wait for the page script to be registered. Waiting
    /// for it without a limit froze every page (the registration did not finish while the
    /// request was held), so the request is always let go after this.
    /// </summary>
    private static readonly TimeSpan MaxHold = TimeSpan.FromMilliseconds(300);

    private readonly ConditionalWeakTable<Tab, TabAds> _ads = new();

    private TabAds AdsOf(Tab tab) => _ads.GetValue(tab, _ => new TabAds());

    /// <summary>
    /// Starts watching a tab's requests. Called once per tab, right after its web view exists and
    /// before it navigates anywhere.
    /// </summary>
    private void AdsHook(Tab tab, CoreWebView2 core)
    {
        var st = AdsOf(tab);

        try
        {
            // Dedicated workers, shared workers and service workers make requests of their own
            // (many trackers and ad loaders run there). The plain filter only sees the page's.
            try
            {
                // The three-argument form is projected under different names by different SDK
                // versions (an overload of AddWebResourceRequestedFilter, or a method of its own),
                // so it is looked up by shape instead of by name.
                var add = typeof(CoreWebView2).GetMethods()
                    .FirstOrDefault(m => m.Name.StartsWith("AddWebResourceRequestedFilter", StringComparison.Ordinal)
                        && m.GetParameters().Length == 3)
                    ?? throw new MissingMethodException("no filter method with source kinds");
                add.Invoke(core, new object[]
                {
                    "*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All,
                });
            }
            catch (Exception ex)
            {
                Log.Write("ads", "requests of workers cannot be watched here: " + ex.Message);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not watch the requests of a tab");
            return;
        }

        core.NavigationStarting += (s, e) => AdsNavStart(core, st, e);
        core.WebResourceRequested += (s, e) => AdsRequest(tab, core, st, e);
        core.WebMessageReceived += (s, e) => AdsMessage(core, st, e);
    }

    /// <summary>A new top-level navigation: the old page's scripts are taken away.</summary>
    private void AdsNavStart(CoreWebView2 core, TabAds st, CoreWebView2NavigationStartingEventArgs e)
    {
        st.Nav = e.Uri;
        st.NavPending = true;
        st.Page = e.Uri;
        st.Blocked = 0;
        st.Top = Cosmetic.Empty;
        st.BlockedUrl = null;

        st.Gen++;

        foreach (var id in st.Scripts.Values)
        {
            if (id.Length == 0)
                continue;

            try
            {
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            }
            catch (Exception ex)
            {
                Log.Write("ads", "could not remove a page script: " + ex.Message);
            }
        }

        st.Scripts.Clear();
        st.MainReg = null;
        st.MainJs = null;

        // The page's script is prepared now, before its request exists, and not waited for here.
        // (Not for a page that is about to be replaced by the warning.)
        if (AdUrl.IsWeb(e.Uri) && App.Ads.Ready && !App.Ads.IsOff(AdUrl.Host(e.Uri))
            && !(AdsStopsPage(st, e.Uri)))
            st.MainReg = AdsPage(core, st, e.Uri, main: true);
    }

    /// <summary>One request the page (or one of its frames) is about to make.</summary>
    private void AdsRequest(Tab tab, CoreWebView2 core, TabAds st, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var ads = App.Ads;
        var url = e.Request.Uri;
        var context = e.ResourceContext;
        var kind = context.ToString();

        // A request a worker makes is judged like the page's, and told apart in the log.
        var worker = false;
        try
        {
            worker = e.RequestedSourceKind != CoreWebView2WebResourceRequestSourceKinds.Document;
        }
        catch (Exception)
        {
            // An older runtime does not say; it only sends the page's requests anyway.
        }

        // What the log shows; the type used for judging stays the plain one.
        var shownKind = worker ? kind + "@worker" : kind;

        var method = e.Request.Method;
        var logging = AdRequestLog.Enabled;

        if (!ads.Ready)
        {
            if (logging)
                AdRequestLog.Add("skip:engine-not-ready", shownKind, method, st.Page, url);
            return;
        }

        if (!AdUrl.IsWeb(url))
        {
            if (logging)
                AdRequestLog.Add("skip:not-web", shownKind, method, st.Page, url);
            return;
        }

        var doc = context == CoreWebView2WebResourceContext.Document && !worker;

        // The page's own document is the first one requested after NavigationStarting. The hosts
        // are compared, not the whole address: the two events may write the same address a little
        // differently, and a page must never be taken for a frame (a frame can be stopped).
        var main = doc && st.NavPending && AdUrl.Host(url) == AdUrl.Host(st.Nav);
        if (main)
            st.NavPending = false;

        // The page asking is the page the tab shows; for the page itself it is the page.
        var page = main ? url : st.Page;
        if (ads.IsOff(AdUrl.Host(page)))
        {
            if (logging)
                AdRequestLog.Add("skip:blocker-off-for-page", shownKind, method, page, url);
            return;
        }

        if (main)
        {
            // The page itself is stopped only by a page rule ($document / $all), and only until the
            // person chooses to go on; the warning has a way back.
            if (AdsStopsPage(st, url))
            {
                if (logging)
                    AdRequestLog.Add("block:page", shownKind, method, page, url);

                Log.Write("ads", "page stopped: " + url);
                st.Blocked++;
                st.BlockedUrl = url;
                var bd = e.GetDeferral();
                _ = AdsBlockPage(core, e, url, bd);
                return;
            }

            // Otherwise it only gets its hiding rules, whose script was started at NavigationStarting.
            if (logging)
                AdRequestLog.Add("skip:main-page", shownKind, method, page, url);

            var reg = st.MainReg ?? AdsPage(core, st, url, main: true);
            var d = e.GetDeferral();
            _ = AdsHold(reg, d, url, core, st, main: true);
            return;
        }

        // A document request that is not the page is a frame. One on the page's own host is left alone.
        if (doc && AdUrl.Host(url) == AdUrl.Host(page))
        {
            if (logging)
                AdRequestLog.Add("skip:frame-same-host", shownKind, method, page, url);

            var reg = AdsPage(core, st, url, main: false);
            var d = e.GetDeferral();
            _ = AdsHold(reg, d, url, core, st, main: false);
            return;
        }

        var verdict = ads.Check(url, page, ReqType.From(kind, false), method);

        if (verdict.Action == AdAction.Block
            || (verdict.Action == AdAction.Redirect && !DataUrl.TryParse(verdict.Redirect, out _, out _)))
        {
            if (logging)
                AdRequestLog.Add(verdict.Action == AdAction.Block ? "block" : "block:redirect-unusable", shownKind, method, page, url);

            st.Blocked++;
            e.Response = doc && !worker
                ? core.Environment.CreateWebResourceResponse(null!, 200, "OK", "Content-Type: text/html")
                : Refused(core);
            return;
        }

        if (verdict.Action == AdAction.Redirect)
        {
            if (logging)
                AdRequestLog.Add("redirect", shownKind, method, page, url);

            st.Blocked++;
            var d = e.GetDeferral();
            _ = AdsRedirect(core, e, verdict.Redirect!, d);
            return;
        }

        if (logging)
            AdRequestLog.Add("allow", shownKind, method, page, url);

        if (doc)
        {
            // A frame that is let through still gets the rules for its own host.
            var reg = AdsPage(core, st, url, main: false);
            var d = e.GetDeferral();
            _ = AdsHold(reg, d, url, core, st, main: false);
        }
    }

    /// <summary>
    /// The answer for a request that is stopped. The page must see a failed connection, as it does
    /// with uBO: a 403 reply counts as "the host answered" for fetch and XHR, and ad-block tests
    /// (and some sites' own checks) read that as "not blocked". A redirect to a data: address is
    /// refused by the browser itself for every kind of request, which the page sees as a network
    /// error. (Tried in Chromium: fetch rejects, XHR and script/image loads report an error.)
    /// </summary>
    private static CoreWebView2WebResourceResponse Refused(CoreWebView2 core) =>
        core.Environment.CreateWebResourceResponse(
            null!, 302, "Found", "Location: data:,\r\nCache-Control: no-store");

    /// <summary>True when a page rule stops this address and the person has not chosen to go on.</summary>
    private static bool AdsStopsPage(TabAds st, string url)
    {
        var host = AdUrl.Host(url);
        return host is not null && !st.Allowed.Contains(host) && App.Ads.BlockDocument(url);
    }

    /// <summary>Answers the page's own request with the warning page.</summary>
    private async Task AdsBlockPage(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string url, Windows.Foundation.Deferral d)
    {
        try
        {
            var html = BlockPage.Build(url, AdUrl.Host(url) ?? url, _tok);
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(System.Text.Encoding.UTF8.GetBytes(html));
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            e.Response = core.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" +
                "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'");
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not show the warning page");
        }
        finally
        {
            d.Complete();
        }
    }

    /// <summary>Answers a request with the engine's stand-in resource (a blank script, a pixel, ...).</summary>
    private async Task AdsRedirect(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string dataUrl, Windows.Foundation.Deferral d)
    {
        try
        {
            if (!DataUrl.TryParse(dataUrl, out var mime, out var body))
                return;

            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(body);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            e.Response = core.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store");
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not build a replacement resource");
        }
        finally
        {
            d.Complete();
        }
    }

    /// <summary>
    /// Holds a document request back until its page script is registered, but never longer than
    /// <see cref="MaxHold"/>. Then lets the request go either way.
    ///
    /// Registering a script can take a second or more (WebView2 does it when its browser process
    /// has time, for example while it starts a process for another site), and a page that started
    /// without it would have neither the scriptlets nor the hiding. So for the page itself, when
    /// the registration is late, the script is also run in the page as soon as it is registered.
    /// The script runs only once per document, so being run twice does no harm.
    /// </summary>
    private async Task AdsHold(Task registration, Windows.Foundation.Deferral d, string url, CoreWebView2 core, TabAds st, bool main)
    {
        var gen = st.Gen;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = await Task.WhenAny(registration, Task.Delay(MaxHold));
            if (first == registration)
            {
                Log.Write("ads", $"page script ready {clock.ElapsedMilliseconds} ms after the request: {AdUrl.Host(url)}");
            }
            else
            {
                Log.Write("ads", $"page script NOT ready after {MaxHold.TotalMilliseconds:0} ms, the page goes on without waiting: {AdUrl.Host(url)}");
                if (main)
                    _ = AdsLate(registration, core, st, gen, url);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "waiting for the page script failed");
        }
        finally
        {
            d.Complete();
        }
    }

    /// <summary>Runs the page's script in the page itself once its registration has finished late.</summary>
    private static async Task AdsLate(Task registration, CoreWebView2 core, TabAds st, int gen, string url)
    {
        try
        {
            await registration;

            // Another page has started since: this script belongs to the old one.
            var js = st.MainJs;
            if (gen != st.Gen || js is null)
                return;

            await core.ExecuteScriptAsync(js);
            if (AdRequestLog.Enabled)
                Log.Write("ads", $"late page script run in the page: {AdUrl.Host(url)}");
        }
        catch (Exception ex)
        {
            Log.Write("ads", "could not run the late page script: " + ex.Message);
        }
    }

    /// <summary>
    /// Prepares and registers the script that hides a document's elements and runs its scriptlets
    /// (for the page, or for a frame in it). Never throws. The caller decides whether to wait.
    /// </summary>
    private async Task AdsPage(CoreWebView2 core, TabAds st, string url, bool main)
    {
        try
        {
            var host = AdUrl.Host(url);
            if (host is null || st.Scripts.ContainsKey(host) || st.Scripts.Count >= MaxPageScripts)
                return;

            var gen = st.Gen;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // The engine's answer can take a while for a site with many rules; the UI thread must not wait for it.
            var rules = await Task.Run(() => App.Ads.PageRules(url));
            var tRules = clock.ElapsedMilliseconds;
            if (main)
                st.Top = rules;

            // Generic rules by class and id are the top frame's business only.
            var generic = main && !rules.GenericHide;
            if (rules.HideSelectors.Length == 0 && rules.InjectedScript.Length == 0 && rules.Procedural == 0 && !generic)
                return;

            var js = PageJs.Build(Adblock.Template, host, _tok, rules.HideSelectors, generic, rules.InjectedScript, rules.ProceduralActions);
            var tBuild = clock.ElapsedMilliseconds;
            if (main && gen == st.Gen)
                st.MainJs = js;

            // A frame of the same host may have asked meanwhile; the first one wins.
            if (st.Scripts.ContainsKey(host))
                return;

            st.Scripts[host] = string.Empty;
            var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(js);
            var tAdd = clock.ElapsedMilliseconds;

            // A new page started while the script was being added: it belongs to the old page.
            if (gen != st.Gen)
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            else
                st.Scripts[host] = id;

            if (AdRequestLog.Enabled || tAdd >= 100)
            {
                Log.Write("ads",
                    $"{host}: page script took {tAdd} ms (rules {tRules} ms, build {tBuild - tRules} ms, register {tAdd - tBuild} ms); " +
                    $"{js.Length} bytes, {rules.HideSelectors.Length} hide, {rules.Procedural} procedural, {rules.InjectedScript.Length} bytes of scriptlets");
            }
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not prepare the page rules");
        }
    }

    /// <summary>
    /// The page's script reports the classes and ids it uses; the answer is the generic selectors
    /// to hide. Anything that does not carry this window's secret is not ours.
    /// </summary>
    private void AdsMessage(CoreWebView2 core, TabAds st, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? msg;
        try
        {
            msg = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        var head = "neat-ads:" + _tok + ":";
        if (msg is null || msg.Length > 400_000 || !msg.StartsWith(head, StringComparison.Ordinal))
            return;

        try
        {
            using var doc = JsonDocument.Parse(msg[head.Length..]);

            // "Continue anyway" on the warning page. Only the address the warning is for is accepted.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("proceed", out var go)
                && go.ValueKind == JsonValueKind.String)
            {
                var target = go.GetString();
                if (target is not null && target == st.BlockedUrl && AdUrl.Host(target) is { } h)
                {
                    st.Allowed.Add(h);
                    st.BlockedUrl = null;
                    Log.Write("ads", "continuing to a stopped page: " + target);
                    core.Navigate(target);
                }

                return;
            }

            var classes = Names(doc.RootElement, "c");
            var ids = Names(doc.RootElement, "i");
            if (classes.Count == 0 && ids.Count == 0)
                return;

            var reply = App.Ads.Generic(classes, ids, st.Top.Exceptions);
            if (reply is not null)
                core.PostWebMessageAsString(head + reply);
        }
        catch (Exception ex)
        {
            Log.Write("ads", "bad message from a page: " + ex.Message);
        }
    }

    private static List<string> Names(JsonElement root, string key)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var x in a.EnumerateArray())
        {
            if (list.Count >= 2000)
                break;

            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 and <= 200 } t)
                list.Add(t);
        }

        return list;
    }

    /// <summary>True when the window a page asked for is an ad's and should not be opened.</summary>
    private bool AdsBlockPopup(Tab tab, string target)
    {
        var ads = App.Ads;
        var st = AdsOf(tab);
        if (!ads.Ready || !AdUrl.IsWeb(target) || !AdUrl.IsWeb(st.Page) || ads.IsOff(AdUrl.Host(st.Page)))
            return false;

        if (!ads.BlockPopup(target, st.Page))
            return false;

        Log.Write("ads", $"popup stopped: {target}");
        return true;
    }
}
