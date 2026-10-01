using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Autodesk.AutoCAD.DatabaseServices also declares a public "Document" type, so the
// unqualified name is ambiguous once both namespaces are imported. Alias the real one.
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
using LlmToolkit;

namespace JeffCAD.AiAssistant
{
    /// <summary>
    /// The real implementations behind both the command line and the Ribbon.
    /// </summary>
    /// <remarks>
    /// These are ordinary static methods that are meaningful without any AutoCAD command
    /// context. The command-line entry points in <see cref="Commands"/> and the Ribbon
    /// buttons in <see cref="RibbonSetup"/> both call into this class.
    ///
    /// This is deliberate. Binding a Ribbon button to a command string means the click
    /// has to be routed through AutoCAD's command engine, which only drains its input
    /// queue when the application is sitting at the command prompt. A click delivered
    /// while the UI has focus is silently swallowed, so the button looks dead. Calling
    /// the method directly removes that dependency entirely.
    /// </remarks>
    internal static class TranslationCommands
    {
        /// <summary>Number of texts merged into a single API request.</summary>
        internal const int DefaultBatchSize = 20;

        /// <summary>Number of requests allowed to be in flight at once.</summary>
        internal const int DefaultMaxParallel = 4;

        /// <summary>Hard cap so a pathological drawing cannot cost thousands of calls.</summary>
        internal const int MaxItemsPerRun = 5000;

        // ------------------------------------------------------------------
        // Entry points shared by the command line and the Ribbon
        // ------------------------------------------------------------------

        /// <summary>Translates the whole drawing (ModelSpace + PaperSpace).</summary>
        internal static void TranslateAll()
        {
            RunTranslation(selectionOnly: false);
        }

        /// <summary>Translates only the current selection.</summary>
        internal static void TranslateSelection()
        {
            RunTranslation(selectionOnly: true);
        }

