using System;
using System.Collections.Generic;
using System.IO;
using LlmToolkit;

namespace JeffCAD.AiAssistant
{
    /// <summary>
    /// The plugin's binding of the reusable LLM toolkit to this application: which file
    /// the configuration lives in, where diagnostics go, and the one-time migration of
    /// the legacy <c>OPENAI_*</c> keys onto their current names.
    /// </summary>
    /// <remarks>
    /// Everything here is host policy rather than toolkit behaviour, which is why it
    /// stayed behind when the transport moved into <c>LlmToolkit</c>. The toolkit
    /// deliberately does not decide a host application's settings file name or log path.
    /// </remarks>
    internal static class AppSettings
    {
        /// <summary>Settings file name, deployed next to the assembly.</summary>
        internal const string FileName = "JeffCAD.AiAssistant.settings.json";

        /// <summary>
        /// The file name used before the rename. Read on first use so an existing install
        /// keeps its configuration instead of appearing to have lost it.
        /// </summary>
        private const string LegacyFileName = "AutoCAD.AITranslate.settings.json";

        private static LlmSettingsStore _store;

        /// <summary>
        /// The shared settings store. Created on first use so the assembly location is
        /// resolved at command time rather than at type-initialisation time.
        /// </summary>
        internal static LlmSettingsStore Store
        {
            get
            {
                if (_store == null)
                {
                    var path = ResolveSettingsPath();
                    _store = new LlmSettingsStore(path, KnownKeys);
                    MigrateIfNeeded(_store, path);
                }

                return _store;
            }
        }

        internal static readonly string[] KnownKeys =
        {
            LlmSettingKeys.ApiKey,
            LlmSettingKeys.Model,
            LlmSettingKeys.BaseUrl,
            LlmSettingKeys.ApiType,
            LlmSettingKeys.SystemPrompt,
            LlmSettingKeys.TimeoutMs,
            LlmSettingKeys.Organization,
            LlmSettingKeys.Project,
            LlmSettingKeys.TargetLanguage
        };

        private static ModelPresetStore _presetStore;

        /// <summary>
        /// Preset persistence, sharing the one settings file. The built-in catalogue comes
        /// from the toolkit, which ships domestic-first defaults.
        /// </summary>
        internal static ModelPresetStore PresetStore =>
            _presetStore ?? (_presetStore = new ModelPresetStore(Store));

        /// <summary>
        /// Diagnostics sink. Writes to <c>%TEMP%</c>: the plugin's failure modes (ribbon
        /// button dead, command not registered) are invisible from a shell, so every
        /// interesting lifecycle event leaves a trace here.
        /// </summary>
        internal static ILlmDiagnostics Diagnostics { get; } =
            new FileDiagnostics(Path.Combine(Path.GetTempPath(), "JeffCAD.AiAssistant.diag.log"));

        /// <summary>Path of the diagnostic log, for messages that point the user at it.</summary>
        internal static string LogFilePath => Path.Combine(Path.GetTempPath(), "JeffCAD.AiAssistant.diag.log");

        /// <summary>Full path of the active settings file.</summary>
        internal static string SettingsFilePath => Store.FilePath;

        /// <summary>Forces the next read to reload the file from disk.</summary>
        internal static void InvalidateCache()
        {
            Store?.InvalidateCache();
        }

        /// <summary>
        /// Resolves the settings file, preferring the current name next to the assembly
        /// and falling back to the pre-rename file when it is the only one present.
        /// </summary>
        private static string ResolveSettingsPath()
        {
            var current = LlmSettingsStore.DefaultPathForEntryAssembly(FileName);

            // GetEntryAssembly() is null under NETLOAD and the fallback inside the toolkit
            // uses the *toolkit* assembly's location, which is the plugin directory too --
            // both assemblies deploy side by side -- so this is normally non-null. Guard
            // anyway: a null here would otherwise silently disable the settings UI.
            if (string.IsNullOrWhiteSpace(current))
            {
                return null;
            }

            try
            {
                var directory = Path.GetDirectoryName(current);
                var legacy = Path.Combine(directory, LegacyFileName);

                // Only adopt the legacy file when the new one does not exist yet. Once
                // the user has saved under the new name, it wins from then on.
                if (!File.Exists(current) && File.Exists(legacy))
                {
                    Diagnostics.Log($"settings: adopting legacy file {LegacyFileName}");
                    return legacy;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException)
            {
                // Fall through to the current name; a failed probe must not stop startup.
            }

            return current;
        }

        /// <summary>
        /// Carries legacy <c>OPENAI_*</c> keys forward.
        /// </summary>
        /// <remarks>
        /// Reads the old names into memory first, then writes them under the new names.
        /// The write is what makes the migration stick, and it is skipped entirely when
        /// nothing needed migrating, so an already-migrated install pays no file I/O on
        /// every command.
        /// </remarks>
        private static void MigrateIfNeeded(LlmSettingsStore store, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            var migrated = LlmSettingKeys.MigrateLegacyKeys(store);
            if (migrated.Count == 0)
            {
                return;
            }

            if (store.Write(migrated))
            {
                Diagnostics.Log($"settings: migrated {migrated.Count} legacy OPENAI_* key(s)");
            }
        }
    }
}
