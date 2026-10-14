using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Elementos de intersecao: alargamento/taper, gota, conversao no canteiro,
/// abertura de canteiro, faixa livre a direita e remocao. Todos com o
/// comprimento (ou raio) arrastavel e preview da rede.
/// </summary>
internal static class ViarioElementCommands
{
    private sealed class LegPick
    {
        public AxisInput Axis = null!;
        public NodeInfo Node = null!;
        public double SNode;
        public int Dir;
        public double CrossHalf;
        public V2 Direction;
    }

    public static void Menu(Document doc)
    {
        PromptKeywordOptions options = new(
            "\nElemento [Alargamento/Gota/COnversao/ABertura/Livre/Ilha/Remover] <Alargamento>: ",
            "Alargamento Gota COnversao ABertura Livre Ilha Remover")
        {
            AllowNone = true
        };

        PromptResult result = doc.Editor.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return;

        switch (result.Status == PromptStatus.OK ? result.StringResult : "Alargamento")
        {
            case "Gota": Gota(doc); break;
            case "COnversao": Conversao(doc); break;
            case "ABertura": Abertura(doc); break;
            case "Livre": Livre(doc); break;
            case "Ilha": ViarioVertexCommands.Islands(doc); break;
            case "Remover": Remove(doc); break;
            default: Alargar(doc); break;
        }
    }

    // ------------------------------------------------------------------
    // Alargamento (faixa de conversao a direita/esquerda)
    // ------------------------------------------------------------------

    public static void Alargar(Document doc)
    {
        Editor ed = doc.Editor;
        LegPick? leg = PickLeg(doc, "\nClique no ramo que recebe o alargamento, perto da intersecao: ",
            out List<AxisInput> axes, out HashSet<string> keys);
        if (leg is null)
            return;

        string? side = Keyword(ed, "\nLado, para quem chega na intersecao [Direita/Esquerda/Ambos] <Direita>: ",
            "Direita Esquerda Ambos", "Direita");
        if (side is null)
            return;

        double? width = AskDouble(ed, "Largura da faixa adicional (m)", 3.0, false);
        if (width is null)
            return;
        double? taper = AskDouble(ed, "Comprimento do taper (m)", 20.0, true);
        if (taper is null)
            return;
        bool? smooth = AskShape(ed);
        if (smooth is null)
            return;

        AxisEvent ev = new()
        {
            Kind = AxisEventKind.Alargamento,
            S = leg.SNode,
            Dir = leg.Dir,
            Side = side == "Direita" ? 1 : side == "Esquerda" ? -1 : 0,
            Width = width.Value,
            Taper = taper.Value,
            Smooth = smooth.Value,
            Length = 30.0
        };

        V2 stopLine = leg.Node.Point + (leg.Direction * leg.CrossHalf);
        double? length = DragEvent(doc, axes, keys, leg, ev, stopLine, "Comprimento do armazenamento (a partir da pista transversal)",
            (e, v) => e.Length = Math.Max(v, 1.0));
        if (length is null)
            return;

        ev.Length = Math.Max(length.Value, 1.0);
        SaveEvent(doc, leg.Axis.Key, ev);
    }

    // ------------------------------------------------------------------
    // Gota
    // ------------------------------------------------------------------

    public static void Gota(Document doc)
    {
        Editor ed = doc.Editor;
        LegPick? leg = PickLeg(doc, "\nClique no ramo que recebe a gota, perto da intersecao: ",
            out List<AxisInput> axes, out HashSet<string> keys);
        if (leg is null)
            return;

        if (leg.Axis.Section.Canteiro > 1e-3)
        {
            ed.WriteMessage("\nEsse eixo ja tem canteiro central; a gota e para vias sem canteiro.");
            return;
        }

        double? width = AskDouble(ed, "Largura maxima da gota (m)", 2.5, false);
        if (width is null)
            return;
        double? gap = AskDouble(ed, "Recuo da ponta em relacao a pista transversal (m)", 1.5, true);
        if (gap is null)
            return;
        double? taper = AskDouble(ed, "Taper do alargamento em volta da gota (m)", 20.0, true);
        if (taper is null)
            return;

        AxisEvent ev = new()
        {
            Kind = AxisEventKind.Gota,
            S = leg.SNode,
            Dir = leg.Dir,
            Side = 0,
            Width = width.Value,
            Gap = gap.Value,
            Taper = taper.Value,
            Smooth = true,
            Length = 15.0
        };

        V2 nose = leg.Node.Point + (leg.Direction * (leg.CrossHalf + gap.Value));
        double? length = DragEvent(doc, axes, keys, leg, ev, nose, "Comprimento da gota",
            (e, v) => e.Length = Math.Max(v, width.Value + 1.0));
        if (length is null)
            return;

        ev.Length = Math.Max(length.Value, width.Value + 1.0);
        SaveEvent(doc, leg.Axis.Key, ev);
    }

