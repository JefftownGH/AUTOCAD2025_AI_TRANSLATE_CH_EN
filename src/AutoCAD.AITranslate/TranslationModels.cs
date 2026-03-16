using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AITranslate
{
    internal enum TextEntityType
    {
        DBText,
        MText
    }

    internal enum TranslationMode
    {
        Replace,
        NewLayer
    }

    internal sealed class TextItem
    {
        public TextItem(ObjectId originalId, ObjectId blockTableRecordId, TextEntityType entityType, string originalText)
        {
            OriginalId = originalId;
            BlockTableRecordId = blockTableRecordId;
            EntityType = entityType;
            OriginalText = originalText;
        }

        public ObjectId OriginalId { get; }
        public ObjectId BlockTableRecordId { get; }
        public TextEntityType EntityType { get; }
        public string OriginalText { get; }
    }

    internal sealed class TranslationRecord
    {
        public TranslationRecord(ObjectId originalId, ObjectId blockTableRecordId, TextEntityType entityType, string originalText, string translatedText)
        {
            OriginalId = originalId;
            BlockTableRecordId = blockTableRecordId;
            EntityType = entityType;
            OriginalText = originalText;
            TranslatedText = translatedText;
            NewEntityId = ObjectId.Null;
        }

        public ObjectId OriginalId { get; }
        public ObjectId BlockTableRecordId { get; }
        public TextEntityType EntityType { get; }
        public string OriginalText { get; }
        public string TranslatedText { get; }
        public ObjectId NewEntityId { get; private set; }

        public void SetNewEntityId(ObjectId newEntityId)
        {
            NewEntityId = newEntityId;
        }
    }
}
