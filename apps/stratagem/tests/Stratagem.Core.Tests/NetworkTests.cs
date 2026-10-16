using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stratagem.Server;
using Xunit;

namespace Stratagem.Core.Tests;

public class QrCodeTests
{
    private const string Link = "http://hellwheel.local/deck?token=0123456789abcdef01234567";

    private static string Matrix(QrCode q) =>
        string.Concat(Enumerable.Range(0, q.Size).SelectMany(y => Enumerable.Range(0, q.Size).Select(x => q[x, y] ? '1' : '0')));

    [Fact]
    public void Phone_link_matches_a_code_checked_with_a_reader()
    {
        // Verified once by decoding with zxing-cpp (all versions 1–10 and all masks were checked that way).
        var q = QrCode.Encode(Link, mask: 2);
        Assert.Equal(4, q.Version);
        Assert.Equal("33f5f925f68399284b592debf55030e0bc60d048627de95ba7f159097eb5d950",
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Matrix(q)))).ToLowerInvariant());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 2)]
    [InlineData(62, 4)]
    [InlineData(63, 5)]
    [InlineData(213, 10)]
    public void Picks_the_smallest_version(int length, int version) =>
        Assert.Equal(version, QrCode.Encode(new string('a', length)).Version);

    [Fact]
    public void Too_long_is_an_error() => Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', 214)));

    [Fact]
    public void Has_finder_patterns_and_svg()
    {
        var q = QrCode.Encode(Link);
        foreach (var (cx, cy) in new[] { (3, 3), (q.Size - 4, 3), (3, q.Size - 4) })
        {
            Assert.True(q[cx, cy]);
            Assert.False(q[cx + 2, cy]);
            Assert.True(q[cx + 3, cy]);
        }
        var svg = QrCode.Encode(Link).ToSvg();
        Assert.StartsWith("<svg", svg);
        Assert.Contains($"viewBox=\"0 0 {q.Size + 8} {q.Size + 8}\"", svg);
    }
}

public class MdnsTests
{
    private static readonly LocalAddress Wifi = new(7, IPAddress.Parse("192.168.178.20"), 24);
    private static readonly LocalAddress HyperV = new(9, IPAddress.Parse("172.28.16.1"), 20);
    private static readonly IReadOnlyList<LocalAddress> Addresses = [HyperV, Wifi];
    private static readonly IPEndPoint Phone = new(IPAddress.Parse("192.168.178.44"), MdnsResponder.Port);

    [Fact]
    public void Answers_its_name_with_the_address_in_the_askers_network()
    {
        var reply = MdnsResponder.Respond(MdnsResponder.Query("HellWheel.local"), "hellwheel", Phone, 7, Addresses);
        Assert.NotNull(reply);
        Assert.False(reply.Value.Unicast); // normal mDNS: answer to the group
        Assert.Equal([Wifi.Address], MdnsResponder.ParseAnswers(reply.Value.Message, "hellwheel.local"));
        Assert.Equal(0x84, reply.Value.Message[2]); // response, authoritative
    }

    [Fact]
    public void Ignores_other_names_and_responses()
    {
        Assert.Null(MdnsResponder.Respond(MdnsResponder.Query("printer.local"), "hellwheel", Phone, 7, Addresses));
        var response = MdnsResponder.Respond(MdnsResponder.Query("hellwheel.local"), "hellwheel", Phone, 7, Addresses)!.Value.Message;
        Assert.Null(MdnsResponder.Respond(response, "hellwheel", Phone, 7, Addresses));
        Assert.Null(MdnsResponder.Respond([1, 2, 3], "hellwheel", Phone, 7, Addresses));
    }

    [Fact]
    public void Legacy_and_unicast_questions_get_a_direct_answer()
    {
        var legacy = MdnsResponder.Respond(MdnsResponder.Query("hellwheel.local", id: 0x1234), "hellwheel",
            new IPEndPoint(Phone.Address, 50000), 7, Addresses)!.Value;
        Assert.True(legacy.Unicast);
        Assert.Equal(0x12, legacy.Message[0]); // same id as the question
        Assert.Equal(0x34, legacy.Message[1]);
        Assert.Equal([Wifi.Address], MdnsResponder.ParseAnswers(legacy.Message, "hellwheel.local"));

        var qu = MdnsResponder.Respond(MdnsResponder.Query("hellwheel.local", unicastResponse: true), "hellwheel", Phone, 7, Addresses)!.Value;
        Assert.True(qu.Unicast);
    }

