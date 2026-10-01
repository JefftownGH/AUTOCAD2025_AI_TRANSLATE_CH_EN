using System;
using System.IO;

namespace LlmToolkit
{
    /// <summary>
    /// Where the toolkit writes diagnostic traces.
    /// </summary>
    /// <remarks>
    /// An interface rather than a static file writer, because a library has no business
    /// deciding where a *host application's* log lives. The original code wrote to a
    /// hardcoded <c>%TEMP%\AutoCAD.AITranslate.diag.log</c> from a static class, which
    /// meant the component could not be reused without dragging that path along, and
    /// could not be unit-tested without touching the real filesystem.
    /// </remarks>
    public interface ILlmDiagnostics
    {
        void Log(string message);
    }

    /// <summary>Discards everything. The default, so the library is silent unless asked.</summary>
    public sealed class NullDiagnostics : ILlmDiagnostics
    {
        public static readonly NullDiagnostics Instance = new NullDiagnostics();

        public void Log(string message)
        {
            // Intentionally empty.
        }
    }

    /// <summary>
    /// Appends to a file. Every failure is swallowed: diagnostics must never take the
    /// host application down, which is the one thing a log writer can be relied on to
    /// get wrong (full disk, read-only directory, file locked by a previous crash).
    /// </summary>
    public sealed class FileDiagnostics : ILlmDiagnostics
    {
        private readonly string _path;

        public FileDiagnostics(string path)
        {
            _path = path;
        }

        public string LogFilePath => _path;

        public void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    _path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // See class remarks.
            }
        }
    }

    /// <summary>
    /// Forwards to a delegate. Lets a host funnel toolkit traces into its own logging
    /// pipeline (WPF text box, ILogger, console) without implementing an interface.
    /// </summary>
    public sealed class DelegateDiagnostics : ILlmDiagnostics
    {
        private readonly Action<string> _sink;

        public DelegateDiagnostics(Action<string> sink)
        {
            _sink = sink;
        }

        public void Log(string message)
        {
            try
            {
                _sink?.Invoke(message);
            }
            catch
            {
                // See FileDiagnostics remarks.
            }
        }
    }
}
