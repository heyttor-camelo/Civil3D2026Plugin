using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Escrita nos objetos nativos da Pipe Network. Cada metodo abre a sua transacao e e chamado
/// dentro de um comando (HYAPLICAR ou um HY*), para virar um unico UNDO. Convencoes:
/// - fundo (invert) = eixo da ponta - InnerHeight/2, como no DRENEXCEL;
/// - declividade positiva descendo no sentido do fluxo; SetSlopeHold* recebe o sinal negativo.
/// </summary>
internal static class HydroEditor
{
    private const string Module = "Hydro";

    private static T? Open<T>(AcadDb.Transaction tr, AcadDb.Database db, string handle, AcadDb.OpenMode mode) where T : AcadDb.DBObject
    {
        AcadDb.ObjectId? id = HydroReader.Id(db, handle);
        return id is null ? null : tr.GetObject(id.Value, mode, false) as T;
    }

    // ------------------------------------------------------------------
    // Estruturas
    // ------------------------------------------------------------------

    /// <summary>
    /// Nome, tampa e fundo. Ao mudar o fundo, os tubos ligados acompanham (movePipes): segurando a
    /// outra ponta (muda a declividade) ou a declividade (tubo inteiro sobe/desce).
    /// </summary>
    public static void EditStructure(AcadDb.Database db, string handle, string? name, double? rim, double? sump, HydroStore.HoldMode hold, bool movePipes = true)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Structure? s = Open<CivilDb.Structure>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (s is null)
            throw new InvalidOperationException("Estrutura nao encontrada (handle " + handle + ").");

        if (name is not null && name.Length > 0 && name != s.Name)
            s.Name = name;

        if (rim.HasValue && Math.Abs(rim.Value - s.RimElevation) > 1e-6)
        {
            // Com o ajuste automatico pela superficie ligado o Civil desfaz a tampa digitada.
            if (s.AutomaticRimSurfaceAdjustment)
            {
                s.AutomaticRimSurfaceAdjustment = false;
                PluginLog.Info(Module, $"{s.Name}: ajuste automatico da tampa pela superficie desligado para aceitar a tampa digitada.");
            }

            s.RimElevation = rim.Value;
        }

        if (sump.HasValue && Math.Abs(sump.Value - s.SumpElevation) > 1e-6)
        {
            double delta = sump.Value - s.SumpElevation;
            if (s.ControlSumpBy != CivilDb.StructureControlSumpType.ByElevation)
                s.ControlSumpBy = CivilDb.StructureControlSumpType.ByElevation;
            s.SumpElevation = sump.Value;

            if (movePipes)
            {
                for (int i = 0; i < s.ConnectedPipesCount; i++)
                {
                    AcadDb.ObjectId pid = s.get_ConnectedPipe(i);
                    if (tr.GetObject(pid, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Pipe p)
                        continue;
                    bool atStart = p.StartStructureId == s.ObjectId;
                    Point3d sp = p.StartPoint, ep = p.EndPoint;
                    if (hold == HydroStore.HoldMode.Declividade)
                    {
                        p.StartPoint = new Point3d(sp.X, sp.Y, sp.Z + delta);
                        p.EndPoint = new Point3d(ep.X, ep.Y, ep.Z + delta);
                    }
                    else if (atStart)
                    {
                        p.StartPoint = new Point3d(sp.X, sp.Y, sp.Z + delta);
                    }
                    else
                    {
                        p.EndPoint = new Point3d(ep.X, ep.Y, ep.Z + delta);
                    }
                }
            }
        }

        tr.Commit();
    }

    /// <summary>Move a estrutura em planta; os tubos ligados acompanham (o Civil mantem a conexao).</summary>
    public static void MoveStructure(AcadDb.Database db, string handle, Point3d to, bool keepSlopes)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Structure? s = Open<CivilDb.Structure>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (s is null)
            throw new InvalidOperationException("Estrutura nao encontrada.");

