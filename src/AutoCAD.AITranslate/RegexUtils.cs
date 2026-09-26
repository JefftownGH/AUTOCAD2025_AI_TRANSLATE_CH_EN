using System.Text.RegularExpressions;

namespace AutoCAD.AITranslate
{
    internal static class RegexUtils
    {
        /// <summary>
        /// Detects text that is worth sending to the translator.
        /// </summary>
        /// <remarks>
        /// Covers more than just the primary CJK block:
        ///   U+2E80-U+2EFF   CJK radicals supplement
        ///   U+3000-U+303F   CJK symbols and punctuation (。、；：？！)
        ///   U+3400-U+4DBF   CJK unified ideographs extension A (rare characters)
        ///   U+4E00-U+9FFF   CJK unified ideographs (the common block)
        ///   U+F900-U+FAFF   CJK compatibility ideographs
        ///   U+FF00-U+FFEF   Halfwidth and fullwidth forms (，；：？！（）)
        ///   U+20000-U+2A6DF Extension B (surrogate pair range)
        /// The original implementation only covered U+4E00-U+9FFF, so labels consisting
        /// solely of Chinese punctuation, or containing rare characters, were skipped.
        /// </remarks>
        private static readonly Regex ChineseRegex = new Regex(
            @"[\u2E80-\u2EFF\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF]|[\uD840-\uD87F][\uDC00-\uDFFF]",
            RegexOptions.Compiled);

        /// <summary>Matches AutoCAD MText inline formatting codes such as \P and \L.</summary>
        private static readonly Regex MTextCodeRegex = new Regex(
            @"\\[A-Za-z]",
            RegexOptions.Compiled);

        public static bool ContainsChinese(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return ChineseRegex.IsMatch(text);
        }

        public static bool ContainsMTextCode(string text)
        {
            return !string.IsNullOrEmpty(text) && MTextCodeRegex.IsMatch(text);
        }
    }
}
