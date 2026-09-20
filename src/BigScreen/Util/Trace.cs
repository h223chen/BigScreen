using System;
using System.IO;

namespace BigScreen.Util;

/// <summary>
/// A crash-proof breadcrumb trail.
///
/// BepInEx's log loses its last lines when the process dies mid-frame, even with
/// InstantFlushing on. Lines written here survive: each one opens the file, appends, and
/// closes, so the data is handed to the OS before the call returns and stays there when the
/// process is killed.
///
/// This is slow and is meant for tracking down a crash, not for normal logging. The file is
/// truncated on the first write of each run.
/// </summary>
internal static class Trace
{
    private static string _path;
    private static bool _started;

    private static string Path
    {
        get
        {
            if (_path == null)
            {
                try { _path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "BigScreen-trace.log"); }
                catch { _path = "BigScreen-trace.log"; }
            }
            return _path;
        }
    }

    public static void Write(string message)
    {
        try
        {
            if (!_started)
            {
                _started = true;
                File.WriteAllText(Path, $"--- BigScreen trace {DateTime.Now:HH:mm:ss} ---{Environment.NewLine}");
            }
            File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Never let tracing take the game down.
        }
    }
}
