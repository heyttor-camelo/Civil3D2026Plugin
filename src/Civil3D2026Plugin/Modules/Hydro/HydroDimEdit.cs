using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// HYMEDIDAS: edicao das medidas da estrutura no proprio desenho, como o Dynamic Input do AutoCAD.
/// Cotas editaveis sobre as arestas da caixa em planta (comprimento e largura internos); Tab passa
/// de uma para outra, a previa (contorno interno e externo) acompanha o valor digitado, Enter ou
/// clique grava no PartData (um UNDO), Esc cancela sem mexer na peca. Respeita a rotacao e o ponto
/// de insercao na face das bocas de lobo de calcada (a face fica parada; a caixa cresce para dentro).
/// </summary>
public sealed class HydroDimEdit
{
    private const string Module = "Hydro";

    [CommandMethod(C3DCommands.Hydro.Medidas, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Medidas()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        try
        {
            AcadDb.ObjectId? id = PickStructure(ed);
            if (id is null)
                return;
            using (PluginLog.Medir(Module, "HYMEDIDAS"))
                Run(doc, ed, id.Value);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, "HYMEDIDAS falhou", ex);
            ed.WriteMessage("\n[HYDRO] HYMEDIDAS falhou: " + ex.Message);
        }

        HydroPanel.NotifyChanged();
    }

