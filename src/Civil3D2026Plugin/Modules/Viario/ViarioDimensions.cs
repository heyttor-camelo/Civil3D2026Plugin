using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// C3DVIARIOCOTAR: cota a secao em planta com um clique sobre o eixo.
///
/// Enquanto o mouse anda sobre um eixo, a cadeia de cotas aparece na estaca sob o
/// cursor (alinhadas, perpendiculares ao eixo: calcada, meio-fio + sarjeta, cada
/// faixa, canteiro, e a largura total numa segunda linha). O clique grava.
/// As posicoes vem das linhas geradas naquela estaca (alargamentos entram); as
/// faixas, da secao. Estilo e escala de cota ficam gravados no desenho
/// (Config); layer VIARIO-COTAS; [Remover] apaga todas as cotas do viario.
/// </summary>
internal static class ViarioDimensions
{
    public const string Layer = ViarioStore.LayerDims;
    public const string TagDim = "COTA";

    /// <summary>Lembrado na sessao: cotar sarjeta e meio-fio separados.</summary>
    private static bool _detail;

    public static bool Detail
    {
        get => _detail;
        set => _detail = value;
    }

    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
        if (axes.Count == 0)
        {
            ed.WriteMessage("\n  Nao ha eixos na rede.");
            return;
        }

        while (true)
        {
            ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
            DimensionJig jig = new(db, axes, result, settings, _detail);
            PromptResult drag = ed.Drag(jig);

            if (drag.Status == PromptStatus.Keyword)
            {
                switch (jig.Keyword)
                {
                    case "Remover":
                        RemovePrompt(doc);
                        break;
                    case "Detalhe":
                        _detail = !_detail;
                        ed.WriteMessage(_detail ? "\n  Sarjeta e meio-fio em cotas separadas." : "\n  Sarjeta e meio-fio numa cota so.");
                        break;
                    case "Estilo":
                        ChooseStyle(doc, settings);
                        break;
                    case "Escala":
                        ChooseScale(doc, settings);
                        break;
                }

                continue;
            }

            if (drag.Status != PromptStatus.OK || jig.Axis is null)
                return;

            int count = Place(doc, jig.Axis, jig.Station, result, _detail, settings);
            ed.WriteMessage($"\n  {count} cota(s) na estaca {Estaca(jig.Station)} de {jig.Axis.Key} (layer {Layer}, estilo {StyleName(db, settings)}" +
                            (settings.CotaEscala > 0 ? $", escala {N(settings.CotaEscala)}" : "") + ").");
        }
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Estaca(double s)
    {
        int e = (int)Math.Floor((s / 20.0) + 1e-9);
        return $"{e}+{(s - (20.0 * e)).ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    // ------------------------------------------------------------------
    // Estilo e escala
    // ------------------------------------------------------------------

    /// <summary>Estilos de cota do desenho (nomes).</summary>
    public static List<string> StyleNames(AcadDb.Database db)
    {
        List<string> names = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DimStyleTable table = (AcadDb.DimStyleTable)tr.GetObject(db.DimStyleTableId, AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in table)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is AcadDb.DimStyleTableRecord r && !r.Name.Contains('$'))
                names.Add(r.Name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>ObjectId do estilo configurado (ou o corrente se nao existir).</summary>
    public static AcadDb.ObjectId StyleId(AcadDb.Database db, ViarioStore.Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CotaEstilo))
            return db.Dimstyle;
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DimStyleTable table = (AcadDb.DimStyleTable)tr.GetObject(db.DimStyleTableId, AcadDb.OpenMode.ForRead);
        return table.Has(settings.CotaEstilo) ? table[settings.CotaEstilo] : db.Dimstyle;
    }

