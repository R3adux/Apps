using Licht.Fenster;

namespace Licht.Fenster.Tests;

public class ViewModelTests
{
    private readonly FakeHub _hub = new();
    private readonly FakeUi _ui = new();
    private readonly FakeClock _clock = new();

    private MainViewModel Window(params string[] lights)
    {
        var main = new MainViewModel(_hub, _ui, _clock);
        main.Start();
        _hub.Raise("""{"t":"hello","autostart":true,"hint":true}""");
        _hub.Raise(Lights.Message(lights));
        return main;
    }

    private static Dictionary<string, object?> Payload(object? args) => (Dictionary<string, object?>)((object?[])args!)[1]!;

    [Fact]
    public void Shows_the_lamps_and_their_state()
    {
        var main = Window(Lights.Json("1", "Flur", on: true, brightness: 70), Lights.Json("2", "Küche", status: "offline"));
        Assert.Equal(new[] { "ready" }, _hub.Sent.Select(m => m["t"]));
        Assert.True(main.Loaded && main.Autostart && main.ShowHint && !main.IsEmpty);
        Assert.Equal(new[] { "An · 70 %", "Nicht erreichbar" }, main.Lamps.Select(l => l.StateText));
        Assert.Equal("Alle aus", main.AllToggleText);
        Assert.True(main.Lamps[0].ShowControls && main.Lamps[0].IsWhite && main.Lamps[0].ShowModeSwitch);
        Assert.False(main.Lamps[1].ShowControls);
        Assert.Equal(11, main.Lamps[0].Presets.Count);

        _hub.Raise(Lights.Message(Lights.Json("2", "Küche", status: "connecting")));
        Assert.Equal("Verbinde … · Aus", Assert.Single(main.Lamps).StateText);
        Assert.Equal("Alle an", main.AllToggleText);
        _hub.Raise(Lights.Message());
        Assert.True(main.IsEmpty);
    }

    [Fact]
    public void Dimming_sends_only_the_brightness_and_always_the_newest_value()
    {
        var main = Window(Lights.Json("1", "Flur", on: true, brightness: 50));
        var lamp = main.Lamps[0];
        lamp.Brightness = 60;
        lamp.Brightness = 61; // while the first one is on its way
        lamp.Brightness = 62;
        Assert.Single(_hub.Calls);
        Assert.Equal("{\"brightness\":60}", Json.Write(Payload(_hub.Calls[0].Args)));
        Assert.Equal("An · 62 %", lamp.StateText); // the screen shows the newest value at once

        _hub.Calls[0].Reply.SetResult(null);
        _clock.Advance(TimeSpan.FromMilliseconds(60)); // the gap between commands
        Assert.Equal(2, _hub.Calls.Count);
        Assert.Equal("{\"brightness\":62}", Json.Write(Payload(_hub.Calls[1].Args))); // 61 skipped: stale
    }

    [Fact]
    public void Changing_the_light_of_a_lamp_that_is_off_switches_it_on()
    {
        var main = Window(Lights.Json("1", "Flur"));
        main.Lamps[0].ApplyPreset(Preset.All.First(p => p.Label == "Blau"));
        Assert.Equal("{\"on\":true,\"mode\":\"colour\",\"color\":{\"h\":230,\"s\":100}}", Json.Write(Payload(_hub.Calls[0].Args)));
        Assert.True(main.Lamps[0].IsOn && main.Lamps[0].IsColour);
    }

    [Fact]
    public void Updates_from_Licht_do_not_move_the_controls_while_the_user_adjusts_them()
    {
        var main = Window(Lights.Json("1", "Flur", on: true, brightness: 50));
        var lamp = main.Lamps[0];
        lamp.Brightness = 80;
        _hub.Raise(Lights.Message(Lights.Json("1", "Flur neu", on: true, brightness: 51, status: "connecting")));
        Assert.Equal(80, lamp.Brightness);
        Assert.Equal("Flur neu", lamp.Name); // name and reachability are taken over anyway
        Assert.True(lamp.IsConnecting);

        _hub.Raise("""{"t":"light","light":""" + Lights.Json("1", "Flur neu", on: true, brightness: 52, status: "offline") + "}");
        Assert.Equal(80, lamp.Brightness); // one lamp reported: the same
        Assert.True(lamp.IsOffline);

        _hub.Calls[0].Reply.SetResult(null);
        _clock.Advance(LampViewModel.QuietAfterInput);
        _hub.Raise("""{"t":"light","light":""" + Lights.Json("1", "Flur neu", on: true, brightness: 79) + "}");
        Assert.Equal(79, lamp.Brightness); // quiet again: the lamp's own value
    }

