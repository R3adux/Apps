using Stratagem.Core.Input;
using Stratagem.Core.Model;
using Stratagem.Core.Radial;

namespace Stratagem.Core.Tests;

public class StratagemCodeTests
{
    [Theory]
    [InlineData("URDDD")]
    [InlineData("↑→↓↓↓")]
    [InlineData("↑ → ↓ ↓ ↓")]
    [InlineData("up right down down down")]
    [InlineData("oben, rechts, unten, unten, unten")]
    [InlineData("u-r-d-d-d")]
    public void Parses_all_notations(string text)
    {
        Assert.True(StratagemCode.TryParse(text, out var code));
        Assert.Equal("URDDD", code.Letters);
        Assert.Equal("↑ → ↓ ↓ ↓", code.Arrows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WASD")]  // ambiguous on purpose
    [InlineData("UPX")]
    public void Rejects_invalid(string text) => Assert.False(StratagemCode.TryParse(text, out _));

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(StratagemCode.Parse("URD"), StratagemCode.Parse("↑→↓"));
        Assert.Equal(StratagemCode.Parse("URD").GetHashCode(), StratagemCode.Parse("↑→↓").GetHashCode());
        Assert.NotEqual(StratagemCode.Parse("URD"), StratagemCode.Parse("URDD"));
    }

    [Fact]
    public void Catalog_loads_and_names_codes()
    {
        var catalog = StratagemCatalog.Default;
        Assert.True(catalog.Entries.Count > 40);
        Assert.Equal("Verstärkung", catalog.NameOf(StratagemCode.Parse("UDRLU")));   // German where known
        Assert.Equal("Adler – 500-kg-Bombe", catalog.NameOf(StratagemCode.Parse("URDDD")));
        Assert.Equal("Eagle Airstrike", catalog.NameOf(StratagemCode.Parse("URDR")));    // else English
        Assert.Equal("URDDD", catalog.Find("Eagle 500kg Bomb")?.Code.Letters);
        Assert.Equal("URDDD", catalog.Find("adler – 500-kg-bombe")?.Code.Letters);
        Assert.Null(catalog.NameOf(StratagemCode.Parse("UUUUUUUUU")));
    }
}

public class KeyTests
{
    [Fact]
    public void Names_round_trip()
    {
        foreach (var name in KeyNames.All)
        {
            Assert.True(KeyNames.TryParse(name, out var key), name);
            Assert.Equal(name, key.Name);
        }
    }

    [Theory]
    [InlineData("w", "KeyW")]
    [InlineData("W", "KeyW")]
    [InlineData("left ctrl", "ControlLeft")]
    [InlineData("LCtrl", "ControlLeft")]
    [InlineData("ctrl", "ControlLeft")]
    [InlineData("up", "ArrowUp")]
    [InlineData("up arrow", "ArrowUp")]
    [InlineData("num 1", "Numpad1")]
    [InlineData("numpad_7", "Numpad7")]
    [InlineData("5", "Digit5")]
    [InlineData("pos1", "Home")]
    public void Game_names_map_to_keys(string raw, string expected) => Assert.Equal(expected, KeyNames.FromGameName(raw)?.Name);

    [Fact]
    public void Extended_keys_differ()
    {
        Assert.NotEqual(KeyNames.ControlLeft, KeyNames.ControlRight);
        Assert.True(KeyNames.ArrowUp.Extended);
        Assert.False(KeyNames.TryParse("Numpad8", out var n8) && n8.Extended);
    }

    [Theory]
    [InlineData("MouseX1")]
    [InlineData("mousex2")]
    [InlineData("ControlLeft")]
    public void InputRef_parses(string name) => Assert.True(InputRef.TryParse(name, out _));

    [Fact]
    public void InputRef_rejects_unknown() => Assert.False(InputRef.TryParse("Mouse9", out _));
}

public class RadialLayoutTests
{
    [Theory]
    [InlineData(0, -100, 0)]   // up
    [InlineData(100, 0, 1)]    // right
    [InlineData(0, 100, 2)]    // down
    [InlineData(-100, 0, 3)]   // left
    [InlineData(70, -100, 0)]  // up-right, still closer to up
    [InlineData(100, -70, 1)]  // right-up, closer to right
    public void Four_segments(double dx, double dy, int expected) => Assert.Equal(expected, RadialLayout.Hit(dx, dy, 4, 10));

    [Theory]
    [InlineData(100, -100, 1)]  // up-right diagonal
    [InlineData(100, 100, 3)]   // down-right
    [InlineData(-100, 100, 5)]  // down-left
    [InlineData(-100, -100, 7)] // up-left
    public void Eight_segments_diagonals(double dx, double dy, int expected) => Assert.Equal(expected, RadialLayout.Hit(dx, dy, 8, 10));

    [Fact]
    public void Dead_zone_and_edge_cases()
    {
        Assert.Null(RadialLayout.Hit(3, 3, 4, 10));
        Assert.Null(RadialLayout.Hit(100, 0, 0, 10));
        Assert.Equal(0, RadialLayout.Hit(-100, 50, 1, 10));
        // Three segments: 0 top, 1 lower right, 2 lower left.
        Assert.Equal(1, RadialLayout.Hit(100, 60, 3, 10));
        Assert.Equal(2, RadialLayout.Hit(-100, 60, 3, 10));
    }

    [Fact]
    public void Clamp_keeps_direction()
    {
        var (x, y) = RadialLayout.Clamp(300, 400, 100);
        Assert.Equal(60, x, 3);
        Assert.Equal(80, y, 3);
        Assert.Equal((5.0, 5.0), RadialLayout.Clamp(5, 5, 100));
    }

    [Fact]
    public void Center_angles_go_clockwise_from_top()
    {
        Assert.Equal(-90, RadialLayout.CenterAngle(0, 4));
        Assert.Equal(0, RadialLayout.CenterAngle(1, 4));
        Assert.Equal(90, RadialLayout.CenterAngle(2, 4));
    }
}
