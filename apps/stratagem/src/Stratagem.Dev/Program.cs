using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;
using Stratagem.Core.Service;
using Stratagem.Core.Settings;
using Stratagem.Server;

// Development host: same service and web server as Stratagem.exe, but fake keyboard and game.
// Type "f" + Enter to toggle "game in foreground", "q" to quit.
var dataDir = Environment.GetEnvironmentVariable("STRATAGEM_DATA") ?? Path.Combine(Path.GetTempPath(), "stratagem-dev");
var webDir = Environment.GetEnvironmentVariable("STRATAGEM_WEB_DIR") ?? FindWebDir();
var store = new SettingsStore(Path.Combine(dataDir, "settings.json"), Console.WriteLine);
var settings = store.Load();
var game = new GameWatcherStub { IsRunning = true, IsFocused = true };
// STRATAGEM_GAME_CONFIG=path/to/input_settings.config: use the game's bindings from that file.
var gameConfig = Environment.GetEnvironmentVariable("STRATAGEM_GAME_CONFIG");
Func<Stratagem.Core.Game.BindingsResult?>? readBindings = gameConfig is null ? null
    : () => Stratagem.Core.Game.GameInputSettings.Read(File.ReadAllText(gameConfig), gameConfig);
// STRATAGEM_TEST_SCREEN=screenshot.png: "screen capture" returns this picture (reading the list without the game).
var testScreen = Environment.GetEnvironmentVariable("STRATAGEM_TEST_SCREEN");
var service = new StratagemService(store, settings, new ServicePlatform(new ConsoleOutput(), new ConsoleView(), game,
    ReadGameBindings: readBindings, Capture: testScreen is null ? null : new PictureCapture(testScreen), DataDir: dataDir), log: Console.WriteLine);
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : settings.Web.Port;
var lanPort = int.TryParse(Environment.GetEnvironmentVariable("LAN_PORT"), out var lp) ? lp : settings.Web.LanPort;
await using var server = await WebServer.StartAsync(service, new WebServerOptions(port, settings.Web.AllowLan, settings.Web.Token, WebDir: webDir, Log: Console.WriteLine, LanPort: lanPort));
using var mdns = settings.Web.AllowLan ? new MdnsResponder(() => service.Current.Web.Name, Console.WriteLine) : null;
Console.WriteLine($"Stratagem (Entwicklung) auf http://localhost:{server.Port} – Web-Dateien aus {webDir}");

if (Console.IsInputRedirected)
{
    await Task.Delay(Timeout.Infinite);
}
while (Console.ReadLine() is { } line && line.Trim() != "q")
{
    if (line.Trim() == "f")
    {
        game.IsFocused = !game.IsFocused;
        game.Raise();
        Console.WriteLine(game.IsFocused ? "Spiel im Vordergrund" : "Spiel im Hintergrund");
    }
}
service.Dispose();

static string FindWebDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var web = Path.Combine(dir.FullName, "web");
        if (File.Exists(Path.Combine(web, "index.html"))) return web;
    }
    throw new DirectoryNotFoundException("web/ nicht gefunden – STRATAGEM_WEB_DIR setzen");
}

sealed class ConsoleOutput : IInputOutput
{
    public void Send(Key key, bool down) => Console.WriteLine($"  {(down ? "▼" : "▲")} {key.Name}");
    public void Send(MouseButton button, bool down) => Console.WriteLine($"  {(down ? "▼" : "▲")} Mouse{button}");
}

sealed class ConsoleView : IRadialView
{
    public void Show(IReadOnlyList<StratagemItem> items, RadialMode mode) => Console.WriteLine($"[Radial auf: {items.Count} Einträge]");
    public void Highlight(int? index) => Console.WriteLine($"[Radial: {index?.ToString() ?? "Mitte"}]");
    public void Hide(bool selected) => Console.WriteLine(selected ? "[Radial zu: ausgewählt]" : "[Radial zu]");
}

sealed class PictureCapture(string path) : Stratagem.Core.Recognition.IScreenCapture
{
    public Stratagem.Core.Recognition.PixelImage? CaptureMenuArea(out double scale)
    {
        var image = Stratagem.Core.Recognition.Png.Decode(File.ReadAllBytes(path));
        scale = image.Height / 1080.0;
        return image;
    }
}
