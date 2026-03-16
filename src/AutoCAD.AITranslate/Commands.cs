using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoCAD.AITranslate
{
    public class Commands : IExtensionApplication
    {
        public void Initialize()
        {
        }

        public void Terminate()
        {
        }

        [CommandMethod("AI_TRANSLATE_ZH2EN")]
        public void TranslateZhToEn()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var service = TranslationService.CreateFromEnvironment();
            if (!service.IsConfigured)
            {
                editor.WriteMessage("\nOpenAI API key not configured. Set OPENAI_API_KEY.");
                return;
            }

            try
            {
                using (doc.LockDocument())
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var db = doc.Database;
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                    var mode = PromptMode(editor);
                    var items = CollectTextItemsFromBlockTableRecords(tr, new[] { bt[BlockTableRecord.ModelSpace], bt[BlockTableRecord.PaperSpace] });
                    ProcessTranslation(editor, db, tr, service, items, mode);

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nAI translate failed: {ex.Message}");
                return;
            }

            editor.WriteMessage("\nAI translate completed.");
        }

        [CommandMethod("AI_TRANSLATE_ZH2EN_SEL")]
        public void TranslateZhToEnSelection()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var service = TranslationService.CreateFromEnvironment();
            if (!service.IsConfigured)
            {
                editor.WriteMessage("\nOpenAI API key not configured. Set OPENAI_API_KEY.");
                return;
            }

            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "TEXT,MTEXT")
            });

            var selResult = editor.GetSelection(filter);
            if (selResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nNo objects selected.");
                return;
            }

            try
            {
                using (doc.LockDocument())
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var mode = PromptMode(editor);
                    var items = CollectTextItems(tr, selResult.Value.GetObjectIds());
                    var db = doc.Database;
                    ProcessTranslation(editor, db, tr, service, items, mode);
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nAI translate failed: {ex.Message}");
            }
        }

        [CommandMethod("AI_TRANSLATE_ROLLBACK")]
        public void RollbackLastTranslation()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var lastRun = TranslationSession.GetLastRun();
            if (lastRun == null || lastRun.Count == 0)
            {
                editor.WriteMessage("\nNo translation session to rollback.");
                return;
            }

            try
            {
                using (doc.LockDocument())
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    foreach (var record in lastRun)
                    {
                        if (record.NewEntityId != ObjectId.Null)
                        {
                            var created = tr.GetObject(record.NewEntityId, OpenMode.ForWrite, false) as Entity;
                            if (created != null)
                            {
                                created.Erase();
                            }

                            continue;
                        }

                        var entity = tr.GetObject(record.OriginalId, OpenMode.ForWrite, false) as Entity;
                        if (entity is DBText dbText)
                        {
                            dbText.TextString = record.OriginalText;
                        }
                        else if (entity is MText mtext)
                        {
                            mtext.Contents = record.OriginalText;
                        }
                    }

                    tr.Commit();
                }

                TranslationSession.ClearLastRun();
                editor.WriteMessage("\nRollback completed.");
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nRollback failed: {ex.Message}");
            }
        }

        private static TranslationMode PromptMode(Editor editor)
        {
            var options = new PromptKeywordOptions("\nTranslation mode")
            {
                AllowNone = true
            };
            options.Keywords.Add("Replace");
            options.Keywords.Add("NewLayer");
            options.Keywords.Default = "Replace";

            var result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return TranslationMode.Replace;
            }

            return string.Equals(result.StringResult, "NewLayer", StringComparison.OrdinalIgnoreCase)
                ? TranslationMode.NewLayer
                : TranslationMode.Replace;
        }

        private static List<TextItem> CollectTextItems(Transaction tr, IEnumerable<ObjectId> objectIds)
        {
            var items = new List<TextItem>();
            foreach (var id in objectIds)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity is DBText dbText)
                {
                    var original = dbText.TextString ?? string.Empty;
                    if (!RegexUtils.ContainsChinese(original))
                    {
                        continue;
                    }

                    items.Add(new TextItem(id, dbText.OwnerId, TextEntityType.DBText, original));
                }
                else if (entity is MText mtext)
                {
                    var original = mtext.Contents ?? string.Empty;
                    if (!RegexUtils.ContainsChinese(original))
                    {
                        continue;
                    }

                    items.Add(new TextItem(id, mtext.OwnerId, TextEntityType.MText, original));
                }
            }

            return items;
        }

        private static List<TextItem> CollectTextItemsFromBlockTableRecords(Transaction tr, IEnumerable<ObjectId> blockTableRecordIds)
        {
            var ids = new List<ObjectId>();
            foreach (var btrId in blockTableRecordIds)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    ids.Add(id);
                }
            }

            return CollectTextItems(tr, ids);
        }

        private static void ProcessTranslation(
            Editor editor,
            Database db,
            Transaction tr,
            TranslationService service,
            List<TextItem> items,
            TranslationMode mode)
        {
            if (items.Count == 0)
            {
                editor.WriteMessage("\nNo Chinese text found.");
                return;
            }

            var records = new List<TranslationRecord>();
            foreach (var item in items)
            {
                var translated = service.TranslateText(item.OriginalText);
                if (string.IsNullOrWhiteSpace(translated) ||
                    string.Equals(translated, item.OriginalText, StringComparison.Ordinal))
                {
                    continue;
                }

                records.Add(new TranslationRecord(item.OriginalId, item.BlockTableRecordId, item.EntityType, item.OriginalText, translated));
            }

            if (records.Count == 0)
            {
                editor.WriteMessage("\nNo translatable text found.");
                return;
            }

            PreviewSamples(editor, records);
            if (!ConfirmApply(editor))
            {
                editor.WriteMessage("\nTranslation cancelled.");
                return;
            }

            ObjectId newLayerId = ObjectId.Null;
            if (mode == TranslationMode.NewLayer)
            {
                newLayerId = LayerUtils.EnsureLayer(db, tr, "AI_TRANSLATED");
            }

            foreach (var record in records)
            {
                if (mode == TranslationMode.Replace)
                {
                    ApplyReplace(tr, record);
                }
                else
                {
                    ApplyNewLayer(tr, record, newLayerId);
                }
            }

            TranslationSession.SetLastRun(records);
            CsvExporter.Export(editor, records);
        }

        private static void ApplyReplace(Transaction tr, TranslationRecord record)
        {
            var entity = tr.GetObject(record.OriginalId, OpenMode.ForWrite, false) as Entity;
            if (entity is DBText dbText)
            {
                dbText.TextString = record.TranslatedText;
            }
            else if (entity is MText mtext)
            {
                mtext.Contents = record.TranslatedText;
            }
        }

        private static void ApplyNewLayer(Transaction tr, TranslationRecord record, ObjectId layerId)
        {
            var entity = tr.GetObject(record.OriginalId, OpenMode.ForRead, false) as Entity;
            if (entity == null)
            {
                return;
            }

            var clone = entity.Clone() as Entity;
            if (clone == null)
            {
                return;
            }

            if (clone is DBText dbText)
            {
                dbText.TextString = record.TranslatedText;
            }
            else if (clone is MText mtext)
            {
                mtext.Contents = record.TranslatedText;
            }

            if (layerId != ObjectId.Null)
            {
                clone.LayerId = layerId;
            }

            var btr = (BlockTableRecord)tr.GetObject(record.BlockTableRecordId, OpenMode.ForWrite, false);
            var newId = btr.AppendEntity(clone);
            tr.AddNewlyCreatedDBObject(clone, true);
            record.SetNewEntityId(newId);
        }

        private static void PreviewSamples(Editor editor, List<TranslationRecord> records)
        {
            editor.WriteMessage($"\nFound {records.Count} items to translate.");
            var sample = records.Take(5).ToList();
            for (var i = 0; i < sample.Count; i++)
            {
                editor.WriteMessage($"\nSample {i + 1}:\n  Original: {sample[i].OriginalText}\n  Translated: {sample[i].TranslatedText}");
            }
        }

        private static bool ConfirmApply(Editor editor)
        {
            var options = new PromptKeywordOptions("\nApply translation?")
            {
                AllowNone = false
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            options.Keywords.Default = "Yes";

            var result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
