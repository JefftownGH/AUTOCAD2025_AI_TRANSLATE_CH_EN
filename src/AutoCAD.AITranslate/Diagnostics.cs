using System;
using System.IO;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// Tiny append-only diagnostic log in %TEMP%. The plugin's failure modes (ribbon
    /// button dead, command not registered) are invisible from a shell, so every
    /// interesting lifecycle event leaves a trace here.
    /// </summary>
    internal static class Diagnostics
    {
        public static string LogFilePath =>
            Path.Combine(Path.GetTempPath(), "AutoCAD.AITranslate.diag.log");

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    LogFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never take the plugin down.
            }
        }
    }
}