    [Fact]
    public void A_failed_command_shows_the_real_state_again()
    {
        var main = Window(Lights.Json("1", "Flur"));
        var lamp = main.Lamps[0];
        lamp.ToggleCommand.Execute(null);
        Assert.True(lamp.IsOn);
        _hub.Calls[0].Reply.SetException(new HubException("Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?", 503));
        _clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(("Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?", false), _ui.Toasts.Single());
        var reload = _hub.Last("lights");
        reload.Reply.SetResult(Json.Parse("[" + Lights.Json("1", "Flur") + "]"));
        Assert.False(lamp.IsOn);
    }

    [Fact]
    public void A_tap_right_after_a_failure_is_sent()
    {
        var main = Window(Lights.Json("1", "Flur"));
        var lamp = main.Lamps[0];
        lamp.ToggleCommand.Execute(null); // on
        _hub.Calls[0].Reply.SetException(new HubException("weg", 503));
        lamp.ToggleCommand.Execute(null); // the user taps again during the gap: off
        _clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(new[] { "update", "update" }, _hub.Calls.Select(c => c.Method)); // no reload: the new tap is on its way
        Assert.Equal("{\"on\":false}", Json.Write(Payload(_hub.Calls[1].Args)));
    }

    [Fact]
    public void All_off_and_on_skips_unreachable_lamps()
    {
        var main = Window(Lights.Json("1", "A", on: true), Lights.Json("2", "B", on: false), Lights.Json("3", "C", on: true, status: "offline"));
        main.AllToggleCommand.Execute(null);
        Assert.Equal(new[] { "1" }, _hub.Calls.Select(c => (string)c.Args[0]!));
        Assert.Equal("Alle an", main.AllToggleText);
        main.AllToggleCommand.Execute(null); // quickly on again: lamp 1 waits for its first command
        Assert.Equal(new[] { "1", "2" }, _hub.Calls.Select(c => (string)c.Args[0]!));
        _hub.Calls[0].Reply.SetResult(null);
        _clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(("1", "{\"on\":true}"), ((string)_hub.Calls[2].Args[0]!, Json.Write(Payload(_hub.Calls[2].Args))));
        Assert.Equal(3, _hub.Calls.Count);
    }

    [Fact]
    public void Lamps_keep_Lichts_order_and_new_ones_appear()
    {
        var main = Window(Lights.Json("1", "A"), Lights.Json("2", "B"));
        var b = main.Lamps[1];
        _hub.Raise(Lights.Message(Lights.Json("2", "B"), Lights.Json("3", "C"), Lights.Json("1", "A")));
        Assert.Equal(new[] { "2", "3", "1" }, main.Lamps.Select(l => l.Id));
        Assert.Same(b, main.Lamps[0]); // the same card, moved
    }

