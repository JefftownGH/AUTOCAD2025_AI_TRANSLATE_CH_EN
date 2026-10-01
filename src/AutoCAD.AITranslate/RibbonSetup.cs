using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// Creates the plugin's Ribbon tab ("AI 翻译").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How a button click reaches the code.</b> Each button carries a
    /// <see cref="CommandHandler"/> whose <see cref="ICommand.Execute"/> calls the target
    /// method in <see cref="TranslationCommands"/> directly. Nothing is routed through the
    /// AutoCAD command engine and no command string is ever posted.
    /// </para>
    /// <para>
    /// This replaces an earlier design that bound each button to a command name and issued
    /// <c>Document.SendStringToExecute</c> on click. That approach is unreliable: the
    /// command engine only drains its input queue when the application is idle at the
    /// command prompt, so a click arriving while the Ribbon has UI focus queues a string
    /// that is then silently discarded. The observable symptom is a button that highlights
    /// on hover but does nothing when clicked. Calling the method directly has no such
    /// dependency.
    /// </para>
    /// <para>
    /// <b>Why the Idle hop.</b> Ribbon clicks are delivered on the WPF dispatcher thread,
    /// but reading or writing the drawing database has to happen on AutoCAD's own context.
    /// Rather than call in place, the click parks the work and lets the next
    /// <see cref="Application.Idle"/> event run it on the correct thread.
    /// </para>
    /// </remarks>
    internal static class RibbonSetup
    {
        private const string TabId = "AITRANSLATE_TAB";

        /// <summary>Work queued by a click, to be run on the next Idle tick.</summary>
        private static readonly Queue<Action> PendingWork = new Queue<Action>();

        private static bool _idleHooked;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public static void Initialize()
        {
            var ribbon = ComponentManager.Ribbon as RibbonControl;
            if (ribbon != null)
            {
                Build(ribbon);
            }
            else
            {
                // The Ribbon control may not exist yet depending on load order, e.g. when
                // the DLL is NETLOADed while the UI is still being constructed.
                Diagnostics.Log("ribbon not ready at Initialize; deferring to Idle");
                AcApp.Idle += OnIdleForRibbon;
            }
        }

        private static void OnIdleForRibbon(object sender, EventArgs e)
        {
            AcApp.Idle -= OnIdleForRibbon;
            var ribbon = ComponentManager.Ribbon as RibbonControl;
            if (ribbon != null)
            {
                Build(ribbon);
            }
            else
            {
                Diagnostics.Log("ribbon still null at Idle; tab not created");
            }
        }

        // ------------------------------------------------------------------
        // Tab construction
        // ------------------------------------------------------------------

        private static void Build(RibbonControl ribbon)
        {
            // Reloading the DLL in the same session must not stack duplicate tabs.
            RibbonTab stale = null;
            foreach (var existing in ribbon.Tabs)
            {
                if (string.Equals(existing.Id, TabId, StringComparison.Ordinal))
                {
                    stale = existing;
                    break;
                }
            }

            if (stale != null)
            {
                ribbon.Tabs.Remove(stale);
                Diagnostics.Log("removed stale ribbon tab from previous load");
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;

            var tab = new RibbonTab { Id = TabId, Title = "AI 翻译" };

            var translatePanel = new RibbonPanelSource { Title = "翻译" };
            translatePanel.Items.Add(CreateButton(
                "翻译全图", "AITRANSLATE_ALL", "翻译整个图纸（模型空间 + 图纸空间）中的中文文本，可选目标语言并逐条校对",
                TranslationCommands.TranslateAll));
            translatePanel.Items.Add(CreateButton(
                "翻译选区", "AITRANSLATE_SEL", "只翻译选中的文本对象，可选目标语言并逐条校对",
                TranslationCommands.TranslateSelection));
            translatePanel.Items.Add(CreateButton(
                "回滚翻译", "AITRANSLATE_ROLLBACK", "撤销当前图纸最近一次翻译",
                TranslationCommands.Rollback));
            tab.Panels.Add(new RibbonPanel { Source = translatePanel });

            var configPanel = new RibbonPanelSource { Title = "配置" };
            configPanel.Items.Add(CreateButton(
                "模型设置", "AITRANSLATE_SETTINGS", "配置大模型 API Key、接口地址、模型等参数",
                TranslationCommands.OpenSettings));
            configPanel.Items.Add(CreateButton(
                "保存译文库", "AITRANSLATE_SAVE_CACHE", "把翻译记忆立即写入磁盘（默认在每次翻译后自动保存）",
                TranslationCommands.SaveCache));
            configPanel.Items.Add(CreateButton(
                "清空缓存", "AITRANSLATE_CLEAR_CACHE", "清空内存缓存与译文记忆库",
                TranslationCommands.ClearCache));
            tab.Panels.Add(new RibbonPanel { Source = configPanel });

            ribbon.Tabs.Add(tab);

            Diagnostics.Log($"ribbon tab built OK, v{version}, buttons=6 (direct in-process calls)");
            GetEditor()?.WriteMessage(
                $"\n[AI 翻译] Ribbon 选项卡已创建 (v{version})。按钮为直接调用，无需命令上下文。");
        }

        private static RibbonButton CreateButton(string text, string id, string tooltip, Action action)
        {
            return new RibbonButton
            {
                Text = text,
                Id = id,
                ShowText = true,
                ShowImage = false,
                Size = RibbonItemSize.Standard,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                ToolTip = tooltip,
                IsEnabled = true,
                CommandHandler = new DirectCallHandler(text, action)
            };
        }

        private static Editor GetEditor()
        {
            return AcApp.DocumentManager.MdiActiveDocument?.Editor;
        }

        // ------------------------------------------------------------------
        // Idle trampoline
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="work"/> on the next Idle tick, which executes on AutoCAD's
        /// own thread with a valid document context.
        /// </summary>
        private static void QueueForAutoCad(Action work)
        {
            lock (PendingWork)
            {
                PendingWork.Enqueue(work);

                if (_idleHooked)
                {
                    return;
                }

                _idleHooked = true;
            }

            AcApp.Idle += OnIdleForWork;
        }

        private static void OnIdleForWork(object sender, EventArgs e)
        {
            Action work = null;
            lock (PendingWork)
            {
                if (PendingWork.Count > 0)
                {
                    work = PendingWork.Dequeue();
                }

                if (PendingWork.Count == 0)
                {
                    AcApp.Idle -= OnIdleForWork;
                    _idleHooked = false;
                }
            }

            if (work == null)
            {
                return;
            }

            try
            {
                work();
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"queued command FAILED: {ex}");
                GetEditor()?.WriteMessage($"\n[AI 翻译] 执行失败: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // ICommand binding
        // ------------------------------------------------------------------

        /// <summary>
        /// An <see cref="ICommand"/> that invokes a plain .NET action. This is the whole
        /// mechanism behind a Ribbon button: no command name, no macro, no dispatch.
        /// </summary>
        private sealed class DirectCallHandler : ICommand
        {
            private readonly string _buttonText;
            private readonly Action _action;

            public DirectCallHandler(string buttonText, Action action)
            {
                _buttonText = buttonText;
                _action = action;
            }

            public bool CanExecute(object parameter) => true;

#pragma warning disable CS0067 // ICommand requires the event; CanExecute is constant.
            public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067

            public void Execute(object parameter)
            {
                Diagnostics.Log($"button '{_buttonText}' clicked; queueing direct call");

                // Hand the work to AutoCAD's thread before touching the database.
                QueueForAutoCad(_action);
            }
        }
    }
}
