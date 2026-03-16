using System.Text.RegularExpressions;

namespace AutoCAD.AITranslate
{
    internal static class RegexUtils
    {
        private static readonly Regex ChineseRegex = new Regex(@"[\p{IsCJKUnifiedIdeographs}]", RegexOptions.Compiled);

        public static bool ContainsChinese(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return ChineseRegex.IsMatch(text);
        }
    }
}
