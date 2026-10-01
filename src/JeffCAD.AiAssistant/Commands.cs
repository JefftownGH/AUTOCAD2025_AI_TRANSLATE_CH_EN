using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Registers the command class with AutoCAD. Without this, AutoCAD has to reflect over
// every type in every loaded assembly to discover [CommandMethod] members; declaring it
// here lets the command set be resolved directly at load time.
[assembly: CommandClass(typeof(JeffCAD.AiAssistant.Commands))]

namespace JeffCAD.AiAssistant
{
    /// <summary>
    /// Command-line entry points and the assembly's extension-application hook.
    /// </summary>
    /// <remarks>
    /// This class deliberately contains no logic beyond argument handing. The real work
    /// lives in <see cref="TranslationCommands"/>, which is also what the Ribbon buttons
    /// call. Keeping one implementation means a command-line run and a button click cannot
    /// drift apart, and it lets the Ribbon bypass the command engine entirely.
    /// </remarks>
    public class Commands : IExtensionApplication
    {
        // ------------------------------------------------------------------
        // IExtensionApplication
        // ------------------------------------------------------------------

        public void Initialize()
        {
            var version = typeof(Commands).Assembly.GetName().Version;
            AppSettings.Diagnostics.Log($"plugin Initialize, v{version}");

            RibbonSetup.Initialize();

            var editor = GetEditor();
            editor?.WriteMessage(
                $"\nJeffCAD AI Assistant v{version} loaded. Model target: {DetectAutoCadVersion()}." +
                "\nRibbon: \"AI 翻译\" tab." +
                "\nTarget language is chosen in the review dialog that appears on every run." +
                "\nCommands: AI_TRANSLATE_ZH2EN, AI_TRANSLATE_ZH2EN_SEL, AI_TRANSLATE_ROLLBACK, " +
                "AI_TRANSLATE_SETTINGS, AI_TRANSLATE_CLEAR_CACHE, AI_TRANSLATE_SAVE_CACHE.");
        }

        public void Terminate()
        {
            // No timers or handlers survive the plugin beyond the ones RibbonSetup detaches
            // on its own Idle callbacks, so there is nothing to unwind here.
        }

        // ------------------------------------------------------------------
        // Command-line surface (thin adapters)
        // ------------------------------------------------------------------

        /// <summary>Translates every Chinese text entity in ModelSpace and PaperSpace.</summary>
        [CommandMethod("AI_TRANSLATE_ZH2EN", CommandFlags.Modal)]
        public void TranslateZhToEn()
        {
            AppSettings.Diagnostics.Log("command AI_TRANSLATE_ZH2EN executed");
            TranslationCommands.TranslateAll();
        }

        /// <summary>Translates only the current selection.</summary>
        [CommandMethod("AI_TRANSLATE_ZH2EN_SEL", CommandFlags.Modal)]
        public void TranslateZhToEnSelection()
        {
            AppSettings.Diagnostics.Log("command AI_TRANSLATE_ZH2EN_SEL executed");
            TranslationCommands.TranslateSelection();
        }

        /// <summary>Rolls back the most recent translation in the active drawing.</summary>
        [CommandMethod("AI_TRANSLATE_ROLLBACK", CommandFlags.Modal)]
        public void RollbackLastTranslation()
        {
            AppSettings.Diagnostics.Log("command AI_TRANSLATE_ROLLBACK executed");
            TranslationCommands.Rollback();
        }

        /// <summary>Opens the model-configuration dialog.</summary>
        [CommandMethod("AI_TRANSLATE_SETTINGS", CommandFlags.Modal)]
        public void OpenModelSettings()
        {
            AppSettings.Diagnostics.Log("command AI_TRANSLATE_SETTINGS executed");
            TranslationCommands.OpenSettings();
        }

        /// <summary>Empties both the in-session and persistent translation caches.</summary>
        [CommandMethod("AI_TRANSLATE_CLEAR_CACHE", CommandFlags.Modal)]
        public void ClearTranslationCache()
        {
            AppSettings.Diagnostics.Log("command AI_TRANSLATE_CLEAR_CACHE executed");
            TranslationCommands.ClearCache();
        }

        /// <summary>Flushes the persistent translation memory to disk.</summary>
        [CommandMethod("AI_TRANSLATE_SAVE_CACHE", CommandFlags.Modal)]
        public void SaveTranslationCache()
        {
            AppSettings.Diagnostics.Log("command AI_TRANSLATE_SAVE_CACHE executed");
            TranslationCommands.SaveCache();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string DetectAutoCadVersion()
        {
            try
            {
                return AcApp.GetSystemVariable("ACADVER")?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static Editor GetEditor()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            return doc?.Editor;
        }
    }
}
