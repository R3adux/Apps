using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Service;
using Stratagem.Core.Settings;

namespace Stratagem.Server;

/// <summary>PC functions offered in the web UI (tray app only; null in tests).</summary>
public interface IDesktop
{
    bool Autostart { get; set; }
    void Quit();
    void OpenDataFolder();
    /// <summary>Shows the Stratagem window (a second start of Stratagem.exe asks for it).</summary>
    void ShowWindow();
}

/// <param name="LanPort">Extra port for the home network (80: the address needs no ":port"); 0 = none.
/// If it is taken, the server runs without it.</param>
public sealed record WebServerOptions(int Port, bool AllowLan, string Token, IDesktop? Desktop = null, string? WebDir = null, Action<string>? Log = null, int LanPort = 0);

/// <summary>
/// Local web server (Kestrel). API under /api, see docs/API.md.
/// Protection: other web pages in the browser must not press keys through this server, so every request
/// that changes something needs the header "X-Stratagem: 1" (a foreign page cannot set it without a CORS
/// preflight, which is never allowed), the Host must be ours (DNS rebinding), and requests from the home
/// network need the token.
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    public const string HeaderName = "X-Stratagem";
    private const string TokenCookie = "stratagem_token";

    private readonly WebApplication _app;
    private readonly StratagemService _service;
    private readonly WebServerOptions _options;
    private readonly List<Client> _clients = [];
    private readonly Timer _pushTimer;
    private int _pushPending;

    private sealed record Client(WebSocket Socket, SemaphoreSlim SendLock);

    private WebServer(WebApplication app, StratagemService service, WebServerOptions options)
    {
        _app = app;
        _service = service;
        _options = options;
        _pushTimer = new Timer(_ => Push(), null, Timeout.Infinite, Timeout.Infinite);
        service.Changed += SchedulePush;
    }

    public int Port { get; private set; }

    /// <summary>The extra home network port, if it could be opened (0 = none).</summary>
    public int LanPort { get; private set; }

    public static async Task<WebServer> StartAsync(StratagemService service, WebServerOptions options)
    {
        var lanPort = options.AllowLan && options.LanPort > 0 && options.LanPort != options.Port ? options.LanPort : 0;
        if (lanPort != 0)
        {
            try { return await StartAsync(service, options, lanPort); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
            {
                options.Log?.Invoke($"Port {lanPort} fürs Heimnetz belegt – Adresse mit :{options.Port} ({e.Message})");
            }
        }
        return await StartAsync(service, options, 0);
    }

    private static async Task<WebServer> StartAsync(StratagemService service, WebServerOptions options, int lanPort)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (options.AllowLan)
            {
                k.Listen(IPAddress.Any, options.Port);
                if (lanPort != 0) k.Listen(IPAddress.Any, lanPort);
            }
            else
            {
                k.Listen(IPAddress.Loopback, options.Port);
                if (System.Net.Sockets.Socket.OSSupportsIPv6) k.Listen(IPAddress.IPv6Loopback, options.Port);
            }
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = SettingsStore.Json.PropertyNamingPolicy;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            foreach (var c in SettingsStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
        });
        var app = builder.Build();
        var server = new WebServer(app, service, options);
        server.Map();
        try { await app.StartAsync(); }
        catch (Exception)
        {
            service.Changed -= server.SchedulePush;
            await server._pushTimer.DisposeAsync();
            await app.DisposeAsync();
            throw;
        }
        server.LanPort = lanPort;
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        server.Port = options.Port != 0 ? options.Port
            : addresses?.Select(a => new Uri(a.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port).FirstOrDefault(p => p != lanPort) ?? 0;
        return server;
    }

    private void Map()
    {
        var app = _app;
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(Guard);

        app.MapGet("/api/state", () => Results.Json(_service.GetState(), JsonOptions));
        app.MapGet("/api/settings", () => Results.Json(_service.Current, JsonOptions));
        app.MapPut("/api/settings", async (HttpRequest req) =>
        {
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(req.Body, SettingsStore.Json);
            if (settings is null) return Results.BadRequest(new { error = "Keine Einstellungen" });
            _service.UpdateSettings(settings);
            return Results.Json(_service.Current, JsonOptions);
        });
        app.MapPost("/api/activate", (ActivateRequest body) =>
        {
            var origin = string.IsNullOrWhiteSpace(body.Origin) ? "Web" : body.Origin.Trim()[..Math.Min(body.Origin.Trim().Length, 30)];
            bool ok;
            if (!string.IsNullOrWhiteSpace(body.Code))
            {
                if (!StratagemCode.TryParse(body.Code, out var code)) return Results.BadRequest(new { error = "Ungültiger Code" });
                ok = _service.Activate(code, origin);
            }
            else if (body.Slot is { } slot) ok = _service.Activate(slot, origin);
            else return Results.BadRequest(new { error = "slot oder code fehlt" });
            return Results.Json(new { ok }, JsonOptions);
        });
        app.MapPost("/api/pause", (PauseRequest body) =>
        {
            _service.SetPaused(body.Paused);
            return Results.Json(new { paused = body.Paused }, JsonOptions);
        });
        app.MapPost("/api/reload-bindings", () =>
        {
            _service.ReloadBindings();
            return Results.Json(_service.GetState().Bindings, JsonOptions);
        });
        // Diagnosis: read the stratagem list from a screenshot (PNG, whole screen) without the game.
        app.MapPost("/api/read-picture", async (HttpRequest req) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length is 0 or > 30_000_000) return Results.BadRequest(new { error = "Bitte ein PNG-Bildschirmfoto schicken (max. 30 MB)." });
            Stratagem.Core.Recognition.PixelImage image;
            try { image = Stratagem.Core.Recognition.Png.Decode(ms.ToArray()); }
            catch (Exception e) { return Results.BadRequest(new { error = $"Kein lesbares PNG ({e.Message}). Bildschirmfotos mit Win+Alt+Druck sind PNG." }); }
            var scale = image.Height / 1080.0 * _service.Current.List.ScreenScale;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rows = _service.ReadPicture(image, scale);
            return Results.Json(new
            {
                width = image.Width,
                height = image.Height,
                ms = watch.ElapsedMilliseconds,
                rows = rows.Select(r => new
                {
                    r.Index,
                    State = r.State.ToString(),
                    Code = r.Code?.Letters,
                    Arrows = r.Code?.Arrows,
                    Name = r.Code is { } c ? _service.Catalog.NameOf(c) : null,
                    Icon = Convert.ToBase64String(r.Icon),
                }),
            }, JsonOptions);
        });
        app.MapGet("/api/catalog", () => Results.Json(
            _service.Catalog.Entries.Select(e => new { Name = e.Display, English = e.Name, Code = e.Code.Letters, e.Code.Arrows }), JsonOptions));
        app.MapGet("/api/keys", () => Results.Json(KeyNames.All, JsonOptions));
        app.MapGet("/api/icon/{index:int}", (int index) =>
            _service.IconOf(index) is { } png ? Results.File(png, "image/png") : Results.NotFound());
        app.MapGet("/api/info", (HttpContext ctx) => Results.Json(new
        {
            version = StratagemService.AppVersion,
            apiVersion = StratagemService.ApiVersion,
            desktop = _options.Desktop is { } d && IsLoopback(ctx) ? new { autostart = d.Autostart } : null,
            lan = _options.AllowLan && IsLoopback(ctx) ? LanUrls() : null,
        }, JsonOptions));
        app.MapPost("/api/autostart", (AutostartRequest body, HttpContext ctx) =>
        {
            if (_options.Desktop is not { } d || !IsLoopback(ctx)) return Results.NotFound();
            d.Autostart = body.Enabled;
            return Results.Json(new { autostart = d.Autostart }, JsonOptions);
        });
        app.MapPost("/api/open-folder", (HttpContext ctx) =>
        {
            if (_options.Desktop is not { } d || !IsLoopback(ctx)) return Results.NotFound();
            d.OpenDataFolder();
            return Results.Ok();
        });
        app.MapPost("/api/show-window", (HttpContext ctx) =>
        {
            if (_options.Desktop is not { } d || !IsLoopback(ctx)) return Results.NotFound();
            d.ShowWindow();
            return Results.Ok();
        });
        // QR code of a phone link (index into "lan" of /api/info), so the phone only has to scan it.
        app.MapGet("/api/qr/{index:int}", (int index, HttpContext ctx) =>
        {
            if (!IsLoopback(ctx)) return Results.NotFound();
            var urls = LanUrls();
            if (index < 0 || index >= urls.Count) return Results.NotFound();
            return Results.Text(QrCode.Encode(urls[index]).ToSvg(), "image/svg+xml");
        });
        app.MapPost("/api/quit", (HttpContext ctx) =>
        {
            if (_options.Desktop is not { } d || !IsLoopback(ctx)) return Results.NotFound();
            _ = Task.Run(async () => { await Task.Delay(200); d.Quit(); });
            return Results.Ok();
        });
        app.Map("/api/events", Events);
        app.MapGet("/{**path}", (string? path, HttpContext ctx) =>
            // The phone opens the bare address: it wants the deck, not the settings.
            string.IsNullOrEmpty(path) && !IsLoopback(ctx) ? Results.Redirect("/deck") : StaticFile(path));
    }

    private static readonly JsonSerializerOptions JsonOptions = SettingsStore.Json;

    public sealed record ActivateRequest(int? Slot, string? Code, string? Origin);
    public sealed record PauseRequest(bool Paused);
    public sealed record AutostartRequest(bool Enabled);

    private async Task Guard(HttpContext ctx, RequestDelegate next)
    {
        var req = ctx.Request;
        var loopback = IsLoopback(ctx);

        // DNS rebinding: a foreign page must not reach us under its own host name.
        var host = req.Host.Host;
        var hostOk = host is "localhost" or "127.0.0.1" or "[::1]" or "::1"
                     || (_options.AllowLan && (IPAddress.TryParse(host.Trim('[', ']'), out _)
                                               || string.Equals(host, LocalName, StringComparison.OrdinalIgnoreCase)));
        if (!hostOk)
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        if (!loopback)
        {
            var token = req.Query["token"].FirstOrDefault() ?? req.Cookies[TokenCookie] ?? req.Headers["X-Stratagem-Token"].FirstOrDefault();
            if (!_options.AllowLan || !FixedTimeEquals(token, _options.Token))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Zugang nur mit Link aus der Stratagem-App (Token fehlt).");
                return;
            }
            if (req.Query.ContainsKey("token"))
                ctx.Response.Cookies.Append(TokenCookie, _options.Token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365) });
        }

        if (req.Path.StartsWithSegments("/api"))
        {
            // A foreign origin never gets in, not even for reading.
            var origin = req.Headers.Origin.FirstOrDefault();
            if (!string.IsNullOrEmpty(origin) && !SameOrigin(origin, req))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var changes = !HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method);
            if (changes && req.Headers[HeaderName] != "1")
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync($"Header {HeaderName}: 1 fehlt");
                return;
            }
        }
        await next(ctx);
    }

    private static bool SameOrigin(string origin, HttpRequest req) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o)
        && string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase);

    private static bool FixedTimeEquals(string? a, string b) =>
        a is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static bool IsLoopback(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    /// <summary>The name the phone uses, e.g. "hellwheel.local" (answered by <see cref="MdnsResponder"/>).</summary>
    private string LocalName => _service.Current.Web.Name + ".local";

    /// <summary>Phone links: first by name, then by IP address (for phones that do not know .local names).</summary>
    private IReadOnlyList<string> LanUrls()
    {
        var port = LanPort == 80 ? "" : $":{(LanPort != 0 ? LanPort : Port)}";
        var urls = new List<string> { $"http://{LocalName}{port}/deck?token={_options.Token}" };
        try
        {
            foreach (var a in LocalAddress.All()) urls.Add($"http://{a.Address}{port}/deck?token={_options.Token}");
        }
        catch (Exception e) { _options.Log?.Invoke($"Netzwerkadressen nicht lesbar: {e.Message}"); }
        return urls;
    }

    // --- live updates -----------------------------------------------------------------------------

    private async Task Events(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = new Client(socket, new SemaphoreSlim(1, 1));
        lock (_clients) _clients.Add(client);
        try
        {
            await Send(client, StateJson());
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, ctx.RequestAborted); // nothing expected; keeps the socket alive
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception) when (ctx.RequestAborted.IsCancellationRequested || socket.State != WebSocketState.Open) { }
        finally
        {
            lock (_clients) _clients.Remove(client);
        }
    }

    // Many changes in a row (e.g. a macro) become one message.
    private void SchedulePush()
    {
        if (Interlocked.Exchange(ref _pushPending, 1) == 0) _pushTimer.Change(50, Timeout.Infinite);
    }

    private void Push()
    {
        Interlocked.Exchange(ref _pushPending, 0);
        Client[] clients;
        lock (_clients) clients = _clients.ToArray();
        if (clients.Length == 0) return;
        var json = StateJson();
        foreach (var c in clients) _ = Send(c, json);
    }

    private byte[] StateJson() => JsonSerializer.SerializeToUtf8Bytes(new { type = "state", state = _service.GetState() }, JsonOptions);

    private static async Task Send(Client client, byte[] json)
    {
        await client.SendLock.WaitAsync();
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await client.Socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception) { /* client went away */ }
        finally { client.SendLock.Release(); }
    }

    // --- web files --------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".json"] = "application/json",
        [".webmanifest"] = "application/manifest+json",
    };

    private IResult StaticFile(string? path)
    {
        path = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (path == "deck") path = "deck.html";
        if (path.Contains("..")) return Results.NotFound();
        var ext = Path.GetExtension(path);
        if (!ContentTypes.TryGetValue(ext, out var type)) return Results.NotFound();

        if (_options.WebDir is { } dir) // development: serve from disk
        {
            var file = Path.Combine(dir, path);
            return File.Exists(file) ? Results.File(File.ReadAllBytes(file), type) : Results.NotFound();
        }
        using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("web/" + path.Replace('\\', '/'));
        if (stream is null) return Results.NotFound();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Results.File(ms.ToArray(), type);
    }

    public async ValueTask DisposeAsync()
    {
        _service.Changed -= SchedulePush;
        await _pushTimer.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
