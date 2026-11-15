// Server and chat strings placed into script text: the saved server list (004c4e2c, #73) and the onClientMessage call (#80).
// Stock writes the strings raw. The escaping is a deliberate departure, so text from a server reads back as one string and runs nothing.
namespace OpenTribes.Tests;

using OpenTribes.Gui;
using OpenTribes.Script;

public class ScriptEscapeTests
{
    /// <summary>A string that closes the literal and the call, runs marker and comments out the rest when written raw.</summary>
    public const string Hostile = "x\"); marker(); // \"; marker(";
    /// <summary>Control bytes, high bytes and a final backslash.</summary>
    public const string AllBytes = "a\tb\nc\rd\u0001e\u001ffégÿh\\";

    private static GuiServerList MakeList()
    {
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 44, 106, 552, 309, ("headerY", 23u), ("horizontalPolicy", 2u), ("verticalPolicy", 2u), ("style", 6u));
        var content = GuiFixture.Make("SimGui::ScrollContentCtrl", 6, 6, 540, 280);
        var list = GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 817, 159, ("active", (byte)1));
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        var fx = new GuiFixture();
        fx.Content(root);
        fx.Frame();
        return (GuiServerList)list;
    }

    // Runs the saved file the way the join screen does (ClientDelegates: addGameServer -> ParseScriptRow) and counts the marker calls.
    private static (List<GuiServerList.Server> Rows, int Marks) Load(string text)
    {
        var interp = new Interpreter();
        var rows = new List<GuiServerList.Server>();
        var marks = 0;
        interp.AddCommand("addGameServer", (i, a) =>
        {
            if (GuiServerList.ParseScriptRow(rows.Count, a) is { } row)
                rows.Add(row);
            return "TRUE";
        });
        interp.AddCommand("marker", (i, a) =>
        {
            ++marks;
            return "";
        });
        interp.Evaluate(text);
        return (rows, marks);
    }

    private static GuiServerList.Server SavedAndLoaded(Action<GuiServerList.Server> set)
    {
        var l = MakeList();
        var s = new GuiServerList.Server(1, "S", "IP:10.0.0.1:28001", "1.11", 50, false) { ModList = "base", Mission = "Raindance", MissionType = "CTF", Cpu = 9 };
        set(s);
        l.AddServer(s);
        var (rows, marks) = Load(l.BuildServerFile());
        Assert.Equal(0, marks);
        return Assert.Single(rows);
    }

    [Fact]
    public void ExportEscapeDoesNotReadBackControlBytes() // 005f3744 keeps the hex digit bug, so the server file needs its own escape
    {
        var (rows, _) = Load("addGameServer(\"a\", \"" + Builtins.ExportEscape("x\u001fy") + "\", \"v\", \"1\", \"FALSE\");");
        Assert.NotEqual("x\u001fy", rows.Count == 1 ? rows[0].Name : null);
    }

    [Fact]
    public void ScriptEscapeReadsBackEveryByte()
    {
        var (rows, _) = Load("addGameServer(\"a\", \"" + Builtins.ScriptEscape(AllBytes) + "\", \"v\", \"1\", \"FALSE\");");
        Assert.Equal(AllBytes, Assert.Single(rows).Name);
    }

    [Fact]
    public void AServerNameWithScriptInItLoadsBackAsOneRow() // 004c4e2c
    {
        var r = SavedAndLoaded(s => s.Name = Hostile);
        Assert.Equal(Hostile, r.Name);
        Assert.Equal(("base", "Raindance", "CTF", 9), (r.ModList, r.Mission, r.MissionType, r.Cpu));
    }

    [Fact]
    public void AServerNameEndingInABackslashLoadsBackAsOneRow() => Assert.Equal("name\\", SavedAndLoaded(s => s.Name = "name\\").Name);

    [Fact]
    public void ModListAndMissionWithScriptInThemLoadBack()
    {
        var r = SavedAndLoaded(s =>
        {
            s.ModList = Hostile;
            s.Mission = Hostile + "m";
            s.MissionType = "t\\";
            s.Version = "v\\";
        });
        Assert.Equal((Hostile, Hostile + "m", "t\\", "v\\"), (r.ModList, r.Mission, r.MissionType, r.Version));
        var b = SavedAndLoaded(s => s.Mission = "mission\\");
        Assert.Equal("mission\\", b.Mission);
        Assert.Equal(AllBytes, SavedAndLoaded(s => s.ModList = AllBytes).ModList);
    }
}
