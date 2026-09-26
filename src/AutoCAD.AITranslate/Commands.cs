using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Autodesk.AutoCAD.DatabaseServices also declares a public "Document" type, so the
// unqualified name is ambiguous once both namespaces are imported. Alias the real one.
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;

namespace AutoCAD.AITranslate
{
    public class Commands : IExtensionApplication
    {
        /// <summary>Number of texts merged into a single API request.</summary>
        private const int DefaultBatchSize = 20;

        /// <summary>Number of requests allowed to be in flight at once.</summary>
        private const int DefaultMaxParallel = 4;

        /// <summary>Hard cap so a pathological drawing cannot cost thousands of calls.</summary>
        private const int MaxItemsPerRun = 5000;

        public void Initialize()
        {
            var editor = GetEditor();
            editor?.WriteMessage(
                $"\nAutoCAD AI Translate loaded. Model target: {DetectAutoCadVersion()}." +
                "\nCommands: AI_TRANSLATE_ZH2EN, AI_TRANSLATE_ZH2EN_SEL, AI_TRANSLATE_ROLLBACK.");
        }

        public void Terminate()
        {
        }

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

        [CommandMethod("AI_TRANSLATE_ZH2EN")]
        public void TranslateZhToEn()
        {
            RunTranslation(selectionOnly: false);
        }

        [CommandMethod("AI_TRANSLATE_ZH2EN_SEL")]
        public void TranslateZhToEnSelection()
        {
            RunTranslation(selectionOnly: true);
        }

        [CommandMethod("AI_TRANSLATE_ROLLBACK")]
        public void RollbackLastTranslation()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
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

        [CommandMethod("AI_TRANSLATE_CLEAR_CACHE")]
        public void ClearTranslationCache()
        {
            var before = TranslationService.CacheCount;
            TranslationService.ClearCache();
            GetEditor()?.WriteMessage($"\nTranslation cache cleared ({before} entries).");
        }

        /// <summary>
        /// Shared implementation of the two translate commands.
        /// </summary>
        /// <remarks>
        /// Execution order matters here and is the main structural fix in this file:
        ///   1. read-only transaction  -> collect candidate text, then commit and release
        ///   2. no transaction at all  -> user prompts and all network I/O
        ///   3. write transaction      -> apply every change in one short, atomic step
        /// Previously the transaction (and the document lock) was held open across the
        /// user prompts and the entire translation, which blocked the document for as
        /// long as the network calls took.
        /// </remarks>
        private void RunTranslation(bool selectionOnly)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var service = TranslationService.CreateFromEnvironment();
            if (!service.IsConfigured)
            {
                editor.WriteMessage(
                    "\nOpenAI API key not configured." +
                    $"\nSet the OPENAI_API_KEY environment variable, or create a settings file at:" +
                    $"\n  {Settings.SettingsFilePath ?? "<assembly directory>\\AutoCAD.AITranslate.settings.json"}");
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
                    "\nUse AI_TRANSLATE_ZH2EN_SEL to translate the drawing in sections.");
                return;
            }

            // ---------- Phase 2: interactive decisions, no transaction held ----------
            var mode = PromptMode(editor);
            if (mode == null)
            {
                editor.WriteMessage("\nTranslation cancelled.");
                return;
            }

            var options = PromptNetworkSettings(editor);
            editor.WriteMessage($"\nFound {items.Count} item(s) containing Chinese text." +
                                $"\nModel: {service.Model} | Batch: {options.BatchSize} | Parallel: {options.MaxParallel}");

            // ---------- Phase 2b: all network I/O, no transaction held ----------
            var stats = new TranslationStats();
            var progressReporter = new ProgressReporter(editor);
            Dictionary<string, TranslationOutcome> resolved;
            try
            {
                resolved = service.ResolveMany(
                    items.Select(i => i.OriginalText).Distinct(StringComparer.Ordinal).ToList(),
                    options.BatchSize,
                    options.MaxParallel,
                    progressReporter.Report,
                    stats);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nTranslation failed: {Describe(ex)}");
                return;
            }
            finally
            {
                progressReporter.Finish();
            }

            var records = new List<TranslationRecord>();
            foreach (var item in items)
            {
                if (!resolved.TryGetValue(item.OriginalText, out var outcome))
                {
                    continue;
                }

                records.Add(new TranslationRecord(
                    item.OriginalId, item.OwnerId, item.EntityType, item.OriginalText, outcome.Translated));
            }

            if (records.Count == 0)
            {
                editor.WriteMessage("\nNothing to apply: no text needed translation.");
                ReportFailureSummary(editor, stats);
                return;
            }

            PreviewSamples(editor, records);
            ReportFailureSummary(editor, stats);

            if (!ConfirmApply(editor))
            {
                editor.WriteMessage("\nTranslation cancelled.");
                return;
            }

            // When the user asked for a preview-only run, report and stop.
            if (options.PreviewOnly)
            {
                editor.WriteMessage("\nPreview only: no changes were written to the drawing.");
                return;
            }

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
                        var mustReplace = mode == TranslationMode.Replace ||
                                          LayerUtils.IsInsideBlockDefinition(doc.Database, record.OwnerId);

