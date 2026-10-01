using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmToolkit
{
    /// <summary>
    /// A named bundle of connection settings. Presets let the user keep several
    /// backends on hand (a vendor API, a company gateway, a local Ollama) and switch
    /// between them without retyping the model name, base URL and API type.
    /// </summary>
    /// <remarks>
    /// Every field here is optional on read. A preset that was hand-edited in the JSON
    /// file and lost a property must still load, so missing values fall back to the same
    /// defaults the dialog uses. This keeps a partially-broken presets array from taking
    /// the whole settings file down with it.
    /// </remarks>
    public sealed class ModelPreset
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
        /// True for presets shipped with the library. They can be edited and "saved as"
        /// a new preset, but the originals cannot be deleted, so a user who clears their
        /// own presets is never left with an empty list.
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

        /// <summary>
        /// Applies this preset onto a fresh <see cref="LlmOptions"/>. Host-specific
        /// values (the model a translation plugin wants by default, for instance) are
        /// the caller's business and are not carried here.
        /// </summary>
        public LlmOptions ToOptions(string apiKey = null)
        {
            return new LlmOptions
            {
                ApiKey = apiKey,
                Model = Model,
                BaseUrl = BaseUrl,
                ApiType = ApiType,
                TimeoutMs = TimeoutMs,
                SystemPrompt = SystemPrompt,
                Organization = Organization,
                Project = Project
            };
        }
    }

    /// <summary>
    /// Reads and writes the preset list. Presets live in the same settings file as the
    /// active configuration, under a single JSON array property, so there is exactly
    /// one file to back up or delete.
    /// </summary>
    public sealed class ModelPresetStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly LlmSettingsStore _settings;
        private readonly Func<IReadOnlyList<ModelPreset>> _builtInFactory;

        /// <param name="settings">Backing store, shared with the rest of the configuration.</param>
        /// <param name="builtInFactory">
        /// Supplies the presets seeded on every load. Injected so a consuming application
        /// can ship its own catalogue and a test can pin a known list. Defaults to
        /// <see cref="DefaultPresets"/>.
        /// </param>
        public ModelPresetStore(
            LlmSettingsStore settings,
            Func<IReadOnlyList<ModelPreset>> builtInFactory = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _builtInFactory = builtInFactory ?? DefaultPresets;
        }

        /// <summary>
        /// Presets available out of the box.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately short, and deliberately domestic-first. Every entry is a
        /// starting point the user is expected to edit, not an exhaustive catalogue.
        /// </para>
        /// <para>
        /// <c>api.openai.com</c> is unreachable from mainland China, so a preset
        /// pointing at it would be dead on arrival for the intended audience. The
        /// toolkit still speaks the OpenAI wire format -- that is the whole point of
        /// being "OpenAI-compatible" -- but OpenAI's own hosted endpoint is not shipped
        /// as a selectable default. A user who has access can add one in seconds.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<ModelPreset> DefaultPresets()
        {
            return new List<ModelPreset>
            {
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
                    Name = "DeepSeek 深度求索",
                    Model = "deepseek-chat",
                    BaseUrl = "https://api.deepseek.com",
                    ApiType = "chat_completions",
                    TimeoutMs = 90000,
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
                    Name = "字节豆包（火山方舟）",
                    Model = "doubao-pro-32k",
                    BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
                    ApiType = "chat_completions",
                    TimeoutMs = 90000,
                    BuiltIn = true
                },
                new ModelPreset
                {
                    Name = "硅基流动 SiliconFlow",
                    Model = "Qwen/Qwen2.5-7B-Instruct",
                    BaseUrl = "https://api.siliconflow.cn/v1",
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
        /// Loads the preset list. Built-ins are always present and come first; user
        /// presets follow. When the user has never saved any, this returns exactly the
        /// built-ins.
        /// </summary>
        public List<ModelPreset> Load()
        {
            var builtIns = _builtInFactory().ToList();
            var result = new List<ModelPreset>(builtIns);

            var stored = ReadStoredPresets();
            if (stored.Count == 0)
            {
                return result;
            }

            // Merge user presets in after the built-ins, keeping names unique.
            //
            // A stored entry whose name matches a built-in is skipped rather than
            // shadowing it. Built-ins are re-seeded from code on every load, so a stored
            // copy would be a stale snapshot of a definition the library is actively
            // maintaining; surfacing the current built-in is less surprising than
            // showing an outdated duplicate.
            var names = new HashSet<string>(
                builtIns.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var preset in stored)
            {
                if (string.IsNullOrWhiteSpace(preset.Name))
                {
                    continue;
                }

                // Defensive: a hand-edited file could set builtIn on a user entry, which
                // would make it un-deletable from the dialog.
                preset.BuiltIn = false;

                if (names.Add(preset.Name))
                {
                    result.Add(preset);
                }
            }

            return result;
        }

        /// <summary>
        /// Persists the user-facing presets (the non-built-in ones) plus the name of the
        /// active preset. Built-ins are re-seeded from code on every load so a future
        /// release can improve them without a migration step.
        /// </summary>
        public bool Save(IEnumerable<ModelPreset> presets, string activeName)
        {
            var userPresets = (presets ?? Enumerable.Empty<ModelPreset>())
                .Where(p => p != null && !p.BuiltIn && !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            var json = JsonSerializer.Serialize(userPresets, SerializerOptions);

            return _settings.Write(new[]
            {
                new KeyValuePair<string, string>(LlmSettingKeys.ModelPresets, json),
                new KeyValuePair<string, string>(
                    LlmSettingKeys.ActivePreset, activeName ?? string.Empty)
            });
        }

        /// <summary>
        /// Builds a preset from whatever is currently configured, so an install that
        /// predates presets still shows its working configuration as a selectable entry.
        /// Returns null when nothing has been configured yet.
        /// </summary>
        public ModelPreset BuildPresetFromActiveSettings(string name = "当前配置")
        {
            var model = _settings.Read(LlmSettingKeys.Model);
            var baseUrl = _settings.Read(LlmSettingKeys.BaseUrl);
            var apiKey = _settings.Read(LlmSettingKeys.ApiKey);

            if (string.IsNullOrWhiteSpace(model) &&
                string.IsNullOrWhiteSpace(baseUrl) &&
                string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            return new ModelPreset
            {
                Name = name,
                Model = model ?? string.Empty,
                BaseUrl = baseUrl ?? string.Empty,
                ApiType = ChatClient.NormalizeApiType(
                    _settings.Read(LlmSettingKeys.ApiType)).ToString().ToLowerInvariant(),
                TimeoutMs = _settings.ReadInt(LlmSettingKeys.TimeoutMs),
                SystemPrompt = _settings.Read(LlmSettingKeys.SystemPrompt) ?? string.Empty,
                Organization = _settings.Read(LlmSettingKeys.Organization) ?? string.Empty,
                Project = _settings.Read(LlmSettingKeys.Project) ?? string.Empty,
                BuiltIn = false
            };
        }

        private List<ModelPreset> ReadStoredPresets()
        {
            var raw = _settings.ReadRaw(LlmSettingKeys.ModelPresets);
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
                // A hand-edited presets array must never prevent the dialog from opening.
                // Report nothing and fall back to the built-ins.
                return new List<ModelPreset>();
            }
        }

        /// <summary>
        /// Case-insensitive lookup used to pre-select the dropdown on open.
        /// </summary>
        public static ModelPreset FindByName(IEnumerable<ModelPreset> presets, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || presets == null)
            {
                return null;
            }

            return presets.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validates a prospective preset. Returns null when acceptable, otherwise a
        /// message suitable for display next to the buttons.
        /// </summary>
        public static string Validate(string name, string baseUrl, int? timeoutMs)
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
