namespace OpenTribes.Server;

using System.Globalization;
using OpenTribes.Core;

/// <summary>The console log file (docs/server-ops.md 6).</summary>
/// <remarks>Port of src/server/console_log.cpp. The original write is at 005f43f8 and the <c>$Console::logMode</c> setter at 005f4544.</remarks>
public sealed class ConsoleLog : IDisposable
{
    private readonly string mPath;
    private readonly Func<DateTime> mLocalTime;
    private int mMode;
    private bool mHeaderDue = true;
    private bool mLastHope;
    private FileStream? mFile;

    /// <summary>Creates a log.</summary>
    /// <param name="path">The log file. The original uses <c>console.log</c> in the working directory.</param>
    /// <param name="localTime">Returns the local time. Defaults to the system clock (<c>std::tm</c> in the C++).</param>
    public ConsoleLog(string path, Func<DateTime>? localTime = null)
    {
        mPath = path;
        mLocalTime = localTime ?? (() => DateTime.Now);
    }

    /// <summary>Closes the file.</summary>
    public void Dispose()
    {
        mFile?.Dispose();
        mFile = null;
    }

    private FileStream? OpenAppend()
    {
        try
        {
            return new FileStream(mPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            return null; // fopen failed
        }
    }

    /// <summary>Sets the log mode from an assignment to <c>$Console::logMode</c> (atoi of the text).</summary>
    /// <param name="mode">0 is off, 1 opens, appends and closes per line, 2 keeps the file open. Any other non-zero value logs nothing but counts as on.</param>
    public void SetMode(int mode)
    {
        if (mode == mMode)
            return;
        if (mode != 0 && mMode == 0)
            mHeaderDue = true;
        if (mMode == 2)
        {
            mFile?.Dispose();
            mFile = null;
        }
        else if (mode == 2)
        {
            mFile = OpenAppend();
        }
        mMode = mode;
    }

    /// <summary>Gets the log mode.</summary>
    /// <returns>The mode.</returns>
    public int Mode() => mMode;

    /// <summary>Selects the LastHope 1.30 header format (docs/lasthope-1.30.md L15).</summary>
    /// <param name="on">True for the LastHope format.</param>
    public void SetLastHope(bool on) => mLastHope = on;

    /// <summary>Writes one console line, followed by <c>\r\n</c>. The first line after the log was switched on, and the first line of the process, is preceded by a date header.</summary>
    /// <param name="line">The line.</param>
    public void Write(string line)
    {
        if (mMode == 0)
            return;
        if (mMode == 1)
            mFile = OpenAppend();
        if (mFile != null)
        {
            if (mHeaderDue)
            {
                var tm = mLocalTime();
                var header = mLastHope
                    ? string.Format(CultureInfo.InvariantCulture, "--- {0}/{1}/{2} -- {3}:{4}:{5} ---\n\r",
                                    tm.Month, tm.Day, tm.Year, tm.Hour, tm.Minute, tm.Second)
                    : string.Format(CultureInfo.InvariantCulture, "-------------------------- {0}/{1}/{2} -- {3:D2}:{4:D2}:{5:D2} -----\r\n",
                                    tm.Month, tm.Day, tm.Year, tm.Hour, tm.Minute, tm.Second);
                mFile.Write(ByteString.ToBytes(header));
                mHeaderDue = false;
            }
            mFile.Write(ByteString.ToBytes(line));
            mFile.Write("\r\n"u8);
            if (mMode == 2)
                mFile.Flush();
        }
        if (mMode == 1 && mFile != null)
        {
            mFile.Dispose();
            mFile = null;
        }
    }
}
