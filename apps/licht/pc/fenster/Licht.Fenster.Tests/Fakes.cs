using Licht.Fenster;

namespace Licht.Fenster.Tests;

/// <summary>Licht as the window sees it, controlled by the test: calls wait until the test answers them.</summary>
public sealed class FakeHub : IHub
{
    public List<(string Method, object?[] Args, TaskCompletionSource<object?> Reply)> Calls { get; } = new();
    public List<IDictionary<string, object?>> Sent { get; } = new();

    public event Action<IDictionary<string, object?>>? Message;

    /// <summary>Answer calls of these methods immediately with this result.</summary>
    public Dictionary<string, Func<object?[], object?>> AutoReply { get; } = new();

    public Task<object?> Call(string method, params object?[] args)
    {
        var reply = new TaskCompletionSource<object?>();
        Calls.Add((method, args, reply));
        if (AutoReply.TryGetValue(method, out var answer)) reply.SetResult(answer(args));
        return reply.Task;
    }

    public void Send(IDictionary<string, object?> message) => Sent.Add(message);

    public void Raise(string json) => Message?.Invoke((IDictionary<string, object?>)Json.Parse(json)!);

    public (string Method, object?[] Args, TaskCompletionSource<object?> Reply) Last(string method) =>
        Calls.Last(c => c.Method == method);
}

/// <summary>Time that moves only when the test says so; delays end when time has moved past them.</summary>
public sealed class FakeClock : IClock
{
    private readonly List<(DateTime Due, TaskCompletionSource<bool> Done)> _waiting = new();

    public DateTime UtcNow { get; private set; } = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public Task Delay(TimeSpan delay)
    {
        var done = new TaskCompletionSource<bool>();
        _waiting.Add((UtcNow + delay, done));
        return done.Task;
    }

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        foreach (var w in _waiting.Where(w => w.Due <= UtcNow).ToList())
        {
            _waiting.Remove(w);
            w.Done.SetResult(true);
        }
    }
}

/// <summary>The window's dialogs and messages, answered by the test.</summary>
public sealed class FakeUi : IUi
{
    public List<(string Text, bool Ok)> Toasts { get; } = new();
    public List<(string Title, string Text)> Notices { get; } = new();
    public Queue<object?> Answers { get; } = new();
    public AddViewModel? Add { get; private set; }
    public ShareViewModel? Share { get; private set; }
    public TaskCompletionSource<bool> DialogClosed { get; private set; } = new();
    public string? Clipboard { get; private set; }
    public int BroughtToFront { get; private set; }
    public bool QuitCalled { get; private set; }
    public (string Title, string Text)? Fatal { get; private set; }

    public Task<string?> AskText(string title, string label, string value, string okLabel) => Task.FromResult((string?)Answers.Dequeue());
    public Task<bool> Confirm(string title, string text, string okLabel, bool danger)
    {
        Confirms.Add((title, text));
        return Task.FromResult((bool)Answers.Dequeue()!);
    }

    public Task Notice(string title, string text)
    {
        Notices.Add((title, text));
        return Task.CompletedTask;
    }

    public void Toast(string text, bool ok = false) => Toasts.Add((text, ok));

    public Task ShowAdd(AddViewModel add)
    {
        Add = add;
        DialogClosed = new TaskCompletionSource<bool>();
        add.CloseRequested += () => DialogClosed.TrySetResult(true);
        return DialogClosed.Task;
    }

    public Task ShowShare(ShareViewModel share)
    {
        Share = share;
        share.CopyToClipboard = text => Clipboard = text;
        var closed = DialogClosed = new TaskCompletionSource<bool>();
        share.CloseRequested += () => closed.TrySetResult(true);
        return closed.Task;
    }

    public ConnectionsViewModel? Connections { get; private set; }
    public List<(string Title, string Text)> Confirms { get; } = new();

    public Task ShowConnections(ConnectionsViewModel connections)
    {
        Connections = connections;
        DialogClosed = new TaskCompletionSource<bool>();
        return DialogClosed.Task;
    }

    public Task ShowFatal(string title, string text)
    {
        Fatal = (title, text);
        return Task.CompletedTask;
    }

    public void CopyToClipboard(string text) => Clipboard = text;
    public void BringToFront() => BroughtToFront++;
    public void Quit() => QuitCalled = true;
}

public static class Lights
{
    public static string Json(string id, string name, bool on = false, string status = "online", string mode = "white",
        int brightness = 50, int temperature = 50, int hue = 0, int sat = 100, bool color = true, bool temp = true) =>
        Licht.Fenster.Json.Write(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = name,
            ["status"] = status,
            ["online"] = status == "online",
            ["on"] = on,
            ["mode"] = mode,
            ["brightness"] = brightness,
            ["temperature"] = temperature,
            ["color"] = new Dictionary<string, object?> { ["h"] = hue, ["s"] = sat },
            ["supports"] = new Dictionary<string, object?> { ["brightness"] = true, ["temperature"] = temp, ["color"] = color },
        });

    public static string Message(params string[] lights) => "{\"t\":\"lights\",\"lights\":[" + string.Join(",", lights) + "]}";
}
