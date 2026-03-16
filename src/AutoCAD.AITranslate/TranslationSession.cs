using System.Collections.Generic;

namespace AutoCAD.AITranslate
{
    internal static class TranslationSession
    {
        private static List<TranslationRecord> _lastRun;

        public static void SetLastRun(List<TranslationRecord> records)
        {
            _lastRun = records;
        }

        public static List<TranslationRecord> GetLastRun()
        {
            return _lastRun;
        }

        public static void ClearLastRun()
        {
            _lastRun = null;
        }
    }
}
