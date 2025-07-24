using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace ArcGisAutoCAD
{
    public static class AcadUtils
    {
        /// <summary>
        /// Removes all entities from the specified AutoCAD layer in the active drawing.
        /// </summary>
        /// <param name="layerName">The name of the AutoCAD layer to clear.</param>
        public static void ClearLayerContents(string layerName)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var toDelete = new List<ObjectId>();
                foreach (ObjectId objId in ms)
                {
                    var ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                    if (ent != null && ent.Layer == layerName)
                        toDelete.Add(objId);
                }
                foreach (var id in toDelete)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    ent?.Erase();
                }
                tr.Commit();
            }
        }
    }
}