        /// <summary>Opens the model-configuration dialog.</summary>
        internal static void OpenSettings()
        {
            AppSettings.Diagnostics.Log("OpenSettings invoked");

            try
            {
                var dialog = new SettingsDialog();
                AcApp.ShowModalWindow(dialog);
                AppSettings.Diagnostics.Log("settings dialog closed");
            }
            catch (Exception ex)
            {
                AppSettings.Diagnostics.Log($"settings dialog FAILED: {ex}");
                GetEditor()?.WriteMessage($"\n[AI 翻译] 打开设置窗口失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Empties both the in-session memo and the persistent translation memory.
        /// </summary>
        /// <remarks>
        /// The persistent memory is the one that survives sessions, so a user asking to
        /// clear the cache almost certainly means both. Clearing only the in-memory half
        /// would leave the next run serving the answers they were trying to discard.
        /// </remarks>
        internal static void ClearCache()
        {
            AppSettings.Diagnostics.Log("ClearCache invoked");

            var sessionBefore = TranslationService.CacheCount;
            var persistentBefore = TranslationService.PersistentCacheCount;
            var verifiedBefore = TranslationService.VerifiedCacheCount;

            TranslationService.ClearCache();

            GetEditor()?.WriteMessage(
                $"\nTranslation cache cleared: {sessionBefore} in-session, " +
                $"{persistentBefore} persistent ({verifiedBefore} of them hand-corrected).");
        }

        /// <summary>Writes the persistent translation memory to disk immediately.</summary>
        internal static void SaveCache()
        {
            AppSettings.Diagnostics.Log("SaveCache invoked");

            var wrote = TranslationService.FlushCache();
            GetEditor()?.WriteMessage(wrote
                ? $"\nTranslation memory saved ({TranslationService.PersistentCacheCount} entries) to:\n  {TranslationCache.CacheFilePath}"
                : "\nTranslation memory had no changes to save.");
        }

        /// <summary>Rolls back the most recent translation in the active drawing.</summary>
        internal static void Rollback()
        {
            AppSettings.Diagnostics.Log("Rollback invoked");

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                AcApp.ShowAlertDialog("请先打开或新建一个图纸，再使用翻译功能。");
                return;
            }

            var editor = doc.Editor;
            var batch = TranslationSession.Peek(doc.Database);
            if (batch == null || batch.Records.Count == 0)
            {
                editor.WriteMessage("\nNo translation session to roll back in this drawing.");
                return;
            }

            var remaining = TranslationSession.Depth(doc.Database);
            var prompt = new PromptKeywordOptions(
                $"\nRoll back {batch.Records.Count} translated item(s) from {batch.Timestamp:HH:mm:ss}" +
                (remaining > 1 ? $" ({remaining - 1} older batch(es) will remain)" : string.Empty) + "?")
            {
                AllowNone = false
            };
            prompt.Keywords.Add("Yes");
            prompt.Keywords.Add("No");
            prompt.Keywords.Default = "Yes";

            var confirm = editor.GetKeywords(prompt);
            if (confirm.Status != PromptStatus.OK ||
                !string.Equals(confirm.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nRollback cancelled.");
                return;
            }

            try
            {
                using (doc.LockDocument())
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var restored = 0;
                    var erased = 0;

                    foreach (var record in batch.Records)
                    {
                        if (record.NewEntityId != ObjectId.Null)
                        {
                            var created = tr.GetObject(record.NewEntityId, OpenMode.ForWrite, openErased: true) as Entity;
                            if (created != null && !created.IsErased)
                            {
                                created.Erase();
                                erased++;
                            }

                            continue;
                        }

                        var entity = tr.GetObject(record.OriginalId, OpenMode.ForWrite, openErased: true) as Entity;
                        if (entity == null || entity.IsErased)
                        {
                            continue;
                        }

                        if (entity is DBText dbText)
                        {
                            dbText.TextString = record.OriginalText;
                            restored++;
                        }
                        else if (entity is MText mtext)
                        {
                            mtext.Contents = record.OriginalText;
                            restored++;
                        }
                    }

                    tr.Commit();
                    editor.WriteMessage($"\nRollback completed: {restored} restored, {erased} removed.");
                }

                TranslationSession.Pop(doc.Database);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nRollback failed: {Describe(ex)}");
            }
        }

        // ------------------------------------------------------------------
        // Shared translation pipeline
        // ------------------------------------------------------------------

        /// <summary>
        /// Shared implementation of the two translate commands.
        /// </summary>
        /// <remarks>
        /// Execution order matters here and is the main structural guarantee:
        ///   1. read-only transaction  -> collect candidate text, then commit and release
        ///   2. no transaction at all  -> user prompts and all network I/O
        ///   3. write transaction      -> apply every change in one short, atomic step
        /// Holding the transaction (and the document lock) across user prompts and the
        /// whole translation would block the drawing for as long as the network calls take.
        /// </remarks>
        private static void RunTranslation(bool selectionOnly)
        {
            AppSettings.Diagnostics.Log($"RunTranslation(selectionOnly={selectionOnly}) invoked");

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                AcApp.ShowAlertDialog("请先打开或新建一个图纸，再使用翻译功能。");
                return;
            }

            var editor = doc.Editor;
            var service = TranslationService.CreateFromEnvironment();
            if (!service.IsConfigured)
            {
                editor.WriteMessage(
                    "\nOpenAI API key not configured." +
                    "\nOpen the model settings (Ribbon \"AI 翻译\" > 模型设置, or run AI_TRANSLATE_SETTINGS)," +
                    $"\nor set {LlmSettingKeys.ApiKey} / create a settings file at:" +
                    $"\n  {AppSettings.SettingsFilePath ?? "<assembly directory>\\AutoCAD.AITranslate.settings.json"}");
                return;
            }

            if (AcApp.GetSystemVariable("DWGTITLED") is short titled && titled == 0)
            {
                editor.WriteMessage("\nWarning: this drawing has not been saved yet.");
            }

            // ---------- Phase 1: read-only scan ----------
            List<TextItem> items;
            try
            {
                items = selectionOnly
                    ? ScanSelection(doc, editor)
                    : ScanModelAndPaperSpace(doc);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nFailed to scan drawing: {Describe(ex)}");
                return;
            }

            if (items == null)
            {
                return; // selection cancelled; message already written
            }

            if (items.Count == 0)
            {
                editor.WriteMessage(selectionOnly
                    ? "\nNo Chinese text found in the selection."
                    : "\nNo Chinese text found in ModelSpace or PaperSpace.");
                return;
            }

            if (items.Count > MaxItemsPerRun)
            {
                editor.WriteMessage(
                    $"\nFound {items.Count} items, which exceeds the {MaxItemsPerRun} item safety limit." +
                    "\nUse the selection command to translate the drawing in sections.");
                return;
            }

            // ---------- Phase 2: pick a language, translate, let the user review ----------
            //
            // No transaction is held from here on. The dialog can stay open for minutes
            // while the user edits translations, and holding the document lock across that
            // would freeze the drawing for the whole session.
            var language = TargetLanguages.ByCode(AppSettings.Store.Read(LlmSettingKeys.TargetLanguage));
            service.SetTargetLanguage(language);

            var options = new NetworkSettings();

            // Cache-only pass first: anything the translation memory already knows is
            // served immediately, so the dialog can open without waiting on the network
            // for text that has been translated before.
            List<TranslationRow> rows;
            try
            {
                rows = BuildRows(service, items, language, options, editor, out var stats, showProgress: true);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nTranslation failed: {Describe(ex)}");
                return;
            }

            var dialog = new TranslationReviewDialog(
                rows, language, TranslationMode.NewLayer, items.Count, service.Model);

            // Retranslation is triggered from inside the dialog, which means it happens
            // while a modal window is up. It touches only the network and the row objects,
            // never the document, so no lock is needed or wanted.
            dialog.RetranslateRequested += (s, targets) =>
            {
                try
                {
                    var pending = items
                        .Where(i => targets.Any(t => t.OriginalId == i.OriginalId))
                        .ToList();

                    if (pending.Count == 0)
                    {
                        return;
                    }

                    service.SetTargetLanguage(dialog.SelectedLanguage);
                    var refreshed = BuildRows(service, pending, dialog.SelectedLanguage, options, editor,
                        out var retryStats, showProgress: false);

                    // Merge the new proposals back by object id, leaving rows the user
                    // edited alone -- an explicit correction must survive a retranslation.
                    var byId = refreshed.ToDictionary(r => r.OriginalId);
                    foreach (var row in targets)
                    {
                        if (!byId.TryGetValue(row.OriginalId, out var fresh) || row.IsEdited)
                        {
                            continue;
                        }

                        row.TranslatedText = fresh.TranslatedText;
                        row.IsConfirmed = !row.IsEmpty;
                    }

                    editor.WriteMessage(
                        $"\nRetranslated {refreshed.Count} item(s) to {dialog.SelectedLanguage.DisplayName}" +
                        $" ({retryStats.RequestsSent} request(s) sent).");
                }
                catch (System.Exception ex)
                {
                    editor.WriteMessage($"\nRetranslation failed: {Describe(ex)}");
                }
            };

            AcApp.ShowModalWindow(dialog);
            var outcome = dialog.Outcome;
            if (outcome == null || !outcome.Accepted)
            {
                editor.WriteMessage("\nTranslation cancelled.");
                return;
            }

            // Remember the language for next time, so the picker opens on the same choice.
            AppSettings.Store.Write(new[]
            {
                new KeyValuePair<string, string>(LlmSettingKeys.TargetLanguage, outcome.Language.Code)
            });

            // Persist the user's decisions before touching the drawing: a correction is
            // knowledge about the term, and should survive a later rollback of the
            // drawing change itself.
            var acceptedPairs = outcome.ConfirmedRows
                .Select(r => new KeyValuePair<string, string>(r.OriginalText, r.TranslatedText))
                .ToList();
            TranslationService.CommitReviewed(outcome.Language.Code, acceptedPairs, verified: false);

            var editedPairs = outcome.EditedRows
                .Select(r => new KeyValuePair<string, string>(r.OriginalText, r.TranslatedText))
                .ToList();
            if (editedPairs.Count > 0)
            {
                TranslationService.CommitReviewed(outcome.Language.Code, editedPairs, verified: true);
            }

            TranslationCache.Flush();

            var records = outcome.ConfirmedRows.Select(r => r.ToRecord()).ToList();
            if (records.Count == 0)
            {
                editor.WriteMessage("\nNothing selected: no changes were written to the drawing.");
                return;
            }

            var mode = outcome.Mode;

            // ---------- Phase 3: short write transaction ----------
            var applied = 0;
            var blockDefinitionFallbacks = 0;
            try
            {
                using (doc.LockDocument())
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var newLayerId = ObjectId.Null;
                    if (mode == TranslationMode.NewLayer)
                    {
                        newLayerId = LayerUtils.EnsureLayer(doc.Database, tr, LayerUtils.TranslatedLayerName);
                    }

                    foreach (var record in records)
                    {
                        // Cloning into a block definition would rewrite every reference of
                        // that block, so such items always fall back to in-place replacement.
                        var insideBlockDefinition =
                            LayerUtils.IsInsideBlockDefinition(doc.Database, tr, record.OwnerId);
                        var mustReplace = mode == TranslationMode.Replace || insideBlockDefinition;

                        if (mustReplace)
                        {
                            if (ApplyReplace(tr, record))
                            {
                                applied++;
                            }

                            if (mode == TranslationMode.NewLayer && insideBlockDefinition)
                            {
                                blockDefinitionFallbacks++;
                            }
                        }
                        else if (ApplyNewLayer(tr, record, newLayerId))
                        {
                            applied++;
                        }
                    }

                    tr.Commit();
                    TranslationSession.Push(doc.Database, records);
                }
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nFailed to apply translation: {Describe(ex)}");
                return;
            }

            editor.WriteMessage($"\nTranslation completed: {applied} item(s) applied to " +
                                $"{outcome.Language.DisplayName}" +
                                (blockDefinitionFallbacks > 0
                                    ? $" ({blockDefinitionFallbacks} inside block definitions were replaced in place; use NewLayer mode on exploded text to keep originals)"
                                    : string.Empty) +
                                ".");
            editor.WriteMessage($"\n  Edited by hand: {outcome.EditedRows.Count}" +
                                $" | Translation memory: {TranslationCache.Count} entries" +
                                $" ({TranslationCache.VerifiedCount} verified)");
            editor.WriteMessage("\n  Use the Ribbon \"回滚翻译\" button (or AI_TRANSLATE_ROLLBACK) to undo.");

            // ---------- Phase 4: optional export, outside the transaction ----------
            CsvExporter.TryExport(editor, records);
        }

