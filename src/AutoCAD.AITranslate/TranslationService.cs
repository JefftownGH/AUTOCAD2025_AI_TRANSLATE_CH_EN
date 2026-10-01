using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCAD.AITranslate
{
    /// <summary>Aggregate outcome of a translation run.</summary>
    /// <remarks>
    /// Counting members are incremented with Interlocked and the error list is guarded by
    /// a lock, because the batch loop runs on multiple threads concurrently.
    /// </remarks>
    internal sealed class TranslationStats
    {
        private readonly object _errorLock = new object();
        private int _requested;
        private int _succeeded;
        private int _skippedUnchanged;
        private int _failed;
        private int _cacheHits;
        private int _requestsSent;

        public int Requested => Volatile.Read(ref _requested);
        public int Succeeded => Volatile.Read(ref _succeeded);
        public int SkippedUnchanged => Volatile.Read(ref _skippedUnchanged);
        public int Failed => Volatile.Read(ref _failed);
        public int CacheHits => Volatile.Read(ref _cacheHits);
        public int RequestsSent => Volatile.Read(ref _requestsSent);

        public List<string> Errors { get; } = new List<string>();

        public void SetRequested(int value) => Interlocked.Exchange(ref _requested, value);

        public void SetCacheHits(int value) => Interlocked.Exchange(ref _cacheHits, value);

        public void IncrementSucceeded() => Interlocked.Increment(ref _succeeded);

        public void IncrementSkipped() => Interlocked.Increment(ref _skippedUnchanged);

        public void IncrementFailed() => Interlocked.Increment(ref _failed);

        public void IncrementRequestsSent() => Interlocked.Increment(ref _requestsSent);

        public void RecordError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lock (_errorLock)
            {
                if (Errors.Count < 20)
                {
                    Errors.Add(message);
                }
            }
        }
    }

    internal sealed class TranslationOutcome
    {
        public TranslationOutcome(string original, string translated, bool fromCache)
        {
            Original = original;
            Translated = translated;
            FromCache = fromCache;
        }

        public string Original { get; }
        public string Translated { get; }
        public bool FromCache { get; }
    }

    internal sealed class TranslationService
    {
        /// <summary>
        /// Cache of original -> translated. Static so that repeated invocations of the
        /// command within one AutoCAD session actually benefit from it; the previous
        /// per-instance cache was rebuilt on every command and therefore never hit.
        /// </summary>
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly object CacheLock = new object();

        private const int MaxCacheEntries = 20000;

        private readonly OpenAiClient _client;

        private TranslationService(OpenAiClient client)
        {
            _client = client;
        }

        public bool IsConfigured => _client.IsConfigured;

        public string Model => _client.Model;

        public string BaseUrl => _client.BaseUrl;

        /// <summary>The language this service translates into.</summary>
        public TargetLanguage TargetLanguage => _client.TargetLanguage;

        /// <summary>
        /// Points this service at a different target language, reusing the same client so
        /// the resolved endpoint and any warmed connection survive the change.
        /// </summary>
        public void SetTargetLanguage(TargetLanguage language)
        {
            _client.SetTargetLanguage(language);
        }

        /// <summary>
        /// Clears both the in-memory memo and the on-disk translation memory. The disk
        /// cache is the one that actually persists across sessions, so clearing only the
        /// former would leave the user's next run silently reading the old answers.
        /// </summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }

            TranslationCache.Clear();
        }

        public static int CacheCount
        {
            get
            {
                lock (CacheLock)
                {
                    return Cache.Count;
                }
            }
        }

        /// <summary>Entries held in the persistent, cross-session translation memory.</summary>
        public static int PersistentCacheCount => TranslationCache.Count;

        /// <summary>Of those, how many were hand-corrected by a user.</summary>
        public static int VerifiedCacheCount => TranslationCache.VerifiedCount;

        /// <summary>Flushes the persistent cache. Safe to call at any time.</summary>
        public static bool FlushCache() => TranslationCache.Flush();

        public static TranslationService CreateFromEnvironment()
        {
            var apiKey = Settings.Read("OPENAI_API_KEY");
            var model = Settings.Read("OPENAI_MODEL");
            var baseUrl = Settings.Read("OPENAI_BASE_URL");
            var organization = Settings.Read("OPENAI_ORG");
            var project = Settings.Read("OPENAI_PROJECT");
            var apiType = Settings.Read("OPENAI_API_TYPE");
            var systemPrompt = Settings.Read("OPENAI_SYSTEM_PROMPT");
            var timeoutMs = Settings.ReadInt("OPENAI_TIMEOUT_MS");

            var client = new OpenAiClient(
                apiKey, model, baseUrl, organization, project, apiType, systemPrompt, timeoutMs);
            return new TranslationService(client);
        }

        /// <summary>
        /// Resolves translations for a list of distinct inputs, consulting the persistent
        /// translation memory first and de-duplicating identical strings so that a drawing
        /// with the same label repeated 50 times costs exactly one API call.
        /// </summary>
        /// <remarks>
        /// Two caches are consulted, in order of authority:
        ///   1. the on-disk translation memory, keyed by (language, source) -- survives
        ///      sessions and drawings, and holds hand-corrected entries that always win;
        ///   2. the in-session memo, which is only a fast path to the same answer.
        /// Anything still unresolved goes to the model, and whatever comes back is written
        /// to both caches so the next run is cheaper.
        /// </remarks>
        public Dictionary<string, TranslationOutcome> ResolveMany(
            IReadOnlyList<string> originals,
            int batchSize,
            int maxParallel,
            Action<int, int> onProgress,
            TranslationStats stats)
        {
            stats.SetRequested(originals.Count);

            var language = _client.TargetLanguage ?? TargetLanguages.ByCode(TargetLanguages.DefaultCode);

            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var original in originals)
            {
                if (seen.Add(original))
                {
                    unique.Add(original);
                }
            }

            var cached = new Dictionary<string, string>(StringComparer.Ordinal);

            // The on-disk memory is authoritative. A user-corrected entry must be served
            // ahead of anything the in-session memo happens to hold, otherwise a
            // correction made in this run would be undone by a stale memo value.
            foreach (var text in unique)
            {
                var persistent = TranslationCache.Lookup(language.Code, text);
                if (persistent != null && !string.IsNullOrWhiteSpace(persistent.Target))
                {
                    cached[text] = persistent.Target;
                    continue;
                }

                lock (CacheLock)
                {
                    if (Cache.TryGetValue(text, out var hit))
                    {
                        cached[text] = hit;
                    }
                }
            }

            stats.SetCacheHits(cached.Count);

            var pending = unique.Where(t => !cached.ContainsKey(t)).ToList();
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in cached)
            {
                resolved[pair.Key] = pair.Value;
            }

            if (pending.Count > 0)
            {
                var effectiveBatchSize = Math.Max(1, batchSize);
                var chunks = Chunk(pending, effectiveBatchSize);
                var resolvedLock = new object();
                var progressLock = new object();
                var completedCount = 0;
                var totalUnits = chunks.Count;

                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, maxParallel)
                };

                Parallel.ForEach(chunks, options, chunk =>
                {
                    var results = TranslateChunk(chunk, stats);

                    int snapshot;
                    lock (resolvedLock)
                    {
                        foreach (var pair in results)
                        {
                            resolved[pair.Key] = pair.Value;
                        }

                        snapshot = ++completedCount;
                    }

                    lock (progressLock)
                    {
                        onProgress?.Invoke(snapshot, totalUnits);
                    }
                });
            }

            // Populate both caches with everything we learned this run. Machine output is
            // recorded as unverified, so it can never displace a hand-corrected entry.
            lock (CacheLock)
            {
                if (Cache.Count > MaxCacheEntries)
                {
                    Cache.Clear();
                }

                foreach (var pair in resolved)
                {
                    Cache[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in resolved)
            {
                TranslationCache.Remember(language.Code, pair.Key, pair.Value, verified: false);
            }

            // Count the cache reads for ranking; a term that keeps coming back should be
            // the last thing evicted.
            TranslationCache.Touch(language.Code, cached.Keys);

            var outcome = new Dictionary<string, TranslationOutcome>(StringComparer.Ordinal);
            foreach (var original in originals)
            {
                if (resolved.TryGetValue(original, out var translated) &&
                    !string.Equals(translated, original, StringComparison.Ordinal))
                {
                    outcome[original] = new TranslationOutcome(original, translated, cached.ContainsKey(original));
                    stats.IncrementSucceeded();
                }
                else
                {
                    stats.IncrementSkipped();
                }
            }

            return outcome;
        }

        /// <summary>
        /// Records the user's accepted or corrected translations into the persistent
        /// memory, marking hand-edited rows as verified.
        /// </summary>
        /// <remarks>
        /// Called after the review dialog is accepted and before anything is written to
        /// the drawing. Doing it here means a correction is remembered even if the
        /// subsequent write is rolled back -- the text the user typed is knowledge about
        /// the term, independent of whether this particular drawing received it.
        /// </remarks>
        public static void CommitReviewed(
            string languageCode,
            IEnumerable<KeyValuePair<string, string>> accepted,
            bool verified)
        {
            if (accepted == null)
            {
                return;
            }

            var language = TargetLanguages.ByCode(languageCode);
            foreach (var pair in accepted)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                TranslationCache.Remember(language.Code, pair.Key, pair.Value, verified);
            }
        }

        /// <summary>
        /// Translates one chunk. Tries the batched (single request) path first and, if the
        /// model's reply cannot be parsed as a well-formed array, degrades gracefully to
        /// per-item requests so that a malformed response never silently drops text.
        /// </summary>
        private Dictionary<string, string> TranslateChunk(List<string> chunk, TranslationStats stats)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            if (chunk.Count == 1)
            {
                TranslateOneWithFallback(chunk[0], result, stats);
                return result;
            }

            try
            {
                var inputs = chunk.ToArray();
                var outputs = _client.TranslateBatch(inputs);
                stats.IncrementRequestsSent();
                if (outputs != null && outputs.Length == inputs.Length)
                {
                    for (var i = 0; i < inputs.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(outputs[i]))
                        {
                            result[inputs[i]] = outputs[i];
                        }
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                stats.RecordError($"Batch failed, falling back to per-item: {ex.Message}");
            }

            foreach (var item in chunk)
            {
                TranslateOneWithFallback(item, result, stats);
            }

            return result;
        }

        private void TranslateOneWithFallback(string text, Dictionary<string, string> sink, TranslationStats stats)
        {
            try
            {
                var translated = _client.TranslateSingle(text);
                stats.IncrementRequestsSent();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    sink[text] = translated;
                }
            }
            catch (Exception ex)
            {
                stats.IncrementFailed();
                stats.RecordError(ex.Message);
            }
        }

        private static List<List<string>> Chunk(List<string> source, int size)
        {
            var chunks = new List<List<string>>();
            for (var i = 0; i < source.Count; i += size)
            {
                chunks.Add(source.GetRange(i, Math.Min(size, source.Count - i)));
            }

            return chunks;
        }

        /// <summary>
        /// Builds the prompt for a single (non-batched) translation.
        /// </summary>
        public static string BuildSinglePrompt(string original)
        {
            return
                "Translate the following Chinese text in an AutoCAD drawing to natural, professional English.\n" +
                "Preserve numbers, units, punctuation and line breaks.\n" +
                "Preserve AutoCAD MText formatting codes such as \\\\P, \\\\L, \\\\l, \\\\O, \\\\o, \\\\S, \\\\A without changing them.\n" +
                "If the text is already English or contains no Chinese, return it unchanged.\n" +
                "Return only the translated text with no extra commentary.\n\n" +
                "Text:\n" + original;
        }
    }

    /// <summary>
    /// Configuration reader. Environment variables take precedence over the JSON file
    /// sitting next to the assembly, which mirrors how the plugin is normally deployed.
    /// Parse failures are surfaced in the settings file but never crash the plugin.
    /// </summary>
    internal static class Settings
    {
        // Reloadable rather than Lazy<T>: saving new values from the settings dialog
        // must invalidate the snapshot so the next command run picks them up.
        private static Dictionary<string, string> _local;
        private static bool _loaded;

        private static Dictionary<string, string> LocalSettings
        {
            get
            {
                if (!_loaded)
                {
                    _local = LoadLocalSettings();
                    _loaded = true;
                }

                return _local;
            }
        }

        /// <summary>Known configuration keys, in the order they are written to the file.</summary>
        public static readonly string[] KnownKeys =
        {
            "OPENAI_API_KEY",
            "OPENAI_MODEL",
            "OPENAI_BASE_URL",
            "OPENAI_API_TYPE",
            "OPENAI_SYSTEM_PROMPT",
            "OPENAI_TIMEOUT_MS",
            "OPENAI_ORG",
            "OPENAI_PROJECT",
            "OPENAI_TARGET_LANGUAGE"
        };

        public static string Read(string key)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            var file = LocalSettings;
            if (file.TryGetValue(key, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
            {
                return fromFile;
            }

            return null;
        }

        /// <summary>
        /// Reads a key without consulting environment variables and without the
        /// string-only restriction of <see cref="Read"/>. Used for structured values
        /// such as the preset array, which is stored as a JSON array and therefore
        /// is not a string in the settings file.
        /// </summary>
        /// <remarks>
        /// Environment variables are deliberately skipped: presets are a UI concern
        /// rather than a deployment knob, and there is no sensible way to express an
        /// array in an environment variable. Active connection settings continue to
        /// honour the environment through <see cref="Read"/>.
        /// </remarks>
        public static string ReadRaw(string key)
        {
            var file = LocalSettings;
            if (file.TryGetValue(key, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
            {
                return fromFile;
            }

            return null;
        }

        public static int? ReadInt(string key)
        {
            var value = Read(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return int.TryParse(value, out var number) && number > 0 ? number : (int?)null;
        }

        public static string SettingsFilePath
        {
            get
            {
                var location = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrWhiteSpace(location))
                {
                    return null;
                }

                var dir = Path.GetDirectoryName(location);
                return string.IsNullOrWhiteSpace(dir)
                    ? null
                    : Path.Combine(dir, "AutoCAD.AITranslate.settings.json");
            }
        }

        /// <summary>
        /// Writes the given values to the settings JSON file next to the assembly,
        /// preserving any unrelated keys already present. Clears the cached snapshot so
        /// the next Read() sees the new values.
        /// </summary>
        /// <returns>true when the file was written successfully.</returns>
        public static bool Write(IEnumerable<KeyValuePair<string, string>> values)
        {
            var path = SettingsFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(path))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in doc.RootElement.EnumerateObject())
                            {
                                merged[property.Name] = property.Value.ValueKind == JsonValueKind.String
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

                // Unchecked JSON escapes would turn Chinese system prompts into \uXXXX
                // noise; write them verbatim so the file stays human-editable.
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                // Values that are themselves JSON (the preset array, or a bare number)
                // must be emitted as JSON, not re-encoded as a quoted string. Detect
                // them and splice the raw text in, so the file stays structured and
                // PresetStore can deserialize it on the next load.
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
        /// (array, object or number). Returns false when the value should be written
        /// as an ordinary string, which is the case for every text setting.
        /// </summary>
        /// <remarks>
        /// Only <c>{</c>, <c>[</c> and a leading digit/sign are considered. A plain
        /// text setting such as a system prompt is left alone even if it happens to
        /// contain JSON punctuation, because it will not *start* with a structural
        /// character in any realistic case, and if it did the result is still a
        /// readable file rather than a silent data corruption.
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
                // A quoted JSON string is still just a string; re-encode it normally
                // so escaping stays consistent with the rest of the file.
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

        /// <summary>Forces the next Read() to reload the settings file from disk.</summary>
        public static void InvalidateCache()
        {
            _local = null;
            _loaded = false;
        }

        private static Dictionary<string, string> LoadLocalSettings()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = SettingsFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return values;
            }            try
            {
                var json = File.ReadAllText(path);
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
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values[property.Name] = value;
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        // Allows OPENAI_TIMEOUT_MS to be written as a bare number.
                        values[property.Name] = property.Value.GetRawText();
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        // The preset list is stored as a JSON array, so it is not a
                        // string. Keep the raw text so PresetStore can deserialize it.
                        values[property.Name] = property.Value.GetRawText();
                    }
                }
            }
            catch (JsonException)
            {
                // Deliberately swallowed: a malformed settings file must not prevent the
                // plugin from loading. The caller reports "not configured" instead.
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
