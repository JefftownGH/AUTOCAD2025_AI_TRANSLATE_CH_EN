using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// A named bundle of connection settings. Presets let the user keep several
    /// backends on hand (a vendor API, a company gateway, a local Ollama) and switch
    /// between them without retyping the model name, base URL and API type.
    /// </summary>
    /// <remarks>
    /// Every field here is optional on read. A preset that was hand-edited in the
    /// JSON file and lost a property must still load, so missing values fall back to
    /// the same defaults the dialog uses. This keeps a partially-broken presets array
    /// from taking the whole settings file down with it.
    /// </remarks>
    internal sealed class ModelPreset
    {
        /// <summary>User-facing label. Must be unique (case-insensitive) within the list.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>One of: auto, responses, chat_completions.</summary>
        [JsonPropertyName("apiType")]
        public string ApiType { get; set; } = "auto";

        /// <summary>Per-request timeout in milliseconds. Null means "use the default".</summary>
        [JsonPropertyName("timeoutMs")]
        public int? TimeoutMs { get; set; }

        [JsonPropertyName("systemPrompt")]
        public string SystemPrompt { get; set; } = string.Empty;

        [JsonPropertyName("organization")]
        public string Organization { get; set; } = string.Empty;

        [JsonPropertyName("project")]
        public string Project { get; set; } = string.Empty;

        /// <summary>
        /// True for presets shipped with the plugin. They can be edited and
        /// "saved as" a new preset, but the originals cannot be deleted, so a user
        /// who clears their own presets is never left with an empty list.
        /// </summary>
        [JsonPropertyName("builtIn")]
        public bool BuiltIn { get; set; }

        /// <summary>Creates a deep copy, used when "save as" forks a preset.</summary>
        public ModelPreset Clone()
        {
            return new ModelPreset
            {
                Name = Name,
                Model = Model,
                BaseUrl = BaseUrl,
                ApiType = ApiType,
                TimeoutMs = TimeoutMs,
                SystemPrompt = SystemPrompt,
                Organization = Organization,
                Project = Project,
                BuiltIn = BuiltIn
            };
        }
    }

    /// <summary>
    /// Reads and writes the user's preset list. Presets live in the same settings
    /// file as the active configuration, under a single JSON array property, so
    /// there is exactly one file to back up or delete.
    /// </summary>
    internal static class PresetStore
    {
        /// <summary>JSON property holding the preset array inside the settings file.</summary>
        internal const string PresetsKey = "MODEL_PRESETS";

        /// <summary>JSON property remembering which preset was last used.</summary>
        internal const string ActivePresetKey = "ACTIVE_PRESET";

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>
        /// Presets available out of the box. Kept deliberately short: each one is a
        /// starting point the user is expected to edit, not an exhaustive catalogue.
        /// </summary>
        internal static List<ModelPreset> BuiltInPresets()
        {
            return new List<ModelPreset>
            {
                new ModelPreset
                {
                    Name = "OpenAI GPT-4.1",
                    Model = "gpt-4.1",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiType = "auto",
                    TimeoutMs = 60000,
                    BuiltIn = true
                },
                new ModelPreset
                {
                    Name = "DeepSeek",
                    Model = "deepseek-chat",
                    BaseUrl = "https://api.deepseek.com",
                    ApiType = "chat_completions",
                    TimeoutMs = 90000,
                    BuiltIn = true
                },
                new ModelPreset
                {
                    Name = "智谱 GLM",
                    Model = "glm-4.6",
                    BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                    ApiType = "chat_completions",
                    TimeoutMs = 120000,
                    BuiltIn = true
                },
                new ModelPreset
                {
                    Name = "阿里通义千问",
                    Model = "qwen-plus",
                    BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                    ApiType = "chat_completions",
                    TimeoutMs = 90000,
                    BuiltIn = true
                },
                new ModelPreset
                {
                    Name = "月之暗面 Kimi",
                    Model = "moonshot-v1-8k",
                    BaseUrl = "https://api.moonshot.cn/v1",
                    ApiType = "chat_completions",
                    TimeoutMs = 90000,
                    BuiltIn = true
                },
                new ModelPreset
                {
                    Name = "本地 Ollama",
                    Model = "qwen2.5:7b",
                    BaseUrl = "http://localhost:11434/v1",
                    ApiType = "chat_completions",
                    TimeoutMs = 120000,
                    BuiltIn = true
                }
            };
        }

        /// <summary>
        /// Loads the preset list. Built-ins are always present and come first;
        /// user presets follow. When the user has never saved any, this returns
        /// exactly the built-ins plus one preset synthesised from the currently
        /// active configuration, so an existing install keeps its settings visible
        /// in the dropdown instead of appearing to have lost them.
        /// </summary>
        internal static List<ModelPreset> Load()
        {
            var builtIns = BuiltInPresets();
            var result = new List<ModelPreset>(builtIns);

            var stored = ReadStoredPresets();
            if (stored.Count == 0)
            {
                var legacy = BuildPresetFromActiveSettings();
                if (legacy != null)
                {
                    result.Add(legacy);
                }

                return result;
            }

            // Merge user presets in after the built-ins, keeping names unique.
            //
            // A stored entry whose name matches a built-in is skipped rather than
            // shadowing it. Built-ins are re-seeded from code on every load, so a
            // stored copy would be a stale snapshot of a definition the plugin is
            // actively maintaining; surfacing the current built-in is less
            // surprising than showing an outdated duplicate.
            var names = new HashSet<string>(
                builtIns.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var preset in stored)
            {
                if (string.IsNullOrWhiteSpace(preset.Name))
                {
                    continue;
                }

                // Defensive: a hand-edited file could set builtIn on a user entry,
                // which would make it un-deletable from the dialog.
                preset.BuiltIn = false;

                if (names.Add(preset.Name))
                {
                    result.Add(preset);
                }
            }

            return result;
        }

        /// <summary>
        /// Persists the user-facing presets (the non-built-in ones) plus the name of
        /// the active preset. Built-ins are re-seeded from code on every load so a
        /// future release can improve them without a migration step.
        /// </summary>
        internal static bool Save(IEnumerable<ModelPreset> presets, string activeName)
        {
            var userPresets = presets
                .Where(p => p != null && !p.BuiltIn && !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            var json = JsonSerializer.Serialize(userPresets, SerializerOptions);

            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(PresetsKey, json),
                new KeyValuePair<string, string>(
                    ActivePresetKey, activeName ?? string.Empty)
            };

            return Settings.Write(values);
        }

        private static List<ModelPreset> ReadStoredPresets()
        {
            var raw = Settings.ReadRaw(PresetsKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<ModelPreset>();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<ModelPreset>>(raw);
                return parsed ?? new List<ModelPreset>();
            }
            catch (JsonException)
            {
                // A hand-edited presets array must never prevent the dialog from
                // opening. Report nothing and fall back to the built-ins.
                Diagnostics.Log($"presets: unparseable {PresetsKey}, ignoring stored presets");
                return new List<ModelPreset>();
            }
        }

        /// <summary>
        /// Builds a preset from whatever is currently configured, so an install that
        /// predates presets still shows its working configuration as a selectable
        /// entry. Returns null when nothing has been configured yet.
        /// </summary>
        private static ModelPreset BuildPresetFromActiveSettings()
        {
            var model = Settings.Read("OPENAI_MODEL");
            var baseUrl = Settings.Read("OPENAI_BASE_URL");
            var apiKey = Settings.Read("OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(model) &&
                string.IsNullOrWhiteSpace(baseUrl) &&
                string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            return new ModelPreset
            {
                Name = "当前配置",
                Model = model ?? string.Empty,
                BaseUrl = baseUrl ?? string.Empty,
                ApiType = OpenAiClient.NormalizeApiType(Settings.Read("OPENAI_API_TYPE")).ToString().ToLowerInvariant(),
                TimeoutMs = Settings.ReadInt("OPENAI_TIMEOUT_MS"),
                SystemPrompt = Settings.Read("OPENAI_SYSTEM_PROMPT") ?? string.Empty,
                Organization = Settings.Read("OPENAI_ORG") ?? string.Empty,
                Project = Settings.Read("OPENAI_PROJECT") ?? string.Empty,
                BuiltIn = false
            };
        }

        /// <summary>
        /// Case-insensitive lookup used to pre-select the dropdown on open.
        /// </summary>
        internal static ModelPreset FindByName(IEnumerable<ModelPreset> presets, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return presets.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validates a prospective preset. Returns null when acceptable, otherwise
        /// a message suitable for display next to the buttons.
        /// </summary>
        internal static string Validate(string name, string baseUrl, int? timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "请填写方案名称。";
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "请填写接口地址。";
            }

            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "接口地址必须是 http:// 或 https:// 开头的完整地址。";
            }

            if (timeoutMs.HasValue && timeoutMs.Value <= 0)
            {
                return "超时时间必须是正整数毫秒数。";
            }

            return null;
        }
    }
}
