using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// One row in the provider list. Wraps a <see cref="ModelPreset"/> and adds the
    /// presentation-only bits the list needs (status dot, built-in tag, unsaved marker)
    /// so the underlying preset stays a pure data object.
    /// </summary>
    internal sealed class PresetRow
    {
        /// <summary>Line drawn after the last built-in row, separating it from user rows.</summary>
        internal const string UserGroupHeader = "自定义供应商";

        private static readonly Brush ReadyDot = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
        private static readonly Brush DraftDot = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1B)));
        private static readonly Brush IncompleteDot = Freeze(new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)));

        private PresetRow() { }

        public ModelPreset Preset { get; private set; }

        public string DisplayName { get; private set; }

        /// <summary>True for a synthetic group-header row rather than a real provider.</summary>
        public bool IsGroupHeader { get; private set; }

        /// <summary>False only for the trailing "new provider" draft row.</summary>
        public bool IsSelectable => !IsGroupHeader;

        /// <summary>
        /// Green when the provider looks usable, amber while the user is editing it in
        /// the detail pane, grey when a required field is still missing. This is what
        /// the coloured dot in each row renders from.
        /// </summary>
        public Brush StatusBrush { get; private set; }

        public Visibility BuiltInTagVisibility =>
            Preset != null && Preset.BuiltIn ? Visibility.Visible : Visibility.Collapsed;

        internal static PresetRow ForPreset(ModelPreset preset, string displayName, bool isDraft)
        {
            var row = new PresetRow
            {
                Preset = preset,
                DisplayName = displayName,
                IsGroupHeader = false
            };
            row.StatusBrush = ResolveBrush(preset, isDraft);
            return row;
        }

        internal static PresetRow ForGroupHeader(string title)
        {
            return new PresetRow
            {
                Preset = null,
                DisplayName = title,
                IsGroupHeader = true,
                StatusBrush = IncompleteDot
            };
        }

        /// <summary>Re-evaluates the dot after the user edits the detail pane.</summary>
        internal void RefreshStatus(bool isDraft)
        {
            StatusBrush = ResolveBrush(Preset, isDraft);
        }

        private static Brush ResolveBrush(ModelPreset preset, bool isDraft)
        {
            if (preset == null)
            {
                return IncompleteDot;
            }

            if (isDraft)
            {
                return DraftDot;
            }

            return !string.IsNullOrWhiteSpace(preset.Model) &&
                   !string.IsNullOrWhiteSpace(preset.BaseUrl)
                ? ReadyDot
                : IncompleteDot;
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Model configuration dialog.
    /// </summary>
    /// <remarks>
    /// Laid out as a provider list on the left and a parameter form on the right, so
    /// the user can see every backend they have configured while editing one of them.
    /// Selecting a row loads it into the form; <see cref="CollectValues"/> then reads
    /// the LIVE form, so the fields -- not the selected row -- are what gets saved as
    /// the running configuration. That means a preset can be tried with a different
    /// model for one run without mutating the saved preset.
    ///
    /// Edits the same key/value store the command line path reads (environment
    /// variables still take precedence over the file for the active configuration).
    /// </remarks>
    public partial class SettingsDialog : Window
    {
        /// <summary>Keys overridden by environment variables; the file cannot win for these.</summary>
        private static readonly string[] EnvOverriddenKeys =
        {
            "OPENAI_API_KEY",
            "OPENAI_MODEL",
            "OPENAI_BASE_URL",
            "OPENAI_API_TYPE",
            "OPENAI_SYSTEM_PROMPT",
            "OPENAI_TIMEOUT_MS",
            "OPENAI_ORG",
            "OPENAI_PROJECT"
        };

        /// <summary>Name carried by the trailing draft row that creates a new provider.</summary>
        private const string NewProviderLabel = "＋ 新增供应商…";

        /// <summary>Working copy of the preset list for this dialog session.</summary>
        private List<ModelPreset> _presets;

        /// <summary>Set while the draft row is selected: a provider that does not exist yet.</summary>
        private bool _isNewProviderDraft;

        /// <summary>
        /// Suppresses <see cref="PresetList_SelectionChanged"/> while the code is
        /// rebuilding the list, so repopulating it does not look like a user click and
        /// overwrite fields the user just typed.
        /// </summary>
        private bool _suppressListEvents;

        /// <summary>
        /// The client used by "test connection", kept between clicks so its in-memory
        /// endpoint cache survives. Rebuilding it on every click threw that cache away
        /// and forced a fresh /responses probe each time. Invalidated whenever the
        /// connection parameters change.
        /// </summary>
        private OpenAiClient _testClient;

        /// <summary>Signature of the parameters <see cref="_testClient"/> was built from.</summary>
        private string _testClientSignature;

        public SettingsDialog()
        {
            InitializeComponent();

            _presets = PresetStore.Load();
            LoadFromSettings();

            RebuildProviderList(selectName: Settings.Read(PresetStore.ActivePresetKey));
            RefreshPresetButtons();

            EnvWarningText.Text = DescribeEnvOverrides();
            EnvWarningText.Visibility = string.IsNullOrEmpty(EnvWarningText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Load / collect
        // ------------------------------------------------------------------

        private void LoadFromSettings()
        {
            ApiKeyBox.Password = Settings.Read("OPENAI_API_KEY") ?? string.Empty;
            ApiKeyPlain.Text = ApiKeyBox.Password;
            ModelBox.Text = Settings.Read("OPENAI_MODEL") ?? "gpt-4.1";
            BaseUrlBox.Text = Settings.Read("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";
            TimeoutBox.Text = (Settings.ReadInt("OPENAI_TIMEOUT_MS") ?? 60000).ToString();
            OrgBox.Text = Settings.Read("OPENAI_ORG") ?? string.Empty;
            ProjectBox.Text = Settings.Read("OPENAI_PROJECT") ?? string.Empty;
            SystemPromptBox.Text = Settings.Read("OPENAI_SYSTEM_PROMPT") ?? string.Empty;

            SelectApiType(Settings.Read("OPENAI_API_TYPE"));

            // Offer every distinct model / base URL from the preset list as a
            // type-ahead suggestion. The plugin no longer ships a fixed list, so
            // these grow with whatever the user saves.
            ModelBox.ItemsSource = _presets
                .Select(p => p.Model)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            BaseUrlBox.ItemsSource = _presets
                .Select(p => p.BaseUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            UpdateStatusHelp();
        }

        private Dictionary<string, string> CollectValues()
        {
            var timeout = ParseTimeout();
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OPENAI_API_KEY"] = ApiKeyBox.Password.Trim(),
                ["OPENAI_MODEL"] = (ModelBox.Text ?? string.Empty).Trim(),
                ["OPENAI_BASE_URL"] = (BaseUrlBox.Text ?? string.Empty).Trim(),
                ["OPENAI_API_TYPE"] = SelectedApiType(),
                ["OPENAI_SYSTEM_PROMPT"] = SystemPromptBox.Text.Trim(),
                ["OPENAI_TIMEOUT_MS"] = timeout > 0 ? timeout.ToString() : string.Empty,
                ["OPENAI_ORG"] = OrgBox.Text.Trim(),
                ["OPENAI_PROJECT"] = ProjectBox.Text.Trim()
            };
        }

        /// <summary>Builds a preset from the values currently in the form.</summary>
        private ModelPreset CurrentFormAsPreset(string name)
        {
            var timeout = ParseTimeout();
            return new ModelPreset
            {
                Name = name ?? string.Empty,
                Model = (ModelBox.Text ?? string.Empty).Trim(),
                BaseUrl = (BaseUrlBox.Text ?? string.Empty).Trim(),
                ApiType = SelectedApiType(),
                TimeoutMs = timeout > 0 ? timeout : (int?)null,
                SystemPrompt = SystemPromptBox.Text.Trim(),
                Organization = OrgBox.Text.Trim(),
                Project = ProjectBox.Text.Trim(),
                BuiltIn = false
            };
        }

        /// <summary>
        /// Copies whatever is in the form onto the currently selected user preset.
        /// Built-ins and the draft row are skipped: the former are re-seeded from code
        /// on every load, so in-place edits would vanish, and the latter has no entry yet.
        /// </summary>
        private bool UpdateSelectedPresetFromForm()
        {
            var selected = SelectedPreset;
            if (selected == null || selected.BuiltIn || _isNewProviderDraft)
            {
                return false;
            }

            var form = CurrentFormAsPreset(selected.Name);
            if (selected.Model == form.Model &&
                selected.BaseUrl == form.BaseUrl &&
                selected.ApiType == form.ApiType &&
                selected.TimeoutMs == form.TimeoutMs &&
                selected.SystemPrompt == form.SystemPrompt &&
                selected.Organization == form.Organization &&
                selected.Project == form.Project)
            {
                return false;
            }

            selected.Model = form.Model;
            selected.BaseUrl = form.BaseUrl;
            selected.ApiType = form.ApiType;
            selected.TimeoutMs = form.TimeoutMs;
            selected.SystemPrompt = form.SystemPrompt;
            selected.Organization = form.Organization;
            selected.Project = form.Project;
            return true;
        }

        private string SelectedApiType()
        {
            if (ApiTypeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag;
            }

            return "auto";
        }

        private void SelectApiType(string apiType)
        {
            switch (OpenAiClient.NormalizeApiType(apiType))
            {
                case ApiType.Responses:
                    ApiTypeBox.SelectedIndex = 1;
                    break;
                case ApiType.ChatCompletions:
                    ApiTypeBox.SelectedIndex = 2;
                    break;
                default:
                    ApiTypeBox.SelectedIndex = 0;
                    break;
            }
        }

        private int ParseTimeout()
        {
            return int.TryParse(TimeoutBox.Text.Trim(), out var value) && value > 0 ? value : 0;
        }

        // ------------------------------------------------------------------
        // Provider list
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the provider list. Every entry loads immediately -- there is no
        /// separate "apply" step -- so the list is rebuilt only on open and after a
        /// structural change (add / rename / delete / reload).
        /// </summary>
        private void RebuildProviderList(string selectName)
        {
            var rows = new List<PresetRow>();
            var builtIns = _presets.Where(p => p.BuiltIn).ToList();
            var userPresets = _presets.Where(p => !p.BuiltIn).ToList();

            foreach (var preset in builtIns)
            {
                rows.Add(PresetRow.ForPreset(preset, preset.Name, isDraft: false));
            }

            if (userPresets.Count > 0)
            {
                rows.Add(PresetRow.ForGroupHeader(PresetRow.UserGroupHeader));
                foreach (var preset in userPresets)
                {
                    rows.Add(PresetRow.ForPreset(preset, preset.Name, isDraft: false));
                }
            }

            // Trailing draft row: select it to start defining a new provider.
            var draft = new ModelPreset
            {
                Name = string.Empty,
                Model = (ModelBox.Text ?? string.Empty).Trim(),
                BaseUrl = (BaseUrlBox.Text ?? string.Empty).Trim(),
                ApiType = SelectedApiType(),
                TimeoutMs = ParseTimeout() > 0 ? ParseTimeout() : (int?)null,
                BuiltIn = false
            };
            rows.Add(PresetRow.ForPreset(draft, NewProviderLabel, isDraft: true));

            _suppressListEvents = true;
            try
            {
                PresetList.ItemsSource = null;
                PresetList.ItemsSource = rows;

                var match = PresetStore.FindByName(_presets, selectName);
                var target = match != null
                    ? rows.FirstOrDefault(r => ReferenceEquals(r.Preset, match))
                    : null;
                PresetList.SelectedItem = target;
            }
            finally
            {
                _suppressListEvents = false;
            }

            RefreshDetailHeader();
        }

        private PresetRow SelectedRow => PresetList.SelectedItem as PresetRow;

        private ModelPreset SelectedPreset => SelectedRow?.Preset;

        /// <summary>
        /// Enables or disables the management buttons for the current selection.
        /// A built-in preset can be renamed into a user preset but not deleted, so
        /// the list can never be emptied of its fallback entries.
        /// </summary>
        private void RefreshPresetButtons()
        {
            var selected = SelectedPreset;
            RenameButton.IsEnabled = selected != null;
            DeleteButton.IsEnabled = selected != null && !selected.BuiltIn;
        }

        /// <summary>Updates the heading above the detail form.</summary>
        private void RefreshDetailHeader()
        {
            if (_isNewProviderDraft)
            {
                DetailTitle.Text = "新增供应商";
                DetailSubtitle.Text = "填写参数后点「保存」，会新建一个供应商方案。";
                return;
            }

            var selected = SelectedPreset;
            if (selected == null)
            {
                DetailTitle.Text = "未选择供应商";
                DetailSubtitle.Text = "从左侧选择一个供应商，或新增一个。";
                return;
            }

            DetailTitle.Text = selected.Name;
            DetailSubtitle.Text = selected.BuiltIn
                ? "内置供应商，不可删除；改名或编辑后会另存为你自己的方案。"
                : "保存时会把当前参数写回这个供应商。";
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressListEvents)
            {
                return;
            }

            var row = SelectedRow;

            // Group headers are not selectable targets; skip straight back over them.
            if (row != null && row.IsGroupHeader)
            {
                return;
            }

            _isNewProviderDraft = row != null && row.Preset != null &&
                                  !row.Preset.BuiltIn &&
                                  string.IsNullOrEmpty(row.Preset.Name);

            RefreshPresetButtons();
            RefreshDetailHeader();

            if (row?.Preset == null)
            {
                return;
            }

            ApplyPresetToForm(row.Preset);
        }

        /// <summary>
        /// Copies a preset's connection parameters into the form. The API key is
        /// deliberately left alone: keys are per-account rather than per-endpoint, and
        /// silently clearing one on a list click would be hostile.
        /// </summary>
        private void ApplyPresetToForm(ModelPreset preset)
        {
            ModelBox.Text = preset.Model ?? string.Empty;
            BaseUrlBox.Text = preset.BaseUrl ?? string.Empty;
            SelectApiType(preset.ApiType);

            // Clear rather than keep the previous provider's value: leaving another
            // vendor's timeout or prompt visible here reads as if it belonged to this one.
            TimeoutBox.Text = preset.TimeoutMs.HasValue && preset.TimeoutMs.Value > 0
                ? preset.TimeoutMs.Value.ToString()
                : string.Empty;

            SystemPromptBox.Text = preset.SystemPrompt ?? string.Empty;
            OrgBox.Text = preset.Organization ?? string.Empty;
            ProjectBox.Text = preset.Project ?? string.Empty;

            StatusText.Text = _isNewProviderDraft
                ? "填写参数后点「保存」新增供应商。"
                : $"已载入供应商「{preset.Name}」。";
        }

        /// <summary>Asks for a name, defaulting to a suggestion the user can accept.</summary>
        private string PromptForPresetName(string title, string defaultValue)
        {
            var dialog = new PresetNameDialog(title, defaultValue)
            {
                Owner = this
            };

            return dialog.ShowDialog() == true ? dialog.PresetName : null;
        }

        // ------------------------------------------------------------------
        // Preset management handlers
        // ------------------------------------------------------------------

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            _presets = PresetStore.Load();
            LoadFromSettings();
            RebuildProviderList(selectName: Settings.Read(PresetStore.ActivePresetKey));
            RefreshPresetButtons();
            StatusText.Text = "✓ 已重新载入方案列表，未保存的修改已丢弃。";
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // Select the trailing draft row rather than saving straight away, so the
            // new provider is created deliberately instead of by a stray click.
            var rows = PresetList.ItemsSource as List<PresetRow>;
            var draftRow = rows?.LastOrDefault(r => r != null && !r.IsGroupHeader &&
                                                     r.Preset != null &&
                                                     string.IsNullOrEmpty(r.Preset.Name));
            if (draftRow == null)
            {
                RebuildProviderList(selectName: null);
                rows = PresetList.ItemsSource as List<PresetRow>;
                draftRow = rows?.LastOrDefault(r => r != null && !r.IsGroupHeader &&
                                                     r.Preset != null &&
                                                     string.IsNullOrEmpty(r.Preset.Name));
            }

            if (draftRow != null)
            {
                PresetList.SelectedItem = draftRow;
                ModelBox.Focus();
                StatusText.Text = "填写参数后点「保存」新增供应商，或点「＋ 添加供应商」新增。";
            }
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedPreset;
            if (selected == null)
            {
                StatusText.Text = "请先选择一个供应商。";
                return;
            }

            var name = PromptForPresetName("重命名供应商", selected.Name);
            if (name == null)
            {
                return;
            }

            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusText.Text = "✗ 供应商名称不能为空。";
                return;
            }

            var clash = PresetStore.FindByName(_presets, name);
            if (clash != null && !ReferenceEquals(clash, selected))
            {
                StatusText.Text = $"✗ 已存在名为「{name}」的供应商，请换一个名称。";
                return;
            }

            // Renaming a built-in forks it into a user preset. Editing the shipped
            // entry in place would be undone on the next load, which is more
            // surprising than turning it into the user's own copy.
            if (selected.BuiltIn)
            {
                var fork = selected.Clone();
                fork.Name = name;
                fork.BuiltIn = false;
                _presets.Add(fork);

                if (!PresetStore.Save(_presets, name))
                {
                    _presets.Remove(fork);
                    StatusText.Text = "✗ 保存失败，重命名未生效。";
                    return;
                }

                RebuildProviderList(selectName: name);
                RefreshPresetButtons();
                StatusText.Text = $"✓ 内置供应商已另存为「{name}」，原内置项保留。";
                return;
            }

            var previousName = selected.Name;
            selected.Name = name;

            if (!PresetStore.Save(_presets, name))
            {
                selected.Name = previousName;
                StatusText.Text = "✗ 保存失败，重命名未生效。";
                return;
            }

            RebuildProviderList(selectName: name);
            RefreshPresetButtons();
            StatusText.Text = $"✓ 已重命名为「{name}」。";
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedPreset;
            if (selected == null)
            {
                StatusText.Text = "请先选择一个供应商。";
                return;
            }

            if (selected.BuiltIn)
            {
                StatusText.Text = "内置供应商不可删除；可以先「重命名」成自己的方案再删除。";
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"确定要删除供应商「{selected.Name}」吗？此操作不可撤销。",
                "删除供应商",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var index = _presets.IndexOf(selected);
            _presets.Remove(selected);

            // Fall back to a neighbouring preset so the dialog never ends up with an
            // empty selection after a delete.
            var nextName = _presets.Count > 0
                ? _presets[Math.Min(index, _presets.Count - 1)].Name
                : null;

            if (!PresetStore.Save(_presets, nextName))
            {
                _presets.Insert(Math.Min(index, _presets.Count), selected);
                StatusText.Text = "✗ 删除失败：无法写入配置文件。";
                return;
            }

            RebuildProviderList(selectName: nextName);
            RefreshPresetButtons();
            StatusText.Text = $"✓ 已删除供应商「{selected.Name}」。";
        }

        // ------------------------------------------------------------------
        // Event handlers
        // ------------------------------------------------------------------

        private void ShowKeyToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ApiKeyPlain == null || ApiKeyBox == null)
            {
                return; // XAML not fully initialized yet
            }

            if (ShowKeyToggle.IsChecked == true)
            {
                ApiKeyPlain.Text = ApiKeyBox.Password;
                ApiKeyBox.Visibility = Visibility.Collapsed;
                ApiKeyPlain.Visibility = Visibility.Visible;
            }
            else
            {
                ApiKeyBox.Password = ApiKeyPlain.Text;
                ApiKeyPlain.Visibility = Visibility.Collapsed;
                ApiKeyBox.Visibility = Visibility.Visible;
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var values = CollectValues();
            if (string.IsNullOrWhiteSpace(values["OPENAI_API_KEY"]))
            {
                StatusText.Text = "✗ 请先填写 API Key。";
                return;
            }

            if (string.IsNullOrWhiteSpace(values["OPENAI_BASE_URL"]))
            {
                StatusText.Text = "✗ 请先填写接口地址。";
                return;
            }

            var timeout = ParseTimeout() > 0 ? ParseTimeout() : 60000;
            var client = GetOrCreateTestClient(values, timeout);

            TestButton.IsEnabled = false;
            StatusText.Text = $"正在连接 {client.BaseUrl}（模型 {client.Model}）...";

            try
            {
                var reply = await Task.Run(() => client.TranslateSingle("你好，世界。"));
                StatusText.Text = $"✓ 连接成功。模型回复：{OpenAiClient.Truncate(reply, 120)}";
            }
            catch (Exception ex)
            {
                // A failed test invalidates the cached client so the next click
                // re-reads the (possibly corrected) settings and re-probes.
                _testClient = null;
                _testClientSignature = null;
                StatusText.Text = $"✗ 连接失败：{OpenAiClient.Truncate(ex.Message, 260)}";
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Returns a client for the current form values, reusing the previous one when
        /// nothing relevant changed. Reuse matters because the client carries the
        /// resolved endpoint in memory; rebuilding it on every click would re-probe
        /// the gateway each time.
        /// </summary>
        private OpenAiClient GetOrCreateTestClient(Dictionary<string, string> values, int timeout)
        {
            var signature = string.Join("\u001f", new[]
            {
                values["OPENAI_API_KEY"],
                values["OPENAI_MODEL"],
                values["OPENAI_BASE_URL"],
                values["OPENAI_ORG"],
                values["OPENAI_PROJECT"],
                values["OPENAI_API_TYPE"],
                values["OPENAI_SYSTEM_PROMPT"],
                timeout.ToString()
            });

            if (_testClient != null &&
                string.Equals(signature, _testClientSignature, StringComparison.Ordinal))
            {
                return _testClient;
            }

            _testClient = new OpenAiClient(
                values["OPENAI_API_KEY"],
                values["OPENAI_MODEL"],
                values["OPENAI_BASE_URL"],
                values["OPENAI_ORG"],
                values["OPENAI_PROJECT"],
                values["OPENAI_API_TYPE"],
                values["OPENAI_SYSTEM_PROMPT"],
                timeout);

            _testClientSignature = signature;
            return _testClient;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var values = CollectValues();

            if (string.IsNullOrWhiteSpace(values["OPENAI_API_KEY"]))
            {
                var confirm = MessageBox.Show(
                    this,
                    "API Key 为空，翻译功能将无法使用。仍要保存吗？",
                    "模型设置",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(values["OPENAI_BASE_URL"]))
            {
                StatusText.Text = "✗ 接口地址不能为空。";
                return;
            }

            // Decide what the current form means before touching the preset list.
            var report = PersistFormIntoTargetPreset();

            // Remember which provider the current values came from, so reopening the
            // dialog lands on the same list entry.
            var activeName = SelectedPreset != null && !string.IsNullOrWhiteSpace(SelectedPreset.Name)
                ? SelectedPreset.Name
                : Settings.Read(PresetStore.ActivePresetKey);

            var merged = new List<KeyValuePair<string, string>>(values);
            if (!string.IsNullOrWhiteSpace(activeName))
            {
                merged.Add(new KeyValuePair<string, string>(PresetStore.ActivePresetKey, activeName));
            }

            if (!Settings.Write(merged))
            {
                StatusText.Text =
                    $"✗ 保存失败：无法写入 {Settings.SettingsFilePath ?? "配置文件"}。" +
                    "请检查文件是否被其他程序占用，或以管理员身份运行 AutoCAD。";
                return;
            }

            if (report != null)
            {
                StatusText.Text = report;
            }

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Writes the live form back into the selected provider when that is what the
        /// user meant, or creates a new provider from the form. Returns a message to
        /// show when something noteworthy happened (created, renamed, save failed),
        /// otherwise null.
        /// </summary>
        private string PersistFormIntoTargetPreset()
        {
            var name = (SelectedPreset?.Name ?? string.Empty).Trim();
            var isLiveEdit = !_isNewProviderDraft && !string.IsNullOrWhiteSpace(name) &&
                             SelectedPreset != null && !SelectedPreset.BuiltIn;

            if (isLiveEdit)
            {
                // Existing user provider: the form is the new definition of it.
                UpdateSelectedPresetFromForm();
                if (!PresetStore.Save(_presets, name))
                {
                    return $"✗ 供应商「{name}」写入失败，其余参数已保存。";
                }

                return $"✓ 已更新供应商「{name}」。";
            }

            if (SelectedPreset != null && SelectedPreset.BuiltIn)
            {
                // A built-in can never be written to, so the edits fork off as a copy.
                var forkName = MakeUniqueName(name);
                var fork = CurrentFormAsPreset(forkName);
                _presets.Add(fork);

                if (!PresetStore.Save(_presets, forkName))
                {
                    _presets.Remove(fork);
                    return "✗ 供应商写入失败，参数已保存为当前配置。";
                }

                RebuildProviderList(selectName: forkName);
                RefreshPresetButtons();
                return $"✓ 内置供应商不可修改，已另存为「{forkName}」。";
            }

            // Draft row, or nothing selected: ask whether to keep this as a provider.
            var answer = MessageBox.Show(
                this,
                "是否把当前参数保存为一个新的供应商？\n\n" +
                "选择「否」则只把它设为当前使用的配置，不进入左侧列表。",
                "保存供应商",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return null;
            }

            var suggested = string.IsNullOrWhiteSpace(name)
                ? SuggestNameFromUrl()
                : name;

            var chosen = PromptForPresetName("新增供应商", MakeUniqueName(suggested));
            if (chosen == null)
            {
                return null;
            }

            chosen = chosen.Trim();
            var error = PresetStore.Validate(chosen, BaseUrlBox.Text, ParseTimeout() > 0 ? ParseTimeout() : (int?)null);
            if (error != null)
            {
                return "✗ " + error;
            }

            if (PresetStore.FindByName(_presets, chosen) != null)
            {
                return $"✗ 已存在名为「{chosen}」的供应商，请换一个名称。";
            }

            var preset = CurrentFormAsPreset(chosen);
            _presets.Add(preset);

            if (!PresetStore.Save(_presets, chosen))
            {
                _presets.Remove(preset);
                return $"✗ 保存失败：无法写入 {Settings.SettingsFilePath ?? "配置文件"}。";
            }

            RebuildProviderList(selectName: chosen);
            RefreshPresetButtons();
            return $"✓ 已新增供应商「{chosen}」。";
        }

        /// <summary>Turns a base URL into a readable default provider name.</summary>
        private string SuggestNameFromUrl()
        {
            var raw = (BaseUrlBox.Text ?? string.Empty).Trim();
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            return "新供应商";
        }

        /// <summary>Appends a numeric suffix until the name is free.</summary>
        private string MakeUniqueName(string baseName)
        {
            var name = string.IsNullOrWhiteSpace(baseName) ? "新供应商" : baseName.Trim();
            if (PresetStore.FindByName(_presets, name) == null)
            {
                return name;
            }

            for (var i = 2; i < 1000; i++)
            {
                var candidate = $"{name} ({i})";
                if (PresetStore.FindByName(_presets, candidate) == null)
                {
                    return candidate;
                }
            }

            return name;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void UpdateStatusHelp()
        {
            var path = Settings.SettingsFilePath;
            StatusText.Text = string.IsNullOrWhiteSpace(path)
                ? "配置将保存到插件目录下的 AutoCAD.AITranslate.settings.json。"
                : $"配置保存到：{path}";
        }

        private static string DescribeEnvOverrides()
        {
            var overridden = EnvOverriddenKeys
                .Where(key => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                .ToList();

            if (overridden.Count == 0)
            {
                return string.Empty;
            }

            return "注意：以下环境变量已设置，优先级高于本窗口保存的值：" +
                   string.Join("、", overridden);
        }
    }
}
