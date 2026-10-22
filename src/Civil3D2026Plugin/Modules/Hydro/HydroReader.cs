using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Le as Pipe Networks e as bacias do desenho para o modelo do HYDRO (so leitura, transacao
/// OpenClose, sem LockDocument: pode ser chamado do painel). Tudo que falhar num objeto vira
/// um aviso naquele objeto, nunca derruba a leitura.
/// </summary>
internal static class HydroReader
{
    public static HydroSnapshot Read(AcadDb.Database db)
    {
        HydroSnapshot snap = new() { Drawing = db.Filename };
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);

        Dictionary<AcadDb.ObjectId, StructureInfo> structuresById = new();
        try
        {
            snap.Templates = HydroRules.LoadTemplates(tr, db);
        }
        catch (Exception ex)
        {
            Infrastructure.PluginLog.Diag("Hydro", "Modelos de regra nao lidos: " + ex.Message);
        }

        AcadDb.ObjectIdCollection networkIds;
        try
        {
            networkIds = cdoc.GetPipeNetworkIds();
        }
        catch
        {
            networkIds = new AcadDb.ObjectIdCollection();
        }

        foreach (AcadDb.ObjectId netId in networkIds)
        {
            if (tr.GetObject(netId, AcadDb.OpenMode.ForRead, false) is not CivilDb.Network net)
                continue;

            NetworkInfo info = new()
            {
                Handle = H(netId),
                Name = net.Name,
                PartsList = Safe(() => net.PartsListName),
                ReferenceSurface = Safe(() => net.ReferenceSurfaceName),
                ReferenceAlignment = Safe(() => net.ReferenceAlignmentName)
            };

            foreach (AcadDb.ObjectId sid in Safe(net.GetStructureIds))
            {
                if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is not CivilDb.Structure s)
                    continue;
                StructureInfo si = ReadStructure(s, info.Handle);
                try
                {
                    si.OwnRules = HydroRules.Read(tr, s);
                    si.Rules = HydroRules.Effective(si.OwnRules, snap.Templates);
                }
                catch (Exception ex)
                {
                    si.Warnings.Add("Regras HYDRO nao lidas: " + ex.Message);
                }

                info.Structures.Add(si);
                info.StructureByHandle[si.Handle] = si;
                structuresById[sid] = si;
            }

            foreach (AcadDb.ObjectId pid in Safe(net.GetPipeIds))
            {
                if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is not CivilDb.Pipe p)
                    continue;
                PipeInfo pi = ReadPipe(p, info.Handle);
                info.Pipes.Add(pi);
                info.PipeByHandle[pi.Handle] = pi;
                Link(info, pi);
            }

            foreach (StructureInfo s in info.Structures)
            {
                if (s.PipeCount == 0)
                    s.Warnings.Add("Estrutura sem tubo ligado.");
                if (s.PipesUndefined.Count > 0)
                    s.Warnings.Add("Tubo ligado sem sentido de fluxo definido.");
                if (s.Depth < 0.05)
                    s.Warnings.Add("Fundo acima da tampa (ou profundidade quase nula).");
            }

            snap.Networks.Add(info);
        }

        // Bacias.
        try
        {
            CivilDb.CatchmentGroupCollection groups = cdoc.GetCatchmentGroups();
            foreach (AcadDb.ObjectId gid in groups)
            {
                if (tr.GetObject(gid, AcadDb.OpenMode.ForRead, false) is not CivilDb.CatchmentGroup g)
                    continue;
                snap.Groups.Add(g.Name);
                foreach (AcadDb.ObjectId cid in Safe(g.GetAllCatchmentIds))
                {
                    if (tr.GetObject(cid, AcadDb.OpenMode.ForRead, false) is not CivilDb.Catchment c)
                        continue;
                    CatchmentInfo ci = ReadCatchment(c, g.Name);
                    AcadDb.ObjectId discharge = CatchmentApi.GetDischarge(c);
                    if (!discharge.IsNull && structuresById.TryGetValue(discharge, out StructureInfo? target))
                    {
                        ci.StructureHandle = target.Handle;
                        ci.StructureName = target.Name;
                        target.Catchments.Add(ci.Name);
                    }

                    snap.Catchments.Add(ci);
                }
            }
        }
        catch (Exception ex)
        {
            Infrastructure.PluginLog.Diag("Hydro", "Bacias nao lidas: " + ex.Message);
        }

        return snap;
    }

    private static StructureInfo ReadStructure(CivilDb.Structure s, string networkHandle)
    {
        StructureInfo si = new() { Handle = H(s.ObjectId), NetworkHandle = networkHandle, Name = s.Name };
        Point3d loc = s.Location;
        si.Point = new P2(loc.X, loc.Y);
        si.Rotation = Safe(() => s.Rotation);
        si.Family = Safe(() => s.PartFamilyName);
        si.Size = Safe(() => s.PartSizeName);
        si.Type = Safe(() => s.StructureType.ToString());
        si.Rim = Safe(() => s.RimElevation);
        si.Sump = Safe(() => s.SumpElevation);
        si.SumpDepth = Safe(() => s.SumpDepth);
        si.SurfaceElevation = Safe(() => s.SurfaceElevationAtInsertionPoint, double.NaN);
        si.InnerDiameter = Safe(() => s.InnerDiameterOrWidth);
        si.ControlSumpBy = Safe(() => s.ControlSumpBy.ToString());
        si.RuleSet = Safe(() => s.RuleSetStyleName);
        si.RefSurface = Safe(() => s.RefSurfaceName);
        si.Hgl = Safe(() => s.HydraulicGradeLine, double.NaN);
        si.Egl = Safe(() => s.EnergyGradeLine, double.NaN);
        si.KnownFlow = Safe(() => s.KnownFlow);
        si.FamilyGuid = "";
        try
        {
            si.Parameters.AddRange(ReadParameters(s));
        }
        catch (Exception ex)
        {
            Infrastructure.PluginLog.Diag("Hydro", $"{s.Name}: parametros da peca nao lidos ({ex.Message}).");
        }

        return si;
    }

    /// <summary>
    /// Todos os parametros numericos da peca (PartData): faixa, lista, fixos da familia e os
    /// internos do desenho do modelo (Geometry_*, marcados Internal). Os de catalogo sao omitidos.
    /// </summary>
    public static List<PartParam> ReadParameters(CivilDb.Part part)
    {
        List<PartParam> list = new();
        using CivilDb.PartDataRecord rec = part.PartData;
        foreach (CivilDb.PartDataField f in rec.GetAllDataFields())
        {
            if (f.DataType != CivilDb.PartCatalogDataType.Double || f.Value is not double v)
                continue;
            string context = f.ContextString ?? "";
            if (context.StartsWith("Catalog_", StringComparison.OrdinalIgnoreCase))
                continue;
            double min = double.NaN, max = double.NaN;
            List<double> vals = new();
            bool range = f.IsFromRange;
            if (range)
            {
                try
                {
                    using CivilDb.PartDataRange r = f.ValueRange;
                    min = Convert.ToDouble(r.RangeMin, CultureInfo.InvariantCulture);
                    max = Convert.ToDouble(r.RangeMax, CultureInfo.InvariantCulture);
                }
                catch
                {
                    range = false;
                }
            }

            // So le a lista quando ha lista: ler ValueList de campo fixo dispara excecao (lento em rede grande).
            if (!range && f.IsFromList)
            {
                try
                {
                    using CivilDb.PartDataList l = f.ValueList;
                    for (int i = 0; i < l.Count; i++)
                        vals.Add(Convert.ToDouble(l[i], CultureInfo.InvariantCulture));
                    if (vals.Count > 0)
                    {
                        min = vals.Min();
                        max = vals.Max();
                    }
                }
                catch
                {
                    // Sem lista legivel.
                }
            }

            bool fromList = !range && f.IsFromList;
            list.Add(new PartParam(f.Name, f.Description, context, v, min, max, f.Units, range, f.IsReadOnly)
            {
                List = fromList ? vals.OrderBy(x => x).ToList() : new List<double>(),
                Internal = context.StartsWith("Geometry_", StringComparison.OrdinalIgnoreCase) || context.StartsWith("BodyGeometry_", StringComparison.OrdinalIgnoreCase)
            });
        }

        return list;
    }

    private static PipeInfo ReadPipe(CivilDb.Pipe p, string networkHandle)
    {
        PipeInfo pi = new() { Handle = H(p.ObjectId), NetworkHandle = networkHandle, Name = p.Name };
        Point3d a = p.StartPoint, b = p.EndPoint;
        pi.Start = new P2(a.X, a.Y);
        pi.End = new P2(b.X, b.Y);
        pi.StartZ = a.Z;
        pi.EndZ = b.Z;
        pi.StartHandle = p.StartStructureId.IsNull ? "" : H(p.StartStructureId);
        pi.EndHandle = p.EndStructureId.IsNull ? "" : H(p.EndStructureId);
        pi.FlowDirection = Safe(() => p.FlowDirection.ToString(), "Bidirectional");
        pi.Family = Safe(() => p.PartFamilyName);
        pi.Size = Safe(() => p.PartSizeName);
        pi.InnerDiameter = Safe(() => p.InnerDiameterOrWidth);
        pi.OuterDiameter = Safe(() => p.OuterDiameterOrWidth);
        pi.InnerHeight = Safe(() => p.InnerHeight);
        if (pi.InnerHeight <= 0)
            pi.InnerHeight = pi.InnerDiameter;
        pi.Length2D = Safe(() => p.Length2D);
        pi.Length3D = Safe(() => p.Length3D);
        pi.CoverStart = Safe(() => p.CoverOfStartPoint, double.NaN);
        pi.CoverEnd = Safe(() => p.CoverOfEndpoint, double.NaN);
        pi.MinimumCover = Safe(() => p.MinimumCover, double.NaN);
        pi.MaximumCover = Safe(() => p.MaximumCover, double.NaN);
        pi.Manning = Safe(() => p.HasManningCoefficient ? p.ManningCoefficient : double.NaN, double.NaN);
        pi.FlowRate = Safe(() => p.FlowRate, double.NaN);
        pi.HglUp = Safe(() => p.HydraulicGradeLineUp, double.NaN);
        pi.HglDown = Safe(() => p.HydraulicGradeLineDown, double.NaN);
        pi.EglUp = Safe(() => p.EnergyGradeLineUp, double.NaN);
        pi.EglDown = Safe(() => p.EnergyGradeLineDown, double.NaN);
        pi.RuleSet = Safe(() => p.RuleSetStyleName);
        return pi;
    }

    private static void Link(NetworkInfo net, PipeInfo p)
    {
        StructureInfo? start = net.StructureByHandle.GetValueOrDefault(p.StartHandle);
        StructureInfo? end = net.StructureByHandle.GetValueOrDefault(p.EndHandle);
        if (start is null || end is null)
            p.Warnings.Add(start is null && end is null ? "Tubo solto (sem estrutura nas duas pontas)." : "Tubo com uma ponta solta.");

        switch (p.FlowDirection)
        {
            case "StartToEnd":
                p.FromHandle = start?.Handle;
                p.ToHandle = end?.Handle;
                start?.PipesOut.Add(p.Handle);
                end?.PipesIn.Add(p.Handle);
                break;
            case "EndToStart":
                p.FromHandle = end?.Handle;
                p.ToHandle = start?.Handle;
                end?.PipesOut.Add(p.Handle);
                start?.PipesIn.Add(p.Handle);
                break;
            default:
                p.Warnings.Add("Sentido do fluxo bidirecional/indefinido.");
                start?.PipesUndefined.Add(p.Handle);
                end?.PipesUndefined.Add(p.Handle);
                break;
        }

        if (p.Defined && p.Slope < -1e-6)
            p.Warnings.Add("Contra-declive: o fundo de jusante esta acima do de montante.");
    }

    private static CatchmentInfo ReadCatchment(CivilDb.Catchment c, string group)
    {
        CatchmentInfo ci = new() { Handle = H(c.ObjectId), Name = c.Name, Group = group };
        ci.Area = Safe(() => c.Area2d);
        ci.C = Safe(() => c.RunoffCoefficient);
        ci.Tc = Safe(() => c.TimeOfConcentration);
        ci.TcMethod = Safe(() => c.TimeOfConcentrationCalculationMethod.ToString());
        ci.FlowLength = CatchmentApi.GetFlowPathLength(c);
        ci.NetworkName = Safe(() => c.ReferencePipeNetworkName);
        try
        {
            Point2dCollection pts = c.BoundaryPolyline2d;
            double sx = 0, sy = 0;
            foreach (Point2d pt in pts)
            {
                ci.Boundary.Add(new P2(pt.X, pt.Y));
                sx += pt.X;
                sy += pt.Y;
            }

            if (pts.Count > 0)
                ci.Centroid = new P2(sx / pts.Count, sy / pts.Count);
        }
        catch
        {
            // Sem contorno: fica so a area.
        }

        try
        {
            Point3d d = c.DischargePoint;
            ci.Discharge = new P2(d.X, d.Y);
        }
        catch
        {
            ci.Discharge = ci.Centroid;
        }

        return ci;
    }

    public static string H(AcadDb.ObjectId id) => id.Handle.ToString();

    /// <summary>ObjectId a partir do handle em hexadecimal (nulo se nao existe mais).</summary>
    public static AcadDb.ObjectId? Id(AcadDb.Database db, string handle)
    {
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return null;
        if (!db.TryGetObjectId(new AcadDb.Handle(value), out AcadDb.ObjectId id) || id.IsNull || id.IsErased)
            return null;
        return id;
    }

    private static T Safe<T>(Func<T> f, T fallback = default!)
    {
        try
        {
            return f();
        }
        catch
        {
            return fallback;
        }
    }

    private static string Safe(Func<string> f)
    {
        try
        {
            return f() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<AcadDb.ObjectId> Safe(Func<AcadDb.ObjectIdCollection> f)
    {
        AcadDb.ObjectIdCollection ids;
        try
        {
            ids = f();
        }
        catch
        {
            yield break;
        }

        foreach (AcadDb.ObjectId id in ids)
            yield return id;
    }
}
