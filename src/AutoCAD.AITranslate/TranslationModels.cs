using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AITranslate
{
    public enum TextEntityType
    {
        DBText,
        MText
    }

    public enum TranslationMode
    {
        /// <summary>Overwrite the existing text in place.</summary>
        Replace,

        /// <summary>Append a translated copy on a dedicated layer, leaving originals untouched.</summary>
        NewLayer
    }

    /// <summary>A text entity discovered during the read-only scan phase.</summary>
    internal sealed class TextItem
    {
        public TextItem(
            ObjectId originalId,
            ObjectId ownerId,
            TextEntityType entityType,
            string originalText,
            bool isInsideBlockDefinition)
        {
            OriginalId = originalId;
            OwnerId = ownerId;
            EntityType = entityType;
            OriginalText = originalText;
            IsInsideBlockDefinition = isInsideBlockDefinition;
        }

        public ObjectId OriginalId { get; }

        /// <summary>The BlockTableRecord that directly owns this entity.</summary>
        public ObjectId OwnerId { get; }

        public TextEntityType EntityType { get; }

        public string OriginalText { get; }

        /// <summary>
        /// True when the owning BlockTableRecord is a block definition rather than
        /// ModelSpace or PaperSpace. Cloning into a block definition would change every
        /// block reference in the drawing, so those items must always be replaced in place.
        /// </summary>
        public bool IsInsideBlockDefinition { get; }
    }

    /// <summary>An item together with its resolved translation, ready to be applied.</summary>
    internal sealed class TranslationRecord
    {
        public TranslationRecord(
            ObjectId originalId,
            ObjectId ownerId,
            TextEntityType entityType,
            string originalText,
            string translatedText)
        {
            OriginalId = originalId;
            OwnerId = ownerId;
            EntityType = entityType;
            OriginalText = originalText;
            TranslatedText = translatedText;
        }

        public ObjectId OriginalId { get; }

        public ObjectId OwnerId { get; }

        public TextEntityType EntityType { get; }

        public string OriginalText { get; }

        public string TranslatedText { get; }

        /// <summary>Set when the NewLayer mode appended a copy of this entity.</summary>
        public ObjectId NewEntityId { get; private set; } = ObjectId.Null;

        public void SetNewEntityId(ObjectId newEntityId)
        {
            NewEntityId = newEntityId;
        }
    }
}
