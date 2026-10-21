using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Lancamento em cadeia (fase H4): PV > tubo > PV. Cada clique cria a estrutura e o tubo ate ela,
/// com linha elastica a partir da ultima estrutura como previa; [Desfazer] tira o ultimo par e
/// Enter termina. O tubo nasce com o sentido montante > jusante, fundo de montante no fundo da
/// estrutura anterior e a declividade minima dos criterios (ou a digitada); a estrutura nova tem o
/// fundo no fundo do tubo e a tampa pela superficie da rede (ou fundo + profundidade padrao).
/// </summary>
public sealed class HydroLaunchCommands
{
    private const string Module = "Hydro";

    private static string N(double v, int d = 3) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    [CommandMethod(C3DCommands.Hydro.Lancar, CommandFlags.Modal)]
    public void Lancar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        try
        {
            using (PluginLog.Medir(Module, "HYLANCAR"))
                Launch(doc, ed);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, "HYLANCAR falhou", ex);
            ed.WriteMessage("\n[HYDRO] HYLANCAR falhou: " + ex.Message);
        }

        HydroPanel.NotifyChanged();
    }

    private static void Launch(Document doc, Editor ed)
    {
        AcadDb.Database db = doc.Database;
        HydroSnapshot snap = HydroReader.Read(db);
        NetworkInfo? net = snap.Find(HydroApply.ActiveNetwork ?? HydroPanel.ActiveNetworkHandle) ?? (snap.Networks.Count == 1 ? snap.Networks[0] : null);
        if (net is null && snap.Networks.Count > 1)
        {
            ed.WriteMessage("\nRedes no desenho:");
            for (int i = 0; i < snap.Networks.Count; i++)
                ed.WriteMessage($"\n  {i + 1}: {snap.Networks[i].Name}");
            PromptIntegerOptions io = new("\nNumero da rede: ") { LowerLimit = 1, UpperLimit = snap.Networks.Count };
            PromptIntegerResult ir = ed.GetInteger(io);
            if (ir.Status != PromptStatus.OK)
                return;
            net = snap.Networks[ir.Value - 1];
        }

        if (net is null)
        {
            List<string> lists = HydroEditor.PartsListNames(db);
            if (lists.Count == 0)
            {
                ed.WriteMessage("\nO desenho nao tem Pipe Network nem parts list: crie uma rede pelo Civil (ou importe uma parts list) antes.");
                return;
            }

            ed.WriteMessage("\nNenhuma Pipe Network: criando uma com a parts list " + lists[0] + ".");
            string h = HydroEditor.CreateNetwork(db, lists[0]);
            snap = HydroReader.Read(db);
            net = snap.Find(h);
            if (net is null)
                return;
        }

        List<HydroEditor.FamilyChoice> structures = HydroEditor.Families(db, net.Handle, false);
        List<HydroEditor.FamilyChoice> pipes = HydroEditor.Families(db, net.Handle, true);
        if (structures.Count == 0 || pipes.Count == 0 || structures.All(f => f.Sizes.Count == 0) || pipes.All(f => f.Sizes.Count == 0))
        {
            ed.WriteMessage("\nA parts list da rede precisa de ao menos uma familia de estrutura e uma de tubo com tamanhos.");
            return;
        }

        (AcadDb.ObjectId sf, AcadDb.ObjectId ss, string sLabel) = Choose(ed, "estrutura", structures, net.Structures.LastOrDefault()?.Family);
        if (sf.IsNull)
            return;
        (AcadDb.ObjectId pf, AcadDb.ObjectId ps, string pLabel) = Choose(ed, "tubo", pipes, net.Pipes.LastOrDefault()?.Family);
        if (pf.IsNull)
            return;

        HydroSettings st = HydroStore.Load(db);
        PromptDoubleOptions so = new("\nDeclividade dos tubos [%]") { DefaultValue = st.MinSlope * 100, UseDefaultValue = true, AllowNegative = false };
        PromptDoubleResult sr = ed.GetDouble(so);
        if (sr.Status != PromptStatus.OK)
            return;
        double slope = sr.Value / 100;
        PromptDoubleOptions dpo = new("\nProfundidade da primeira estrutura (tampa - fundo) [m]") { DefaultValue = 1.5, UseDefaultValue = true, AllowNegative = false };
        PromptDoubleResult dpr = ed.GetDouble(dpo);
        if (dpr.Status != PromptStatus.OK)
            return;
        double depth = dpr.Value;

        ed.WriteMessage($"\nRede {net.Name}: estrutura {sLabel}, tubo {pLabel}, declividade {N(slope * 100, 2)} %. Clique os PVs; [Desfazer] volta um; Enter termina.");

        // Cadeia: (estrutura, tubo que chega nela).
        List<(AcadDb.ObjectId Structure, AcadDb.ObjectId Pipe)> chain = new();
        Point3d? last = null;
        double lastInvert = 0;
        while (true)
        {
            PromptPointOptions po = new(last is null ? "\nPrimeiro PV: " : "\nProximo PV [Desfazer]: ") { AllowNone = true };
            if (last is Point3d lp)
            {
                po.UseBasePoint = true;
                po.BasePoint = lp;
                po.UseDashedLine = false;
                po.Keywords.Add("Desfazer");
            }

            PromptPointResult pr = ed.GetPoint(po);
            if (pr.Status == PromptStatus.Keyword)
            {
                if (chain.Count == 0)
                    continue;
                (AcadDb.ObjectId s, AcadDb.ObjectId p) = chain[^1];
                chain.RemoveAt(chain.Count - 1);
                Erase(db, p);
                Erase(db, s);
                if (chain.Count == 0)
                {
                    last = null;
                }
                else
                {
                    using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
                    CivilDb.Structure prev = (CivilDb.Structure)tr.GetObject(chain[^1].Structure, AcadDb.OpenMode.ForRead);
                    last = prev.Location;
                    lastInvert = prev.SumpElevation;
                }

                ed.WriteMessage("\nUltimo PV desfeito.");
                continue;
            }

            if (pr.Status != PromptStatus.OK)
                break;

            Point3d pt = new(pr.Value.X, pr.Value.Y, 0);
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDb.Network network = (CivilDb.Network)tr.GetObject(HydroReader.Id(db, net.Handle)!.Value, AcadDb.OpenMode.ForWrite);
                AcadDb.ObjectId newStructure = AcadDb.ObjectId.Null;
                network.AddStructure(sf, ss, pt, 0, ref newStructure, false);
                CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(newStructure, AcadDb.OpenMode.ForWrite);
                double surf = Surface(s);
                AcadDb.ObjectId newPipe = AcadDb.ObjectId.Null;
                if (last is Point3d from)
                {
                    double length = new Point2d(from.X, from.Y).GetDistanceTo(new Point2d(pt.X, pt.Y));
                    if (length < 0.05)
                    {
                        ed.WriteMessage("\nPonto muito perto do anterior: ignorado.");
                        s.Erase();
                        tr.Commit();
                        continue;
                    }

                    double downInvert = lastInvert - (slope * length);
                    CivilDb.Pipe? pipe = AddPipe(tr, network, pf, ps, from, pt, lastInvert, downInvert, chain[^1].Structure, newStructure, out newPipe);
                    if (pipe is null)
                    {
                        s.Erase();
                        tr.Commit();
                        ed.WriteMessage("\nNao foi possivel criar o tubo.");
                        continue;
                    }

                    s.ControlSumpBy = CivilDb.StructureControlSumpType.ByElevation;
                    s.SumpElevation = downInvert;
                    s.RimElevation = double.IsNaN(surf) ? downInvert + depth : surf;
                    lastInvert = downInvert;
                    ed.WriteMessage($"\n{s.Name}: tubo {pipe.Name} L {N(length, 2)} m, fundo {N(lastInvert)}, tampa {N(s.RimElevation)}.");
                }
                else
                {
                    double rim = double.IsNaN(surf) ? 100 : surf;
                    s.RimElevation = rim;
                    s.ControlSumpBy = CivilDb.StructureControlSumpType.ByElevation;
                    s.SumpElevation = rim - depth;
                    lastInvert = s.SumpElevation;
                    ed.WriteMessage($"\n{s.Name}: tampa {N(rim)}{(double.IsNaN(surf) ? " (sem superficie na rede: tampa 100,000)" : "")}, fundo {N(lastInvert)}.");
                }

                chain.Add((newStructure, newPipe));
                tr.Commit();
            }

            last = pt;
        }

        ed.WriteMessage($"\n{chain.Count} estrutura(s) e {chain.Count(c => !c.Pipe.IsNull)} tubo(s) lancados. Use DRENNUM para numerar e HYCALCULAR para calcular.");
    }

    private static double Surface(CivilDb.Structure s)
    {
        try
        {
            double v = s.SurfaceElevationAtInsertionPoint;
            return double.IsNaN(v) || Math.Abs(v) > 1e6 ? double.NaN : v;
        }
        catch
        {
            return double.NaN;
        }
    }

    private static CivilDb.Pipe? AddPipe(AcadDb.Transaction tr, CivilDb.Network network, AcadDb.ObjectId familyId, AcadDb.ObjectId sizeId,
        Point3d from, Point3d to, double upInvert, double downInvert, AcadDb.ObjectId fromStructure, AcadDb.ObjectId toStructure, out AcadDb.ObjectId newPipe)
    {
        newPipe = AcadDb.ObjectId.Null;
        LineSegment3d line = new(new Point3d(from.X, from.Y, upInvert), new Point3d(to.X, to.Y, downInvert));
        network.AddLinePipe(familyId, sizeId, line, ref newPipe, false);
        if (newPipe.IsNull || tr.GetObject(newPipe, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Pipe p)
            return null;
        double half = (p.InnerHeight > 0 ? p.InnerHeight : p.InnerDiameterOrWidth) / 2.0;
        p.StartPoint = new Point3d(from.X, from.Y, upInvert + half);
        p.EndPoint = new Point3d(to.X, to.Y, downInvert + half);
        try
        {
            p.ConnectToStructure(CivilDb.ConnectorPositionType.Start, fromStructure, true);
            p.ConnectToStructure(CivilDb.ConnectorPositionType.End, toStructure, true);
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso(Module, "Conexao do tubo: " + ex.Message);
        }

        p.FlowDirectionMethod = CivilDb.FlowDirectionMethodType.StartToEnd;
        return p;
    }

    private static void Erase(AcadDb.Database db, AcadDb.ObjectId id)
    {
        if (id.IsNull || id.IsErased)
            return;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(id, AcadDb.OpenMode.ForWrite, false) is AcadDb.DBObject o)
            o.Erase();
        tr.Commit();
    }

    private static (AcadDb.ObjectId, AcadDb.ObjectId, string) Choose(Editor ed, string what, List<HydroEditor.FamilyChoice> families, string? preferred)
    {
        int def = Math.Max(0, families.FindIndex(f => f.Sizes.Count > 0 && string.Equals(f.Family, preferred, StringComparison.OrdinalIgnoreCase)));
        if (families[def].Sizes.Count == 0)
            def = families.FindIndex(f => f.Sizes.Count > 0);
        ed.WriteMessage($"\nFamilias de {what}:");
        for (int i = 0; i < families.Count; i++)
            ed.WriteMessage($"\n  {i + 1}: {families[i].Family} ({families[i].Sizes.Count} tamanho(s))");
        PromptIntegerOptions fo = new($"\nFamilia de {what}") { LowerLimit = 1, UpperLimit = families.Count, DefaultValue = def + 1, UseDefaultValue = true };
        PromptIntegerResult fr = ed.GetInteger(fo);
        if (fr.Status != PromptStatus.OK)
            return (AcadDb.ObjectId.Null, AcadDb.ObjectId.Null, "");
        HydroEditor.FamilyChoice fam = families[fr.Value - 1];
        if (fam.Sizes.Count == 0)
        {
            ed.WriteMessage("\nEssa familia nao tem tamanhos.");
            return (AcadDb.ObjectId.Null, AcadDb.ObjectId.Null, "");
        }

        ed.WriteMessage($"\nTamanhos de {fam.Family}:");
        for (int i = 0; i < fam.Sizes.Count; i++)
            ed.WriteMessage($"\n  {i + 1}: {fam.Sizes[i].Size}");
        PromptIntegerOptions so = new("\nTamanho") { LowerLimit = 1, UpperLimit = fam.Sizes.Count, DefaultValue = 1, UseDefaultValue = true };
        PromptIntegerResult sr = ed.GetInteger(so);
        if (sr.Status != PromptStatus.OK)
            return (AcadDb.ObjectId.Null, AcadDb.ObjectId.Null, "");
        return (fam.FamilyId, fam.Sizes[sr.Value - 1].SizeId, fam.Family + " / " + fam.Sizes[sr.Value - 1].Size);
    }
}
