// docs/client-scripts.md 2.2 to 2.15: the console commands of the client with their real effect, registered over the stub
// table of OpenTribes.Script (ClientCommandTable). Hardware and Windows services follow D36: the OpenGL device is the only
// device (D31), the CD layer is absent, the browser and the editor tools are not opened unless the user opts in.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public sealed class ClientCommandHandlers
{
    private readonly ClientApp mApp;
    private Interpreter I => mApp.Interp;
    private int mShotSeq;
    private bool mInputOpen = true;
    private readonly HashSet<string> mInactive = new(StringComparer.OrdinalIgnoreCase);
    private string mFsDevice = "Software", mWinDevice = "Software";
    private int mResIndex;

    // The modes the one device lists (docs/client-app.md 4.3): the fixed 4:3 ladder, plus the desktop.
    public static readonly (int W, int H)[] Modes = { (640, 480), (800, 600), (1024, 768), (1280, 1024), (1600, 1200) };
    public static readonly string[] DeviceNames = { "OpenGL", "Software", "Glide" };

    public ClientCommandHandlers(ClientApp app) => mApp = app;

    public bool DeviceActive(string name) => !mInactive.Contains(name);

    private void Cmd(string name, Func<string[], string?> body) => I.AddCommand(name, (_, a) => body(a));

    private bool IsCanvas(string cmd, string name, bool quiet = false)
    {
        if (string.Equals(name, mApp.Canvas.Name, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!quiet)
            I.Print(1, "Could not find simCanvas: " + name);
        return false;
    }

    private static string B(bool v) => v ? "True" : "False";

    public static bool IsTrue(string s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

    public void Register()
    {
        RegisterShell();
        RegisterDisplay();
        RegisterInput();
        RegisterRedbook();
        RegisterNet();
        RegisterGame();
        RegisterMisc();
    }

    // ---- 2.2 shell ----

    private static readonly string[] kUrlSchemes = { "http://", "https://", "ftp://", "mailto:", "file://" };

    private bool OpenWithSystem(string what)
    {
        if (!mApp.Opt.AllowBrowser)
        {
            mApp.Log("shell: not opened (R6; start with --allow-browser): " + what);
            return true;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = OperatingSystem.IsWindows() ? what : OperatingSystem.IsMacOS() ? "open" : "xdg-open", Arguments = OperatingSystem.IsWindows() ? "" : "\"" + what + "\"", UseShellExecute = OperatingSystem.IsWindows() });
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    private void RegisterShell()
    {
        string? Html(string[] a, int urlIndex)
        {
            string url = a[urlIndex];
            if (!kUrlSchemes.Any(s => url.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                I.Print(1, "HTMLOpen: can only open properly prefixed HTML URLs.");
                return "False";
            }
            return null;
        }
        Cmd("HTMLOpen", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "HTMLOpen(url);");
                return "False";
            }
            return Html(a, 1) ?? (OpenWithSystem(a[1]) ? "True" : "False");
        });
        Cmd("HTMLOpenAndGoWin", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "HTMLOpenAndGoWin(canvas, url);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            if (Html(a, 2) is string bad)
                return bad;
            mApp.SetFullscreen(false);
            return OpenWithSystem(a[2]) ? "True" : "False";
        });
        Cmd("Notepad", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "Notepad(file);");
                return "False";
            }
            if (!a[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                I.Print(1, "Notepad: can only open .txt files");
                return "False";
            }
            if (a[1].Contains(' '))
            {
                I.Print(1, "Notepad: file names with spaces are not supported");
                return "False";
            }
            return OpenWithSystem(Builtins.HostFilePath(mApp.Opt.Root, a[1])) ? "True" : "False";
        });
    }

    // ---- 2.3 display and canvas ----

    private void RegisterDisplay()
    {
        Cmd("flushTextureCache", a =>
        {
            if (a.Length >= 2 && !IsCanvas(a[0], a[1]))
                return "False";
            mApp.Dev?.FlushTextureCache();
            return "";
        });
        Cmd("setGamma", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "setGamma(canvasName, gamma);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            double g = Numbers.BorlandAtof(a[2]);
            if (g < 0.05 || g > 20)
            {
                I.Print(1, "Out of bounds gamma, must be between 0.05 and 20, value clamped");
                g = Math.Clamp(g, 0.05, 20);
            }
            mApp.SetGamma((float)g);
            I.SetVariable("$pref::Display::gammaValue", g.ToString("0.######", CultureInfo.InvariantCulture));
            return "";
        });
        Cmd("setFullscreenDevice", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "setFullscreenDevice(canvasName, deviceName);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            if (!DeviceNames.Contains(a[2], StringComparer.OrdinalIgnoreCase))
            {
                I.Print(1, "Failed to set Fullscreen Device to " + a[2]);
                return "False";
            }
            mFsDevice = a[2];
            return "True";
        });
        Cmd("setWindowedDevice", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "setWindowedDevice(canvasName, deviceName);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            if (!DeviceNames.Contains(a[2], StringComparer.OrdinalIgnoreCase))
            {
                I.Print(1, "Failed to set windowed Device to " + a[2]);
                return "False";
            }
            mWinDevice = a[2];
            return "True";
        });
        Cmd("listDevices", a =>
        {
            I.Print(1, "Number of devices: 1");
            var sb = new StringBuilder("OpenGL (SDL3)" + (string.Equals(mFsDevice, "OpenGL", StringComparison.OrdinalIgnoreCase) ? " *" : "") + ": ");
            sb.Append(string.Join(", ", Modes.Select(m => $"{m.W} x {m.H}")));
            I.Print(1, sb.ToString());
            return "";
        });
        Cmd("testDevice", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "testDevice <deviceName>");
                return "False";
            }
            return DeviceNames.Contains(a[1], StringComparer.OrdinalIgnoreCase) ? "" : "False";
        });
        Cmd("isGfxDriver", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "Incorrect syntax for isGfxDriver(CanvasName, DriverName)");
                return "False";
            }
            // D31: Software and OpenGL exist; Glide is not reproduced (the stock default then falls to Software).
            return IsCanvas(a[0], a[1]) && (string.Equals(a[2], "OpenGL", StringComparison.OrdinalIgnoreCase) || string.Equals(a[2], "Software", StringComparison.OrdinalIgnoreCase)) ? "true" : "False";
        });
        Cmd("messageCanvasDevice", a =>
        {
            if (a.Length >= 2 && !IsCanvas(a[0], a[1]))
                return "False";
            return "";
        });
        Cmd("setScreenShotSeq", a =>
        {
            if (a.Length == 2)
                mShotSeq = Numbers.BorlandAtoi(a[1]);
            return "";
        });
        Cmd("screenShot", a =>
        {
            if (a.Length < 2 || a.Length > 3)
            {
                I.Print(1, "screenShot(canvasName [, fileName]);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            string file;
            if (a.Length == 3)
                file = a[2];
            else
            {
                do
                    file = "temp\\sshot" + mShotSeq++.ToString("D4", CultureInfo.InvariantCulture) + ".bmp";
                while (File.Exists(Builtins.HostFilePath(mApp.Opt.Root, file)));
            }
            if (!Builtins.IsWriteablePath(file))
            {
                I.Print(1, "Error creating file '" + file + "'");
                return "False";
            }
            mApp.QueueScreenshot(Builtins.HostFilePath(mApp.Opt.Root, file));
            return "";
        });
        Cmd("swapSurfaces", a =>
        {
            if (a.Length == 2 && IsCanvas(a[0], a[1]))
                mApp.SetFullscreen(!mApp.IsFullscreen);
            return "";
        });
        Cmd("isFullscreenMode", a => a.Length == 2 && IsCanvas(a[0], a[1]) ? (mApp.IsFullscreen ? "true" : "FALSE") : "False");
        Cmd("setFullscreenMode", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "setFullscreenMode(canvasName, bool);");
                return "False";
            }
            if (IsCanvas(a[0], a[1]))
                mApp.SetFullscreen(IsTrue(a[2]));
            return "";
        });
        Cmd("setFSResolution", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "setFSResolution(canvasName, WxH);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            string[] wh = a[2].Split('x');
            if (wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h) && w > 0 && h > 0)
            {
                int k = Array.FindIndex(Modes, m => m.W == w && m.H == h);
                if (k >= 0)
                    mResIndex = k;
                mApp.FullscreenSize = (w, h);
                if (mApp.IsFullscreen)
                    mApp.ResizeWindow(w, h);
            }
            return "";
        });
        // The one device is not a virtual full screen mode.
        Cmd("isVirtualFS", a => "false");
        Cmd("nextRes", a => StepRes(a, +1));
        Cmd("prevRes", a => StepRes(a, -1));
        Cmd("lockWindowSize", a => { mApp.LockSize = true; return ""; });
        Cmd("unlockWindowSize", a => { mApp.LockSize = false; return ""; });
        Cmd("setWindowSize", a =>
        {
            if (a.Length != 4)
            {
                I.Print(1, "setWindowSize(canvasName, width, height);");
                return "False";
            }
            if (!IsCanvas(a[0], a[1]))
                return "False";
            int w = Numbers.BorlandAtoi(a[2]), h = Numbers.BorlandAtoi(a[3]);
            if (w > 0 && h > 0)
            {
                mApp.ResizeWindow(w, h);
                I.Print(1, $"Mode: {w} x {h}");
            }
            return "";
        });
        Cmd("resetUpdateRegion", a =>
        {
            if (a.Length != 2 || !string.Equals(a[1], mApp.Canvas.Name, StringComparison.OrdinalIgnoreCase))
            {
                I.Print(1, "resetUpdateRegion: " + (a.Length > 1 ? a[1] : "") + " is not a SimGui::Canvas");
                return "False";
            }
            mApp.Canvas.Invalidate();
            return "";
        });
    }

    private string? StepRes(string[] a, int d)
    {
        if (a.Length != 2 || !IsCanvas(a[0], a[1]))
            return "False";
        mResIndex = (mResIndex + d + Modes.Length) % Modes.Length;
        var (w, h) = Modes[mResIndex];
        mApp.FullscreenSize = (w, h);
        mApp.ResizeWindow(w, h);
        I.Print(1, $"Mode: {w} x {h}");
        return "";
    }

    // ---- 2.6 input ----

    private static readonly string[] kDevices = { "keyboard0", "mouse0" };

    private void RegisterInput()
    {
        bool created = true;
        Cmd("newInputManager", a =>
        {
            if (a.Length != 1)
            {
                I.Print(1, "newInputManager();");
                return "False";
            }
            if (created)
                return "False";
            created = true;
            return "True";
        });
        Cmd("listInputDevices", a =>
        {
            foreach (string d in kDevices)
                I.Print(1, d + (mInactive.Contains(d) ? "" : " (active)") + (d.StartsWith("keyboard") ? ": SDL3 keyboard, 111 keys" : ": SDL3 mouse, 3 buttons, 3 axes"));
            return "";
        });
        Cmd("getInputDeviceInfo", a =>
        {
            if (a.Length != 2 || !kDevices.Contains(a[1], StringComparer.OrdinalIgnoreCase))
            {
                I.Print(1, "getInputDeviceInfo: no such device " + (a.Length > 1 ? a[1] : ""));
                return "False";
            }
            DeviceInfo(a[1], line => I.Print(1, line));
            return "";
        });
        Cmd("saveInputDeviceInfo", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "saveInputDeviceInfo(file);");
                return "False";
            }
            if (!Builtins.IsWriteablePath(a[1]))
            {
                I.Print(1, "Error creating file '" + a[1] + "'");
                return "False";
            }
            var lines = new List<string>();
            foreach (string d in kDevices)
                DeviceInfo(d, lines.Add);
            File.WriteAllLines(Builtins.HostFilePath(mApp.Opt.Root, a[1]), lines);
            return "";
        });
        Cmd("inputOpen", a => { mInputOpen = true; return "True"; });
        Cmd("inputClose", a => { mInputOpen = false; return ""; });
        Cmd("inputCapture", a => { mApp.GrabMouse(true); return ""; });
        Cmd("inputRelease", a => { mApp.GrabMouse(false); return ""; });
        Cmd("inputActivate", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "inputActivate(deviceName | all);");
                return "False";
            }
            if (string.Equals(a[1], "all", StringComparison.OrdinalIgnoreCase))
            {
                mInactive.Clear();
                return "True";
            }
            if (!kDevices.Contains(a[1], StringComparer.OrdinalIgnoreCase))
            {
                I.Print(1, "inputActivate: unknown device " + a[1]);
                return "False";
            }
            mInactive.Remove(a[1]);
            return "True";
        });
        Cmd("inputDeactivate", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "inputDeactivate(deviceName | all);");
                return "False";
            }
            if (string.Equals(a[1], "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string d in kDevices)
                    mInactive.Add(d);
                mApp.Input.Clear();
                return "True";
            }
            if (!kDevices.Contains(a[1], StringComparer.OrdinalIgnoreCase))
            {
                I.Print(1, "inputDeactivate: unknown device " + a[1]);
                return "False";
            }
            mInactive.Add(a[1]);
            return "True";
        });
        // Test aid: the state of the input path (docs/client-scripts.md 3.6 to 3.8).
        Cmd("inputStatus", a =>
        {
            I.Print(1, $"input: maps pushed {mApp.Input.StackDepth}, held keys {mApp.Input.HeldCount}, modifiers {mApp.Input.Modifiers}, " +
                       $"content {mApp.Canvas.Content?.Id}, cursor {(mApp.Canvas.CursorOn ? "on" : "off")}, pushes {mApp.Play.MapPushes}, pops {mApp.Play.MapPops}");
            var m = mApp.Play.Moves;
            I.Print(1, $"move: fwd {m.Forward} back {m.Backward} left {m.Left} right {m.Right} jet {m.JetOn} turn {m.TurnAcc} fire {m.FireCounter}");
            return "";
        });
        Cmd("pushActionMap", a =>
        {
            if (a.Length != 2)
                return "False";
            ActionMap? m = mApp.Maps.FindMap(ClientCommands.ActionMapName(a[1]));
            if (m != null)
                mApp.Input.Push(m, mApp.ConsoleConsumer);
            return "True";
        });
        Cmd("popActionMap", a =>
        {
            if (a.Length != 2)
                return "False";
            ActionMap? m = mApp.Maps.FindMap(ClientCommands.ActionMapName(a[1]));
            if (m != null)
                mApp.Input.Pop(m, mApp.ConsoleConsumer);
            return "True";
        });
        // 2.7: the cursor and operating system pointer switches.
        Cmd("windowsMouseEnable", a => { if (a.Length == 2 && IsCanvas(a[0], a[1])) mApp.WindowsMouse = true; return ""; });
        Cmd("windowsMouseDisable", a => { if (a.Length == 2 && IsCanvas(a[0], a[1])) mApp.WindowsMouse = false; return ""; });
        Cmd("windowsKeyboardEnable", a => { if (a.Length == 2 && IsCanvas(a[0], a[1])) mApp.WindowsKeyboard = true; return ""; });
        Cmd("windowsKeyboardDisable", a => { if (a.Length == 2 && IsCanvas(a[0], a[1])) mApp.WindowsKeyboard = false; return ""; });
        _ = mInputOpen;
    }

    private static void DeviceInfo(string device, Action<string> line)
    {
        line("Type\tName");
        if (device.StartsWith("keyboard", StringComparison.OrdinalIgnoreCase))
        {
            foreach (ClientCommands.KeyName k in ClientKeyTable.Keys)
                line("Key\t" + (k.Name ?? k.Lower.ToString()));
        }
        else
        {
            line("Axis\txaxis0");
            line("Axis\tyaxis0");
            line("Axis\tzaxis0");
            for (int b = 0; b < 3; ++b)
                line("Button\tbutton" + b);
        }
    }

    // ---- 2.5 CD audio (D36) ----

    private void RegisterRedbook()
    {
        // No CD layer: newRedbook fails like a machine without a drive, the rest finds no object (the stock start-up
        // tolerates both: `unable to find Redbook Obj CD`).
        Cmd("newRedbook", a =>
        {
            if (a.Length != 3)
            {
                I.Print(1, "newRedbook <obj Id> <canvas Id>");
                return "False";
            }
            if (!IsCanvas(a[0], a[2]))
                return "False";
            // The install's console.log of a machine with no drive shows no line here, only the later
            // `unable to find Redbook Obj CD` (client-scripts.md 2.5 says `Err, could not open a device.`: spec error E1).
            return "False";
        });
        foreach (string n in new[] { "rbOpen", "rbClose", "rbEject", "rbRetract", "rbGetStatus", "rbGetTrackCount", "rbGetTrackInfo",
                                     "rbGetTrackPosition", "rbPlay", "rbStop", "rbPause", "rbResume", "rbSetVolume", "rbGetVolume", "rbSetPlayMode" })
            Cmd(n, a =>
            {
                if (a.Length < 2)
                {
                    I.Print(1, n + " <obj Id>");
                    return "False";
                }
                I.Print(1, "unable to find Redbook Obj " + a[1]);
                return "False";
            });
    }

    // ---- 2.4 net ----

    private void RegisterNet()
    {
        Cmd("netStats", a =>
        {
            if (mApp.Game?.Net is { } net)
                I.Print(1, $"Net: packets {net.World.Packets}, events {net.World.Events}, ghost updates {net.World.GhostUpdates}, unknown bits {net.World.UnknownBits}");
            return "";
        });
        Cmd("logPacketStats", a => "");
        Cmd("net::kick", a =>
        {
            if (a.Length is < 2 or > 3)
            {
                I.Print(1, "kick(playerId, <reason>);");
                return "False";
            }
            I.Print(1, "no server to kick player from");
            return "False";
        });
        Cmd("connect", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "Connect: <net_address>");
                return "False";
            }
            // A +connect start-up never passes the play-mode radio of the menu, so the mode is empty and the stock onConnection would
            // skip gui\Loading.gui (and its progress bar). The command line join is a JOIN.
            if (I.PeekVariable("$connectAddress").Length > 0 && I.PeekVariable("$pref::PlayGameMode").Length == 0)
                I.SetVariable("$pref::PlayGameMode", "JOIN");
            return mApp.Connect(a[1]) ? "True" : "False";
        });
        Cmd("disconnect", a =>
        {
            mApp.Disconnect();
            return "";
        });
        Cmd("playDemo", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "PlayDemo: file");
                return "False";
            }
            return mApp.PlayDemo(a[1]) ? "True" : "False";
        });
        Cmd("timeDemo", a =>
        {
            if (a.Length != 2)
            {
                I.Print(1, "TimeDemo: file");
                return "False";
            }
            return mApp.PlayDemo(a[1]) ? "True" : "False";
        });
        // R6: the master list is local only. Names resolve if they are numeric loopback addresses; every other master stays
        // unresolved and the script goes on with an empty list.
        Cmd("DNet::resolveMasters", a =>
        {
            int n = 0;
            for (int k = 1; k < a.Length; ++k)
                if (mApp.IsLocalAddress(a[k]))
                    ++n;
            return n.ToString(CultureInfo.InvariantCulture);
        });
        Cmd("DNet::getResolvedMaster", a => "");
        Cmd("DNet::TranslateAddress", a => a.Length >= 2 && mApp.IsLocalAddress(a[1]) ? a[1] : "");
        Cmd("remoteEval", a =>
        {
            // remoteEval(manager, function, args...): the wire call is the function name and its arguments.
            if (a.Length < 3)
            {
                I.Print(1, "remoteEval(client/manager, function, args...);");
                return "False";
            }
            return mApp.Game?.PostRemoteEval(a[2..]) == true ? "True" : "False";
        });
        Cmd("resetSimTime", a => "");
        // The client keeps no ban list; onExit() still writes the file (client-scripts.md 5.4).
        Cmd("BanList::export", a =>
        {
            if (a.Length != 2)
                return "false";
            string path = Builtins.ResolveGamePath(mApp.Opt.Root, a[1]);
            if (path.Length > 0 && Builtins.IsWriteablePath(a[1]))
            {
                try
                {
                    File.WriteAllBytes(path, Array.Empty<byte>());
                }
                catch (IOException)
                {
                }
            }
            return "true";
        });
    }

    // ---- 2.13 game plugin commands with a client effect ----

    private void RegisterGame()
    {
        Cmd("resetPlayDelegate", a =>
        {
            mApp.Play.Reset();
            return "";
        });
        Cmd("rebuildCommandMap", a => "");
        Cmd("Client::exitLobbyMode", a =>
        {
            mApp.Interp.SetVariable("$InLobbyMode", "False");
            mApp.Interp.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\play.gui\");");
            return "TRUE";
        });
        Cmd("GUI::isIn8BitMode", a => "false");
        Cmd("GUI::issue8BitWarning", a => "");
        Cmd("GUI::issueInternetWarning", a => "");
    }

    // ---- 2.11 and the process ----

    private void RegisterMisc()
    {
        Cmd("quit", a =>
        {
            mApp.RequestQuit();
            return "";
        });
        Cmd("Console::enable", a =>
        {
            if (a.Length == 2 && (IsTrue(a[1]) || string.Equals(a[1], "false", StringComparison.OrdinalIgnoreCase)))
                return "True";
            I.Print(1, "Console::enable(<true/false>);");
            return "False";
        });
    }
}
