using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;

namespace ContourCheckPlugin
{
    // 纯 C# 内存数据结构 (多线程安全)
    public class ContourDTO
    {
        public ObjectId Id { get; set; }
        public string Handle { get; set; }
        public double Elevation { get; set; }
        public List<Point2d> Vertices { get; set; } = new List<Point2d>();

        // 用于等高线自检的计算式合并
        public List<ObjectId> MergedIds { get; set; } = new List<ObjectId>();
        public List<string> MergedHandles { get; set; } = new List<string>();
    }

    public class GcdDTO
    {
        public ObjectId Id { get; set; }
        public Point3d Position { get; set; }
        public bool IsError { get; set; } = false;
        public string ErrorMsg { get; set; }
    }

    public class ContourChecker
    {
        // =====================================================================
        // 功能 1：【纯粹的高程点独立检查】 
        // =====================================================================
        [CommandMethod("CHECK_GCD")]
        public void CheckGcd()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptStringOptions pso = new PromptStringOptions("\n请输入等高线图层名称 (多个用逗号隔开，仅用于作为参考系): ");
            pso.DefaultValue = "811021,812021,831021";
            pso.UseDefaultValue = true;
            PromptResult pr = ed.GetString(pso);
            if (pr.Status != PromptStatus.OK) return;

            HashSet<string> targetLayers = new HashSet<string>(
                pr.StringResult.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim().ToUpper())
            );

            PromptDoubleOptions pdo = new PromptDoubleOptions("\n请输入固定等高距 (如 2.0): ");
            pdo.DefaultValue = 2.0;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double interval = pdr.Value;

            List<ContourDTO> contourDTOs = new List<ContourDTO>();
            List<GcdDTO> gcdDTOs = new List<GcdDTO>();

