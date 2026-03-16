using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AutoCAD.AITranslate
{
    internal sealed class TranslationService
    {
        private readonly OpenAiClient _client;
        private readonly Dictionary<string, string> _cache;

        private TranslationService(OpenAiClient client)
        {
            _client = client;
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public bool IsConfigured => _client.IsConfigured;

        public static TranslationService CreateFromEnvironment()
        {
            var apiKey = ReadSetting("OPENAI_API_KEY");
            var model = ReadSetting("OPENAI_MODEL");
            var baseUrl = ReadSetting("OPENAI_BASE_URL");
            var organization = ReadSetting("OPENAI_ORG");
            var project = ReadSetting("OPENAI_PROJECT");
            var apiType = ReadSetting("OPENAI_API_TYPE");
            var systemPrompt = ReadSetting("OPENAI_SYSTEM_PROMPT");
            var timeoutMs = ReadIntSetting("OPENAI_TIMEOUT_MS");

            var client = new OpenAiClient(apiKey, model, baseUrl, organization, project, apiType, systemPrompt, timeoutMs);
            return new TranslationService(client);
        }

        public string TranslateText(string original)
        {
            if (string.IsNullOrWhiteSpace(original))
            {
                return original;
            }

            if (_cache.TryGetValue(original, out var cached))
            {
                return cached;
            }

            var prompt = BuildPrompt(original);
            var translated = _client.TranslateToEnglish(prompt);
            var result = string.IsNullOrWhiteSpace(translated) ? original : translated;
            _cache[original] = result;
            return result;
        }

        private static string BuildPrompt(string original)
        {
            return
                "Translate the following Chinese text in an AutoCAD drawing to natural, professional English.\n" +
                "Preserve numbers, units, punctuation, and line breaks.\n" +
                "Preserve AutoCAD MText formatting codes such as \\\\P, \\\\L, \\\\l, \\\\O, \\\\o, \\\\S, \\\\A without changing them.\n" +
                "Return only the translated text with no extra commentary.\n\n" +
                "Text:\n" + original;
        }

        private static readonly Lazy<Dictionary<string, string>> LocalSettings =
            new Lazy<Dictionary<string, string>>(LoadLocalSettings, true);

        private static string ReadSetting(string key)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            if (LocalSettings.Value.TryGetValue(key, out var fromFile) &&
                !string.IsNullOrWhiteSpace(fromFile))
            {
                return fromFile;
            }

            return null;
        }

        private static Dictionary<string, string> LoadLocalSettings()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = GetLocalSettingsPath();
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
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values[property.Name] = value;
                    }
                }
            }
            catch
            {
                return values;
            }

            return values;
        }

        private static int? ReadIntSetting(string key)
        {
            var value = ReadSetting(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (int.TryParse(value, out var number) && number > 0)
            {
                return number;
            }

            return null;
        }

        private static string GetLocalSettingsPath()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var dir = Path.GetDirectoryName(location);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            return Path.Combine(dir, "AutoCAD.AITranslate.settings.json");
        }
    }
}
