using System;
using System.Collections.Generic;
using System.Linq;

namespace JeffCAD.AiAssistant
{
    /// <summary>A selectable translation target.</summary>
    public sealed class TargetLanguage
    {
        public TargetLanguage(string code, string displayName, string promptName)
        {
            Code = code;
            DisplayName = displayName;
            PromptName = promptName;
        }

        /// <summary>Stable short tag used as a cache key component. Never localised.</summary>
        public string Code { get; }

        /// <summary>What the picker shows, in the user's own language.</summary>
        public string DisplayName { get; }

        /// <summary>
        /// How the language is named inside the prompt. Spelled out in English because
        /// that is what models follow most reliably -- passing "韩文" works less often
        /// than "Korean" even on Chinese-first models.
        /// </summary>
        public string PromptName { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// The languages offered in the translation dialog.
    /// </summary>
    /// <remarks>
    /// Deliberately a fixed list rather than free text. A typo in a free-text language
    /// field produces confident nonsense from the model with no error, which is worse
    /// than a slightly shorter menu. Adding a language is a one-line change.
    /// </remarks>
    public static class TargetLanguages
    {
        /// <summary>The language assumed when no explicit choice was ever made.</summary>
        public const string DefaultCode = "en";

        public static readonly IReadOnlyList<TargetLanguage> All = new List<TargetLanguage>
        {
            new TargetLanguage("en", "英语 (English)",  "English"),
            new TargetLanguage("ko", "韩语 (한국어)",   "Korean"),
            new TargetLanguage("ja", "日语 (日本語)",   "Japanese"),
            new TargetLanguage("ru", "俄语 (Русский)",  "Russian"),
            new TargetLanguage("de", "德语 (Deutsch)",  "German"),
            new TargetLanguage("fr", "法语 (Français)", "French"),
            new TargetLanguage("es", "西班牙语 (Español)", "Spanish"),
            new TargetLanguage("pt", "葡萄牙语 (Português)", "Portuguese"),
            new TargetLanguage("it", "意大利语 (Italiano)", "Italian"),
            new TargetLanguage("ar", "阿拉伯语 (العربية)", "Arabic"),
            new TargetLanguage("th", "泰语 (ไทย)",      "Thai"),
            new TargetLanguage("vi", "越南语 (Tiếng Việt)", "Vietnamese"),
        };

        public static TargetLanguage ByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return ByCode(DefaultCode);
            }

            var match = All.FirstOrDefault(
                l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? ByCode(DefaultCode);
        }

        /// <summary>
        /// Builds the system prompt for a target language, replacing any language the
        /// user's own system prompt happens to mention.
        /// </summary>
        /// <remarks>
        /// The user's own system prompt is kept (it carries house style such as
        /// "直接给出翻译结果，不要解释") but an explicit language instruction is appended,
        /// because a stored prompt written for English would otherwise fight the picker.
        /// </remarks>
        public static string BuildSystemPrompt(string userSystemPrompt, TargetLanguage language)
        {
            var target = language ?? ByCode(DefaultCode);

            var instruction =
                $"Translate the user's text into {target.PromptName}. " +
                "Preserve the original formatting, line breaks, numbers, units and symbols exactly. " +
                "Do not add explanations, notes, quotes or transliterations -- " +
                "output only the translated text itself.";

            if (string.IsNullOrWhiteSpace(userSystemPrompt))
            {
                return instruction;
            }

            return userSystemPrompt.Trim() + " " + instruction;
        }
    }
}