            // 阶段一：【提取原始数据】
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    Entity ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    if (ent is Polyline poly && targetLayers.Contains(poly.Layer.ToUpper()))
                    {
                        var dto = new ContourDTO
                        {
                            Id = poly.ObjectId,
                            Handle = poly.Handle.Value.ToString("X"),
                            Elevation = poly.Elevation
                        };
                        for (int i = 0; i < poly.NumberOfVertices; i++)
                        {
                            dto.Vertices.Add(poly.GetPoint2dAt(i));
                        }
                        if (dto.Vertices.Count > 1) contourDTOs.Add(dto);
                    }
                    else if (ent is BlockReference br && string.Equals(br.Name, "GCD", StringComparison.OrdinalIgnoreCase))
                    {
                        gcdDTOs.Add(new GcdDTO
                        {
                            Id = br.ObjectId,
                            Position = br.Position
                        });
                    }
                }
                tr.Commit();
            }

            if (contourDTOs.Count < 1 || gcdDTOs.Count == 0)
            {
                Application.ShowAlertDialog("【提示】图纸中未找到有效的等高线或 GCD 高程点！");
                return;
            }

            // 阶段二：【高程点计算】 (纯净版，多线程)
            Parallel.ForEach(gcdDTOs, gcd =>
            {
                Point2d p2d = new Point2d(gcd.Position.X, gcd.Position.Y);
                double zP = gcd.Position.Z;

                ContourDTO c1 = null;
                double minDist1 = double.MaxValue;
                Point2d np1 = Point2d.Origin;

                foreach (var cItem in contourDTOs)
                {
                    Point2d closest = FastGetClosestPoint(p2d, cItem.Vertices, out double d);
                    if (d < minDist1)
                    {
                        minDist1 = d;
                        c1 = cItem;
                        np1 = closest;
                    }
                }

                if (c1 == null) return;

                double z1 = c1.Elevation;
                bool isError = false;

                if (minDist1 < 0.001)
                {
                    if (Math.Abs(zP - z1) > 0.001) isError = true;
                }
                else
                {
                    Vector2d dir = (p2d - np1).GetNormal();
                    double rayLength = Math.Max(interval * 100.0, 200.0);
                    Point2d rayStart = p2d;
                    Point2d rayEnd = p2d + dir * rayLength;

                    ContourDTO c2 = null;
                    double minC2Dist = double.MaxValue;

                    foreach (var cItem in contourDTOs)
                    {
                        if (FastLineIntersectsPolyline(rayStart, rayEnd, cItem.Vertices, out Point2d intPt))
                        {
                            if (cItem == c1 && intPt.GetDistanceTo(np1) < 0.01) continue;

                            double dist = p2d.GetDistanceTo(intPt);
                            if (dist < minC2Dist && dist > 0.001)
                            {
                                minC2Dist = dist;
                                c2 = cItem;
                            }
                        }
                    }

                    if (c2 != null)
                    {
                        double z2 = c2.Elevation;
                        double minZ, maxZ;

                        if (Math.Abs(z1 - z2) < 0.001)
                        {
                            minZ = z1 - interval;
                            maxZ = z1 + interval;
                        }
                        else
                        {
                            minZ = Math.Min(z1, z2);
                            maxZ = Math.Max(z1, z2);
                        }

                        if (zP < (minZ - 0.001) || zP > (maxZ + 0.001)) isError = true;
                    }
                    else
                    {
                        if (zP < (z1 - interval - 0.001) || zP > (z1 + interval + 0.001)) isError = true;
                    }
                }

                if (isError) gcd.IsError = true;
            });

            // 阶段三：【写回图形】 (仅标记异常高程点)
            int gcdErrorCount = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId errorLayerId;
                if (!lt.Has("ERROR_POINTS"))
                {
                    lt.UpgradeOpen();
                    LayerTableRecord ltr = new LayerTableRecord();
                    ltr.Name = "ERROR_POINTS";
                    ltr.Color = Autodesk.AutoCAD.Colors.Color.FromRgb(210, 160, 255); // 淡紫色
                    errorLayerId = lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                else { errorLayerId = lt["ERROR_POINTS"]; }

                foreach (var gcd in gcdDTOs)
                {
                    if (gcd.IsError)
                    {
                        gcdErrorCount++;
                        BlockReference br = tr.GetObject(gcd.Id, OpenMode.ForWrite) as BlockReference;
                        if (br != null) br.LayerId = errorLayerId;

                        Circle marker = new Circle(gcd.Position, Vector3d.ZAxis,20);
                        marker.LayerId = errorLayerId;
                        marker.ColorIndex = 256;
                        modelSpace.AppendEntity(marker);
                        tr.AddNewlyCreatedDBObject(marker, true);
                    }
                }
                tr.Commit();
            }

            // 阶段四：【弹窗展示结果】
            Application.ShowAlertDialog(
                $"【高程点质检完成】\n\n" +
                $"共处理高程点: {gcdDTOs.Count} 个\n" +
                $"发现越界异常点: {gcdErrorCount} 个\n\n" +
                $"💡 异常点已在图纸上用淡紫色圆圈标记并归入 ERROR_POINTS 图层。");
        }

        // =====================================================================
        // 功能 2：【等高线自检 & 纯计算式断线合并】
        // =====================================================================
        [CommandMethod("CHECK_CONTOUR")]
        public void CheckContour()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptStringOptions pso = new PromptStringOptions("\n请输入等高线图层名称 (多个用逗号隔开): ");
            pso.DefaultValue = "811021,812021,831021";
            pso.UseDefaultValue = true;
            PromptResult pr = ed.GetString(pso);
            if (pr.Status != PromptStatus.OK) return;

            HashSet<string> targetLayers = new HashSet<string>(
                pr.StringResult.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(s => s.Trim().ToUpper())
            );

            // 等高距仅用于后续的相邻高程差检查
            PromptDoubleOptions pdo = new PromptDoubleOptions("\n请输入固定等高距 (如 2.0): ");
            pdo.DefaultValue = 2.0;
            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;
            double interval = pdr.Value;

            const double internalMergeGap = 1.0;

            List<ContourDTO> contourDTOs = new List<ContourDTO>();
            var magentaPolyIds = new List<ObjectId>();

            // 阶段一：【提取数据并检查小数及零高程】
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent is Polyline poly && targetLayers.Contains(poly.Layer.ToUpper()))
                    {
                        double z = poly.Elevation;
                        string handle = poly.Handle.Value.ToString("X");

                        // 如果高程为 0 (容差<0.001)，或小数点后有数值 (即非整数，容差<0.001)
                        bool isZero = Math.Abs(z) < 0.001;
                        bool hasDecimal = Math.Abs(Math.Round(z) - z) > 0.001;

                        if (isZero || hasDecimal)
                        {
                            magentaPolyIds.Add(poly.ObjectId);
                        }

                        var dto = new ContourDTO
                        {
                            Id = poly.ObjectId,
                            Handle = handle,
                            Elevation = z
                        };

                        dto.MergedIds.Add(poly.ObjectId);
                        dto.MergedHandles.Add(handle);

                        for (int i = 0; i < poly.NumberOfVertices; i++)
                        {
                            dto.Vertices.Add(poly.GetPoint2dAt(i));
                        }
                        if (dto.Vertices.Count > 1) contourDTOs.Add(dto);
                    }
                }
                tr.Commit();
            }

            if (contourDTOs.Count < 2)
            {
                Application.ShowAlertDialog("【错误】图纸中指定图层的有效等高线不足 2 条！");
                return;
            }

            // 阶段二：【内存断线计算式缝合 (Virtual Merge)】
            bool hasJoined = true;
            int virtualJoinCount = 0;

            while (hasJoined)
            {
                hasJoined = false;
                for (int i = 0; i < contourDTOs.Count; i++)
                {
                    ContourDTO c1 = contourDTOs[i];
                    Point2d p1Start = c1.Vertices.First();
                    Point2d p1End = c1.Vertices.Last();

                    for (int j = i + 1; j < contourDTOs.Count; j++)
                    {
                        ContourDTO c2 = contourDTOs[j];

                        if (Math.Abs(c1.Elevation - c2.Elevation) < 0.001)
                        {
                            Point2d p2Start = c2.Vertices.First();
                            Point2d p2End = c2.Vertices.Last();
                            bool merged = false;

                            if (p1End.GetDistanceTo(p2Start) <= internalMergeGap)
                            {
                                c1.Vertices.AddRange(c2.Vertices);
                                merged = true;
                            }
                            else if (p1End.GetDistanceTo(p2End) <= internalMergeGap)
                            {
                                var rev2 = new List<Point2d>(c2.Vertices);
                                rev2.Reverse();
                                c1.Vertices.AddRange(rev2);
                                merged = true;
                            }
                            else if (p1Start.GetDistanceTo(p2End) <= internalMergeGap)
                            {
                                c1.Vertices.InsertRange(0, c2.Vertices);
                                merged = true;
                            }
                            else if (p1Start.GetDistanceTo(p2Start) <= internalMergeGap)
                            {
                                var rev2 = new List<Point2d>(c2.Vertices);
                                rev2.Reverse();
                                c1.Vertices.InsertRange(0, rev2);
                                merged = true;
                            }

                            if (merged)
                            {
                                c1.MergedIds.AddRange(c2.MergedIds);
                                c1.MergedHandles.AddRange(c2.MergedHandles);
                                contourDTOs.RemoveAt(j);
                                virtualJoinCount++;
                                hasJoined = true;
                                break;
                            }
                        }
                    }
                    if (hasJoined) break;
                }
            }

            // 阶段三：【多线程检查相邻等高线高差】
            object contourLock = new object();
            var contourCyanIds = new List<ObjectId>();
            HashSet<string> reportedPairs = new HashSet<string>();
            int diffErrorCount = 0;

            Parallel.ForEach(contourDTOs, c1 =>
            {
                Point2d samplePt = c1.Vertices[c1.Vertices.Count / 2];
                ContourDTO nearestC = null;
                double minDist = double.MaxValue;

                foreach (var c2 in contourDTOs)
                {
                    if (c2 == c1) continue;
                    double d = FastGetClosestDistance(samplePt, c2.Vertices);
                    if (d < minDist)
                    {
                        minDist = d;
                        nearestC = c2;
                    }
                }

                if (nearestC != null)
                {
                    double z1 = c1.Elevation;
                    double z2 = nearestC.Elevation;
                    double diff = Math.Abs(z1 - z2);

                    if (Math.Abs(diff) > 0.001 && Math.Abs(diff - interval) > 0.001)
                    {
                        lock (contourLock)
                        {
                            string h1 = c1.MergedHandles[0];
                            string h2 = nearestC.MergedHandles[0];
                            string pairKey = string.Compare(h1, h2) < 0 ? $"{h1}-{h2}" : $"{h2}-{h1}";

                            if (!reportedPairs.Contains(pairKey))
                            {
                                reportedPairs.Add(pairKey);
                                contourCyanIds.AddRange(c1.MergedIds);
                                contourCyanIds.AddRange(nearestC.MergedIds);
                                diffErrorCount++;
                            }
                        }
                    }
                }
            });

            // 阶段四：【纯改色，写回图形 (带有防覆盖逻辑)】
            Autodesk.AutoCAD.Colors.Color magentaColor = Autodesk.AutoCAD.Colors.Color.FromRgb(255, 0, 255);
            Autodesk.AutoCAD.Colors.Color cyanColor = Autodesk.AutoCAD.Colors.Color.FromRgb(0, 191, 255);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 用来记录已经变成了洋红色的线段 ID，优先级最高
                HashSet<ObjectId> coloredMagentaIds = new HashSet<ObjectId>();

                // 1. 优先涂洋红色 (高程为 0 或有小数属于绝对异常，优先展示)
                foreach (ObjectId id in magentaPolyIds.Distinct())
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent != null)
                    {
                        ent.Color = magentaColor;
                        coloredMagentaIds.Add(id); // 记录下来
                    }
                }

                // 2. 接着涂天蓝色 (高程跨度错误)
                foreach (ObjectId id in contourCyanIds.Distinct())
                {
                    // 【关键防冲突】：如果这条线已经被涂成了洋红色，就跳过不覆盖
                    if (coloredMagentaIds.Contains(id)) continue;

                    Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent != null) ent.Color = cyanColor;
                }
                tr.Commit();
            }

            // 阶段五：【弹窗展示结果】
            Application.ShowAlertDialog(
                $"【等高线自检完成】\n\n" +
                $"✅ 成功在内存中计算式缝合断线: {virtualJoinCount} 处\n\n" +
                $"1. 高程为 0 或带有小数 (洋红高亮优先): {magentaPolyIds.Distinct().Count()} 处\n" +
                $"2. 相邻高程差不符规则 (天蓝高亮): {diffErrorCount} 处\n\n" +
                $"💡 异常线段已全部在图纸中直接变色高亮。");
        }

        #region 纯 C# 高性能几何计算辅助算法 (供双模块复用)
        private static double FastGetClosestDistance(Point2d pt, List<Point2d> poly)
        {
            FastGetClosestPoint(pt, poly, out double dist);
            return dist;
        }

        private static Point2d FastGetClosestPoint(Point2d p, List<Point2d> poly, out double minDist)
        {
            minDist = double.MaxValue;
            Point2d closest = poly[0];

            for (int i = 0; i < poly.Count - 1; i++)
            {
                Point2d a = poly[i];
                Point2d b = poly[i + 1];
                Point2d proj = GetClosestPointOnSegment(p, a, b);
                double d = p.GetDistanceTo(proj);
                if (d < minDist)
                {
                    minDist = d;
                    closest = proj;
                }
            }
            return closest;
        }

        private static Point2d GetClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
        {
            Vector2d ab = b - a;
            double lengthSq = ab.X * ab.X + ab.Y * ab.Y;
            if (lengthSq < 0.000001) return a;

            Vector2d ap = p - a;
            double t = (ap.X * ab.X + ap.Y * ab.Y) / lengthSq;
            if (t < 0.0) return a;
            if (t > 1.0) return b;

            return a + ab * t;
        }

        private static bool FastLineIntersectsPolyline(Point2d p1, Point2d p2, List<Point2d> poly, out Point2d intPt)
        {
            intPt = Point2d.Origin;
            double minDist = double.MaxValue;
            bool found = false;

            for (int i = 0; i < poly.Count - 1; i++)
            {
                if (GetLineIntersection(p1, p2, poly[i], poly[i + 1], out Point2d pt))
                {
                    double d = p1.GetDistanceTo(pt);
                    if (d < minDist)
                    {
                        minDist = d;
                        intPt = pt;
                        found = true;
                    }
                }
            }
            return found;
        }

        private static bool GetLineIntersection(Point2d p1, Point2d p2, Point2d p3, Point2d p4, out Point2d pt)
        {
            pt = Point2d.Origin;
            double denominator = (p4.Y - p3.Y) * (p2.X - p1.X) - (p4.X - p3.X) * (p2.Y - p1.Y);
            if (Math.Abs(denominator) < 0.000001) return false;

            double ua = ((p4.X - p3.X) * (p1.Y - p3.Y) - (p4.Y - p3.Y) * (p1.X - p3.X)) / denominator;
            double ub = ((p2.X - p1.X) * (p1.Y - p3.Y) - (p2.Y - p1.Y) * (p1.X - p3.X)) / denominator;

            if (ua >= 0.0 && ua <= 1.0 && ub >= 0.0 && ub <= 1.0)
            {
                pt = new Point2d(p1.X + ua * (p2.X - p1.X), p1.Y + ua * (p2.Y - p1.Y));
                return true;
            }
            return false;
        }
        #endregion
    }
}
