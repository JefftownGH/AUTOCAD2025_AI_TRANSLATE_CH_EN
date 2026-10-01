using System;
using System.Collections.Generic;

namespace LlmToolkit
{
    /// <summary>
    /// Everything needed to talk to one OpenAI-compatible endpoint.
    /// </summary>
    /// <remarks>
    /// A plain mutable options object rather than a long constructor, because the
    /// original eight-positional-argument constructor was both easy to mis-order (two
    /// adjacent strings, <c>organization</c> and <c>project</c>, were trivially
    /// swappable with no compiler complaint) and impossible to extend without touching
    /// every call site.
    /// </remarks>
    public sealed class LlmOptions
    {
        /// <summary>Bearer token. Blank means "not configured".</summary>
        public string ApiKey { get; set; }

        public string Model { get; set; }

        /// <summary>
        /// API root, without a trailing slash and without the route. For example
        /// <c>https://open.bigmodel.cn/api/paas/v4</c>; the client appends
        /// <c>/chat/completions</c>.
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>One of: auto, responses, chat_completions. Unrecognised values mean auto.</summary>
        public string ApiType { get; set; }

        /// <summary>Optional house-style prompt prepended to every request.</summary>
        public string SystemPrompt { get; set; }

        /// <summary>Per-request timeout in milliseconds. Null means the client default.</summary>
        public int? TimeoutMs { get; set; }

        /// <summary>Sent as <c>OpenAI-Organization</c>. Only meaningful for api.openai.com.</summary>
        public string Organization { get; set; }

        /// <summary>Sent as <c>OpenAI-Project</c>. Only meaningful for api.openai.com.</summary>
        public string Project { get; set; }

        /// <summary>
        /// Default timeout when <see cref="TimeoutMs"/> is unset. 60 s suits a small
        /// single-string request; batch or reasoning models routinely need more, which
        /// is why every preset sets its own.
        /// </summary>
        public const int DefaultTimeoutMs = 60000;

        /// <summary>Default model when none is configured.</summary>
        public const string DefaultModel = "glm-4.6";

        /// <summary>
        /// Default base URL when none is configured. Points at a gateway reachable from
        /// mainland China; the previous default (<c>api.openai.com</c>) could not
        /// connect there at all, so a fresh install looked broken rather than
        /// unconfigured.
        /// </summary>
        public const string DefaultBaseUrl = "https://open.bigmodel.cn/api/paas/v4";

        /// <summary>Whether an API key has been supplied.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

        /// <summary>Deep copy, so a caller can tweak one field without side effects.</summary>
        public LlmOptions Clone()
        {
            return new LlmOptions
            {
                ApiKey = ApiKey,
                Model = Model,
                BaseUrl = BaseUrl,
                ApiType = ApiType,
                SystemPrompt = SystemPrompt,
                TimeoutMs = TimeoutMs,
                Organization = Organization,
                Project = Project
            };
        }

        /// <summary>
        /// Loads options from a settings store, applying the defaults above and
        /// migrating any legacy keys first.
        /// </summary>
        public static LlmOptions FromSettings(LlmSettingsStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            return new LlmOptions
            {
                ApiKey = store.Read(LlmSettingKeys.ApiKey),
                Model = store.Read(LlmSettingKeys.Model),
                BaseUrl = store.Read(LlmSettingKeys.BaseUrl),
                ApiType = store.Read(LlmSettingKeys.ApiType),
                SystemPrompt = store.Read(LlmSettingKeys.SystemPrompt),
                TimeoutMs = store.ReadInt(LlmSettingKeys.TimeoutMs),
                Organization = store.Read(LlmSettingKeys.Organization),
                Project = store.Read(LlmSettingKeys.Project)
            };
        }

        /// <summary>Writes these options back to a settings store under the current key names.</summary>
        public bool SaveTo(LlmSettingsStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            return store.Write(new[]
            {
                new KeyValuePair<string, string>(LlmSettingKeys.ApiKey, ApiKey ?? string.Empty),
                new KeyValuePair<string, string>(LlmSettingKeys.Model, Model ?? string.Empty),
                new KeyValuePair<string, string>(LlmSettingKeys.BaseUrl, BaseUrl ?? string.Empty),
                new KeyValuePair<string, string>(LlmSettingKeys.ApiType, ApiType ?? string.Empty),
                new KeyValuePair<string, string>(LlmSettingKeys.SystemPrompt, SystemPrompt ?? string.Empty),
                new KeyValuePair<string, string>(
                    LlmSettingKeys.TimeoutMs,
                    TimeoutMs.HasValue && TimeoutMs.Value > 0 ? TimeoutMs.Value.ToString() : string.Empty),
                new KeyValuePair<string, string>(LlmSettingKeys.Organization, Organization ?? string.Empty),
                new KeyValuePair<string, string>(LlmSettingKeys.Project, Project ?? string.Empty)
            });
        }
    }
}
