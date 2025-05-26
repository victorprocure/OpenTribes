// Port of src/server/server_commands.hpp and src/server/server_commands.cpp: console commands of server operations:
// ban list, kick, master-server command surface, quit, and the client/GUI commands that exist on a dedicated server
// and only print their own errors there. docs/server-ops.md 4-7.
using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Server;

// src/server/server_commands.hpp struct ServerCommandContext.
public sealed class ServerCommandContext
{
    public NetServer? Net;
    public string RootDir = "";                 // game directory for BanList::export (host path)
    public Func<string, bool>? IsChatMenu;      // object exists and is a ChatMenu
    public Action? Quit;                        // quit(): leave the main loop
}

public static class ServerCommands
{
    // src/server/server_commands.cpp registerServerCommands.
    public static void RegisterServerCommands(Interpreter interp, ServerCommandContext ctx)
    {
        NetServer net = ctx.Net!;

        // ---- ban list (0x00401808..0x004018a0): "true"/"false" in lower case ----
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
        string root = ctx.RootDir;
        interp.AddCommand("BanList::export", (i, a) =>
        {
            if (a.Length != 2)
                return "false";
            // The original opens the file with no directory rule and dies when it cannot be
            // created (D11). Here: paths inside the game directory only (D13), failures ignored.
            string path = Builtins.ResolveGamePath(root, a[1]);
            if (path.Length == 0)
                return "true";
            try
            {
                using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
                foreach (string line in net.Bans().ExportLines())
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

        // ---- net::kick (plugin 0x0051f86c id 5 -> 0x0042babc) ----
        interp.AddCommand("net::kick", (i, a) =>
        {
            if (a.Length != 2 && a.Length != 3)
            {
                i.Print(1, "kick(playerId, <reason>);");
                return null;
            }
            net.Kick(unchecked((uint)Numbers.BorlandAtoi(a[1])), a.Length == 3 ? a[2] : null);
            return null; // "False" in every case
        });

        interp.AddCommand("listPlayers", (i, a) =>
        {
            foreach (string line in net.PlayerList())
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

        // ---- net plugin commands that need a client ----
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

        // ---- quit (0x005f3504: PostQuitMessage) ----
        Action? quit = ctx.Quit;
        interp.AddCommand("quit", (i, a) =>
        {
            quit?.Invoke();
            return null;
        });
    }
}