        // Guarda declividade e fundo de cada tubo ligado para repor depois do movimento.
        List<(AcadDb.ObjectId Id, bool AtStart, double Slope, double OtherZ)> pipes = new();
        for (int i = 0; i < s.ConnectedPipesCount; i++)
        {
            AcadDb.ObjectId pid = s.get_ConnectedPipe(i);
            if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is CivilDb.Pipe p)
            {
                bool atStart = p.StartStructureId == s.ObjectId;
                pipes.Add((pid, atStart, p.Slope, atStart ? p.EndPoint.Z : p.StartPoint.Z));
            }
        }

        s.Location = new Point3d(to.X, to.Y, s.Location.Z);

        foreach ((AcadDb.ObjectId id, bool atStart, double slope, double otherZ) in pipes)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Pipe p)
                continue;
            Point3d sp = p.StartPoint, ep = p.EndPoint;
            Point3d here = new(to.X, to.Y, atStart ? sp.Z : ep.Z);
            if (atStart)
                p.StartPoint = here;
            else
                p.EndPoint = here;
            if (keepSlopes)
            {
                // Mantem a declividade geometrica a partir da outra ponta.
                if (atStart)
                    p.SetSlopeHoldEnd(slope);
                else
                    p.SetSlopeHoldStart(slope);
            }
        }

        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Tubos
    // ------------------------------------------------------------------

    /// <summary>
    /// Nome, fundo de montante, fundo de jusante e declividade (segurando montante). Com fundo de
    /// montante e declividade, o jusante sai da declividade; com os dois fundos, a declividade sai deles.
    /// </summary>
    public static void EditPipe(AcadDb.Database db, string handle, string? name, double? upInvert, double? downInvert, double? slope)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado (handle " + handle + ").");

        if (name is not null && name.Length > 0 && name != p.Name)
            p.Name = name;

        bool startUp = p.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
        double half = (p.InnerHeight > 0 ? p.InnerHeight : p.InnerDiameterOrWidth) / 2.0;

        if (upInvert.HasValue)
            SetEndZ(p, startUp, upInvert.Value + half);
        if (downInvert.HasValue)
            SetEndZ(p, !startUp, downInvert.Value + half);
        if (slope.HasValue)
        {
            double s = Math.Abs(slope.Value);
            if (startUp)
                p.SetSlopeHoldStart(-s);
            else
                p.SetSlopeHoldEnd(-s);
        }

        tr.Commit();
    }

    private static void SetEndZ(CivilDb.Pipe p, bool start, double z)
    {
        if (start)
        {
            Point3d sp = p.StartPoint;
            p.StartPoint = new Point3d(sp.X, sp.Y, z);
        }
        else
        {
            Point3d ep = p.EndPoint;
            p.EndPoint = new Point3d(ep.X, ep.Y, z);
        }
    }

    /// <summary>Inverte o sentido do fluxo (bidirecional vira Start > End).</summary>
    public static void Reverse(AcadDb.Database db, IEnumerable<string> handles)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (p is null)
                continue;
            p.FlowDirectionMethod = p.FlowDirection == CivilDb.FlowDirectionType.StartToEnd
                ? CivilDb.FlowDirectionMethodType.EndToStart
                : CivilDb.FlowDirectionMethodType.StartToEnd;
        }

        tr.Commit();
    }

    /// <summary>Aplica o rule set do Civil (Part.ApplyRules) em tubos e estruturas.</summary>
    public static int ApplyRules(AcadDb.Database db, IEnumerable<string> handles)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            CivilDb.Part? part = Open<CivilDb.Part>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (part is null)
                continue;
            try
            {
                part.ApplyRules();
                n++;
            }
            catch (Exception ex)
            {
                PluginLog.Aviso(Module, $"ApplyRules em {part.Name}: {ex.Message}");
            }
        }

        tr.Commit();
        return n;
    }

    /// <summary>Troca o tamanho do tubo pelo diametro interno (tamanho mais proximo da familia).</summary>
    public static void ResizePipe(AcadDb.Database db, string handle, double innerDiameter)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado.");
        double before = p.InnerDiameterOrWidth;
        double zUp = p.StartPoint.Z - (p.InnerHeight / 2.0), zDown = p.EndPoint.Z - (p.InnerHeight / 2.0);
        p.ResizeByInnerDiameterOrWidth(innerDiameter, true);
        // Mantem os fundos (o Civil pode segurar o eixo ao trocar o tamanho).
        double half = p.InnerHeight / 2.0;
        p.StartPoint = new Point3d(p.StartPoint.X, p.StartPoint.Y, zUp + half);
        p.EndPoint = new Point3d(p.EndPoint.X, p.EndPoint.Y, zDown + half);
        PluginLog.Info(Module, $"{p.Name}: diametro {before * 1000:0} -> {p.InnerDiameterOrWidth * 1000:0} mm");
        tr.Commit();
    }

    /// <summary>Troca familia e tamanho mantendo as conexoes (Part.SwapPartFamilyAndSize).</summary>
    public static int Swap(AcadDb.Database db, IEnumerable<string> handles, AcadDb.ObjectId familyId, AcadDb.ObjectId sizeId)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            CivilDb.Part? part = Open<CivilDb.Part>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (part is null)
                continue;
            bool isPipe = part is CivilDb.Pipe;
            double zUp = 0, zDown = 0;
            if (part is CivilDb.Pipe pp)
            {
                zUp = pp.StartPoint.Z - (pp.InnerHeight / 2.0);
                zDown = pp.EndPoint.Z - (pp.InnerHeight / 2.0);
            }

            part.SwapPartFamilyAndSize(familyId, sizeId);
            if (isPipe && part is CivilDb.Pipe p2)
            {
                double half = p2.InnerHeight / 2.0;
                p2.StartPoint = new Point3d(p2.StartPoint.X, p2.StartPoint.Y, zUp + half);
                p2.EndPoint = new Point3d(p2.EndPoint.X, p2.EndPoint.Y, zDown + half);
            }

            n++;
        }

        tr.Commit();
        return n;
    }

    /// <summary>Liga a ponta (Start/End) do tubo a estrutura; estrutura vazia desliga.</summary>
    public static void Connect(AcadDb.Database db, string pipeHandle, bool start, string? structureHandle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, pipeHandle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado.");
        CivilDb.ConnectorPositionType pos = start ? CivilDb.ConnectorPositionType.Start : CivilDb.ConnectorPositionType.End;
        if (string.IsNullOrEmpty(structureHandle))
        {
            p.Disconnect(pos);
        }
        else
        {
            AcadDb.ObjectId? sid = HydroReader.Id(db, structureHandle);
            if (sid is null)
                throw new InvalidOperationException("Estrutura nao encontrada.");
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(sid.Value, AcadDb.OpenMode.ForWrite, false);
            p.ConnectToStructure(pos, s.ObjectId, true);
        }

        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Bacias
    // ------------------------------------------------------------------

    public static void EditCatchment(AcadDb.Database db, string handle, double? c, double? tc, string? structureHandle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Catchment? cat = Open<CivilDb.Catchment>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (cat is null)
            throw new InvalidOperationException("Bacia nao encontrada.");
        if (c.HasValue)
            cat.RunoffCoefficient = c.Value;
        if (tc.HasValue)
        {
            cat.TimeOfConcentrationCalculationMethod = CivilDb.TimeOfConcentrationCalculationMethod.CalculationMethodUserDefined;
            cat.TimeOfConcentration = tc.Value;
        }

        if (structureHandle is not null)
        {
            if (structureHandle.Length == 0)
            {
                cat.ReferenceDischargeObjectId = AcadDb.ObjectId.Null;
            }
            else
            {
                AcadDb.ObjectId? sid = HydroReader.Id(db, structureHandle);
                if (sid is null)
                    throw new InvalidOperationException("Estrutura nao encontrada.");
                cat.ReferenceDischargeObjectId = sid.Value;
            }
        }

        tr.Commit();
    }

    /// <summary>Cria um catchment a partir de uma polilinha fechada (grupo criado se nao existir).</summary>
    public static string CreateCatchment(AcadDb.Database db, AcadDb.ObjectId polylineId, string groupName, string name, double c, double tc, string? structureHandle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(polylineId, AcadDb.OpenMode.ForRead, false) is not AcadDb.Curve curve)
            throw new InvalidOperationException("Selecione uma polilinha fechada.");
        Point3dCollection pts = new();
        if (curve is AcadDb.Polyline pl)
        {
            for (int i = 0; i < pl.NumberOfVertices; i++)
                pts.Add(pl.GetPoint3dAt(i));
        }
        else
        {
            double len = curve.GetDistanceAtParameter(curve.EndParam);
            int n = Math.Max(12, (int)(len / 2));
            for (int i = 0; i < n; i++)
                pts.Add(curve.GetPointAtDist(len * i / n));
        }

        if (pts.Count < 3)
            throw new InvalidOperationException("A polilinha precisa de ao menos 3 vertices.");

        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
        CivilDb.CatchmentGroupCollection groups = cdoc.GetCatchmentGroups();
        AcadDb.ObjectId groupId = AcadDb.ObjectId.Null;
        foreach (AcadDb.ObjectId gid in groups)
        {
            if (tr.GetObject(gid, AcadDb.OpenMode.ForRead, false) is CivilDb.CatchmentGroup g && string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase))
            {
                groupId = gid;
                break;
            }
        }

        if (groupId.IsNull)
            groupId = CivilDb.CatchmentGroup.Create(db, groupName);

        AcadDb.ObjectId id = CivilDb.Catchment.Create(name, AcadDb.ObjectId.Null, groupId, AcadDb.ObjectId.Null, pts);
        CivilDb.Catchment cat = (CivilDb.Catchment)tr.GetObject(id, AcadDb.OpenMode.ForWrite, false);
        cat.RunoffCoefficient = c;
        cat.TimeOfConcentrationCalculationMethod = CivilDb.TimeOfConcentrationCalculationMethod.CalculationMethodUserDefined;
        cat.TimeOfConcentration = tc;
        if (!string.IsNullOrEmpty(structureHandle) && HydroReader.Id(db, structureHandle) is AcadDb.ObjectId sid)
            cat.ReferenceDischargeObjectId = sid;
        string handle = cat.Handle.ToString();
        tr.Commit();
        return handle;
    }

    // ------------------------------------------------------------------
    // Redes, parts lists e rotulos
    // ------------------------------------------------------------------

    public static List<string> PartsListNames(AcadDb.Database? db)
    {
        List<string> names = new();
        if (db is null)
            return names;
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
            foreach (AcadDb.ObjectId id in cdoc.Styles.PartsListSet)
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartsList pl)
                    names.Add(pl.Name);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Parts lists nao lidas: " + ex.Message);
        }

        return names;
    }

    /// <summary>Familias (e tamanhos) de tubos ou de estruturas da parts list da rede.</summary>
    public sealed record FamilyChoice(string Family, AcadDb.ObjectId FamilyId, List<(string Size, AcadDb.ObjectId SizeId)> Sizes);

    public static List<FamilyChoice> Families(AcadDb.Database db, string networkHandle, bool pipes)
    {
        List<FamilyChoice> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        CivilDb.Network? net = Open<CivilDb.Network>(tr, db, networkHandle, AcadDb.OpenMode.ForRead);
        if (net is null || net.PartsListId.IsNull)
            return list;
        CivilDb.Styles.PartsList pl = (CivilDb.Styles.PartsList)tr.GetObject(net.PartsListId, AcadDb.OpenMode.ForRead, false);
        CivilDb.DomainType domain = pipes ? CivilDb.DomainType.Pipe : CivilDb.DomainType.Structure;
        foreach (AcadDb.ObjectId fid in pl.GetPartFamilyIdsByDomain(domain))
        {
            if (tr.GetObject(fid, AcadDb.OpenMode.ForRead, false) is not CivilDb.Styles.PartFamily fam)
                continue;
            List<(string, AcadDb.ObjectId)> sizes = new();
            for (int i = 0; i < fam.PartSizeCount; i++)
            {
                AcadDb.ObjectId sid = fam[i];
                string label = "tamanho " + (i + 1);
                try
                {
                    if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartSize size)
                        label = SizeLabel(size, pipes);
                }
                catch
                {
                    // Fica o rotulo generico.
                }

                sizes.Add((label, sid));
            }

            list.Add(new FamilyChoice(fam.Description, fid, sizes));
        }

        return list.OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string SizeLabel(CivilDb.Styles.PartSize size, bool pipes)
    {
        CivilDb.PartDataRecord rec = size.SizeDataRecord;
        CivilDb.PartContextType[] contexts = pipes
            ? new[] { CivilDb.PartContextType.PipeInnerDiameter, CivilDb.PartContextType.PipeInnerWidth, CivilDb.PartContextType.PipeInnerHeight }
            : new[] { CivilDb.PartContextType.StructInnerDiameter, CivilDb.PartContextType.StructInnerLength, CivilDb.PartContextType.StructInnerWidth, CivilDb.PartContextType.StructRimToSumpHeight };
        List<string> parts = new();
        foreach (CivilDb.PartContextType ctx in contexts)
        {
            try
            {
                CivilDb.PartDataField f = rec.GetDataFieldBy(ctx);
                if (f.Value is double d)
                    parts.Add($"{f.Description.Split(' ')[0]} {d:0.###} {f.Units}");
                else if (f.Value is not null)
                    parts.Add(f.Value.ToString() ?? "");
            }
            catch
            {
                // Contexto nao existe neste tamanho.
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "tamanho";
    }

    public static string CreateNetwork(AcadDb.Database db, string partsListName)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
        string name = "Rede HYDRO";
        AcadDb.ObjectId id = CivilDb.Network.Create(cdoc, ref name);
        CivilDb.Network net = (CivilDb.Network)tr.GetObject(id, AcadDb.OpenMode.ForWrite, false);
        foreach (AcadDb.ObjectId pid in cdoc.Styles.PartsListSet)
        {
            if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartsList pl && string.Equals(pl.Name, partsListName, StringComparison.OrdinalIgnoreCase))
            {
                net.PartsListId = pid;
                break;
            }
        }

        string handle = net.Handle.ToString();
        tr.Commit();
        PluginLog.Info(Module, $"Rede {name} criada com parts list {partsListName}.");
        return handle;
    }

    /// <summary>Rotulos de planta nativos (estilo padrao da rede) nos tubos e/ou estruturas.</summary>
    public static int AddLabels(AcadDb.Database db, string? networkHandle, bool pipes, bool structures)
    {
        if (networkHandle is null)
            return 0;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Network? net = Open<CivilDb.Network>(tr, db, networkHandle, AcadDb.OpenMode.ForRead);
        if (net is null)
            return 0;
        if (pipes)
        {
            AcadDb.ObjectId style = net.PipePlanLabelStyleId;
            foreach (AcadDb.ObjectId pid in net.GetPipeIds())
            {
                try
                {
                    if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is CivilDb.Pipe p && p.GetPipeLabelIds().Count == 0)
                    {
                        if (style.IsNull)
                            CivilDb.PipeLabel.Create(pid, 0.5);
                        else
                            CivilDb.PipeLabel.Create(pid, 0.5, style);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Aviso(Module, "Rotulo de tubo: " + ex.Message);
                }
            }
        }

        if (structures)
        {
            foreach (AcadDb.ObjectId sid in net.GetStructureIds())
            {
                try
                {
                    if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Structure s && s.GetAvailableStructureLabelIds().Count == 0)
                    {
                        CivilDb.StructureLabel.Create(sid);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Aviso(Module, "Rotulo de estrutura: " + ex.Message);
                }
            }
        }

        tr.Commit();
        return n;
    }
}
