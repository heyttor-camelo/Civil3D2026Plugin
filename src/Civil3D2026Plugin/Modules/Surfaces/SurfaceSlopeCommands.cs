using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Surfaces
{
    public class SurfaceSlopeCommands
    {
        private const double PlaneSlopeTolerancePercent = 1.0;

        [CommandMethod(C3DCommands.Superficies.SlopeFilter)]
        public void FilterTinSurfaceByPlaneSlope()
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                ObjectId sourceSurfaceId = PromptTinSurface(ed);
                if (sourceSurfaceId == ObjectId.Null)
                    return;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    TinSurface? source = tr.GetObject(sourceSurfaceId, OpenMode.ForRead) as TinSurface;
                    if (source == null)
                    {
                        ed.WriteMessage("\nA entidade selecionada não é uma TinSurface.");
                        return;
                    }

                    string filterMode = PromptKeyword(
                        ed,
                        "\nAção [Manter/Remover] <Manter>: ",
                        "Manter Remover",
                        "Manter");

                    string inputMode = PromptKeyword(
                        ed,
                        "\nFormato da inclinação [Percentual/HV/VH] <Percentual>: ",
                        "Percentual HV VH",
                        "Percentual");

                    double minSlope = PromptSlopeValue(ed, $"\nValor mínimo ({GetInputLabel(inputMode)}): ", inputMode);
                    double maxSlope = PromptSlopeValue(ed, $"\nValor máximo ({GetInputLabel(inputMode)}): ", inputMode);

                    if (minSlope > maxSlope)
                    {
                        double tmp = minSlope;
                        minSlope = maxSlope;
                        maxSlope = tmp;
                    }

                    string defaultName = SafeSurfaceName(source.Name + "_FILTRADA_NORMAL");
                    string outName = PromptString(ed, $"\nNome da nova superfície <{defaultName}>: ", defaultName);

                    tr.Commit();

                    CreateFilteredSurface(
                        doc,
                        db,
                        ed,
                        sourceSurfaceId,
                        outName,
                        minSlope,
                        maxSlope,
                        filterMode.Equals("Remover", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.UserBreak)
            {
                return;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nErro: {ex.Message}");
            }
        }

        private void CreateFilteredSurface(
            Document doc,
            Database db,
            Editor ed,
            ObjectId sourceSurfaceId,
            string outputSurfaceName,
            double minSlopePercent,
            double maxSlopePercent,
            bool removeInsideRange)
        {
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                TinSurface? source = tr.GetObject(sourceSurfaceId, OpenMode.ForRead) as TinSurface;
                if (source == null)
                {
                    ed.WriteMessage("\nNão foi possível abrir a superfície origem.");
                    return;
                }

                ObjectId newSurfaceId = TinSurface.Create(db, outputSurfaceName);
                TinSurface? target = tr.GetObject(newSurfaceId, OpenMode.ForWrite) as TinSurface;
                if (target == null)
                {
                    ed.WriteMessage("\nNão foi possível criar a nova superfície.");
                    return;
                }

                target.PasteSurface(sourceSurfaceId);
                target.Rebuild();

                List<TinSurfaceTriangle> triangles = new List<TinSurfaceTriangle>();
                Dictionary<string, bool> triangleKeepMap = new Dictionary<string, bool>();

                int totalTriangles = 0;
                int keptTriangles = 0;
                int rejectedTriangles = 0;

                foreach (TinSurfaceTriangle tri in target.GetTriangles(false))
                {
                    if (!tri.IsValid) continue;

                    totalTriangles++;
                    triangles.Add(tri);

                    Point3d p1 = tri.Vertex1.Location;
                    Point3d p2 = tri.Vertex2.Location;
                    Point3d p3 = tri.Vertex3.Location;

                    double slopePercent = GetTrianglePlaneSlopePercent(p1, p2, p3);

                    bool triMatches = SlopeIsInsideRange(
                        slopePercent,
                        minSlopePercent,
                        maxSlopePercent,
                        PlaneSlopeTolerancePercent);

                    bool keepThisTriangle = removeInsideRange ? !triMatches : triMatches;

                    string triKey = GetTriangleKey(p1, p2, p3);
                    triangleKeepMap[triKey] = keepThisTriangle;

                    if (keepThisTriangle) keptTriangles++;
                    else rejectedTriangles++;
                }

                HashSet<string> edgeKeys = new HashSet<string>();
                List<TinSurfaceEdge> edgesToDelete = new List<TinSurfaceEdge>();

                foreach (TinSurfaceTriangle tri in triangles)
                {
                    TryAddEdgeIfFullyRejected(tri.Edge1, triangleKeepMap, edgeKeys, edgesToDelete);
                    TryAddEdgeIfFullyRejected(tri.Edge2, triangleKeepMap, edgeKeys, edgesToDelete);
                    TryAddEdgeIfFullyRejected(tri.Edge3, triangleKeepMap, edgeKeys, edgesToDelete);
                }

                if (edgesToDelete.Count > 0)
                {
                    target.DeleteLines(edgesToDelete);
                    target.Rebuild();
                }

                tr.Commit();

                ed.WriteMessage(
                    $"\nSuperfície criada: {outputSurfaceName}" +
                    $"\nTriângulos avaliados: {totalTriangles}" +
                    $"\nTriângulos mantidos pelo critério: {keptTriangles}" +
                    $"\nTriângulos rejeitados pelo critério: {rejectedTriangles}" +
                    $"\nLinhas TIN deletadas: {edgesToDelete.Count}" +
                    $"\nFaixa informada: {minSlopePercent:F4}% a {maxSlopePercent:F4}%" +
                    $"\nTolerância aplicada: ±{PlaneSlopeTolerancePercent:F2}%" +
                    $"\nFaixa efetiva: {(minSlopePercent - PlaneSlopeTolerancePercent):F4}% a {(maxSlopePercent + PlaneSlopeTolerancePercent):F4}%.");
            }
        }

        private static void TryAddEdgeIfFullyRejected(
            TinSurfaceEdge edge,
            Dictionary<string, bool> triangleKeepMap,
            HashSet<string> edgeKeys,
            List<TinSurfaceEdge> edgesToDelete)
        {
            if (!edge.IsValid) return;

            bool hasAdjacentTriangle = false;
            bool anyAdjacentKept = false;

            CheckAdjacentTriangle(edge.Triangle1, triangleKeepMap, ref hasAdjacentTriangle, ref anyAdjacentKept);
            CheckAdjacentTriangle(edge.Triangle2, triangleKeepMap, ref hasAdjacentTriangle, ref anyAdjacentKept);

            if (!hasAdjacentTriangle) return;
            if (anyAdjacentKept) return;

            string edgeKey = GetEdgeKey(edge);
            if (edgeKeys.Contains(edgeKey)) return;

            edgeKeys.Add(edgeKey);
            edgesToDelete.Add(edge);
        }

        private static void CheckAdjacentTriangle(
            TinSurfaceTriangle tri,
            Dictionary<string, bool> triangleKeepMap,
            ref bool hasAdjacentTriangle,
            ref bool anyAdjacentKept)
        {
            if (tri == null || !tri.IsValid) return;

            hasAdjacentTriangle = true;

            string triKey = GetTriangleKey(
                tri.Vertex1.Location,
                tri.Vertex2.Location,
                tri.Vertex3.Location);

            if (triangleKeepMap.TryGetValue(triKey, out bool keep) && keep)
            {
                anyAdjacentKept = true;
            }
        }

        private static bool SlopeIsInsideRange(
            double slopePercent,
            double minSlopePercent,
            double maxSlopePercent,
            double tolerancePercent)
        {
            return slopePercent >= (minSlopePercent - tolerancePercent)
                && slopePercent <= (maxSlopePercent + tolerancePercent);
        }

        private static double GetTrianglePlaneSlopePercent(Point3d p1, Point3d p2, Point3d p3)
        {
            Vector3d u = p2 - p1;
            Vector3d v = p3 - p1;
            Vector3d n = u.CrossProduct(v);

            double horizontalComponent = Math.Sqrt((n.X * n.X) + (n.Y * n.Y));
            double verticalComponent = Math.Abs(n.Z);

            if (n.Length < 1e-12)
                return 0.0;

            if (verticalComponent < 1e-12)
                return double.MaxValue;

            return 100.0 * (horizontalComponent / verticalComponent);
        }

        private static string GetTriangleKey(Point3d p1, Point3d p2, Point3d p3)
        {
            List<string> pts = new List<string>
            {
                PointKey(p1),
                PointKey(p2),
                PointKey(p3)
            };

            pts.Sort(StringComparer.Ordinal);
            return string.Join("||", pts);
        }

        private static string GetEdgeKey(TinSurfaceEdge edge)
        {
            Point3d a = edge.Vertex1.Location;
            Point3d b = edge.Vertex2.Location;

            string sa = PointKey(a);
            string sb = PointKey(b);

            return string.CompareOrdinal(sa, sb) <= 0
                ? sa + "->" + sb
                : sb + "->" + sa;
        }

        private static string PointKey(Point3d p)
        {
            return $"{p.X:F6}|{p.Y:F6}|{p.Z:F6}";
        }

        private static ObjectId PromptTinSurface(Editor ed)
        {
            PromptEntityOptions peo = new PromptEntityOptions("\nSelecione a TinSurface: ");
            peo.SetRejectMessage("\nSelecione uma superfície TIN.");
            peo.AddAllowedClass(typeof(TinSurface), false);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return ObjectId.Null;

            return per.ObjectId;
        }

        private static string PromptKeyword(Editor ed, string message, string keywords, string defaultValue)
        {
            PromptKeywordOptions pko = new PromptKeywordOptions(message, keywords);
            pko.AllowNone = true;

            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status == PromptStatus.Cancel)
                throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.UserBreak);

            return string.IsNullOrWhiteSpace(pr.StringResult) ? defaultValue : pr.StringResult;
        }

        private static string PromptString(Editor ed, string message, string defaultValue)
        {
            PromptStringOptions pso = new PromptStringOptions(message);
            pso.AllowSpaces = true;

            PromptResult pr = ed.GetString(pso);
            if (pr.Status == PromptStatus.Cancel)
                throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.UserBreak);

            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult))
                return defaultValue;

            return SafeSurfaceName(pr.StringResult.Trim());
        }

        private static double PromptSlopeValue(Editor ed, string message, string mode)
        {
            while (true)
            {
                PromptStringOptions pso = new PromptStringOptions(message);
                pso.AllowSpaces = false;

                PromptResult pr = ed.GetString(pso);
                if (pr.Status == PromptStatus.Cancel)
                    throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.UserBreak);

                if (pr.Status != PromptStatus.OK)
                    continue;

                string raw = (pr.StringResult ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                try
                {
                    return ParseSlopeToPercent(raw, mode);
                }
                catch
                {
                    ed.WriteMessage($"\nValor inválido. Exemplo aceito para {GetInputLabel(mode)}: {GetInputExample(mode)}");
                }
            }
        }

        private static string GetInputLabel(string mode)
        {
            if (mode.Equals("HV", StringComparison.OrdinalIgnoreCase))
                return "H:V";
            if (mode.Equals("VH", StringComparison.OrdinalIgnoreCase))
                return "V:H";
            return "%";
        }

        private static string GetInputExample(string mode)
        {
            if (mode.Equals("HV", StringComparison.OrdinalIgnoreCase))
                return "1.5:1";
            if (mode.Equals("VH", StringComparison.OrdinalIgnoreCase))
                return "1:1.5";
            return "66.67 ou 66.67%";
        }

        private static double ParseSlopeToPercent(string raw, string mode)
        {
            if (mode.Equals("Percentual", StringComparison.OrdinalIgnoreCase))
            {
                string txt = raw.Replace("%", "").Trim();
                return ParseDoubleFlexible(txt);
            }

            string[] parts = raw.Split(':');
            if (parts.Length != 2)
                throw new FormatException("Relação inválida.");

            double a = ParseDoubleFlexible(parts[0]);
            double b = ParseDoubleFlexible(parts[1]);

            if (a <= 0.0 || b <= 0.0)
                throw new FormatException("A relação deve ter valores positivos.");

            if (mode.Equals("HV", StringComparison.OrdinalIgnoreCase))
            {
                return 100.0 * (b / a);
            }

            if (mode.Equals("VH", StringComparison.OrdinalIgnoreCase))
            {
                return 100.0 * (a / b);
            }

            throw new FormatException("Modo inválido.");
        }

        private static double ParseDoubleFlexible(string txt)
        {
            string s = txt.Trim().Replace(" ", "");

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double v1))
                return v1;

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v2))
                return v2;

            s = s.Replace(",", ".");
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v3))
                return v3;

            throw new FormatException("Número inválido.");
        }

        private static string SafeSurfaceName(string name)
        {
            string s = name.Trim();

            foreach (char c in new[] { '<', '>', '/', '\\', '\"', ':', ';', '?', '*', '|', '=', ',' })
            {
                s = s.Replace(c, '_');
            }

            while (s.Contains("  "))
                s = s.Replace("  ", " ");

            if (string.IsNullOrWhiteSpace(s))
                s = "SUPERFICIE_FILTRADA";

            return s;
        }
    }
}