using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;

namespace Civil3D2026Plugin.Modules.FeatureLines;

public class FeatureLineElevationCommands
{
    [CommandMethod(C3DCommands.FeatureLines.ZSet)]
    public void FLZSET()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;

        // 0) Modo inicial
        PromptKeywordOptions startOpt = new PromptKeywordOptions(
            "\nModo inicial [Corte/Selecionar] <Selecionar>: ",
            "Corte Selecionar");
        startOpt.AllowNone = true;

        PromptResult startRes = ed.GetKeywords(startOpt);
        if (startRes.Status == PromptStatus.Cancel) return;

        string startMode = string.IsNullOrWhiteSpace(startRes.StringResult) ? "Selecionar" : startRes.StringResult;
        bool useCut = startMode.Equals("Corte", System.StringComparison.OrdinalIgnoreCase);

        // 1) Seleção base (sempre)
        PromptSelectionOptions pso = new PromptSelectionOptions();
        pso.MessageForAdding = useCut
            ? "\nSelecione as Feature Lines que serão analisadas pelo corte: "
            : "\nSelecione Feature Lines (pode selecionar várias): ";
        pso.MessageForRemoval = "\nRemover da seleção: ";

        PromptSelectionResult psrBase = ed.GetSelection(pso);
        if (psrBase.Status != PromptStatus.OK || psrBase.Value == null || psrBase.Value.Count == 0)
            return;

        SelectionSet ss = psrBase.Value;

        // Variáveis do corte
        Point3d a = default(Point3d);
        Point3d b = default(Point3d);
        Vector2d ab = default(Vector2d);
        string side = "LEFT";

        if (useCut)
        {
            // Linha de corte (2 cliques)
            PromptPointResult p1r = ed.GetPoint("\n1º ponto da linha de corte: ");
            if (p1r.Status != PromptStatus.OK) return;

            PromptPointOptions p2o = new PromptPointOptions("\n2º ponto da linha de corte: ");
            p2o.BasePoint = p1r.Value;
            p2o.UseBasePoint = true;

            PromptPointResult p2r = ed.GetPoint(p2o);
            if (p2r.Status != PromptStatus.OK) return;

            a = p1r.Value;
            b = p2r.Value;

            ab = new Vector2d(b.X - a.X, b.Y - a.Y);
            if (ab.Length < 1e-9)
            {
                ed.WriteMessage("\nLinha de corte inválida (pontos iguais).");
                return;
            }

            // Clique do lado (reconhece LEFT/RIGHT automaticamente)
            PromptPointResult sidePick = ed.GetPoint("\nClique em um ponto do lado que você quer usar para avaliar (esq/dir da linha): ");
            if (sidePick.Status != PromptStatus.OK) return;

            Vector2d ap = new Vector2d(sidePick.Value.X - a.X, sidePick.Value.Y - a.Y);
            double cross = (ab.X * ap.Y) - (ab.Y * ap.X);

            side = (cross > 0.0) ? "LEFT" : "RIGHT";
        }

        // 2) Modo: MAX/MIN/VAL/PICK
        PromptKeywordOptions pko = new PromptKeywordOptions("\nModo [MAX/MIN/VAL/PICK] <MAX>: ", "MAX MIN VAL PICK");
        pko.AllowNone = true;

        PromptResult pkr = ed.GetKeywords(pko);
        if (pkr.Status == PromptStatus.Cancel) return;

        string mode = string.IsNullOrWhiteSpace(pkr.StringResult) ? "MAX" : pkr.StringResult.ToUpperInvariant();

        double fixedZ = 0.0;

        if (mode == "VAL")
        {
            PromptDoubleOptions pdo = new PromptDoubleOptions("\nDigite a cota (Z) para aplicar: ");
            pdo.AllowNone = false;
            pdo.AllowNegative = true;

            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK) return;

