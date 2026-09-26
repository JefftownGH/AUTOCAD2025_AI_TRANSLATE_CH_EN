using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AITranslate
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
        /// Determines whether an entity's owner is a block definition (as opposed to
        /// ModelSpace or PaperSpace). Entities inside block definitions must not be
        /// cloned, because the clone would apply to every reference of that block.
        /// </summary>
        public static bool IsInsideBlockDefinition(Database db, ObjectId ownerId)
        {
            if (ownerId == ObjectId.Null || db == null)
            {
                return false;
            }

            return ownerId != db.ModelSpaceId && ownerId != db.PaperSpaceId;
        }
    }
}
