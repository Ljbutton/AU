using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The Live desk page's script: lists keep one node per item even when two items share a key (two OBS
/// output warnings both titled "OBS output"), held keys act once per press, closing the replay keys
/// popover stops waiting for a key, and Panic undoes only a panic that's on in full.
/// </summary>
public class CasterPageScriptTests
{
    private static string Page()
    {
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream("ui/caster.html")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The page's text from the line starting with <paramref name="from"/> up to the next line that is just "}" or "});".</summary>
    private static string Block(string page, string from)
    {
        var m = Regex.Match(page, Regex.Escape(from) + ".*?\\n\\}\\)?;?\\n", RegexOptions.Singleline);
        Assert.True(m.Success, "no " + from);
        return m.Value;
    }

    [Fact]
    public void Warnings_are_keyed_by_title_and_text()
    {
        string page = Page();
        Assert.Contains("syncList($('desk-warn'), w, x => x.title + '\\n' + x.text,", page);
    }

    [Fact]
    public void Held_keys_act_once_on_the_live_desk_and_only_scrub_keys_repeat_in_a_replay()
    {
        string page = Page();
        string desk = Block(page, "const holdToggle = () =>");
        Assert.Contains("if (e.repeat) return;", desk);
        // The repeat check comes before anything acts (Esc, Enter, P, digits, H).
        Assert.True(desk.IndexOf("if (e.repeat) return;") < desk.IndexOf("e.key === 'Escape'"));
        string replay = Block(page, "const KEY_REPEATS =");
        Assert.Contains("if (e.repeat && !KEY_REPEATS.has(name))", replay);
        foreach (var once in new[] { "playPause", "restart", "live", "angle", "follow", "intermission", "voiceMute", "alertsPause" })
            Assert.DoesNotContain($"'{once}'", replay.Substring(0, replay.IndexOf('\n')));
    }

    [Fact]
    public void Panic_and_the_hold_pill_undo_only_while_the_panic_is_on_in_full()
    {
        // Not the hold's reason: a hold left as "Panic" (another lobby taken by hand, the hold kept) would
        // turn the next Panic into Undo, which switches lobbies instead of going to be right back.
        string page = Page();
        Assert.Contains("if (DESK && DESK.panic) { deckDo('undo'); return; }", Block(page, "function panicPress()"));
        Assert.Contains("$('pill-hold').addEventListener('click', () => { if (DESK && DESK.panic) deckDo('undo'); else holdToggle(); });", page);
        Assert.Contains("panic = !!d.panic;", Block(page, "function renderBreaks()"));
        Assert.DoesNotContain("why === 'Panic') deckDo", page);
        Assert.DoesNotContain("why === 'Panic') { deckDo", page);
    }

    [Fact]
    public void The_live_desk_keeps_its_icon_rail_and_one_screen_in_a_smaller_window()
    {
        // The body has class "ld" on the Live desk too: a rule for plain ".ld" would lay the body out as
        // well (in a column, the side menu as a full-width strip). Only the desk's own div is laid out.
        string page = Page();
        string css = page.Substring(0, page.IndexOf("</style>", StringComparison.Ordinal));
        Assert.DoesNotMatch(new Regex(@"(^|[\s,}])\.ld\{", RegexOptions.Multiline), css);
        Assert.Contains("div.ld{flex:1;", css);
        Assert.Contains("div.ld{display:flex;flex-direction:column}", css);
        // One long page only below about 1000 px (Red Alert's first window was 1180 × 780).
        Assert.Contains("@media (max-width:1000px),(max-height:620px){", css);
        Assert.DoesNotContain("@media (max-width:1200px),(max-height:620px)", css);
    }

    [Fact]
    public void A_cancelled_armed_camera_is_said_once_and_lobby_numbers_are_set_in_Settings()
    {
        string page = Page();
        Assert.Contains("if (d.armedDropped && d.armedDropped.at !== droppedSaid)", page);
        Assert.Contains("api('POST', '/app/admin/numbers', body)", page);
        Assert.Contains("deckNumbers({ action: 'renumber' })", page);
    }

    [Fact]
    public void Closing_the_keys_popover_or_Esc_stops_waiting_for_a_key()
    {
        string page = Page();
        Assert.Contains("keyWait = null", Block(page, "function closePops()"));
        string replay = Block(page, "const KEY_REPEATS =");
        int esc = replay.IndexOf("if (e.code === 'Escape') { keyWait = null; renderKeys(); return; }");
        Assert.True(esc > 0, "Esc cancels the wait");
        Assert.True(esc < replay.IndexOf("map[keyWait] = e.code"), "Esc is checked before the key is saved");
    }

    [Fact]
    public void The_win_counter_is_gone_from_the_desk_and_the_graphics()
    {
        string page = Page();
        foreach (var gone in new[] { "data-wins", "/app/admin/wins", "gfx-wins", "winScope", "'winCounter'", "Win counter" })
            Assert.DoesNotContain(gone, page);
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream("ui/broadcast.html")!;
        string graphics = new StreamReader(s).ReadToEnd();
        foreach (var gone in new[] { "winCounter", "S.wins", "IMPOSTOR WINS", ".wins{" })
            Assert.DoesNotContain(gone, graphics);
    }

    /// <summary>
    /// syncList itself, run in Node with a small stand-in for the DOM (skipped where Node isn't installed).
    /// </summary>
    [Fact]
    public void A_list_with_two_items_that_share_a_key_does_not_grow()
    {
        string page = Page();
        var m = Regex.Match(page, "function syncList\\(.*?\\n\\}\\n", RegexOptions.Singleline);
        Assert.True(m.Success);
        string script = Dom + m.Value + Checks;
        string? output = RunNode(script);
        if (output == null) return;              // no Node here
        Assert.Equal("ok", output.Trim());
    }

    private const string Dom = """
        class El {
          constructor(h) { this.h = h; this.dataset = {}; this.kids = []; this.parent = null; }
          get children() { return this.kids; }
          set innerHTML(v) { for (const c of this.kids) c.parent = null; this.kids = []; this.inner = v; }
          contains(x) { for (; x; x = x.parent) if (x === this) return true; return false; }
          remove() { if (!this.parent) return; const a = this.parent.kids; a.splice(a.indexOf(this), 1); this.parent = null; }
          replaceWith(n) { const p = this.parent; n.remove(); p.kids[p.kids.indexOf(this)] = n; n.parent = p; this.parent = null; }
          insertBefore(n, ref) { n.remove(); const i = ref ? this.kids.indexOf(ref) : this.kids.length; this.kids.splice(i, 0, n); n.parent = this; }
        }
        let ptrDown = null, ptrAgain = false;
        const makeEl = h => new El(h);

        """;

    private const string Checks = """

        const assert = (ok, what) => { if (!ok) { console.log('FAIL: ' + what); process.exit(1); } };
        const box = new El('');
        const html = x => x.title + ': ' + x.text;
        const two = [{ title: 'OBS output', text: "Streaming, but OBS isn't recording." }, { title: 'OBS output', text: 'The stream is dropping frames' }];
        for (let i = 0; i < 10; i++) syncList(box, two, x => x.title, html, 'none');
        assert(box.children.length === 2, 'two items, same key: ' + box.children.length + ' nodes after 10 renders');
        assert(box.children.map(c => c.h).join('|') === two.map(html).join('|'), 'in order');
        const kept = [...box.children];
        syncList(box, two, x => x.title, html, 'none');
        assert(box.children[0] === kept[0] && box.children[1] === kept[1], 'unchanged items keep their nodes');
        syncList(box, two.slice(1), x => x.title, html, 'none');
        assert(box.children.length === 1 && box.children[0].h === html(two[1]), 'one left');
        // Nodes left over with a key twice (as before this fix) are cleared up on the next render.
        const stale = new El('stale'); stale.dataset.k = 'OBS output'; box.insertBefore(stale, null);
        syncList(box, two.slice(1), x => x.title, html, 'none');
        assert(box.children.length === 1, 'leftover node with the same key removed: ' + box.children.length);
        // Keyed by title and text (as the warnings are), and a key that looks like a made-up one.
        const three = [...two, { title: 'OBS output#2', text: 'x' }];
        for (let i = 0; i < 5; i++) syncList(box, three, x => x.title, html, 'none');
        assert(box.children.length === 3, 'three items: ' + box.children.length);
        for (let i = 0; i < 5; i++) syncList(box, two, x => x.title + '\n' + x.text, html, 'none');
        assert(box.children.length === 2, 'title and text: ' + box.children.length);
        syncList(box, [], x => x.title, html, 'none');
        assert(box.children.length === 0 && box.inner === 'none', 'empty');
        console.log('ok');
        """;

    private static string? RunNode(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), $"synclist-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            var psi = new ProcessStartInfo("node", $"\"{file}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            Process p;
            try { p = Process.Start(psi)!; }
            catch (Win32Exception) { return null; }
            using (p)
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                Assert.True(p.WaitForExit(30000), "node timed out");
                Assert.True(p.ExitCode == 0, stdout + stderr);
                return stdout;
            }
        }
        finally { File.Delete(file); }
    }
}
