// Port of src/server/main.cpp (stage P1: usage only; later stages port the server start-up).
namespace OpenTribes.Server;

public static class Program
{
    // src/server/main.cpp usage.
    private static void Usage()
    {
        Console.Error.Write(
            "usage: tribes-server [--port N] [--hostname S] [--password S] [--max-players N]\n" +
            "                     [--info S] [--lasthope] [--console-log FILE] [--allow-masters]\n" +
            "                     [--root DIR [--report-unknown | --report-unknown-at-exit] [-- ARGS...]]\n" +
            "                     [--virtual-time[=MS]] [--frame-eval FILE]\n" +
            "  --root DIR          game directory (console.cs, base/); runs console.cs with ARGS as $cargv\n" +
            "  --virtual-time[=MS] test mode: the clock advances MS (default 1) per frame, no sleeping\n" +
            "  --frame-eval FILE   console lines, one per frame at the frame-end step; \"@wait MS\"\n" +
            "                      runs frames for MS of clock time; the server exits at the end\n");
    }

    public static int Main(string[] args)
    {
        Usage();
        return 2;
    }
}
