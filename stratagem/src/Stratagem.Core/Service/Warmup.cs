using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Service;

/// <summary>
/// Runs the input paths once at startup with throwaway objects, so the JIT compiles them now and not on
/// the player's first Ctrl press in the game (a first-call hitch of a few milliseconds in the hook).
/// Sends nothing, shows nothing.
/// </summary>
public static class Warmup
{
    public static void Run()
    {
        var items = new[] { new StratagemItem(StratagemCode.Parse("URDDD"), "Warmup"), new StratagemItem(StratagemCode.Parse("DDUR"), null) };
        using var runner = new MacroRunner(new NoOutput(), new PhysicalKeyState());
        foreach (var mode in new[] { RadialMode.HoldRelease, RadialMode.TapDrag })
        {
            var radial = new RadialController(new NoView(), () => items, (_, _) => { }, (_, _) => { })
            {
                Options = RadialOptions.Default with { Mode = mode },
                Enabled = true,
            };
            var router = new InputRouter(radial, runner, new PhysicalKeyState(), () => true, _ => { });
            router.SetHotkeys(new Dictionary<InputRef, HotkeyAction> { [InputRef.Of(KeyNames.KeyW)] = HotkeyAction.ForSlot(0) });
            var trigger = KeyNames.ControlLeft;
            router.OnKey(trigger, true);
            router.OnKey(trigger, false);
            router.OnMouseButton(MouseButton.Left, true);
            for (var i = 0; i < 20; i++) router.OnMouseMove(3, 5);
            router.OnMouseButton(MouseButton.Left, false);
            router.OnKey(trigger, true);
            router.OnKey(KeyNames.Escape, true);
            router.OnKey(KeyNames.Escape, false);
            router.OnKey(trigger, false);
            router.OnKey(KeyNames.KeyW, true);
            router.OnKey(KeyNames.KeyW, false);
            radial.OnTimer();
            _ = router.NeedsMouse;
        }
        _ = MacroPlanner.Plan(items[0].Code, GameBindings.Default, new MacroTiming());
        // The screen reader on a small synthetic picture (an "icon" column with structure, no list behind it).
        var picture = new byte[200 * 200 * 4];
        for (var i = 0; i < picture.Length; i += 4) picture[i] = picture[i + 1] = picture[i + 2] = (byte)(i % 97 * 2);
        _ = Recognition.MenuReader.Read(new Recognition.PixelImage(200, 200, picture), 1.0);
        _ = new Recognition.RadialSlots().Arrange(items);
    }

    private sealed class NoOutput : IInputOutput
    {
        public void Send(Key key, bool down) { }
        public void Send(MouseButton button, bool down) { }
    }

    private sealed class NoView : IRadialView
    {
        public void Show(IReadOnlyList<StratagemItem> items, RadialMode mode) { }
        public void Highlight(int? index) { }
        public void Hide(bool selected) { }
    }
}
