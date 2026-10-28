using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Cotas editaveis que aparecem sozinhas ao selecionar uma estrutura (sem comando): um grip em cada
/// cota (comprimento e largura internos), que desenha a cota e o valor sobre a caixa. Clicar no grip
/// abre o HYMEDIDAS com o foco naquela medida (campo de valor do Dynamic Input; Tab passa para a outra,
/// Enter grava, Esc cancela). Os grips do Civil continuam; os nossos so disparam o comando.
/// Tudo protegido: excecao aqui derrubaria o Civil 3D.
/// </summary>
internal sealed class HydroDimGrips : AcadDb.GripOverrule
{
    private static HydroDimGrips? _instance;
    private const double Gap = 0.35;

    public static void Initialize()
    {
        if (_instance is not null)
            return;
        try
        {
            _instance = new HydroDimGrips();
            AddOverrule(RXObject.GetClass(typeof(CivilDb.Structure)), _instance, false);
            Overruling = true;
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("Hydro", "Cotas nos grips nao registradas: " + ex.Message);
            _instance = null;
        }
    }

    public static void Terminate()
    {
        try
        {
            if (_instance is not null)
                RemoveOverrule(RXObject.GetClass(typeof(CivilDb.Structure)), _instance);
        }
        catch
        {
            // Encerramento.
        }

        _instance = null;
    }

    public override void GetGripPoints(AcadDb.Entity entity, AcadDb.GripDataCollection grips, double curViewUnitSize, int gripSize, Vector3d curViewDir, AcadDb.GetGripPointsFlags bitFlags)
    {
        base.GetGripPoints(entity, grips, curViewUnitSize, gripSize, curViewDir, bitFlags);
        try
        {
            if (entity is not CivilDb.Structure s || entity.ObjectId.IsNull || !HydroStore.Load(entity.Database).DimGrips)
                return;
            HydroDimEdit.Shape sh = HydroDimEdit.Shape.Of(s);
            double l = sh.InnerL / 2, w = sh.InnerW / 2, ol = l + sh.Wall, ow = w + sh.Wall;
            if (sh.Length is HydroDimEdit.Measure ml)
                grips.Add(new DimGrip("comprimento", sh.Local(-l, -ow - Gap), sh.Local(l, -ow - Gap), sh.Local(0, -ow - Gap), $"comprimento {Fmt(ml.Value)}"));
            if (sh.Width is HydroDimEdit.Measure mw)
                grips.Add(new DimGrip("largura", sh.Local(ol + Gap, -w), sh.Local(ol + Gap, w), sh.Local(ol + Gap, 0), $"largura {Fmt(mw.Value)}"));
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag("Hydro", "Cotas nos grips: " + ex.Message);
        }
    }

    public override void MoveGripPointsAt(AcadDb.Entity entity, AcadDb.GripDataCollection grips, Vector3d offset, AcadDb.MoveGripPointsFlags bitFlags)
    {
        // Os nossos grips nao arrastam (disparam o comando); so os do Civil seguem para ele.
        AcadDb.GripDataCollection theirs = new();
        foreach (AcadDb.GripData g in grips)
        {
            if (g is not DimGrip)
                theirs.Add(g);
        }

        if (theirs.Count > 0)
            base.MoveGripPointsAt(entity, theirs, offset, bitFlags);
    }

    private static string Fmt(double m) => m.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') + " m";

    /// <summary>Grip de cota: desenha a linha de cota e o valor; clique abre o campo da medida.</summary>
    private sealed class DimGrip : AcadDb.GripData
    {
        private readonly string _key, _text;
        private readonly Point3d _a, _b;

        public DimGrip(string key, Point3d a, Point3d b, Point3d at, string text)
        {
            _key = key;
            _a = a;
            _b = b;
            _text = text;
            GripPoint = at;
            TriggerGrip = true;
            HotGripInvokesRightClick = false;
        }

        public override bool ViewportDraw(ViewportDraw worldDraw, AcadDb.ObjectId entityId, DrawType type, Point3d? imageGripPoint, int gripSizeInPixels)
        {
            try
            {
                Point3d p = imageGripPoint ?? GripPoint;
                double size = worldDraw.Viewport.GetNumPixelsInUnitSquare(p).X;
                double unit = size > 1e-9 ? 1.0 / size : 0.01;
                worldDraw.SubEntityTraits.Color = (short)(type == DrawType.HotGrip ? 1 : type == DrawType.HoverGrip ? 2 : 4);
                worldDraw.Geometry.WorldLine(_a, _b);
                Vector3d along = (_b - _a).GetNormal();
                Vector3d n = along.GetPerpendicularVector() * (unit * 5);
                worldDraw.Geometry.WorldLine(_a - n, _a + n);
                worldDraw.Geometry.WorldLine(_b - n, _b + n);
                double half = unit * gripSizeInPixels;
                Point3dCollection box = new() { p + new Vector3d(-half, -half, 0), p + new Vector3d(half, -half, 0), p + new Vector3d(half, half, 0), p + new Vector3d(-half, half, 0), p + new Vector3d(-half, -half, 0) };
                worldDraw.Geometry.Polyline(box, Vector3d.ZAxis, IntPtr.Zero);
                double h = unit * 12;
                double ang = Math.Atan2(along.Y, along.X);
                if (ang > Math.PI / 2 || ang < -Math.PI / 2)
                    ang += Math.PI;
                Vector3d dir = new(Math.Cos(ang), Math.Sin(ang), 0);
                worldDraw.Geometry.Text(p + (dir.GetPerpendicularVector() * (h * 0.8)) - (dir * (h * _text.Length * 0.3)), Vector3d.ZAxis, dir, h, 1.0, 0.0, _text);
            }
            catch
            {
                // Desenho do grip nunca derruba o Civil.
            }

            return true;
        }

        public override ReturnValue OnHotGrip(AcadDb.ObjectId entityId, Context contextFlags)
        {
            try
            {
                Document? doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc is not null)
                {
                    HydroDimEdit.FocusNext = _key;
                    doc.Editor.SetImpliedSelection(new[] { entityId });
                    doc.SendStringToExecute(C3DCommands.Hydro.Medidas + " ", true, false, false);
                }
            }
            catch (System.Exception ex)
            {
                PluginLog.Diag("Hydro", "Grip de cota: " + ex.Message);
            }

            return ReturnValue.GripHotToWarm;
        }
    }
}