                        if (mustReplace)
                        {
                            if (ApplyReplace(tr, record))
                            {
                                applied++;
                            }

                            if (mode == TranslationMode.NewLayer &&
                                LayerUtils.IsInsideBlockDefinition(doc.Database, record.OwnerId))
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

            editor.WriteMessage($"\nTranslation completed: {applied} item(s) applied" +
                                (blockDefinitionFallbacks > 0
                                    ? $" ({blockDefinitionFallbacks} inside block definitions were replaced in place; use NewLayer mode on exploded text to keep originals)"
                                    : string.Empty) +
                                ".");
            editor.WriteMessage($"\n  Unique strings: {stats.Requested} | API requests: {stats.RequestsSent} | Cache hits: {stats.CacheHits}");
            editor.WriteMessage("\n  Run AI_TRANSLATE_ROLLBACK to undo, or AI_TRANSLATE_CLEAR_CACHE to free memory.");

            // ---------- Phase 4: optional export, outside the transaction ----------
            CsvExporter.TryExport(editor, records);
        }

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
                        AddTextItem(items, db, id, dbText.OwnerId, TextEntityType.DBText, dbText.TextString);
                    }
                    else if (entity is MText mtext)
                    {
                        AddTextItem(items, db, id, mtext.OwnerId, TextEntityType.MText, mtext.Contents);
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
                    AddTextItem(items, db, id, dbText.OwnerId, TextEntityType.DBText, dbText.TextString);
                }
                else if (entity is MText mtext)
                {
                    AddTextItem(items, db, id, mtext.OwnerId, TextEntityType.MText, mtext.Contents);
                }
            }
        }

        private static void AddTextItem(
            List<TextItem> items,
            Database db,
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

            items.Add(new TextItem(id, ownerId, entityType, original, LayerUtils.IsInsideBlockDefinition(db, ownerId)));
        }

        private static TranslationMode? PromptMode(Editor editor)
        {
            var options = new PromptKeywordOptions(
                "\nTranslation mode [Replace in place / New layer keeps originals]")
            {
                AllowNone = false
            };
            options.Keywords.Add("Replace");
            options.Keywords.Add("NewLayer");
            options.Keywords.Default = "NewLayer";

            var result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK)
            {
                return null;
            }

            return string.Equals(result.StringResult, "Replace", StringComparison.OrdinalIgnoreCase)
                ? TranslationMode.Replace
                : TranslationMode.NewLayer;
        }

        private sealed class NetworkSettings
        {
            public int BatchSize { get; set; } = DefaultBatchSize;
            public int MaxParallel { get; set; } = DefaultMaxParallel;
            public bool PreviewOnly { get; set; }
        }

        private static NetworkSettings PromptNetworkSettings(Editor editor)
        {
            var settings = new NetworkSettings();

            var batchOptions = new PromptIntegerOptions(
                $"\nItems per API request (1 = one call per item) <{DefaultBatchSize}>")
            {
                AllowNone = true,
                AllowZero = false,
                LowerLimit = 1,
                UpperLimit = 200
            };
            var batchResult = editor.GetInteger(batchOptions);
            if (batchResult.Status == PromptStatus.OK && batchResult.Value > 0)
            {
                settings.BatchSize = batchResult.Value;
            }

            var parallelOptions = new PromptIntegerOptions(
                $"\nConcurrent requests <{DefaultMaxParallel}>")
            {
                AllowNone = true,
                AllowZero = false,
                LowerLimit = 1,
                UpperLimit = 16
            };
            var parallelResult = editor.GetInteger(parallelOptions);
            if (parallelResult.Status == PromptStatus.OK && parallelResult.Value > 0)
            {
                settings.MaxParallel = parallelResult.Value;
            }

            var previewOptions = new PromptKeywordOptions("\nWrite changes to the drawing? [Yes/PreviewOnly]")
            {
                AllowNone = false
            };
            previewOptions.Keywords.Add("Yes");
            previewOptions.Keywords.Add("PreviewOnly");
            previewOptions.Keywords.Default = "Yes";

            var previewResult = editor.GetKeywords(previewOptions);
            settings.PreviewOnly = previewResult.Status == PromptStatus.OK &&
                                   string.Equals(previewResult.StringResult, "PreviewOnly", StringComparison.OrdinalIgnoreCase);

            return settings;
        }

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

        private static void PreviewSamples(Editor editor, List<TranslationRecord> records)
        {
            editor.WriteMessage($"\n{records.Count} item(s) ready to translate. Sample:");
            var sample = records.Take(5).ToList();
            for (var i = 0; i < sample.Count; i++)
            {
                editor.WriteMessage($"\n  [{i + 1}] {sample[i].OriginalText}" +
                                    $"\n   -> {sample[i].TranslatedText}");
            }
        }

        private static void ReportFailureSummary(Editor editor, TranslationStats stats)
        {
            if (stats.Failed == 0)
            {
                return;
            }

            editor.WriteMessage($"\nWarning: {stats.Failed} item(s) could not be translated.");
            foreach (var error in stats.Errors.Take(3))
            {
                editor.WriteMessage($"\n  - {OpenAiClient.Truncate(error, 300)}");
            }
        }

        private static bool ConfirmApply(Editor editor)
        {
            var options = new PromptKeywordOptions("\nApply translation? [Yes/No]")
            {
                AllowNone = false
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            options.Keywords.Default = "No";

            var result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
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

            private static string Describe(TimeSpan span)
            {
                return span.TotalSeconds < 60
                    ? $"{span.TotalSeconds:F1}s"
                    : $"{(int)span.TotalMinutes}m {span.Seconds}s";
            }
        }

        private static string Describe(System.Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                return string.Join(" | ", agg.InnerExceptions.Select(e => e.Message));
            }

            return ex.Message;
        }
    }
}
