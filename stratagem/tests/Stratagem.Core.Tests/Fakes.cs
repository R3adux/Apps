using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Tests;

public sealed class FakeOutput : IInputOutput
{
    private readonly List<string> _events = [];
    public IReadOnlyList<string> Events { get { lock (_events) return _events.ToList(); } }
    public void Send(Key key, bool down) { lock (_events) _events.Add($"{(down ? "+" : "-")}{key.Name}"); }
    public void Send(MouseButton button, bool down) { lock (_events) _events.Add($"{(down ? "+" : "-")}Mouse{button}"); }
    public void Clear() { lock (_events) _events.Clear(); }
}

public sealed class FakeView : IRadialView
{
    public bool Visible { get; private set; }
    public int? Highlighted { get; private set; }
    public IReadOnlyList<StratagemItem> Items { get; private set; } = [];
    public int ShowCount { get; private set; }
    public void Show(IReadOnlyList<StratagemItem> items, RadialMode mode) { Visible = true; Items = items; ShowCount++; }
    public void Highlight(int? index) => Highlighted = index;
    public void Hide() { Visible = false; Highlighted = null; }
}

public static class Items
{
    public static readonly StratagemItem Reinforce = new(StratagemCode.Parse("UDRLU"), "Reinforce");
    public static readonly StratagemItem Resupply = new(StratagemCode.Parse("DDUR"), "Resupply");
    public static readonly StratagemItem Eagle = new(StratagemCode.Parse("URDDD"), "Eagle 500kg Bomb");
    public static readonly StratagemItem Laser = new(StratagemCode.Parse("RDURD"), "Orbital Laser", Available: false);

    /// <summary>4 items: top = Reinforce, right = Resupply, bottom = Eagle, left = Laser (cooldown).</summary>
    public static IReadOnlyList<StratagemItem> Four => [Reinforce, Resupply, Eagle, Laser];
}
