using OpenTribes.Client.Scene;
using Xunit;

namespace OpenTribes.Tests;

// Grenade_clientUpdate never advances the ambient thread of the shell shape (no call to ShapeThread_advanceTime 005c01b0).
public class ProjectileAmbientTests
{
    [Theory]
    [InlineData("Grenade", false)]
    [InlineData("Bullet", true)]
    [InlineData("RocketDumb", true)]
    [InlineData("SeekingMissile", true)]
    public void OnlyTheGrenadeKeepsFrameZero(string cls, bool advances) => Assert.Equal(advances, ProjectileVisual.AdvancesAmbient(cls));
}
