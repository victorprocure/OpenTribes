// Port of tests/test_command_registry.cpp: one native command, one registration. Modules that both register a name
// silently replace each other (the later one wins), which has hidden reference-verified commands behind simpler
// stubs more than once. The C++ builds the full script, sim and game command set (fixtures World) and fails (exit
// status 1) when a name is registered twice and not listed below; here the same check is one assertion. Since stage P3
// the set is the script built-ins and the sim commands; P4 adds the game commands through Fixtures.World.
namespace OpenTribes.Tests;

public class CommandRegistryTests
{
    [Fact]
    public void TestCommandRegistry()
    {
        string root = Path.Combine(TestPaths.TmpDir("command_registry_root"));
        Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "base"));
        var w = new Fixtures.World(root);

        // Intended replacements: (none). LastHope mode (applyLastHopeMode) and the network
        // layer (GameServer::registerCommands: remoteEval, startGhosting, resetGhostManagers)
        // replace commands on purpose, but they are not part of this set.
        var allowed = new HashSet<string>();

        var bad = new List<string>();
        foreach (string name in w.In.ReplacedCommands())
            if (!allowed.Contains(name))
                bad.Add("command registered twice: " + name);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}
