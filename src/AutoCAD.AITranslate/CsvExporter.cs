using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.EditorInput;

namespace AutoCAD.AITranslate
{
    internal static class CsvExporter
    {
        public static void Export(Editor editor, List<TranslationRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return;
            }

            var options = new PromptSaveFileOptions("\nExport translation CSV?")
            {
                Filter = "CSV (*.csv)|*.csv",
                DialogCaption = "Save Translation CSV",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                InitialFileName = "translations.csv"
            };

            var result = editor.GetFileNameForSave(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return;
            }

            var path = result.StringResult;
            WriteCsv(path, records);
            editor.WriteMessage($"\nCSV exported: {path}");
        }

        private static void WriteCsv(string path, List<TranslationRecord> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Original,Translated");
            foreach (var record in records)
            {
                sb.AppendLine($"{Escape(record.OriginalText)},{Escape(record.TranslatedText)}");
            }

            var bytes = Encoding.UTF8.GetPreamble();
            var content = Encoding.UTF8.GetBytes(sb.ToString());
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Write(content, 0, content.Length);
            }
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }
    }
}
