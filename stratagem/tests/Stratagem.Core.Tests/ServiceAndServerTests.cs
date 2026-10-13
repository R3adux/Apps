using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Stratagem.Core.Game;
using Stratagem.Core.Input;
using Stratagem.Core.Service;
using Stratagem.Core.Settings;
using Stratagem.Server;

namespace Stratagem.Core.Tests;

public sealed class ServiceFixture : IDisposable
{
    public readonly string Dir = Directory.CreateTempSubdirectory().FullName;
    public readonly FakeOutput Output = new();
    public readonly FakeView View = new();
    public readonly GameWatcherStub Game = new() { IsRunning = true, IsFocused = true };
    public readonly StratagemService Service;

    public ServiceFixture(Func<BindingsResult?>? readBindings = null)
    {
        var store = new SettingsStore(Path.Combine(Dir, "settings.json"));
        var settings = store.Load();
        settings.Timing = new TimingSettings { KeyHoldMs = 5, KeyGapMs = 5, MenuOpenMs = 5 };
        Service = new StratagemService(store, settings, new ServicePlatform(Output, View, Game, ReadGameBindings: readBindings));
    }

    public void Dispose()
    {
        Service.Dispose();
        Directory.Delete(Dir, true);
    }
}

public class ServiceTests
{
    [Fact]
    public async Task Activates_only_while_game_is_focused()
    {
        using var f = new ServiceFixture();
        f.Game.IsFocused = false;
        f.Game.Raise();
        Assert.False(f.Service.Activate(0, "Test"));
        Assert.False(f.Service.Radial.Enabled);

        f.Game.IsFocused = true;
        f.Game.Raise();
        Assert.True(f.Service.Radial.Enabled);
        Assert.True(f.Service.Activate(0, "Test"));
        await MacroRunnerTests.WaitUntil(() => !f.Service.Runner.IsRunning && f.Output.Events.Count > 0);
        Assert.Equal("+ControlLeft", f.Output.Events[0]);
        Assert.Equal("-ControlLeft", f.Output.Events[^1]);
        Assert.Contains(f.Service.GetState().Events, e => e.Text.Contains("Reinforce"));
    }

    [Fact]
    public void Pause_disables_everything()
    {
        using var f = new ServiceFixture();
        f.Service.SetPaused(true);
        Assert.False(f.Service.IsActive);
        Assert.False(f.Service.Radial.Enabled);
        Assert.False(f.Service.Activate(0, "Test"));
        Assert.True(f.Service.GetState().Paused);
    }

    [Fact]
    public void Game_bindings_win_over_settings()
    {
        var fromGame = new BindingsResult(GameBindings.Default with { Up = KeyNames.ArrowUp }, "file", ["note"]);
        using var f = new ServiceFixture(() => fromGame);
        Assert.Equal(KeyNames.ArrowUp, f.Service.Bindings.Up);
        var state = f.Service.GetState();
        Assert.Equal("ArrowUp", state.Bindings.Up);
        Assert.Equal("file", state.Bindings.SourceFile);
    }

    [Fact]
    public void Missing_game_file_falls_back_with_note()
    {
        using var f = new ServiceFixture(() => null);
        Assert.Equal(GameBindings.Default, f.Service.Bindings);
        Assert.NotEmpty(f.Service.GetState().Bindings.Notes);
    }

    [Fact]
    public void Screen_source_without_reader_falls_back_to_manual()
    {
        using var f = new ServiceFixture();
        var s = f.Service.Settings;
        s.List.Source = ListSource.Screen;
        f.Service.UpdateSettings(s);
        var state = f.Service.GetState();
        Assert.NotNull(state.List.Problem);
        Assert.Equal(6, state.List.Items.Count);
    }

    [Fact]
    public void Settings_update_reconfigures_radial()
    {
        using var f = new ServiceFixture();
        var s = f.Service.Settings;
        s.Radial.Trigger = "MouseX1";
        f.Service.UpdateSettings(s);
        Assert.Equal("MouseX1", f.Service.Radial.Options.Trigger.Name);
        Assert.True(f.Service.Router.OnMouseButton(MouseButton.X1, true));
        Assert.True(f.View.Visible);
    }
}

public class WebServerTests
{
    private static async Task<(ServiceFixture F, WebServer Server, HttpClient Http)> Start()
    {
        var f = new ServiceFixture();
        var server = await WebServer.StartAsync(f.Service, new WebServerOptions(0, false, "0123456789abcdef0123"));
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        return (f, server, http);
    }

    private static HttpRequestMessage Post(string url, object body, bool header = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (header) req.Headers.Add(WebServer.HeaderName, "1");
        return req;
    }

    [Fact]
    public async Task State_and_activation()
    {
        var (f, server, http) = await Start();
        await using var _ = server;
        using var __ = f;
        var state = await http.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Equal(1, state.GetProperty("apiVersion").GetInt32());
        Assert.Equal(6, state.GetProperty("list").GetProperty("items").GetArrayLength());

        var ok = await http.SendAsync(Post("/api/activate", new { slot = 1, origin = "Test" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());

        var bad = await http.SendAsync(Post("/api/activate", new { code = "xyz" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Changes_need_the_header()
    {
        var (f, server, http) = await Start();
        await using var _ = server;
        using var __ = f;
        var res = await http.SendAsync(Post("/api/activate", new { slot = 0 }, header: false));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(f.Output.Events);
    }

    [Fact]
    public async Task Foreign_origin_and_host_are_rejected()
    {
        var (f, server, http) = await Start();
        await using var _ = server;
        using var __ = f;
        var req = Post("/api/activate", new { slot = 0 });
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(req)).StatusCode);

        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        rebinding.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await http.SendAsync(rebinding)).StatusCode);

        var same = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        same.Headers.Add("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(same)).StatusCode);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        var (f, server, http) = await Start();
        await using var _ = server;
        using var __ = f;
        var settings = await http.GetFromJsonAsync<JsonElement>("/api/settings");
        var json = settings.GetRawText().Replace("\"holdRelease\"", "\"tapDrag\"", StringComparison.OrdinalIgnoreCase);
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add(WebServer.HeaderName, "1");
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Radial.RadialMode.TapDrag, f.Service.Settings.Radial.Mode);
    }

    [Fact]
    public async Task WebSocket_sends_state_on_connect_and_change()
    {
        var (f, server, http) = await Start();
        await using var _ = server;
        using var __ = f;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/events"), CancellationToken.None);
        var first = await Receive(ws);
        Assert.Equal("state", first.GetProperty("type").GetString());
        f.Service.SetPaused(true);
        JsonElement next;
        do next = await Receive(ws);
        while (!next.GetProperty("state").GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task Serves_web_ui()
    {
        var (f, server, http) = await Start();
        await using var _ = server;
        using var __ = f;
        var html = await http.GetStringAsync("/");
        Assert.Contains("<title>Stratagem</title>", html);
        Assert.Contains("<title>", await http.GetStringAsync("/deck"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/../secret.txt")).StatusCode);
    }

    private static async Task<JsonElement> Receive(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}
