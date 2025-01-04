// Port of src/server/console_log.hpp and src/server/console_log.cpp: console log file (original 0x005f43f8 write,
// 0x005f4544 $Console::logMode setter). docs/server-ops.md 6.
using System.Globalization;
using OpenTribes.Core;

namespace OpenTribes.Server;

public sealed class ConsoleLog : IDisposable
{
    private readonly string mPath;
    private readonly Func<DateTime> mLocalTime;
    private int mMode;
    private bool mHeaderDue = true;
    private bool mLastHope;
    private FileStream? mFile;

    // src/server/console_log.cpp ConsoleLog::ConsoleLog: path is the log file ("console.log" in the working directory
    // on the original); localTime replaces the local clock (std::tm in the C++).
    public ConsoleLog(string path, Func<DateTime>? localTime = null)
    {
        mPath = path;
        mLocalTime = localTime ?? (() => DateTime.Now);
    }

    // src/server/console_log.cpp ConsoleLog::~ConsoleLog.
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

    // src/server/console_log.cpp ConsoleLog::setMode: $Console::logMode assignment, atoi of the text. 0 off, 1
    // open/append/close per line, 2 keep the file open. Any other non-zero value logs nothing but counts as "on".
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

    public int Mode() => mMode;

    // src/server/console_log.hpp setLastHope: LastHope 1.30 header format (docs/lasthope-1.30.md L15).
    public void SetLastHope(bool on) => mLastHope = on;

    // src/server/console_log.cpp ConsoleLog::write: one console line; "\r\n" is appended. The first line after the log
    // was switched on (and the first line of the process) is preceded by a date header.
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
                DateTime tm = mLocalTime();
                string header = mLastHope
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
