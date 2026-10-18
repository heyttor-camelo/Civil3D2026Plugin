using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Vinculo gravado na viewport (XData C3D_VIEWFRAME).</summary>
internal sealed record ViewFrameLink(ObjectId Viewport, ObjectId Frame, bool Perfil, ObjectId ProfileView, bool Girar);

/// <summary>
/// Liga view frames do Civil as viewports dos layouts e atualiza as vistas pela geometria
/// do frame: em planta, centro e giro pela tangente do eixo no meio do frame; em perfil,
/// o intervalo de estacas do profile view mostrado passa a ser o do frame.
/// A API nao da o retangulo do frame; usa-se eixo + estacas inicial/final.
/// </summary>
internal static class LayoutViewFrameSync
{
    public const string App = "C3D_VIEWFRAME";

    // ------------------------------------------------------------------
    // Vinculos
    // ------------------------------------------------------------------

    public static Dictionary<ObjectId, ViewFrameLink> LerVinculos(Database db)
    {
        var dic = new Dictionary<ObjectId, ViewFrameLink>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var lDic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in lDic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType)
                continue;
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                ViewFrameLink? v = LerVinculo(db, vp);
                if (v is not null)
                    dic[id] = v;
            }
        }
        tr.Commit();
        return dic;
    }

    /// <summary>
    /// XData: 1001 app; 1000 "F:&lt;handle do frame&gt;"; 1000 "P:&lt;handle do profile view&gt;" (so perfil);
    /// 1070 tipo (0 planta, 1 perfil); 1071 girar (0/1).
    /// </summary>
    private static ViewFrameLink? LerVinculo(Database db, Viewport vp)
    {
        using ResultBuffer? rb = vp.GetXDataForApplication(App);
        if (rb is null)
            return null;
        ObjectId frame = ObjectId.Null, pv = ObjectId.Null;
        bool perfil = false, girar = true;
        foreach (TypedValue tv in rb)
        {
            switch (tv.TypeCode)
            {
                case (int)DxfCode.ExtendedDataAsciiString when tv.Value is string s && s.Length > 2:
                    if (s.StartsWith("F:", StringComparison.Ordinal)) frame = DoHandle(db, s[2..]);
                    else if (s.StartsWith("P:", StringComparison.Ordinal)) pv = DoHandle(db, s[2..]);
                    break;
                case (int)DxfCode.ExtendedDataInteger16:
                    perfil = Convert.ToInt32(tv.Value) != 0;
                    break;
                case (int)DxfCode.ExtendedDataInteger32:
                    girar = Convert.ToInt32(tv.Value) != 0;
                    break;
            }
        }
        if (frame.IsNull || frame.IsErased)
            return null;
        return new ViewFrameLink(vp.ObjectId, frame, perfil, pv, girar);
    }

    private static ObjectId DoHandle(Database db, string handle)
    {
        try
        {
            return db.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
        }
        catch (System.Exception)
        {
            return ObjectId.Null;
        }
    }

    public static OpResult Vincular(Document doc, ObjectId viewport, ObjectId frame, bool perfil, ObjectId profileView, bool girar)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        GarantirApp(tr, db);
        if (tr.GetObject(viewport, OpenMode.ForWrite) is not Viewport vp)
            return new OpResult(0, 1, "O objeto nao e viewport.");

        var dados = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, App),
            new((int)DxfCode.ExtendedDataAsciiString, "F:" + frame.Handle),
            new((int)DxfCode.ExtendedDataInteger16, (short)(perfil ? 1 : 0)),
            new((int)DxfCode.ExtendedDataInteger32, girar ? 1 : 0)
        };
        if (!profileView.IsNull)
            dados.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "P:" + profileView.Handle));
        vp.XData = new ResultBuffer(dados.ToArray());
        tr.Commit();
        return new OpResult(1, 0, perfil ? "Viewport vinculada ao view frame como perfil." : "Viewport vinculada ao view frame (planta).");
    }

    public static OpResult Desvincular(Document doc, IEnumerable<ObjectId> viewports)
    {
        int n = 0;
        using DocumentLock _ = doc.LockDocument();
        using Transaction tr = doc.Database.TransactionManager.StartTransaction();
        foreach (ObjectId id in viewports)
        {
            if (tr.GetObject(id, OpenMode.ForWrite) is not Viewport vp)
                continue;
            using ResultBuffer? rb = vp.GetXDataForApplication(App);
            if (rb is null)
                continue;
            vp.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App));
            n++;
        }
        tr.Commit();
        return new OpResult(n, 0, $"{n} viewport(s) desvinculada(s).");
    }

    /// <summary>
    /// Vincula sozinho: para cada frame com folha (layout) deste desenho, a viewport de planta
    /// do layout cujo centro de vista cai sobre o eixo do frame, e a viewport que mostra um
    /// profile view do mesmo eixo como perfil.
    /// </summary>
    public static OpResult VincularAutomatico(Document doc, IReadOnlyList<ViewFrameItem> frames, IReadOnlyList<SheetData> folhas)
    {
        int planta = 0, perfil = 0, semLayout = 0;
        Database db = doc.Database;
        List<(ObjectId Id, ObjectId Alinhamento, Extents3d Ext)> pvs = ProfileViews(db);

        foreach (ViewFrameItem f in frames.Where(f => f.Tipo == "Frame"))
        {
            SheetData? folha = folhas.FirstOrDefault(x => x.Nome.Equals(f.Layout, StringComparison.OrdinalIgnoreCase));
            if (folha is null)
            {
                semLayout++;
                continue;
            }

            ObjectId alin = AlinhamentoDoFrame(db, f.Id);
            Point3d? centro = PontoNoEixo(db, alin, (f.EstacaInicio + f.EstacaFim) / 2);

            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (SheetViewport v in folha.Viewports.Where(v => v.Planta))
            {
                if (tr.GetObject(v.Id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                Point2d[]? reg = Regiao(tr, vp);
                if (reg is null)
                    continue;

                if (centro is Point3d c && Dentro(reg, new Point2d(c.X, c.Y)))
                {
                    tr.Commit();
                    Vincular(doc, v.Id, f.Id, false, ObjectId.Null, true);
                    planta++;
                    goto proximoPerfil;
                }
            }
            tr.Commit();

        proximoPerfil:
            if (alin.IsNull)
                continue;
            foreach (SheetViewport v in folha.Viewports.Where(v => v.Planta))
            {
                using Transaction tr2 = db.TransactionManager.StartTransaction();
                if (tr2.GetObject(v.Id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                Point2d[]? reg = Regiao(tr2, vp);
                tr2.Commit();
                if (reg is null)
                    continue;
                var pv = pvs.FirstOrDefault(p => p.Alinhamento == alin && Dentro(reg, Centro(p.Ext)));
                if (!pv.Id.IsNull)
                {
                    Vincular(doc, v.Id, f.Id, true, pv.Id, false);
                    perfil++;
                    break;
                }
            }
        }

        return new OpResult(planta + perfil, semLayout,
            $"{planta} viewport(s) de planta e {perfil} de perfil vinculada(s)" + (semLayout > 0 ? $"; {semLayout} frame(s) sem layout neste desenho." : "."));
    }

    // ------------------------------------------------------------------
    // Atualizar
    // ------------------------------------------------------------------

    /// <summary>Atualiza as viewports vinculadas (todas ou so as dadas) pela posicao atual dos frames.</summary>
    public static OpResult Atualizar(Document doc, IEnumerable<ObjectId>? somente, IReadOnlyList<ViewFrameItem> frames)
    {
        Database db = doc.Database;
        Dictionary<ObjectId, ViewFrameLink> vinculos = LerVinculos(db);
        var alvo = somente is null ? vinculos.Values.ToList() : vinculos.Where(kv => somente.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (alvo.Count == 0)
            return new OpResult(0, 0, "Nenhuma viewport vinculada a view frame (use Vincular).");

        int ok = 0, falhas = 0;
        var erros = new List<string>();
        using DocumentLock _ = doc.LockDocument();
        foreach (ViewFrameLink v in alvo)
        {
            try
            {
                ViewFrameItem? f = frames.FirstOrDefault(x => x.Id == v.Frame);
                double e0 = f?.EstacaInicio ?? Estaca(db, v.Frame, "StartStation");
                double e1 = f?.EstacaFim ?? Estaca(db, v.Frame, "EndStation");
                if (double.IsNaN(e0) || double.IsNaN(e1))
                    throw new InvalidOperationException("frame sem estacas");

                if (v.Perfil)
                    AtualizarPerfil(db, v, e0, e1);
                else
                    AtualizarPlanta(db, v, e0, e1);
                ok++;
            }
            catch (System.Exception ex)
            {
                falhas++;
                erros.Add(ex.Message);
            }
        }
        return new OpResult(ok, falhas, $"{ok} viewport(s) atualizada(s)" + (falhas > 0 ? $"; {falhas} falha(s): {string.Join("; ", erros.Take(3))}" : "."));
    }

    private static void AtualizarPlanta(Database db, ViewFrameLink v, double e0, double e1)
    {
        ObjectId alin = AlinhamentoDoFrame(db, v.Frame);
        double meio = (e0 + e1) / 2;
        Point3d? centro = PontoNoEixo(db, alin, meio);
        if (centro is null)
            throw new InvalidOperationException("nao foi possivel localizar o eixo do frame");

        using Transaction tr = db.TransactionManager.StartTransaction();
        var vp = (Viewport)tr.GetObject(v.Viewport, OpenMode.ForWrite);
        bool travada = vp.Locked;
        vp.Locked = false;

        if (v.Girar)
        {
            Vector3d? tg = TangenteNoEixo(db, alin, meio);
            if (tg is Vector3d t && t.Length > 1e-9)
                vp.TwistAngle = -Math.Atan2(t.Y, t.X);
        }

        // Centro da vista em DCS = ponto do eixo no sistema da viewport.
        Matrix3d wcs2dcs = Dcs2Wcs(vp).Inverse();
        Point3d c = centro.Value.TransformBy(wcs2dcs);
        vp.ViewCenter = new Point2d(c.X, c.Y);
        vp.Locked = travada;
        tr.Commit();
    }

    private static void AtualizarPerfil(Database db, ViewFrameLink v, double e0, double e1)
    {
        if (v.ProfileView.IsNull || v.ProfileView.IsErased)
            throw new InvalidOperationException("profile view do vinculo nao existe mais");

        using Transaction tr = db.TransactionManager.StartTransaction();
        DBObject pv = tr.GetObject(v.ProfileView, OpenMode.ForWrite);
        // StationRangeMode.UserSpecified e StationStart/StationEnd (lidos por reflexao: enum do Civil).
        PropertyInfo? modo = pv.GetType().GetProperty("StationRangeMode");
        if (modo is not null && modo.PropertyType.IsEnum)
            modo.SetValue(pv, Enum.Parse(modo.PropertyType, "UserSpecified"));
        pv.GetType().GetProperty("StationStart")?.SetValue(pv, e0);
        pv.GetType().GetProperty("StationEnd")?.SetValue(pv, e1);

        // Recentra a viewport no profile view atualizado.
        if (pv is Entity ent)
        {
            Extents3d ext = ent.GeometricExtents;
            var vp = (Viewport)tr.GetObject(v.Viewport, OpenMode.ForWrite);
            bool travada = vp.Locked;
            vp.Locked = false;
            Point3d c = Centro3(ext).TransformBy(Dcs2Wcs(vp).Inverse());
            vp.ViewCenter = new Point2d(c.X, c.Y);
            vp.Locked = travada;
        }
        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Civil (por reflexao: eixo, ponto e tangente; profile views)
    // ------------------------------------------------------------------

    private static ObjectId AlinhamentoDoFrame(Database db, ObjectId frame)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        DBObject f = tr.GetObject(frame, OpenMode.ForRead);
        ObjectId id = f.GetType().GetProperty("AlignmentId")?.GetValue(f) is ObjectId a ? a : ObjectId.Null;
        tr.Commit();
        return id;
    }

    private static double Estaca(Database db, ObjectId frame, string prop)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        DBObject f = tr.GetObject(frame, OpenMode.ForRead);
        object? v = f.GetType().GetProperty(prop)?.GetValue(f);
        tr.Commit();
        return v is null ? double.NaN : Convert.ToDouble(v);
    }

    private static Point3d? PontoNoEixo(Database db, ObjectId alin, double estaca)
    {
        if (alin.IsNull)
            return null;
        using Transaction tr = db.TransactionManager.StartTransaction();
        DBObject a = tr.GetObject(alin, OpenMode.ForRead);
        try
        {
            // Alignment.PointLocation(station, offset, ref easting, ref northing)
            MethodInfo? m = a.GetType().GetMethod("PointLocation", new[] { typeof(double), typeof(double), typeof(double).MakeByRefType(), typeof(double).MakeByRefType() });
            if (m is null)
                return null;
            object[] args = { estaca, 0.0, 0.0, 0.0 };
            m.Invoke(a, args);
            return new Point3d((double)args[2], (double)args[3], 0);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        finally
        {
            tr.Commit();
        }
    }

    private static Vector3d? TangenteNoEixo(Database db, ObjectId alin, double estaca)
    {
        Point3d? a = PontoNoEixo(db, alin, estaca - 0.5);
        Point3d? b = PontoNoEixo(db, alin, estaca + 0.5);
        if (a is null || b is null)
            return null;
        return b.Value - a.Value;
    }

    private static List<(ObjectId Id, ObjectId Alinhamento, Extents3d Ext)> ProfileViews(Database db)
    {
        var lista = new List<(ObjectId, ObjectId, Extents3d)>();
        try
        {
            CivilDocument civ = CivilDocument.GetCivilDocument(db);
            if (civ.GetType().GetMethod("GetProfileViewIds", Type.EmptyTypes)?.Invoke(civ, null) is not ObjectIdCollection ids)
                return lista;
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (ObjectId id in ids)
            {
                DBObject pv = tr.GetObject(id, OpenMode.ForRead);
                ObjectId alin = pv.GetType().GetProperty("AlignmentId")?.GetValue(pv) is ObjectId a ? a : ObjectId.Null;
                if (pv is Entity e)
                    lista.Add((id, alin, e.GeometricExtents));
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
        }
        return lista;
    }

    // ------------------------------------------------------------------
    // Geometria de viewport (igual ao mapa-chave)
    // ------------------------------------------------------------------

    private static Point2d[]? Regiao(Transaction tr, Viewport vp)
    {
        if (!vp.ViewDirection.IsParallelTo(Vector3d.ZAxis) || vp.CustomScale <= 0)
            return null;
        Point3d c = vp.CenterPoint;
        double w = vp.Width / 2, h = vp.Height / 2;
        var papel = new[]
        {
            new Point3d(c.X - w, c.Y - h, 0), new Point3d(c.X + w, c.Y - h, 0),
            new Point3d(c.X + w, c.Y + h, 0), new Point3d(c.X - w, c.Y + h, 0)
        };
        Matrix3d ps2wcs = Dcs2Wcs(vp) * Dcs2Psdcs(vp).Inverse();
        return papel.Select(p => p.TransformBy(ps2wcs)).Select(p => new Point2d(p.X, p.Y)).ToArray();
    }

    private static Matrix3d Dcs2Wcs(Viewport vp) =>
        Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
        Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
        Matrix3d.PlaneToWorld(vp.ViewDirection);

    private static Matrix3d Dcs2Psdcs(Viewport vp) =>
        Matrix3d.Scaling(vp.CustomScale, vp.CenterPoint) *
        Matrix3d.Displacement(vp.CenterPoint - Point3d.Origin) *
        Matrix3d.Displacement(new Vector3d(-vp.ViewCenter.X, -vp.ViewCenter.Y, 0.0));

    private static bool Dentro(Point2d[] poly, Point2d p)
    {
        bool dentro = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                dentro = !dentro;
        }
        return dentro;
    }

    private static Point2d Centro(Extents3d e) => new((e.MinPoint.X + e.MaxPoint.X) / 2, (e.MinPoint.Y + e.MaxPoint.Y) / 2);

    private static Point3d Centro3(Extents3d e) => new((e.MinPoint.X + e.MaxPoint.X) / 2, (e.MinPoint.Y + e.MaxPoint.Y) / 2, 0);

    private static void GarantirApp(Transaction tr, Database db)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(App))
            return;
        rat.UpgradeOpen();
        var r = new RegAppTableRecord { Name = App };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }
}
