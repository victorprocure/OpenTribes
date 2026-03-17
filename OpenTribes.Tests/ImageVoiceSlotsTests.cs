// Image sound voice bookkeeping of a player ghost (docs/audio.md 3.2), no device and no data (R5, R9).
using OpenTribes.Client.Audio;

namespace OpenTribes.Tests;

public class ImageVoiceSlotsTests
{
    [Fact]
    public void OneShotHandleIsKeptSoTheVoiceFollowsThePlayer()
    {
        var s = new ImageVoiceSlots();
        s.Started(0, 41, loops: false);
        Assert.Equal(41, s.Handles[0]);
    }

    [Fact]
    public void StateChangeStopsOnlyLoopingVoices()
    {
        var s = new ImageVoiceSlots();
        s.Started(0, 41, loops: false);
        s.Started(1, 42, loops: true);
        Assert.Equal(0, s.StateChanged(0));
        Assert.Equal(41, s.Handles[0]);
        Assert.Equal(42, s.StateChanged(1));
        Assert.Equal(0, s.Handles[1]);
        Assert.Equal(0, s.StateChanged(1));
    }

    [Fact]
    public void FailedStartClearsTheSlotAndBadSlotsAreIgnored()
    {
        var s = new ImageVoiceSlots();
        s.Started(2, 7, loops: true);
        s.Started(2, 0, loops: true);
        Assert.Equal(0, s.StateChanged(2));
        s.Started(9, 5, loops: true);
        Assert.Equal(0, s.StateChanged(-1));
    }
}
