using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AITranslate
{
    /// <summary>
    /// Keeps rollback state for the last translation performed in each open document.
    /// </summary>
    /// <remarks>
    /// The previous implementation used a single static field shared by the whole process.
    /// AutoCAD is a multiple-document (MDI) application, so running the rollback command
    /// after switching documents would try to open object ids belonging to document A
    /// through a transaction on document B. That either threw or, worse, erased unrelated
    /// entities. State is therefore keyed per Database, and entries are dropped
    /// automatically once a document is closed.
    /// </remarks>
    internal static class TranslationSession
    {
        private static readonly ConditionalWeakTable<Database, DocumentState> States =
            new ConditionalWeakTable<Database, DocumentState>();

        private sealed class DocumentState
        {
            /// <summary>
            /// A stack rather than a single slot so that consecutive translations can be
            /// rolled back one at a time.
            /// </summary>
            public readonly Stack<TranslationBatch> Batches = new Stack<TranslationBatch>();
        }

        public static void Push(Database db, List<TranslationRecord> records)
        {
            if (db == null || records == null || records.Count == 0)
            {
                return;
            }

            var state = States.GetOrCreateValue(db);
            lock (state.Batches)
            {
                state.Batches.Push(new TranslationBatch(records, DateTime.Now));
            }
        }

        /// <summary>Returns the most recent batch for this document, or null.</summary>
        public static TranslationBatch Peek(Database db)
        {
            if (db == null)
            {
                return null;
            }

            if (!States.TryGetValue(db, out var state))
            {
                return null;
            }

            lock (state.Batches)
            {
                return state.Batches.Count > 0 ? state.Batches.Peek() : null;
            }
        }

        /// <summary>Discards the most recent batch for this document.</summary>
        public static void Pop(Database db)
        {
            if (db == null || !States.TryGetValue(db, out var state))
            {
                return;
            }

            lock (state.Batches)
            {
                if (state.Batches.Count > 0)
                {
                    state.Batches.Pop();
                }
            }
        }

        public static int Depth(Database db)
        {
            if (db == null || !States.TryGetValue(db, out var state))
            {
                return 0;
            }

            lock (state.Batches)
            {
                return state.Batches.Count;
            }
        }

        public static void Clear(Database db)
        {
            if (db == null || !States.TryGetValue(db, out var state))
            {
                return;
            }

            lock (state.Batches)
            {
                state.Batches.Clear();
            }
        }
    }

    internal sealed class TranslationBatch
    {
        public TranslationBatch(List<TranslationRecord> records, DateTime timestamp)
        {
            Records = records;
            Timestamp = timestamp;
        }

        public List<TranslationRecord> Records { get; }

        public DateTime Timestamp { get; }
    }
}
