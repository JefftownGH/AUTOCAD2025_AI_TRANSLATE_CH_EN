using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCAD.AITranslate
{
    internal static class LayerUtils
    {
        public static ObjectId EnsureLayer(Database db, Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                return lt[layerName];
            }

            lt.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = layerName
            };
            var id = lt.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }
    }
}