        /// <summary>
        /// Translates a set of scanned items and turns them into reviewable rows.
        /// </summary>
        /// <remarks>
        /// Reads the persistent translation memory before issuing any request, so a second
        /// run over the same drawing costs nothing and a term corrected once is reused
        /// everywhere.
        /// </remarks>
        private static List<TranslationRow> BuildRows(
            TranslationService service,
            List<TextItem> items,
            TargetLanguage language,
            NetworkSettings options,
            Editor editor,
            out TranslationStats stats,
            bool showProgress)
        {
            stats = new TranslationStats();

            ProgressReporter progressReporter = null;
            if (showProgress)
            {
                progressReporter = new ProgressReporter(editor);
            }

            // A method group cannot be null-conditional, so the delegate is built only
            // when a reporter exists. Passing null leaves the callback unset, which
            // ResolveMany already tolerates.
            Action<int, int> onProgress = progressReporter == null
                ? (Action<int, int>)null
                : progressReporter.Report;

            Dictionary<string, TranslationOutcome> resolved;
            try
            {
                resolved = service.ResolveMany(
                    items.Select(i => i.OriginalText).Distinct(StringComparer.Ordinal).ToList(),
                    options.BatchSize,
                    options.MaxParallel,
                    onProgress,
                    stats);
            }
            finally
            {
                progressReporter?.Finish();
            }

            var rows = new List<TranslationRow>();
            foreach (var item in items)
            {
                if (!resolved.TryGetValue(item.OriginalText, out var outcome))
                {
                    continue;
                }

                var remembered = TranslationCache.Lookup(language.Code, item.OriginalText);

                rows.Add(new TranslationRow(
                    item.OriginalId,
                    item.OwnerId,
                    item.EntityType,
                    item.OriginalText,
                    outcome.Translated,
                    item.IsInsideBlockDefinition,
                    outcome.FromCache,
                    remembered != null && remembered.Verified));
            }

            return rows;
        }

