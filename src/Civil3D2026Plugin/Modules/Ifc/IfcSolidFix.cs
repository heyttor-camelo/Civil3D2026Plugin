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
        public long Milliseconds;
        public List<string> Details = new();
        public string Error = "";
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

    /// <summary>Lê o .ifc, corrige e grava de volta. Sem o que corrigir, o arquivo não é regravado.</summary>
    public static Result ApplyToFile(string ifcPath, Database db, IEnumerable<ObjectId> corridorIds, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var r = new Result();
        try
        {
            string text = File.ReadAllText(ifcPath, Encoding.ASCII);
            string? fixedText = Apply(text, db, corridorIds, r, log);
            if (fixedText is not null)
                File.WriteAllText(ifcPath, fixedText, Encoding.ASCII);
        }
        catch (System.Exception ex)
        {
            r.Error = ex.Message;
            log?.Invoke("Correcao de pecas: " + ex);
        }
        r.Milliseconds = sw.ElapsedMilliseconds;
        return r;
    }

    /// <summary>Devolve o texto corrigido, ou null se nada mudou.</summary>
    public static string? Apply(string text, Database db, IEnumerable<ObjectId> corridorIds, Result r, Action<string>? log)
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
            log?.Invoke("Correcao de pecas: nenhuma peca de corredor com propriedades no .ifc.");
            return null;
        }
        MapConversion map = step.Map();

        // 2) Sólidos do corredor num banco temporário (o desenho não é tocado).
        var solids = new Dictionary<string, List<Mesh>>();
        foreach (ObjectId cid in corridorIds.Distinct())
        {
            using var side = new Database(true, true);
            ObjectIdCollection ids;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (tr.GetObject(cid, OpenMode.ForRead) is not CivilCorridorType c)
                {
                    tr.Commit();
                    continue;
                }
                var prm = new Autodesk.Civil.DatabaseServices.ExportCorridorSolidsParams
                {
                    ExportShapes = true,
                    ExportLinks = false,
                    CreateSolidForShape = true,
                    SweepSolidForShape = false
                };
                ids = c.ExportSolids(prm, side);
                tr.Commit();
            }
            using (Transaction st = side.TransactionManager.StartTransaction())
            {
                bool first = r.Solids == 0;
                foreach (ObjectId sid in ids)
                {
                    if (sid.IsNull || sid.IsErased || st.GetObject(sid, OpenMode.ForRead) is not Solid3d s)
                        continue;
                    r.Solids++;
                    var sp = SolidProperties(s, st);
                    if (first)
                    {
                        log?.Invoke("Correcao de pecas: propriedades do primeiro solido: " + string.Join("; ", sp.Select(kv => kv.Key + "=" + kv.Value)));
                        first = false;
                    }
                    Mesh? m = Facet(s);
                    if (m is null)
                        continue;
                    string k = Key(sp);
                    if (!solids.TryGetValue(k, out var l))
                        solids[k] = l = new List<Mesh>();
                    l.Add(m);
                }
                st.Commit();
            }
        }

        // 3) Comparação em planta e troca da malha.
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
            foreach (Mesh m in sl)
            {
                double[] mb = Box(m.Points);
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
                    assigned[best].Add(m);
            }
            for (int i = 0; i < parts.Count; i++)
            {
                if (assigned[i].Count == 0)
                    continue;
                r.Matched++;
                Product p = parts[i].P;
                double[] place = parts[i].Place;
                var current = parts[i].Current;
                double aIfc = current.Sum(c => PlanArea(c.World, c.Tris));
                var merged = Mesh.Merge(assigned[i]);
                double aSolid = PlanArea(merged.Points, merged.Tris);
                double diff = aSolid - aIfc;
                if (!(diff >= MinArea || (diff >= MinAreaRatio && diff >= MinRatio * aSolid)))
                    continue;
                // Pontos do sólido (desenho) -> coordenadas locais da peça no .ifc.
                double[] inv = Step.Inverse(place);
                var local = merged.Points.Select(w => Step.Xf(inv, map.ToIfc(w))).ToList();
                edits.ReplaceBody(p.Shape, current.Select(c => c.Tfs).ToList(), local, merged.Tris, merged.Closed);
                r.Replaced++;
                r.AreaRecovered += diff;
                r.Details.Add($"#{p.Id} {p.Name} [{kv.Key}]: {aIfc:F2} -> {aSolid:F2} m2");
            }
        }
        foreach (string d in r.Details)
            log?.Invoke("Correcao de pecas: " + d);
        return r.Replaced > 0 ? edits.Result() : null;
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

            public string Result()
            {
                var sb = new StringBuilder(_s._lines.Sum(l => l.Length + 2));
                for (int i = 0; i < _s._lines.Count; i++)
                {
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

        private static string F(double v) => v.ToString("0.######", IC);

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
