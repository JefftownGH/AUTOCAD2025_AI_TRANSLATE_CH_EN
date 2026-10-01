using Autodesk.AutoCAD.DatabaseServices;

namespace JeffCAD.AiAssistant
{
    internal static class LayerUtils
    {
        public const string TranslatedLayerName = "AI_TRANSLATED";

        /// <summary>
        /// Returns the id of <paramref name="layerName"/>, creating it if absent.
        /// </summary>
        public static ObjectId EnsureLayer(Database db, Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                return lt[layerName];
            }

            lt.UpgradeOpen();
            var layer = new LayerTableRecord { Name = layerName };
            var id = lt.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        /// <summary>
        /// Determines whether an entity's owner is a block definition, as opposed to
        /// ModelSpace or PaperSpace. Entities inside block definitions must not be cloned,
        /// because a clone would apply to every reference of that block.
        /// </summary>
        /// <remarks>
        /// ModelSpaceId and PaperSpaceId live on the BlockTable, not on Database, so the
        /// table has to be opened to compare against them.
        /// </remarks>
        public static bool IsInsideBlockDefinition(Database db, Transaction tr, ObjectId ownerId)
        {
            if (db == null || tr == null || ownerId == ObjectId.Null)
            {
                return false;
            }

            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null)
            {
                return false;
            }

            return ownerId != bt[BlockTableRecord.ModelSpace] && ownerId != bt[BlockTableRecord.PaperSpace];
        }
    }
}
