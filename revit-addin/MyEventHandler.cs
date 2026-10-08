// Suppress IDE style suggestions in bulk (no build/runtime effect)
#pragma warning disable IDE0019 // Use pattern matching
#pragma warning disable IDE0028 // Simplify collection initialization
#pragma warning disable IDE0031 // Simplify null check
#pragma warning disable IDE0060 // Unused parameter
#pragma warning disable IDE0220 // Implicit cast in foreach
#pragma warning disable IDE0270 // Simplify null check
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ALIS
{
    public class MyEventHandler : IExternalEventHandler
    {
        public enum EventID
        {
            None = 0,
            FullAnalysis,
            ApplyDesign,
            ResetToInitial
        }

        public EventID RequestId { get; set; } = EventID.None;
        public MyWindow AppWindow { get; set; }

        public string GetName()
        {
            return "ALIS External Event Handler";
        }

        public void Execute(UIApplication app)
        {
            try
            {
                switch (RequestId)
                {
                    case EventID.None:
                        return;
                    case EventID.FullAnalysis:
                        ExecuteFullAnalysis(app);
                        break;
                    case EventID.ApplyDesign:
                        ApplyDesignFromRevised(app);
                        break;
                    case EventID.ResetToInitial:
                        ResetToInitialState(app);
                        break;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Execution error:\n{ex.Message}");
            }
            finally
            {
                try
                {
                    if (AppWindow != null)
                    {
                        AppWindow.Dispatcher.Invoke(() =>
                        {
                            // Load the Before image and chart only for FullAnalysis
                            if (RequestId == EventID.FullAnalysis)
                            {
                                AppWindow.LoadImagesFromTemp();
                            }
                            // Load the After image and chart only for ApplyDesign
                            else if (RequestId == EventID.ApplyDesign)
                            {
                                AppWindow.LoadAfterImagesOnly();
                            }

                            AppWindow.Show();
                            AppWindow.Activate();
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UI restore error: {ex.Message}");
                }

                RequestId = EventID.None;
            }
        }

        private void ExecuteFullAnalysis(UIApplication app)
        {
            bool bimSuccess = false;
            string errorMessage = "";

            try
            {
                // BIM analysis (rendering is done by Rendering.py)
                try
                {
                    AnalyzeBIMAndFillWindowsXAML(app);
                    bimSuccess = true;
                    System.Diagnostics.Debug.WriteLine("✅ BIM analysis done");
                }
                catch (Exception ex)
                {
                    errorMessage = $"BIM analysis failed: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"❌ {errorMessage}");
                }

                if (!bimSuccess)
                {
                    TaskDialog.Show("Warning", $"BIM analysis failed:\n{errorMessage}");
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Full analysis error:\n{ex.Message}");
            }
        }
        private void ResetToInitialState(UIApplication app)
        {
            // C# side reset: called from ResetButton_Click in MyWindow.xaml.cs after Temp cleanup
            // current_analysis.txt may not exist, so handle it quietly
            try
            {
                System.Diagnostics.Debug.WriteLine("[ALIS] ResetToInitialState run");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ALIS] Reset error: {ex.Message}");
            }
        }
        private static readonly Dictionary<string, (int R, int G, int B)> ColorNames =
            new Dictionary<string, (int R, int G, int B)>
        {
            { "Beige",  (226, 191, 155) },
            { "Gray",   (128, 128, 128) },
            { "Grey",   (128, 128, 128) },
            { "White",  (255, 255, 255) },
            { "Black",  (0, 0, 0) },
            { "Brown",  (165, 42, 42) },
            { "Blue",   (0, 0, 255) },
            { "Green",  (0, 255, 0) },
            { "Red",    (255, 0, 0) },
            { "Yellow", (255, 255, 0) }
        };

        private static string GetNearestColorName(int r, int g, int b)
        {
            string closestColorName = "Unknown";
            double smallestDistance = double.MaxValue;

            foreach (var kvp in ColorNames)
            {
                var (cr, cg, cb) = kvp.Value;
                double distance = Math.Sqrt(
                    Math.Pow(r - cr, 2) +
                    Math.Pow(g - cg, 2) +
                    Math.Pow(b - cb, 2));

                if (distance < smallestDistance)
                {
                    smallestDistance = distance;
                    closestColorName = kvp.Key;
                }
            }

            return closestColorName;
        }
        private void AnalyzeBIMAndFillWindowsXAML(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;

            StringBuilder result = new StringBuilder();

            try
            {
                // 1. Ceiling Height analysis
                string ceilingHeightM = "0.00";

                try
                {
                    var ceilings = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Ceilings)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    System.Diagnostics.Debug.WriteLine($"[Ceiling analysis] Ceilings found: {ceilings.Count}");

                    foreach (Element ceiling in ceilings)
                    {
                        Parameter param = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                        if (param != null && param.HasValue)
                        {
                            double heightFeet = param.AsDouble();
                            double heightMeters = heightFeet * 0.3048;
                            ceilingHeightM = heightMeters.ToString("F2");

                            System.Diagnostics.Debug.WriteLine($"  ✅ Ceiling height: {heightMeters:F2}m");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Ceiling error] {ex.Message}");
                }

                result.AppendLine($"Ceiling Height: {ceilingHeightM}");

                // 2. WWR analysis - based on GROSS wall area (length × user_height)
                // ⚠️ Important: use the same basis as UpdateWWR (do not use HOST_AREA_COMPUTED, it is NET)
                // GROSS = full wall area including openings, the standard WWR definition in architecture
                double wwrValue = 0.0;
                try
                {
                    var winCollector = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance))
                        .OfCategory(BuiltInCategory.OST_Windows);

                    double totalWinArea = 0.0;
                    double totalHostWallArea = 0.0;
                    var hostWallIds = new System.Collections.Generic.HashSet<ElementId>();

                    foreach (Element we in winCollector)
                    {
                        FamilyInstance win = we as FamilyInstance;
                        if (win == null) continue;

                        // ── Real window area: BoundingBox X (width) × Z (height) ──
                        double winArea = 0;
                        var bb = win.get_BoundingBox(null);
                        if (bb != null)
                        {
                            double bbX = Math.Abs(bb.Max.X - bb.Min.X); // ft
                            double bbZ = Math.Abs(bb.Max.Z - bb.Min.Z); // ft
                            winArea = bbX * bbZ * 0.0929; // ft² → m²
                            System.Diagnostics.Debug.WriteLine("[WWR] Window[" + win.Id + "] " + win.Symbol.Name +
                                " X=" + (bbX * 304.8).ToString("F0") + "mm Z=" + (bbZ * 304.8).ToString("F0") +
                                "mm → area=" + winArea.ToString("F3") + "m2");
                        }
                        totalWinArea += winArea;

                        // ── Host wall GROSS area (length × height, no duplicates) ──
                        Wall hostWall = win.Host as Wall;
                        if (hostWall != null && !hostWallIds.Contains(hostWall.Id))
                        {
                            hostWallIds.Add(hostWall.Id);
                            LocationCurve hwLoc = hostWall.Location as LocationCurve;
                            Parameter pHwH = hostWall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                            if (hwLoc != null && pHwH != null && pHwH.AsDouble() > 0.001)
                            {
                                double hwLenM = hwLoc.Curve.Length * 0.3048;
                                double hwHM = pHwH.AsDouble() * 0.3048;
                                double hwGrossM2 = hwLenM * hwHM;
                                totalHostWallArea += hwGrossM2;
                                System.Diagnostics.Debug.WriteLine("[WWR] HostWall[" + hostWall.Id + "] " +
                                    hostWall.Name + " L=" + hwLenM.ToString("F2") + "m H=" + hwHM.ToString("F2") +
                                    "m GROSS=" + hwGrossM2.ToString("F2") + "m2");
                            }
                        }
                    }

                    if (totalHostWallArea > 0.001 && totalWinArea > 0.001)
                        wwrValue = Math.Round(totalWinArea / totalHostWallArea * 100.0, 1);
                    else
                        wwrValue = 0;

                    System.Diagnostics.Debug.WriteLine("[WWR] WindowTotal=" + totalWinArea.ToString("F3") +
                        "m2 HostWall GROSS=" + totalHostWallArea.ToString("F2") + "m2 WWR=" + wwrValue + "%");
                }
                catch (Exception exWWR)
                {
                    System.Diagnostics.Debug.WriteLine("[WWR error] " + exWWR.Message);
                    wwrValue = 0;
                }

                result.AppendLine("WWR: " + wwrValue.ToString("F1"));

                // 3. CCT analysis
                string lightCCTValue = "4000";

                try
                {
                    var lights = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_LightingFixtures)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    System.Diagnostics.Debug.WriteLine($"[Lighting analysis] Lights found: {lights.Count}");

                    foreach (Element light in lights)
                    {
                        Parameter instParam = light.get_Parameter(BuiltInParameter.FBX_LIGHT_INITIAL_COLOR_TEMPERATURE);
                        if (instParam != null && instParam.HasValue)
                        {
                            lightCCTValue = instParam.AsDouble().ToString("F0");
                            System.Diagnostics.Debug.WriteLine($"  ✅ Light instance CCT: {lightCCTValue}K");
                            break;
                        }

                        ElementType lightType = doc.GetElement(light.GetTypeId()) as ElementType;
                        if (lightType != null)
                        {
                            Parameter typeParam = lightType.get_Parameter(BuiltInParameter.FBX_LIGHT_INITIAL_COLOR_TEMPERATURE);
                            if (typeParam != null && typeParam.HasValue)
                            {
                                lightCCTValue = typeParam.AsDouble().ToString("F0");
                                System.Diagnostics.Debug.WriteLine($"  ✅ Light type CCT: {lightCCTValue}K");
                                break;
                            }
                        }
                    }

                    if (lights.Count == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("  ⚠️ No light found, using default: 4000K");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Lighting error] {ex.Message}");
                }

                result.AppendLine($"CCT: {lightCCTValue}");

                // 4. Wall material color analysis
                string wallColor = "Beige";
                FilteredElementCollector wallCollector = new FilteredElementCollector(doc);
                Wall wall = wallCollector
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .FirstOrDefault();

                if (wall != null)
                {
                    ElementId wallTypeId = wall.GetTypeId();
                    WallType wallType = doc.GetElement(wallTypeId) as WallType;

                    if (wallType != null)
                    {
                        CompoundStructure structure = wallType.GetCompoundStructure();
                        if (structure != null)
                        {
                            IList<CompoundStructureLayer> layers = structure.GetLayers();
                            foreach (var layer in layers)
                            {
                                if (layer.Function == MaterialFunctionAssignment.Finish1 ||
                                    layer.Function == MaterialFunctionAssignment.Structure)
                                {
                                    ElementId materialId = layer.MaterialId;
                                    if (materialId != ElementId.InvalidElementId)
                                    {
                                        Material material = doc.GetElement(materialId) as Material;
                                        if (material != null)
                                        {
                                            Color color = material.Color;
                                            wallColor = GetNearestColorName(color.Red, color.Green, color.Blue);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                result.AppendLine($"Room Color: {wallColor}");

                // 5. Floor material image file name analysis
                string floorTexture = "wood";
                string floorAnalysisLog = @"C:\Temp\floor_analysis_debug.txt";
                File.WriteAllText(floorAnalysisLog, $"=== Floor analysis start: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");

                FilteredElementCollector floorCollector = new FilteredElementCollector(doc);
                var allFloors = floorCollector
                    .OfClass(typeof(Floor))
                    .Cast<Floor>()
                    .ToList();

                File.AppendAllText(floorAnalysisLog, $"Total floors: {allFloors.Count}\n");

                // Print all floor IDs
                File.AppendAllText(floorAnalysisLog, $"\n=== All floor IDs ===\n");
                foreach (Floor f in allFloors)
                {
                    FloorType ft = doc.GetElement(f.GetTypeId()) as FloorType;
                    string typeName = ft != null ? ft.Name : "Unknown";
                    File.AppendAllText(floorAnalysisLog, $"  ID: {f.Id.IntegerValue} - type: '{typeName}'\n");
                }
                File.AppendAllText(floorAnalysisLog, $"\n");

                if (allFloors.Count > 0)
                {
                    // Interior floor ID (priority)
                    int targetFloorId = 13443083;
                    Floor targetFloor = allFloors.FirstOrDefault(f => f.Id.IntegerValue == targetFloorId);

                    if (targetFloor != null)
                    {
                        File.AppendAllText(floorAnalysisLog, $"✅ Interior floor found (ID: {targetFloorId})\n");
                        System.Diagnostics.Debug.WriteLine($"[Floor analysis] Interior floor found (ID: {targetFloorId})");
                    }
                    else
                    {
                        File.AppendAllText(floorAnalysisLog, $"⚠️ Interior floor ID {targetFloorId} not found - using the first floor\n");
                        targetFloor = allFloors[0];
                    }

                    // Print all floor types (for reference)
                    HashSet<ElementId> processedTypes = new HashSet<ElementId>();
                    File.AppendAllText(floorAnalysisLog, $"\n=== All floor type info ===\n");

                    foreach (Floor floor in allFloors)
                    {
                        FloorType floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                        if (floorType == null || processedTypes.Contains(floorType.Id))
                            continue;

                        processedTypes.Add(floorType.Id);

                        File.AppendAllText(floorAnalysisLog, $"\n━━━━━━━━━━━━━━━━━━━━━━━━━━\n");
                        File.AppendAllText(floorAnalysisLog, $"Floor type: '{floorType.Name}'\n");

                        CompoundStructure structure = floorType.GetCompoundStructure();
                        if (structure != null && structure.GetLayers().Count > 0)
                        {
                            IList<CompoundStructureLayer> layers = structure.GetLayers();
                            File.AppendAllText(floorAnalysisLog, $"Layer count: {layers.Count}\n");

                            // Print all layers
                            for (int i = 0; i < layers.Count; i++)
                            {
                                CompoundStructureLayer layer = layers[i];
                                ElementId materialId = layer.MaterialId;

                                File.AppendAllText(floorAnalysisLog, $"\n[Layer {i + 1}]\n");
                                File.AppendAllText(floorAnalysisLog, $"  Thickness: {layer.Width}\n");
                                File.AppendAllText(floorAnalysisLog, $"  Material ID: {materialId}\n");

                                if (materialId != ElementId.InvalidElementId)
                                {
                                    Material material = doc.GetElement(materialId) as Material;
                                    if (material != null)
                                    {
                                        File.AppendAllText(floorAnalysisLog, $"  Material name: '{material.Name}'\n");
                                    }
                                }
                            }
                        }
                    }

                    File.AppendAllText(floorAnalysisLog, $"\n━━━━━━━━━━━━━━━━━━━━━━━━━━\n");
                    File.AppendAllText(floorAnalysisLog, $"=== Analyzing interior floor (ID: {targetFloor.Id.IntegerValue}) ===\n\n");

                    // Actual analysis: first layer of the interior floor
                    FloorType selectedFloorType = doc.GetElement(targetFloor.GetTypeId()) as FloorType;

                    if (selectedFloorType != null)
                    {
                        File.AppendAllText(floorAnalysisLog, $"Selected floor type: '{selectedFloorType.Name}'\n");

                        CompoundStructure structure = selectedFloorType.GetCompoundStructure();
                        if (structure != null && structure.GetLayers().Count > 0)
                        {
                            IList<CompoundStructureLayer> layers = structure.GetLayers();

                            // Check only the first layer (finish)
                            CompoundStructureLayer topLayer = layers[0];
                            ElementId materialId = topLayer.MaterialId;

                            File.AppendAllText(floorAnalysisLog, $"First layer material ID: {materialId}\n");
                            System.Diagnostics.Debug.WriteLine($"[Floor analysis] First layer material ID: {materialId}");

                            if (materialId != ElementId.InvalidElementId)
                            {
                                Material material = doc.GetElement(materialId) as Material;
                                if (material != null)
                                {
                                    File.AppendAllText(floorAnalysisLog, $"\n=== Material analysis start ===\n");
                                    File.AppendAllText(floorAnalysisLog, $"Material ID: {materialId}\n");
                                    File.AppendAllText(floorAnalysisLog, $"Material name: '{material.Name}'\n");
                                    System.Diagnostics.Debug.WriteLine($"[Floor analysis] Material name: '{material.Name}'");

                                    // Get the floor material from the material name
                                    string materialName = material.Name;

                                    bool foundFromName = false;

                                    // Extract from the Floor_ pattern
                                    if (materialName.StartsWith("Floor_", StringComparison.OrdinalIgnoreCase))
                                    {
                                        // "Floor_marble" → "marble"
                                        // "Floor_Finish_tile" → "finish_tile"
                                        string extracted = materialName.Substring(6).ToLower(); // "Floor_".Length = 6

                                        File.AppendAllText(floorAnalysisLog, $"Floor_ pattern found\n");
                                        File.AppendAllText(floorAnalysisLog, $"  Original: '{materialName}'\n");
                                        File.AppendAllText(floorAnalysisLog, $"  Extracted: '{extracted}'\n");

                                        // finish_tile → tile
                                        if (extracted.Contains("finish_"))
                                        {
                                            string cleaned = extracted.Replace("finish_", "");
                                            File.AppendAllText(floorAnalysisLog, $"  Removed 'finish_': '{cleaned}'\n");
                                            floorTexture = cleaned;
                                        }
                                        else
                                        {
                                            floorTexture = extracted;
                                        }

                                        File.AppendAllText(floorAnalysisLog, $"✅ Final result: '{floorTexture}'\n");
                                        System.Diagnostics.Debug.WriteLine($"[Floor analysis] Extracted from Floor_ pattern: {materialName} → {floorTexture}");
                                        foundFromName = true;
                                    }
                                    else
                                    {
                                        // Not a Floor_ pattern: search keywords in the full name
                                        string lowerName = materialName.ToLower();
                                        Dictionary<string, string> keywordMap = new Dictionary<string, string>
                                        {
                                            { "marble", "marble" },
                                            { "tile", "tile" },
                                            { "wood", "wood" },
                    { "wooden", "wood" },
                                            { "concrete", "concrete" },
                                            { "carpet", "carpet" },
                                            { "stone", "stone" }
                                        };

                                        foreach (var kvp in keywordMap)
                                        {
                                            if (lowerName.Contains(kvp.Key))
                                            {
                                                floorTexture = kvp.Value;
                                                foundFromName = true;
                                                File.AppendAllText(floorAnalysisLog, $"✅ Extracted from keyword: {materialName} → {floorTexture}\n");
                                                System.Diagnostics.Debug.WriteLine($"[Floor analysis] Extracted from keyword: {floorTexture}");
                                                break;
                                            }
                                        }
                                    }

                                    // Check the properties of the Material itself
                                    File.AppendAllText(floorAnalysisLog, $"=== Material basic info ===\n");
                                    File.AppendAllText(floorAnalysisLog, $"Color: {material.Color.Red}, {material.Color.Green}, {material.Color.Blue}\n");
                                    File.AppendAllText(floorAnalysisLog, $"Transparency: {material.Transparency}\n");

                                    // Skip the Asset info if the name already gave a result
                                    if (!foundFromName)
                                    {
                                        // Try to read the image from the Asset
                                        ElementId assetId = material.AppearanceAssetId;
                                        File.AppendAllText(floorAnalysisLog, $"Asset ID: {assetId}\n");

                                        if (assetId != ElementId.InvalidElementId)
                                        {
                                            AppearanceAssetElement assetElement = doc.GetElement(assetId) as AppearanceAssetElement;
                                            if (assetElement != null)
                                            {
                                                File.AppendAllText(floorAnalysisLog, $"Asset name: {assetElement.Name}\n");
                                                System.Diagnostics.Debug.WriteLine($"[Floor analysis] Asset name: {assetElement.Name}");

                                                try
                                                {
                                                    Asset asset = assetElement.GetRenderingAsset();
                                                    if (asset != null)
                                                    {
                                                        File.AppendAllText(floorAnalysisLog, $"Asset property count: {asset.Size}\n");

                                                        // Check the unifiedbitmap_Bitmap property first
                                                        AssetProperty bitmapProp = asset.FindByName("unifiedbitmap_Bitmap");
                                                        if (bitmapProp != null)
                                                        {
                                                            File.AppendAllText(floorAnalysisLog, $"unifiedbitmap_Bitmap property found (type: {bitmapProp.GetType().Name})\n");

                                                            if (bitmapProp is AssetPropertyString bitmapString)
                                                            {
                                                                string imagePath = bitmapString.Value;
                                                                File.AppendAllText(floorAnalysisLog, $"Image Path: '{imagePath}'\n");

                                                                if (!string.IsNullOrEmpty(imagePath))
                                                                {
                                                                    string fileName = Path.GetFileNameWithoutExtension(imagePath);
                                                                    floorTexture = fileName.ToLower();
                                                                    File.AppendAllText(floorAnalysisLog, $"✅ File name extracted: {fileName} → {floorTexture}\n");
                                                                    System.Diagnostics.Debug.WriteLine($"[Floor analysis] Image found: {imagePath} → {floorTexture}");
                                                                }
                                                                else
                                                                {
                                                                    File.AppendAllText(floorAnalysisLog, $"⚠️ Image path is empty\n");
                                                                }
                                                            }
                                                            else
                                                            {
                                                                File.AppendAllText(floorAnalysisLog, $"⚠️ Not an AssetPropertyString\n");
                                                            }
                                                        }
                                                        else
                                                        {
                                                            File.AppendAllText(floorAnalysisLog, $"No unifiedbitmap_Bitmap property - searching other properties\n");

                                                            // Look for other image/bitmap properties
                                                            bool foundImage = false;
                                                            for (int i = 0; i < asset.Size; i++)
                                                            {
                                                                AssetProperty prop = asset.Get(i);
                                                                string propNameLower = prop.Name.ToLower();

                                                                if (propNameLower.Contains("image") || propNameLower.Contains("bitmap"))
                                                                {
                                                                    File.AppendAllText(floorAnalysisLog, $"  Property found: {prop.Name} ({prop.GetType().Name})\n");

                                                                    if (prop is AssetPropertyString stringProp)
                                                                    {
                                                                        string imagePath = stringProp.Value;
                                                                        if (!string.IsNullOrEmpty(imagePath))
                                                                        {
                                                                            string fileName = Path.GetFileNameWithoutExtension(imagePath);
                                                                            floorTexture = fileName.ToLower();
                                                                            File.AppendAllText(floorAnalysisLog, $"  ✅ File name extracted: {fileName} → {floorTexture}\n");
                                                                            System.Diagnostics.Debug.WriteLine($"[Floor analysis] Image found ({prop.Name}): {imagePath} → {floorTexture}");
                                                                            foundImage = true;
                                                                            break;
                                                                        }
                                                                    }
                                                                }
                                                            }

                                                            if (!foundImage)
                                                            {
                                                                File.AppendAllText(floorAnalysisLog, $"❌ Image property not found\n");
                                                                File.AppendAllText(floorAnalysisLog, $"=== All properties ===\n");
                                                                for (int i = 0; i < asset.Size; i++)
                                                                {
                                                                    AssetProperty prop = asset.Get(i);
                                                                    File.AppendAllText(floorAnalysisLog, $"  [{i}] {prop.Name} ({prop.GetType().Name})\n");
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        File.AppendAllText(floorAnalysisLog, $"❌ Asset is null\n");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    File.AppendAllText(floorAnalysisLog, $"❌ Asset read error: {ex.Message}\n");
                                                    System.Diagnostics.Debug.WriteLine($"[Floor analysis] Asset read error: {ex.Message}");
                                                }
                                            }
                                            else
                                            {
                                                File.AppendAllText(floorAnalysisLog, $"❌ AppearanceAssetElement is null\n");
                                            }
                                        }
                                        else
                                        {
                                            File.AppendAllText(floorAnalysisLog, $"❌ No Asset\n");
                                            System.Diagnostics.Debug.WriteLine($"[Floor analysis] No Asset");
                                        }
                                    }
                                    else
                                    {
                                        File.AppendAllText(floorAnalysisLog, $"❌ Material is null\n");
                                    }
                                }
                                else
                                {
                                    File.AppendAllText(floorAnalysisLog, $"❌ No material on the first layer\n");
                                }
                            }
                            else
                            {
                                File.AppendAllText(floorAnalysisLog, $"❌ No CompoundStructure or 0 layers\n");
                            }
                        }
                        else
                        {
                            File.AppendAllText(floorAnalysisLog, $"❌ FloorType is null\n");
                        }
                    }

                    File.AppendAllText(floorAnalysisLog, $"\nFinal result: {floorTexture}\n");
                    System.Diagnostics.Debug.WriteLine($"[Floor analysis] Final result: {floorTexture}");
                    result.AppendLine($"Floor Material: {floorTexture}");

                    string outputPath = @"C:\Temp\current_analysis.txt";
                    File.WriteAllText(outputPath, result.ToString());

                    System.Diagnostics.Debug.WriteLine($"✅ BIM analysis done:\n{result}");

                    // ── Save a Revit viewport screenshot (rendering reference) ──
                    try
                    {
                        ExportViewportImage(doc, @"C:\Temp\rendering_reference_camera.png");
                    }
                    catch (Exception exImg)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ExportImage] Failed (ignored): {exImg.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"BIM analysis error: {ex.Message}");
            }
        }

        private void ExportViewportImage(Document doc, string destPath)
        {
            try
            {
                // Save to exportPath in the temp folder
                string tempDir = @"C:\Temp";
                string tempName = "rendering_reference_camera";

                ImageExportOptions opts = new ImageExportOptions
                {
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = 1024,
                    ImageResolution = ImageResolution.DPI_150,
                    FitDirection = FitDirectionType.Horizontal,
                    ExportRange = ExportRange.CurrentView,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    FilePath = System.IO.Path.Combine(tempDir, tempName)
                };

                doc.ExportImage(opts);

                // ExportImage adds the view name to the file name -> find the newest file and rename it
                System.Threading.Thread.Sleep(300); // Wait for the file write
                var candidates = System.IO.Directory.GetFiles(tempDir, tempName + "*.png")
                    .OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).ToArray();

                if (candidates.Length > 0 && candidates[0] != destPath)
                {
                    System.IO.File.Copy(candidates[0], destPath, true);
                    System.Diagnostics.Debug.WriteLine($"[ExportImage] Viewport screenshot saved: {destPath}");
                }
                else if (candidates.Length > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExportImage] Path already correct: {destPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExportViewportImage] error: {ex.Message}");
                throw;
            }
        }

        private void ApplyDesignFromRevised(UIApplication app)
        {
            try
            {
                string revisedFile = @"C:\Temp\current_analysis.txt";
                if (!File.Exists(revisedFile))
                {
                    throw new Exception("current_analysis.txt not found.");
                }

                Dictionary<string, string> revisedData = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(revisedFile))
                {
                    if (line.Contains(":"))
                    {
                        string[] parts = line.Split(':');
                        if (parts.Length == 2)
                        {
                            revisedData[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }

                UIDocument uidoc = app.ActiveUIDocument;
                Document doc = uidoc.Document;

                // Track the items that succeeded
                List<string> successItems = new List<string>();
                List<string> failedItems = new List<string>();

                using (Transaction trans = new Transaction(doc, "Apply Revised Design"))
                {
                    trans.Start();

                    // 1. Ceiling Height
                    if (revisedData.ContainsKey("Ceiling Height"))
                    {
                        try
                        {
                            double targetHeight = double.Parse(revisedData["Ceiling Height"]);
                            int updated = UpdateCeilingHeight(doc, targetHeight);
                            if (updated > 0)
                            {
                                successItems.Add($"Ceiling Height: {targetHeight}m ({updated} items)");
                                System.Diagnostics.Debug.WriteLine($"✅ Ceiling height: {targetHeight}m");
                            }
                            else
                            {
                                failedItems.Add("Ceiling Height (no modifiable elements)");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedItems.Add($"Ceiling Height: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"⚠️ Ceiling update failed: {ex.Message}");
                        }
                    }

                    // 2. WWR (Window to Wall Ratio)
                    if (revisedData.ContainsKey("WWR"))
                    {
                        try
                        {
                            double targetWWR = double.Parse(revisedData["WWR"]);
                            int updated = UpdateWWR(doc, targetWWR);
                            if (updated > 0)
                            {
                                successItems.Add($"WWR: {targetWWR}% ({updated} items)");
                                System.Diagnostics.Debug.WriteLine($"✅ WWR: {targetWWR}%");
                            }
                            else
                            {
                                failedItems.Add("WWR (no modifiable windows)");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedItems.Add($"WWR: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"⚠️ WWR update failed: {ex.Message}");
                        }
                    }

                    // 3. CCT
                    if (revisedData.ContainsKey("CCT"))
                    {
                        try
                        {
                            double targetCCT = double.Parse(revisedData["CCT"]);
                            int updated = UpdateLightingCCT(doc, targetCCT);
                            if (updated > 0)
                            {
                                successItems.Add($"Light CCT: {targetCCT}K ({updated} items)");
                                System.Diagnostics.Debug.WriteLine($"✅ CCT: {targetCCT}K");
                            }
                            else
                            {
                                failedItems.Add("Light CCT (no modifiable elements)");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedItems.Add($"Light CCT: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"⚠️ CCT failed: {ex.Message}");
                        }
                    }

                    // 4. Wall Color
                    if (revisedData.ContainsKey("Room Color"))
                    {
                        try
                        {
                            string targetColor = revisedData["Room Color"];
                            int updated = UpdateWallColor(doc, targetColor);
                            if (updated > 0)
                            {
                                successItems.Add($"Wall Color: {targetColor} ({updated} items)");
                                System.Diagnostics.Debug.WriteLine($"✅ Wall color: {targetColor}");
                            }
                            else
                            {
                                failedItems.Add("Wall Color (no modifiable elements)");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedItems.Add($"Wall Color: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"⚠️ Wall color failed: {ex.Message}");
                        }
                    }

                    // 5. Floor Texture
                    // 5. Floor Material
                    if (revisedData.ContainsKey("Floor Material"))
                    {
                        try
                        {
                            string targetTexture = revisedData["Floor Material"];
                            int updated = UpdateFloorTexture(doc, targetTexture);
                            if (updated > 0)
                            {
                                successItems.Add($"Floor Material: {targetTexture} ({updated} items)");
                                System.Diagnostics.Debug.WriteLine($"✅ Floor material: {targetTexture}");
                            }
                            else
                            {
                                failedItems.Add($"Floor Material: {targetTexture} (change failed)");
                            }
                        }
                        catch (Exception ex)
                        {
                            failedItems.Add($"Floor Material: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"⚠️ Floor material failed: {ex.Message}");
                        }
                    }

                    // Window positions are spread evenly inside UpdateWWR
                    trans.Commit();
                    System.Diagnostics.Debug.WriteLine("=== Transaction Commit succeeded ===");
                }

                // After image: handled by Rendering.py

                // Build the result message
                StringBuilder resultMessage = new StringBuilder();

                if (successItems.Count > 0)
                {
                    resultMessage.AppendLine("[OK] Successful items:");
                    foreach (string item in successItems)
                    {
                        resultMessage.AppendLine($"  • {item}");
                    }
                }

                if (failedItems.Count > 0)
                {
                    resultMessage.AppendLine();
                    resultMessage.AppendLine("[WARN] Failed items:");
                    foreach (string item in failedItems)
                    {
                        resultMessage.AppendLine($"  • {item}");
                    }
                }

                if (successItems.Count > 0)
                {
                    resultMessage.AppendLine();
                    resultMessage.AppendLine("After image saved.");
                    TaskDialog.Show("Applied", resultMessage.ToString());
                }
                else
                {
                    TaskDialog.Show("Apply Failed", "No modifiable elements.\n\n" + resultMessage.ToString());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ ApplyDesignFromRevised failed: {ex.Message}");
                TaskDialog.Show("Error", $"ApplyDesignFromRevised failed:\n\n{ex.Message}");
            }
        }


        private int UpdateWWR(Document doc, double targetWWR)
        {
            int updatedCount = 0;
            try
            {
                System.Diagnostics.Debug.WriteLine("=== UpdateWWR start: " + targetWWR + "% (improved: width+height+sill together) ===");

                var windows = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .ToElements();
                if (windows.Count == 0) return 0;

                // ── 1. Group windows by host wall ──
                // If one wall has several windows, split the area evenly
                var wallWinMap = new Dictionary<ElementId, List<FamilyInstance>>();
                foreach (Element we in windows)
                {
                    FamilyInstance win = we as FamilyInstance;
                    if (win == null) continue;
                    Wall hw = win.Host as Wall;
                    if (hw == null) continue;
                    if (!wallWinMap.ContainsKey(hw.Id))
                        wallWinMap[hw.Id] = new List<FamilyInstance>();
                    wallWinMap[hw.Id].Add(win);
                }

                // Track handled symbols (so instances sharing a symbol are not changed many times)
                var processedSymbols = new HashSet<ElementId>();

                foreach (var kv in wallWinMap)
                {
                    Wall hostWall = doc.GetElement(kv.Key) as Wall;
                    var winList = kv.Value;
                    if (hostWall == null || winList.Count == 0) continue;

                    try
                    {
                        // ── 2. Wall GROSS size (same basis as the analysis) ──
                        LocationCurve wallLoc = hostWall.Location as LocationCurve;
                        if (wallLoc == null) continue;
                        Parameter pWallH = hostWall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                        if (pWallH == null || pWallH.AsDouble() < 0.001) continue;

                        double wallLenM = wallLoc.Curve.Length * 0.3048;
                        double wallHM = pWallH.AsDouble() * 0.3048;
                        double wallGrossM2 = wallLenM * wallHM;

                        // ── 3. Target total window area for this wall ──
                        double wallTargetWinAreaM2 = (targetWWR / 100.0) * wallGrossM2;
                        // Split evenly across the N windows on the same wall
                        double winTargetAreaM2 = wallTargetWinAreaM2 / winList.Count;

                        System.Diagnostics.Debug.WriteLine("[WWR] Wall[" + hostWall.Id + "] " +
                            "GROSS=" + wallGrossM2.ToString("F2") + "m² windows=" + winList.Count + " " +
                            "→ target area per window=" + winTargetAreaM2.ToString("F2") + "m²");

                        foreach (var win in winList)
                        {
                            try
                            {
                                // ── 4. Get current window size (several attempts) ──
                                double curWmm = 0, curHmm = 0;

                                // (a) From the symbol name (e.g. "0915 x 1830mm")
                                string symName = win.Symbol.Name.Replace("mm", "").Replace("MM", "");
                                var nameParts = symName.Split('x');
                                if (nameParts.Length == 2)
                                {
                                    double.TryParse(nameParts[0].Trim(), out curWmm);
                                    double.TryParse(nameParts[1].Trim(), out curHmm);
                                }

                                // (b) Read the parameters directly
                                if (curWmm < 10)
                                {
                                    foreach (var wn in new[] { "\uD3ED", "Width" })
                                    {
                                        Parameter p = win.Symbol.LookupParameter(wn) ?? win.LookupParameter(wn);
                                        if (p != null && p.AsDouble() > 0.01) { curWmm = p.AsDouble() * 304.8; break; }
                                    }
                                }
                                if (curHmm < 10)
                                {
                                    foreach (var hn in new[] { "\uB192\uC774", "Height", "Rough Height" })
                                    {
                                        Parameter p = win.Symbol.LookupParameter(hn) ?? win.LookupParameter(hn);
                                        if (p != null && p.AsDouble() > 0.01) { curHmm = p.AsDouble() * 304.8; break; }
                                    }
                                }

                                // (c) From the BoundingBox (last resort)
                                if (curWmm < 10 || curHmm < 10)
                                {
                                    var bb = win.get_BoundingBox(null);
                                    if (bb != null)
                                    {
                                        double bbW = Math.Abs(bb.Max.X - bb.Min.X) * 304.8;
                                        double bbH = Math.Abs(bb.Max.Z - bb.Min.Z) * 304.8;
                                        if (curWmm < 10) curWmm = bbW;
                                        if (curHmm < 10) curHmm = bbH;
                                    }
                                }

                                // (d) Default
                                if (curWmm < 10) curWmm = 915;
                                if (curHmm < 10) curHmm = 1830;

                                // ── 5. Current sill position ──
                                double curSillM = 0;
                                Parameter pSill = win.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                                if (pSill != null && pSill.HasValue) curSillM = Math.Max(0, pSill.AsDouble() * 0.3048);

                                System.Diagnostics.Debug.WriteLine("  [Window " + win.Id + "] current: " +
                                    curWmm.ToString("F0") + "×" + curHmm.ToString("F0") + "mm, sill=" +
                                    (curSillM * 1000).ToString("F0") + "mm");

                                // ── 6. Max possible size (2 steps: keep curSill first, lower the sill if needed) ──
                                // Width: 90% of wall length (divided by N if the wall has N windows)
                                double maxWidthM = Math.Max(0.3, (wallLenM * 0.9) / winList.Count);
                                double topMarginM = 0.20;

                                // Attempt A: check if the current sill can be kept
                                double maxHeightA = Math.Max(0.3, wallHM - curSillM - topMarginM);
                                double maxAreaA = maxWidthM * maxHeightA;

                                double maxHeightM;
                                bool sillCanBePreserved;
                                if (winTargetAreaM2 <= maxAreaA + 0.01)
                                {
                                    // Target area is reachable with the current sill (small to medium WWR)
                                    maxHeightM = maxHeightA;
                                    sillCanBePreserved = true;
                                }
                                else
                                {
                                    // Current sill is not enough -> allow sill down to 0 (large WWR / floor-to-ceiling)
                                    maxHeightM = Math.Max(0.3, wallHM - topMarginM);
                                    sillCanBePreserved = false;
                                }

                                // ── 7. Choose target size: keep aspect ratio -> apply caps ──
                                // Attempt 1: keep the current window aspect ratio (W/H ratio)
                                double curAspect = (curHmm > 10) ? (curWmm / curHmm) : 0.5;
                                double targetHM = Math.Sqrt(winTargetAreaM2 / curAspect);
                                double targetWM = curAspect * targetHM;

                                // Attempt 2: if width exceeds the cap, fix width and recompute height
                                if (targetWM > maxWidthM)
                                {
                                    targetWM = maxWidthM;
                                    targetHM = winTargetAreaM2 / targetWM;
                                }
                                // Attempt 3: if height exceeds the cap, fix height and recompute width
                                if (targetHM > maxHeightM)
                                {
                                    targetHM = maxHeightM;
                                    targetWM = winTargetAreaM2 / targetHM;
                                    if (targetWM > maxWidthM) targetWM = maxWidthM;
                                }

                                // Enforce minimum values
                                targetWM = Math.Max(0.1, targetWM);
                                targetHM = Math.Max(0.1, targetHM);

                                double targetWmm = Math.Round(targetWM * 1000.0);
                                double targetHmm = Math.Round(targetHM * 1000.0);
                                double achievedAreaM2 = targetWM * targetHM;
                                double achievedWWR = Math.Round((achievedAreaM2 * winList.Count) / wallGrossM2 * 100.0, 1);

                                System.Diagnostics.Debug.WriteLine("    target: " +
                                    targetWmm.ToString("F0") + "×" + targetHmm.ToString("F0") + "mm " +
                                    "(achieved WWR≈" + achievedWWR + "%)");

                                if (achievedWWR < targetWWR - 1.0)
                                {
                                    System.Diagnostics.Debug.WriteLine("    ⚠️ Target WWR not reached due to wall limits " +
                                        "(requested " + targetWWR + "% → achieved " + achievedWWR + "%)");
                                }

                                // ── 8. Set symbol (type) width/height parameters ──
                                bool symbolAlreadyProcessed = processedSymbols.Contains(win.Symbol.Id);
                                if (!symbolAlreadyProcessed)
                                {
                                    double targetWft = targetWmm / 304.8;
                                    double targetHft = targetHmm / 304.8;

                                    bool widthSet = false, heightSet = false;

                                    foreach (var wn in new[] { "\uD3ED", "Width" })
                                    {
                                        Parameter p = win.Symbol.LookupParameter(wn);
                                        if (p != null && !p.IsReadOnly)
                                        {
                                            try
                                            {
                                                p.Set(targetWft);
                                                widthSet = true;
                                                System.Diagnostics.Debug.WriteLine("    ✅ Width(" + wn + ") set: " + targetWmm + "mm");
                                                break;
                                            }
                                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("    Width(" + wn + ") failed: " + ex.Message); }
                                        }
                                    }

                                    foreach (var hn in new[] { "\uB192\uC774", "Height", "Rough Height" })
                                    {
                                        Parameter p = win.Symbol.LookupParameter(hn);
                                        if (p != null && !p.IsReadOnly)
                                        {
                                            try
                                            {
                                                p.Set(targetHft);
                                                heightSet = true;
                                                System.Diagnostics.Debug.WriteLine("    ✅ Height(" + hn + ") set: " + targetHmm + "mm");
                                                break;
                                            }
                                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("    Height(" + hn + ") failed: " + ex.Message); }
                                        }
                                    }

                                    if (!widthSet && !heightSet)
                                    {
                                        System.Diagnostics.Debug.WriteLine("    ❌ Failed to set both width and height parameters");
                                        continue;
                                    }

                                    // Sync the type name (reflect the real size)
                                    string newTypeName = ((int)targetWmm).ToString("D4") + " x " + ((int)targetHmm).ToString("D4") + "mm";
                                    if (win.Symbol.Name != newTypeName)
                                    {
                                        try
                                        {
                                            FamilySymbol existing = null;
                                            var fsCol = new FilteredElementCollector(doc)
                                                .OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Windows);
                                            foreach (FamilySymbol fs in fsCol)
                                            {
                                                if (fs.FamilyName == win.Symbol.FamilyName && fs.Name == newTypeName)
                                                { existing = fs; break; }
                                            }
                                            if (existing != null) win.Symbol = existing;
                                            else win.Symbol.Name = newTypeName;
                                        }
                                        catch (Exception exN) { System.Diagnostics.Debug.WriteLine("    Rename failed: " + exN.Message); }
                                    }

                                    processedSymbols.Add(win.Symbol.Id);
                                }

                                // ── 9. Auto-adjust sill (place the larger window properly) ──
                                try
                                {
                                    if (pSill != null && !pSill.IsReadOnly)
                                    {
                                        // Ideal sill = where the window top sits topMargin below the ceiling
                                        double idealSillM = Math.Max(0, wallHM - topMarginM - targetHM);

                                        // Two cases:
                                        // (a) Small window: idealSill ≥ curSill -> keep curSill (respect the user's intent)
                                        // (b) Large window: idealSill < curSill -> lower to idealSill (window must not cut the ceiling)
                                        double newSillM = Math.Min(curSillM, idealSillM);
                                        newSillM = Math.Max(0, newSillM);

                                        // Apply only when the change matters (1 cm or more)
                                        if (Math.Abs(newSillM - curSillM) > 0.01)
                                        {
                                            pSill.Set(newSillM / 0.3048);
                                            System.Diagnostics.Debug.WriteLine("    Sill adjusted: " +
                                                (curSillM * 1000).ToString("F0") + " → " +
                                                (newSillM * 1000).ToString("F0") + "mm" +
                                                (sillCanBePreserved ? " (kept)" : " (lowered for a large window)"));
                                        }
                                        else
                                        {
                                            System.Diagnostics.Debug.WriteLine("    Sill kept: " +
                                                (curSillM * 1000).ToString("F0") + "mm");
                                        }
                                    }
                                }
                                catch (Exception exS) { System.Diagnostics.Debug.WriteLine("    Sill adjust failed: " + exS.Message); }

                                // ── 10. Move to the wall's horizontal center (spread evenly if several windows) ──
                                try
                                {
                                    LocationPoint lp = win.Location as LocationPoint;
                                    if (lp != null)
                                    {
                                        XYZ wallStart = wallLoc.Curve.GetEndPoint(0);
                                        XYZ wallEnd = wallLoc.Curve.GetEndPoint(1);
                                        XYZ wallDir = (wallEnd - wallStart).Normalize();
                                        double projStart = wallStart.DotProduct(wallDir);
                                        double projEnd = wallEnd.DotProduct(wallDir);

                                        // Spread N windows evenly: window i goes at (i+1)/(N+1)
                                        int idx = winList.IndexOf(win);
                                        double t = (double)(idx + 1) / (winList.Count + 1);
                                        double projTarget = projStart + (projEnd - projStart) * t;
                                        double projCur = lp.Point.DotProduct(wallDir);
                                        double moveDist = projTarget - projCur;

                                        if (Math.Abs(moveDist) > 0.001)
                                        {
                                            ElementTransformUtils.MoveElement(doc, win.Id, wallDir.Multiply(moveDist));
                                            System.Diagnostics.Debug.WriteLine("    Moved: " +
                                                (moveDist * 304.8).ToString("F0") + "mm (index " + idx + "/" + winList.Count + ")");
                                        }
                                    }
                                }
                                catch (Exception exM) { System.Diagnostics.Debug.WriteLine("    Move failed: " + exM.Message); }

                                updatedCount++;
                            }
                            catch (Exception exW) { System.Diagnostics.Debug.WriteLine("  Window ERR: " + exW.Message); }
                        }
                    }
                    catch (Exception exWall) { System.Diagnostics.Debug.WriteLine("Wall ERR: " + exWall.Message); }
                }

                System.Diagnostics.Debug.WriteLine("=== UpdateWWR done: " + updatedCount + " ===");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("UpdateWWR error: " + ex.Message); }
            return updatedCount;
        }

        // ── Type name sync helper ──
        private void CenterWindowsOnWalls(Document doc)
        {
            try
            {
                var windows = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType().ToElements();
                foreach (Element we in windows)
                {
                    FamilyInstance win = we as FamilyInstance;
                    if (win == null) continue;
                    Wall hostWall = win.Host as Wall;
                    if (hostWall == null) continue;
                    LocationPoint lp = win.Location as LocationPoint;
                    LocationCurve wallCurve = hostWall.Location as LocationCurve;
                    if (lp == null || wallCurve == null) continue;
                    XYZ wallStart = wallCurve.Curve.GetEndPoint(0);
                    XYZ wallEnd = wallCurve.Curve.GetEndPoint(1);
                    XYZ wallDir = (wallEnd - wallStart).Normalize();
                    double projS = wallStart.DotProduct(wallDir);
                    double projE = wallEnd.DotProduct(wallDir);
                    double projM = (projS + projE) / 2.0;
                    double projC = lp.Point.DotProduct(wallDir);
                    double moveDist = projM - projC;
                    if (Math.Abs(moveDist) > 0.001)
                        ElementTransformUtils.MoveElement(doc, win.Id, wallDir.Multiply(moveDist));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("CenterWindows error: " + ex.Message); }
        }

        private void RenameElementType(Document doc, ElementType elemType, string newName)
        {
            try
            {
                if (elemType == null || elemType.Name == newName) return;
                // Look for an existing type in the same family
                Element existing = null;
                foreach (Element e in new FilteredElementCollector(doc).OfClass(elemType.GetType()))
                {
                    ElementType et = e as ElementType;
                    if (et != null && et.Id != elemType.Id && et.FamilyName == elemType.FamilyName && et.Name == newName)
                    { existing = e; break; }
                }
                if (existing == null) elemType.Name = newName;
                // If the name already exists -> keep the current name (ignored)
                System.Diagnostics.Debug.WriteLine("  [NameSync] " + elemType.FamilyName + " → " + newName);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("  [NameSync failed] " + ex.Message); }
        }


        private int UpdateCeilingHeight(Document doc, double targetHeightMeters)
        {
            int updatedCount = 0;

            try
            {
                System.Diagnostics.Debug.WriteLine("=== Ceiling height update start ===");
                System.Diagnostics.Debug.WriteLine($"Target height: {targetHeightMeters}m");

                var ceilings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ceilings)
                    .WhereElementIsNotElementType()
                    .Cast<Ceiling>()
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"Ceilings found: {ceilings.Count}");

                foreach (Ceiling ceiling in ceilings)
                {
                    // Skip elements from linked files
                    if (ceiling.Document.IsLinked)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Ceiling {ceiling.Id}: linked file (skipped)");
                        continue;
                    }

                    // Skip elements inside groups
                    if (ceiling.GroupId != ElementId.InvalidElementId)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Ceiling {ceiling.Id}: inside a group (skipped)");
                        continue;
                    }

                    Parameter offsetParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);

                    if (offsetParam == null)
                    {
                        continue;
                    }

                    // Skip quietly if read-only
                    if (offsetParam.IsReadOnly)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Ceiling {ceiling.Id}: read-only (skipped)");
                        continue;
                    }

                    try
                    {
                        double newHeightFeet = targetHeightMeters / 0.3048;
                        offsetParam.Set(newHeightFeet);
                        // Name sync: "Ceiling H2700mm" format
                        string ceilNewName = "Ceiling H" + ((int)Math.Round(targetHeightMeters * 1000)).ToString() + "mm";
                        RenameElementType(doc, doc.GetElement(ceiling.GetTypeId()) as ElementType, ceilNewName);
                        updatedCount++;
                        System.Diagnostics.Debug.WriteLine($"  ✅ Ceiling {ceiling.Id}: {targetHeightMeters:F2}m");
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                    {
                        // Skip quietly if read-only or not editable
                        System.Diagnostics.Debug.WriteLine($"  Ceiling {ceiling.Id}: not editable (skipped)");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ⚠️ Ceiling {ceiling.Id} error: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"=== Ceiling update done: {updatedCount} ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ UpdateCeilingHeight error: {ex.Message}");
            }

            return updatedCount;
        }

        private int UpdateLightingCCT(Document doc, double targetCCT)
        {
            int updatedCount = 0;

            try
            {
                System.Diagnostics.Debug.WriteLine($"=== CCT update start: {targetCCT}K ===");

                // Get the current active view
                View activeView = doc.ActiveView;
                if (activeView == null)
                {
                    System.Diagnostics.Debug.WriteLine("❌ No active view");
                    return 0;
                }

                // Collect all lights in the view
                FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id);
                var lights = collector.OfCategory(BuiltInCategory.OST_LightingFixtures)
                                      .WhereElementIsNotElementType()
                                      .ToList();

                System.Diagnostics.Debug.WriteLine($"Lights found: {lights.Count}");

                foreach (Element light in lights)
                {
                    // Skip elements from linked files
                    if (light.Document.IsLinked)
                    {
                        continue;
                    }

                    // Skip elements inside groups
                    if (light.GroupId != ElementId.InvalidElementId)
                    {
                        continue;
                    }

                    bool updated = false;

                    // Handle the type parameter first
                    ElementType lightType = doc.GetElement(light.GetTypeId()) as ElementType;
                    if (lightType != null)
                    {
                        try
                        {
                            // Try to set the CCT parameter
                            Parameter cctParam = lightType.get_Parameter(BuiltInParameter.FBX_LIGHT_INITIAL_COLOR_TEMPERATURE);
                            if (cctParam != null && !cctParam.IsReadOnly)
                            {
                                double oldValue = cctParam.AsDouble();
                                cctParam.Set(targetCCT);
                                System.Diagnostics.Debug.WriteLine($"  ✅ {light.Name} (type): {oldValue:F0}K → {targetCCT}K");
                                updatedCount++;
                                RenameElementType(doc, lightType, "Light CCT" + ((int)Math.Round(targetCCT)).ToString() + "K");
                                updated = true;
                            }

                            // Also try to find the parameter by name
                            if (!updated)
                            {
                                // Look it up by a name such as the Korean-locale name or "Initial Color Temperature"
                                Parameter cctByName = lightType.LookupParameter("\uCD08\uAE30\u0020\uC0C9\uC628\uB3C4");
                                if (cctByName == null)
                                    cctByName = lightType.LookupParameter("Initial Color Temperature");
                                if (cctByName == null)
                                    cctByName = lightType.LookupParameter("CCT");
                                if (cctByName == null)
                                    cctByName = lightType.LookupParameter("Color Temperature");

                                if (cctByName != null && !cctByName.IsReadOnly)
                                {
                                    try
                                    {
                                        if (cctByName.StorageType == StorageType.Double)
                                        {
                                            double oldValue = cctByName.AsDouble();
                                            cctByName.Set(targetCCT);
                                            System.Diagnostics.Debug.WriteLine($"  ✅ {light.Name} (type-name): {oldValue:F0}K → {targetCCT}K");
                                            updatedCount++;
                                            RenameElementType(doc, lightType, "Light CCT" + ((int)Math.Round(targetCCT)).ToString() + "K");
                                            updated = true;
                                        }
                                        else if (cctByName.StorageType == StorageType.Integer)
                                        {
                                            int oldValue = cctByName.AsInteger();
                                            cctByName.Set((int)targetCCT);
                                            System.Diagnostics.Debug.WriteLine($"  ✅ {light.Name} (type-name): {oldValue}K → {(int)targetCCT}K");
                                            updatedCount++;
                                            RenameElementType(doc, lightType, "Light CCT" + ((int)Math.Round(targetCCT)).ToString() + "K");
                                            updated = true;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"  ⚠️ {light.Name} (name-type): {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"  ⚠️ Light {light.Id} type error: {ex.Message}");
                        }
                    }

                    // Also try the instance parameter (when the type parameter does not work)
                    if (!updated)
                    {
                        try
                        {
                            // Instance-level CCT parameter
                            Parameter instCctParam = light.get_Parameter(BuiltInParameter.FBX_LIGHT_INITIAL_COLOR_TEMPERATURE);
                            if (instCctParam != null && !instCctParam.IsReadOnly)
                            {
                                double oldValue = instCctParam.AsDouble();
                                instCctParam.Set(targetCCT);
                                System.Diagnostics.Debug.WriteLine($"  ✅ {light.Name} (instance): {oldValue:F0}K → {targetCCT}K");
                                updatedCount++;
                                RenameElementType(doc, doc.GetElement(light.GetTypeId()) as ElementType, "Light CCT" + ((int)Math.Round(targetCCT)).ToString() + "K");
                            }
                            else
                            {
                                // Find the instance parameter by name
                                Parameter instByName = light.LookupParameter("\uCD08\uAE30\u0020\uC0C9\uC628\uB3C4");
                                if (instByName == null)
                                    instByName = light.LookupParameter("Initial Color Temperature");
                                if (instByName == null)
                                    instByName = light.LookupParameter("CCT");
                                if (instByName == null)
                                    instByName = light.LookupParameter("Color Temperature");

                                if (instByName != null && !instByName.IsReadOnly)
                                {
                                    if (instByName.StorageType == StorageType.Double)
                                    {
                                        double oldValue = instByName.AsDouble();
                                        instByName.Set(targetCCT);
                                        System.Diagnostics.Debug.WriteLine($"  ✅ {light.Name} (instance-name): {oldValue:F0}K → {targetCCT}K");
                                        updatedCount++;
                                        RenameElementType(doc, doc.GetElement(light.GetTypeId()) as ElementType, "Light CCT" + ((int)Math.Round(targetCCT)).ToString() + "K");
                                    }
                                    else if (instByName.StorageType == StorageType.Integer)
                                    {
                                        int oldValue = instByName.AsInteger();
                                        instByName.Set((int)targetCCT);
                                        System.Diagnostics.Debug.WriteLine($"  ✅ {light.Name} (instance-name): {oldValue}K → {(int)targetCCT}K");
                                        updatedCount++;
                                        RenameElementType(doc, doc.GetElement(light.GetTypeId()) as ElementType, "Light CCT" + ((int)Math.Round(targetCCT)).ToString() + "K");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"  ⚠️ Light {light.Id} instance error: {ex.Message}");
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"=== CCT update done: {updatedCount} ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ UpdateLightingCCT error: {ex.Message}");
            }

            return updatedCount;
        }

        private int UpdateWallColor(Document doc, string colorName)
        {
            int updatedCount = 0;

            try
            {
                System.Diagnostics.Debug.WriteLine($"=== Wall color update start: {colorName} ===");

                Autodesk.Revit.DB.Color color = ConvertColorNameToRGB(colorName);
                System.Diagnostics.Debug.WriteLine($"RGB: ({color.Red}, {color.Green}, {color.Blue})");

                var walls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"Walls found: {walls.Count}");

                // Track handled types (avoid duplicates)
                HashSet<ElementId> processedTypes = new HashSet<ElementId>();

                foreach (Wall wall in walls)
                {
                    try
                    {
                        // Skip elements from linked files
                        if (wall.Document.IsLinked)
                        {
                            continue;
                        }

                        // Skip elements inside groups
                        if (wall.GroupId != ElementId.InvalidElementId)
                        {
                            continue;
                        }

                        WallType wallType = wall.WallType;

                        // Skip types already handled
                        if (processedTypes.Contains(wallType.Id))
                        {
                            continue;
                        }

                        // Get the CompoundStructure
                        CompoundStructure compStruct = wallType.GetCompoundStructure();
                        if (compStruct == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Wall type {wallType.Name}: no compound structure (skipped)");
                            processedTypes.Add(wallType.Id);
                            continue;
                        }

                        // Get the layers
                        IList<CompoundStructureLayer> layers = compStruct.GetLayers();
                        if (layers == null || layers.Count == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Wall type {wallType.Name}: no layers (skipped)");
                            processedTypes.Add(wallType.Id);
                            continue;
                        }

                        // The very first layer (outermost, Finish layer)
                        CompoundStructureLayer topLayer = layers[0];

                        System.Diagnostics.Debug.WriteLine($"  Wall type {wallType.Name}: trying to edit the first layer");

                        // Create a new material (unique name with a timestamp)
                        string materialName = $"Wall_{colorName}_{DateTime.Now.Ticks}";
                        Material newMaterial = CreateOrGetMaterial(doc, materialName, color);

                        if (newMaterial == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Wall type {wallType.Name}: material creation failed (skipped)");
                            processedTypes.Add(wallType.Id);
                            continue;
                        }

                        try
                        {
                            // Keep the existing layer properties and swap only the material
                            double thickness = topLayer.Width;
                            MaterialFunctionAssignment function = topLayer.Function;

                            // Create a new layer (only the material changes)
                            CompoundStructureLayer newTopLayer = new CompoundStructureLayer(
                                thickness,
                                function,
                                newMaterial.Id
                            );

                            // Copy the layer list
                            List<CompoundStructureLayer> newLayers = new List<CompoundStructureLayer>();

                            // Replace the first layer with the new material
                            newLayers.Add(newTopLayer);

                            // Keep the other layers as they are
                            for (int i = 1; i < layers.Count; i++)
                            {
                                newLayers.Add(layers[i]);
                            }

                            // Set the new layers on the CompoundStructure
                            compStruct.SetLayers(newLayers);

                            // Apply the CompoundStructure to the WallType
                            wallType.SetCompoundStructure(compStruct);

                            processedTypes.Add(wallType.Id);
                            // Name sync: "Wall white" format
                            RenameElementType(doc, wallType, "Wall " + colorName.Trim().ToLower());
                            updatedCount++;

                            System.Diagnostics.Debug.WriteLine($"  ✅ Wall type {wallType.Name}: first layer material changed");
                            System.Diagnostics.Debug.WriteLine($"      Material: {newMaterial.Name}");
                            System.Diagnostics.Debug.WriteLine($"      Thickness: {thickness * 304.8:F1}mm");
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Wall type {wallType.Name}: read-only or system type (skipped)");
                            System.Diagnostics.Debug.WriteLine($"      error: {ex.Message}");
                            processedTypes.Add(wallType.Id);
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Wall type {wallType.Name}: not editable (skipped)");
                            System.Diagnostics.Debug.WriteLine($"      error: {ex.Message}");
                            processedTypes.Add(wallType.Id);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"  ⚠️ Wall type {wallType.Name}: {ex.GetType().Name}");
                            System.Diagnostics.Debug.WriteLine($"      error: {ex.Message}");
                            processedTypes.Add(wallType.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ⚠️ Wall error: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"=== Wall color update done: {updatedCount} types ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ UpdateWallColor error: {ex.Message}");
            }

            return updatedCount;
        }

        private int UpdateFloorTexture(Document doc, string textureName)
        {
            int updatedCount = 0;
            string logFile = @"C:\Temp\floor_debug.txt";

            try
            {
                // Initialize the log file
                File.WriteAllText(logFile, $"=== Floor material update start: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
                File.AppendAllText(logFile, $"Input material: '{textureName}'\n");

                System.Diagnostics.Debug.WriteLine($"=== Floor material update start: {textureName} ===");

                // Normalize the material name
                string normalizedTexture = textureName.Trim().ToLower();
                // Normalize similar terms
                if (normalizedTexture == "wooden" || normalizedTexture == "hardwood" || normalizedTexture == "parquet") normalizedTexture = "wood";
                if (normalizedTexture == "ceramic" || normalizedTexture == "porcelain" || normalizedTexture == "tiles") normalizedTexture = "tile";
                if (normalizedTexture == "marbles") normalizedTexture = "marble";
                if (normalizedTexture == "grey" || normalizedTexture == "cement") normalizedTexture = "concrete";
                File.AppendAllText(logFile, $"Normalized material: '{normalizedTexture}'\n");
                System.Diagnostics.Debug.WriteLine($"Normalized material: '{normalizedTexture}'");

                // Material name -> image file name map
                Dictionary<string, string> textureFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "wood", "wood" },
                    { "wooden", "wood" },
                    { "concrete", "concrete" },
                    { "tile", "tile" },
                    { "marble", "marble" },
                    { "carpet", "carpet" },
                    { "stone", "stone" }
                };

                string materialsFolder = System.IO.Path.Combine(MyWindow.ProjectRoot, "materials");
                File.AppendAllText(logFile, $"Material folder: {materialsFolder}\n");

                if (!textureFileMap.TryGetValue(normalizedTexture, out string baseFileName))
                {
                    File.AppendAllText(logFile, $"❌ Unknown material: '{textureName}'\n");
                    File.AppendAllText(logFile, $"Available materials: {string.Join(", ", textureFileMap.Keys)}\n");
                    System.Diagnostics.Debug.WriteLine($"⚠️ Unknown material: '{textureName}'");
                    return 0;
                }

                File.AppendAllText(logFile, $"Base file name: '{baseFileName}'\n");

                // Find the image file (jpg, png)
                string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp" };
                string imagePath = null;

                foreach (string ext in extensions)
                {
                    string testPath = Path.Combine(materialsFolder, baseFileName + ext);
                    File.AppendAllText(logFile, $"Searching: {testPath}\n");

                    if (File.Exists(testPath))
                    {
                        imagePath = testPath;
                        File.AppendAllText(logFile, $"✅ Image found: {imagePath}\n");
                        System.Diagnostics.Debug.WriteLine($"✅ Image found: {imagePath}");
                        break;
                    }
                    else
                    {
                        File.AppendAllText(logFile, $"  No file\n");
                    }
                }

                if (imagePath == null)
                {
                    File.AppendAllText(logFile, $"❌ Image file not found (extensions: .jpg, .png, .bmp)\n");
                    System.Diagnostics.Debug.WriteLine($"❌ No image file: {baseFileName}");
                    return 0;
                }

                // Collect floors
                var floors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .Cast<Floor>()
                    .ToList();

                File.AppendAllText(logFile, $"\nFloors found: {floors.Count}\n");
                System.Diagnostics.Debug.WriteLine($"Floors found: {floors.Count}");

                if (floors.Count == 0)
                {
                    File.AppendAllText(logFile, $"❌ No floor elements\n");
                    return 0;
                }

                // Interior floor ID (priority)
                int targetFloorId = 13443083;
                Floor targetFloor = floors.FirstOrDefault(f => f.Id.IntegerValue == targetFloorId);

                if (targetFloor != null)
                {
                    File.AppendAllText(logFile, $"✅ Interior floor found (ID: {targetFloorId}) - only this floor is changed\n");
                    System.Diagnostics.Debug.WriteLine($"Interior floor found (ID: {targetFloorId})");

                    // Handle only the interior floor
                    floors = new List<Floor> { targetFloor };
                }
                else
                {
                    File.AppendAllText(logFile, $"⚠️ Interior floor ID {targetFloorId} not found - changing all floors\n");
                }

                HashSet<ElementId> processedTypes = new HashSet<ElementId>();

                foreach (Floor floor in floors)
                {
                    try
                    {
                        if (floor.Document.IsLinked || floor.GroupId != ElementId.InvalidElementId)
                        {
                            File.AppendAllText(logFile, $"  Floor {floor.Id}: linked or inside a group (skipped)\n");
                            continue;
                        }

                        FloorType floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                        if (floorType == null || processedTypes.Contains(floorType.Id))
                            continue;

                        File.AppendAllText(logFile, $"\n[Floor type] {floorType.Name}\n");
                        System.Diagnostics.Debug.WriteLine($"\nFloor type: {floorType.Name}");

                        CompoundStructure compStruct = floorType.GetCompoundStructure();
                        if (compStruct == null || compStruct.GetLayers().Count == 0)
                        {
                            File.AppendAllText(logFile, $"  ❌ No compound structure\n");
                            System.Diagnostics.Debug.WriteLine($"  No compound structure");
                            processedTypes.Add(floorType.Id);
                            continue;
                        }

                        File.AppendAllText(logFile, $"  Layer count: {compStruct.GetLayers().Count}\n");

                        // Material of the first layer
                        CompoundStructureLayer topLayer = compStruct.GetLayers()[0];
                        ElementId materialId = topLayer.MaterialId;

                        if (materialId == ElementId.InvalidElementId)
                        {
                            File.AppendAllText(logFile, $"  ❌ No material on the first layer\n");
                            System.Diagnostics.Debug.WriteLine($"  No material");
                            processedTypes.Add(floorType.Id);
                            continue;
                        }

                        Material material = doc.GetElement(materialId) as Material;
                        if (material == null)
                        {
                            File.AppendAllText(logFile, $"  ❌ Material not found (ID: {materialId})\n");
                            System.Diagnostics.Debug.WriteLine($"  Material not found");
                            processedTypes.Add(floorType.Id);
                            continue;
                        }

                        File.AppendAllText(logFile, $"  Current material name: {material.Name}\n");
                        System.Diagnostics.Debug.WriteLine($"  Material: {material.Name}");

                        // Rename the material to Floor_(material name)
                        string newMaterialName = $"Floor_{normalizedTexture}";
                        if (material.Name != newMaterialName)
                        {
                            try
                            {
                                material.Name = newMaterialName;
                                File.AppendAllText(logFile, $"  ✅ Material renamed: {material.Name} → {newMaterialName}\n");
                                System.Diagnostics.Debug.WriteLine($"  Material renamed: {newMaterialName}");
                            }
                            catch (Exception ex)
                            {
                                File.AppendAllText(logFile, $"  ⚠️ Material rename failed: {ex.Message}\n");
                            }
                        }
                        else
                        {
                            File.AppendAllText(logFile, $"  Material name is already '{newMaterialName}'\n");
                        }

                        // Get the AppearanceAsset
                        ElementId assetId = material.AppearanceAssetId;
                        File.AppendAllText(logFile, $"  Current Asset ID: {assetId}\n");

                        // Check the Asset and replace it if needed
                        bool needNewAsset = false;

                        if (assetId != ElementId.InvalidElementId)
                        {
                            AppearanceAssetElement currentAsset = doc.GetElement(assetId) as AppearanceAssetElement;
                            if (currentAsset != null)
                            {
                                File.AppendAllText(logFile, $"  Current Asset name: {currentAsset.Name}\n");

                                // Keep a Generic Asset as it is (do not replace)
                                if (currentAsset.Name.Contains("Generic") || currentAsset.Name.Contains("\uC77C\uBC18"))
                                {
                                    File.AppendAllText(logFile, $"  ℹ️ Generic Asset - checking image property\n");

                                    // Check the image property of the Generic Asset
                                    try
                                    {
                                        Asset asset = currentAsset.GetRenderingAsset();
                                        if (asset != null)
                                        {
                                            AssetProperty bitmapProp = asset.FindByName("unifiedbitmap_Bitmap");
                                            if (bitmapProp != null)
                                            {
                                                File.AppendAllText(logFile, $"  ✅ unifiedbitmap_Bitmap property present (type: {bitmapProp.GetType().Name})\n");
                                                needNewAsset = false; // Use the current Asset
                                            }
                                            else
                                            {
                                                File.AppendAllText(logFile, $"  ⚠️ No unifiedbitmap_Bitmap property - Asset must be replaced\n");
                                                needNewAsset = true;
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        File.AppendAllText(logFile, $"  ⚠️ Asset property check error: {ex.Message}\n");
                                        needNewAsset = true;
                                    }
                                }
                                else
                                {
                                    File.AppendAllText(logFile, $"  ℹ️ Not a Generic Asset - using it as is\n");
                                    needNewAsset = false;
                                }
                            }
                        }
                        else
                        {
                            File.AppendAllText(logFile, $"  No Asset\n");
                            needNewAsset = true;
                        }

                        if (needNewAsset)
                        {
                            File.AppendAllText(logFile, $"  Trying to create an image-capable Asset\n");

                            // Collect all Assets
                            var allAssets = new FilteredElementCollector(doc)
                                .OfClass(typeof(AppearanceAssetElement))
                                .Cast<AppearanceAssetElement>()
                                .ToList();

                            File.AppendAllText(logFile, $"  Searching for a usable Asset... (total {allAssets.Count})\n");

                            AppearanceAssetElement imageAsset = null;

                            // Step 1: find a non-Generic Ceramic, Stone or Tile asset
                            foreach (var testAsset in allAssets)
                            {
                                if (testAsset.Name.Contains("Generic") || testAsset.Name.Contains("\uC77C\uBC18"))
                                    continue;

                                if (testAsset.Name.Contains("Ceramic") ||
                                    testAsset.Name.Contains("Stone") ||
                                    testAsset.Name.Contains("Tile") ||
                                    testAsset.Name.Contains("\uC138\uB77C\uBBF9") ||
                                    testAsset.Name.Contains("\uD0C0\uC77C") ||
                                    testAsset.Name.Contains("Porcelain") ||
                                    testAsset.Name.Contains("Marble") ||
                                    testAsset.Name.Contains("\uB300\uB9AC\uC11D"))
                                {
                                    try
                                    {
                                        Asset asset = testAsset.GetRenderingAsset();
                                        if (asset != null)
                                        {
                                            var bitmapProp = asset.FindByName("unifiedbitmap_Bitmap");
                                            if (bitmapProp is AssetPropertyString)
                                            {
                                                imageAsset = testAsset;
                                                File.AppendAllText(logFile, $"  ✅ Image-capable Asset found (step 1): {testAsset.Name}\n");
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }

                            // Step 2: find a non-Generic Asset with a unifiedbitmap_Bitmap property
                            if (imageAsset == null)
                            {
                                File.AppendAllText(logFile, $"  Step 1 failed - step 2: searching for unifiedbitmap_Bitmap property\n");

                                foreach (var testAsset in allAssets)
                                {
                                    if (testAsset.Name.Contains("Generic") || testAsset.Name.Contains("\uC77C\uBC18"))
                                        continue;

                                    try
                                    {
                                        Asset asset = testAsset.GetRenderingAsset();
                                        if (asset != null)
                                        {
                                            var bitmapProp = asset.FindByName("unifiedbitmap_Bitmap");
                                            if (bitmapProp is AssetPropertyString)
                                            {
                                                imageAsset = testAsset;
                                                File.AppendAllText(logFile, $"  ✅ Image-capable Asset found (step 2): {testAsset.Name}\n");
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }

                            // Step 3: search all Assets (including Generic)
                            if (imageAsset == null)
                            {
                                File.AppendAllText(logFile, $"  ⚠️ Step 2 failed - step 3: searching all Assets (including Generic)\n");

                                int assetIndex = 0;
                                foreach (var testAsset in allAssets)
                                {
                                    assetIndex++;
                                    try
                                    {
                                        Asset asset = testAsset.GetRenderingAsset();
                                        if (asset != null)
                                        {
                                            var bitmapProp = asset.FindByName("unifiedbitmap_Bitmap");

                                            // Detailed log (first 10 only)
                                            if (assetIndex <= 10)
                                            {
                                                File.AppendAllText(logFile, $"    [{assetIndex}] {testAsset.Name}: ");
                                                if (bitmapProp != null)
                                                {
                                                    File.AppendAllText(logFile, $"unifiedbitmap_Bitmap present (type: {bitmapProp.GetType().Name})\n");
                                                }
                                                else
                                                {
                                                    File.AppendAllText(logFile, $"No unifiedbitmap_Bitmap\n");
                                                }
                                            }

                                            if (bitmapProp is AssetPropertyString)
                                            {
                                                imageAsset = testAsset;
                                                File.AppendAllText(logFile, $"  ✅ Asset found (step 3, #{assetIndex}): {testAsset.Name}\n");
                                                break;
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        if (assetIndex <= 10)
                                        {
                                            File.AppendAllText(logFile, $"    [{assetIndex}] {testAsset.Name}: error - {ex.Message}\n");
                                        }
                                    }
                                }

                                if (imageAsset == null)
                                {
                                    File.AppendAllText(logFile, $"  ❌ Searched {assetIndex} Assets but found no unifiedbitmap_Bitmap\n");
                                }
                            }

                            if (imageAsset != null)
                            {
                                string newAssetName = material.Name + "_ImageAsset_" + DateTime.Now.Ticks;
                                AppearanceAssetElement newAssetElement = imageAsset.Duplicate(newAssetName);
                                assetId = newAssetElement.Id;

                                File.AppendAllText(logFile, $"  ✅ New Asset created: {newAssetName} (ID: {assetId})\n");

                                // Set the image path right after duplication
                                File.AppendAllText(logFile, $"  Trying to change the image of the duplicated Asset now\n");

                                try
                                {
                                    using (AppearanceAssetEditScope editScope = new AppearanceAssetEditScope(doc))
                                    {
                                        Asset editableAsset = editScope.Start(assetId);

                                        // Find the unifiedbitmap_Bitmap property
                                        AssetProperty bitmapProp = editableAsset.FindByName("unifiedbitmap_Bitmap");
                                        if (bitmapProp is AssetPropertyString bitmapString)
                                        {
                                            string oldValue = bitmapString.Value;
                                            bitmapString.Value = imagePath;
                                            File.AppendAllText(logFile, $"  ✅ Image changed right after Asset duplication\n");
                                            File.AppendAllText(logFile, $"    Before: {oldValue}\n");
                                            File.AppendAllText(logFile, $"    After: {imagePath}\n");
                                        }
                                        else
                                        {
                                            File.AppendAllText(logFile, $"  ⚠️ unifiedbitmap_Bitmap property not found\n");
                                        }

                                        editScope.Commit(true);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    File.AppendAllText(logFile, $"  ⚠️ Image change error right after Asset duplication: {ex.Message}\n");
                                }

                                material.AppearanceAssetId = assetId;
                            }
                            else
                            {
                                File.AppendAllText(logFile, $"  ❌ No image-capable Asset found\n");
                                File.AppendAllText(logFile, $"  ⚠️ Fallback: show the material with the Material color\n");

                                // Color map per material
                                Dictionary<string, Autodesk.Revit.DB.Color> textureColorMap = new Dictionary<string, Autodesk.Revit.DB.Color>
                                {
                                    { "marble", new Autodesk.Revit.DB.Color(240, 240, 240) },  // White-gray
                                    { "tile", new Autodesk.Revit.DB.Color(220, 200, 180) },    // Beige
                                    { "wood", new Autodesk.Revit.DB.Color(139, 90, 60) },      // Brown
                                    { "concrete", new Autodesk.Revit.DB.Color(180, 180, 180) }, // Gray
                                    { "carpet", new Autodesk.Revit.DB.Color(160, 140, 120) },  // Light brown
                                    { "stone", new Autodesk.Revit.DB.Color(128, 128, 128) }    // Dark gray
                                };

                                if (textureColorMap.TryGetValue(normalizedTexture, out Autodesk.Revit.DB.Color targetColor))
                                {
                                    material.Color = targetColor;
                                    File.AppendAllText(logFile, $"  ✅ Material color changed: RGB({targetColor.Red}, {targetColor.Green}, {targetColor.Blue})\n");
                                    updatedCount++;
                                }
                                else
                                {
                                    File.AppendAllText(logFile, $"  ⚠️ No color mapping: {normalizedTexture}\n");
                                }

                                processedTypes.Add(floorType.Id);
                                continue;
                            }
                        }

                        if (assetId == ElementId.InvalidElementId)
                        {
                            File.AppendAllText(logFile, $"  ❌ Asset creation failed\n");
                            System.Diagnostics.Debug.WriteLine($"  Asset creation failed");
                            processedTypes.Add(floorType.Id);
                            continue;
                        }

                        // Edit the Asset (recheck and set the image path)
                        AppearanceAssetElement appearanceAsset = doc.GetElement(assetId) as AppearanceAssetElement;
                        if (appearanceAsset != null)
                        {
                            File.AppendAllText(logFile, $"  Current Asset name: {appearanceAsset.Name}\n");
                            File.AppendAllText(logFile, $"  === Final image path check and set ===\n");

                            using (AppearanceAssetEditScope editScope = new AppearanceAssetEditScope(doc))
                            {
                                Asset editableAsset = editScope.Start(assetId);
                                File.AppendAllText(logFile, $"  Asset property count: {editableAsset.Size}\n");

                                // Find the image property
                                string[] propertyNames = { "unifiedbitmap_Bitmap", "generic_diffuse" };
                                bool imageSet = false;

                                foreach (string propName in propertyNames)
                                {
                                    AssetProperty imageProperty = editableAsset.FindByName(propName);
                                    if (imageProperty != null)
                                    {
                                        File.AppendAllText(logFile, $"  Property found: {propName} (type: {imageProperty.GetType().Name})\n");

                                        if (imageProperty is AssetPropertyString imageString)
                                        {
                                            string oldValue = imageString.Value;

                                            // Check that the path is the one we want
                                            if (oldValue != imagePath)
                                            {
                                                imageString.Value = imagePath;
                                                File.AppendAllText(logFile, $"    ⚠️ Old value differs! Overwriting\n");
                                                File.AppendAllText(logFile, $"    Before: {oldValue}\n");
                                                File.AppendAllText(logFile, $"    New: {imagePath}\n");
                                            }
                                            else
                                            {
                                                File.AppendAllText(logFile, $"    ✅ Path already correct: {imagePath}\n");
                                            }

                                            File.AppendAllText(logFile, $"  ✅ Image path set\n");
                                            System.Diagnostics.Debug.WriteLine($"  ✅ Image set: {propName}");
                                            imageSet = true;
                                            break;
                                        }
                                        else
                                        {
                                            File.AppendAllText(logFile, $"    ⚠️ Not an AssetPropertyString\n");
                                        }
                                    }
                                }

                                if (!imageSet)
                                {
                                    File.AppendAllText(logFile, $"  ❌ Image property not found\n");
                                    File.AppendAllText(logFile, $"  === All available properties ===\n");
                                    for (int i = 0; i < editableAsset.Size; i++)
                                    {
                                        AssetProperty prop = editableAsset.Get(i);
                                        File.AppendAllText(logFile, $"    [{i}] {prop.Name} ({prop.GetType().Name})\n");
                                    }
                                }

                                editScope.Commit(true);
                                File.AppendAllText(logFile, $"  Commit done\n");

                                if (imageSet)
                                {
                                    processedTypes.Add(floorType.Id);
                                    updatedCount++;
                                    File.AppendAllText(logFile, $"  ✅ Floor type changed\n");
                                    System.Diagnostics.Debug.WriteLine($"  ✅ Change done");
                                }
                            }
                        }
                        else
                        {
                            File.AppendAllText(logFile, $"  ❌ Could not get the AppearanceAssetElement\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(logFile, $"  ❌ error: {ex.Message}\n");
                        File.AppendAllText(logFile, $"  Stack: {ex.StackTrace}\n");
                        System.Diagnostics.Debug.WriteLine($"  ⚠️ error: {ex.Message}");
                    }
                }

                File.AppendAllText(logFile, $"\n=== Done: {updatedCount} types updated ===\n");

                if (updatedCount == 0)
                {
                    File.AppendAllText(logFile, $"\n⚠️ Image update failed\n");
                    File.AppendAllText(logFile, $"Revit has no image-capable Asset.\n\n");
                    File.AppendAllText(logFile, $"=== How to set it by hand ===\n");
                    File.AppendAllText(logFile, $"1. Revit → open the Material Browser\n");
                    File.AppendAllText(logFile, $"2. Select the 'Floor_tile' material\n");
                    File.AppendAllText(logFile, $"3. Appearance tab → Image section\n");
                    File.AppendAllText(logFile, $"4. Select the image file:\n");
                    File.AppendAllText(logFile, $"   {imagePath}\n");
                    File.AppendAllText(logFile, $"5. Apply\n\n");
                    File.AppendAllText(logFile, $"The Material color was changed instead.\n");
                }

                System.Diagnostics.Debug.WriteLine($"\n=== Done: {updatedCount} types ===\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"\n❌ Overall error: {ex.Message}\n");
                File.AppendAllText(logFile, $"Stack: {ex.StackTrace}\n");
                System.Diagnostics.Debug.WriteLine($"❌ UpdateFloorTexture error: {ex.Message}");
            }

            return updatedCount;
        }

        private Autodesk.Revit.DB.Color ConvertColorNameToRGB(string colorName)
        {
            // Convert to lowercase to ignore case
            string lowerColorName = colorName.Trim().ToLower();

            System.Diagnostics.Debug.WriteLine($"[Color convert] input: '{colorName}' → normalized: '{lowerColorName}'");

            Dictionary<string, (byte, byte, byte)> colorMap = new Dictionary<string, (byte, byte, byte)>()
            {
                // Basic colors
                { "white", (255, 255, 255) },
                { "black", (0, 0, 0) },
                { "gray", (128, 128, 128) },
                { "grey", (128, 128, 128) },
                
                // Light gray variants
                { "light gray", (192, 192, 192) },
                { "light grey", (192, 192, 192) },
                { "lightgray", (192, 192, 192) },
                { "lightgrey", (192, 192, 192) },
                
                // Dark gray variants
                { "dark gray", (64, 64, 64) },
                { "dark grey", (64, 64, 64) },
                { "darkgray", (64, 64, 64) },
                { "darkgrey", (64, 64, 64) },
                
                // Beige/ivory
                { "beige", (245, 245, 220) },
                { "ivory", (255, 255, 240) },
                { "cream", (255, 253, 208) },
                
                // Brown
                { "brown", (139, 69, 19) },
                { "tan", (210, 180, 140) },
                
                // Primary colors
                { "red", (255, 0, 0) },
                { "green", (0, 128, 0) },
                { "blue", (0, 0, 255) },
                { "yellow", (255, 255, 0) },
                { "cyan", (0, 255, 255) },
                { "magenta", (255, 0, 255) },
                
                // Pastel tones
                { "pink", (255, 192, 203) },
                { "lavender", (230, 230, 250) },
                { "mint", (189, 252, 201) },
                { "peach", (255, 218, 185) },
                
                // Other
                { "orange", (255, 165, 0) },
                { "purple", (128, 0, 128) },
                { "navy", (0, 0, 128) },
                { "teal", (0, 128, 128) }
            };

            // Parse RGB format "r,g,b"
            if (colorName.Contains(","))
            {
                try
                {
                    string[] rgb = colorName.Split(',');
                    if (rgb.Length == 3)
                    {
                        byte r = byte.Parse(rgb[0].Trim());
                        byte g = byte.Parse(rgb[1].Trim());
                        byte b = byte.Parse(rgb[2].Trim());
                        System.Diagnostics.Debug.WriteLine($"[Color convert] RGB parsed: ({r}, {g}, {b})");
                        return new Autodesk.Revit.DB.Color(r, g, b);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Color convert] RGB parse error: {ex.Message}");
                }
            }

            // Look up the color map
            if (colorMap.ContainsKey(lowerColorName))
            {
                var (r, g, b) = colorMap[lowerColorName];
                System.Diagnostics.Debug.WriteLine($"[Color convert] match found: ({r}, {g}, {b})");
                return new Autodesk.Revit.DB.Color(r, g, b);
            }

            // Default (light beige)
            System.Diagnostics.Debug.WriteLine($"[Color convert] ⚠️ No match - using default: (245, 245, 220)");
            System.Diagnostics.Debug.WriteLine($"[Color convert] Available colors: {string.Join(", ", colorMap.Keys)}");
            return new Autodesk.Revit.DB.Color(245, 245, 220);
        }

        private Material CreateOrGetMaterial(Document doc, string materialName, Autodesk.Revit.DB.Color color)
        {
            try
            {
                Material targetMaterial = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(m => m.Name == materialName);

                if (targetMaterial == null)
                {
                    ElementId newMaterialId = Material.Create(doc, materialName);
                    targetMaterial = doc.GetElement(newMaterialId) as Material;

                    if (targetMaterial == null)
                        return null;
                }

                targetMaterial.Color = color;
                targetMaterial.SurfaceForegroundPatternColor = color;
                targetMaterial.SurfaceBackgroundPatternColor = color;

                ElementId assetId = targetMaterial.AppearanceAssetId;

                if (assetId == ElementId.InvalidElementId)
                {
                    FilteredElementCollector assetCollector = new FilteredElementCollector(doc);
                    AppearanceAssetElement defaultAsset = assetCollector
                        .OfClass(typeof(AppearanceAssetElement))
                        .Cast<AppearanceAssetElement>()
                        .FirstOrDefault(a => a.Name.Contains("Generic"));

                    if (defaultAsset != null)
                    {
                        assetId = defaultAsset.Duplicate(materialName + "_Appearance").Id;
                        targetMaterial.AppearanceAssetId = assetId;
                    }
                }

                if (assetId != ElementId.InvalidElementId)
                {
                    AppearanceAssetElement appearanceAssetElement = doc.GetElement(assetId) as AppearanceAssetElement;

                    if (appearanceAssetElement != null)
                    {
                        using (AppearanceAssetEditScope editScope = new AppearanceAssetEditScope(doc))
                        {
                            Asset editableAsset = editScope.Start(assetId);
                            AssetProperty colorProperty = editableAsset.FindByName("generic_diffuse");

                            if (colorProperty != null && colorProperty is AssetPropertyDoubleArray4d diffuse)
                            {
                                double r = color.Red / 255.0;
                                double g = color.Green / 255.0;
                                double b = color.Blue / 255.0;

                                diffuse.SetValueAsDoubles(new double[] { r, g, b, 1.0 });
                            }

                            editScope.Commit(true);
                        }
                    }
                }

                return targetMaterial;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ CreateOrGetMaterial error: {ex.Message}");
                return null;
            }
        }

    }
}