        // ------------------------------------------------------------------
        // Scanning
        // ------------------------------------------------------------------

        private static List<TextItem> ScanModelAndPaperSpace(AcadDocument doc)
        {
            var items = new List<TextItem>();
            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var db = doc.Database;
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                CollectFromBlockTableRecord(tr, db, bt[BlockTableRecord.ModelSpace], items);
                CollectFromBlockTableRecord(tr, db, bt[BlockTableRecord.PaperSpace], items);

                tr.Commit();
            }

            return items;
        }

        private static List<TextItem> ScanSelection(AcadDocument doc, Editor editor)
        {
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "TEXT,MTEXT,INSERT")
            });

            var selResult = editor.GetSelection(filter);
            if (selResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nNo objects selected.");
                return null;
            }

            var items = new List<TextItem>();
            var skippedBlockReferences = 0;

            using (doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var db = doc.Database;
                foreach (var id in selResult.Value.GetObjectIds())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, openErased: false) as Entity;
                    if (entity == null)
                    {
                        continue;
                    }

                    if (entity is BlockReference blockRef)
                    {
                        // Text nested in a block reference belongs to the block definition
                        // shared by every reference, so it cannot be treated as a
                        // per-selection copy. Count it and warn rather than silently
                        // leaving it untranslated.
                        var blockName = GetBlockName(tr, blockRef);
                        var nested = CountChineseInBlockDefinition(tr, db, blockRef, blockName);
                        if (nested > 0)
                        {
                            skippedBlockReferences++;
                        }

                        continue;
                    }

                    if (entity is DBText dbText)
                    {
                        AddTextItem(items, db, tr, id, dbText.OwnerId, TextEntityType.DBText, dbText.TextString);
                    }
                    else if (entity is MText mtext)
                    {
                        AddTextItem(items, db, tr, id, mtext.OwnerId, TextEntityType.MText, mtext.Contents);
                    }
                }

                tr.Commit();
            }

            if (skippedBlockReferences > 0)
            {
                editor.WriteMessage(
                    $"\nNote: {skippedBlockReferences} selected block reference(s) contain Chinese text in their" +
                    "\nblock definition. Explode them (EXPLODE) if you want that text translated.");
            }

            return items;
        }

        private static string GetBlockName(Transaction tr, BlockReference blockRef)
        {
            try
            {
                if (blockRef.IsDynamicBlock)
                {
                    var dyn = tr.GetObject(blockRef.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    return dyn?.Name;
                }

                var btr = tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                return btr?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static int CountChineseInBlockDefinition(
            Transaction tr, Database db, BlockReference blockRef, string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName) ||
                string.Equals(blockName, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(blockName, BlockTableRecord.PaperSpace, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(blockName))
                {
                    return 0;
                }

                var btr = (BlockTableRecord)tr.GetObject(bt[blockName], OpenMode.ForRead);
                var count = 0;
                foreach (var id in btr)
                {
                    if (id.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(id, OpenMode.ForRead, false) is DBText t && RegexUtils.ContainsChinese(t.TextString))
                    {
                        count++;
                    }
                    else if (tr.GetObject(id, OpenMode.ForRead, false) is MText m && RegexUtils.ContainsChinese(m.Contents))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static void CollectFromBlockTableRecord(
            Transaction tr, Database db, ObjectId btrId, List<TextItem> items)
        {
            if (btrId == ObjectId.Null)
            {
                return;
            }

            var btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null)
            {
                return;
            }

            foreach (var id in btr)
            {
                // Entities pending deletion must be skipped; opening them would throw.
                if (id.IsErased)
                {
                    continue;
                }

                var entity = tr.GetObject(id, OpenMode.ForRead, openErased: false) as Entity;
                if (entity is DBText dbText)
                {
                    AddTextItem(items, db, tr, id, dbText.OwnerId, TextEntityType.DBText, dbText.TextString);
                }
                else if (entity is MText mtext)
                {
                    AddTextItem(items, db, tr, id, mtext.OwnerId, TextEntityType.MText, mtext.Contents);
                }
            }
        }

        private static void AddTextItem(
            List<TextItem> items,
            Database db,
            Transaction tr,
            ObjectId id,
            ObjectId ownerId,
            TextEntityType entityType,
            string text)
        {
            var original = text ?? string.Empty;
            if (!RegexUtils.ContainsChinese(original))
            {
                return;
            }

            var insideBlockDefinition = LayerUtils.IsInsideBlockDefinition(db, tr, ownerId);
            items.Add(new TextItem(id, ownerId, entityType, original, insideBlockDefinition));
        }

        // ------------------------------------------------------------------
        // Networking options
        // ------------------------------------------------------------------

        /// <summary>
        /// Batching knobs. The write mode and the target language now come from the
        /// review dialog, so only the throughput settings remain as defaults.
        /// </summary>
        private sealed class NetworkSettings
        {
            public int BatchSize { get; set; } = DefaultBatchSize;
            public int MaxParallel { get; set; } = DefaultMaxParallel;
        }

        // ------------------------------------------------------------------
        // Apply
        // ------------------------------------------------------------------

        private static bool ApplyReplace(Transaction tr, TranslationRecord record)
        {
            var entity = tr.GetObject(record.OriginalId, OpenMode.ForWrite, openErased: true) as Entity;
            if (entity == null || entity.IsErased)
            {
                return false;
            }

            if (entity is DBText dbText)
            {
                dbText.TextString = record.TranslatedText;
                return true;
            }

            if (entity is MText mtext)
            {
                mtext.Contents = record.TranslatedText;
                return true;
            }

            return false;
        }

        private static bool ApplyNewLayer(Transaction tr, TranslationRecord record, ObjectId layerId)
        {
            var entity = tr.GetObject(record.OriginalId, OpenMode.ForRead, false) as Entity;
            if (entity == null)
            {
                return false;
            }

            var clone = entity.Clone() as Entity;
            if (clone == null)
            {
                return false;
            }

            if (clone is DBText dbText)
            {
                dbText.TextString = record.TranslatedText;
            }
            else if (clone is MText mtext)
            {
                mtext.Contents = record.TranslatedText;
            }
            else
            {
                clone.Dispose();
                return false;
            }

            if (layerId != ObjectId.Null)
            {
                clone.LayerId = layerId;
            }

            var btr = tr.GetObject(record.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
            if (btr == null)
            {
                clone.Dispose();
                return false;
            }

            var newId = btr.AppendEntity(clone);
            tr.AddNewlyCreatedDBObject(clone, true);
            record.SetNewEntityId(newId);
            return true;
        }

        /// <summary>
        /// Throttled command-line progress so long runs do not flood the editor.
        /// </summary>
        private sealed class ProgressReporter
        {
            private readonly Editor _editor;
            private readonly DateTime _start = DateTime.Now;
            private int _lastPercent = -1;

            public ProgressReporter(Editor editor)
            {
                _editor = editor;
            }

            public void Report(int completed, int total)
            {
                if (total <= 0)
                {
                    return;
                }

                var percent = (int)Math.Round(completed * 100.0 / total);
                if (percent == _lastPercent)
                {
                    return;
                }

                // Only emit on 10% boundaries to keep the command line readable.
                if (percent % 10 != 0 && percent != 100)
                {
                    return;
                }

                _lastPercent = percent;
                _editor?.WriteMessage($"\n  Translating... {percent}% ({completed}/{total} batches)");
            }

            public void Finish()
            {
                _editor?.WriteMessage($"\n  Translation stage finished in {Describe(DateTime.Now - _start)}.");
            }
        }

        private static string Describe(TimeSpan span)
        {
            return span.TotalSeconds < 60
                ? $"{span.TotalSeconds:F1}s"
                : $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }

        private static string Describe(System.Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                return string.Join(" | ", agg.InnerExceptions.Select(e => e.Message));
            }

            return ex.Message;
        }

        private static Editor GetEditor()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            return doc?.Editor;
        }
    }
}