    // ------------------------------------------------------------------
    // Conversao a esquerda tirada do canteiro (baia)
    // ------------------------------------------------------------------

    public static void Conversao(Document doc)
    {
        Editor ed = doc.Editor;
        LegPick? leg = PickLeg(doc, "\nClique na aproximacao (via com canteiro), perto da intersecao: ",
            out List<AxisInput> axes, out HashSet<string> keys);
        if (leg is null)
            return;

        double canteiro = leg.Axis.Section.Canteiro;
        if (canteiro <= 1e-3)
        {
            ed.WriteMessage("\nEsse eixo nao tem canteiro central. Use Alargamento > Esquerda para criar a faixa de conversao.");
            return;
        }

        string? side = Keyword(ed, "\nLado do canteiro, para quem chega [Esquerda/Direita] <Esquerda>: ",
            "Esquerda Direita", "Esquerda");
        if (side is null)
            return;

        double? width = AskDouble(ed, $"Largura da faixa de conversao (m, canteiro tem {N(canteiro)} m)",
            Math.Min(3.0, Math.Max(canteiro - 0.6, 0.5)), false);
        if (width is null)
            return;
        double? taper = AskDouble(ed, "Comprimento do taper (m)", 20.0, true);
        if (taper is null)
            return;

        AxisEvent ev = new()
        {
            Kind = AxisEventKind.Baia,
            S = leg.SNode,
            Dir = leg.Dir,
            Side = side == "Direita" ? 1 : -1,
            Width = width.Value,
            Taper = taper.Value,
            Smooth = true,
            Length = 30.0
        };

        V2 stopLine = leg.Node.Point + (leg.Direction * leg.CrossHalf);
        double? length = DragEvent(doc, axes, keys, leg, ev, stopLine, "Comprimento do armazenamento",
            (e, v) => e.Length = Math.Max(v, 1.0));
        if (length is null)
            return;

        ev.Length = Math.Max(length.Value, 1.0);
        SaveEvent(doc, leg.Axis.Key, ev);
    }

    // ------------------------------------------------------------------
    // Abertura de canteiro (retorno)
    // ------------------------------------------------------------------

    public static void Abertura(Document doc)
    {
        Editor ed = doc.Editor;
        PromptPointResult pick = ed.GetPoint(new PromptPointOptions("\nClique no eixo com canteiro, no centro da abertura: "));
        if (pick.Status != PromptStatus.OK)
            return;

        V2 p = ToWcs(ed, pick.Value);
        (List<AxisInput> axes, _, HashSet<string> keys) = ViarioSync.ComputeCurrent(doc.Database);
        AxisInput? axis = axes
            .Where(a => a.Section.Canteiro > 1e-3)
            .OrderBy(a => a.Path.Project(p).Dist)
            .FirstOrDefault();

        if (axis is null || axis.Path.Project(p).Dist > axis.Section.EdgeOffset(EdgeLevel.Pista) + 5.0)
        {
            ed.WriteMessage("\nNenhum eixo com canteiro perto do ponto.");
            return;
        }

        (double s, V2 center, _) = axis.Path.Project(p);
        AxisEvent ev = new() { Kind = AxisEventKind.Abertura, S = s, Length = 12.0 };
        EngineSettings engine = ViarioSync.EngineSettingsFor(doc.Database);

        ValueJig jig = new("Metade do comprimento da abertura", keys, center, 6.0, v =>
        {
            AxisEvent e = ev.Clone();
            e.Length = Math.Max(2.0 * v, 1.0);
            return (WithEvent(axes, axis.Key, e), engine);
        });

        PromptResult result = ed.Drag(jig);
        if (jig.ToDefault || (result.Status != PromptStatus.OK && result.Status != PromptStatus.None))
            return;

        ev.Length = Math.Max(2.0 * jig.Value, 1.0);
        SaveEvent(doc, axis.Key, ev);
    }

    // ------------------------------------------------------------------
    // Faixa livre a direita (esquina)
    // ------------------------------------------------------------------