    private static AcadDb.ObjectId? PickStructure(Editor ed)
    {
        PromptSelectionResult pre = ed.SelectImplied();
        if (pre.Status == PromptStatus.OK)
        {
            AcadDb.ObjectId first = pre.Value.GetObjectIds().FirstOrDefault(i => i.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Structure))));
            ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());
            if (!first.IsNull)
                return first;
        }

        PromptEntityOptions o = new("\nEstrutura para editar as medidas: ");
        o.SetRejectMessage("\nSelecione uma estrutura da Pipe Network.");
        o.AddAllowedClass(typeof(CivilDb.Structure), false);
        PromptEntityResult r = ed.GetEntity(o);
        return r.Status == PromptStatus.OK ? r.ObjectId : null;
    }

    private static void Run(Document doc, Editor ed, AcadDb.ObjectId id)
    {
        AcadDb.Database db = doc.Database;
        Shape shape;
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            shape = Shape.Of(s);
        }

        if (shape.Length is null && shape.Width is null)
        {
            ed.WriteMessage($"\n{shape.Name}: comprimento e largura internos sao fixos nesta familia (nao estao em faixa no catalogo).");
            return;
        }

        ed.WriteMessage($"\n{shape.Name}: digite a medida na cota (m). Tab: proxima cota   Enter/clique: grava   Esc: cancela.");
        DimJig jig = new(shape);
        PromptResult res = ed.Drag(jig);
        if (res.Status is not (PromptStatus.OK or PromptStatus.None))
        {
            ed.WriteMessage("\n[HYDRO] Medidas canceladas; a peca nao mudou.");
            return;
        }

        Dictionary<string, double> values = new(StringComparer.OrdinalIgnoreCase);
        List<string> what = new();
        foreach (Measure? m in new[] { shape.Length, shape.Width })
        {
            if (m is null || Math.Abs(m.Value - m.Original) < 1e-6)
                continue;
            values[m.Name] = m.Value / m.Factor;
            what.Add($"{m.Label} {m.Original:0.000} > {m.Value:0.000} m");
        }

        if (values.Count == 0)
        {
            ed.WriteMessage("\n[HYDRO] Nada mudou.");
            return;
        }

        string msg = HydroEditor.SetParameters(db, id.Handle.ToString(), values);
        ed.WriteMessage($"\n[HYDRO] {shape.Name}: {string.Join("; ", what)} ({msg}).");
    }

    // ------------------------------------------------------------------

    /// <summary>Uma medida editavel (m), com a faixa do catalogo.</summary>
    private sealed class Measure
    {
        public string Name = "";
        public string Label = "";
        public double Original;
        public double Value;
        public double Factor = 1;
        public double Min = double.NaN;
        public double Max = double.NaN;

        public double Clamp(double v) =>
            Math.Max(double.IsNaN(Min) ? 0.01 : Min, Math.Min(double.IsNaN(Max) ? 1e5 : Max, v));
    }

    /// <summary>Caixa em planta: insercao, eixos locais, centro, parede e as medidas editaveis.</summary>
    private sealed class Shape
    {
        public string Name = "";
        public Point3d Insertion;
        public Vector3d U, V;
        public double Wall;
        public double Dx, Dy;          // centro da caixa em relacao a insercao, no sistema local (m)
        public double OuterL0, OuterW0; // medidas externas gravadas, para escalar o deslocamento do centro
        public double InnerL0, InnerW0;
        public Measure? Length, Width;

        public double InnerL => Length?.Value ?? InnerL0;
        public double InnerW => Width?.Value ?? InnerW0;

        public static Shape Of(CivilDb.Structure s)
        {
            Shape sh = new() { Name = s.Name, Insertion = s.Location };
            double r = s.Rotation;
            sh.U = new Vector3d(Math.Cos(r), Math.Sin(r), 0);
            sh.V = new Vector3d(-Math.Sin(r), Math.Cos(r), 0);
            List<PartParam> ps = HydroReader.ReadParameters(s);
            PartParam? wall = ps.FirstOrDefault(p => p.Context.Equals("WallThickness", StringComparison.OrdinalIgnoreCase));
            sh.Wall = wall?.Meters ?? s.WallThickness;
            sh.Length = Make(ps, "StructInnerLength", "comprimento");
            sh.Width = Make(ps, "StructInnerWidth", "largura");
            sh.InnerL0 = sh.Length?.Original ?? Math.Max(0.1, s.Length - (2 * sh.Wall));
            sh.InnerW0 = sh.Width?.Original ?? Math.Max(0.1, s.InnerDiameterOrWidth);
            sh.OuterL0 = sh.InnerL0 + (2 * sh.Wall);
            sh.OuterW0 = sh.InnerW0 + (2 * sh.Wall);
            Point2d c = HydroPipeInside.BoxCenter(s);
            Vector3d d = new Point3d(c.X, c.Y, sh.Insertion.Z) - sh.Insertion;
            sh.Dx = d.DotProduct(sh.U);
            sh.Dy = d.DotProduct(sh.V);
            return sh;
        }

        private static Measure? Make(List<PartParam> ps, string context, string label)
        {
            PartParam? p = ps.FirstOrDefault(x => x.Context.Equals(context, StringComparison.OrdinalIgnoreCase) && x.Editable && !x.Fixed && x.IsRange);
            if (p is null)
                return null;
            return new Measure
            {
                Name = p.Name,
                Label = label,
                Original = p.Meters,
                Value = p.Meters,
                Factor = p.Factor,
                Min = double.IsNaN(p.Min) ? double.NaN : p.Min * p.Factor,
                Max = double.IsNaN(p.Max) ? double.NaN : p.Max * p.Factor
            };
        }

        /// <summary>Centro atual: o deslocamento a partir da insercao escala com a medida externa (a face da insercao fica parada).</summary>
        public Point3d Center()
        {
            double ol = InnerL + (2 * Wall), ow = InnerW + (2 * Wall);
            double dx = OuterL0 > 1e-6 ? Dx * ol / OuterL0 : 0;
            double dy = OuterW0 > 1e-6 ? Dy * ow / OuterW0 : 0;
            return Insertion + (U * dx) + (V * dy);
        }

        public Point3d Local(double x, double y) => Center() + (U * x) + (V * y);
    }

    /// <summary>Jig sem ponto: so as cotas dinamicas editaveis e a previa do contorno.</summary>
    private sealed class DimJig : DrawJig
    {
        private readonly Shape _s;
        private readonly AcadDb.AlignedDimension? _lenDim, _widDim;
        private readonly List<(AcadDb.AlignedDimension Dim, Measure M)> _dims = new();
        private bool _dirty = true;
        private const double Gap = 0.35;

        public DimJig(Shape s)
        {
            _s = s;
            if (s.Length is not null)
            {
                _lenDim = new AcadDb.AlignedDimension { DynamicDimension = true };
                _dims.Add((_lenDim, s.Length));
            }

            if (s.Width is not null)
            {
                _widDim = new AcadDb.AlignedDimension { DynamicDimension = true };
                _dims.Add((_widDim, s.Width));
            }

            PlaceDims();
        }

        private void PlaceDims()
        {
            double l = _s.InnerL / 2, w = _s.InnerW / 2, ow = w + _s.Wall, ol = l + _s.Wall;
            if (_lenDim is not null)
            {
                _lenDim.XLine1Point = _s.Local(-l, -w);
                _lenDim.XLine2Point = _s.Local(l, -w);
                _lenDim.DimLinePoint = _s.Local(0, -ow - Gap);
            }

            if (_widDim is not null)
            {
                _widDim.XLine1Point = _s.Local(l, -w);
                _widDim.XLine2Point = _s.Local(l, w);
                _widDim.DimLinePoint = _s.Local(ol + Gap, 0);
            }
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            JigPromptPointOptions o = new("\nTab: proxima cota   Enter: grava   Esc: cancela")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted | UserInputControls.GovernedByOrthoMode
            };
            PromptPointResult r = prompts.AcquirePoint(o);
            if (r.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;
            if (!_dirty)
                return SamplerStatus.NoChange;
            _dirty = false;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            try
            {
                double l = _s.InnerL / 2, w = _s.InnerW / 2, ol = l + _s.Wall, ow = w + _s.Wall;
                draw.SubEntityTraits.Color = 3;
                Rect(draw, l, w);
                draw.SubEntityTraits.Color = 4;
                Rect(draw, ol, ow);
                draw.SubEntityTraits.Color = 2;
                draw.Geometry.Circle(_s.Insertion, 0.05, Vector3d.ZAxis);
            }
            catch
            {
                // Previa nunca derruba o comando.
            }

            return true;
        }

        private void Rect(WorldDraw draw, double hx, double hy)
        {
            Point3dCollection pts = new()
            {
                _s.Local(-hx, -hy), _s.Local(hx, -hy), _s.Local(hx, hy), _s.Local(-hx, hy), _s.Local(-hx, -hy)
            };
            draw.Geometry.Polyline(pts, Vector3d.ZAxis, IntPtr.Zero);
        }

        protected override AcadDb.DynamicDimensionDataCollection GetDynamicDimensionData(double dimScale)
        {
            AcadDb.DynamicDimensionDataCollection col = new();
            bool first = true;
            foreach ((AcadDb.AlignedDimension dim, _) in _dims)
            {
                col.Add(new AcadDb.DynamicDimensionData(dim, true, true) { Editable = true, Focal = first });
                first = false;
            }

            return col;
        }

        protected override void OnDimensionValueChanged(AcadDb.DynamicDimensionChangedEventArgs e)
        {
            try
            {
                int i = e.Index;
                double v = e.Value;
                if (i < 0 || i >= _dims.Count || v <= 0)
                    return;
                Measure m = _dims[i].M;
                m.Value = m.Clamp(v);
                PlaceDims();
                _dirty = true;
            }
            catch
            {
                // Valor ilegivel: mantem o anterior.
            }
        }
    }
}