    [Fact]
    public void Ipv6_question_gets_no_address_but_an_answer()
    {
        var reply = MdnsResponder.Respond(MdnsResponder.Query("hellwheel.local", type: 28), "hellwheel", Phone, 7, Addresses)!.Value;
        Assert.Empty(MdnsResponder.ParseAnswers(reply.Message, "hellwheel.local"));
        Assert.Equal(1, reply.Message[6] << 8 | reply.Message[7]); // one record: NSEC "only IPv4"
    }

    [Fact]
    public void Reads_compressed_names()
    {
        // "x.local" at 12, then a pointer to it.
        byte[] packet = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, (byte)'x', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0, 0xC0, 12];
        var offset = 21;
        Assert.True(MdnsResponder.TryReadName(packet, ref offset, out var name));
        Assert.Equal("x.local", name);
        Assert.Equal(23, offset);
        var loop = 0;
        Assert.False(MdnsResponder.TryReadName([0xC0, 0], ref loop, out _)); // pointer to itself
    }

    [Fact]
    public void Same_network_check()
    {
        Assert.True(Wifi.SameNetwork(IPAddress.Parse("192.168.178.250")));
        Assert.False(Wifi.SameNetwork(IPAddress.Parse("192.168.179.1")));
        Assert.True(HyperV.SameNetwork(IPAddress.Parse("172.28.31.255")));
    }
}

public class LanWebTests
{
    private const string Token = "0123456789abcdef0123";

    [Fact]
    public async Task Phone_reaches_the_deck_by_name_with_the_token()
    {
        var lan = LocalAddress.All().FirstOrDefault()?.Address;
        if (lan is null) Assert.Skip("Kein Netzwerkadapter mit IPv4-Adresse");
        using var f = new ServiceFixture();
        await using var server = await WebServer.StartAsync(f.Service, new WebServerOptions(0, true, Token));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://{lan}:{server.Port}") };

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/deck")).StatusCode); // no token

        var byName = new HttpRequestMessage(HttpMethod.Get, $"/?token={Token}");
        byName.Headers.Host = $"hellwheel.local:{server.Port}";
        var root = await http.SendAsync(byName);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode); // the bare address leads to the deck
        Assert.Equal("/deck", root.Headers.Location?.OriginalString);

        var other = new HttpRequestMessage(HttpMethod.Get, $"/deck?token={Token}");
        other.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await http.SendAsync(other)).StatusCode);

        // From the PC itself: phone links (by name first) with QR codes; not from the phone.
        using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var info = await local.GetFromJsonAsync<JsonElement>("/api/info");
        var links = info.GetProperty("lan").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal($"http://hellwheel.local:{server.Port}/deck?token={Token}", links[0]);
        Assert.Contains(links, l => l.Contains(lan.ToString()));
        Assert.StartsWith("<svg", await local.GetStringAsync("/api/qr/0"));
        var fromPhone = new HttpRequestMessage(HttpMethod.Get, $"/api/qr/0?token={Token}");
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(fromPhone)).StatusCode);
    }

    [Fact]
    public async Task Renaming_works_at_once()
    {
        using var f = new ServiceFixture();
        var s = f.Service.Settings;
        s.Web.Name = "Mein-PC.local";
        f.Service.UpdateSettings(s);
        Assert.Equal("mein-pc", f.Service.Current.Web.Name);
        s = f.Service.Settings;
        s.Web.Name = "nicht gültig!";
        f.Service.UpdateSettings(s);
        Assert.Equal("hellwheel", f.Service.Current.Web.Name);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Taken_lan_port_falls_back_to_the_normal_port()
    {
        using var blocker = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
        blocker.Start();
        var taken = ((IPEndPoint)blocker.LocalEndpoint).Port;
        using var f = new ServiceFixture();
        var log = new List<string>();
        await using var server = await WebServer.StartAsync(f.Service, new WebServerOptions(0, true, Token, Log: log.Add, LanPort: taken));
        Assert.Equal(0, server.LanPort);
        Assert.Contains(log, l => l.Contains($"Port {taken}"));
        using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/api/state")).StatusCode);
    }
}