            fixedZ = pdr.Value;
        }
        else if (mode == "PICK")
        {
            PromptEntityOptions peo = new PromptEntityOptions("\nSelecione UMA Feature Line para clicar no PI e copiar a cota: ");
            peo.SetRejectMessage("\nIsso não é uma Feature Line.");
            peo.AddAllowedClass(typeof(FeatureLine), false);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            PromptPointResult ppr = ed.GetPoint("\nClique perto do PI/vértice para copiar a cota (Z): ");
            if (ppr.Status != PromptStatus.OK) return;

            using (Transaction trPick = doc.Database.TransactionManager.StartTransaction())
            {
                FeatureLine? flPick = trPick.GetObject(per.ObjectId, OpenMode.ForRead) as FeatureLine;
                if (flPick == null) return;

                Point3d closest;
                if (!TryGetClosestFeatureLinePoint(flPick, ppr.Value, out closest))
                {
                    ed.WriteMessage("\nNão consegui achar um PI/ElevationPoint nessa Feature Line.");
                    return;
                }

                fixedZ = closest.Z;
                trPick.Commit();
            }
        }

        int totalProcessados = 0;
        int totalIgnorados = 0;

        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (SelectedObject so in ss)
            {
                if (so == null || so.ObjectId == ObjectId.Null)
                {
                    totalIgnorados++;
                    continue;
                }

                FeatureLine? fl = tr.GetObject(so.ObjectId, OpenMode.ForWrite, false) as FeatureLine;
                if (fl == null)
                {
                    totalIgnorados++;
                    continue;
                }

                // --- DEFINE targetZ ---
                double targetZ;

                if (!useCut)
                {
                    // Selecionar: comportamento antigo (MAX/MIN do perfil inteiro)
                    if (mode == "MIN") targetZ = fl.MinElevation;
                    else if (mode == "VAL") targetZ = fixedZ;
                    else if (mode == "PICK") targetZ = fixedZ;
                    else targetZ = fl.MaxElevation; // MAX
                }
                else
                {
                    // Corte:
                    // - VAL/PICK: usa direto
                    // - MAX/MIN: calcula MAX/MIN somente dos pontos do LADO escolhido
                    if (mode == "VAL" || mode == "PICK")
                    {
                        targetZ = fixedZ;
                    }
                    else
                    {
                        Point3dCollection ptsAll = fl.GetPoints(FeatureLinePointType.AllPoints);

                        if (ptsAll == null || ptsAll.Count == 0)
                        {
                            // fallback seguro
                            targetZ = (mode == "MIN") ? fl.MinElevation : fl.MaxElevation;
                        }
                        else
                        {
                            bool foundSidePoint = false;
                            double best = 0.0;

                            foreach (Point3d p in ptsAll)
                            {
                                Vector2d ap2 = new Vector2d(p.X - a.X, p.Y - a.Y);
                                double cross2 = (ab.X * ap2.Y) - (ab.Y * ap2.X);

                                bool onChosenSide =
                                    (side == "LEFT" && cross2 >= 0.0) ||
                                    (side == "RIGHT" && cross2 <= 0.0);

                                if (!onChosenSide) continue;

                                double z = p.Z;

                                if (!foundSidePoint)
                                {
                                    best = z;
                                    foundSidePoint = true;
                                }
                                else
                                {
                                    if (mode == "MIN" && z < best) best = z;
                                    else if (mode == "MAX" && z > best) best = z;
                                }
                            }

                            // Se por algum motivo não achou nenhum ponto no lado, fallback
                            if (!foundSidePoint)
                                targetZ = (mode == "MIN") ? fl.MinElevation : fl.MaxElevation;
                            else
                                targetZ = best;
                        }
                    }

                }

                // --- APLICAÇÃO FINAL ---
                // Agora: aplicar targetZ em TODOS os pontos da Feature Line (como você pediu)
                int n = fl.PointsCount;
                for (int i = 0; i < n; i++)
                    fl.SetPointElevation(i, targetZ);

                totalProcessados++;
            }

            tr.Commit();
        }

        ed.WriteMessage("\n✅ FUNCIONA! " + totalProcessados + " FeatureLine(s) ajustada(s)...");
    }

    private static bool TryGetClosestFeatureLinePoint(FeatureLine fl, Point3d pick, out Point3d closest)
    {
        closest = default(Point3d);

        Point3dCollection piPts = fl.GetPoints(FeatureLinePointType.PIPoint);
        Point3dCollection elPts = fl.GetPoints(FeatureLinePointType.ElevationPoint);

        bool found = false;
        double bestD2 = double.MaxValue;

        foreach (Point3d pt in piPts)
        {
            double dx = pt.X - pick.X;
            double dy = pt.Y - pick.Y;
            double d2 = dx * dx + dy * dy;

            if (d2 < bestD2)
            {
                bestD2 = d2;
                closest = pt;
                found = true;
            }
        }

        foreach (Point3d pt in elPts)
        {
            double dx = pt.X - pick.X;
            double dy = pt.Y - pick.Y;
            double d2 = dx * dx + dy * dy;

            if (d2 < bestD2)
            {
                bestD2 = d2;
                closest = pt;
                found = true;
            }
        }

        return found;
    }

    [CommandMethod(C3DCommands.FeatureLines.Descarregar)]
    public static void DescarregarExtensao()
    {
        Document doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc != null)
        {
            doc.Editor.WriteMessage("\nFLDESCARREGAR é um comando legado de aviso.");
            doc.Editor.WriteMessage("\nDLLs .NET carregadas pelo Civil 3D não são descarregadas por este comando.");
            doc.Editor.WriteMessage("\nPara garantir uma nova versão da DLL, feche/reabra o Civil 3D e faça NETLOAD novamente.");
        }
    }
}
