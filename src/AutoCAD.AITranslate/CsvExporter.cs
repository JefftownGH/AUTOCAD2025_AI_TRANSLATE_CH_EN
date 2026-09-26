using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.EditorInput;

namespace AutoCAD.AITranslate
{
    internal static class CsvExporter
    {
        /// <summary>
        /// Prompts for a destination and writes the translation pairs.
        /// </summary>
        /// <remarks>
        /// Deliberately swallows its own errors: by the time this runs the translations
        /// have already been applied to the drawing, so a failed export must not surface
        /// as "translation failed", which is what used to happen when the write threw
        /// its way up to the command-level catch block.
        /// </remarks>
        public static bool TryExport(Editor editor, List<TranslationRecord> records)
        {
            if (editor == null || records == null || records.Count == 0)
            {
                return false;
            }

            var options = new PromptSaveFileOptions("\nExport translation CSV?")
            {
                Filter = "CSV (*.csv)|*.csv",
                DialogCaption = "Save Translation CSV",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                InitialFileName = $"translations-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };

            var result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return false;
            }

            var path = result.StringResult;
            try
            {
                WriteCsv(path, records);
                editor.WriteMessage($"\nCSV exported: {path}");
                return true;
            }
            catch (Exception ex)
            {
                editor.WriteMessage($"\nWarning: translation applied but CSV export failed: {ex.Message}");
                return false;
            }
        }

        private static void WriteCsv(string path, List<TranslationRecord> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Original,Translated");
            foreach (var record in records)
            {
                sb.Append(Escape(record.OriginalText));
                sb.Append(',');
                sb.AppendLine(Escape(record.TranslatedText));
            }

            // UTF-8 with a BOM so that Excel on Windows detects the encoding and renders
            // the Chinese characters correctly.
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            using var writer = new StreamWriter(path, append: false, encoding);
            writer.Write(sb.ToString());
        }

        /// <summary>
        /// RFC 4180 field escaping.
        /// </summary>
        /// <remarks>
        /// The previous version only escaped double quotes. MText content regularly
        /// contains real newlines once \P has been normalised, and an unescaped newline
        /// splits one record across two rows, corrupting the whole file. Line breaks are
        /// now escaped to the literal two-character sequences \n and \r so that every
        /// record stays on exactly one line.
        /// </remarks>
        internal static string Escape(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var escaped = value
                .Replace("\\", "\\\\")
                .Replace("\"", "\"\"")
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace("\r", "\\n");

            return $"\"{escaped}\"";
        }
    }
}
