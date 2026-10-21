using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Hydro.Calc;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Liga o modelo lido do desenho ao calculo puro (NetworkCalc) e grava os resultados nos campos
/// nativos (FlowRate, HGL/EGL de tubos e estruturas), no Property Set HYDRO e no NOD.
/// </summary>
internal static class HydroCalc
{
    private const string Module = "Hydro";
    private const double G = 9.80665;

    public sealed record Outcome(Dictionary<string, HydroResult> Results, List<string> Messages, int Pipes, int Structures);

    /// <summary>Monta as entradas puras a partir do modelo lido.</summary>
    public static (List<NodeInput> Nodes, List<PipeInput> Pipes, List<CatchmentInput> Catchments) Inputs(NetworkInfo net, HydroSnapshot snap, HydroSettings s)
    {
        List<NodeInput> nodes = net.Structures.Select(st => new NodeInput(st.Handle, st.KnownFlow)).ToList();
        List<PipeInput> pipes = net.Pipes.Where(p => p.Defined)
            .Select(p => new PipeInput(p.Handle, p.FromHandle!, p.ToHandle!, p.InnerDiameter, Math.Max(p.Slope, 0), p.Length2D, double.IsNaN(p.Manning) ? s.Manning : p.Manning))
            .ToList();
        List<CatchmentInput> catchments = snap.Catchments.Where(c => c.StructureHandle.Length > 0 && net.StructureByHandle.ContainsKey(c.StructureHandle))
            .Select(c => new CatchmentInput(c.Handle, c.StructureHandle, c.AreaHa, c.C, c.Tc)).ToList();
        return (nodes, pipes, catchments);
    }