    [Fact]
    public async Task Adding_lamps_from_a_share_code_with_progress()
    {
        var main = Window(Lights.Json("1", "A"));
        main.AddCommand.Execute(null);
        var add = _ui.Add!;
        Assert.False(add.SubmitCommand.CanExecute(null));
        add.Code = " MT:ABC*DEF ";
        add.SubmitCommand.Execute(null);
        Assert.True(add.Busy);
        var call = _hub.Last("add");
        Assert.Equal("{\"code\":\"MT:ABC*DEF\"}", Json.Write(call.Args[0]));
        _hub.Raise("""{"t":"adding","index":1,"total":3}""");
        Assert.Equal("Lampe 2 von 3 wird hinzugefügt …", add.ProgressText);
        call.Reply.SetResult(Json.Parse("""{"added":["2","3"],"present":1,"failed":[],"lights":[""" + Lights.Json("1", "A") + "," + Lights.Json("2", "B") + "," + Lights.Json("3", "C") + "]}"));
        await _ui.DialogClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("2 Lampen hinzugefügt, 1 war schon da.", true), _ui.Toasts.Last());
        Assert.Equal(3, main.Lamps.Count);
        Assert.False(add.Busy);
    }

    [Fact]
    public void Adding_shows_errors_in_the_dialog()
    {
        Window();
        new MainViewModel(_hub, _ui, _clock).AddCommand.Execute(null);
        var add = _ui.Add!;
        add.Code = "1234";
        add.Ip = "192.168.0.5";
        add.SubmitCommand.Execute(null);
        var call = _hub.Last("add");
        Assert.Equal("{\"code\":\"1234\",\"ip\":\"192.168.0.5\"}", Json.Write(call.Args[0]));
        call.Reply.SetException(new HubException("Der Code muss 11 oder 21 Ziffern haben.", 400));
        Assert.Equal("Der Code muss 11 oder 21 Ziffern haben.", add.Error);
        Assert.False(_ui.DialogClosed.Task.IsCompleted);
    }

    [Theory]
    [InlineData(1, 0, 0, "1 Lampe hinzugefügt.")]
    [InlineData(0, 1, 0, "Diese Lampe ist schon in Licht.")]
    [InlineData(0, 3, 0, "Alle 3 Lampen sind schon in Licht.")]
    [InlineData(2, 0, 1, "2 Lampen hinzugefügt, 1 hat nicht geklappt.")]
    public void Add_summaries(int added, int present, int failed, string text) => Assert.Equal(text, AddViewModel.Summary(added, present, failed));

    [Fact]
    public void Network_search_lists_lamps_ready_to_add()
    {
        var main = Window();
        main.AddCommand.Execute(null);
        var add = _ui.Add!;
        add.Code = "34970112332";
        Assert.False(add.ShowSearchResult);
        add.SearchCommand.Execute(null);
        Assert.True(add.ShowSearchResult && add.Searching);
        var call = _hub.Last("discover");
        Assert.Equal("34970112332", call.Args[0]);
        call.Reply.SetResult(Json.Parse("""[{"name":"A60","mode":2,"addresses":["192.168.0.9:5540"],"matchesCode":true}]"""));
        var found = Assert.Single(add.Found);
        Assert.Equal(("A60", "geteilt – Code aus der anderen App · 192.168.0.9:5540 · passt zum Code"), (found.Name, found.Detail));
        Assert.Null(add.SearchText);
        Assert.True(add.ShowSearchResult && !add.Searching);
    }

    [Fact]
    public void Sharing_all_lamps_shows_a_QR_code()
    {
        var main = Window(Lights.Json("1", "A"), Lights.Json("2", "B"));
        main.ShareAllCommand.Execute(null);
        var share = _ui.Share!;
        Assert.True(share.Preparing);
        var call = _hub.Last("share");
        Assert.Empty(call.Args);
        call.Reply.SetResult(Json.Parse("""{"code":"MT:Y.K90AFN00KA0648G00","lamps":["A"],"skipped":["B"],"validMinutes":15}"""));
        Assert.False(share.Preparing);
        Assert.True(share.HasCode);
        Assert.NotNull(share.Matrix);
        Assert.Equal("Enthält: A", share.LampsText);
        Assert.Contains("B", share.SkippedText);
        Assert.Null(share.ManualCode);
        share.CopyCommand.Execute(null);
        Assert.Equal("MT:Y.K90AFN00KA0648G00", _ui.Clipboard);
    }

    [Fact]
    public void Sharing_one_lamp_also_shows_its_digits()
    {
        var main = Window(Lights.Json("1", "A"));
        main.Lamps[0].ShareCommand.Execute(null);
        var call = _hub.Last("share");
        Assert.Equal("[[\"1\"]]", Json.Write(call.Args));
        call.Reply.SetResult(Json.Parse("""{"code":"MT:Y.K90AFN00KA0648G00","manual":"34970112332","lamps":["A"],"skipped":[],"validMinutes":15}"""));
        Assert.Equal("3497-011-2332", _ui.Share!.ManualCode);
        Assert.Equal("„A“ teilen", _ui.Share.Title);
    }

    [Fact]
    public void Sharing_fails_with_a_reason()
    {
        var main = Window(Lights.Json("1", "A"));
        main.ShareAllCommand.Execute(null);
        _hub.Last("share").Reply.SetException(new HubException("Keine Lampe antwortet gerade.", 503));
        Assert.Equal("Keine Lampe antwortet gerade.", _ui.Share!.Error);
        Assert.False(_ui.Share.Preparing);
    }

    [Fact]
    public void Rename_and_remove()
    {
        var main = Window(Lights.Json("1", "A"), Lights.Json("2", "B"));
        _ui.Answers.Enqueue("  Wohnzimmer  ");
        main.Lamps[0].RenameCommand.Execute(null);
        var rename = _hub.Last("rename");
        Assert.Equal(new object?[] { "1", "Wohnzimmer" }, rename.Args);
        rename.Reply.SetResult(null);
        Assert.Equal("Wohnzimmer", main.Lamps[0].Name);

        _ui.Answers.Enqueue(false); // not sure after all
        main.Lamps[1].RemoveCommand.Execute(null);
        Assert.DoesNotContain(_hub.Calls, c => c.Method == "remove");
        _ui.Answers.Enqueue(true);
        main.Lamps[1].RemoveCommand.Execute(null);
        _hub.Last("remove").Reply.SetResult(Json.Parse("""{"ok":true,"localOnly":true}"""));
        Assert.Single(main.Lamps);
        Assert.Equal("Lampe war nicht erreichbar", _ui.Notices.Single().Title);
    }

    [Fact]
    public void Messages_from_Licht_show_bring_to_front_fatal_and_quit()
    {
        var main = Window();
        _hub.Raise("""{"t":"show"}""");
        Assert.Equal(1, _ui.BroughtToFront);
        _hub.Raise("""{"t":"message","kind":"error","title":"Ältere Licht-Version läuft noch","text":"Bitte beenden."}""");
        Assert.Equal("Ältere Licht-Version läuft noch", _ui.Notices.Single().Title);
        _hub.Raise("""{"t":"fatal","title":"Licht konnte nicht starten","text":"Storage is locked"}""");
        Assert.Equal(("Licht konnte nicht starten", "Storage is locked"), _ui.Fatal);
        Assert.True(_ui.QuitCalled);
        Assert.Equal("quit", _hub.Sent.Last()["t"]);
    }

    [Fact]
    public void Autostart_and_quit()
    {
        var main = Window();
        main.AutostartCommand.Execute(null); // on → off
        var call = _hub.Last("autostart");
        Assert.Equal(new object?[] { false }, call.Args);
        call.Reply.SetResult(false);
        Assert.False(main.Autostart);
        _ui.Answers.Enqueue(true);
        main.QuitCommand.Execute(null);
        Assert.Equal("quit", _hub.Sent.Last()["t"]);
    }

    [Fact]
    public void The_lamps_as_last_seen_appear_before_Licht_answers()
    {
        var dir = Directory.CreateTempSubdirectory("licht-cache-").FullName;
        try
        {
            var cache = new LightsCache(dir);
            var first = new MainViewModel(_hub, _ui, _clock, cache);
            _hub.Raise(Lights.Message(Lights.Json("1", "Flur", on: true)));
            _clock.Advance(MainViewModel.SaveDelay); // saved a moment later
            _hub.Raise(Lights.Message(Lights.Json("1", "Flur", on: true, brightness: 30)));
            _clock.Advance(MainViewModel.SaveDelay); // replaces the saved file
            var next = new MainViewModel(new FakeHub(), _ui, _clock, cache);
            Assert.False(next.Loaded);
            var lamp = Assert.Single(next.Lamps);
            Assert.Equal(("Flur", "Verbinde … · An · 30 %"), (lamp.Name, lamp.StateText)); // reachable? Licht tells in a moment
            Assert.False(next.IsLoading);
            File.WriteAllText(Path.Combine(dir, "fenster-lampen.json"), "kaputt");
            Assert.Empty(new MainViewModel(new FakeHub(), _ui, _clock, cache).Lamps); // a broken cache is ignored
            Assert.NotNull(first);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string ConnectionsJson(int supported, params (int Index, string Name, bool Own)[] entries) =>
        Json.Write(new Dictionary<string, object?>
        {
            ["supported"] = supported,
            ["entries"] = entries.Select(e => (object?)new Dictionary<string, object?> { ["index"] = e.Index, ["name"] = e.Name, ["own"] = e.Own }).ToList(),
        });

    [Fact]
    public async Task Connected_apps_are_listed_and_an_old_one_removed()
    {
        var main = Window(Lights.Json("1", "Flur", on: true));
        main.Lamps[0].ConnectionsCommand.Execute(null);
        var connections = _ui.Connections!;
        Assert.True(connections.Loading);
        Assert.Equal("Verbundene Apps – „Flur“", connections.Title);
        _hub.Last("connections").Reply.SetResult(Json.Parse(ConnectionsJson(5,
            (1, "Licht PC 3F2A", true), (2, "SMART+", false), (3, "Licht Handy 08E1", false), (4, "Licht Handy 63C4", false), (5, "Google", false))));
        Assert.False(connections.Loading);
        Assert.Equal(new[] { "Licht PC 3F2A", "SMART+", "Licht Handy 08E1", "Licht Handy 63C4", "Google" }, connections.Items.Select(i => i.Name));
        Assert.True(connections.IsFull);
        Assert.StartsWith("5 von 5 Plätzen belegt. Die Lampe ist voll", connections.SummaryText);

        connections.RemoveCommand.Execute(connections.Items[0]); // Licht itself: nothing happens
        Assert.Empty(_ui.Confirms);

        _ui.Answers.Enqueue(false);
        connections.RemoveCommand.Execute(connections.Items[2]); // not sure after all
        Assert.DoesNotContain(_hub.Calls, c => c.Method == "removeConnection");

        _ui.Answers.Enqueue(true);
        connections.RemoveCommand.Execute(connections.Items[2]);
        Assert.Equal("„Licht Handy 08E1“ entfernen?", _ui.Confirms.Last().Title);
        Assert.True(connections.Busy);
        var call = _hub.Last("removeConnection");
        Assert.Equal(("1", 3), ((string)call.Args[0]!, (int)call.Args[1]!));
        call.Reply.SetResult(Json.Parse(ConnectionsJson(5, (1, "Licht PC 3F2A", true), (2, "SMART+", false), (4, "Licht Handy 63C4", false), (5, "Google", false))));
        await Task.Yield();
        Assert.False(connections.Busy || connections.IsFull);
        Assert.Equal("4 von 5 Plätzen belegt.", connections.SummaryText);
        Assert.Equal(("„Licht Handy 08E1“ wurde von „Flur“ entfernt.", true), _ui.Toasts.Last());
    }

    [Fact]
    public void A_failed_removal_is_shown_in_the_dialog()
    {
        var main = Window(Lights.Json("1", "Flur", on: true));
        main.Lamps[0].ConnectionsCommand.Execute(null);
        var connections = _ui.Connections!;
        _hub.Last("connections").Reply.SetResult(Json.Parse(ConnectionsJson(5, (1, "Licht PC", true), (2, "SMART+", false))));
        _ui.Answers.Enqueue(true);
        connections.RemoveCommand.Execute(connections.Items[1]);
        _hub.Last("removeConnection").Reply.SetException(new HubException("Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?", 503));
        Assert.Equal("Die Lampe antwortet gerade nicht. Ist sie eingeschaltet?", connections.Error);
        Assert.False(connections.Busy);
        Assert.Equal(2, connections.Items.Count);
    }

    [Fact]
    public void Sharing_a_full_lamp_leads_to_its_connected_apps()
    {
        var main = Window(Lights.Json("1", "Flur", on: true));
        main.Lamps[0].ShareCommand.Execute(null);
        var share = _ui.Share!;
        _hub.Last("share").Reply.SetException(new HubException("Die Lampe ist schon mit 5 Apps verbunden …", 409));
        Assert.True(share.CanOpenConnections);
        share.ConnectionsCommand.Execute(null);
        Assert.True(_ui.DialogClosed.Task.IsCompleted || _ui.Connections != null);
        Assert.Equal("Flur", _ui.Connections!.LampName);
        Assert.Equal("connections", _hub.Calls.Last().Method);
    }

    [Fact]
    public void Shared_codes_say_which_lamps_are_full()
    {
        var main = Window(Lights.Json("1", "Flur", on: true), Lights.Json("2", "Küche", on: true));
        main.ShareAllCommand.Execute(null);
        _hub.Last("share").Reply.SetResult(Json.Parse("""{"code":"MT:Y.K90AFN00KA0648G00","lamps":["Flur"],"skipped":[],"full":["Küche"],"validMinutes":15}"""));
        Assert.StartsWith("Nicht dabei, weil schon mit so vielen Apps verbunden, wie sie kann: Küche.", _ui.Share!.FullText);
        Assert.False(_ui.Share.CanOpenConnections);
    }
}
