using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeffCAD.AiAssistant
{
    /// <summary>One remembered translation.</summary>
    internal sealed class CacheEntry
    {
        /// <summary>The source string exactly as it appeared in the drawing.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }

        /// <summary>The translation the user accepted (machine output or hand-edited).</summary>
        [JsonPropertyName("target")]
        public string Target { get; set; }

        /// <summary>
        /// BCP-47-ish target language tag, e.g. <c>en</c> or <c>ko</c>. Part of the lookup
        /// key: "齿轮" translated to English and to Korean are different answers, and
        /// serving one for the other would be a silent, hard-to-spot defect.
        /// </summary>
        [JsonPropertyName("lang")]
        public string Language { get; set; }

        /// <summary>
        /// True when a human edited this text. Verified entries always outrank machine
        /// output and are never overwritten by a later automatic run.
        /// </summary>
        [JsonPropertyName("verified")]
        public bool Verified { get; set; }

        /// <summary>How many times this entry has been served, used to rank eviction.</summary>
        [JsonPropertyName("hits")]
        public int Hits { get; set; }

        [JsonPropertyName("created")]
        public DateTime CreatedUtc { get; set; }

        [JsonPropertyName("used")]
        public DateTime LastUsedUtc { get; set; }
    }

    /// <summary>
    /// User-level translation memory that survives across sessions and drawings.
    /// </summary>
    /// <remarks>
    /// Stored at <c>%LOCALAPPDATA%\JeffCAD.AiAssistant\translation-cache.json</c> rather
    /// than next to the assembly: the plugin directory is often read-only, and the whole
    /// point of this cache is that a term translated once in one drawing is reused in the
    /// next.
    ///
    /// Design constraints worth stating explicitly:
    ///   - The key is (language, source). Language must be part of it, otherwise asking
    ///     for Korean after having translated to English would silently return English.
    ///   - User-edited entries are marked verified and win over machine output. Without
    ///     this, a hand-corrected term would be clobbered by the next automatic run --
    ///     the single most frustrating possible behaviour for the user.
    ///   - Every file operation is defensive: a corrupt or locked cache degrades to an
    ///     empty cache rather than taking the plugin down.
    /// </remarks>
    internal static class TranslationCache
    {
        private const int MaxEntries = 20000;

        private static readonly object Gate = new object();

        private static Dictionary<string, CacheEntry> _entries;
        private static bool _loaded;
        private static bool _dirty;

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>Full path of the cache file. Never null in practice.</summary>
        public static string CacheFilePath
        {
            get
            {
                // LocalApplicationData (not ApplicationData): a translation memory is
                // machine-local and can grow to thousands of entries, so there is no
                // reason to drag it across a roaming profile on every logon.
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = Path.GetTempPath();
                }

                return Path.Combine(root, "JeffCAD.AiAssistant", "translation-cache.json");
            }
        }

        /// <summary>Builds the dictionary key. Language is case-insensitive; source is exact.</summary>
        private static string KeyFor(string language, string source)
        {
            return (language ?? string.Empty).Trim().ToLowerInvariant() + "\u001f" + (source ?? string.Empty);
        }

        private static Dictionary<string, CacheEntry> Entries
        {
            get
            {
                lock (Gate)
                {
                    if (!_loaded)
                    {
                        _entries = LoadFromDisk();
                        _loaded = true;
                    }

                    return _entries;
                }
            }
        }

        private static Dictionary<string, CacheEntry> LoadFromDisk()
        {
            var result = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            try
            {
                var path = CacheFilePath;
                if (!File.Exists(path))
                {
                    return result;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return result;
                }

                var list = JsonSerializer.Deserialize<List<CacheEntry>>(json, ReadOptions);
                if (list == null)
                {
                    return result;
                }

                foreach (var entry in list)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Source))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(entry.Language))
                    {
                        // Written by an older build that had no language dimension.
                        // Assume English rather than discarding otherwise-good data.
                        entry.Language = TargetLanguages.DefaultCode;
                    }

                    result[KeyFor(entry.Language, entry.Source)] = entry;
                }
            }
            catch (Exception ex)
            {
                AppSettings.Diagnostics.Log($"translation cache: unreadable, starting empty ({ex.GetType().Name}: {ex.Message})");
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            }

            return result;
        }

        /// <summary>Looks up a remembered translation. Returns null when unknown.</summary>
        public static CacheEntry Lookup(string language, string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            var key = KeyFor(language, source);
            var entries = Entries;
            lock (Gate)
            {
                return entries.TryGetValue(key, out var hit) ? hit : null;
            }
        }

        /// <summary>
        /// Records a translation. A verified entry is never downgraded by machine output.
        /// </summary>
        /// <returns>true when the stored value actually changed.</returns>
        public static bool Remember(
            string language,
            string source,
            string target,
            bool verified)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                // Nothing was translated; caching this would only add noise.
                return false;
            }

            var entries = Entries;
            var key = KeyFor(language, source);
            var now = DateTime.UtcNow;

            lock (Gate)
            {
                if (entries.TryGetValue(key, out var existing))
                {
                    // A verified (human-corrected) entry outranks fresh machine output.
                    // The one exception is a new verified edit, which always wins.
                    if (existing.Verified && !verified)
                    {
                        existing.Hits++;
                        existing.LastUsedUtc = now;
                        _dirty = true;
                        return false;
                    }

                    var changed = !string.Equals(existing.Target, target, StringComparison.Ordinal)
                                  || existing.Verified != verified;
                    existing.Target = target;
                    existing.Verified = verified;
                    existing.Hits++;
                    existing.LastUsedUtc = now;
                    if (changed)
                    {
                        _dirty = true;
                    }

                    return changed;
                }

                entries[key] = new CacheEntry
                {
                    Source = source,
                    Target = target,
                    Language = language,
                    Verified = verified,
                    Hits = 1,
                    CreatedUtc = now,
                    LastUsedUtc = now
                };

                _dirty = true;
                return true;
            }
        }

        /// <summary>Bumps usage counters after a batch of lookups resolved from cache.</summary>
        public static void Touch(string language, IEnumerable<string> sources)
        {
            if (sources == null)
            {
                return;
            }

            var entries = Entries;
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                foreach (var source in sources)
                {
                    if (entries.TryGetValue(KeyFor(language, source), out var entry))
                    {
                        entry.Hits++;
                        entry.LastUsedUtc = now;
                        _dirty = true;
                    }
                }
            }
        }

        /// <summary>Removes one entry so a bad translation can be retranslated.</summary>
        public static bool Forget(string language, string source)
        {
            var entries = Entries;
            lock (Gate)
            {
                var removed = entries.Remove(KeyFor(language, source));
                if (removed)
                {
                    _dirty = true;
                }

                return removed;
            }
        }

        public static int Count
        {
            get
            {
                var entries = Entries;
                lock (Gate)
                {
                    return entries.Count;
                }
            }
        }

        public static int VerifiedCount
        {
            get
            {
                var entries = Entries;
                lock (Gate)
                {
                    return entries.Count(e => e.Value.Verified);
                }
            }
        }

        /// <summary>Drops every entry and deletes the file.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
                _loaded = true;
                _dirty = false;
            }

            try
            {
                var path = CacheFilePath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                AppSettings.Diagnostics.Log($"translation cache: clear failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// Writes the cache to disk, evicting the least valuable entries first when over
        /// the limit. Never throws: losing a cache write is not worth failing a translation.
        /// </summary>
        /// <returns>true when the file was written.</returns>
        public static bool Flush()
        {
            List<CacheEntry> snapshot;
            lock (Gate)
            {
                if (!_loaded || !_dirty)
                {
                    return false;
                }

                if (_entries.Count > MaxEntries)
                {
                    // Keep verified entries and the most frequently used ones; drop the
                    // rest. Verified work is the expensive, human-supplied part.
                    var keep = _entries.Values
                        .OrderByDescending(e => e.Verified)
                        .ThenByDescending(e => e.Hits)
                        .ThenByDescending(e => e.LastUsedUtc)
                        .Take(MaxEntries)
                        .ToList();

                    _entries = keep.ToDictionary(
                        e => KeyFor(e.Language, e.Source), e => e, StringComparer.Ordinal);
                }

                snapshot = _entries.Values.ToList();
                _dirty = false;
            }

            try
            {
                var path = CacheFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Write to a sibling then move, so a crash mid-write cannot leave a
                // half-serialized cache that the next session refuses to load.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, WriteOptions), Encoding.UTF8);
                File.Copy(temp, path, overwrite: true);
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // A leftover .tmp is harmless.
                }

                return true;
            }
            catch (Exception ex)
            {
                AppSettings.Diagnostics.Log($"translation cache: flush failed ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        /// <summary>Discards the in-memory snapshot so the next read re-reads the file.</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                _loaded = false;
                _dirty = false;
                _entries = null;
            }
        }
    }
}
