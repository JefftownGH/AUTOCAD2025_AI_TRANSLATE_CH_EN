using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// One line of the bilingual review list: the text found in the drawing, the proposed
    /// translation, and whether the user has signed off on it.
    /// </summary>
    /// <remarks>
    /// Implements <see cref="INotifyPropertyChanged"/> so an edit in the grid is visible to
    /// the summary counter and the confirm toggle immediately. Kept free of WPF types so
    /// the confirmation and override rules can be exercised without a UI.
    /// </remarks>
    public sealed class TranslationRow : INotifyPropertyChanged
    {
        private string _translatedText;
        private bool _isConfirmed;
        private bool _isEdited;

        public TranslationRow(
            ObjectId originalId,
            ObjectId ownerId,
            TextEntityType entityType,
            string originalText,
            string translatedText,
            bool isInsideBlockDefinition,
            bool fromCache,
            bool fromVerifiedCache)
        {
            OriginalId = originalId;
            OwnerId = ownerId;
            EntityType = entityType;
            OriginalText = originalText;
            _translatedText = translatedText ?? string.Empty;
            IsInsideBlockDefinition = isInsideBlockDefinition;
            FromCache = fromCache;
            FromVerifiedCache = fromVerifiedCache;

            // Confirmed by default, per the chosen interaction model: every proposed
            // translation is accepted unless the user actively unticks it. Reviewing 80
            // rows should cost a few clicks, not eighty.
            _isConfirmed = !string.IsNullOrWhiteSpace(_translatedText);
        }

        public ObjectId OriginalId { get; }

        public ObjectId OwnerId { get; }

        public TextEntityType EntityType { get; }

        /// <summary>The text as it exists in the drawing right now.</summary>
        public string OriginalText { get; }

        /// <summary>The proposed translation. Writable: the user may correct it inline.</summary>
        public string TranslatedText
        {
            get => _translatedText;
            set
            {
                var incoming = value ?? string.Empty;
                if (string.Equals(_translatedText, incoming, StringComparison.Ordinal))
                {
                    return;
                }

                _translatedText = incoming;

                // Any manual edit marks the row as human-authored. That flag is what
                // promotes the entry to a verified cache record, so a correction made
                // once is honoured on every later run instead of being overwritten.
                IsEdited = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>Whether this row should be written to the drawing.</summary>
        public bool IsConfirmed
        {
            get => _isConfirmed;
            set
            {
                if (_isConfirmed == value)
                {
                    return;
                }

                _isConfirmed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>True once the user has typed in the translation cell.</summary>
        public bool IsEdited
        {
            get => _isEdited;
            private set
            {
                if (_isEdited == value)
                {
                    return;
                }

                _isEdited = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceLabel));
            }
        }

        /// <summary>Set by the dialog when a row failed to produce any translation.</summary>
        public bool HasError { get; set; }

        public bool IsInsideBlockDefinition { get; }

        public bool FromCache { get; }

        /// <summary>True when this came from a previously human-corrected cache entry.</summary>
        public bool FromVerifiedCache { get; }

        public bool IsEmpty => string.IsNullOrWhiteSpace(TranslatedText);

        /// <summary>Where the proposal came from, shown as a small label.</summary>
        public string SourceLabel
        {
            get
            {
                if (IsEdited)
                {
                    return "手动修改";
                }

                if (FromVerifiedCache)
                {
                    return "缓存·已校对";
                }

                if (FromCache)
                {
                    return "缓存";
                }

                return "机器翻译";
            }
        }

        public string StatusText
        {
            get
            {
                if (!IsConfirmed)
                {
                    return "跳过";
                }

                return IsEmpty ? "译为空白" : "待写入";
            }
        }

        /// <summary>Applies the user's decisions back onto a record for the write phase.</summary>
        internal TranslationRecord ToRecord()
        {
            return new TranslationRecord(
                OriginalId, OwnerId, EntityType, OriginalText, TranslatedText);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
