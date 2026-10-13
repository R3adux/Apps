using Stratagem.Core.Game;
using Stratagem.Core.Input;
using Stratagem.Core.Radial;
using Stratagem.Core.Settings;

namespace Stratagem.Core.Tests;

public class SjsonTests
{
    [Fact]
    public void Parses_implicit_root_nesting_and_comments()
    {
        var root = Sjson.Parse("""
            // comment
            name = "test" /* block
            comment */
            count = 3.5
            flag = true
            list = [ 1, "two" { a = 1 } ]
            nested = { inner = { key = value } }
            """);
        Assert.Equal("test", root["name"]);
        Assert.Equal(3.5, root["count"]);
        Assert.Equal(true, root["flag"]);
        var list = Assert.IsType<List<object?>>(root["list"]);
        Assert.Equal(3, list.Count);
        var nested = Assert.IsType<Dictionary<string, object?>>(root["nested"]);
        var inner = Assert.IsType<Dictionary<string, object?>>(nested["inner"]);
        Assert.Equal("value", inner["key"]);
    }

    [Fact]
    public void Tolerates_missing_closing_braces() =>
        Assert.True(Sjson.Parse("a = { b = [ 1 2").ContainsKey("a"));
}

public class GameInputSettingsTests
{
    // Shape as described by community tools (NicholasDJM/Helldivers-2-Stratagem-Macros). To be checked against a real file.
    private const string Sample = """
        Menu = {
        	Start = [
        		{
        			trigger = "Press"
        			device_type = "Keyboard"
        			input = "escape"
        		}
        	]
        }
        Stratagem = {
        	Start = [
        		{
        			trigger = "Hold"
        			device_type = "Keyboard"
        			input = "left ctrl"
        			threshold = 0.5
        		}
        	]
        	Up = [
        		{
        			trigger = "Press"
        			device_type = "Keyboard"
        			input = "w"
        		}
        		{
        			trigger = "Press"
        			device_type = "Keyboard"
        			input = "up"
        		}
        	]
        	Down = [
        		{ trigger = "Press" device_type = "Keyboard" input = "s" }
        	]
        	Left = [
        		{ trigger = "Press" device_type = "Gamepad" input = "dpad_left" }
        		{ trigger = "Press" device_type = "Keyboard" input = "a" }
        	]
        	Right = [
        		{ trigger = "Press" device_type = "Keyboard" input = "d" }
        	]
        }
        """;

    [Fact]
    public void Reads_stratagem_section_and_prefers_arrows()
    {
        var r = GameInputSettings.Read(Sample, "x");
        Assert.Equal(KeyNames.ControlLeft, r.Bindings.Menu);
        Assert.Equal(MenuMode.Hold, r.Bindings.MenuMode);
        Assert.Equal(KeyNames.ArrowUp, r.Bindings.Up);
        Assert.Equal(KeyNames.KeyS, r.Bindings.Down);
        Assert.Equal(KeyNames.KeyA, r.Bindings.Left);
        Assert.Equal(KeyNames.KeyD, r.Bindings.Right);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Press_mode_and_unknown_names()
    {
        var text = Sample.Replace("\"Hold\"", "\"Press\"").Replace("input = \"d\"", "input = \"some_new_key\"");
        var r = GameInputSettings.Read(text);
        Assert.Equal(MenuMode.Press, r.Bindings.MenuMode);
        Assert.Equal(GameBindings.Default.Right, r.Bindings.Right);
        Assert.Contains(r.Notes, n => n.Contains("some_new_key"));
    }

    [Fact]
    public void Unsupported_trigger_is_reported()
    {
        var r = GameInputSettings.Read(Sample.Replace("\"Hold\"", "\"DoubleTap\""));
        Assert.Contains(r.Notes, n => n.Contains("DoubleTap"));
    }

    [Fact]
    public void Missing_section_gives_defaults_with_note()
    {
        var r = GameInputSettings.Read("Menu = { }");
        Assert.Equal(GameBindings.Default, r.Bindings);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void Locates_active_user_first()
    {
        var steam = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string Make(string user)
            {
                var dir = Path.Combine(steam, "userdata", user, "553850", "remote");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "input_settings.config");
                File.WriteAllText(file, Sample);
                return file;
            }
            var other = Make("111");
            var active = Make("222");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(1));
            var found = GameInputSettings.Locate(steam, 222, null);
            Assert.Equal([active, other], found);
            Assert.Equal([other, active], GameInputSettings.Locate(steam, null, null));
            Assert.Empty(GameInputSettings.Locate(null, null, null));
        }
        finally { Directory.Delete(steam, true); }
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_round_trip()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var s = store.Load();
            Assert.True(File.Exists(store.Path));
            s.Radial.Mode = RadialMode.TapDrag;
            s.Hotkeys.Add(new HotkeySetting { Input = "Numpad1", Slot = 1 });
            store.Save(s);
            var again = store.Load();
            Assert.Equal(RadialMode.TapDrag, again.Radial.Mode);
            Assert.Single(again.Hotkeys);
            Assert.Contains("\"tapDrag\"", File.ReadAllText(store.Path), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Broken_file_is_kept_and_defaults_used()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ not json");
            var logs = new List<string>();
            var s = new SettingsStore(path, logs.Add).Load();
            Assert.Equal(RadialMode.HoldRelease, s.Radial.Mode);
            Assert.True(File.Exists(Path.Combine(dir, "settings.broken.json")));
            Assert.NotEmpty(logs);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Normalize_repairs_values()
    {
        var s = new AppSettings();
        s.Radial.Trigger = "Nonsense";
        s.Timing.KeyHoldMs = 0;
        s.Web.Token = "";
        var fixes = s.Normalize();
        Assert.Equal("ControlLeft", s.Radial.Trigger);
        Assert.Equal(5, s.Timing.KeyHoldMs);
        Assert.True(s.Web.Token.Length >= 16);
        Assert.True(fixes.Count >= 2);
    }

    [Fact]
    public void Converts_to_runtime_objects()
    {
        var s = new AppSettings();
        s.Hotkeys.Add(new HotkeySetting { Input = "Numpad1", Slot = 1 });
        s.Hotkeys.Add(new HotkeySetting { Input = "MouseX2", Code = "URDDD", Slot = 3 });
        s.Hotkeys.Add(new HotkeySetting { Input = "Unknown", Slot = 1 });
        var hotkeys = s.ToHotkeys();
        Assert.Equal(2, hotkeys.Count);
        Assert.Equal(0, hotkeys[InputRef.Parse("Numpad1")].Slot);
        Assert.Equal("URDDD", hotkeys[InputRef.Parse("MouseX2")].Code!.Letters);

        s.List.Manual.Add(new ManualEntry { Code = "RDURD" }); // name from catalog? (not in catalog → null)
        s.List.Manual.Add(new ManualEntry { Code = "invalid!" });
        var items = s.ToManualItems(Model.StratagemCatalog.Default);
        Assert.Equal(7, items.Count);
        Assert.Equal("Reinforce", items[0].Name);

        s.Game.Up = "ArrowUp";
        Assert.Equal(KeyNames.ArrowUp, s.ToManualBindings().Up);
    }
}
