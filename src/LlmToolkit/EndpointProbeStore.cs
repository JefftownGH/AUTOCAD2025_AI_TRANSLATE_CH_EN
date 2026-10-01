using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LlmToolkit
{
    /// <summary>
    /// Remembers which API endpoint family a given gateway actually serves, so the
    /// "auto" probe only ever runs once per base URL instead of once per request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: in auto mode the client tries <c>/responses</c> first and falls
    /// back to <c>/chat/completions</c> on 404. Most Chinese gateways (Zhipu/BigModel,
    /// DeepSeek, Moonshot, DashScope) implement only Chat Completions, so that first
    /// attempt is guaranteed to fail. When the probe result lived in an instance field
    /// it was useless in practice -- every "test connection" click builds a fresh
    /// client, so the cache was always cold and every click paid a doomed round trip.
    /// Worse, a transient transport failure during that doomed request surfaced to the
    /// user as "The SSL connection could not be established", which looks like a TLS
    /// problem and is not.
    /// </para>
    /// <para>
    /// The cache is keyed by base URL only (case-insensitive, trailing slash trimmed)
    /// and never by API key: two machines behind the same gateway implement the same
    /// routes, and a key rotation should not force a re-probe. A stale entry is
    /// self-correcting -- if the recorded endpoint starts 404ing, the client clears the
    /// entry and probes again.
    /// </para>
    /// <para>
    /// Unlike the original static implementation, this takes its
    /// <see cref="LlmSettingsStore"/> by constructor so it can be pointed at a test
    /// file and so two independent configurations do not share one cache.
    /// </para>
    /// </remarks>
    public sealed class EndpointProbeStore
    {
        /// <summary>Settings key holding the discovered endpoint map.</summary>
        public const string CacheKey = "LLM_RESOLVED_ENDPOINTS";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private readonly LlmSettingsStore _settings;

        public EndpointProbeStore(LlmSettingsStore settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Returns the recorded endpoint for <paramref name="baseUrl"/>, or null when
        /// nothing has been recorded yet. Never throws: a corrupt cache is treated as an
        /// empty one, because failing to open a settings dialog over a cache entry would
        /// be far worse than re-probing once.
        /// </summary>
        public ApiType? Lookup(string baseUrl)
        {
            var key = Normalize(baseUrl);
            if (key == null)
            {
                return null;
            }

            var entries = ReadAll();
            if (!entries.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (!Enum.TryParse<ApiType>(raw, ignoreCase: true, out var parsed))
            {
                return null;
            }

            // Only the two concrete families are meaningful to record. "Auto" is the
            // absence of an answer, not an answer.
            return parsed == ApiType.Responses || parsed == ApiType.ChatCompletions
                ? parsed
                : (ApiType?)null;
        }

        /// <summary>
        /// Records the endpoint family that worked for <paramref name="baseUrl"/>.
        /// Writing through the settings store keeps this in the same file as the rest
        /// of the configuration.
        /// </summary>
        public bool Remember(string baseUrl, ApiType resolved)
        {
            var key = Normalize(baseUrl);
            if (key == null || (resolved != ApiType.Responses && resolved != ApiType.ChatCompletions))
            {
                return false;
            }

            var entries = ReadAll();
            var value = resolved.ToString();

            if (entries.TryGetValue(key, out var existing) &&
                string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                return true; // already recorded; avoid a pointless file write
            }

            entries[key] = value;

            var json = JsonSerializer.Serialize(entries, Options);
            var check = _settings.ReadRaw(CacheKey);
            if (string.Equals(check, json, StringComparison.Ordinal))
            {
                return true;
            }

            return _settings.Write(new[]
            {
                new KeyValuePair<string, string>(CacheKey, json)
            });
        }

        /// <summary>
        /// Drops a recorded endpoint, forcing a fresh probe. Called when a recorded value
        /// turns out to be wrong (the gateway changed, or a proxy rewrote the response),
        /// so the stale entry self-heals instead of permanently breaking the connection.
        /// </summary>
        public bool Forget(string baseUrl)
        {
            var key = Normalize(baseUrl);
            if (key == null)
            {
                return false;
            }

            var entries = ReadAll();
            if (!entries.Remove(key))
            {
                return false;
            }

            if (entries.Count == 0)
            {
                // Nothing left. Storing "{}" would leave a meaningless key behind, so
                // clear the entry instead -- and that means writing, not just returning.
                //
                // Returning true here without writing was a real bug: the in-memory
                // dictionary was empty but the file still held the old JSON, so the very
                // next read resurrected the entry Forget was supposed to have dropped.
                // The self-healing path silently did nothing, which is the worst shape a
                // fallback can take -- the gateway stays "remembered" as broken forever.
                return _settings.Write(new[]
                {
                    new KeyValuePair<string, string>(CacheKey, string.Empty)
                });
            }

            var json = JsonSerializer.Serialize(entries, Options);
            return _settings.Write(new[]
            {
                new KeyValuePair<string, string>(CacheKey, json)
            });
        }

        private Dictionary<string, string> ReadAll()
        {
            var raw = _settings.ReadRaw(CacheKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                return parsed == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Canonical cache key: case-insensitive, no trailing slash, no surrounding
        /// whitespace. Null for anything unusable so callers can skip the lookup.
        /// </summary>
        private static string Normalize(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return null;
            }

            return baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
        }
    }
}