    public static void Livre(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult pick = ed.GetPoint(new PromptPointOptions("\nClique perto da esquina que recebe a faixa livre: "));
        if (pick.Status != PromptStatus.OK)
            return;

        V2 p = ToWcs(ed, pick.Value);
        (List<AxisInput> axes, ViarioResult current, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);
        CornerInfo? corner = current.Corners
            .OrderBy(c => V2.Dist(c.Corner, p))
            .FirstOrDefault();

        if (corner is null || V2.Dist(corner.Corner, p) > 40.0)
        {
            ed.WriteMessage("\nNenhuma esquina perto do ponto clicado.");
            return;
        }

        double? width = AskDouble(ed, "Largura da faixa livre (m)", 5.0, false);
        if (width is null)
            return;
        double? setback = AskDouble(ed, "Afastamento da ilha em relacao aos bordos (m)", 0.6, true);
        if (setback is null)
            return;

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        double initial = Math.Max(corner.Radius, 25.0);

        ValueJig jig = new("Raio do meio-fio externo da faixa livre", keys, corner.Corner, initial, v =>
        {
            EngineSettings e = engine.Clone();
            e.SlipLanes[corner.Key] = new SlipLane { Radius = Math.Max(v, 1.0), Width = width.Value, Setback = setback.Value };
            return (axes, e);
        });

        PromptResult result = ed.Drag(jig);
        Dictionary<string, SlipLane> slips = ViarioStore.LoadSlipLanes(db);

        if (jig.ToDefault)
        {
            slips.Remove(corner.Key);
        }
        else if (result.Status == PromptStatus.OK || result.Status == PromptStatus.None)
        {
            slips[corner.Key] = new SlipLane
            {
                Radius = Math.Max(result.Status == PromptStatus.OK ? jig.Value : initial, 1.0),
                Width = width.Value,
                Setback = setback.Value
            };
        }
        else
        {
            return;
        }

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveSlipLanes(tr, db, slips);
            tr.Commit();
        }

        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Remover elemento
    // ------------------------------------------------------------------

    public static void Remove(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult pick = ed.GetPoint(new PromptPointOptions("\nClique perto do elemento a remover (alargamento, gota, conversao, abertura ou faixa livre): "));
        if (pick.Status != PromptStatus.OK)
            return;

        V2 p = ToWcs(ed, pick.Value);
        (List<AxisInput> axes, ViarioResult current, _) = ViarioSync.ComputeCurrent(db);
        Dictionary<string, SlipLane> slips = ViarioStore.LoadSlipLanes(db);

        double best = 40.0;
        string? bestAxis = null;
        AxisEvent? bestEvent = null;
        string? bestSlip = null;

        foreach (AxisInput axis in axes)
        {
            foreach (AxisEvent ev in axis.Events)
            {
                double along = ev.Kind == AxisEventKind.Abertura ? 0.0 : ev.Gap + (ev.Length / 2.0) + 5.0;
                double s = Math.Clamp(ev.S + (ev.Dir * along), 0.0, axis.Path.Length);
                double d = V2.Dist(axis.Path.PointAt(s), p);
                if (d < best)
                {
                    best = d;
                    bestAxis = axis.Key;
                    bestEvent = ev;
                    bestSlip = null;
                }
            }
        }

        foreach (CornerInfo corner in current.Corners.Where(c => slips.ContainsKey(c.Key)))
        {
            double d = V2.Dist(corner.Corner, p);
            if (d < best)
            {
                best = d;
                bestSlip = corner.Key;
                bestEvent = null;
            }
        }

        if (bestSlip is null && bestEvent is null)
        {
            ed.WriteMessage("\nNenhum elemento perto do ponto.");
            return;
        }

        string description = bestSlip is not null ? "faixa livre a direita" : bestEvent!.Describe();
        string? confirm = Keyword(ed, "\nRemover " + description + "? [Sim/Nao] <Sim>: ", "Sim Nao", "Sim");
        if (confirm != "Sim")
            return;

        if (bestSlip is not null)
        {
            slips.Remove(bestSlip);
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            ViarioStore.SaveSlipLanes(tr, db, slips);
            tr.Commit();
        }
        else
        {
            AxisEvent target = bestEvent!;
            ModifyAxisEvents(doc, bestAxis!, events =>
                events.RemoveAll(e => e.Serialize() == target.Serialize()));
        }

        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Comum
    // ------------------------------------------------------------------

    private static LegPick? PickLeg(Document doc, string message, out List<AxisInput> axes, out HashSet<string> keys)
    {
        Editor ed = doc.Editor;
        axes = new List<AxisInput>();
        keys = new HashSet<string>();

        PromptPointResult pick = ed.GetPoint(new PromptPointOptions(message));
        if (pick.Status != PromptStatus.OK)
            return null;

        V2 p = ToWcs(ed, pick.Value);
        (List<AxisInput> list, ViarioResult result, HashSet<string> existing) = ViarioSync.ComputeCurrent(doc.Database);
        axes = list;
        keys = existing;

        AxisInput? axis = list.OrderBy(a => a.Path.Project(p).Dist).FirstOrDefault();
        if (axis is null || axis.Path.Project(p).Dist > axis.Section.EdgeOffset(EdgeLevel.Alinhamento) + 5.0)
        {
            ed.WriteMessage("\nNenhum eixo da rede perto do ponto.");
            return null;
        }

        double sClick = axis.Path.Project(p).S;
        double length = axis.Path.Length;

        NodeInfo? node = null;
        double sNode = 0, bestDelta = double.MaxValue;
        foreach (NodeInfo n in result.Nodes.Where(n => n.AxisKeys.Contains(axis.Key)))
        {
            double s = axis.Path.Project(n.Point).S;
            double delta = Math.Abs(Signed(sClick - s, length, axis.Path.Closed));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                node = n;
                sNode = s;
            }
        }

        if (node is null)
        {
            ed.WriteMessage("\nEsse eixo nao tem intersecao.");
            return null;
        }

        int dir = Signed(sClick - sNode, length, axis.Path.Closed) >= 0 ? 1 : -1;
        V2 tangent = axis.Path.TangentAt(Math.Clamp(sNode, 0, length));
        double crossHalf = node.AxisKeys
            .Where(k => k != axis.Key)
            .Select(k => list.FirstOrDefault(a => a.Key == k))
            .Where(a => a is not null)
            .Select(a => a!.Section.EdgeOffset(EdgeLevel.Pista))
            .DefaultIfEmpty(0.0)
            .Max();

        return new LegPick
        {
            Axis = axis,
            Node = node,
            SNode = sNode,
            Dir = dir,
            CrossHalf = crossHalf,
            Direction = tangent * dir
        };
    }

