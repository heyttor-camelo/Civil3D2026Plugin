using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Preview da rede enquanto um eixo e arrastado pelos grips (ou por STRETCH/MOVE).
///
/// Durante o arraste o AutoCAD desenha um clone (sem ObjectId) do eixo com a
/// geometria provisoria. Este overrule intercepta esse desenho: identifica a
/// qual eixo o clone corresponde, recalcula a rede com a geometria provisoria no
/// lugar dele e desenha o que vai mudar, como o jig do lancamento. Em eixos com
/// concordancia mantida, a cadeia e refeita antes, e o eixo resolvido aparece
/// junto. Ao soltar, a regeneracao normal (fim do comando) grava o resultado.
/// </summary>
internal sealed class ViarioGripPreview : DrawableOverrule
{
    private static ViarioGripPreview? _instance;
    private static bool _busy;

    private sealed class Cache
    {
        public AcadDb.Database Db = null!;
        public string Command = string.Empty;
        public DateTime At;
        public List<AxisInput> Axes = new();
        public HashSet<string> Keys = new();
        public EngineSettings Settings = new();
    }

    private static Cache? _cache;

    public static void Register()
    {
        if (_instance is not null)
            return;

        _instance = new ViarioGripPreview();
        _instance.SetXDataFilter(ViarioStore.RegApp);
        Overrule.AddOverrule(RXObject.GetClass(typeof(AcadDb.Polyline)), _instance, false);
        Overrule.Overruling = true;
    }

    public static void Unregister()
    {
        if (_instance is null)
            return;

        try
        {
            Overrule.RemoveOverrule(RXObject.GetClass(typeof(AcadDb.Polyline)), _instance);
        }
        catch
        {
            // Encerramento.
        }

        _instance = null;
        _cache = null;
    }

    /// <summary>A rede mudou: o proximo arraste recolhe os eixos de novo.</summary>
    public static void Invalidate() => _cache = null;

    public override bool WorldDraw(Drawable drawable, WorldDraw wd)
    {
        bool result = base.WorldDraw(drawable, wd);
        if (_busy || ViarioSync.Suspended)
            return result;

        try
        {
            if (drawable is not AcadDb.Polyline pl || !pl.ObjectId.IsNull || pl.Closed)
                return result; // so o clone provisorio do arraste (sem ObjectId)

            if (!ViarioStore.IsAxis(pl, out _))
                return result;

            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null || !IsDragCommand(doc.CommandInProgress))
                return result;

            _busy = true;
            Preview(doc, pl, wd);
        }
        catch
        {
            // Preview nunca derruba o arraste.
        }
        finally
        {
            _busy = false;
        }

        return result;
    }

    private static bool IsDragCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
            return false;
        string c = command.ToUpperInvariant();
        return c.Contains("GRIP") || c.Contains("STRETCH") || c.Contains("MOVE") || c.Contains("ROTATE") ||
               c.Contains("SCALE") || c.Contains("MIRROR");
    }

    private static Cache Current(Document doc)
    {
        AcadDb.Database db = doc.Database;
        string command = doc.CommandInProgress ?? string.Empty;
        if (_cache is not null && ReferenceEquals(_cache.Db, db) && _cache.Command == command &&
            (DateTime.UtcNow - _cache.At).TotalSeconds < 20)
        {
            return _cache;
        }

        (List<AxisInput> axes, _, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);
        _cache = new Cache
        {
            Db = db,
            Command = command,
            At = DateTime.UtcNow,
            Axes = axes,
            Keys = keys,
            Settings = ViarioSync.EngineSettingsFor(db)
        };
        return _cache;
    }

    private static void Preview(Document doc, AcadDb.Polyline clone, WorldDraw wd)
    {
        Cache cache = Current(doc);
        if (cache.Axes.Count == 0)
            return;

        (List<V2> pts, List<double> bulges) = ViarioStore.Vertices(clone);
        if (pts.Count < 2)
            return;

        AxisInput? target = Match(cache.Axes, pts, bulges);
        if (target is null)
            return;

        // Concordancia mantida: a cadeia e refeita a partir dos vertices provisorios.
        if (target.Chained && pts.Count > 2)
        {
            (pts, bulges, bool changed) = ViarioChain.Solve(pts, bulges);
            if (changed)
            {
                using AcadDb.Polyline solved = ViarioStore.ToPolyline(AxisJig.PathOf(pts, bulges).Segs, closed: false);
                solved.ColorIndex = 1;
                wd.Geometry.Draw(solved);
            }
        }

        GPath path = AxisJig.PathOf(pts, bulges);
        if (path.IsEmpty)
            return;

        List<AxisInput> axes = new(cache.Axes.Count);
        Box2 box = path.Bounds().Union(target.Path.Bounds()).Inflate(200.0);
        foreach (AxisInput a in cache.Axes)
        {
            if (a.Key == target.Key)
                axes.Add(a.With(path));
            else if (cache.Axes.Count <= 150 || a.Path.Bounds().Intersects(box))
                axes.Add(a);
        }

        ViarioResult result = ViarioEngine.Compute(axes, cache.Settings);
        ViarioPreview.DrawChanges(wd, result, cache.Keys);
    }

    /// <summary>
    /// Qual eixo o clone representa: mesmo numero de trechos e o maior numero de
    /// vertices coincidentes (num arraste de grip, todos menos um coincidem);
    /// se nada coincide (MOVE do eixo inteiro), o de mesma forma.
    /// </summary>
    private static AxisInput? Match(List<AxisInput> axes, List<V2> pts, List<double> bulges)
    {
        AxisInput? best = null;
        double bestScore = 0;
        foreach (AxisInput a in axes)
        {
            if (a.Path.Closed || a.Path.Segs.Count != pts.Count - 1)
                continue;

            List<V2> av = new() { a.Path.Segs[0].A };
            av.AddRange(a.Path.Segs.Select(s => s.B));

            int same = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (V2.Dist(av[i], pts[i]) < 1e-6)
                    same++;
            }

            bool sameShape = true;
            for (int i = 0; i < a.Path.Segs.Count && sameShape; i++)
            {
                double la = a.Path.Segs[i].Length;
                double lb = V2.Dist(pts[i], pts[i + 1]);
                double ba = a.Path.Segs[i].IsArc ? a.Path.Segs[i].Bulge : 0.0;
                double bb = i < bulges.Count ? bulges[i] : 0.0;
                sameShape = Math.Abs(la - (a.Path.Segs[i].IsArc ? la : lb)) < 1e-3 && Math.Abs(ba - bb) < 1e-6;
            }

            double score = (same * 10.0) + (sameShape ? 5.0 : 0.0) + 1.0;
            if (score > bestScore)
            {
                bestScore = score;
                best = a;
            }
        }

        // Exige alguma evidencia: vertice coincidente ou mesma forma.
        return bestScore >= 5.0 ? best : null;
    }
}
