using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Raio em vertice de eixo (curva do proprio eixo, ex.: quadra) e ilhas eliminadas.
/// </summary>
internal static class ViarioVertexCommands
{
    // VertexPick e a geometria pura ficam em ViarioVertexGeometry.cs.

    // ------------------------------------------------------------------
    // Vertice do eixo
    // ------------------------------------------------------------------

    /// <summary>Procura o vertice de eixo mais proximo do ponto.</summary>
    public static VertexPick? FindVertex(AcadDb.Database db, IReadOnlyList<AxisInput> axes, V2 p, double maxDist)
    {
        VertexPick? best = null;
        double bestDist = maxDist;
        Dictionary<string, AxisInput> byKey = axes.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (AxisInput axis in axes)
        {
            AcadDb.Handle handle;
            try
            {
                handle = new AcadDb.Handle(Convert.ToInt64(axis.Key, 16));
            }
            catch
            {
                continue;
            }

            if (!db.TryGetObjectId(handle, out AcadDb.ObjectId id) || id.IsErased)
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Polyline pl)
                continue;

            List<(V2 P, double Bulge)> verts = new();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                Point2d q = pl.GetPoint2dAt(i);
                verts.Add((new V2(q.X, q.Y), pl.GetBulgeAt(i)));
            }

            int n = verts.Count;
            bool closed = pl.Closed;
            double face = axis.Section.EdgeOffset(EdgeLevel.FaceMF);

