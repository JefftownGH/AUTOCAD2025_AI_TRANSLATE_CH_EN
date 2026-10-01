using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace LlmToolkit
{
    /// <summary>
    /// Configuration reader/writer backed by a JSON file, with environment variables
    /// taking precedence over the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The precedence order mirrors how plugins and services are normally deployed: a
    /// developer can override a single value for one shell session without editing the
    /// file, while the file remains the durable store the UI writes to.
    /// </para>
    /// <para>
    /// This type is deliberately instance-based rather than static. The original
    /// implementation was a static class bound to one file name, which made it
    /// impossible to have two independent configurations in one process (for example a
    /// per-user and a per-machine file), and made it untestable against a temp path.
    /// </para>
    /// </remarks>
    public sealed class LlmSettingsStore
    {
        private readonly string _filePath;
        private readonly IReadOnlyList<string> _knownKeys;

        private Dictionary<string, string> _cache;
        private bool _loaded;

        /// <summary>
        /// Creates a store for a specific file.
        /// </summary>
        /// <param name="filePath">
        /// Full path to the JSON settings file. When the file does not exist, reads
        /// return null and <see cref="Write"/> creates it. Pass null to derive the path
        /// from the entry assembly's location -- see <see cref="DefaultPathForEntryAssembly"/>.
        /// </param>
        /// <param name="knownKeys">
        /// Keys that <see cref="KnownKeys"/> advertises. Purely for UI convenience;
        /// reading and writing work for any key.
        /// </param>
        public LlmSettingsStore(string filePath, IReadOnlyList<string> knownKeys = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
            _knownKeys = knownKeys ?? Array.Empty<string>();
        }

        /// <summary>
        /// The canonical "<c>&lt;assembly&gt;.settings.json</c> next to the entry
        /// assembly" location. Returns null when the location cannot be determined,
        /// which happens in some hosted environments.
        /// </summary>
        public static string DefaultPathForEntryAssembly(string baseName)
        {
            var location = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                // Fall back to the executing assembly: GetEntryAssembly() is null when
                // the host is unmanaged (which is exactly the AutoCAD case under
                // NETLOAD), and the calling assembly is then the right answer.
                location = Assembly.GetExecutingAssembly()?.Location;
            }

            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var dir = Path.GetDirectoryName(location);
            return string.IsNullOrWhiteSpace(dir)
                ? null
                : Path.Combine(dir, baseName);
        }

        /// <summary>Full path of the backing file, or null when none could be resolved.</summary>
        public string FilePath => _filePath;

        /// <summary>The keys this store advertises to a settings UI.</summary>
        public IReadOnlyList<string> KnownKeys => _knownKeys;

        /// <summary>
        /// Reads a value. Environment variables win over the file. Returns null when
        /// the key is absent or blank in both.
        /// </summary>
        public string Read(string key)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            return ReadRaw(key);
        }

        /// <summary>
        /// Reads a value from the file only, ignoring environment variables and the
        /// string-only restriction of <see cref="Read"/>. Used for structured values
        /// such as a JSON array, which is not a string in the file.
        /// </summary>
        /// <remarks>
        /// Environment variables are deliberately skipped here: a preset array is a UI
        /// concern rather than a deployment knob, and there is no sensible way to
        /// express an array in an environment variable. Active connection settings
        /// continue to honour the environment through <see cref="Read"/>.
        /// </remarks>
        public string ReadRaw(string key)
        {
            var file = Load();
            return file.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        /// <summary>Reads a positive integer, or null when absent/unparseable.</summary>
        public int? ReadInt(string key)
        {
            var value = Read(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return int.TryParse(value, out var number) && number > 0 ? number : (int?)null;
        }

        /// <summary>Reads a boolean. Accepts 1/0, true/false, yes/no.</summary>
        public bool? ReadBool(string key)
        {
            var value = Read(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var v = value.Trim();
            if (string.Equals(v, "1", StringComparison.Ordinal) ||
                string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(v, "0", StringComparison.Ordinal) ||
                string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }

        /// <summary>
        /// Writes the given values, preserving any unrelated keys already present.
        /// Values that are themselves JSON (an array, an object, or a bare number) are
        /// spliced in verbatim rather than being re-encoded as quoted strings.
        /// </summary>
        /// <returns>true when the file was written successfully.</returns>
        public bool Write(IEnumerable<KeyValuePair<string, string>> values)
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                return false;
            }

            try
            {
                var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(_filePath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in doc.RootElement.EnumerateObject())
                            {
                                merged[property.Name] =
                                    property.Value.ValueKind == JsonValueKind.String
                                        ? property.Value.GetString() ?? string.Empty
                                        : property.Value.GetRawText();
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Unreadable existing file: start over with a fresh document
                        // rather than blocking the user from saving a good one.
                        merged.Clear();
                    }
                }

                foreach (var pair in values)
                {
                    merged[pair.Key] = pair.Value ?? string.Empty;
                }

                // Unchecked JSON escapes would turn non-ASCII prompts into \uXXXX noise;
                // write them verbatim so the file stays human-editable.
                using (var stream = new FileStream(
                           _filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
                {
                    writer.WriteStartObject();
                    foreach (var pair in merged)
                    {
                        if (TryWriteRawJson(writer, pair.Key, pair.Value))
                        {
                            continue;
                        }

                        writer.WriteString(pair.Key, pair.Value ?? string.Empty);
                    }

                    writer.WriteEndObject();
                }

                InvalidateCache();
                return true;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes a value verbatim when it is already valid JSON of a non-string kind
        /// (array, object or number). Returns false when the value should be written as
        /// an ordinary string, which is the case for every text setting.
        /// </summary>
        /// <remarks>
        /// Only <c>{</c>, <c>[</c> and a leading digit/sign are considered. A plain text
        /// setting such as a system prompt is left alone even if it happens to contain
        /// JSON punctuation, because it will not *start* with a structural character in
        /// any realistic case, and if it did the result is still a readable file rather
        /// than silent data corruption.
        /// </remarks>
        private static bool TryWriteRawJson(Utf8JsonWriter writer, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var c = value.TrimStart()[0];
            var looksStructural = c == '{' || c == '[' || c == '-' || char.IsDigit(c);
            if (!looksStructural)
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(value);

                // A quoted JSON string is still just a string; re-encode it normally so
                // escaping stays consistent with the rest of the file.
                if (doc.RootElement.ValueKind == JsonValueKind.String)
                {
                    return false;
                }

                writer.WritePropertyName(key);
                doc.RootElement.WriteTo(writer);
                return true;
            }
            catch (JsonException)
            {
                // Not JSON after all - treat as text.
                return false;
            }
        }

        /// <summary>Forces the next read to reload the file from disk.</summary>
        public void InvalidateCache()
        {
            _cache = null;
            _loaded = false;
        }

        /// <summary>
        /// Returns every stored key/value pair. Used to copy settings between stores,
        /// for example when applying migrated legacy keys.
        /// </summary>
        public IReadOnlyDictionary<string, string> Snapshot()
        {
            return new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, string> Load()
        {
            if (_loaded)
            {
                return _cache;
            }

            _cache = LoadFromDisk();
            _loaded = true;
            return _cache;
        }

        private Dictionary<string, string> LoadFromDisk()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(_filePath) || !File.Exists(_filePath))
            {
                return values;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return values;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return values;
                }

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            var value = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                values[property.Name] = value;
                            }
                            break;

                        case JsonValueKind.Number:
                        case JsonValueKind.Array:
                        case JsonValueKind.Object:
                            // Numbers arrive as a bare number (a timeout expressed in
                            // milliseconds) and arrays as structured data (the preset
                            // list). Keep the raw text so the typed readers and the
                            // preset store can parse them.
                            values[property.Name] = property.Value.GetRawText();
                            break;
                    }
                }
            }
            catch (JsonException)
            {
                // Deliberately swallowed: a malformed settings file must not prevent the
                // host application from loading. Callers report "not configured" instead.
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return values;
        }
    }
}
