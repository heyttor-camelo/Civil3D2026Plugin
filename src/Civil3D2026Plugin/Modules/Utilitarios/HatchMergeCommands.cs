using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Utilitarios.Contorno;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using Region = Autodesk.AutoCAD.DatabaseServices.Region;

namespace Civil3D2026Plugin.Modules.Utilitarios;

/// <summary>
/// C3DUNIRHACHURAS: remonta o contorno de hachuras picadas, como as filled regions que o Revit
/// exporta em varios pedacos encostados (e sem associatividade). Os lacos de cada hachura sao
/// lidos, as divisas entre pecas se cancelam (ContornoUniao) e sai uma polilinha fechada por
/// contorno; no modo Hachura sai tambem uma hachura associativa no lugar das pecas.
/// </summary>
public sealed class HatchMergeCommands
{
    private const string Modulo = "C3DUNIRHACHURAS";

    private static double _tol = 0.001;
    private static string _resultado = "Hachura";
    private static string _agrupar = "Iguais";
    private static string _cor = "Manter";
    private static string _ordem = "Fundo";
    private static string _motor = "Regiao";

    private sealed class Grupo
    {
        public List<ObjectId> Hachuras { get; } = new();
        public List<IReadOnlyList<Laco>> Pecas { get; } = new();
        public ObjectId Modelo { get; set; }
        public double AreaModelo { get; set; } = -1;
    }

