using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Registers the command class with AutoCAD. Without this, AutoCAD has to reflect over
// every type in every loaded assembly to discover [CommandMethod] members; declaring it
// here lets the command set be resolved directly at load time.
[assembly: CommandClass(typeof(AutoCAD.AITranslate.Commands))]

namespace AutoCAD.AITranslate
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
            Diagnostics.Log($"plugin Initialize, v{version}");

            RibbonSetup.Initialize();

            var editor = GetEditor();
            editor?.WriteMessage(
                $"\nAutoCAD AI Translate v{version} loaded. Model target: {DetectAutoCadVersion()}." +
                "\nRibbon: \"AI 翻译\" tab." +
                "\nCommands: AI_TRANSLATE_ZH2EN, AI_TRANSLATE_ZH2EN_SEL, AI_TRANSLATE_ROLLBACK, " +
                "AI_TRANSLATE_SETTINGS, AI_TRANSLATE_CLEAR_CACHE.");
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
            Diagnostics.Log("command AI_TRANSLATE_ZH2EN executed");
            TranslationCommands.TranslateAll();
        }

        /// <summary>Translates only the current selection.</summary>
        [CommandMethod("AI_TRANSLATE_ZH2EN_SEL", CommandFlags.Modal)]
        public void TranslateZhToEnSelection()
        {
            Diagnostics.Log("command AI_TRANSLATE_ZH2EN_SEL executed");
            TranslationCommands.TranslateSelection();
        }

        /// <summary>Rolls back the most recent translation in the active drawing.</summary>
        [CommandMethod("AI_TRANSLATE_ROLLBACK", CommandFlags.Modal)]
        public void RollbackLastTranslation()
        {
            Diagnostics.Log("command AI_TRANSLATE_ROLLBACK executed");
            TranslationCommands.Rollback();
        }

        /// <summary>Opens the model-configuration dialog.</summary>
        [CommandMethod("AI_TRANSLATE_SETTINGS", CommandFlags.Modal)]
        public void OpenModelSettings()
        {
            Diagnostics.Log("command AI_TRANSLATE_SETTINGS executed");
            TranslationCommands.OpenSettings();
        }

        /// <summary>Empties the in-memory translation cache.</summary>
        [CommandMethod("AI_TRANSLATE_CLEAR_CACHE", CommandFlags.Modal)]
        public void ClearTranslationCache()
        {
            Diagnostics.Log("command AI_TRANSLATE_CLEAR_CACHE executed");
            TranslationCommands.ClearCache();
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
