using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SplitWallsByLevels
{
    [Transaction(TransactionMode.Manual)]
    public class CmdSplitWallsByLevels : IExternalCommand
    {
        private const double EPS = 1e-6;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            var selIds = uidoc.Selection.GetElementIds();
            if (selIds == null || selIds.Count == 0)
            {
                TaskDialog.Show("Split Walls", "Select Basic Walls + Levels (in the same selection), then run the command.");
                return Result.Cancelled;
            }

            var walls = new List<Wall>();
            var levels = new List<Level>();

            foreach (var id in selIds)
            {
                var e = doc.GetElement(id);
                if (e is Wall w) walls.Add(w);
                else if (e is Level lv) levels.Add(lv);
            }

            if (walls.Count == 0)
            {
                TaskDialog.Show("Split Walls", "No Walls found in selection.");
                return Result.Cancelled;
            }

            if (levels.Count == 0)
            {
                TaskDialog.Show("Split Walls", "No Levels found in selection. Select one or more Levels as split boundaries.");
                return Result.Cancelled;
            }

            levels = levels
                .GroupBy(l => l.Id.Value)   // Revit 2024+: Value
                .Select(g => g.First())
                .OrderBy(l => l.Elevation)
                .ToList();

            int createdTotal = 0;
            int replacedWalls = 0;
            int skippedTotal = 0;

            using (TransactionGroup tg = new TransactionGroup(doc, "Split Basic Walls by Selected Levels"))
            {
                tg.Start();

                using (Transaction t = new Transaction(doc, "Split"))
                {
                    t.Start();

                    foreach (var wall in walls)
                    {
                        if (!IsBasicWallSafe(wall, out string whySkip))
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, whySkip, levels, null);
                            continue;
                        }

                        if (!(wall.Location is LocationCurve lc) || lc.Curve == null)
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, "No LocationCurve (wall has no curve).", levels, null);
                            continue;
                        }

                        Curve wallCurve = lc.Curve;
                        ElementId wallTypeId = wall.GetTypeId();
                        bool flip = wall.Flipped;
                        bool isStructural = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;

                        // BASE
                        var pBase = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                        if (pBase == null)
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, "Missing WALL_BASE_CONSTRAINT.", levels, null);
                            continue;
                        }

                        Level baseLevel = doc.GetElement(pBase.AsElementId()) as Level;
                        if (baseLevel == null)
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, "Base Level is null.", levels, null);
                            continue;
                        }

                        double baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;
                        double baseElev = baseLevel.Elevation + baseOffset;

                        // TOP via WALL_HEIGHT_TYPE
                        ElementId topLevelId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId()
                                               ?? ElementId.InvalidElementId;

                        bool topIsLevel = topLevelId != ElementId.InvalidElementId;

                        Level topLevel = null;
                        double topOffset = 0.0;
                        double unconnectedHeight = 0.0;
                        double topElev;

                        if (topIsLevel)
                        {
                            topLevel = doc.GetElement(topLevelId) as Level;
                            if (topLevel == null)
                            {
                                skippedTotal++;
                                ShowDebugSkip(doc, wall, "Top is constrained, but Top Level is null.", levels,
                                    new WallHeights(baseLevel, baseOffset, null, 0, 0, baseElev, double.NaN, true));
                                continue;
                            }

                            topOffset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0.0;
                            topElev = topLevel.Elevation + topOffset;
                        }
                        else
                        {
                            unconnectedHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0.0;
                            topElev = baseElev + unconnectedHeight;
                        }

                        if (topElev <= baseElev + EPS)
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, "Top elevation <= Base elevation (invalid height).", levels,
                                new WallHeights(baseLevel, baseOffset, topLevel, topOffset, unconnectedHeight, baseElev, topElev, topIsLevel));
                            continue;
                        }

                        var heights = new WallHeights(baseLevel, baseOffset, topLevel, topOffset, unconnectedHeight, baseElev, topElev, topIsLevel);

                        // Split boundaries
                        var splitLevels = levels
                            .Where(lv => lv.Elevation > baseElev + EPS && lv.Elevation < topElev - EPS)
                            .OrderBy(lv => lv.Elevation)
                            .ToList();

                        if (splitLevels.Count == 0)
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, "No selected levels fall strictly inside the wall height range.", levels, heights);
                            continue; // safe: do NOT delete original
                        }

                        var createdIds = new List<ElementId>();

                        Wall CreateSegment(Level segBaseLevel, double segBaseOffset, Level segTopLevelOrNull, double segTopOffsetOrZero, double segUnconnectedHeight)
                        {
                            // NOTE: This creates a new wall segment (straight copy of curve).
                            // If the original was slanted, the created will be vertical (API limitation via Wall.Create overload).
                            var newWall = Wall.Create(doc, wallCurve, wallTypeId, segBaseLevel.Id, segUnconnectedHeight, segBaseOffset, flip, isStructural);

                            CopySafeInstanceParams(wall, newWall);

                            if (segTopLevelOrNull != null)
                            {
                                var pTopType = newWall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                                if (pTopType != null && !pTopType.IsReadOnly)
                                    pTopType.Set(segTopLevelOrNull.Id);

                                var pTopOffset = newWall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                                if (pTopOffset != null && !pTopOffset.IsReadOnly)
                                    pTopOffset.Set(segTopOffsetOrZero);
                            }

                            return newWall;
                        }

                        // Segment 0
                        {
                            Level segTop = splitLevels[0];
                            double segHeight = segTop.Elevation - baseElev;
                            if (segHeight > EPS)
                            {
                                var w0 = CreateSegment(baseLevel, baseOffset, segTop, 0.0, segHeight);
                                createdIds.Add(w0.Id);
                            }
                        }

                        // Middle
                        for (int i = 1; i < splitLevels.Count; i++)
                        {
                            Level segBase = splitLevels[i - 1];
                            Level segTop = splitLevels[i];

                            double segHeight = segTop.Elevation - segBase.Elevation;
                            if (segHeight > EPS)
                            {
                                var wi = CreateSegment(segBase, 0.0, segTop, 0.0, segHeight);
                                createdIds.Add(wi.Id);
                            }
                        }

                        // Last
                        {
                            Level lastBase = splitLevels.Last();
                            double lastBaseElev = lastBase.Elevation;

                            if (topIsLevel)
                            {
                                double segHeight = (topLevel.Elevation + topOffset) - lastBaseElev;
                                if (segHeight > EPS)
                                {
                                    var wLast = CreateSegment(lastBase, 0.0, topLevel, topOffset, segHeight);
                                    createdIds.Add(wLast.Id);
                                }
                            }
                            else
                            {
                                double segHeight = topElev - lastBaseElev;
                                if (segHeight > EPS)
                                {
                                    var wLast = CreateSegment(lastBase, 0.0, null, 0.0, segHeight);
                                    createdIds.Add(wLast.Id);
                                }
                            }
                        }

                        if (createdIds.Count == 0)
                        {
                            skippedTotal++;
                            ShowDebugSkip(doc, wall, "Computed segments are zero (unexpected).", levels, heights);
                            continue;
                        }

                        // Replace original
                        doc.Delete(wall.Id);
                        replacedWalls++;
                        createdTotal += createdIds.Count;
                    }

                    t.Commit();
                }

                tg.Assimilate();
            }

            TaskDialog.Show("Split Walls",
                $"Done.\nCreated walls: {createdTotal}\nReplaced original walls: {replacedWalls}\nSkipped elements: {skippedTotal}\n\n" +
                "Rule: Only selected Levels strictly between wall Base and Top are used as split boundaries.\n" +
                "If a wall is skipped, a DEBUG dialog will show why.");

            return Result.Succeeded;
        }

        // Only enforce: not Curtain, not Stacked, not Profile-edited.
        // IMPORTANT: we DO NOT block by Cross-Section anymore.
        private static bool IsBasicWallSafe(Wall wall, out string why)
        {
            why = null;

            if (wall.WallType == null)
            {
                why = "WallType is null.";
                return false;
            }

            if (wall.WallType.Kind == WallKind.Curtain)
            {
                why = "Curtain Wall is not supported.";
                return false;
            }

            if (wall.WallType.Kind == WallKind.Stacked)
            {
                why = "Stacked Wall is not supported.";
                return false;
            }

            if (wall.SketchId != ElementId.InvalidElementId)
            {
                why = "Wall has edited profile (SketchId != Invalid).";
                return false;
            }

            return true;
        }

        private class WallHeights
        {
            public Level BaseLevel;
            public double BaseOffset;
            public Level TopLevel;
            public double TopOffset;
            public double UnconnectedHeight;
            public double BaseElev;
            public double TopElev;
            public bool TopIsLevel;

            public WallHeights(Level baseLevel, double baseOffset, Level topLevel, double topOffset, double unconnectedHeight, double baseElev, double topElev, bool topIsLevel)
            {
                BaseLevel = baseLevel;
                BaseOffset = baseOffset;
                TopLevel = topLevel;
                TopOffset = topOffset;
                UnconnectedHeight = unconnectedHeight;
                BaseElev = baseElev;
                TopElev = topElev;
                TopIsLevel = topIsLevel;
            }
        }

        private static void ShowDebugSkip(Document doc, Wall wall, string reason, List<Level> selectedLevels, WallHeights heightsOrNull)
        {
            try
            {
                double FtToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

                string lvInfo = string.Join("\n", selectedLevels.Select(lv => $"{lv.Name,-20} elev(mm)={FtToMm(lv.Elevation):F1}"));

                string heightInfo = "(height info unavailable)";
                if (heightsOrNull != null)
                {
                    var h = heightsOrNull;
                    heightInfo =
                        $"Base: {h.BaseLevel?.Name}  baseElev(mm)={FtToMm(h.BaseElev):F1}  (BaseOffset mm={FtToMm(h.BaseOffset):F1})\n" +
                        (h.TopIsLevel
                            ? $"Top : {h.TopLevel?.Name}  topElev(mm)={FtToMm(h.TopElev):F1}  (TopOffset mm={FtToMm(h.TopOffset):F1})\n"
                            : $"Top : Unconnected topElev(mm)={FtToMm(h.TopElev):F1}  (Height mm={FtToMm(h.UnconnectedHeight):F1})\n");
                }

                TaskDialog.Show("DEBUG - Wall skipped",
                    $"WallId: {wall.Id.Value}\nReason: {reason}\n\n" +
                    $"{heightInfo}\n" +
                    $"Selected Levels ({selectedLevels.Count}):\n{lvInfo}\n\n" +
                    "Split rule: levels must be strictly BETWEEN baseElev and topElev.");
            }
            catch { }
        }

        private static void CopySafeInstanceParams(Wall src, Wall dst)
        {
            CopyParam(src, dst, BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            CopyParam(src, dst, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            CopyParam(src, dst, BuiltInParameter.ALL_MODEL_MARK);
            CopyParam(src, dst, BuiltInParameter.WALL_KEY_REF_PARAM); // Location line
        }

        private static void CopyParam(Element src, Element dst, BuiltInParameter bip)
        {
            try
            {
                var pSrc = src.get_Parameter(bip);
                var pDst = dst.get_Parameter(bip);
                if (pSrc == null || pDst == null) return;
                if (pDst.IsReadOnly) return;
                if (pSrc.StorageType != pDst.StorageType) return;

                switch (pSrc.StorageType)
                {
                    case StorageType.Integer: pDst.Set(pSrc.AsInteger()); break;
                    case StorageType.Double: pDst.Set(pSrc.AsDouble()); break;
                    case StorageType.String: pDst.Set(pSrc.AsString()); break;
                    case StorageType.ElementId: pDst.Set(pSrc.AsElementId()); break;
                }
            }
            catch { }
        }
    }
}