    /// <summary>Calcula e grava. Deve rodar num comando (documento travado).</summary>
    public static Outcome Run(AcadDb.Database db, NetworkInfo net, HydroSnapshot snap, HydroSettings s)
    {
        (List<NodeInput> nodes, List<PipeInput> pipes, List<CatchmentInput> catchments) = Inputs(net, snap, s);
        NetworkResult r = NetworkCalc.Run(nodes, pipes, catchments, s.Idf, s.ReturnPeriod, s.Criteria, s.Manning);

        Dictionary<string, HydroResult> results = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, ReachResult x) in r.Pipes)
            results[id] = ToResult(x);
        foreach ((string id, ReachResult x) in r.Nodes)
            results[id] = ToResult(x);

        List<HydroViolation> violations = HydroChecker.Check(net, snap, s, results);
        int np = 0, ns = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            Dictionary<string, (double Hgl, double Egl)> atNode = new(StringComparer.OrdinalIgnoreCase);
            foreach (PipeInfo p in net.Pipes)
            {
                if (!results.TryGetValue(p.Handle, out HydroResult? res))
                    continue;
                AcadDb.ObjectId? id = HydroReader.Id(db, p.Handle);
                if (id is null || tr.GetObject(id.Value, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Pipe pipe)
                    continue;

                double y = res.DepthRatio * p.InnerDiameter;
                double head = res.Velocity * res.Velocity / (2 * G);
                double hglUp = p.UpInvert + y, hglDown = p.DownInvert + y;
                Try(() => pipe.FlowRate = res.Flow, "FlowRate", p.Name);
                Try(() => pipe.HydraulicGradeLineUp = hglUp, "HGL mont.", p.Name);
                Try(() => pipe.HydraulicGradeLineDown = hglDown, "HGL jus.", p.Name);
                Try(() => pipe.EnergyGradeLineUp = hglUp + head, "EGL mont.", p.Name);
                Try(() => pipe.EnergyGradeLineDown = hglDown + head, "EGL jus.", p.Name);
                Try(() => pipe.ReturnPeriod = (int)Math.Round(s.ReturnPeriod), "Tr", p.Name);

                if (p.FromHandle is string from)
                    Merge(atNode, from, hglUp, hglUp + head);
                if (p.ToHandle is string to)
                    Merge(atNode, to, hglDown, hglDown + head);

                string warnings = string.Join(" | ", violations.Where(v => v.Handle == p.Handle).Select(v => v.Rule + ": " + v.Value));
                HydroPropertySet.TryApply(tr, db, pipe, Values(res, s, warnings), out string msg);
                if (msg.Length > 0)
                    PluginLog.Aviso(Module, $"Property Set em {p.Name}: {msg}");
                np++;
            }

            foreach (StructureInfo st in net.Structures)
            {
                AcadDb.ObjectId? id = HydroReader.Id(db, st.Handle);
                if (id is null || tr.GetObject(id.Value, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Structure structure)
                    continue;
                if (atNode.TryGetValue(st.Handle, out (double Hgl, double Egl) he))
                {
                    Try(() => structure.HydraulicGradeLine = he.Hgl, "HGL", st.Name);
                    Try(() => structure.EnergyGradeLine = he.Egl, "EGL", st.Name);
                }

                if (results.TryGetValue(st.Handle, out HydroResult? res))
                {
                    string warnings = string.Join(" | ", violations.Where(v => v.Handle == st.Handle).Select(v => v.Rule + ": " + v.Value));
                    HydroPropertySet.TryApply(tr, db, structure, Values(res, s, warnings), out string msg);
                    if (msg.Length > 0)
                        PluginLog.Aviso(Module, $"Property Set em {st.Name}: {msg}");
                    ns++;
                }
            }

            HydroStore.SaveResults(tr, db, results);
            tr.Commit();
        }

        foreach (string m in r.Messages)
            PluginLog.Aviso(Module, m);
        PluginLog.Info(Module, $"Calculo da rede {net.Name}: {np} tubo(s), {ns} estrutura(s), Tr {s.ReturnPeriod:0} anos, IDF {s.Idf.Name}.");
        return new Outcome(results, r.Messages, np, ns);
    }

    private static HydroResult ToResult(ReachResult x) => new()
    {
        Flow = x.Flow, AreaHa = x.AreaHa, C = x.C, Tc = x.TcMinutes, Intensity = x.Intensity,
        DepthRatio = x.DepthRatio, Velocity = x.Velocity, TravelTime = x.TravelTimeMinutes, Flows = x.Flows, FullFlow = x.FullFlow
    };

    private static void Merge(Dictionary<string, (double Hgl, double Egl)> map, string key, double hgl, double egl)
    {
        if (map.TryGetValue(key, out (double Hgl, double Egl) cur))
            map[key] = (Math.Max(cur.Hgl, hgl), Math.Max(cur.Egl, egl));
        else
            map[key] = (hgl, egl);
    }

    private static Dictionary<string, object> Values(HydroResult r, HydroSettings s, string warnings) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Q_m3s"] = Math.Round(r.Flow, 5),
        ["Area_ha"] = Math.Round(r.AreaHa, 4),
        ["C"] = Math.Round(r.C, 3),
        ["Tc_min"] = Math.Round(r.Tc, 2),
        ["I_mmh"] = Math.Round(r.Intensity, 2),
        ["Lamina_yD"] = Math.Round(r.DepthRatio, 3),
        ["V_ms"] = Math.Round(r.Velocity, 3),
        ["Qplena_m3s"] = Math.Round(r.FullFlow, 5),
        ["Tpercurso_min"] = Math.Round(r.TravelTime, 2),
        ["Tr_anos"] = s.ReturnPeriod,
        ["IDF"] = s.Idf.Name + ": " + s.Idf.Formula(),
        ["Avisos"] = warnings,
        ["Versao"] = C3DVersions.Hydro
    };

    private static void Try(Action a, string what, string item)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, $"{item}: {what} nao gravado ({ex.Message}).");
        }
    }

    public static string Summary(Outcome o, NetworkInfo net, Dictionary<string, HydroResult> results)
    {
        PipeInfo? last = net.OrderedPipes().LastOrDefault(p => results.ContainsKey(p.Handle));
        string outlet = last is null ? "" : $"; vazao no ultimo trecho ({last.Name}): {results[last.Handle].Flow.ToString("0.0000", CultureInfo.InvariantCulture)} m3/s";
        return $"{o.Pipes} tubo(s) e {o.Structures} estrutura(s) calculados{outlet}" + (o.Messages.Count > 0 ? $"; {o.Messages.Count} aviso(s) no log" : "");
    }
}
