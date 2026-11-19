using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AecPd = Autodesk.Aec.PropertyData.DatabaseServices;
using CivilCorridorType = Autodesk.Civil.DatabaseServices.Corridor;

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// Corrige peças do corredor que a exportação IFC da Autodesk grava incompletas.
/// A extensão monta a malha de cada peça faixa a faixa entre seções e descarta as faixas em que seções vizinhas
/// se cruzam (curvas de meio-fio fechadas, bico de ilha: pavimento mais largo que o raio). O sólido do corredor
/// (Extract Corridor Solids) não tem esse defeito. Aqui: os sólidos são gerados num banco temporário (o desenho
/// não muda), casados com as peças do .ifc pelas mesmas propriedades (corredor, baseline, região, subassembly,
/// código, lado, estaca) e, onde a peça do .ifc cobre em planta menos que o sólido, a malha dela é trocada pela
/// do sólido. GUID, nome, classe, estilos e propriedades da peça ficam.
/// Diagnóstico em 19/11/2026 (KM_01, SARJETA 12 a 15): 81,5 m² de pavimento faltando no .ifc.
/// </summary>
internal static class IfcSolidFix
{
    internal sealed class Result
    {
        public int Solids;
        public int Products;
        public int Matched;
        public int Replaced;
        public double AreaRecovered;
        public int RebaixoElements;
        public int SplitFailed;
        public HashSet<string> RebaixoCodes = new();
        public long Milliseconds;
        public List<string> Details = new();
        public string Error = "";
    }

    /// <summary>Rebaixo do MFREBAIXO: trecho da subassembly de meio-fio/sarjeta que vira elemento próprio no .ifc.</summary>
    internal sealed class RebaixoCut
    {
        public string Corridor = "";
        public string Baseline = "";
        public string Subassembly = "";
        public string Code = "";
        public string Label = "";
        public string Side = "";
        public double S0, S1, S2, S3;
        public double? FinalHeight, EntrySlope, ExitSlope;
    }

    internal sealed class Options
    {
        /// <summary>Troca a malha das peças cortadas pela exportação (ver resumo da classe).</summary>
        public bool FixCut = true;

        /// <summary>Rebaixos a separar (vazio = nenhum).</summary>
        public List<RebaixoCut> Rebaixos = new();
    }

    /// <summary>Diferença mínima em planta para trocar a malha (m²), e a relativa (fração da área do sólido).</summary>
    private const double MinArea = 0.5, MinRatio = 0.02, MinAreaRatio = 0.05;

    private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    /// <summary>Classes de peça de corredor que podem ter a malha trocada.</summary>
    private static readonly HashSet<string> ShapeClasses = new(StringComparer.Ordinal)
    {
        "IFCPAVEMENT", "IFCCOURSE", "IFCKERB", "IFCBUILTELEMENT", "IFCSLAB", "IFCPLATE", "IFCEARTHWORKSFILL", "IFCEARTHWORKSCUT", "IFCFOOTING"
    };

    /// <summary>Propriedades que identificam a peça (iguais nos sólidos extraídos e no .ifc).</summary>
    private static readonly string[] KeyProps = { "corridorname", "baselinename", "regionname", "subassemblyhandle", "codename", "side" };

