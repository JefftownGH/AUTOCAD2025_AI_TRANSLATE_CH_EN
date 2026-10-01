using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmToolkit;

namespace JeffCAD.AiAssistant
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

        private readonly ChatClient _client;

        /// <summary>
        /// The language this service translates into. Held here rather than in the client
        /// because the client is now a general-purpose LLM transport that knows nothing
        /// about translation; the prompt it sends is composed by this class.
        /// </summary>
        private TargetLanguage _targetLanguage;

        private TranslationService(ChatClient client, TargetLanguage targetLanguage)
        {
            _client = client;
            _targetLanguage = targetLanguage ?? TargetLanguages.ByCode(TargetLanguages.DefaultCode);
        }

        public bool IsConfigured => _client.IsConfigured;

        public string Model => _client.Model;

        public string BaseUrl => _client.BaseUrl;

        /// <summary>The language this service translates into.</summary>
        public TargetLanguage TargetLanguage => _targetLanguage;

        /// <summary>
        /// Points this service at a different target language, reusing the same client so
        /// the resolved endpoint and any warmed connection survive the change. Only the
        /// composed system prompt changes; no reconnect or re-probe happens.
        /// </summary>
        public void SetTargetLanguage(TargetLanguage language)
        {
            _targetLanguage = language ?? TargetLanguages.ByCode(TargetLanguages.DefaultCode);
            _client.SetSystemPrompt(TargetLanguages.BuildSystemPrompt(_systemPrompt, _targetLanguage));
        }

        /// <summary>The user's own system prompt, before any language instruction is appended.</summary>
        private string _systemPrompt;

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
            // Load from the shared settings store, which also migrates any legacy
            // OPENAI_* keys onto their current names.
            var options = LlmOptions.FromSettings(AppSettings.Store);

            var language = TargetLanguages.ByCode(
                AppSettings.Store.Read(LlmSettingKeys.TargetLanguage));

            // The system prompt is composed here, not taken verbatim from settings, so a
            // stored prompt written for one language cannot contradict the picker.
            var systemPrompt = TargetLanguages.BuildSystemPrompt(options.SystemPrompt, language);
            options.SystemPrompt = systemPrompt;

            var client = new ChatClient(
                options,
                new EndpointProbeStore(AppSettings.Store),
                AppSettings.Diagnostics);

            return new TranslationService(client, language)
            {
                _systemPrompt = options.SystemPrompt
            };
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

            var language = _targetLanguage ?? TargetLanguages.ByCode(TargetLanguages.DefaultCode);

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
                var outputs = TranslateManyInOneRequest(inputs);
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
                var translated = _client.Send(BuildSinglePrompt(text));
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

        /// <summary>
        /// Sends a whole chunk in one request, asking for a JSON array back.
        /// </summary>
        /// <remarks>
        /// Uses the toolkit's <see cref="JsonArrayProtocol"/>, which returns null rather
        /// than a partial result when the reply is malformed or the wrong length. A
        /// length mismatch is the dangerous case: the model merged or dropped an entry,
        /// so index i no longer corresponds to input i, and applying it would write the
        /// wrong text onto the wrong drawing entity. Returning null here makes the caller
        /// fall back to per-item requests instead.
        /// </remarks>
        private string[] TranslateManyInOneRequest(string[] inputs)
        {
            var raw = _client.Send(BuildBatchPrompt(inputs), expectContent: false);
            return JsonArrayProtocol.TryParse(raw, inputs.Length);
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
        /// Builds the prompt for a batched translation.
        /// </summary>
        /// <remarks>
        /// The target language is a parameter, not a constant. The original prompt
        /// hardcoded the word "English", so asking for Korean silently produced English --
        /// a defect the caller could not detect from the response shape, because a
        /// well-formed English array came back either way.
        /// </remarks>
        public string BuildBatchPrompt(string[] inputs)
        {
            return JsonArrayProtocol.BuildBatchPrompt(
                inputs,
                $"Translate each element of the following JSON array into {_targetLanguage.PromptName}",
                AutoCADMTextTokens);
        }

        /// <summary>
        /// Builds the prompt for a single (non-batched) translation, used both for
        /// one-element chunks and as the fallback when a batch reply cannot be parsed.
        /// </summary>
        public string BuildSinglePrompt(string original)
        {
            return
                $"Translate the following text from a CAD drawing into {_targetLanguage.PromptName}.\n" +
                "Preserve numbers, units, punctuation and line breaks.\n" +
                "Preserve AutoCAD MText formatting codes such as \\\\P, \\\\L, \\\\l, \\\\O, \\\\o, \\\\S, \\\\A without changing them.\n" +
                $"If the text is already in {_targetLanguage.PromptName} or contains nothing translatable, return it unchanged.\n" +
                "Return only the translated text with no extra commentary.\n\n" +
                "Text://n" + original;
        }

        /// <summary>
        /// MText formatting codes that must survive translation untouched. Shared with the
        /// batch prompt so the two paths cannot drift apart.
        /// </summary>
        private static readonly string[] AutoCADMTextTokens =
        {
            "AutoCAD MText formatting codes such as \\\\P, \\\\L, \\\\l, \\\\O, \\\\o, \\\\S, \\\\A"
        };
    }
}
