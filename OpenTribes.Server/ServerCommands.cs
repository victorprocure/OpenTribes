namespace OpenTribes.Server;

using OpenTribes.Core;
using OpenTribes.Script;

/// <summary>What the server commands need from the host.</summary>
/// <remarks>Port of src/server/server_commands.hpp.</remarks>
public sealed class ServerCommandContext
{
    /// <summary>The server network.</summary>
    public NetServer? Net;
    /// <summary>The game directory, a host path, for <c>BanList::export</c>.</summary>
    public string RootDir = "";                 // game directory for BanList::export (host path)
    /// <summary>Tells whether an object exists and is a ChatMenu.</summary>
    public Func<string, bool>? IsChatMenu;
    /// <summary>Leaves the main loop, for <c>quit()</c>.</summary>
    public Action? Quit;
}

/// <summary>The console commands of server operations: ban list, kick, the master command surface, quit, and the client and GUI commands that exist on a dedicated server and only print their own errors there (docs/server-ops.md 4-7).</summary>
public static class ServerCommands
{
    /// <summary>Registers the server commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="ctx">The host services.</param>
    /// <remarks>The ban list commands (00401808 to 004018a0) return lowercase <c>true</c> and <c>false</c>. <c>net::kick</c> is plugin 0051f86c id 5 and calls 0042babc. <c>quit</c> is 005f3504, PostQuitMessage.</remarks>
    public static void RegisterServerCommands(Interpreter interp, ServerCommandContext ctx)
    {
        NetServer net = ctx.Net!;

        interp.AddCommand("BanList::add", (i, a) =>
        {
            if (a.Length != 3)
                return "false";
            net.Bans().Add(a[1], Numbers.BorlandAtoi(a[2]));
            return "true";
        });
        interp.AddCommand("BanList::addAbsolute", (i, a) =>
        {
            if (a.Length != 3)
                return "false";
            net.Bans().AddAbsolute(a[1], Numbers.BorlandAtoi(a[2]));
            return "true";
        });
        interp.AddCommand("BanList::remove", (i, a) =>
        {
            if (a.Length != 2)
                return "false";
            net.Bans().Remove(a[1]);
            return "true";
        });
        var root = ctx.RootDir;
        interp.AddCommand("BanList::export", (i, a) =>
        {
            if (a.Length != 2)
                return "false";
            // The original opens the file with no directory rule and dies when it cannot be
            // created (D11). Here: paths inside the game directory only (D13), failures ignored.
            var path = Builtins.ResolveGamePath(root, a[1]);
            if (path.Length == 0)
                return "true";
            try
            {
                using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
                foreach (var line in net.Bans().ExportLines())
                {
                    f.Write(ByteString.ToBytes(line));
                    f.Write("\r\n"u8);
                }
            }
            catch (Exception)
            {
                // fopen failed: nothing is written.
            }
            return "true";
        });

        interp.AddCommand("net::kick", (i, a) =>
        {
            if (a.Length is not 2 and not 3)
            {
                i.Print(1, "kick(playerId, <reason>);");
                return null;
            }
            net.Kick(unchecked((uint)Numbers.BorlandAtoi(a[1])), a.Length == 3 ? a[2] : null);
            return null; // "False" in every case
        });

        interp.AddCommand("listPlayers", (i, a) =>
        {
            foreach (var line in net.PlayerList())
                i.Print(1, line);
            return null;
        });

        // DNet::resolveMasters and DNet::getResolvedMaster: src/server/master.cpp.
        interp.AddCommand("DNet::TranslateAddress", (i, a) => null);
        interp.AddCommand("addGameServer", (i, a) =>
        {
            if (a.Length == 6)
                i.Print(1, "Unable to locate the GAME server list control");
            else
                i.Print(1, "addGameServer(address, name, version, ping, favorite <TRUE OR FALSE>);");
            return "FALSE";
        });
        interp.AddCommand("rebuildServerList", (i, a) => "");

        interp.AddCommand("netStats", (i, a) => null);
        interp.AddCommand("logPacketStats", (i, a) =>
        {
            i.Print(1, "Send stats: 0 packets");
            i.Print(1, "Receive stats: 0 packets");
            return null;
        });
        interp.AddCommand("connect", (i, a) =>
        {
            i.Print(1, "Connect: <net_address>");
            return null;
        });
        interp.AddCommand("disconnect", (i, a) =>
        {
            i.Print(1, "disconnect: not connected.");
            return null;
        });

        Action? quit = ctx.Quit;
        interp.AddCommand("quit", (i, a) =>
        {
            quit?.Invoke();
            return null;
        });
    }
}
