// A client whose startup failed must dispose without throwing, so the process exits with the error message.
namespace OpenTribes.Tests;

using OpenTribes.Client.App;

public sealed class ClientStartupFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-startfail-" + Guid.NewGuid().ToString("N"));

    public ClientStartupFailureTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void DisposeAfterAFailedStartDoesNotThrow()
    {
        // No console.cs in the root: Start fails before it assigns the delegates.
        var opt = ClientOptions.Parse(["--root", _root, "--headless"], out var err)!;
        Assert.Equal("", err);
        var app = new ClientApp(opt);
        Assert.False(app.Start(out var error));
        Assert.NotEqual("", error);
        app.Dispose();
    }
}
