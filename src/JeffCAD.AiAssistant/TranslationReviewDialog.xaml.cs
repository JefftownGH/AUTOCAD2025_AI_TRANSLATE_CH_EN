using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace JeffCAD.AiAssistant
{
    /// <summary>What the user decided in the review dialog.</summary>
    public sealed class ReviewOutcome
    {
        public bool Accepted { get; set; }

        /// <summary>The language the user settled on, which may differ from the initial one.</summary>
        public TargetLanguage Language { get; set; }

        /// <summary>How the accepted rows should be written to the drawing.</summary>
        public TranslationMode Mode { get; set; } = TranslationMode.NewLayer;

        /// <summary>Only the rows the user left ticked.</summary>
        public List<TranslationRow> ConfirmedRows { get; set; } = new List<TranslationRow>();

        /// <summary>Rows whose translation text the user typed or edited.</summary>
        public List<TranslationRow> EditedRows { get; set; } = new List<TranslationRow>();
    }

    /// <summary>
    /// The bilingual review surface: pick a language, read original next to translation,
    /// correct the translation inline, tick off what should be written.
    /// </summary>
    /// <remarks>
    /// The dialog owns no AutoCAD state and performs no drawing work. It receives rows that
    /// were assembled during a read-only scan and returns the user's decisions, so all
    /// document mutation stays in the single short write transaction in
    /// <see cref="TranslationCommands"/>.
    ///
    /// Language changes are reported back through <see cref="LanguageChanged"/> rather than
    /// acted on locally, because retranslating needs the network client and a scan list that
    /// only the command layer holds.
    ///
    /// Declared <c>public</c> because the XAML-generated partial is public: the two halves
    /// must agree, and the generated half is not editable.
    /// </remarks>
    public partial class TranslationReviewDialog : Window
    {
        private readonly ObservableCollection<TranslationRow> _rows;
        private bool _suppressLanguageEvent;

        public TranslationReviewDialog(
            IReadOnlyList<TranslationRow> rows,
            TargetLanguage initialLanguage,
            TranslationMode initialMode,
            int itemCount,
            string modelName)
        {
            InitializeComponent();

            _rows = new ObservableCollection<TranslationRow>(rows ?? new List<TranslationRow>());
            RowList.ItemsSource = _rows;

            _suppressLanguageEvent = true;
            LanguageBox.ItemsSource = TargetLanguages.All;
            LanguageBox.SelectedItem = initialLanguage ?? TargetLanguages.ByCode(TargetLanguages.DefaultCode);
            _suppressLanguageEvent = false;

            ModeBilingual.IsChecked = initialMode != TranslationMode.Replace;
            ModeReplace.IsChecked = initialMode == TranslationMode.Replace;

            HeaderText.Text = $"翻译预览 — 共 {itemCount} 处文本";
            HeaderSubText.Text =
                $"模型：{modelName}。可直接编辑译文；取消勾选的行不会被写入图纸。" +
                "修改过的译文会被记住，下次翻译优先使用。";

            foreach (var row in _rows)
            {
                row.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TranslationRow.IsConfirmed) ||
                        e.PropertyName == nameof(TranslationRow.TranslatedText))
                    {
                        UpdateSummary();
                    }
                };
            }

            UpdateSummary();
        }

        /// <summary>Raised when the user picks a different target language.</summary>
        public event EventHandler<TargetLanguage> LanguageChanged;

        /// <summary>The language currently selected in the picker.</summary>
        public TargetLanguage SelectedLanguage =>
            LanguageBox.SelectedItem as TargetLanguage ?? TargetLanguages.ByCode(TargetLanguages.DefaultCode);

        /// <summary>Replaces the row set after a retranslation, keeping the list in sync.</summary>
        public void ReplaceRows(IEnumerable<TranslationRow> rows)
        {
            _rows.Clear();
            foreach (var row in rows ?? Enumerable.Empty<TranslationRow>())
            {
                _rows.Add(row);
                row.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TranslationRow.IsConfirmed) ||
                        e.PropertyName == nameof(TranslationRow.TranslatedText))
                    {
                        UpdateSummary();
                    }
                };
            }

            UpdateSummary();
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLanguageEvent)
            {
                return;
            }

            LanguageChanged?.Invoke(this, SelectedLanguage);
        }

        private void WriteMode_Changed(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent before the controls exist; guard on the
            // fields being wired rather than on sender, which can arrive first.
            if (RowList == null)
            {
                return;
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                row.IsConfirmed = true;
            }
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                row.IsConfirmed = false;
            }
        }

        private void Retranslate_Click(object sender, RoutedEventArgs e)
        {
            // Rows the user has not signed off on are the ones worth another attempt.
            var targets = _rows.Where(r => !r.IsConfirmed || r.IsEmpty).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(this,
                    "没有需要重译的行。只有未勾选或译文为空的项会被重译。",
                    "重译", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            RetranslateRequested?.Invoke(this, targets);
        }

        /// <summary>Raised when the user asks for the unconfirmed rows to be retranslated.</summary>
        public event EventHandler<List<TranslationRow>> RetranslateRequested;

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = _rows.Where(r => r.IsConfirmed).ToList();
            if (confirmed.Count == 0)
            {
                var answer = MessageBox.Show(this,
                    "没有任何行被勾选，写入图纸不会产生改动。仍要关闭吗？",
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            var blank = confirmed.Where(r => r.IsEmpty).ToList();
            if (blank.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    $"有 {blank.Count} 行译文为空。这些文本在图纸中会被清空。是否继续？",
                    "译文为空", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            Outcome = new ReviewOutcome
            {
                Accepted = true,
                Language = SelectedLanguage,
                Mode = ModeReplace.IsChecked == true ? TranslationMode.Replace : TranslationMode.NewLayer,
                ConfirmedRows = confirmed,
                EditedRows = confirmed.Where(r => r.IsEdited).ToList()
            };

            DialogResult = true;
            Close();
        }

        /// <summary>The user's decisions. Null until the dialog is accepted.</summary>
        public ReviewOutcome Outcome { get; private set; }

        private void UpdateSummary()
        {
            var total = _rows.Count;
            var confirmed = _rows.Count(r => r.IsConfirmed);
            var edited = _rows.Count(r => r.IsEdited);
            var fromCache = _rows.Count(r => r.FromCache);
            var blank = _rows.Count(r => r.IsConfirmed && r.IsEmpty);

            SummaryText.Text = $"已选 {confirmed} / {total} 行写入";

            var parts = new List<string>();
            if (edited > 0)
            {
                parts.Add($"手动修改 {edited} 行（将记住并优先复用）");
            }

            if (fromCache > 0)
            {
                parts.Add($"缓存命中 {fromCache} 行");
            }

            if (blank > 0)
            {
                parts.Add($"{blank} 行译文为空");
            }

            SummarySubText.Text = parts.Count > 0
                ? string.Join("；", parts)
                : "未做修改。";
        }
    }
}
