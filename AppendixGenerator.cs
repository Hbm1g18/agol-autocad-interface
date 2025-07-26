using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.EditorInput;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArcGisAutoCAD
{
    public static class AppendixGenerator
    {
        public static void CreateLayout(AppendixTemplate template, Dictionary<string, object> record, string imageField, string commentField)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;
            var layoutName = $"APP_{record["id"]}";
            var createdLayouts = new List<(string Name, ObjectId LayoutId)>();

            try
            {
                using (doc.LockDocument())
                using (var templateDb = new Database(false, true))
                {
                    // Open the template drawing
                    templateDb.ReadDwgFile(template.DwgPath, FileOpenMode.OpenForReadAndAllShare, true, null);

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        // Check if layout already exists
                        var layoutMgr = LayoutManager.Current;
                        if (layoutMgr.LayoutExists(layoutName))
                        {
                            ed.WriteMessage($"\nLayout '{layoutName}' already exists.");
                            return;
                        }

                        // Copy the layout from template
                        using (var templateTr = templateDb.TransactionManager.StartTransaction())
                        {
                            // Get source layout (always use "Layout1" from template)
                            string sourceLayoutName = "Layout1";
                            var templateLayouts = (DBDictionary)templateTr.GetObject(
                                templateDb.LayoutDictionaryId, OpenMode.ForRead);
                            
                            if (!templateLayouts.Contains(sourceLayoutName))
                            {
                                throw new Exception($"Layout '{sourceLayoutName}' not found in template.");
                            }

                            var sourceLayoutId = templateLayouts.GetAt(sourceLayoutName);
                            var sourceLayout = (Layout)templateTr.GetObject(sourceLayoutId, OpenMode.ForRead);
                            var sourceBtr = (BlockTableRecord)templateTr.GetObject(
                                sourceLayout.BlockTableRecordId, OpenMode.ForRead);

                            // Create new layout in target database
                            var newLayoutId = layoutMgr.CreateLayout(layoutName);
                            createdLayouts.Add((layoutName, newLayoutId));
                            var newLayout = (Layout)tr.GetObject(newLayoutId, OpenMode.ForWrite);
                            var destBtr = (BlockTableRecord)tr.GetObject(
                                newLayout.BlockTableRecordId, OpenMode.ForWrite);

                            // Clone all objects using WblockCloneObjects
                            var idMap = new IdMapping();
                            templateDb.WblockCloneObjects(
                                new ObjectIdCollection(sourceBtr.Cast<ObjectId>().ToArray()),
                                destBtr.ObjectId,
                                idMap,
                                DuplicateRecordCloning.Replace,
                                false);

                            // Copy layout properties
                            newLayout.CopyFrom(sourceLayout);

                            templateTr.Commit();
                        }

                        // Now handle the image and attributes in the new layout
                        string imagePath = record.TryGetValue(imageField, out var ival) ? ival?.ToString() : "";
                        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                        {
                            // Insert image in modelspace (same as before)
                            double rectWidth = 150, rectHeight = 110;
                            int id = Convert.ToInt32(record["id"]);
                            var basePoint = new Point3d(id * -1000, 0, 0);
                            string imageName = Path.GetFileNameWithoutExtension(imagePath);

                            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                            ObjectId layerId;
                            if (!lt.Has(imageName))
                            {
                                lt.UpgradeOpen();
                                var ltr = new LayerTableRecord { Name = imageName };
                                lt.Add(ltr);
                                tr.AddNewlyCreatedDBObject(ltr, true);
                            }
                            layerId = lt[imageName];

                            var rect = new Polyline(4)
                            {
                                Closed = true,
                                LayerId = layerId
                            };
                            rect.AddVertexAt(0, new Point2d(basePoint.X, basePoint.Y), 0, 0, 0);
                            rect.AddVertexAt(1, new Point2d(basePoint.X + rectWidth, basePoint.Y), 0, 0, 0);
                            rect.AddVertexAt(2, new Point2d(basePoint.X + rectWidth, basePoint.Y + rectHeight), 0, 0, 0);
                            rect.AddVertexAt(3, new Point2d(basePoint.X, basePoint.Y + rectHeight), 0, 0, 0);
                            
                            var modelSpace = (BlockTableRecord)tr.GetObject(
                                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                            modelSpace.AppendEntity(rect);
                            tr.AddNewlyCreatedDBObject(rect, true);

                            // Insert the image (same as before)
                            RasterImage.EnableReactors(true);
                            var imageDef = new RasterImageDef { SourceFileName = imagePath };
                            imageDef.Load();

                            ObjectId dictId = RasterImageDef.GetImageDictionary(db);
                            if (dictId.IsNull)
                                dictId = RasterImageDef.CreateImageDictionary(db);
                            var imageDict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);

                            string imageKey = "IMG_" + imageName;
                            if (!imageDict.Contains(imageKey))
                            {
                                imageDict.SetAt(imageKey, imageDef);
                                tr.AddNewlyCreatedDBObject(imageDef, true);
                            }

                            var image = new RasterImage
                            {
                                ImageDefId = imageDict.GetAt(imageKey),
                                ShowImage = true,
                                LayerId = layerId
                            };

                            Vector3d u = new Vector3d(rectWidth, 0, 0);
                            Vector3d v = new Vector3d(0, rectHeight, 0);
                            image.Orientation = new CoordinateSystem3d(basePoint, u, v);
                            modelSpace.AppendEntity(image);
                            tr.AddNewlyCreatedDBObject(image, true);
                            image.AssociateRasterDef(imageDef);

                            // Get the new layout's paperspace
                            var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                            var layout = (Layout)tr.GetObject(layoutDict.GetAt(layoutName), OpenMode.ForRead);
                            var paperSpace = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

                            // Update the COMMENT attribute in the layout
                            string commentValue = record.TryGetValue(commentField, out var cval) ? cval?.ToString() : "";
                            string idValue = record["id"]?.ToString(); // Get the ID from the record

                            // Format the comment as "Photograph {ID}: commentValue"
                            string formattedComment = $"Photograph {idValue}: {commentValue}";
                            foreach (ObjectId objId in paperSpace)
                            {
                                var ent = tr.GetObject(objId, OpenMode.ForWrite) as BlockReference;
                                if (ent == null) continue;
                                
                                var brDef = (BlockTableRecord)tr.GetObject(ent.BlockTableRecord, OpenMode.ForRead);
                                if (brDef.Name == "APPENDIX_TEMPLATE")
                                {
                                    foreach (ObjectId attId in ent.AttributeCollection)
                                    {
                                        if (tr.GetObject(attId, OpenMode.ForWrite) is AttributeReference attRef && attRef.Tag == "COMMENT")
                                        {
                                            attRef.TextString = formattedComment;
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                        

                        tr.Commit();
                    }
                    using (var tr2 = db.TransactionManager.StartTransaction())
                    {
                        // First ensure the Defpoints layer exists
                        LayerTable lt = (LayerTable)tr2.GetObject(db.LayerTableId, OpenMode.ForRead);
                        if (!lt.Has("Defpoints"))
                        {
                            lt.UpgradeOpen();
                            LayerTableRecord ltr = new LayerTableRecord { Name = "Defpoints" };
                            lt.Add(ltr);
                            tr2.AddNewlyCreatedDBObject(ltr, true);
                        }

                        foreach (var (lname, layoutId) in createdLayouts)
                        {
                            try
                            {
                                // Skip default layouts (Layout1 and Layout2)
                                if (lname.Equals("Layout1", StringComparison.OrdinalIgnoreCase) || 
                                    lname.Equals("Layout2", StringComparison.OrdinalIgnoreCase))
                                {
                                    ed.WriteMessage($"\nSkipping viewport creation for default layout: {lname}");
                                    continue;
                                }

                                // Verify layout still exists
                                if (!layoutId.IsValid || layoutId.IsErased || layoutId.IsNull)
                                {
                                    ed.WriteMessage($"\nLayout ID invalid for {lname}");
                                    continue;
                                }

                                var layout = (Layout)tr2.GetObject(layoutId, OpenMode.ForRead);

                                if (layout.ModelType)
                                    continue;

                                // Verify BlockTableRecord exists
                                if (layout.BlockTableRecordId.IsNull)
                                {
                                    ed.WriteMessage($"\nNo paperspace block found for layout {lname}");
                                    continue;
                                }

                                var paperSpace = (BlockTableRecord)tr2.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

                                // Viewport parameters
                                double rectWidth = 150;
                                double rectHeight = 110;
                                double viewportHeight = 100;
                                double aspect = rectWidth / rectHeight;
                                double viewportWidth = viewportHeight * aspect;
                                Point3d paperCenter = new Point3d(108, 203, 0);

                                // Get ID from layout name
                                string idStr = lname.Replace("APP_", "");
                                if (!int.TryParse(idStr, out int id))
                                {
                                    ed.WriteMessage($"\nInvalid ID in layout name: {lname}");
                                    continue;
                                }

                                // Model space center point
                                double baseX = id * -1000;
                                Point2d modelCenter = new Point2d(baseX + rectWidth / 2.0, rectHeight / 2.0);

                                // Create viewport
                                var viewport = new Viewport
                                {
                                    CenterPoint = paperCenter,
                                    Width = viewportWidth,
                                    Height = viewportHeight,
                                    ViewCenter = modelCenter,
                                    ViewHeight = rectHeight,
                                    Layer = "Defpoints"
                                };

                                viewport.SetDatabaseDefaults();
                                paperSpace.AppendEntity(viewport);
                                tr2.AddNewlyCreatedDBObject(viewport, true);

                                // Activate and update viewport
                                viewport.On = true;
                                viewport.UpdateDisplay();
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception ex)
                            {
                                ed.WriteMessage($"\nDictionary key not found for layout {lname}: {ex.Message}");
                            }
                            catch (Exception ex)
                            {
                                ed.WriteMessage($"\nGeneral error creating viewport for {lname}: {ex.Message}");
                            }
                        }
                        tr2.Commit();
                    }

                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nError creating appendix layout: {ex.Message}");
            }
        }

        
    }

    
}