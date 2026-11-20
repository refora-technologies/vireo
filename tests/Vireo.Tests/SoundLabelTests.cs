using Vireo.Services;
using Xunit;

namespace Vireo.Tests;

public class SoundLabelTests
{
    // Made-up process numbers: 100 is Vireo, 200 its page engine, 300 the engine's sound helper; 900 is another program's engine.
    private static readonly Dictionary<int, int> Tree = new()
    {
        [100] = 10,
        [200] = 100,
        [300] = 200,
        [900] = 50,
        [901] = 900,
        [400] = 300, // deeper than any real helper
        [500] = 400,
        [600] = 500,
    };

    [Fact]
    public void The_engines_sound_helper_is_ours() => Assert.True(SoundLabel.IsOurs(300, 100, Tree));

    [Fact]
    public void The_engine_itself_is_ours() => Assert.True(SoundLabel.IsOurs(200, 100, Tree));

    [Fact]
    public void Another_programs_engine_is_not_ours()
    {
        Assert.False(SoundLabel.IsOurs(901, 100, Tree));
        Assert.False(SoundLabel.IsOurs(900, 100, Tree));
    }

    [Fact]
    public void Vireo_itself_and_unknown_processes_are_not_labelled()
    {
        Assert.False(SoundLabel.IsOurs(100, 100, Tree)); // its own sounds (none today) keep Windows' own naming
        Assert.False(SoundLabel.IsOurs(12345, 100, Tree));
    }

    [Fact]
    public void The_walk_stops_after_a_few_steps()
    {
        Assert.True(SoundLabel.IsOurs(500, 100, Tree)); // four steps up
        Assert.False(SoundLabel.IsOurs(600, 100, Tree)); // five: too far to trust (parents' numbers get reused)
    }

    [Fact]
    public void A_loop_in_the_parent_list_ends()
    {
        var loop = new Dictionary<int, int> { [1] = 2, [2] = 1, [7] = 7, [8] = 0 };
        Assert.False(SoundLabel.IsOurs(1, 100, loop));
        Assert.False(SoundLabel.IsOurs(7, 100, loop));
        Assert.False(SoundLabel.IsOurs(8, 100, loop));
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData(null, null, true)]
    [InlineData("Vireo", "", true)]
    [InlineData("vireo", @"C:\Apps\Vireo.exe,0", true)]
    [InlineData("Vireo", @"C:\Apps\Vireo.exe,0", false)]
    [InlineData("Vireo", @"c:\apps\vireo.exe,0", false)]
    public void Only_entries_without_our_name_and_icon_are_changed(string? name, string? icon, bool needs) =>
        Assert.Equal(needs, SoundLabel.NeedsLabel(name, icon, @"C:\Apps\Vireo.exe,0"));
}