    private static double Signed(double delta, double length, bool closed)
    {
        if (!closed || length <= 0)
            return delta;
        delta %= length;
        if (delta > length / 2.0)
            delta -= length;
        if (delta < -length / 2.0)
            delta += length;
        return delta;
    }

    /// <summary>Arrasta o comprimento do elemento a partir do ponto base, com preview.</summary>
    private static double? DragEvent(Document doc, List<AxisInput> axes, HashSet<string> keys, LegPick leg, AxisEvent ev,
        V2 basePoint, string label, Action<AxisEvent, double> apply)
    {
        EngineSettings engine = ViarioSync.EngineSettingsFor(doc.Database);
        ValueJig jig = new(label, keys, basePoint, ev.Length, v =>
        {
            AxisEvent e = ev.Clone();
            apply(e, v);
            return (WithEvent(axes, leg.Axis.Key, e), engine);
        });

        PromptResult result = doc.Editor.Drag(jig);
        if (jig.ToDefault)
            return ev.Length;
        if (result.Status == PromptStatus.None)
            return ev.Length;
        return result.Status == PromptStatus.OK ? jig.Value : null;
    }

    private static List<AxisInput> WithEvent(List<AxisInput> axes, string key, AxisEvent ev) =>
        axes.Select(a => a.Key == key ? a.With(a.Events.Append(ev).ToList()) : a).ToList();

    private static void SaveEvent(Document doc, string axisKey, AxisEvent ev)
    {
        ModifyAxisEvents(doc, axisKey, events => events.Add(ev));
        ViarioResult? result = ViarioSync.Regenerate(doc, quiet: true);
        doc.Editor.WriteMessage("\n  Elemento criado: " + ev.Describe() + ".");
        if (result is not null)
            ViarioSync.WriteWarnings(doc, result);
    }

    private static void ModifyAxisEvents(Document doc, string axisKey, Action<List<AxisEvent>> change)
    {
        AcadDb.Database db = doc.Database;
        AcadDb.ObjectId id = db.GetObjectId(false, new AcadDb.Handle(Convert.ToInt64(axisKey, 16)), 0);

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.Entity entity = (AcadDb.Entity)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
        if (!ViarioStore.ReadAxis(entity, out string sectionId, out double? radius, out List<AxisEvent> events))
            return;

        change(events);
        ViarioStore.SetAxis(entity, sectionId, radius, events);
        tr.Commit();
    }

    private static V2 ToWcs(Editor ed, Point3d ucs)
    {
        Point3d w = ucs.TransformBy(ed.CurrentUserCoordinateSystem);
        return new V2(w.X, w.Y);
    }

    private static string? Keyword(Editor ed, string message, string keywords, string fallback)
    {
        PromptKeywordOptions options = new(message, keywords) { AllowNone = true };
        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.None)
            return fallback;
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static bool? AskShape(Editor ed)
    {
        string? shape = Keyword(ed, "\nFormato do taper [Suave/Reto] <Suave>: ", "Suave Reto", "Suave");
        return shape is null ? null : shape == "Suave";
    }

    private static double? AskDouble(Editor ed, string label, double current, bool allowZero)
    {
        PromptDoubleOptions options = new("\n" + label + " <" + N(current) + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = allowZero
        };

        PromptDoubleResult result = ed.GetDouble(options);
        if (result.Status == PromptStatus.None)
            return current;
        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
