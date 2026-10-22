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
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
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
    }

    /// <summary>One request the page (or one of its frames) is about to make.</summary>
    private void AdsRequest(Tab tab, CoreWebView2 core, TabAds st, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var ads = App.Ads;
        if (!ads.Ready)
            return;

        var url = e.Request.Uri;
        if (!AdUrl.IsWeb(url))
            return;

        var context = e.ResourceContext;
        var doc = context == CoreWebView2WebResourceContext.Document;

        // The page's own document is the first one requested after NavigationStarting. The hosts
        // are compared, not the whole address: the two events may write the same address a little
        // differently, and a page must never be taken for a frame (a frame can be stopped).
        var main = doc && st.NavPending && AdUrl.Host(url) == AdUrl.Host(st.Nav);
        if (main)
            st.NavPending = false;

        // The page asking is the page the tab shows; for the page itself it is the page.
        var page = main ? url : st.Page;
        if (ads.IsOff(AdUrl.Host(page)))
            return;

        if (main)
        {
            // The page itself is never stopped (there is no way back from a blank page yet);
            // it only gets its hiding rules.
            var d = e.GetDeferral();
            _ = AdsPage(core, st, url, main: true, d);
            return;
        }

        // A document request that is not the page is a frame. One on the page's own host is left alone.
        if (doc && AdUrl.Host(url) == AdUrl.Host(page))
        {
            var d = e.GetDeferral();
            _ = AdsPage(core, st, url, main: false, d);
            return;
        }

        var verdict = ads.Check(url, page, ReqType.From(context.ToString(), false), e.Request.Method);

        if (verdict.Action == AdAction.Block
            || (verdict.Action == AdAction.Redirect && !DataUrl.TryParse(verdict.Redirect, out _, out _)))
        {
            st.Blocked++;
            e.Response = doc
                ? core.Environment.CreateWebResourceResponse(null!, 200, "OK", "Content-Type: text/html")
                : core.Environment.CreateWebResourceResponse(null!, 403, "Blocked", "Cache-Control: no-store");
            return;
        }

        if (verdict.Action == AdAction.Redirect)
        {
            st.Blocked++;
            var d = e.GetDeferral();
            _ = AdsRedirect(core, e, verdict.Redirect!, d);
            return;
        }

        if (doc)
        {
            // A frame that is let through still gets the rules for its own host.
            var d = e.GetDeferral();
            _ = AdsPage(core, st, url, main: false, d);
        }
    }

    /// <summary>Answers a request with the engine's stand-in resource (a blank script, a pixel, ...).</summary>
    private async Task AdsRedirect(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string dataUrl, CoreWebView2Deferral d)
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
    /// A document is about to load (the page, or a frame in it): registers the script that hides
    /// its elements and runs its scriptlets. It runs inside the request's deferral, so the script is
    /// in place before the document is created.
    /// </summary>
    private async Task AdsPage(CoreWebView2 core, TabAds st, string url, bool main, CoreWebView2Deferral d)
    {
        try
        {
            var host = AdUrl.Host(url);
            if (host is null || st.Scripts.ContainsKey(host) || st.Scripts.Count >= MaxPageScripts)
                return;

            var gen = st.Gen;
            var rules = App.Ads.PageRules(url);
            if (main)
                st.Top = rules;

            // Generic rules by class and id are the top frame's business only.
            var generic = main && !rules.GenericHide;
            if (rules.HideSelectors.Length == 0 && rules.InjectedScript.Length == 0 && !generic)
                return;

            var js = PageJs.Build(Adblock.Template, host, _tok, rules.HideSelectors, generic, rules.InjectedScript);

            // A frame of the same host may have asked meanwhile; the first one wins.
            if (st.Scripts.ContainsKey(host))
                return;

            st.Scripts[host] = string.Empty;
            var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(js);

            // A new page started while the script was being added: it belongs to the old page.
            if (gen != st.Gen)
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            else
                st.Scripts[host] = id;

            if (rules.Procedural > 0)
                Log.Write("ads", $"{host}: {rules.Procedural} procedural rules skipped (not supported yet)");
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not prepare the page rules");
        }
        finally
        {
            d.Complete();
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
