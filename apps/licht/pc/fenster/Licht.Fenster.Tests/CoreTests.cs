using System.IO.Pipes;
using System.Text;
using Licht.Fenster;
using ZXing;
using ZXing.Common;

namespace Licht.Fenster.Tests;

public class JsonTests
{
    [Fact]
    public void Reads_and_writes_the_messages_exactly()
    {
        var text = """{"t":"lights","lights":[{"id":"7","name":"Küche \"hinten\"\n","on":true,"brightness":70,"x":-1.5e2,"n":null,"e":[],"o":{}}]}""";
        var value = (IDictionary<string, object?>)Json.Parse(text)!;
        var light = (IDictionary<string, object?>)((IList<object?>)value["lights"]!)[0]!;
        Assert.Equal("Küche \"hinten\"\n", light["name"]);
        Assert.Equal(true, light["on"]);
        Assert.Equal(70.0, light["brightness"]);
        Assert.Equal(-150.0, light["x"]);
        Assert.Null(light["n"]);
        Assert.Equal(text, Json.Write(value).Replace("-150", "-1.5e2"));
    }

    [Fact]
    public void Unicode_escapes_and_control_characters()
    {
        Assert.Equal("Aä😀\u2028", Json.Parse("\"A\\u00e4\\ud83d\\ude00\\u2028\""));
        Assert.Equal("\"\\u0001\\u2028\\t\"", Json.Write("\u0001\u2028\t"));
        Assert.Equal("[1,2.5,true,null,\"x\"]", Json.Write(new object?[] { 1, 2.5, true, null, "x" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\" 1}")]
    [InlineData("[1,]")]
    [InlineData("01")]
    [InlineData("\"ab")]
    [InlineData("tru")]
    [InlineData("{} x")]
    [InlineData("\"\\x\"")]
    public void Broken_json_is_rejected(string text) => Assert.Throws<System.FormatException>(() => Json.Parse(text));
}

public class LightTests
{
    [Fact]
    public void A_lamp_from_Licht_and_back()
    {
        var light = Light.FromJson((IDictionary<string, object?>)Json.Parse(Lights.Json("3", "Flur", on: true, status: "connecting", mode: "colour", brightness: 40, hue: 200, sat: 80))!);
        Assert.Equal(("3", "Flur", LampStatus.Connecting, true, LampMode.Colour, 40, 200, 80),
            (light.Id, light.Name, light.Status, light.On, light.Mode, light.Brightness, light.Hue, light.Saturation));
        Assert.True(light.SupportsColor && light.SupportsTemperature && light.SupportsBrightness);
        var again = Light.FromJson((IDictionary<string, object?>)Json.Parse(Json.Write(light.ToJson()))!);
        Assert.Equal(Json.Write(light.ToJson()), Json.Write(again.ToJson()));
    }

    [Fact]
    public void Older_messages_without_status_use_online()
    {
        var light = Light.FromJson((IDictionary<string, object?>)Json.Parse("""{"id":"1","name":"A","online":false}""")!);
        Assert.Equal(LampStatus.Offline, light.Status);
    }

    [Fact]
    public void Colours_are_the_same_as_in_the_phone_app()
    {
        // Values of web/color.js
        Assert.Equal(new Rgb(255, 0, 0), LampColors.FromHsv(0, 1));
        Assert.Equal(new Rgb(0, 0, 255), LampColors.FromHsv(240, 1));
        Assert.Equal(new Rgb(255, 255, 255), LampColors.FromHsv(123, 0));
        Assert.Equal(new Rgb(255, 166, 77), LampColors.FromTemperature(0));
        Assert.Equal(new Rgb(255, 241, 220), LampColors.FromTemperature(50));
        Assert.Equal(new Rgb(207, 227, 255), LampColors.FromTemperature(100));
        Assert.Equal(new Rgb(255, 204, 149), LampColors.FromTemperature(25));
        Assert.Equal(11, Preset.All.Count);
    }
}

public class QrTests
{
    [Theory]
    [InlineData("MT:Y.K90AFN00KA0648G00")]
    [InlineData("MT:2XO00Q1212-1064IJ3P0WO3M0XE.L1GJRQ1QPKJ1KD5N1ORLP11SLJ1O0*2XO00YDA13ARG46Y900")]
    public void Share_codes_become_readable_QR_codes(string code) => Assert.Equal(code, Decode(Qr.Matrix(code)));

    [Fact]
    public void Ten_lamps_with_addresses_fit_into_one_QR_code()
    {
        var one = "2XO00Q1212-1064IJ3P0WO3M0XE.L1GJRQ1QPKJ1KD5N1ORLP11SLJ1O0";
        var code = "MT:" + string.Join("*", Enumerable.Repeat(one, 10));
        var matrix = Qr.Matrix(code);
        Assert.True(matrix.GetLength(0) <= 109, $"QR code too big: {matrix.GetLength(0)} modules"); // still easy to scan
        Assert.Equal(code, Decode(matrix));
    }

    private static string? Decode(bool[,] matrix)
    {
        const int scale = 4, quiet = 4;
        var size = matrix.GetLength(0);
        var pixels = (size + 2 * quiet) * scale;
        var bytes = new byte[pixels * pixels];
        for (var y = 0; y < pixels; y++)
        for (var x = 0; x < pixels; x++)
        {
            var my = y / scale - quiet;
            var mx = x / scale - quiet;
            var dark = my >= 0 && mx >= 0 && my < size && mx < size && matrix[my, mx];
            bytes[y * pixels + x] = dark ? (byte)0 : (byte)255;
        }
        var source = new RGBLuminanceSource(bytes, pixels, pixels, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(source)),
            new Dictionary<DecodeHintType, object> { [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } });
        return result?.Text;
    }
}

public class HubConnectionTests
{
    [Fact]
    public async Task Calls_replies_events_and_the_end_of_Licht()
    {
        // Licht's side of the pipes
        using var toWindow = new AnonymousPipeServerStream(PipeDirection.Out);
        using var fromWindow = new AnonymousPipeServerStream(PipeDirection.In);
        using var windowIn = new AnonymousPipeClientStream(PipeDirection.In, toWindow.ClientSafePipeHandle);
        using var windowOut = new AnonymousPipeClientStream(PipeDirection.Out, fromWindow.ClientSafePipeHandle);
        var licht = new StreamWriter(toWindow, new UTF8Encoding(false)) { AutoFlush = true };
        var fromWindowReader = new StreamReader(fromWindow, Encoding.UTF8);

        var connection = new HubConnection(new StreamReader(windowIn, Encoding.UTF8), new StreamWriter(windowOut, new UTF8Encoding(false)), null);
        var messages = new List<string?>();
        var closed = new TaskCompletionSource<bool>();
        connection.Message += m => { lock (messages) messages.Add(m.Str("t")); };
        connection.Closed += () => closed.TrySetResult(true);
        connection.Start();

        var call = connection.Call("update", "7", new Dictionary<string, object?> { ["on"] = true });
        var request = (IDictionary<string, object?>)Json.Parse((await fromWindowReader.ReadLineAsync())!)!;
        Assert.Equal("call", request["t"]);
        Assert.Equal("update", request["method"]);
        Assert.Equal("""["7",{"on":true}]""", Json.Write(request["args"]));

        await licht.WriteLineAsync("""{"t":"light","light":{"id":"7"}}""");
        await licht.WriteLineAsync("kein json");
        await licht.WriteLineAsync(Json.Write(new Dictionary<string, object?> { ["t"] = "reply", ["id"] = request["id"], ["ok"] = true, ["result"] = Json.Parse("""{"light":{"id":"7"}}""") }));
        var result = (IDictionary<string, object?>)(await call.WaitAsync(TimeSpan.FromSeconds(5)))!;
        Assert.NotNull(result.Obj("light"));

        var failing = connection.Call("remove", "8");
        var second = (IDictionary<string, object?>)Json.Parse((await fromWindowReader.ReadLineAsync())!)!;
        await licht.WriteLineAsync(Json.Write(new Dictionary<string, object?> { ["t"] = "reply", ["id"] = second["id"], ["ok"] = false, ["error"] = "Lampe nicht gefunden.", ["status"] = 404 }));
        var error = await Assert.ThrowsAsync<HubException>(() => failing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(("Lampe nicht gefunden.", 404), (error.Message, error.Status));

        var pending = connection.Call("lights");
        await fromWindowReader.ReadLineAsync();
        toWindow.Dispose(); // Licht ends
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HubException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        lock (messages) Assert.Equal(new[] { "light" }, messages);
        await Assert.ThrowsAsync<HubException>(() => connection.Call("lights")); // after the end: fails at once
    }
}