    public static string StyleName(AcadDb.Database db, ViarioStore.Settings settings)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        return tr.GetObject(StyleId(db, settings), AcadDb.OpenMode.ForRead) is AcadDb.DimStyleTableRecord r ? r.Name : "corrente";
    }

    private static void ChooseStyle(Document doc, ViarioStore.Settings settings)
    {
        Editor ed = doc.Editor;
        List<string> names = StyleNames(doc.Database);
        ed.WriteMessage("\n  Estilos de cota: " + string.Join(", ", names));
        PromptStringOptions opt = new("\nEstilo de cota (vazio = corrente) <" + (settings.CotaEstilo.Length > 0 ? settings.CotaEstilo : "corrente") + ">: ")
        {
            AllowSpaces = true
        };
        PromptResult r = ed.GetString(opt);
        if (r.Status != PromptStatus.OK)
            return;
        string text = r.StringResult.Trim();
        if (text.Length > 0 && !names.Any(n => n.Equals(text, StringComparison.OrdinalIgnoreCase)))
        {
            ed.WriteMessage("\n  Estilo nao encontrado.");
            return;
        }

        settings.CotaEstilo = names.FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase)) ?? "";
        Save(doc.Database, settings);
    }

    private static void ChooseScale(Document doc, ViarioStore.Settings settings)
    {
        PromptDoubleOptions opt = new("\nEscala das cotas, DIMSCALE (0 = a do estilo/desenho) <" + N(settings.CotaEscala) + ">: ")
        {
            AllowNegative = false,
            AllowZero = true,
            AllowNone = true
        };
        PromptDoubleResult r = doc.Editor.GetDouble(opt);
        if (r.Status == PromptStatus.None)
            return;
        if (r.Status != PromptStatus.OK)
            return;
        settings.CotaEscala = r.Value;
        Save(doc.Database, settings);
    }

    private static void Save(AcadDb.Database db, ViarioStore.Settings settings)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.SaveSettings(tr, db, settings);
        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Geracao
    // ------------------------------------------------------------------

    /// <summary>Cotas (nao residentes) da cadeia na estaca s do eixo.</summary>
    internal static List<AcadDb.AlignedDimension> Build(AcadDb.Database db, AxisInput axis, double s, ViarioResult result, bool detail, ViarioStore.Settings settings)
    {
        List<AcadDb.AlignedDimension> dims = new();
        GPath path = axis.Path;
        V2 p0 = path.PointAt(s);
        V2 t = path.TangentAt(s).Unit();
        V2 n = t.Left;
        List<double> breaks = ViarioDimensionPlan.Breaks(axis, s, result, detail);
        if (breaks.Count < 2)
            return dims;

        AcadDb.ObjectId style = StyleId(db, settings);
        double txt = TextHeight(db, style, settings);
        double off1 = 2.0 * txt;          // linha de cota da cadeia, adiante do ponto
        double off2 = off1 + (4.0 * txt); // largura total, uma fileira acima (nunca sobre a cadeia)

        Point3d P(V2 v) => new(v.X, v.Y, 0);
        void Add(double a, double b, double off)
        {
            V2 pa = p0 + (n * a), pb = p0 + (n * b);
            V2 mid = ((pa + pb) * 0.5) + (t * off);
            AcadDb.AlignedDimension dim = new(P(pa), P(pb), P(mid), "<>", style);
            dim.SetDatabaseDefaults(db);
            dim.Layer = Layer;
            if (settings.CotaEscala > 0)
                dim.Dimscale = settings.CotaEscala;
            dims.Add(dim);
        }

        for (int i = 1; i < breaks.Count; i++)
            Add(breaks[i - 1], breaks[i], off1);
        Add(breaks[0], breaks[^1], off2);
        return dims;
    }

    /// <summary>
    /// Altura do texto da cota no modelo: DIMTXT do estilo x DIMSCALE (o configurado,
    /// senao o do estilo/desenho); estilo anotativo usa a escala de anotacao corrente.
    /// </summary>
    public static double TextHeight(AcadDb.Database db, AcadDb.ObjectId style, ViarioStore.Settings settings)
    {
        double dimtxt = db.Dimtxt;
        double dimscale = db.Dimscale;
        bool annotative = false;
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(style, AcadDb.OpenMode.ForRead) is AcadDb.DimStyleTableRecord r)
            {
                dimtxt = r.Dimtxt;
                dimscale = r.Dimscale;
                annotative = r.Annotative == AcadDb.AnnotativeStates.True;
            }
        }
        catch
        {
            // Fica com as variaveis do desenho.
        }

        double factor;
        if (annotative)
        {
            double anno = 1.0;
            try
            {
                anno = db.Cannoscale?.Scale ?? 1.0;
            }
            catch
            {
                // Sem escala de anotacao.
            }

            factor = anno > 1e-9 ? 1.0 / anno : 1.0;
        }
        else
        {
            factor = settings.CotaEscala > 0 ? settings.CotaEscala : dimscale > 0 ? dimscale : 1.0;
        }

        return Math.Max(0.05, (dimtxt > 0 ? dimtxt : 2.5) * factor);
    }

    private static int Place(Document doc, AxisInput axis, double s, ViarioResult result, bool detail, ViarioStore.Settings settings)
    {
        AcadDb.Database db = doc.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayers(tr, db);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        int count = 0;
        foreach (AcadDb.AlignedDimension dim in Build(db, axis, s, result, detail, settings))
        {
            ms.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
            ViarioStore.SetTag(dim, TagDim, axis.Key, s.ToString("0.###", CultureInfo.InvariantCulture));
            count++;
        }

        tr.Commit();
        return count;
    }

    /// <summary>Remover: a cadeia perto do clique (mesma estaca do mesmo eixo) ou [Todas].</summary>
    private static void RemovePrompt(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        while (true)
        {
            PromptPointOptions opt = new("\nClique perto da cota a remover ou [Todas] <voltar>: ") { AllowNone = true };
            opt.Keywords.Add("Todas");
            opt.AppendKeywordsToMessage = false;
            PromptPointResult r = ed.GetPoint(opt);
            if (r.Status == PromptStatus.Keyword)
            {
                ed.WriteMessage($"\n  {RemoveAll(db)} cota(s) do viario removida(s).");
                return;
            }

            if (r.Status != PromptStatus.OK)
                return;

            Point3d w = r.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            int n = RemoveNear(db, new V2(w.X, w.Y));
            ed.WriteMessage(n == 0 ? "\n  Nenhuma cota do viario perto desse ponto." : $"\n  {n} cota(s) da cadeia removida(s).");
        }
    }

    /// <summary>Apaga a cadeia (eixo + estaca) da cota mais proxima do ponto.</summary>
    private static int RemoveNear(AcadDb.Database db, V2 p)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);

        List<(AcadDb.ObjectId Id, string Group, double Dist)> dims = new();
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("DIMENSION", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Dimension dim)
                continue;
            List<string>? values = ViarioStore.ReadValues(dim);
            if (values is null || values.Count < 3 || values[0] != TagDim)
                continue;

            double dist;
            if (dim is AcadDb.AlignedDimension al)
            {
                V2 a = new(al.XLine1Point.X, al.XLine1Point.Y), b = new(al.XLine2Point.X, al.XLine2Point.Y);
                dist = Seg.Line(a, b).Closest(p).Dist;
            }
            else
            {
                dist = V2.Dist(new V2(dim.TextPosition.X, dim.TextPosition.Y), p);
            }

            dims.Add((id, values[1] + "@" + values[2], dist));
        }

        if (dims.Count == 0)
            return 0;

        (AcadDb.ObjectId _, string group, double best) = dims.OrderBy(d => d.Dist).First();
        if (best > 15.0)
            return 0;

        int n = 0;
        foreach ((AcadDb.ObjectId id, string g, _) in dims.Where(d => d.Group == group))
        {
            tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
            n++;
        }

        tr.Commit();
        return n;
    }

    private static int RemoveAll(AcadDb.Database db)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("DIMENSION", StringComparison.OrdinalIgnoreCase))
                continue;
            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            if (ViarioStore.ReadTag(obj, out _) != TagDim)
                continue;
            obj.UpgradeOpen();
            obj.Erase();
            n++;
        }

        tr.Commit();
        return n;
    }

    // ------------------------------------------------------------------
    // Jig: a cadeia segue o cursor ao longo do eixo
    // ------------------------------------------------------------------

    private sealed class DimensionJig : DrawJig
    {
        private readonly AcadDb.Database _db;
        private readonly List<AxisInput> _axes;
        private readonly ViarioResult _result;
        private readonly ViarioStore.Settings _settings;
        private readonly bool _detail;
        private V2 _mouse;

        public DimensionJig(AcadDb.Database db, List<AxisInput> axes, ViarioResult result, ViarioStore.Settings settings, bool detail)
        {
            _db = db;
            _axes = axes;
            _result = result;
            _settings = settings;
            _detail = detail;
        }

        public AxisInput? Axis { get; private set; }
        public double Station { get; private set; }
        public string? Keyword { get; private set; }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            JigPromptPointOptions options = new(
                "\nCota das faixas: passe sobre o eixo e clique ou [Detalhe/Estilo/Escala/Remover] <sair>" +
                (_detail ? " (sarjeta e meio-fio separados): " : ": "))
            {
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted
            };
            options.Keywords.Add("Detalhe");
            options.Keywords.Add("Estilo");
            options.Keywords.Add("Escala");
            options.Keywords.Add("Remover");

            PromptPointResult r = prompts.AcquirePoint(options);
            if (r.Status == PromptStatus.Keyword)
            {
                Keyword = r.StringResult;
                return SamplerStatus.OK;
            }

            if (r.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            V2 p = new(r.Value.X, r.Value.Y);
            if (V2.Dist(p, _mouse) < 1e-9)
                return SamplerStatus.NoChange;
            _mouse = p;

            Axis = null;
            double best = double.MaxValue;
            foreach (AxisInput a in _axes)
            {
                (double s, _, double dist) = a.Path.Project(p);
                if (dist < best && dist <= a.Section.EdgeOffset(EdgeLevel.Alinhamento) + 3.0)
                {
                    best = dist;
                    Station = s;
                    Axis = a;
                }
            }

            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            try
            {
                if (Axis is null)
                    return true;
                foreach (AcadDb.AlignedDimension dim in Build(_db, Axis, Station, _result, _detail, _settings))
                {
                    using (dim)
                        draw.Geometry.Draw(dim);
                }
            }
            catch
            {
                // Preview nunca derruba o comando.
            }

            return true;
        }
    }
}
