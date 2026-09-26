using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        public static void ClearCache()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }
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
        /// Resolves translations for a list of distinct inputs, using the cache where
        /// possible and de-duplicating identical strings so that a drawing with the same
        /// label repeated 50 times costs exactly one API call.
        /// </summary>
        public Dictionary<string, TranslationOutcome> ResolveMany(
            IReadOnlyList<string> originals,
            int batchSize,
            int maxParallel,
            Action<int, int> onProgress,
            TranslationStats stats)
        {
            stats.SetRequested(originals.Count);

            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cached = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var original in originals)
            {
                if (seen.Add(original))
                {
                    unique.Add(original);
                }
            }

            foreach (var text in unique)
            {
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

            // Populate the cache with everything we learned this run.
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
        private static readonly Lazy<Dictionary<string, string>> LocalSettings =
            new Lazy<Dictionary<string, string>>(LoadLocalSettings, true);

        public static string Read(string key)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            var file = LocalSettings.Value;
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

        private static Dictionary<string, string> LoadLocalSettings()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = SettingsFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return values;
            }

            try
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
