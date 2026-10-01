using System;
using System.Collections.Generic;

namespace LlmToolkit
{
    /// <summary>
    /// The configuration keys this toolkit reads and writes, plus the migration that
    /// carries a legacy configuration forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The keys are deliberately vendor-neutral. An earlier revision named every one
    /// of them <c>OPENAI_*</c>, which was accurate when OpenAI was the only backend but
    /// became actively misleading once the presets covered Zhipu, DeepSeek, Qwen,
    /// Moonshot and local Ollama: users reasonably concluded the plugin required an
    /// OpenAI account and therefore could not work in mainland China, where
    /// <c>api.openai.com</c> is not reachable at all.
    /// </para>
    /// <para>
    /// Renaming the keys would silently discard every existing user's configuration, so
    /// <see cref="MigrateLegacyKeys"/> reads the old names when the new one is absent
    /// and rewrites them under the new names on the next save. Upgrade is therefore
    /// invisible: no file to edit, nothing to re-enter.
    /// </para>
    /// </remarks>
    public static class LlmSettingKeys
    {
        public const string ApiKey = "LLM_API_KEY";
        public const string Model = "LLM_MODEL";
        public const string BaseUrl = "LLM_BASE_URL";
        public const string ApiType = "LLM_API_TYPE";
        public const string SystemPrompt = "LLM_SYSTEM_PROMPT";
        public const string TimeoutMs = "LLM_TIMEOUT_MS";
        public const string Organization = "LLM_ORG";
        public const string Project = "LLM_PROJECT";

        /// <summary>Preset array, stored as a JSON array in the same file.</summary>
        public const string ModelPresets = "LLM_MODEL_PRESETS";

        /// <summary>Name of the last-selected preset.</summary>
        public const string ActivePreset = "LLM_ACTIVE_PRESET";

        /// <summary>
        /// Keys written to the file, in the order the settings UI presents them.
        /// </summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            ApiKey,
            Model,
            BaseUrl,
            ApiType,
            SystemPrompt,
            TimeoutMs,
            Organization,
            Project
        };

        /// <summary>
        /// Legacy key name to current key name. Every entry here was in active use in a
        /// shipped build, so removing one would strand the users who still have it.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> LegacyKeyMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OPENAI_API_KEY"] = ApiKey,
                ["OPENAI_MODEL"] = Model,
                ["OPENAI_BASE_URL"] = BaseUrl,
                ["OPENAI_API_TYPE"] = ApiType,
                ["OPENAI_SYSTEM_PROMPT"] = SystemPrompt,
                ["OPENAI_TIMEOUT_MS"] = TimeoutMs,
                ["OPENAI_ORG"] = Organization,
                ["OPENAI_PROJECT"] = Project,

                // The translation plugin's target language. Not a connection setting,
                // so it is not in All -- but it was renamed alongside its siblings and
                // must migrate too, otherwise every existing user silently reverts to
                // the default target language.
                ["OPENAI_TARGET_LANGUAGE"] = "LLM_TARGET_LANGUAGE"
            };

        /// <summary>The current-name key for the plugin-specific target language.</summary>
        public const string TargetLanguage = "LLM_TARGET_LANGUAGE";

        /// <summary>
        /// Copies any legacy key onto its current name, returning the pairs that need
        /// to be persisted so the file converges on the new names.
        /// </summary>
        /// <remarks>
        /// Only fills a gap: a value already present under the new name always wins, so
        /// a user who has edited the new key never has it reverted by a stale legacy
        /// entry. Returns an empty list when nothing needed migrating, which lets the
        /// caller skip a pointless file write on every single run.
        /// </remarks>
        public static IReadOnlyList<KeyValuePair<string, string>> MigrateLegacyKeys(
            LlmSettingsStore store)
        {
            var migrated = new List<KeyValuePair<string, string>>();

            if (store == null)
            {
                return migrated;
            }

            foreach (var legacy in LegacyKeyMap)
            {
                // Already on the new key (file or environment) - nothing to do.
                if (!string.IsNullOrWhiteSpace(store.Read(legacy.Value)))
                {
                    continue;
                }

                var old = store.ReadRaw(legacy.Key);
                if (string.IsNullOrWhiteSpace(old))
                {
                    continue;
                }

                migrated.Add(new KeyValuePair<string, string>(legacy.Value, old));
            }

            return migrated;
        }
    }
}