    /// <summary>Lê o .ifc, corrige e grava de volta. Sem o que mudar, o arquivo não é regravado.</summary>
    public static Result ApplyToFile(string ifcPath, Database db, IEnumerable<ObjectId> corridorIds, Options options, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var r = new Result();
        try
        {
            string text = File.ReadAllText(ifcPath, Encoding.ASCII);
            string? fixedText = Apply(text, db, corridorIds, options, r, log);
            if (fixedText is not null)
                File.WriteAllText(ifcPath, fixedText, Encoding.ASCII);
        }
        catch (System.Exception ex)
        {
            r.Error = ex.Message;
            log?.Invoke("Pecas do corredor: " + ex);
        }
        r.Milliseconds = sw.ElapsedMilliseconds;
        return r;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Devolve o texto alterado, ou null se nada mudou.</summary>
    public static string? Apply(string text, Database db, IEnumerable<ObjectId> corridorIds, Options options, Result r, Action<string>? log)
    {
        var step = new Step(text);

        // 1) Peças de corredor no .ifc, com as propriedades.
        var props = step.PropertiesByObject();
        var products = new Dictionary<string, List<Product>>();
        foreach (int id in step.IdsOfTypes(ShapeClasses))
        {
            List<object?> a = step.Args(id);
            if (a.Count < 7 || Step.Str(a[4]) != "CorridorShape" || a[6] is not Step.Ref pds)
                continue;
            if (!props.TryGetValue(id, out var pv))
                continue;
            var p = new Product { Id = id, Name = Step.Str(a[2]), Key = Key(pv), Placement = a[5] as Step.Ref, Shape = pds.Id };
            r.Products++;
            if (!products.TryGetValue(p.Key, out var l))
                products[p.Key] = l = new List<Product>();
            l.Add(p);
        }
        if (products.Count == 0)
        {
            log?.Invoke("Pecas do corredor: nenhuma peca de corredor com propriedades no .ifc.");
            return null;
        }
        MapConversion map = step.Map();

        // 2) Sólidos do corredor num banco temporário (o desenho não é tocado). Os da subassembly de um rebaixo
        //    são cortados nas estacas de início e fim dele; cada pedaço sabe a que rebaixo pertence (ou nenhum).
        var solids = new Dictionary<string, List<Mesh>>();
        foreach (ObjectId cid in corridorIds.Distinct())
        {
            using var side = new Database(true, true);
            using Transaction tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(cid, OpenMode.ForRead) is not CivilCorridorType c)
                continue;
            var prm = new Autodesk.Civil.DatabaseServices.ExportCorridorSolidsParams
            {
                ExportShapes = true,
                ExportLinks = false,
                CreateSolidForShape = true,
                SweepSolidForShape = false
            };
            ObjectIdCollection ids = c.ExportSolids(prm, side);
            var cuts = options.Rebaixos.Where(x => Same(x.Corridor, c.Name)).ToList();
            var paths = new Dictionary<string, BaselinePath?>(StringComparer.OrdinalIgnoreCase);
            BaselinePath? PathOf(string baseline)
            {
                if (paths.TryGetValue(baseline, out var bp))
                    return bp;
                bp = null;
                foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                {
                    if (Same(b.Name ?? "", baseline))
                    {
                        bp = BaselinePath.Of(b);
                        break;
                    }
                }
                if (bp is null)
                    log?.Invoke($"Pecas do corredor: baseline '{baseline}' sem caminho; baselines do corredor: {string.Join(" | ", c.Baselines.Cast<Autodesk.Civil.DatabaseServices.Baseline>().Select(b => b.Name))}");
                paths[baseline] = bp;
                return bp;
            }
            using (Transaction st = side.TransactionManager.StartTransaction())
            {
                bool first = r.Solids == 0;
                foreach (ObjectId sid in ids)
                {
                    if (sid.IsNull || sid.IsErased || st.GetObject(sid, OpenMode.ForWrite) is not Solid3d s)
                        continue;
                    r.Solids++;
                    var sp = SolidProperties(s, st);
                    if (first)
                    {
                        log?.Invoke("Pecas do corredor: propriedades do primeiro solido: " + string.Join("; ", sp.Select(kv => kv.Key + "=" + kv.Value)));
                        first = false;
                    }
                    string k = Key(sp);
                    if (!solids.TryGetValue(k, out var l))
                        solids[k] = l = new List<Mesh>();
                    string bl = sp.TryGetValue("baselinename", out string? x1) ? x1 : "";
                    string sa = sp.TryGetValue("subassemblyname", out string? x2) ? x2 : "";
                    var mine = cuts.Where(x => Same(x.Baseline, bl) && Same(x.Subassembly, sa)).ToList();
                    BaselinePath? al = mine.Count > 0 ? PathOf(bl) : null;
                    // Malha do sólido inteiro (antes de cortar: o corte altera o sólido).
                    Mesh? whole = Facet(s);
                    if (whole is null)
                        continue;
                    whole.Source = r.Solids;
                    if (al is null)
                    {
                        l.Add(whole);
                        continue;
                    }
                    var sliced = Slice(s, mine, al);
                    var pieces = new List<Mesh>();
                    bool ok = true;
                    foreach ((Solid3d piece, int reb) in sliced)
                    {
                        Mesh? m = Facet(piece);
                        if (!ReferenceEquals(piece, s))
                            piece.Dispose();
                        if (m is null)
                        {
                            ok = false;
                            continue;
                        }
                        m.Rebaixo = reb >= 0 ? mine[reb] : null;
                        m.Source = r.Solids;
                        pieces.Add(m);
                    }
                    if (pieces.All(x => x.Rebaixo is null))
                    {
                        l.Add(whole);
                        continue;
                    }
                    // Conferência do corte: os pedaços ficam dentro do sólido e somam a mesma área em planta.
                    double[] wb = Box(whole.Points);
                    double aw = PlanArea(whole.Points, whole.Tris), ap = pieces.Sum(x => PlanArea(x.Points, x.Tris));
                    ok = ok && pieces.All(x =>
                    {
                        double[] pb = Box(x.Points);
                        return pb[0] >= wb[0] - 0.05 && pb[1] >= wb[1] - 0.05 && pb[2] <= wb[2] + 0.05 && pb[3] <= wb[3] + 0.05;
                    }) && Math.Abs(ap - aw) <= Math.Max(0.05, 0.01 * aw);
                    if (!ok)
                    {
                        r.SplitFailed++;
                        log?.Invoke($"Pecas do corredor: corte invalido em {bl} / {sa} / {(sp.TryGetValue("codename", out string? cn2) ? cn2 : "")} ({aw:F2} m2 inteiro, {ap:F2} m2 nos pedacos); solido mantido inteiro");
                        l.Add(whole);
                        continue;
                    }
                    l.AddRange(pieces);
                }                st.Commit();
            }
            tr.Commit();
        }

        // 3) Comparação em planta, troca da malha e separação dos rebaixos.
        var edits = new Step.Editor(step);
        foreach (var kv in products)
        {
            if (!solids.TryGetValue(kv.Key, out var sl))
                continue;
            // Peças com a mesma identificação (ex.: mesma região em trechos): cada sólido vai para a peça que mais
            // se sobrepõe a ele em planta.
            var parts = kv.Value.Select(p =>
            {
                double[] place = step.Placement(p.Placement);
                var current = step.BodyMeshes(p.Shape, place, map);
                return (P: p, Place: place, Current: current, Box: Box(current.SelectMany(c => c.World)));
            }).Where(x => x.Current.Count > 0).ToList();
            var assigned = parts.Select(_ => new List<Mesh>()).ToList();
            // Os pedaços de um mesmo sólido (cortes dos rebaixos) vão juntos para a mesma peça.
            foreach (var grp in sl.GroupBy(m => m.Source))
            {
                double[] mb = Box(grp.SelectMany(m => m.Points));
                int best = -1;
                double bestOv = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    double ov = Overlap(mb, parts[i].Box);
                    if (ov > bestOv)
                    {
                        bestOv = ov;
                        best = i;
                    }
                }
                if (best < 0 && parts.Count == 1)
                    best = 0;
                if (best >= 0)
                    assigned[best].AddRange(grp);
            }
            for (int i = 0; i < parts.Count; i++)
            {
                if (assigned[i].Count == 0)
                    continue;
                r.Matched++;
                Product p = parts[i].P;
                double[] place = parts[i].Place;
                double[] inv = Step.Inverse(place);
                var current = parts[i].Current;
                List<int> tfs = current.Select(c => c.Tfs).ToList();
                List<double[]> Local(Mesh m) => m.Points.Select(w => Step.Xf(inv, map.ToIfc(w))).ToList();

                var groups = assigned[i].Where(m => m.Rebaixo is not null).GroupBy(m => m.Rebaixo!).ToList();
                if (groups.Count > 0)
                {
                    // Rebaixos: a peça original fica com o que está fora deles; cada rebaixo vira um elemento novo.
                    var outside = assigned[i].Where(m => m.Rebaixo is null).ToList();
                    int g0 = 0;
                    if (outside.Count > 0)
                    {
                        Mesh o = Mesh.Merge(outside);
                        edits.ReplaceBody(p.Shape, tfs, Local(o), o.Tris, o.Closed);
                    }
                    else
                    {
                        // A peça inteira está dentro de um rebaixo: ela mesma passa a ser o rebaixo.
                        Mesh o = Mesh.Merge(groups[0].ToList());
                        edits.ReplaceBody(p.Shape, tfs, Local(o), o.Tris, o.Closed);
                        edits.MarkRebaixo(p.Id, $"{p.Name} - Rebaixo {groups[0].Key.Code}", Pset(groups[0].Key));
                        r.RebaixoElements++;
                        r.RebaixoCodes.Add(groups[0].Key.Code);
                        g0 = 1;
                    }
                    for (int g = g0; g < groups.Count; g++)
                    {
                        Mesh o = Mesh.Merge(groups[g].ToList());
                        int nid = edits.AddCopy(p.Id, p.Shape, tfs, $"{p.Name} - Rebaixo {groups[g].Key.Code}", Local(o), o.Tris, o.Closed, Pset(groups[g].Key));
                        r.RebaixoElements++;
                        r.RebaixoCodes.Add(groups[g].Key.Code);
                        r.Details.Add($"#{nid} {p.Name} - Rebaixo {groups[g].Key.Code} (de #{p.Id})");
                    }
                    continue;
                }
                if (!options.FixCut)
                    continue;
                double aIfc = current.Sum(c => PlanArea(c.World, c.Tris));
                var merged = Mesh.Merge(assigned[i]);
                double aSolid = PlanArea(merged.Points, merged.Tris);
                double diff = aSolid - aIfc;
                if (!(diff >= MinArea || (diff >= MinAreaRatio && diff >= MinRatio * aSolid)))
                    continue;
                // Pontos do sólido (desenho) -> coordenadas locais da peça no .ifc.
                edits.ReplaceBody(p.Shape, tfs, Local(merged), merged.Tris, merged.Closed);
                r.Replaced++;
                r.AreaRecovered += diff;
                r.Details.Add($"#{p.Id} {p.Name} [{kv.Key}]: {aIfc:F2} -> {aSolid:F2} m2");
            }
        }
        foreach (string d in r.Details)
            log?.Invoke("Pecas do corredor: " + d);
        return edits.Changed ? edits.Result() : null;
    }

    /// <summary>Propriedades do rebaixo para o property set C3D_REBAIXO.</summary>
    private static List<(string Name, string Value)> Pset(RebaixoCut c)
    {
        static string L(double v) => "IFCLENGTHMEASURE(" + Step.F(v) + ")";
        var l = new List<(string, string)>
        {
            ("Codigo", "IFCLABEL('" + c.Code + "')"),
            ("Rebaixo", "IFCLABEL('" + IfcReport.Encode(c.Label) + "')"),
            ("Lado", "IFCLABEL('" + c.Side + "')"),
            ("Baseline", "IFCLABEL('" + IfcReport.Encode(c.Baseline) + "')"),
            ("EstacaInicial", L(c.S0)),
            ("InicioRebaixado", L(c.S1)),
            ("FimRebaixado", L(c.S2)),
            ("EstacaFinal", L(c.S3)),
            ("ComprimentoTotal", L(c.S3 - c.S0))
        };
        if (c.FinalHeight is double h)
            l.Add(("AlturaFinal", L(h)));
        if (c.EntrySlope is double e)
            l.Add(("RampaEntrada", "IFCRATIOMEASURE(" + Step.F(e) + ")"));
        if (c.ExitSlope is double x)
            l.Add(("RampaSaida", "IFCRATIOMEASURE(" + Step.F(x) + ")"));
        return l;
    }

    /// <summary>
    /// Corta o sólido por planos verticais perpendiculares ao alinhamento nas estacas de início e fim de cada rebaixo
    /// e diz, para cada pedaço, a que rebaixo ele pertence (estaca do centro do pedaço; -1 = fora).
    /// </summary>
    private static List<(Solid3d Piece, int Rebaixo)> Slice(Solid3d s, List<RebaixoCut> cuts, BaselinePath al)
    {
        var pieces = new List<Solid3d> { s };
        foreach (double st in cuts.SelectMany(x => new[] { x.S0, x.S3 }).Distinct())
        {
            Plane? plane = PlaneAt(al, st);
            if (plane is null)
                continue;
            var next = new List<Solid3d>();
            foreach (Solid3d p in pieces)
            {
                next.Add(p);
                try
                {
                    Solid3d other = p.Slice(plane, true);
                    if (other is not null && !other.IsNull)
                        next.Add(other);
                }
                catch
                {
                    // O plano não corta este pedaço.
                }
            }
            pieces = next;
        }
        var result = new List<(Solid3d, int)>();
        foreach (Solid3d p in pieces)
        {
            int reb = -1;
            try
            {
                Point3d c = p.MassProperties.Centroid;
                double station = al.Station(c.X, c.Y);
                for (int i = 0; i < cuts.Count; i++)
                {
                    if (station > cuts[i].S0 + 0.001 && station < cuts[i].S3 - 0.001)
                    {
                        reb = i;
                        break;
                    }
                }
            }
            catch
            {
                // Sem centro: fica fora dos rebaixos.
            }
            result.Add((p, reb));
        }
        return result;
    }

    private static Plane? PlaneAt(BaselinePath bp, double station)
    {
        (Point3d? p, Vector3d? t) = bp.At(station);
        return p is Point3d pt && t is Vector3d d ? new Plane(new Point3d(pt.X, pt.Y, 0), d) : null;
    }

    /// <summary>
    /// Baseline do corredor (alinhamento ou feature line) amostrada a cada 0,25 m: ponto e direção numa estaca e a
    /// estaca de um ponto (projeção no trecho mais próximo).
    /// </summary>
    internal sealed class BaselinePath
    {
        private readonly Autodesk.Civil.DatabaseServices.Baseline _b;
        private readonly List<(double S, double X, double Y)> _p = new();

        public double Start, End;

        private BaselinePath(Autodesk.Civil.DatabaseServices.Baseline b) => _b = b;

        public static BaselinePath? Of(Autodesk.Civil.DatabaseServices.Baseline b)
        {
            try
            {
                var bp = new BaselinePath(b) { Start = b.StartStation, End = b.EndStation };
                int n = Math.Max(2, (int)Math.Ceiling((bp.End - bp.Start) / 0.25) + 1);
                for (int i = 0; i < n; i++)
                {
                    double s = i == n - 1 ? bp.End : bp.Start + (i * 0.25);
                    Point3d q = b.StationOffsetElevationToXYZ(new Point3d(s, 0, 0));
                    bp._p.Add((s, q.X, q.Y));
                }
                return bp._p.Count >= 2 ? bp : null;
            }
            catch
            {
                return null;
            }
        }

        public (Point3d? Point, Vector3d? Dir) At(double station)
        {
            try
            {
                double s = Math.Min(End, Math.Max(Start, station));
                Point3d q = _b.StationOffsetElevationToXYZ(new Point3d(s, 0, 0));
                Vector3d d = _b.GetDirectionAtStation(s);
                var h = new Vector3d(d.X, d.Y, 0);
                return h.Length < 1e-9 ? (null, null) : (q, h.GetNormal());
            }
            catch
            {
                return (null, null);
            }
        }

        public double Station(double x, double y)
        {
            double best = double.MaxValue, st = double.NaN;
            for (int i = 0; i + 1 < _p.Count; i++)
            {
                var (s0, x0, y0) = _p[i];
                var (s1, x1, y1) = _p[i + 1];
                double dx = x1 - x0, dy = y1 - y0, l2 = (dx * dx) + (dy * dy);
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (((x - x0) * dx) + ((y - y0) * dy)) / l2));
                double px = x0 + (t * dx) - x, py = y0 + (t * dy) - y, d2 = (px * px) + (py * py);
                if (d2 < best)
                {
                    best = d2;
                    st = s0 + (t * (s1 - s0));
                }
            }
            return st;
        }
    }
    /// <summary>Caixa em planta [xmin, ymin, xmax, ymax].</summary>
    private static double[] Box(IEnumerable<double[]> pts)
    {
        double[] b = { double.MaxValue, double.MaxValue, double.MinValue, double.MinValue };
        foreach (double[] p in pts)
        {
            b[0] = Math.Min(b[0], p[0]);
            b[1] = Math.Min(b[1], p[1]);
            b[2] = Math.Max(b[2], p[0]);
            b[3] = Math.Max(b[3], p[1]);
        }
        return b;
    }

    private static double Overlap(double[] a, double[] b) =>
        Math.Max(0, Math.Min(a[2], b[2]) - Math.Max(a[0], b[0])) * Math.Max(0, Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]));

    private sealed class Product
    {
        public int Id;
        public string Name = "";
        public string Key = "";
        public Step.Ref? Placement;
        public int Shape;
    }

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Key(Dictionary<string, string> p)
    {
        var parts = new List<string>();
        foreach (string k in KeyProps)
        {
            parts.Add(p.TryGetValue(k, out string? x) ? x.Trim().ToUpperInvariant() : "");
        }
        return string.Join("|", parts);
    }

    /// <summary>Propriedades do sólido extraído (Property Sets AEC), com nomes normalizados.</summary>
    private static Dictionary<string, string> SolidProperties(Solid3d s, Transaction tr)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (ObjectId psId in AecPd.PropertyDataServices.GetPropertySets(s))
            {
                var ps = (AecPd.PropertySet)tr.GetObject(psId, OpenMode.ForRead);
                var def = (AecPd.PropertySetDefinition)tr.GetObject(ps.PropertySetDefinition, OpenMode.ForRead);
                foreach (AecPd.PropertyDefinition pd in def.Definitions)
                {
                    object? v = null;
                    try
                    {
                        v = ps.GetAt(pd.Id);
                    }
                    catch
                    {
                        // Propriedade sem valor.
                    }
                    string k = Norm(pd.Name);
                    if (!d.ContainsKey(k) || string.IsNullOrEmpty(d[k]))
                        d[k] = Convert.ToString(v, IC) ?? "";
                }
            }
        }
        catch
        {
            // Sem property sets: o sólido não casa com nenhuma peça.
        }
        return d;
    }

    /// <summary>Malha triangular do sólido, fechada e com normais para fora.</summary>
    private static Mesh? Facet(Solid3d s)
    {
        try
        {
            var fd = new MeshFaceterData { FaceterMeshType = 2, FaceterDevSurface = 0.01, FaceterMaxEdgeLength = 0 };
            MeshDataCollection md = SubDMesh.GetObjectMesh(s, fd);
            var m = new Mesh();
            foreach (Point3d p in md.VertexArray)
                m.Points.Add(new[] { p.X, p.Y, p.Z });
            Int32Collection f = md.FaceArray;
            for (int k = 0; k < f.Count;)
            {
                int n = f[k];
                for (int q = 2; q < n; q++)
                    m.Tris.Add(new[] { f[k + 1], f[k + q], f[k + q + 1] });
                k += n + 1;
            }
            if (m.Tris.Count == 0)
                return null;
            m.Weld();
            m.Orient();
            return m;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Área em planta das faces voltadas para cima (malha fechada, normais para fora).</summary>
    private static double PlanArea(List<double[]> p, List<int[]> t)
    {
        double a = 0;
        foreach (int[] f in t)
        {
            double[] u = p[f[0]], v = p[f[1]], w = p[f[2]];
            double z = ((v[0] - u[0]) * (w[1] - u[1])) - ((v[1] - u[1]) * (w[0] - u[0]));
            if (z > 0)
                a += z / 2;
        }
        return a;
    }

    internal sealed class Mesh
    {
        public List<double[]> Points = new();
        public List<int[]> Tris = new();
        public bool Closed = true;
        public RebaixoCut? Rebaixo;
        public int Source;

        public static Mesh Merge(List<Mesh> l)
        {
            if (l.Count == 1)
                return l[0];
            var m = new Mesh { Closed = l.All(x => x.Closed) };
            foreach (Mesh x in l)
            {
                int b = m.Points.Count;
                m.Points.AddRange(x.Points);
                m.Tris.AddRange(x.Tris.Select(t => new[] { t[0] + b, t[1] + b, t[2] + b }));
            }
            return m;
        }

        /// <summary>Junta vértices repetidos (1/10 mm), tira triângulos degenerados e confere se a malha é fechada.</summary>
        public void Weld()
        {
            var key = new Dictionary<(long, long, long), int>();
            var map = new int[Points.Count];
            var pts = new List<double[]>();
            for (int i = 0; i < Points.Count; i++)
            {
                double[] p = Points[i];
                var k = ((long)Math.Round(p[0] * 1e4), (long)Math.Round(p[1] * 1e4), (long)Math.Round(p[2] * 1e4));
                if (!key.TryGetValue(k, out int j))
                {
                    j = pts.Count;
                    key[k] = j;
                    pts.Add(p);
                }
                map[i] = j;
            }
            Points = pts;
            Tris = Tris.Select(t => new[] { map[t[0]], map[t[1]], map[t[2]] })
                .Where(t => t[0] != t[1] && t[1] != t[2] && t[0] != t[2]).ToList();
            var edges = new Dictionary<(int, int), int>();
            foreach (int[] t in Tris)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = t[e], b = t[(e + 1) % 3];
                    var k = a < b ? (a, b) : (b, a);
                    edges[k] = edges.TryGetValue(k, out int c) ? c + 1 : 1;
                }
            }
            Closed = edges.Values.All(c => c == 2);
        }

        /// <summary>Volume com sinal negativo = normais para dentro: inverte todos os triângulos.</summary>
        public void Orient()
        {
            // Relativo a um ponto da malha: com coordenadas UTM (7.788.000) o produto misto perde precisão.
            double[] o = Points[Tris[0][0]];
            double[] R(double[] p) => new[] { p[0] - o[0], p[1] - o[1], p[2] - o[2] };
            double v = 0;
            foreach (int[] t in Tris)
            {
                double[] a = R(Points[t[0]]), b = R(Points[t[1]]), c = R(Points[t[2]]);
                v += (a[0] * ((b[1] * c[2]) - (b[2] * c[1]))) - (a[1] * ((b[0] * c[2]) - (b[2] * c[0]))) + (a[2] * ((b[0] * c[1]) - (b[1] * c[0])));
            }
            if (v < 0)
                Tris = Tris.Select(t => new[] { t[0], t[2], t[1] }).ToList();
        }
    }

    internal sealed class MapConversion
    {
        public double E, N, H, A = 1, B, Scale = 1, Unit = 1;

        /// <summary>Coordenadas do .ifc (projeto) -> desenho.</summary>
        public double[] ToWorld(double[] p)
        {
            double x = p[0] * Unit, y = p[1] * Unit, z = p[2] * Unit;
            return new[] { E + (Scale * ((A * x) - (B * y))), N + (Scale * ((B * x) + (A * y))), H + z };
        }

        /// <summary>Desenho -> coordenadas do .ifc (projeto).</summary>
        public double[] ToIfc(double[] w)
        {
            double dx = (w[0] - E) / Scale, dy = (w[1] - N) / Scale;
            double x = (A * dx) + (B * dy), y = (-B * dx) + (A * dy);
            return new[] { x / Unit, y / Unit, (w[2] - H) / Unit };
        }
    }

    /// <summary>Leitor/editor mínimo de STEP (uma entidade por linha, como a extensão grava).</summary>
    internal sealed class Step
    {
        internal sealed record Ref(int Id);

        internal sealed record Typed(string Name, object? Value);

        private readonly List<string> _lines;
        private readonly string _nl;
        private readonly Dictionary<int, int> _index = new();
        private readonly Dictionary<int, string> _type = new();
        private readonly Dictionary<int, List<object?>> _cache = new();

        public Step(string text)
        {
            _nl = text.Contains("\r\n") ? "\r\n" : "\n";
            _lines = text.Split(new[] { _nl }, StringSplitOptions.None).ToList();
            for (int i = 0; i < _lines.Count; i++)
            {
                string l = _lines[i];
                if (l.Length < 4 || l[0] != '#')
                    continue;
                int eq = l.IndexOf('=');
                if (eq < 0 || !int.TryParse(l.AsSpan(1, eq - 1), NumberStyles.Integer, IC, out int id))
                    continue;
                int k = eq + 1;
                while (k < l.Length && l[k] == ' ')
                    k++;
                int lp = l.IndexOf('(', k);
                if (lp < 0)
                    continue;
                _index[id] = i;
                _type[id] = l.Substring(k, lp - k).Trim();
            }
        }

        public IEnumerable<int> IdsOfTypes(HashSet<string> types) => _type.Where(kv => types.Contains(kv.Value)).Select(kv => kv.Key).ToList();

        public string TypeOf(int id) => _type.TryGetValue(id, out string? t) ? t : "";

        public List<object?> Args(int id)
        {
            if (_cache.TryGetValue(id, out var a))
                return a;
            string l = _lines[_index[id]];
            int pos = l.IndexOf('(', l.IndexOf('='));
            a = (List<object?>)Parse(l, ref pos)!;
            _cache[id] = a;
            return a;
        }

        public static string Str(object? o) => o as string ?? "";

        private static double D(object? o) => o switch
        {
            double d => d,
            Typed t => D(t.Value),
            _ => 0
        };

        /// <summary>Propriedades simples (normalizadas) de cada objeto, juntando todos os property sets dele.</summary>
        public Dictionary<int, Dictionary<string, string>> PropertiesByObject()
        {
            var result = new Dictionary<int, Dictionary<string, string>>();
            foreach (int rel in IdsOfTypes(new HashSet<string> { "IFCRELDEFINESBYPROPERTIES" }))
            {
                List<object?> a = Args(rel);
                if (a.Count < 6 || a[5] is not Ref ps || TypeOf(ps.Id) != "IFCPROPERTYSET" || a[4] is not List<object?> objs)
                    continue;
                var pv = new List<(string, string)>();
                if (Args(ps.Id)[4] is List<object?> items)
                {
                    foreach (object? it in items)
                    {
                        if (it is not Ref pr || TypeOf(pr.Id) != "IFCPROPERTYSINGLEVALUE")
                            continue;
                        List<object?> pa = Args(pr.Id);
                        object? val = pa.Count > 2 ? pa[2] : null;
                        string vs = val is Typed t ? Convert.ToString(t.Value, IC) ?? "" : Convert.ToString(val, IC) ?? "";
                        pv.Add((Norm(Str(pa[0])), IfcReport.Decode(vs)));
                    }
                }
                foreach (object? o in objs)
                {
                    if (o is not Ref r)
                        continue;
                    if (!result.TryGetValue(r.Id, out var d))
                        result[r.Id] = d = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach ((string k, string v) in pv)
                    {
                        if (!d.ContainsKey(k) || string.IsNullOrEmpty(d[k]))
                            d[k] = v;
                    }
                }
            }
            return result;
        }

        public MapConversion Map()
        {
            var m = new MapConversion();
            int mc = _type.Where(kv => kv.Value == "IFCMAPCONVERSION").Select(kv => kv.Key).DefaultIfEmpty(0).Min();
            if (mc != 0)
            {
                List<object?> a = Args(mc);
                m.E = D(a[2]);
                m.N = D(a[3]);
                m.H = D(a[4]);
                if (a.Count > 6 && a[5] is not null && a[6] is not null)
                {
                    double ax = D(a[5]), ay = D(a[6]), l = Math.Sqrt((ax * ax) + (ay * ay));
                    if (l > 1e-12)
                    {
                        m.A = ax / l;
                        m.B = ay / l;
                    }
                }
                if (a.Count > 7 && a[7] is not null && D(a[7]) > 0)
                    m.Scale = D(a[7]);
            }
            foreach (int u in IdsOfTypes(new HashSet<string> { "IFCSIUNIT" }))
            {
                List<object?> a = Args(u);
                if (a.Count > 3 && Str(a[1]) == ".LENGTHUNIT." && Str(a[3]) == ".METRE.")
                    m.Unit = Str(a[2]) switch { ".MILLI." => 0.001, ".CENTI." => 0.01, ".DECI." => 0.1, _ => 1 };
            }
            return m;
        }

        /// <summary>Matriz 3x4 (linha a linha) do posicionamento do produto no sistema do projeto.</summary>
        public double[] Placement(Ref? r)
        {
            if (r is null || !_type.ContainsKey(r.Id))
                return Identity();
            List<object?> a = Args(r.Id);
            return TypeOf(r.Id) switch
            {
                "IFCLOCALPLACEMENT" => Mul(Placement(a[0] as Ref), Axis(a[1] as Ref)),
                "IFCLINEARPLACEMENT" => Mul(Placement(a[0] as Ref), a.Count > 2 ? Axis(a[2] as Ref) : Identity()),
                _ => Identity()
            };
        }

        private double[] Axis(Ref? r)
        {
            if (r is null || TypeOf(r.Id) != "IFCAXIS2PLACEMENT3D")
                return Identity();
            List<object?> a = Args(r.Id);
            var loc = (List<object?>)Args(((Ref)a[0]!).Id)[0]!;
            double[] o = { D(loc[0]), D(loc[1]), loc.Count > 2 ? D(loc[2]) : 0 };
            double[] z = a.Count > 1 && a[1] is Ref zr ? Dir(zr) : new double[] { 0, 0, 1 };
            double[] x = a.Count > 2 && a[2] is Ref xr ? Dir(xr) : new double[] { 1, 0, 0 };
            z = Unit3(z);
            double dz = Dot(x, z);
            x = Unit3(new[] { x[0] - (z[0] * dz), x[1] - (z[1] * dz), x[2] - (z[2] * dz) });
            double[] y = { (z[1] * x[2]) - (z[2] * x[1]), (z[2] * x[0]) - (z[0] * x[2]), (z[0] * x[1]) - (z[1] * x[0]) };
            return new[] { x[0], y[0], z[0], o[0], x[1], y[1], z[1], o[1], x[2], y[2], z[2], o[2] };
        }

        private double[] Dir(Ref r)
        {
            var l = (List<object?>)Args(r.Id)[0]!;
            return new[] { D(l[0]), D(l[1]), l.Count > 2 ? D(l[2]) : 0 };
        }

        /// <summary>Malhas do corpo (representação Body) do produto, em coordenadas do desenho.</summary>
        public List<(int Tfs, List<double[]> World, List<int[]> Tris)> BodyMeshes(int pds, double[] place, MapConversion map)
        {
            var list = new List<(int, List<double[]>, List<int[]>)>();
            if (Args(pds)[2] is not List<object?> reps)
                return list;
            foreach (object? ro in reps)
            {
                if (ro is not Ref rr || TypeOf(rr.Id) != "IFCSHAPEREPRESENTATION")
                    continue;
                List<object?> ra = Args(rr.Id);
                if (Str(ra[1]) != "Body" || ra[3] is not List<object?> items)
                    continue;
                foreach (object? io in items)
                {
                    if (io is not Ref ir || TypeOf(ir.Id) != "IFCTRIANGULATEDFACESET")
                        continue;
                    List<object?> ta = Args(ir.Id);
                    var coords = (List<object?>)Args(((Ref)ta[0]!).Id)[0]!;
                    var world = coords.Select(c => { var l = (List<object?>)c!; return map.ToWorld(Xf(place, new[] { D(l[0]), D(l[1]), D(l[2]) })); }).ToList();
                    List<int>? pn = ta.Count > 4 && ta[4] is List<object?> pl ? pl.Select(x => (int)D(x)).ToList() : null;
                    var tris = new List<int[]>();
                    foreach (object? t in (List<object?>)ta[3]!)
                    {
                        int[] f = ((List<object?>)t!).Select(x => (int)D(x) - 1).ToArray();
                        if (pn is not null)
                            f = f.Select(i => pn[i] - 1).ToArray();
                        tris.Add(f);
                    }
                    list.Add((ir.Id, world, tris));
                }
            }
            return list;
        }

        // ---- edição ----

        internal sealed class Editor
        {
            private readonly Step _s;
            private readonly Dictionary<int, string> _replace = new();
            private readonly HashSet<int> _remove = new();

            public Editor(Step s) => _s = s;

            /// <summary>Troca as malhas do corpo: a primeira recebe os pontos/triângulos novos, as outras saem.</summary>
            public void ReplaceBody(int pds, List<int> tfs, List<double[]> local, List<int[]> tris, bool closed)
            {
                int keep = tfs[0];
                int list = ((Ref)_s.Args(keep)[0]!).Id;
                var sb = new StringBuilder();
                sb.Append('#').Append(list).Append("= IFCCARTESIANPOINTLIST3D((");
                for (int i = 0; i < local.Count; i++)
                    sb.Append(i == 0 ? "(" : ",(").Append(F(local[i][0])).Append(',').Append(F(local[i][1])).Append(',').Append(F(local[i][2])).Append(')');
                sb.Append("),$);");
                _replace[list] = sb.ToString();
                var tb = new StringBuilder();
                tb.Append('#').Append(keep).Append("= IFCTRIANGULATEDFACESET(#").Append(list).Append(",$,").Append(closed ? ".T." : ".F.").Append(",(");
                for (int i = 0; i < tris.Count; i++)
                    tb.Append(i == 0 ? "(" : ",(").Append(tris[i][0] + 1).Append(',').Append(tris[i][1] + 1).Append(',').Append(tris[i][2] + 1).Append(')');
                tb.Append("),$);");
                _replace[keep] = tb.ToString();
                if (tfs.Count == 1)
                    return;
                var drop = new HashSet<int>(tfs.Skip(1));
                foreach (int t in drop)
                {
                    _remove.Add(t);
                    _remove.Add(((Ref)_s.Args(t)[0]!).Id);
                }
                // Representação Body só com a malha que ficou; estilos das malhas tiradas saem junto.
                if (_s.Args(pds)[2] is List<object?> reps)
                {
                    foreach (object? ro in reps)
                    {
                        if (ro is not Ref rr || _s.TypeOf(rr.Id) != "IFCSHAPEREPRESENTATION")
                            continue;
                        string l = _s._lines[_s._index[rr.Id]];
                        if (!l.Contains("#" + keep.ToString(IC)))
                            continue;
                        List<object?> ra = _s.Args(rr.Id);
                        var keepItems = ((List<object?>)ra[3]!).OfType<Ref>().Select(x => x.Id).Where(x => !drop.Contains(x)).ToList();
                        int open = l.LastIndexOf('(');
                        _replace[rr.Id] = l.Substring(0, open) + "(" + string.Join(",", keepItems.Select(x => "#" + x.ToString(IC))) + "));";
                    }
                }
                foreach (int id in _s.IdsOfTypes(new HashSet<string> { "IFCSTYLEDITEM" }))
                {
                    if (_s.Args(id)[0] is Ref it && drop.Contains(it.Id))
                        _remove.Add(id);
                }
            }

            private readonly List<string> _added = new();
            private int _next;
            private Dictionary<int, List<int>>? _relsOf;

            public bool Changed => _replace.Count > 0 || _remove.Count > 0 || _added.Count > 0;

            private string Line(int id) => _replace.TryGetValue(id, out string? l) ? l : _s._lines[_s._index[id]];

            private int NextId()
            {
                if (_next == 0)
                    _next = _s._index.Keys.Max() + 1;
                return _next++;
            }

            /// <summary>GUID novo no formato do IFC (22 caracteres, base 64 do padrão).</summary>
            public static string NewGuid()
            {
                const string C = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";
                var n = System.Numerics.BigInteger.Parse("0" + Guid.NewGuid().ToString("N"), NumberStyles.HexNumber, IC);
                var r = new char[22];
                for (int i = 21; i >= 0; i--)
                {
                    r[i] = C[(int)(n % 64)];
                    n /= 64;
                }
                return new string(r);
            }

            private static readonly System.Text.RegularExpressions.Regex NameArg = new(@"^(#\d+=\s*IFC\w+\('[^']*',(?:#\d+|\$),)('(?:[^']|'')*'|\$)");

            private static string SetName(string line, string name) =>
                NameArg.Replace(line, m => m.Groups[1].Value + "'" + name.Replace("'", "''") + "'", 1);

            private string OwnerOf(int prod) => _s.Args(prod)[1] is Ref oh ? "#" + oh.Id.ToString(IC) : "$";

            /// <summary>Relações (contenção, agregação, propriedades exceto Quantities) que citam cada objeto.</summary>
            private Dictionary<int, List<int>> RelsOf()
            {
                if (_relsOf is not null)
                    return _relsOf;
                _relsOf = new Dictionary<int, List<int>>();
                foreach (int rel in _s.IdsOfTypes(new HashSet<string> { "IFCRELCONTAINEDINSPATIALSTRUCTURE", "IFCRELAGGREGATES", "IFCRELDEFINESBYPROPERTIES" }))
                {
                    List<object?> a = _s.Args(rel);
                    string t = _s.TypeOf(rel);
                    object? list = t == "IFCRELAGGREGATES" ? (a.Count > 5 ? a[5] : null) : (a.Count > 4 ? a[4] : null);
                    if (t == "IFCRELDEFINESBYPROPERTIES" && a.Count > 5 && a[5] is Ref ps && _s.TypeOf(ps.Id) == "IFCPROPERTYSET" && Str(_s.Args(ps.Id)[2]) == "Quantities")
                        continue;
                    if (list is not List<object?> objs)
                        continue;
                    foreach (Ref o in objs.OfType<Ref>())
                    {
                        if (!_relsOf.TryGetValue(o.Id, out var l))
                            _relsOf[o.Id] = l = new List<int>();
                        l.Add(rel);
                    }
                }
                return _relsOf;
            }

            /// <summary>Acrescenta o objeto à lista de objetos relacionados da relação.</summary>
            private void AppendRel(int rel, int obj)
            {
                string l = Line(rel);
                int i = l.IndexOf("(#", l.IndexOf('(') + 1, StringComparison.Ordinal);
                int close = l.IndexOf(')', i);
                _replace[rel] = l.Substring(0, close) + ",#" + obj.ToString(IC) + l.Substring(close);
            }

            private void AddPset(int prod, string oh, string psetName, List<(string Name, string Value)> values)
            {
                var ids = new List<int>();
                foreach ((string n, string v) in values)
                {
                    int id = NextId();
                    ids.Add(id);
                    _added.Add($"#{id}= IFCPROPERTYSINGLEVALUE('{n}',$,{v},$);");
                }
                int ps = NextId();
                _added.Add($"#{ps}= IFCPROPERTYSET('{NewGuid()}',{oh},'{psetName}',$,({string.Join(",", ids.Select(x => "#" + x.ToString(IC)))}));");
                int rel = NextId();
                _added.Add($"#{rel}= IFCRELDEFINESBYPROPERTIES('{NewGuid()}',{oh},$,$,(#{prod}),#{ps});");
            }

            /// <summary>A própria peça passa a ser o rebaixo: nome novo e property set C3D_REBAIXO.</summary>
            public void MarkRebaixo(int prod, string newName, List<(string Name, string Value)> pset)
            {
                _replace[prod] = SetName(Line(prod), newName);
                AddPset(prod, OwnerOf(prod), "C3D_REBAIXO", pset);
            }

            /// <summary>
            /// Elemento novo copiado da peça (mesma classe, posicionamento, estilos, contenção e property sets; GUID e
            /// nome novos), com a malha dada e o property set C3D_REBAIXO. Devolve o id do elemento.
            /// </summary>
            public int AddCopy(int prod, int pds, List<int> tfs, string name, List<double[]> local, List<int[]> tris, bool closed, List<(string Name, string Value)> pset)
            {
                int list = NextId(), face = NextId(), rep = NextId(), shape = NextId(), copy = NextId();
                var sb = new StringBuilder();
                sb.Append('#').Append(list).Append("= IFCCARTESIANPOINTLIST3D((");
                for (int i = 0; i < local.Count; i++)
                    sb.Append(i == 0 ? "(" : ",(").Append(F(local[i][0])).Append(',').Append(F(local[i][1])).Append(',').Append(F(local[i][2])).Append(')');
                sb.Append("),$);");
                _added.Add(sb.ToString());
                var tb = new StringBuilder();
                tb.Append('#').Append(face).Append("= IFCTRIANGULATEDFACESET(#").Append(list).Append(",$,").Append(closed ? ".T." : ".F.").Append(",(");
                for (int i = 0; i < tris.Count; i++)
                    tb.Append(i == 0 ? "(" : ",(").Append(tris[i][0] + 1).Append(',').Append(tris[i][1] + 1).Append(',').Append(tris[i][2] + 1).Append(')');
                tb.Append("),$);");
                _added.Add(tb.ToString());
                int ctx = 0;
                if (_s.Args(pds)[2] is List<object?> reps)
                {
                    foreach (Ref rr in reps.OfType<Ref>())
                    {
                        List<object?> ra = _s.Args(rr.Id);
                        if (_s.TypeOf(rr.Id) == "IFCSHAPEREPRESENTATION" && Str(ra[1]) == "Body" && ra[0] is Ref cr)
                        {
                            ctx = cr.Id;
                            break;
                        }
                    }
                }
                _added.Add($"#{rep}= IFCSHAPEREPRESENTATION(#{ctx},'Body','Tessellation',(#{face}));");
                _added.Add($"#{shape}= IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}));");
                string pl = Line(prod);
                pl = "#" + copy.ToString(IC) + pl.Substring(pl.IndexOf('='));
                pl = SetName(pl, name);
                int g = pl.IndexOf("('", StringComparison.Ordinal);
                int ge = pl.IndexOf('\'', g + 2);
                pl = pl.Substring(0, g + 2) + NewGuid() + pl.Substring(ge);
                pl = System.Text.RegularExpressions.Regex.Replace(pl, "#" + pds.ToString(IC) + @"(?!\d)", "#" + shape.ToString(IC));
                _added.Add(pl);
                // Estilo (cor/material) da malha original.
                foreach (int id in _s.IdsOfTypes(new HashSet<string> { "IFCSTYLEDITEM" }))
                {
                    if (_s.Args(id)[0] is not Ref it || it.Id != tfs[0])
                        continue;
                    string sl = _s._lines[_s._index[id]];
                    int a = sl.IndexOf('(');
                    int b = sl.IndexOf(',', a);
                    _added.Add("#" + NextId().ToString(IC) + "= IFCSTYLEDITEM(#" + face.ToString(IC) + sl.Substring(b));
                }
                if (RelsOf().TryGetValue(prod, out var rels))
                {
                    foreach (int rel in rels)
                        AppendRel(rel, copy);
                }
                AddPset(copy, OwnerOf(prod), "C3D_REBAIXO", pset);
                return copy;
            }

            public string Result()
            {
                var sb = new StringBuilder(_s._lines.Sum(l => l.Length + 2) + _added.Sum(l => l.Length + 2));
                int endData = _s._lines.FindLastIndex(l => l.Trim() == "ENDSEC;");
                for (int i = 0; i < _s._lines.Count; i++)
                {
                    if (i == endData)
                    {
                        foreach (string a in _added)
                            sb.Append(a).Append(_s._nl);
                    }
                    string l = _s._lines[i];
                    int id = LineId(l);
                    if (id > 0 && _s._index.TryGetValue(id, out int ix) && ix == i)
                    {
                        if (_remove.Contains(id))
                            continue;
                        if (_replace.TryGetValue(id, out string? nl))
                            l = nl;
                    }
                    sb.Append(l);
                    if (i < _s._lines.Count - 1)
                        sb.Append(_s._nl);
                }
                return sb.ToString();
            }

            private static int LineId(string l)
            {
                if (l.Length < 2 || l[0] != '#')
                    return 0;
                int eq = l.IndexOf('=');
                return eq > 1 && int.TryParse(l.AsSpan(1, eq - 1), NumberStyles.Integer, IC, out int id) ? id : 0;
            }
        }

        internal static string F(double v) => v.ToString("0.######", IC);

        // ---- matrizes 3x4 ----

        public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    r[(i * 4) + j] = (a[i * 4] * b[j]) + (a[(i * 4) + 1] * b[4 + j]) + (a[(i * 4) + 2] * b[8 + j]);
                r[(i * 4) + 3] = (a[i * 4] * b[3]) + (a[(i * 4) + 1] * b[7]) + (a[(i * 4) + 2] * b[11]) + a[(i * 4) + 3];
            }
            return r;
        }

        /// <summary>Inversa de um posicionamento rígido (rotação ortonormal + translação).</summary>
        public static double[] Inverse(double[] m)
        {
            var r = new double[12];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    r[(i * 4) + j] = m[(j * 4) + i];
                r[(i * 4) + 3] = -((m[i] * m[3]) + (m[4 + i] * m[7]) + (m[8 + i] * m[11]));
            }
            return r;
        }

        public static double[] Xf(double[] m, double[] p) => new[]
        {
            (m[0] * p[0]) + (m[1] * p[1]) + (m[2] * p[2]) + m[3],
            (m[4] * p[0]) + (m[5] * p[1]) + (m[6] * p[2]) + m[7],
            (m[8] * p[0]) + (m[9] * p[1]) + (m[10] * p[2]) + m[11]
        };

        private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

        private static double[] Unit3(double[] a)
        {
            double l = Math.Sqrt(Dot(a, a));
            return l < 1e-15 ? a : new[] { a[0] / l, a[1] / l, a[2] / l };
        }

        // ---- parser ----

        private static object? Parse(string s, ref int i)
        {
            while (s[i] == ' ')
                i++;
            char c = s[i];
            if (c == '(')
            {
                i++;
                var l = new List<object?>();
                while (true)
                {
                    while (s[i] == ' ')
                        i++;
                    if (s[i] == ')')
                    {
                        i++;
                        return l;
                    }
                    l.Add(Parse(s, ref i));
                    while (s[i] == ' ')
                        i++;
                    if (s[i] == ',')
                        i++;
                }
            }
            if (c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                return sb.ToString();
            }
            if (c == '#')
            {
                int j = i + 1;
                while (char.IsDigit(s[j]))
                    j++;
                var r = new Ref(int.Parse(s.AsSpan(i + 1, j - i - 1), NumberStyles.Integer, IC));
                i = j;
                return r;
            }
            if (c is '$' or '*')
            {
                i++;
                return null;
            }
            if (c == '.')
            {
                int j = s.IndexOf('.', i + 1);
                string e = s.Substring(i, j - i + 1);
                i = j + 1;
                return e;
            }
            if (char.IsLetter(c))
            {
                int j = i;
                while (char.IsLetterOrDigit(s[j]) || s[j] == '_')
                    j++;
                string name = s.Substring(i, j - i);
                i = j;
                var inner = (List<object?>)Parse(s, ref i)!;
                return new Typed(name, inner.Count > 0 ? inner[0] : null);
            }
            {
                int j = i;
                while ("0123456789+-.Ee".IndexOf(s[j]) >= 0)
                    j++;
                double v = double.Parse(s.AsSpan(i, j - i), NumberStyles.Float, IC);
                i = j;
                return v;
            }
        }
    }
}