            for (int i = 0; i < n; i++)
            {
                VertexPick? candidate = ViarioVertexGeometry.CornerAt(verts, closed, i);
                if (candidate is null)
                    continue;

                double d = V2.Dist(candidate.Pi, p);
                if (candidate.HasArc)
                    d = Math.Min(d, new Seg(verts[i].P, verts[(i + 1) % n].P, verts[i].Bulge).Closest(p).Dist);
                if (d >= bestDist)
                    continue;

                candidate.Id = id;
                candidate.AxisKey = axis.Key;
                candidate.Vertices = verts;
                candidate.Closed = closed;
                candidate.Face = face;
                candidate.Distance = d;
                best = candidate;
                bestDist = d;
            }
        }

        return best;
    }

    /// <summary>Raio de um vertice do eixo com preview; grava na propria polilinha.</summary>
    public static void EditVertex(Document doc, VertexPick v, V2 click, List<AxisInput> axes, EngineSettings engine, HashSet<string> keys)
    {
        Editor ed = doc.Editor;
        bool inner = ViarioVertexGeometry.InnerSide(v, click);
        double current = ViarioVertexGeometry.FaceRadiusOf(v, inner);
        ed.WriteMessage($"\n  Vertice do eixo ({(inner ? "lado de dentro" : "lado de fora")} da curva): raio atual na face do meio-fio {current:0.##} m" +
                        (v.AxisRadius > 0 ? $" (eixo R={v.AxisRadius:0.##} m)." : " (canto vivo)."));

        string? lastError = null;
        ValueJig jig = new("Raio na face do meio-fio", keys, v.Pi, current, r =>
        {
            List<(V2 P, double Bulge)>? verts = ViarioVertexGeometry.Apply(v, Math.Max(ViarioVertexGeometry.AxisRadiusFor(v, inner, r), 0.0), out lastError);
            if (verts is null)
                return (axes, engine);
            GPath path = ViarioVertexGeometry.PathOf(verts, v.Closed);
            return (axes.Select(a => a.Key == v.AxisKey ? new AxisInput(a.Key, path, a.Section, a.Radius, a.Events) : a).ToList(), engine);
        });

        PromptResult result = ed.Drag(jig);
        double face;
        if (jig.ToDefault)
            face = 0.0;
        else if (result.Status == PromptStatus.OK)
            face = jig.Value;
        else
            return;

        double axisRadius = face <= 1e-6 ? 0.0 : ViarioVertexGeometry.AxisRadiusFor(v, inner, face);
        if (face > 1e-6 && axisRadius <= 1e-6)
        {
            ed.WriteMessage($"\n  Do lado de fora o raio precisa ser maior que {v.Face:0.##} m (meia largura ate a face).");
            return;
        }

        List<(V2 P, double Bulge)>? final = ViarioVertexGeometry.Apply(v, axisRadius, out string? error);
        if (final is null)
        {
            ed.WriteMessage("\n  " + error);
            return;
        }

        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            WriteVertices((AcadDb.Polyline)tr.GetObject((AcadDb.ObjectId)v.Id!, AcadDb.OpenMode.ForWrite), final);
            tr.Commit();
        }

        ed.WriteMessage(face <= 1e-6
            ? "\n  Vertice voltou a canto vivo."
            : $"\n  Eixo concordado: R={axisRadius:0.##} m no eixo, {face:0.##} m na face do meio-fio do lado clicado.");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    /// <summary>Cantos de eixo (vertices) que formam a ilha: o lado de dentro da ilha de cada canto.</summary>
    internal sealed class IslandVertices
    {
        public AcadDb.ObjectId Id;
        public string AxisKey = "";
        public List<(V2 P, double Bulge)> Vertices = new();
        public bool Closed;
        public double Face;
        public List<(V2 Pi, bool Inner, double CurrentFace)> Corners = new();
    }

    public static List<IslandVertices> FindIslandVertices(AcadDb.Database db, IReadOnlyList<AxisInput> axes, IReadOnlyList<Seg> ring)
    {
        List<IslandVertices> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (AxisInput axis in axes)
        {
            AcadDb.ObjectId id;
            try
            {
                if (!db.TryGetObjectId(new AcadDb.Handle(Convert.ToInt64(axis.Key, 16)), out id) || id.IsErased)
                    continue;
            }
            catch
            {
                continue;
            }

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Polyline pl)
                continue;

            List<(V2 P, double Bulge)> verts = new();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                Point2d q = pl.GetPoint2dAt(i);
                verts.Add((new V2(q.X, q.Y), pl.GetBulgeAt(i)));
            }

            double face = axis.Section.EdgeOffset(EdgeLevel.FaceMF);
            IslandVertices item = new() { Id = id, AxisKey = axis.Key, Vertices = verts, Closed = pl.Closed, Face = face };
            for (int i = 0; i < verts.Count; i++)
            {
                VertexPick? c = ViarioVertexGeometry.CornerAt(verts, pl.Closed, i);
                if (c is null)
                    continue;

                // Ponto um pouco alem da face, para cada lado da bissetriz: o que cai dentro da ilha.
                V2 d1 = (c.Pi - c.Prev).Unit();
                V2 d2 = (c.Next - c.Pi).Unit();
                V2 bis = (d2 - d1).Length < 1e-9 ? new V2(-d1.Y, d1.X) : (d2 - d1).Unit();   // aponta para dentro da curva
                double reach = (face + 0.5) / Math.Max(Math.Sin((Math.PI - c.Deflection) / 2.0), 0.2);
                c.Face = face;
                double radiusFace(bool inner) => ViarioVertexGeometry.FaceRadiusOf(c, inner);
                V2 inside = c.Pi + (bis * (reach + Math.Max(c.AxisRadius, 0.0)));
                V2 outside = c.Pi - (bis * reach);
                if (ViarioEngine.RingContains(ring, inside))
                    item.Corners.Add((c.Pi, true, radiusFace(true)));
                else if (ViarioEngine.RingContains(ring, outside))
                    item.Corners.Add((c.Pi, false, radiusFace(false)));
            }

            if (item.Corners.Count > 0)
                list.Add(item);
        }

        return list;
    }

    /// <summary>Regrava os vertices da polilinha (mesma entidade, mesmo handle e XData).</summary>
    public static void WriteVertices(AcadDb.Polyline pl, List<(V2 P, double Bulge)> verts)
    {
        double elevation = pl.Elevation;
        while (pl.NumberOfVertices > verts.Count)
            pl.RemoveVertexAt(pl.NumberOfVertices - 1);
        for (int i = 0; i < verts.Count; i++)
        {
            Point2d q = new(verts[i].P.X, verts[i].P.Y);
            if (i < pl.NumberOfVertices)
            {
                pl.SetPointAt(i, q);
                pl.SetBulgeAt(i, verts[i].Bulge);
            }
            else
            {
                pl.AddVertexAt(i, q, verts[i].Bulge, 0, 0);
            }
        }

        pl.Elevation = elevation;
    }

    // ------------------------------------------------------------------
    // Ilhas eliminadas
    // ------------------------------------------------------------------

    private static readonly EdgeLevel[] IslandLevels = { EdgeLevel.FaceMF, EdgeLevel.Canteiro, EdgeLevel.Ilha };

    /// <summary>C3DVIARIOILHA: elimina (ou restaura) a ilha clicada.</summary>
    public static void Islands(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointOptions options = new("\nClique dentro da ilha a eliminar ou [Restaurar/Todas]: ");
        options.Keywords.Add("Restaurar");
        options.Keywords.Add("Todas");
        options.AppendKeywordsToMessage = false;
        PromptPointResult pick = ed.GetPoint(options);

        List<V2> seeds = ViarioStore.LoadRemovedIslands(db);

        if (pick.Status == PromptStatus.Keyword && pick.StringResult == "Todas")
        {
            if (seeds.Count == 0)
            {
                ed.WriteMessage("\n  Nenhuma ilha eliminada.");
                return;
            }

            Save(doc, new List<V2>());
            ed.WriteMessage($"\n  {seeds.Count} ilha(s) restaurada(s).");
            ViarioSync.Regenerate(doc, quiet: false);
            return;
        }

        if (pick.Status == PromptStatus.Keyword)
        {
            PromptPointResult where = ed.GetPoint(new PromptPointOptions("\nClique dentro da ilha eliminada: "));
            if (where.Status != PromptStatus.OK)
                return;

            V2 q = ToWcs(ed, where.Value);
            int index = FindSeedFor(db, seeds, q);
            if (index < 0)
            {
                ed.WriteMessage("\n  Nenhuma ilha eliminada nesse ponto.");
                return;
            }

            seeds.RemoveAt(index);
            Save(doc, seeds);
            ed.WriteMessage("\n  Ilha restaurada.");
            ViarioSync.Regenerate(doc, quiet: false);
            return;
        }

        if (pick.Status != PromptStatus.OK)
            return;

        V2 p = ToWcs(ed, pick.Value);
        (_, ViarioResult current, _) = ViarioSync.ComputeCurrent(db);
        OutputLine? ring = current.Lines
            .Where(l => l.Closed && IslandLevels.Contains(l.Level) && ViarioEngine.RingContains(l.Segs, p))
            .OrderBy(l => ViarioEngine.RingArea(l.Segs))
            .FirstOrDefault();

        if (ring is null)
        {
            ed.WriteMessage("\n  O ponto nao esta dentro de uma ilha (anel fechado de meio-fio, canteiro ou gota).");
            return;
        }

        double area = ViarioEngine.RingArea(ring.Segs);
        if (area > 5000.0)
        {
            PromptKeywordOptions confirm = new($"\nEsse anel tem {area:0} m2 (parece uma quadra). Eliminar mesmo assim? [Sim/Nao] <Nao>: ", "Sim Nao")
            {
                AllowNone = true
            };
            PromptResult c = ed.GetKeywords(confirm);
            if (c.Status != PromptStatus.OK || c.StringResult != "Sim")
                return;
        }

        seeds.Add(p);
        Save(doc, seeds);
        ed.WriteMessage($"\n  Ilha eliminada ({area:0.#} m2 viram pavimento). Para voltar: {C3DCommands.Viario.Ilha} > Restaurar.");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    /// <summary>Semente cuja ilha (calculada sem ela) contem o ponto; senao a mais proxima ate 10 m.</summary>
    private static int FindSeedFor(AcadDb.Database db, List<V2> seeds, V2 q)
    {
        if (seeds.Count == 0)
            return -1;

        (List<AxisInput> axes, _, _) = ViarioSync.ComputeCurrent(db);
        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        for (int i = 0; i < seeds.Count; i++)
        {
            EngineSettings without = engine.Clone();
            without.RemovedIslands.RemoveAt(i);
            ViarioResult r = ViarioEngine.Compute(axes, without);
            OutputLine? ring = r.Lines
                .Where(l => l.Closed && IslandLevels.Contains(l.Level) && ViarioEngine.RingContains(l.Segs, seeds[i]))
                .OrderBy(l => ViarioEngine.RingArea(l.Segs))
                .FirstOrDefault();
            if (ring is not null && ViarioEngine.RingContains(ring.Segs, q))
                return i;
        }

        int nearest = Enumerable.Range(0, seeds.Count).OrderBy(i => V2.Dist(seeds[i], q)).First();
        return V2.Dist(seeds[nearest], q) <= 10.0 ? nearest : -1;
    }

    private static void Save(Document doc, List<V2> seeds)
    {
        using AcadDb.Transaction tr = doc.Database.TransactionManager.StartTransaction();
        ViarioStore.SaveRemovedIslands(tr, doc.Database, seeds);
        tr.Commit();
    }

    private static V2 ToWcs(Editor ed, Point3d p)
    {
        Point3d w = p.TransformBy(ed.CurrentUserCoordinateSystem);
        return new V2(w.X, w.Y);
    }
}