    [CommandMethod(C3DCommands.Utilitarios.UnirHachuras, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void UnirHachuras()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        var filtro = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "HATCH") });
        ObjectId[]? ids = null;
        PromptSelectionResult implicita = ed.SelectImplied();
        if (implicita.Status == PromptStatus.OK)
        {
            ids = implicita.Value.GetObjectIds();
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
        }

        if (!Opcoes(ed))
            return;

        if (ids is null)
        {
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelecione as hachuras picadas: ",
                RejectObjectsOnLockedLayers = true
            };
            PromptSelectionResult psr = ed.GetSelection(pso, filtro);
            if (psr.Status != PromptStatus.OK)
                return;
            ids = psr.Value.GetObjectIds();
        }

        bool hachura = _resultado == "Hachura";
        int lidas = 0, falhas = 0, travadas = 0;
        int contornos = 0, furos = 0, novasHachuras = 0, gruposDiferentes = 0, abertos = 0;

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            // Grupos por espaco (Model/layout), plano e, conforme a opcao, layer, cor e padrao.
            var grupos = new Dictionary<string, Grupo>();
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Hatch h)
                    continue;
                var ltr = (LayerTableRecord)tr.GetObject(h.LayerId, OpenMode.ForRead);
                if (ltr.IsLocked)
                {
                    travadas++;
                    continue;
                }

                List<Laco> lacos;
                try
                {
                    lacos = LerLacos(h, _tol);
                }
                catch (System.Exception ex)
                {
                    PluginLog.Aviso(Modulo, $"Hachura {h.Handle} nao lida: {ex.Message}");
                    falhas++;
                    continue;
                }
                if (lacos.Count == 0)
                {
                    falhas++;
                    continue;
                }

                string chave = Chave(h);
                if (!grupos.TryGetValue(chave, out Grupo? g))
                    grupos[chave] = g = new Grupo();
                g.Hachuras.Add(id);
                g.Pecas.Add(lacos);
                double area = lacos.Sum(l => Math.Abs(l.AreaComSinal()));
                if (area > g.AreaModelo)
                {
                    // A maior peca empresta padrao, cor e layer para o resultado.
                    g.AreaModelo = area;
                    g.Modelo = id;
                }
                lidas++;
            }

            foreach (Grupo g in grupos.Values)
            {
                ResultadoUniao r = _motor == "Regiao"
                    ? UnirPorRegiao(g.Pecas, _tol) ?? ContornoUniao.Unir(g.Pecas, _tol)
                    : ContornoUniao.Unir(g.Pecas, _tol);
                if (r.Ilhas.Count == 0)
                    continue;
                abertos += r.TrechosAbertos;
                double ref_ = Math.Max(Math.Abs(r.AreaEntrada), 1e-12);
                if (Math.Abs(r.AreaEntrada - r.AreaSaida) / ref_ > 0.001)
                    gruposDiferentes++;

                var modelo = (Hatch)tr.GetObject(g.Modelo, OpenMode.ForRead);
                var espaco = (BlockTableRecord)tr.GetObject(modelo.OwnerId, OpenMode.ForWrite);
                var criadas = new ObjectIdCollection();

                foreach (Ilha ilha in r.Ilhas)
                {
                    Polyline externo = NovaPolilinha(tr, espaco, modelo, ilha.Externo);
                    var furosIds = ilha.Furos.Select(f => NovaPolilinha(tr, espaco, modelo, f).ObjectId).ToList();
                    contornos++;
                    furos += furosIds.Count;

                    if (!hachura)
                        continue;
                    Hatch nova = NovaHachura(tr, espaco, modelo, _cor == "PorLayer");
                    nova.Associative = true;
                    nova.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { externo.ObjectId });
                    foreach (ObjectId fid in furosIds)
                        nova.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { fid });
                    nova.EvaluateHatch(true);
                    criadas.Add(nova.ObjectId);
                    novasHachuras++;
                }

                if (!hachura)
                    continue;

                var ordem = (DrawOrderTable)tr.GetObject(espaco.DrawOrderTableId, OpenMode.ForWrite);
                if (_ordem == "Fundo")
                {
                    ordem.MoveToBottom(criadas);
                }
                else
                {
                    // Fica onde estava a peca mais de baixo.
                    ObjectIdCollection completa = ordem.GetFullDrawOrder(0);
                    var originais = new HashSet<ObjectId>(g.Hachuras);
                    foreach (ObjectId oid in completa)
                    {
                        if (originais.Contains(oid))
                        {
                            ordem.MoveBelow(criadas, oid);
                            break;
                        }
                    }
                }

                foreach (ObjectId oid in g.Hachuras)
                    tr.GetObject(oid, OpenMode.ForWrite).Erase();
            }

            tr.Commit();
        }

        string msg = $"\n{lidas} hachura(s) lida(s): {contornos} contorno(s)" +
                     (furos > 0 ? $" com {furos} furo(s)" : "") +
                     (hachura ? $", {novasHachuras} hachura(s) nova(s) associativa(s) no lugar das pecas." : " (polilinhas; as hachuras ficaram como estavam).");
        if (travadas > 0)
            msg += $" {travadas} em layer travada ignorada(s).";
        if (falhas > 0)
            msg += $" {falhas} sem contorno legivel ignorada(s).";
        ed.WriteMessage(msg);
        if (gruposDiferentes > 0)
            ed.WriteMessage($"\nAtencao: em {gruposDiferentes} grupo(s) a area do contorno difere da soma das pecas (pecas sobrepostas ou folgas maiores que a tolerancia {_tol:0.####}). Confira ou aumente a [Tolerancia].");
        if (abertos > 0)
            ed.WriteMessage($"\nAtencao: {abertos} trecho(s) de borda nao fecharam contorno e foram descartados; aumente a [Tolerancia] se faltar pedaco.");
    }

    /// <summary>Opcoes antes da selecao; false se o usuario cancelar.</summary>
    private static bool Opcoes(Editor ed)
    {
        while (true)
        {
            string resumo = $"motor {_motor}, tolerancia {_tol:0.####}, {_resultado}, agrupar {_agrupar}, cor {_cor}, ordem {_ordem}";
            var pko = new PromptKeywordOptions(
                $"\nUnir hachuras ({resumo}) [Motor/Tolerancia/Resultado/Agrupar/Cor/Ordem] <Selecionar>: ",
                "Motor Tolerancia Resultado Agrupar Cor Ordem")
            { AllowNone = true };
            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status == PromptStatus.None)
                return true;
            if (pr.Status != PromptStatus.OK)
                return false;

            switch (pr.StringResult)
            {
                case "Motor":
                    _motor = Escolher(ed, "\nMotor: uniao de regioes do AutoCAD (aceita pecas sobrepostas) ou pelas arestas (so pecas encostadas) [Regiao/Arestas]", "Regiao Arestas", _motor);
                    break;
                case "Tolerancia":
                    var pdo = new PromptDistanceOptions($"\nVertices e bordas a menos disso sao o mesmo ponto/linha <{_tol:0.####}>: ")
                    {
                        AllowNone = true,
                        AllowNegative = false,
                        AllowZero = false
                    };
                    PromptDoubleResult pdr = ed.GetDistance(pdo);
                    if (pdr.Status == PromptStatus.OK)
                        _tol = pdr.Value;
                    break;
                case "Resultado":
                    _resultado = Escolher(ed, "\nResultado: hachura associativa no lugar das pecas ou so o contorno [Hachura/Contorno]", "Hachura Contorno", _resultado);
                    break;
                case "Agrupar":
                    _agrupar = Escolher(ed, "\nUnir pecas com mesma layer, cor e padrao, so mesma layer, ou todas [Iguais/Layer/Tudo]", "Iguais Layer Tudo", _agrupar);
                    break;
                case "Cor":
                    _cor = Escolher(ed, "\nCor da hachura nova [Manter/PorLayer]", "Manter PorLayer", _cor);
                    break;
                case "Ordem":
                    _ordem = Escolher(ed, "\nOrdem de desenho da hachura nova: atras de tudo ou onde estavam as pecas [Fundo/Manter]", "Fundo Manter", _ordem);
                    break;
            }
        }
    }

    private static string Escolher(Editor ed, string msg, string palavras, string atual)
    {
        var pko = new PromptKeywordOptions($"{msg} <{atual}>: ", palavras) { AllowNone = true };
        PromptResult pr = ed.GetKeywords(pko);
        return pr.Status == PromptStatus.OK ? pr.StringResult : atual;
    }

    private static string Chave(Hatch h)
    {
        Vector3d n = h.Normal;
        string plano = $"{h.OwnerId.Handle}|{n.X:0.######},{n.Y:0.######},{n.Z:0.######}|{Math.Round(h.Elevation / Math.Max(_tol, 1e-9))}";
        if (_agrupar == "Tudo")
            return plano;
        if (_agrupar == "Layer")
            return plano + "|" + h.Layer.ToUpperInvariant();
        return plano + "|" + h.Layer.ToUpperInvariant() + "|" + ChaveCor(h.Color) + "|" + ChavePadrao(h);
    }

    private static string ChaveCor(AcColor c)
        => c.IsByLayer ? "BYLAYER" : c.IsByBlock ? "BYBLOCK" : c.IsByAci ? $"ACI{c.ColorIndex}" : $"RGB{c.ColorValue.ToArgb()}";

    private static string ChavePadrao(Hatch h)
    {
        if (h.IsGradient)
            return $"G|{h.GradientName}|{h.GradientAngle:0.###}";
        return $"{h.PatternType}|{h.PatternName.ToUpperInvariant()}|{h.PatternScale:0.####}|{h.PatternAngle:0.####}|{h.PatternSpace:0.####}";
    }

    /// <summary>Lacos da hachura no plano dela (OCS), com os arcos como bulge.</summary>
    private static List<Laco> LerLacos(Hatch h, double tol)
    {
        var lacos = new List<Laco>();
        for (int i = 0; i < h.NumberOfLoops; i++)
        {
            HatchLoop loop = h.GetLoopAt(i);
            var laco = new Laco();
            if (loop.IsPolyline)
            {
                foreach (BulgeVertex bv in loop.Polyline)
                    laco.Add(new P2(bv.Vertex.X, bv.Vertex.Y), bv.Bulge);
            }
            else
            {
                var trechos = new List<List<(P2 P, double B)>>();
                foreach (Curve2d c in loop.Curves)
                    trechos.Add(Trecho(c));
                foreach ((P2 p, double b) in Encadear(trechos, Math.Max(tol, 1e-6)))
                    laco.Add(p, b);
            }
            if (laco.Count >= 2)
                lacos.Add(laco);
        }
        return lacos;
    }

    /// <summary>Um trecho do laco do inicio ao fim: vertices com bulge, o ultimo ponto e o fim.</summary>
    private static List<(P2 P, double B)> Trecho(Curve2d c)
    {
        static P2 P(Point2d p) => new(p.X, p.Y);
        var t = new List<(P2, double)>();
        switch (c)
        {
            case LineSegment2d ls:
                t.Add((P(ls.StartPoint), 0.0));
                t.Add((P(ls.EndPoint), 0.0));
                break;
            case CircularArc2d ca:
            {
                double sinal = ca.IsClockWise ? -1.0 : 1.0;
                double varre = ca.EndAngle - ca.StartAngle;
                if (Math.Abs(varre - (2.0 * Math.PI)) < 1e-9 || varre <= 0)
                {
                    // Circulo inteiro: duas metades.
                    Point2d p0 = ca.StartPoint;
                    Point2d p1 = ca.Center + (ca.Center - p0);
                    t.Add((P(p0), sinal));
                    t.Add((P(p1), sinal));
                    t.Add((P(p0), 0.0));
                }
                else
                {
                    t.Add((P(ca.StartPoint), sinal * Math.Tan(varre / 4.0)));
                    t.Add((P(ca.EndPoint), 0.0));
                }
                break;
            }
            default:
            {
                // Elipse, spline: aproximadas por retas.
                int n = c is NurbCurve2d nc ? Math.Max(17, nc.NumControlPoints * 4) : 49;
                foreach (Point2d p in c.GetSamplePoints(n))
                    t.Add((P(p), 0.0));
                break;
            }
        }
        return t;
    }

    /// <summary>Junta os trechos na ordem do laco, virando os que vieram ao contrario.</summary>
    private static List<(P2 P, double B)> Encadear(List<List<(P2 P, double B)>> trechos, double tol)
    {
        var resto = new List<List<(P2 P, double B)>>(trechos.Where(t => t.Count >= 2));
        var saida = new List<(P2 P, double B)>();
        if (resto.Count == 0)
            return saida;

        List<(P2 P, double B)> atual = resto[0];
        resto.RemoveAt(0);
        // O primeiro trecho vira se o fim dele nao encostar em nenhum outro e o inicio sim.
        if (resto.Count > 0 && !resto.Any(o => Perto(o[0].P, atual[^1].P, tol) || Perto(o[^1].P, atual[^1].P, tol))
            && resto.Any(o => Perto(o[0].P, atual[0].P, tol) || Perto(o[^1].P, atual[0].P, tol)))
            atual = Inverter(atual);

        while (true)
        {
            for (int i = 0; i < atual.Count - 1; i++)
                saida.Add(atual[i]);
            P2 fim = atual[^1].P;
            if (resto.Count == 0)
                break;

            int idx = resto.FindIndex(o => Perto(o[0].P, fim, tol));
            if (idx >= 0)
            {
                atual = resto[idx];
            }
            else if ((idx = resto.FindIndex(o => Perto(o[^1].P, fim, tol))) >= 0)
            {
                atual = Inverter(resto[idx]);
            }
            else
            {
                // Laco quebrado: segue na ordem gravada.
                idx = 0;
                atual = resto[0];
            }
            resto.RemoveAt(idx);
        }
        return saida;
    }

    private static bool Perto(P2 a, P2 b, double tol) => a.DistanceTo(b) <= tol;

    private static List<(P2 P, double B)> Inverter(List<(P2 P, double B)> t)
    {
        var r = new List<(P2 P, double B)>(t.Count);
        for (int i = t.Count - 1; i >= 0; i--)
            r.Add((t[i].P, i > 0 ? -t[i - 1].B : 0.0));
        return r;
    }

    // ------------------------------------------------------------------
    // Motor Regiao: cada peca vira uma Region, tudo e unido com o modelador do AutoCAD
    // (aguenta pecas sobrepostas e divisas que nao batem exatamente) e o contorno sai da
    // explosao da regiao final. Vertices e arcos originais ficam como estao.
    // ------------------------------------------------------------------

    /// <summary>Uniao pelas regioes; null se o modelador recusar alguma peca (cai no motor por arestas).</summary>
    private static ResultadoUniao? UnirPorRegiao(IReadOnlyList<IReadOnlyList<Laco>> pecas, double tol)
    {
        // Coordenadas locais: o modelador perde precisao com coordenadas UTM.
        P2 origem = pecas[0][0].Pontos[0];
        var res = new ResultadoUniao();
        Region? total = null;
        var descartar = new List<DBObject>();
        try
        {
            foreach (IReadOnlyList<Laco> peca in pecas)
            {
                using var curvas = new DBObjectCollection();
                foreach (Laco l in peca)
                {
                    if (l.Count < 2)
                        continue;
                    var pl = new Polyline();
                    for (int i = 0; i < l.Count; i++)
                        pl.AddVertexAt(i, new Point2d(l.Pontos[i].X - origem.X, l.Pontos[i].Y - origem.Y), l.Bulges[i], 0, 0);
                    pl.Closed = true;
                    curvas.Add(pl);
                }
                if (curvas.Count == 0)
                {
                    res.LacosDescartados++;
                    continue;
                }

                DBObjectCollection regioes;
                try
                {
                    regioes = Region.CreateFromCurves(curvas);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    PluginLog.Aviso(Modulo, $"Regiao recusada pelo modelador ({ex.ErrorStatus}); usando o motor por arestas.");
                    return null;
                }
                finally
                {
                    foreach (DBObject c in curvas)
                        c.Dispose();
                }

                foreach (DBObject o in regioes)
                {
                    if (o is not Region r)
                    {
                        o.Dispose();
                        continue;
                    }
                    if (total is null)
                    {
                        total = r;
                    }
                    else
                    {
                        total.BooleanOperation(BooleanOperationType.BoolUnite, r);
                        r.Dispose();
                    }
                }
            }
            if (total is null)
                return null;

            // Soma real das pecas (furos descontados), para a conferencia de area.
            res.AreaEntrada = AreaDasPecas(pecas, tol);

            var segs = new List<(P2 A, P2 B, double Bulge)>();
            Explodir(total, segs, descartar);

            foreach (Laco l in EncadearSegs(segs, Math.Max(tol, 1e-6), res))
            {
                for (int i = 0; i < l.Count; i++)
                    l.Pontos[i] = new P2(l.Pontos[i].X + origem.X, l.Pontos[i].Y + origem.Y);
            }
        }
        finally
        {
            total?.Dispose();
            foreach (DBObject d in descartar)
                d.Dispose();
        }
        return res;

        // Lacos locais encadeados e classificados, ja somados em res.
        static IEnumerable<Laco> EncadearSegs(List<(P2 A, P2 B, double Bulge)> segs, double t, ResultadoUniao r)
        {
            var lacos = new List<Laco>();
            var usado = new bool[segs.Count];
            for (int ini = 0; ini < segs.Count; ini++)
            {
                if (usado[ini])
                    continue;
                usado[ini] = true;
                var laco = new Laco();
                laco.Add(segs[ini].A, segs[ini].Bulge);
                P2 inicio = segs[ini].A;
                P2 fim = segs[ini].B;
                bool fechou = false;
                int passos = 0;
                while (passos++ <= segs.Count)
                {
                    if (fim.DistanceTo(inicio) <= t)
                    {
                        fechou = true;
                        break;
                    }
                    int prox = -1;
                    bool inverso = false;
                    double melhor = t;
                    for (int j = 0; j < segs.Count; j++)
                    {
                        if (usado[j])
                            continue;
                        double da = segs[j].A.DistanceTo(fim), db = segs[j].B.DistanceTo(fim);
                        if (da <= melhor) { melhor = da; prox = j; inverso = false; }
                        if (db < melhor) { melhor = db; prox = j; inverso = true; }
                    }
                    if (prox < 0)
                        break;
                    usado[prox] = true;
                    if (inverso)
                    {
                        laco.Add(segs[prox].B, -segs[prox].Bulge);
                        fim = segs[prox].A;
                    }
                    else
                    {
                        laco.Add(segs[prox].A, segs[prox].Bulge);
                        fim = segs[prox].B;
                    }
                }
                if (fechou)
                    lacos.Add(laco);
                else
                    r.TrechosAbertos += laco.Count;
            }
            ContornoUniao.Classificar(lacos, t, r, orientarPorProfundidade: true);
            return r.Ilhas.SelectMany(i => i.Furos.Prepend(i.Externo));
        }
    }

    /// <summary>Soma das areas das pecas, furos (lacos dentro de outro laco da mesma peca) descontados.</summary>
    private static double AreaDasPecas(IReadOnlyList<IReadOnlyList<Laco>> pecas, double tol)
    {
        double total = 0.0;
        foreach (IReadOnlyList<Laco> peca in pecas)
        {
            var r = new ResultadoUniao();
            ContornoUniao.Classificar(peca, tol, r, orientarPorProfundidade: true);
            total += r.AreaSaida;
        }
        return total;
    }

    /// <summary>Segmentos (reta ou arco com bulge) das bordas da regiao, no plano XY local.</summary>
    private static void Explodir(Region regiao, List<(P2 A, P2 B, double Bulge)> segs, List<DBObject> descartar)
    {
        using var partes = new DBObjectCollection();
        regiao.Explode(partes);
        foreach (DBObject o in partes)
        {
            descartar.Add(o);
            switch (o)
            {
                case Region sub:
                    Explodir(sub, segs, descartar);
                    break;
                case Line ln:
                    segs.Add((new P2(ln.StartPoint.X, ln.StartPoint.Y), new P2(ln.EndPoint.X, ln.EndPoint.Y), 0.0));
                    break;
                case Arc arc:
                {
                    double varre = arc.EndAngle - arc.StartAngle;
                    while (varre <= 0)
                        varre += 2.0 * Math.PI;
                    while (varre > 2.0 * Math.PI)
                        varre -= 2.0 * Math.PI;
                    double bulge = Math.Tan(varre / 4.0) * (arc.Normal.Z < 0 ? -1.0 : 1.0);
                    segs.Add((new P2(arc.StartPoint.X, arc.StartPoint.Y), new P2(arc.EndPoint.X, arc.EndPoint.Y), bulge));
                    break;
                }
                case Circle ci:
                {
                    var a = new P2(ci.Center.X + ci.Radius, ci.Center.Y);
                    var b = new P2(ci.Center.X - ci.Radius, ci.Center.Y);
                    segs.Add((a, b, 1.0));
                    segs.Add((b, a, 1.0));
                    break;
                }
                case Curve cv:
                {
                    // Elipse, spline: aproximada por retas.
                    const int n = 32;
                    double p0 = cv.StartParam, p1 = cv.EndParam;
                    Point3d ant = cv.GetPointAtParameter(p0);
                    for (int i = 1; i <= n; i++)
                    {
                        Point3d p = cv.GetPointAtParameter(p0 + ((p1 - p0) * i / n));
                        segs.Add((new P2(ant.X, ant.Y), new P2(p.X, p.Y), 0.0));
                        ant = p;
                    }
                    break;
                }
            }
        }
    }

    private static Polyline NovaPolilinha(Transaction tr, BlockTableRecord espaco, Hatch modelo, Laco laco)
    {
        var pl = new Polyline();
        pl.SetDatabaseDefaults();
        pl.Normal = modelo.Normal;
        pl.Elevation = modelo.Elevation;
        for (int i = 0; i < laco.Count; i++)
            pl.AddVertexAt(i, new Point2d(laco.Pontos[i].X, laco.Pontos[i].Y), laco.Bulges[i], 0, 0);
        pl.Closed = true;
        pl.Layer = modelo.Layer;
        pl.ColorIndex = 256;
        espaco.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        return pl;
    }

    /// <summary>Hachura vazia com o padrao, a layer, a cor e a transparencia do modelo.</summary>
    private static Hatch NovaHachura(Transaction tr, BlockTableRecord espaco, Hatch m, bool porLayer)
    {
        var h = new Hatch();
        espaco.AppendEntity(h);
        tr.AddNewlyCreatedDBObject(h, true);
        h.SetDatabaseDefaults();
        h.Layer = m.Layer;
        h.Color = porLayer ? AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256) : m.Color;
        h.Transparency = m.Transparency;
        h.Normal = m.Normal;
        h.Elevation = m.Elevation;

        if (m.IsGradient)
        {
            h.HatchObjectType = HatchObjectType.GradientObject;
            h.SetGradient(m.GradientType, m.GradientName);
            h.GradientAngle = m.GradientAngle;
            h.GradientShift = m.GradientShift;
            h.GradientOneColorMode = m.GradientOneColorMode;
            h.ShadeTintValue = m.ShadeTintValue;
            h.SetGradientColors(m.GetGradientColors());
        }
        else
        {
            // O padrao vem primeiro: angulo e escala so valem com um padrao definido, e em SOLID
            // o setter de angulo da eInvalidInput.
            bool solid = string.Equals(m.PatternName, "SOLID", StringComparison.OrdinalIgnoreCase);
            if (m.PatternType == HatchPatternType.UserDefined)
            {
                h.PatternSpace = m.PatternSpace;
                h.PatternDouble = m.PatternDouble;
            }
            h.SetHatchPattern(m.PatternType, m.PatternName);
            if (!solid)
            {
                Tentar(() => h.PatternScale = m.PatternScale);
                Tentar(() => h.PatternAngle = m.PatternAngle);
                Tentar(() => h.Origin = m.Origin);
            }
        }
        h.HatchStyle = HatchStyle.Normal;
        Tentar(() => h.BackgroundColor = m.BackgroundColor);
        return h;
    }

    /// <summary>Propriedade que o AutoCAD pode recusar para certo padrao: a hachura sai sem ela.</summary>
    private static void Tentar(Action acao)
    {
        try
        {
            acao();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            PluginLog.Diag(Modulo, $"Propriedade da hachura ignorada: {ex.ErrorStatus}");
        }
    }
}
