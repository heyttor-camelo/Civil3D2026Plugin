using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Configuracao de uma serie de pranchas planta e perfil (uma por eixo), gravada no NOD
/// (dicionario C3D_PRANCHAS, um Xrecord por eixo).
/// </summary>
internal sealed class SheetSeries
{
    public ObjectId Eixo { get; set; }
    public string EixoNome { get; set; } = "";
    public string Modelo { get; set; } = "";
    public ObjectId VpPlanta { get; set; }
    public ObjectId VpPerfil { get; set; }
    public double EstacaInicial { get; set; } = double.NaN;
    public double EstacaFinal { get; set; } = double.NaN;
    public double Arredondar { get; set; } = 20;
    public double Folga { get; set; } = 10;
    public bool Girar { get; set; } = true;
    public string Prefixo { get; set; } = "PP-";
    public int Inicio { get; set; } = 1;
    public int Digitos { get; set; } = 2;
    public ObjectId EstiloPerfil { get; set; }
    public ObjectId BandasPerfil { get; set; }
    public bool AjustarCotas { get; set; } = true;
    public bool Automatico { get; set; } = true;
    public bool Numerar { get; set; } = true;

    // Calibracao do profile view (medida ao criar os frames).
    public double MargemPerfil { get; set; } = double.NaN;   // largura alem das estacas (eixos, rotulos), unidades de desenho
    public double SobraVertical { get; set; } = double.NaN;  // altura alem do grafico (bandas, titulos)
    public double Exagero { get; set; } = double.NaN;        // unidades de desenho por metro de cota

    public List<ObjectId> Frames { get; } = new();
    public List<ObjectId> Cortes { get; } = new();
    /// <summary>Frames da lista que o usuario apagou (para limpar os rotulos deles).</summary>
    public List<ObjectId> FramesApagados { get; } = new();

    public bool ComPerfil => !VpPerfil.IsNull;

    public string Chave => "EIXO_" + Eixo.Handle.ToString();

    public string NomeFolha(int indice) =>
        Prefixo + (Inicio + indice).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, Digitos), '0');
}

/// <summary>Um frame (retangulo no Model) da serie e o que esta ligado a ele.</summary>
internal sealed class SheetFrameInfo
{
    public ObjectId Id { get; init; }
    public ObjectId Eixo { get; set; }
    public int Indice { get; set; }
    public double E0 { get; set; }
    public double E1 { get; set; }
    /// <summary>
    /// Posicao dos cortes no frame: X = distancia a ponta do frame (inicio: aresta 3-0; fim: aresta
    /// 1-2), Y = fracao no sentido transversal. Esticar uma ponta nao mexe no corte da outra.
    /// </summary>
    public Point2d F0 { get; set; }
    public Point2d F1 { get; set; }
    public ObjectId Layout { get; set; }
    public ObjectId VpPlanta { get; set; }
    public ObjectId VpPerfil { get; set; }
    public ObjectId Perfil { get; set; }
    public ObjectId Rotulo { get; set; }
    public string Aviso { get; set; } = "";

    public string LayoutNome { get; set; } = "";
    public string PerfilNome { get; set; } = "";
    public string EixoNome { get; set; } = "";
    public string Situacao { get; set; } = "";
    public double Extensao => E1 - E0;
}

/// <summary>Viewports e tamanhos do layout modelo.</summary>
internal sealed record SheetTemplate(
    ObjectId Layout, string Nome,
    ObjectId VpPlanta, double WPlanta, double HPlanta, double EscalaPlanta,
    ObjectId VpPerfil, double WPerfil, double HPerfil, double EscalaPerfil)
{
    public bool TemPerfil => !VpPerfil.IsNull && EscalaPerfil > 0;
    /// <summary>Frame em unidades de desenho (papel / custom scale).</summary>
    public double LarguraFrame => WPlanta / EscalaPlanta;
    public double AlturaFrame => HPlanta / EscalaPlanta;
    public double LarguraPerfil => TemPerfil ? WPerfil / EscalaPerfil : 0;
    public double AlturaPerfil => TemPerfil ? HPerfil / EscalaPerfil : 0;
}

/// <summary>Quanto cabe por folha (previa no painel).</summary>
internal sealed record SheetCapacity(double Planta, double Perfil, bool PerfilCalibrado, double Comprimento, int Folhas);

/// <summary>
/// Pranchas planta e perfil com frames proprios (no lugar dos view frames do Civil, que a API
/// nao deixa criar nem mover). O frame e uma polilinha no Model do tamanho da viewport de planta
/// do layout modelo; cada frame ganha um layout (copia do modelo) e um profile view com o
/// intervalo de estacas do frame, no tamanho da viewport de perfil. Mover, girar ou esticar um
/// frame (ou editar o eixo) atualiza a viewport de planta, as estacas do perfil, a viewport de
/// perfil e as linhas de corte.
/// </summary>
internal static class LayoutSheetSeries
{
    public const string Dicionario = "C3D_PRANCHAS";
    public const string App = "C3D_FRAME";
    public const string LayerFrame = "PRANCHA_FRAME";
    public const string LayerCorte = "PRANCHA_CORTE";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------
    // Leitura e gravacao da serie
    // ------------------------------------------------------------------

    public static List<SheetSeries> LerSeries(Database db)
    {
        var lista = new List<SheetSeries>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(Dicionario))
        {
            var dic = (DBDictionary)tr.GetObject(nod.GetAt(Dicionario), OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                if (tr.GetObject(e.Value, OpenMode.ForRead) is Xrecord xr)
                {
                    SheetSeries? s = DeXrecord(tr, db, xr);
                    if (s is not null)
                        lista.Add(s);
                }
            }
        }
        tr.Commit();
        return lista;
    }

    public static SheetSeries? LerSerie(Database db, ObjectId eixo) => LerSeries(db).FirstOrDefault(s => s.Eixo == eixo);

    private static SheetSeries? DeXrecord(Transaction tr, Database db, Xrecord xr)
    {
        if (xr.Data is null)
            return null;
        var s = new SheetSeries();
        foreach (TypedValue tv in xr.Data.AsArray())
        {
            if (tv.Value is not string t)
                continue;
            int i = t.IndexOf('=');
            if (i <= 0)
                continue;
            string k = t[..i], v = t[(i + 1)..];
            switch (k)
            {
                case "E": s.Eixo = DoHandle(db, v); break;
                case "M": s.Modelo = v; break;
                case "VP": s.VpPlanta = DoHandle(db, v); break;
                case "VF": s.VpPerfil = DoHandle(db, v); break;
                case "EI": s.EstacaInicial = Num(v); break;
                case "EF": s.EstacaFinal = Num(v); break;
                case "AR": s.Arredondar = Num(v, 20); break;
                case "FO": s.Folga = Num(v, 10); break;
                case "GI": s.Girar = v == "1"; break;
                case "PR": s.Prefixo = v; break;
                case "IN": s.Inicio = (int)Num(v, 1); break;
                case "DI": s.Digitos = (int)Num(v, 2); break;
                case "EP": s.EstiloPerfil = DoHandle(db, v); break;
                case "BP": s.BandasPerfil = DoHandle(db, v); break;
                case "AC": s.AjustarCotas = v == "1"; break;
                case "AU": s.Automatico = v == "1"; break;
                case "NU": s.Numerar = v == "1"; break;
                case "MP": s.MargemPerfil = Num(v); break;
                case "SV": s.SobraVertical = Num(v); break;
                case "EX": s.Exagero = Num(v); break;
                case "F":
                {
                    ObjectId id = DoHandle(db, v);
                    if (Valido(id))
                        s.Frames.Add(id);
                    else if (!id.IsNull && id.IsErased)
                        s.FramesApagados.Add(id);
                    break;
                }
                case "C":
                {
                    ObjectId id = DoHandle(db, v);
                    if (Valido(id))
                        s.Cortes.Add(id);
                    break;
                }
            }
        }
        if (!Valido(s.Eixo))
            return null;
        if (tr.GetObject(s.Eixo, OpenMode.ForRead) is CivilDb.Alignment al)
            s.EixoNome = al.Name;
        return s;
    }

    private static void Gravar(Transaction tr, Database db, SheetSeries s)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        DBDictionary dic;
        if (nod.Contains(Dicionario))
        {
            dic = (DBDictionary)tr.GetObject(nod.GetAt(Dicionario), OpenMode.ForWrite, false, true);
        }
        else
        {
            nod.UpgradeOpen();
            dic = new DBDictionary();
            nod.SetAt(Dicionario, dic);
            tr.AddNewlyCreatedDBObject(dic, true);
        }

        var dados = new List<TypedValue>();
        void Add(string k, string v) => dados.Add(new TypedValue((int)DxfCode.Text, k + "=" + v));
        Add("E", H(s.Eixo));
        Add("M", s.Modelo);
        Add("VP", H(s.VpPlanta));
        Add("VF", H(s.VpPerfil));
        Add("EI", D(s.EstacaInicial));
        Add("EF", D(s.EstacaFinal));
        Add("AR", D(s.Arredondar));
        Add("FO", D(s.Folga));
        Add("GI", s.Girar ? "1" : "0");
        Add("PR", s.Prefixo);
        Add("IN", s.Inicio.ToString(Inv));
        Add("DI", s.Digitos.ToString(Inv));
        Add("EP", H(s.EstiloPerfil));
        Add("BP", H(s.BandasPerfil));
        Add("AC", s.AjustarCotas ? "1" : "0");
        Add("AU", s.Automatico ? "1" : "0");
        Add("NU", s.Numerar ? "1" : "0");
        Add("MP", D(s.MargemPerfil));
        Add("SV", D(s.SobraVertical));
        Add("EX", D(s.Exagero));
        foreach (ObjectId id in s.Frames)
            Add("F", H(id));
        foreach (ObjectId id in s.Cortes)
            Add("C", H(id));

        if (dic.Contains(s.Chave))
        {
            var xr = (Xrecord)tr.GetObject(dic.GetAt(s.Chave), OpenMode.ForWrite, false, true);
            xr.Data = new ResultBuffer(dados.ToArray());
        }
        else
        {
            var xr = new Xrecord { Data = new ResultBuffer(dados.ToArray()) };
            dic.SetAt(s.Chave, xr);
            tr.AddNewlyCreatedDBObject(xr, true);
        }
    }

    /// <summary>
    /// Grava as opcoes editadas no painel numa serie existente. Frames, linhas de corte e calibracao
    /// vem do que esta gravado (a copia do painel pode estar velha depois de uma atualizacao automatica).
    /// </summary>
    public static void Salvar(Document doc, SheetSeries s)
    {
        Database db = doc.Database;
        SheetSeries? gravada = LerSerie(db, s.Eixo);
        if (gravada is null)
            return;
        CopiarOpcoes(s, gravada);
        using IDisposable __ = SheetSeriesSync.Suspender();
        using DocumentLock _ = doc.LockDocument();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Gravar(tr, db, gravada);
            tr.Commit();
        }
        SheetSeriesSync.Recarregar(db);
    }

    private static void CopiarOpcoes(SheetSeries de, SheetSeries para)
    {
        para.Modelo = de.Modelo;
        para.VpPlanta = de.VpPlanta;
        para.VpPerfil = de.VpPerfil;
        para.EstacaInicial = de.EstacaInicial;
        para.EstacaFinal = de.EstacaFinal;
        para.Arredondar = de.Arredondar;
        para.Folga = de.Folga;
        para.Girar = de.Girar;
        para.Prefixo = de.Prefixo;
        para.Inicio = de.Inicio;
        para.Digitos = de.Digitos;
        para.EstiloPerfil = de.EstiloPerfil;
        para.BandasPerfil = de.BandasPerfil;
        para.AjustarCotas = de.AjustarCotas;
        para.Automatico = de.Automatico;
        para.Numerar = de.Numerar;
    }

    // ------------------------------------------------------------------
    // Frames
    // ------------------------------------------------------------------

    /// <summary>Frames da serie (os apagados ficam de fora), com situacao para a lista.</summary>
    public static List<SheetFrameInfo> LerFrames(Database db, SheetSeries s)
    {
        var lista = new List<SheetFrameInfo>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId id in s.Frames)
        {
            SheetFrameInfo? f = LerFrame(tr, db, id);
            if (f is null)
                continue;
            f.EixoNome = s.EixoNome;
            lista.Add(f);
        }

        lista = lista.OrderBy(f => f.E0).ToList();
        for (int i = 0; i < lista.Count; i++)
        {
            SheetFrameInfo f = lista[i];
            var sit = new List<string>();
            if (!Valido(f.Layout))
                sit.Add("sem prancha");
            if (s.ComPerfil && Valido(f.Layout) && !Valido(f.Perfil))
                sit.Add("sem perfil");
            if (i > 0)
            {
                double d = f.E0 - lista[i - 1].E1;
                if (d > 0.5)
                    sit.Add($"lacuna de {d:0.#} m antes");
                else if (d < -0.5)
                    sit.Add($"sobrepoe {-d:0.#} m");
            }
            if (Valido(f.VpPlanta) && tr.GetObject(f.VpPlanta, OpenMode.ForRead) is Viewport vp && vp.CustomScale > 0 &&
                tr.GetObject(f.Id, OpenMode.ForRead) is Polyline pl && Vertices(pl) is Point3d[] v)
            {
                double w = v[0].DistanceTo(v[1]), h = v[0].DistanceTo(v[3]);
                if (w > vp.Width / vp.CustomScale * 1.01 || h > vp.Height / vp.CustomScale * 1.01)
                    sit.Add("frame maior que a viewport");
            }
            if (f.Aviso.Length > 0)
                sit.Add(f.Aviso);
            f.Situacao = sit.Count == 0 ? "ok" : string.Join("; ", sit);
        }
        tr.Commit();
        return lista;
    }

    private static SheetFrameInfo? LerFrame(Transaction tr, Database db, ObjectId id)
    {
        if (!Valido(id) || tr.GetObject(id, OpenMode.ForRead) is not Polyline pl)
            return null;
        Dictionary<string, string> kv = Kv(pl.GetXDataForApplication(App));
        if (kv.Count == 0)
            return null;
        bool distancias = kv.GetValueOrDefault("FV", "") == "2";
        var f = new SheetFrameInfo
        {
            Id = id,
            Eixo = DoHandle(db, kv.GetValueOrDefault("S", "")),
            Indice = (int)Num(kv.GetValueOrDefault("N", "0"), 0),
            E0 = Num(kv.GetValueOrDefault("E0", "0"), 0),
            E1 = Num(kv.GetValueOrDefault("E1", "0"), 0),
            F0 = Par(kv.GetValueOrDefault("F0", "")),
            F1 = Par(kv.GetValueOrDefault("F1", "")),
            Layout = DoHandle(db, kv.GetValueOrDefault("L", "")),
            VpPlanta = DoHandle(db, kv.GetValueOrDefault("VP", "")),
            VpPerfil = DoHandle(db, kv.GetValueOrDefault("VF", "")),
            Perfil = DoHandle(db, kv.GetValueOrDefault("PV", "")),
            Rotulo = DoHandle(db, kv.GetValueOrDefault("T", "")),
            Aviso = kv.GetValueOrDefault("AV", "")
        };
        if (!distancias && Vertices(pl) is Point3d[] vv)
        {
            // Formato antigo (fracoes do comprimento): converte para distancia a ponta.
            double w = vv[0].DistanceTo(vv[1]);
            f.F0 = new Point2d(f.F0.X * w, f.F0.Y);
            f.F1 = new Point2d((1 - f.F1.X) * w, f.F1.Y);
        }
        if (Valido(f.Layout) && tr.GetObject(f.Layout, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Layout lay)
            f.LayoutNome = lay.LayoutName;
        else
            f.Layout = ObjectId.Null;
        if (Valido(f.Perfil))
        {
            DBObject pv = tr.GetObject(f.Perfil, OpenMode.ForRead);
            f.PerfilNome = pv.GetType().GetProperty("Name")?.GetValue(pv) as string ?? "perfil";
        }
        else
        {
            f.Perfil = ObjectId.Null;
        }
        return f;
    }

    private static void GravarFrame(Polyline pl, SheetFrameInfo f)
    {
        var dados = new List<TypedValue> { new((int)DxfCode.ExtendedDataRegAppName, App) };
        void Add(string k, string v) => dados.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, k + "=" + v));
        Add("S", H(f.Eixo));
        Add("N", f.Indice.ToString(Inv));
        Add("E0", D(f.E0));
        Add("E1", D(f.E1));
        Add("FV", "2");
        Add("F0", D(f.F0.X) + ";" + D(f.F0.Y));
        Add("F1", D(f.F1.X) + ";" + D(f.F1.Y));
        Add("L", H(f.Layout));
        Add("VP", H(f.VpPlanta));
        Add("VF", H(f.VpPerfil));
        Add("PV", H(f.Perfil));
        Add("T", H(f.Rotulo));
        if (f.Aviso.Length > 0)
            Add("AV", f.Aviso.Length > 200 ? f.Aviso[..200] : f.Aviso);
        pl.XData = new ResultBuffer(dados.ToArray());
    }

    // ------------------------------------------------------------------
    // Listas para o painel
    // ------------------------------------------------------------------

    public static List<(ObjectId Id, string Nome, double Ini, double Fim)> Eixos(Database db)
    {
        var lista = new List<(ObjectId, string, double, double)>();
        try
        {
            CivilDocument civ = CivilDocument.GetCivilDocument(db);
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (ObjectId id in civ.GetAlignmentIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment al)
                    lista.Add((id, al.Name, al.StartingStation, al.EndingStation));
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
            // Desenho sem Civil: lista vazia.
        }
        return lista.OrderBy(a => a.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<(ObjectId Id, string Nome)> EstilosPerfil(Database db) => Estilos(db, "ProfileViewStyles");

    public static List<(ObjectId Id, string Nome)> BandasPerfil(Database db) => Estilos(db, "ProfileViewBandSetStyles");

    private static List<(ObjectId Id, string Nome)> Estilos(Database db, string colecao)
    {
        var lista = new List<(ObjectId, string)>();
        try
        {
            CivilDocument civ = CivilDocument.GetCivilDocument(db);
            object? estilos = civ.GetType().GetProperty("Styles")?.GetValue(civ);
            if (estilos?.GetType().GetProperty(colecao)?.GetValue(estilos) is not System.Collections.IEnumerable ids)
                return lista;
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (object o in ids)
            {
                if (o is not ObjectId id || !Valido(id))
                    continue;
                DBObject st = tr.GetObject(id, OpenMode.ForRead);
                lista.Add((id, st.GetType().GetProperty("Name")?.GetValue(st) as string ?? id.Handle.ToString()));
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
            // Sem estilos: o Civil usa o padrao.
        }
        return lista;
    }

    /// <summary>Le e valida o layout modelo e as viewports escolhidas.</summary>
    public static SheetTemplate LerModelo(Database db, SheetSeries s)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (s.Modelo.Length == 0 || !dic.Contains(s.Modelo))
            throw new InvalidOperationException("Escolha o layout modelo (passo 1).");
        var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(s.Modelo), OpenMode.ForRead);

        Viewport? Vp(ObjectId id)
        {
            if (!Valido(id) || tr.GetObject(id, OpenMode.ForRead) is not Viewport vp || vp.OwnerId != lay.BlockTableRecordId)
                return null;
            return vp;
        }

        Viewport planta = Vp(s.VpPlanta) ?? throw new InvalidOperationException($"Marque a viewport de planta do layout {s.Modelo} (passo 1).");
        if (planta.CustomScale <= 0)
            throw new InvalidOperationException("A viewport de planta do modelo nao tem escala definida.");
        Viewport? perfil = s.ComPerfil ? Vp(s.VpPerfil) : null;
        if (s.ComPerfil && perfil is null)
            throw new InvalidOperationException($"A viewport de perfil escolhida nao esta mais no layout {s.Modelo}: marque de novo ou escolha \"sem perfil\".");
        if (perfil is not null && perfil.ObjectId == planta.ObjectId)
            throw new InvalidOperationException("Planta e perfil estao na mesma viewport: escolha viewports diferentes.");

        var m = new SheetTemplate(lay.ObjectId, lay.LayoutName,
            planta.ObjectId, planta.Width, planta.Height, planta.CustomScale,
            perfil?.ObjectId ?? ObjectId.Null, perfil?.Width ?? 0, perfil?.Height ?? 0, perfil?.CustomScale ?? 0);
        tr.Commit();
        return m;
    }

    /// <summary>Quanto cabe por folha na planta e no perfil, e quantas folhas (estimativa).</summary>
    public static SheetCapacity? Capacidade(Database db, SheetSeries s)
    {
        try
        {
            SheetTemplate m = LerModelo(db, s);
            double planta = m.LarguraFrame - 2 * s.Folga;
            double perfil = m.TemPerfil ? CapacidadePerfil(s, m) : double.PositiveInfinity;
            double comp = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (tr.GetObject(s.Eixo, OpenMode.ForRead) is CivilDb.Alignment al)
                {
                    double ini = double.IsNaN(s.EstacaInicial) ? al.StartingStation : Math.Max(al.StartingStation, s.EstacaInicial);
                    double fim = double.IsNaN(s.EstacaFinal) ? al.EndingStation : Math.Min(al.EndingStation, s.EstacaFinal);
                    comp = Math.Max(0, fim - ini);
                }
                tr.Commit();
            }
            double porFolha = Math.Min(planta, perfil);
            int n = porFolha > 1 ? (int)Math.Ceiling(comp / porFolha - 1e-9) : 0;
            return new SheetCapacity(planta, perfil, !double.IsNaN(s.MargemPerfil), comp, n);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static double CapacidadePerfil(SheetSeries s, SheetTemplate m)
    {
        double w = m.LarguraPerfil;
        double margem = double.IsNaN(s.MargemPerfil) ? w * 0.12 : s.MargemPerfil;
        return w * 0.98 - margem;
    }

    /// <summary>Palpite das viewports de planta e perfil de um layout: perfil = a que mostra um profile view; senao planta = a maior.</summary>
    public static (ObjectId Planta, ObjectId Perfil) Palpite(Database db, SheetData folha)
    {
        var vps = folha.Viewports.Where(v => v.Planta).ToList();
        if (vps.Count == 0)
            return (ObjectId.Null, ObjectId.Null);
        if (vps.Count == 1)
            return (vps[0].Id, ObjectId.Null);

        ObjectId perfil = ObjectId.Null;
        try
        {
            List<Extents3d> pvs = ExtensoesDePerfis(db);
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (SheetViewport v in vps)
            {
                if (tr.GetObject(v.Id, OpenMode.ForRead) is not Viewport vp || vp.CustomScale <= 0)
                    continue;
                Point3d c = CentroDaVista(vp);
                if (pvs.Any(e => c.X >= e.MinPoint.X && c.X <= e.MaxPoint.X && c.Y >= e.MinPoint.Y && c.Y <= e.MaxPoint.Y))
                {
                    perfil = v.Id;
                    break;
                }
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
        }

        var resto = vps.Where(v => v.Id != perfil).OrderByDescending(v => v.Largura * v.Altura).ToList();
        ObjectId planta = resto[0].Id;
        if (perfil.IsNull && resto.Count > 1)
            perfil = resto.Skip(1).OrderByDescending(v => v.Largura).First().Id;
        return (planta, perfil);
    }

    /// <summary>
    /// Profile views do desenho com o eixo de cada um. A lista vem de Alignment.GetProfileViewIds
    /// (CivilDocument nao tem esse metodo).
    /// </summary>
    public static List<(ObjectId Id, ObjectId Eixo)> ProfileViewsDoDesenho(Database db)
    {
        var lista = new List<(ObjectId, ObjectId)>();
        try
        {
            CivilDocument civ = CivilDocument.GetCivilDocument(db);
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (ObjectId aid in civ.GetAlignmentIds())
            {
                DBObject al = tr.GetObject(aid, OpenMode.ForRead);
                if (al.GetType().GetMethod("GetProfileViewIds", Type.EmptyTypes)?.Invoke(al, null) is ObjectIdCollection ids)
                    foreach (ObjectId id in ids)
                        if (Valido(id))
                            lista.Add((id, aid));
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
            // Sem Civil: lista vazia.
        }
        return lista;
    }

    private static List<Extents3d> ExtensoesDePerfis(Database db)
    {
        var lista = new List<Extents3d>();
        List<ObjectId> ids = ProfileViewsDoDesenho(db).Select(p => p.Id).ToList();
        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId id in ids)
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e)
                    lista.Add(e.GeometricExtents);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }
        tr.Commit();
        return lista;
    }

    // ------------------------------------------------------------------
    // Gerar
    // ------------------------------------------------------------------

    /// <summary>
    /// Cria os frames ao longo do eixo (se a serie ainda nao tem) e, se pedido, as pranchas
    /// (layouts copiados do modelo) e os perfis das folhas que ainda nao tem. Depois alinha tudo.
    /// </summary>
    public static OpResult Gerar(Document doc, SheetSeries s, bool criarPranchas, Point3d? basePerfis)
    {
        Database db = doc.Database;
        using IDisposable __ = SheetSeriesSync.Suspender();
        using DocumentLock _ = doc.LockDocument();

        SheetTemplate m = LerModelo(db, s);
        var avisos = new List<string>();
        SheetSeries? gravada = LerSerie(db, s.Eixo);
        if (gravada is not null)
        {
            s.Frames.Clear();
            s.Frames.AddRange(gravada.Frames);
            s.Cortes.Clear();
            s.Cortes.AddRange(gravada.Cortes);
            if (double.IsNaN(s.MargemPerfil))
            {
                s.MargemPerfil = gravada.MargemPerfil;
                s.SobraVertical = gravada.SobraVertical;
                s.Exagero = gravada.Exagero;
            }
        }

        List<SheetFrameInfo> frames = LerFrames(db, s);
        int novosFrames = 0;
        double ini, fim;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            ini = double.IsNaN(s.EstacaInicial) ? al.StartingStation : Math.Clamp(s.EstacaInicial, al.StartingStation, al.EndingStation);
            fim = double.IsNaN(s.EstacaFinal) ? al.EndingStation : Math.Clamp(s.EstacaFinal, al.StartingStation, al.EndingStation);
            s.EixoNome = al.Name;
            tr.Commit();
        }
        if (fim - ini < 1)
            throw new InvalidOperationException("Trecho do eixo muito curto: confira as estacas inicial e final.");

        Point3d origemPerfis = basePerfis ?? BasePadraoPerfis(db, m);

        if (frames.Count == 0)
        {
            Calibracao = "";
            if (s.ComPerfil && m.TemPerfil)
                Calibrar(db, s, m, ini, fim, origemPerfis, avisos);
            if (Calibracao.Length > 0)
                doc.Editor.WriteMessage("\n[Pranchas] " + Calibracao);
            double maxPerfil = s.ComPerfil && m.TemPerfil ? CapacidadePerfil(s, m) : double.PositiveInfinity;
            if (maxPerfil < 5)
                throw new InvalidOperationException("A viewport de perfil comporta menos de 5 m de eixo: aumente a viewport ou mude a escala dela.");

            using Transaction tr = db.TransactionManager.StartTransaction();
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            List<FrameGeo> geos = Encaixar(ax, ini, fim, m.LarguraFrame, m.AlturaFrame, s.Folga, s.Arredondar, maxPerfil, s.Girar);

            GarantirApp(tr, db);
            GarantirLayer(tr, db, LayerFrame, 30, plotar: false);
            GarantirLayer(tr, db, LayerCorte, 1, plotar: true);
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite, false, true);
            s.Frames.Clear();
            for (int i = 0; i < geos.Count; i++)
            {
                FrameGeo g = geos[i];
                Point3d[] v = Cantos(g.Centro, g.U, m.LarguraFrame, m.AlturaFrame);
                var pl = new Polyline { Closed = true, Layer = LayerFrame };
                for (int k = 0; k < 4; k++)
                    pl.AddVertexAt(k, new Point2d(v[k].X, v[k].Y), 0, 0, 0);
                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);

                var f = new SheetFrameInfo
                {
                    Id = pl.ObjectId,
                    Eixo = s.Eixo,
                    Indice = i,
                    E0 = g.E0,
                    E1 = g.E1,
                    F0 = CorteLocal(v, ax.Ponto(g.E0), inicio: true),
                    F1 = CorteLocal(v, ax.Ponto(g.E1), inicio: false),
                    LayoutNome = s.NomeFolha(i)
                };
                f.Rotulo = Rotulo(tr, ms, f, v, m.EscalaPlanta, ObjectId.Null);
                GravarFrame(pl, f);
                s.Frames.Add(pl.ObjectId);
            }
            Gravar(tr, db, s);
            tr.Commit();
            novosFrames = geos.Count;
            frames = LerFrames(db, s);
        }
        else
        {
            // Serie existente: so grava as opcoes (estilos, numeracao, automatico).
            using Transaction tr = db.TransactionManager.StartTransaction();
            Gravar(tr, db, s);
            tr.Commit();
        }

        int novas = 0, perfis = 0;
        List<string> nomesNovos = new();
        if (criarPranchas)
            (novas, perfis, nomesNovos) = CriarPranchas(doc, s, m, frames, origemPerfis, avisos);
        else if (novosFrames == 0)
            return new OpResult(0, 0, "A serie ja tem frames. Use Atualizar para realinhar, ou Remover para gerar de novo.");

        SheetSeriesSync.Recarregar(db);
        OpResult r = AtualizarInterno(doc, s.Eixo, null, avisos);

        if (novas > 0)
        {
            if (s.Numerar)
                NumerarSerie(doc, s);
            try
            {
                if (LayoutKeyMap.Existe(db) && !LayoutKeyMap.Ler(db).Automatico)
                    LayoutKeyMap.Gerar(doc, false);
            }
            catch (System.Exception ex)
            {
                avisos.Add("mapa-chave: " + ex.Message);
            }
        }

        var msg = new List<string>();
        if (novosFrames > 0)
            msg.Add($"{novosFrames} frame(s) ao longo de {s.EixoNome}");
        if (criarPranchas)
            msg.Add(novas > 0 ? $"{novas} prancha(s) ({nomesNovos.First()} a {nomesNovos.Last()})" : "nenhuma prancha nova");
        if (perfis > 0)
            msg.Add($"{perfis} perfil(is)");
        string texto = string.Join(", ", msg) + ".";
        if (!criarPranchas)
            texto += " Ajuste os frames no Model se quiser e clique em Gerar pranchas.";
        if (avisos.Count > 0)
        {
            texto += " Avisos: " + string.Join("; ", avisos.Distinct().Take(3)) + (avisos.Count > 3 ? " (mais na linha de comando)" : "");
            doc.Editor.WriteMessage("\n[Pranchas] " + string.Join("\n[Pranchas] ", avisos.Distinct()));
        }
        return new OpResult(novosFrames + novas, r.Falhas, texto);
    }

    private static (int Pranchas, int Perfis, List<string> Nomes) CriarPranchas(Document doc, SheetSeries s, SheetTemplate m,
        List<SheetFrameInfo> frames, Point3d origemPerfis, List<string> avisos)
    {
        Database db = doc.Database;
        LayoutManager lm = LayoutManager.Current;
        List<string> ordem = LayoutOps.OrdemAtual(db);
        var existentes = new HashSet<string>(ordem, StringComparer.OrdinalIgnoreCase);
        int pranchas = 0, perfis = 0;
        var nomes = new List<string>();
        double passo = m.TemPerfil ? m.AlturaPerfil * 1.6 : 0;
        List<SheetFrameInfo> lista = frames.OrderBy(f => f.E0).ToList();

        for (int i = 0; i < lista.Count; i++)
        {
            SheetFrameInfo f = lista[i];
            bool precisaLayout = !Valido(f.Layout);
            bool precisaPerfil = s.ComPerfil && m.TemPerfil && !Valido(f.Perfil);
            if (!precisaLayout && !precisaPerfil)
                continue;

            string nome = f.LayoutNome;
            if (precisaLayout)
            {
                nome = s.NomeFolha(f.Indice);
                for (int k = 2; existentes.Contains(nome); k++)
                    nome = $"{s.NomeFolha(f.Indice)} ({k})";
                try
                {
                    lm.CopyLayout(s.Modelo, nome);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    avisos.Add($"copiar o modelo para {nome}: {ex.Message}");
                    continue;
                }
                existentes.Add(nome);
                f.LayoutNome = nome;
                nomes.Add(nome);
                pranchas++;

                // Aba logo depois da folha anterior da serie (ou antes da seguinte, ou depois do modelo).
                ordem.Remove(nome);
                string? antes = lista.Take(i).Select(x => x.LayoutNome).LastOrDefault(n => n.Length > 0 && ordem.Contains(n, StringComparer.OrdinalIgnoreCase));
                string? depois = lista.Skip(i + 1).Select(x => x.LayoutNome).FirstOrDefault(n => n.Length > 0 && ordem.Contains(n, StringComparer.OrdinalIgnoreCase));
                int pos;
                if (antes is not null)
                    pos = ordem.FindIndex(n => n.Equals(antes, StringComparison.OrdinalIgnoreCase)) + 1;
                else if (depois is not null)
                    pos = ordem.FindIndex(n => n.Equals(depois, StringComparison.OrdinalIgnoreCase));
                else
                    pos = ordem.FindIndex(n => n.Equals(s.Modelo, StringComparison.OrdinalIgnoreCase)) + 1;
                ordem.Insert(pos <= 0 ? ordem.Count : Math.Min(pos, ordem.Count), nome);
            }

            try
            {
                using Transaction tr = db.TransactionManager.StartTransaction();
                var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(precisaLayout ? dic.GetAt(nome) : f.Layout, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                f.Layout = lay.ObjectId;
                if (!Valido(f.VpPlanta))
                    f.VpPlanta = Correspondente(tr, btr, (Viewport)tr.GetObject(m.VpPlanta, OpenMode.ForRead));
                if (m.TemPerfil && !Valido(f.VpPerfil))
                    f.VpPerfil = Correspondente(tr, btr, (Viewport)tr.GetObject(m.VpPerfil, OpenMode.ForRead));
                if (f.VpPlanta.IsNull)
                    avisos.Add($"{nome}: viewport de planta nao encontrada na prancha");
                GravarFrame((Polyline)tr.GetObject(f.Id, OpenMode.ForWrite, false, true), f);
                tr.Commit();
            }
            catch (System.Exception ex)
            {
                avisos.Add($"{nome}: vinculo da prancha nao gravado ({ex.Message})");
                continue;
            }

            if (precisaPerfil && Valido(f.VpPerfil))
            {
                try
                {
                    Point3d origem = origemPerfis + new Vector3d(0, -passo * f.Indice, 0);
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        f.Perfil = CriarProfileView(tr, db, s.Eixo, origem, nome, s.EstiloPerfil, s.BandasPerfil);
                        tr.Commit();
                    }
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        GravarFrame((Polyline)tr.GetObject(f.Id, OpenMode.ForWrite, false, true), f);
                        tr.Commit();
                    }
                    perfis++;
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"{nome}: perfil nao criado ({ex.Message})");
                }
            }
        }

        if (pranchas > 0)
            LayoutOps.GravarOrdem(db, ordem);
        return (pranchas, perfis, nomes);
    }

    private static void NumerarSerie(Document doc, SheetSeries s)
    {
        try
        {
            List<SheetFrameInfo> frames = LerFrames(doc.Database, s);
            List<string> nomes = frames.Where(f => f.LayoutNome.Length > 0).Select(f => f.LayoutNome).ToList();
            if (nomes.Count == 0)
                return;
            List<SheetText2> marcados = LayoutNumbering.Buscar(doc.Database, new TextFilter { SoMarcados = true });
            if (!marcados.Any(t => nomes.Contains(t.Layout, StringComparer.OrdinalIgnoreCase)))
                return;
            var regra = new NumberingRule
            {
                Inicio = s.Inicio,
                Digitos = s.Digitos,
                Total = nomes.Count,
                SoLayoutsMarcados = true,
                Marcados = nomes
            };
            LayoutNumbering.Numerar(doc, regra);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\n[Pranchas] Numeracao: {ex.Message}");
        }
    }

    /// <summary>Viewport da copia que corresponde a do modelo (mesmo centro e tamanho no papel).</summary>
    private static ObjectId Correspondente(Transaction tr, BlockTableRecord btr, Viewport modelo)
    {
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Viewport v &&
                v.CenterPoint.DistanceTo(modelo.CenterPoint) < 1e-3 &&
                Math.Abs(v.Width - modelo.Width) < 1e-3 && Math.Abs(v.Height - modelo.Height) < 1e-3)
                return id;
        }
        return ObjectId.Null;
    }

    /// <summary>Perfis empilhados abaixo de tudo o que ha no Model (ou do eixo, se a extensao do desenho nao for valida).</summary>
    private static Point3d BasePadraoPerfis(Database db, SheetTemplate m)
    {
        double passo = m.TemPerfil ? m.AlturaPerfil * 1.6 : 100;
        Point3d min = db.Extmin, max = db.Extmax;
        if (min.X < max.X && min.Y < max.Y && max.X - min.X < 1e8)
            return new Point3d(min.X, min.Y - passo * 1.5, 0);
        return new Point3d(0, -passo * 2, 0);
    }

    // ------------------------------------------------------------------
    // Atualizar
    // ------------------------------------------------------------------

    /// <summary>
    /// Realinha a serie pelos frames: estacas dos cortes (pela posicao deles no frame), viewport de
    /// planta (centro e giro), intervalo e cotas do perfil, viewport de perfil e linhas de corte.
    /// </summary>
    public static OpResult Atualizar(Document doc, ObjectId eixo, ICollection<ObjectId>? soFrames)
    {
        using IDisposable __ = SheetSeriesSync.Suspender();
        using DocumentLock _ = doc.LockDocument();
        var avisos = new List<string>();
        OpResult r = AtualizarInterno(doc, eixo, soFrames, avisos);
        if (avisos.Count > 0)
            doc.Editor.WriteMessage("\n[Pranchas] " + string.Join("\n[Pranchas] ", avisos.Distinct()));
        return r;
    }

    /// <summary>Atualiza todas as series do desenho.</summary>
    public static OpResult AtualizarTodas(Document doc)
    {
        List<SheetSeries> series = LerSeries(doc.Database);
        if (series.Count == 0)
            return new OpResult(0, 0, "Nenhuma serie de pranchas no desenho.");
        int ok = 0, falhas = 0;
        var msgs = new List<string>();
        foreach (SheetSeries s in series)
        {
            OpResult r = Atualizar(doc, s.Eixo, null);
            ok += r.Ok;
            falhas += r.Falhas;
            msgs.Add($"{s.EixoNome}: {r.Mensagem}");
        }
        return new OpResult(ok, falhas, string.Join(" ", msgs));
    }

    private static OpResult AtualizarInterno(Document doc, ObjectId eixo, ICollection<ObjectId>? soFrames, List<string> avisos)
    {
        Database db = doc.Database;
        SheetSeries? s = LerSerie(db, eixo);
        if (s is null)
            return new OpResult(0, 0, "Serie de pranchas nao encontrada.");
        SheetTemplate? m = null;
        try
        {
            m = LerModelo(db, s);
        }
        catch (System.Exception)
        {
            // Sem modelo (apagado/renomeado): atualiza mesmo assim, sem ajuste de cotas.
        }

        List<SheetFrameInfo> frames = LerFrames(db, s);
        bool Alvo(SheetFrameInfo f) => soFrames is null || soFrames.Contains(f.Id);
        int ok = 0, falhas = 0;
        double escalaPlanta = m?.EscalaPlanta ?? 0;
        foreach (SheetFrameInfo f in frames.Where(f => f.LayoutNome.Length == 0))
            f.LayoutNome = s.NomeFolha(f.Indice);

        // 1. Estacas e planta.
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite, false, true);
            foreach (SheetFrameInfo f in frames.Where(Alvo))
            {
                try
                {
                    var pl = (Polyline)tr.GetObject(f.Id, OpenMode.ForWrite, false, true);
                    Point3d[]? v = Vertices(pl);
                    if (v is null)
                    {
                        avisos.Add($"{f.LayoutNome}: o frame precisa ter 4 vertices");
                        falhas++;
                        continue;
                    }
                    double e0 = Encaixe(ax.Estaca(CorteMundo(v, f.F0, inicio: true)), s.Arredondar);
                    double e1 = Encaixe(ax.Estaca(CorteMundo(v, f.F1, inicio: false)), s.Arredondar);
                    if (e1 < e0)
                        (e0, e1) = (e1, e0);
                    f.E0 = e0;
                    f.E1 = e1;
                    f.Aviso = "";
                    double escala = escalaPlanta;
                    if (Valido(f.VpPlanta) && tr.GetObject(f.VpPlanta, OpenMode.ForWrite, false, true) is Viewport vp)
                    {
                        AlinharPlanta(vp, v);
                        escala = vp.CustomScale;
                        if (escalaPlanta <= 0)
                            escalaPlanta = escala;
                    }
                    f.Rotulo = Rotulo(tr, ms, f, v, escala, f.Rotulo);
                    GravarFrame(pl, f);
                    ok++;
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"{f.LayoutNome}: planta nao atualizada ({ex.Message})");
                    falhas++;
                }
            }
            foreach (ObjectId apagado in s.FramesApagados)
                Apagar(tr, RotuloDeApagado(tr, db, apagado));
            tr.Commit();
        }

        // 2. Perfis.
        foreach (SheetFrameInfo f in frames.Where(Alvo).Where(f => Valido(f.Perfil)))
        {
            try
            {
                DefinirEstacasPerfil(db, f.Perfil, f.E0, f.E1);
                string? aviso = m is not null ? AjustarCotas(db, f.Perfil, s, m) : null;
                if (Valido(f.VpPerfil))
                    AlinharPerfil(db, f.VpPerfil, f.Perfil);
                if (aviso is not null)
                {
                    f.Aviso = aviso;
                    avisos.Add($"{f.LayoutNome}: {aviso}");
                }
            }
            catch (System.Exception ex)
            {
                f.Aviso = "perfil: " + ex.Message;
                avisos.Add($"{f.LayoutNome}: perfil nao atualizado ({ex.Message})");
                falhas++;
            }
        }

        // 3. Linhas de corte, avisos nos frames e serie (frames apagados saem da lista).
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            foreach (SheetFrameInfo f in frames.Where(Alvo))
            {
                try
                {
                    GravarFrame((Polyline)tr.GetObject(f.Id, OpenMode.ForWrite, false, true), f);
                }
                catch (System.Exception)
                {
                    // Aviso fica so na linha de comando.
                }
            }
            if (escalaPlanta <= 0)
                escalaPlanta = EscalaDeQualquerPlanta(tr, frames);
            RefazerCortes(tr, db, s, frames, ax, escalaPlanta);
            s.Frames.Clear();
            s.Frames.AddRange(frames.OrderBy(f => f.E0).Select(f => f.Id));
            Gravar(tr, db, s);
            tr.Commit();
        }

        return new OpResult(ok, falhas, $"{ok} folha(s) de {s.EixoNome} alinhada(s)" + (falhas > 0 ? $"; {falhas} falha(s) (detalhes na linha de comando)." : "."));
    }

    /// <summary>Muda as estacas de uma folha sem mexer no frame (os cortes andam dentro dele).</summary>
    public static OpResult DefinirEstacas(Document doc, ObjectId frame, double e0, double e1)
    {
        if (e1 - e0 < 1)
            return new OpResult(0, 1, "A estaca final precisa ser maior que a inicial.");
        Database db = doc.Database;
        ObjectId eixo;
        string aviso = "";
        using (SheetSeriesSync.Suspender())
        using (doc.LockDocument())
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            SheetFrameInfo f = LerFrame(tr, db, frame) ?? throw new InvalidOperationException("Frame nao encontrado.");
            var al = (CivilDb.Alignment)tr.GetObject(f.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            var pl = (Polyline)tr.GetObject(frame, OpenMode.ForWrite, false, true);
            Point3d[] v = Vertices(pl) ?? throw new InvalidOperationException("O frame precisa ter 4 vertices.");
            f.E0 = ax.Limitar(e0);
            f.E1 = ax.Limitar(e1);
            f.F0 = CorteLocal(v, ax.Ponto(f.E0), inicio: true);
            f.F1 = CorteLocal(v, ax.Ponto(f.E1), inicio: false);
            if (Fora(v, f.F0) || Fora(v, f.F1))
                aviso = " Atencao: o corte ficou fora do frame; mova ou estique o frame.";
            GravarFrame(pl, f);
            eixo = f.Eixo;
            tr.Commit();
        }
        OpResult r = Atualizar(doc, eixo, new[] { frame });
        return r with { Mensagem = r.Mensagem + aviso };
    }

    private static bool Fora(Point3d[] v, Point2d f)
    {
        double w = v[0].DistanceTo(v[1]);
        return f.X < -0.02 * w || f.X > 1.02 * w || f.Y < -0.02 || f.Y > 1.02;
    }

    /// <summary>Remove a serie: frames, rotulos e linhas de corte; com tudo=true tambem perfis e layouts.</summary>
    public static OpResult Remover(Document doc, ObjectId eixo, bool tudo)
    {
        Database db = doc.Database;
        SheetSeries? s = LerSerie(db, eixo);
        if (s is null)
            return new OpResult(0, 0, "Serie nao encontrada.");
        List<SheetFrameInfo> frames = LerFrames(db, s);
        var layouts = new List<string>();
        using (SheetSeriesSync.Suspender())
        using (doc.LockDocument())
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId apagado in s.FramesApagados)
                    Apagar(tr, RotuloDeApagado(tr, db, apagado));
                foreach (SheetFrameInfo f in frames)
                {
                    Apagar(tr, f.Rotulo);
                    if (tudo)
                        Apagar(tr, f.Perfil);
                    Apagar(tr, f.Id);
                    if (tudo && f.LayoutNome.Length > 0)
                        layouts.Add(f.LayoutNome);
                }
                foreach (ObjectId c in s.Cortes)
                    Apagar(tr, c);
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (nod.Contains(Dicionario))
                {
                    var dic = (DBDictionary)tr.GetObject(nod.GetAt(Dicionario), OpenMode.ForWrite, false, true);
                    if (dic.Contains(s.Chave))
                    {
                        ObjectId xr = dic.GetAt(s.Chave);
                        dic.Remove(s.Chave);
                        Apagar(tr, xr);
                    }
                }
                tr.Commit();
            }
        }
        string extra = "";
        if (layouts.Count > 0)
            extra = " " + LayoutOps.Excluir(doc, layouts).Mensagem;
        SheetSeriesSync.Recarregar(db);
        return new OpResult(frames.Count, 0, $"Serie de {s.EixoNome} removida ({frames.Count} frame(s)).{extra}");
    }

    /// <summary>Apaga se puder (mesmo em layer travada). Retorna true se o objeto nao existe mais.</summary>
    private static bool Apagar(Transaction tr, ObjectId id)
    {
        if (!Valido(id))
            return true;
        try
        {
            tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return false;
        }
    }

    /// <summary>Rotulo de um frame que o usuario apagou (le o XData do objeto apagado).</summary>
    private static ObjectId RotuloDeApagado(Transaction tr, Database db, ObjectId frame)
    {
        try
        {
            if (tr.GetObject(frame, OpenMode.ForRead, true) is Polyline pl)
                return DoHandle(db, Kv(pl.GetXDataForApplication(App)).GetValueOrDefault("T", ""));
        }
        catch (System.Exception)
        {
        }
        return ObjectId.Null;
    }

    private static double EscalaDeQualquerPlanta(Transaction tr, List<SheetFrameInfo> frames)
    {
        foreach (SheetFrameInfo f in frames)
            if (Valido(f.VpPlanta) && tr.GetObject(f.VpPlanta, OpenMode.ForRead) is Viewport vp && vp.CustomScale > 0)
                return vp.CustomScale;
        return 0;
    }

    /// <summary>Estaca a menos de 1 mm de um multiplo do arredondamento vira o multiplo (projecao do Civil tem ruido).</summary>
    private static double Encaixe(double estaca, double arred)
    {
        if (arred <= 0)
            return estaca;
        double r = Math.Round(estaca / arred) * arred;
        return Math.Abs(r - estaca) < 1e-3 ? r : estaca;
    }

    // ------------------------------------------------------------------
    // Viewports
    // ------------------------------------------------------------------

    /// <summary>Centro do frame no centro da viewport; aresta 0-1 do frame na horizontal do papel.</summary>
    private static void AlinharPlanta(Viewport vp, Point3d[] v)
    {
        bool travada = vp.Locked;
        vp.Locked = false;
        Vector3d u = v[1] - v[0];
        double twist = -Math.Atan2(u.Y, u.X);
        if (twist < 0)
            twist += 2 * Math.PI;
        vp.TwistAngle = twist;
        Point3d centro = new((v[0].X + v[2].X) / 2, (v[0].Y + v[2].Y) / 2, 0);
        Point3d c = centro.TransformBy(Dcs2Wcs(vp).Inverse());
        vp.ViewCenter = new Point2d(c.X, c.Y);
        vp.Locked = travada;
    }

    /// <summary>Viewport de perfil: inicio do profile view na borda esquerda, centrado na altura.</summary>
    private static void AlinharPerfil(Database db, ObjectId vpId, ObjectId pvId)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var pv = (Entity)tr.GetObject(pvId, OpenMode.ForRead);
        Extents3d ext = pv.GeometricExtents;
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite, false, true);
        if (vp.CustomScale <= 0)
        {
            tr.Commit();
            return;
        }
        bool travada = vp.Locked;
        vp.Locked = false;
        vp.TwistAngle = 0;
        double w = vp.Width / vp.CustomScale;
        var alvo = new Point3d(ext.MinPoint.X - w * 0.01 + w / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
        Point3d c = alvo.TransformBy(Dcs2Wcs(vp).Inverse());
        vp.ViewCenter = new Point2d(c.X, c.Y);
        vp.Locked = travada;
        tr.Commit();
    }

    private static Point3d CentroDaVista(Viewport vp) =>
        new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0).TransformBy(Dcs2Wcs(vp));

    private static Matrix3d Dcs2Wcs(Viewport vp) =>
        Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
        Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
        Matrix3d.PlaneToWorld(vp.ViewDirection);

    // ------------------------------------------------------------------
    // Profile view (por reflexao: o modulo nao depende da forma exata da API)
    // ------------------------------------------------------------------

    private static ObjectId CriarProfileView(Transaction tr, Database db, ObjectId eixo, Point3d origem, string nome, ObjectId estilo, ObjectId bandas)
    {
        Type tipo = typeof(CivilDocument).Assembly.GetType("Autodesk.Civil.DatabaseServices.ProfileView")
                    ?? throw new InvalidOperationException("tipo ProfileView nao encontrado na API do Civil");
        if (bandas.IsNull)
            bandas = BandasPerfil(db).Select(b => b.Id).FirstOrDefault();
        if (estilo.IsNull)
            estilo = EstilosPerfil(db).Select(b => b.Id).FirstOrDefault();

        // Mais nova primeiro: Create(alignmentId, insertPosition, name, bandSetId, styleId).
        var metodos = tipo.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(mt => mt.Name == "Create")
            .OrderBy(mt => mt.GetParameters().FirstOrDefault()?.ParameterType == typeof(ObjectId) ? 0 : 1).ToList();
        string ultimoErro = "nenhuma assinatura de ProfileView.Create reconhecida";
        foreach (string candidato in new[] { nome, nome + " (2)", nome + " (3)", $"{nome} {DateTime.Now:HHmmss}" })
        {
            foreach (MethodInfo mt in metodos)
            {
                ParameterInfo[] p = mt.GetParameters();
                object?[]? args = null;
                bool documento = false;
                if (p.Length == 5 && p[0].ParameterType == typeof(ObjectId) && p[1].ParameterType == typeof(Point3d) &&
                    p[2].ParameterType == typeof(string) && p[3].ParameterType == typeof(ObjectId) && p[4].ParameterType == typeof(ObjectId))
                {
                    args = new object?[] { eixo, origem, candidato, bandas, estilo };
                }
                else if (p.Length == 5 && p[0].ParameterType == typeof(CivilDocument) && p[1].ParameterType == typeof(string) &&
                         p[2].ParameterType == typeof(ObjectId) && p[3].ParameterType == typeof(ObjectId) && p[4].ParameterType == typeof(Point3d))
                {
                    args = new object?[] { CivilDocument.GetCivilDocument(db), candidato, bandas, eixo, origem };
                    documento = true;
                }
                if (args is null)
                    continue;
                try
                {
                    if (mt.Invoke(null, args) is ObjectId id && !id.IsNull)
                    {
                        if (documento && !estilo.IsNull)
                        {
                            DBObject pv = tr.GetObject(id, OpenMode.ForWrite, false, true);
                            pv.GetType().GetProperty("StyleId")?.SetValue(pv, estilo);
                        }
                        return id;
                    }
                }
                catch (TargetInvocationException ex)
                {
                    ultimoErro = ex.InnerException?.Message ?? ex.Message;
                }
            }
        }
        throw new InvalidOperationException("ProfileView.Create: " + ultimoErro);
    }

    private static void DefinirEstacasPerfil(Database db, ObjectId pvId, double e0, double e1)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        DBObject pv = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
        PropertyInfo modo = Prop(pv, "StationRangeMode") ?? throw Faltando(pv, "StationRangeMode", "Station");
        PropertyInfo ini = Prop(pv, "StationStart", "StartStation") ?? throw Faltando(pv, "StationStart", "Station");
        PropertyInfo fim = Prop(pv, "StationEnd", "EndStation") ?? throw Faltando(pv, "StationEnd", "Station");
        Chamar(() => DefinirEnum(pv, modo, "User"), "modo de estacas");
        Chamar(() => DefinirIntervalo(pv, ini, fim, e0, e1), "intervalo de estacas");
        tr.Commit();
    }

    /// <summary>
    /// Cotas: le o intervalo automatico (dados) e, se couber, abre o intervalo ate encher a altura da
    /// viewport de perfil. Retorna um aviso quando o perfil nao cabe.
    /// </summary>
    private static string? AjustarCotas(Database db, ObjectId pvId, SheetSeries s, SheetTemplate m)
    {
        PropertyInfo? modo, pMin, pMax;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            DBObject pv = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
            modo = Prop(pv, "ElevationRangeMode");
            pMin = Prop(pv, "ElevationMin", "MinimumElevation");
            pMax = Prop(pv, "ElevationMax", "MaximumElevation");
            if (modo is null || pMin is null || pMax is null)
            {
                tr.Commit();
                return s.AjustarCotas ? "cotas automaticas (API sem ElevationRangeMode)" : null;
            }
            if (!s.AjustarCotas || !m.TemPerfil)
            {
                // Opcao desligada: as cotas do profile view ficam como o usuario deixou.
                tr.Commit();
                return null;
            }
            Chamar(() => DefinirEnum(pv, modo, "Auto"), "modo de cotas");
            tr.Commit();
        }

        double emin, emax;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
            emin = Convert.ToDouble(pMin.GetValue(pv), Inv);
            emax = Convert.ToDouble(pMax.GetValue(pv), Inv);
            tr.Commit();
        }

        double a = s.SobraVertical, b = s.Exagero;
        if (double.IsNaN(a) || double.IsNaN(b) || b <= 0)
            return "cotas automaticas (perfil nao calibrado)";
        double disponivel = m.AlturaPerfil * 0.94;
        double faixa = (disponivel - a) / b;
        double dados = emax - emin;
        if (faixa < dados - 1e-6)
            return $"perfil mais alto que a viewport (precisa {(a + b * dados) * m.EscalaPerfil:0} mm, ha {m.HPerfil:0} mm): reduza o exagero vertical ou aumente a viewport";

        double meio = (emin + emax) / 2;
        double lo = Math.Floor(meio - faixa / 2);
        double hi = lo + Math.Floor(faixa);
        if (lo > emin)
            lo = Math.Floor(emin);
        if (hi < emax)
            hi = Math.Ceiling(emax);
        if (hi - lo > faixa + 1e-6 && faixa >= dados)
        {
            lo = emin;
            hi = emin + faixa;
        }
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            DBObject pv = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
            Chamar(() => DefinirEnum(pv, modo, "User"), "modo de cotas");
            Chamar(() => DefinirIntervalo(pv, pMin, pMax, lo, hi), "intervalo de cotas");
            tr.Commit();
        }
        return null;
    }

    /// <summary>
    /// Mede um profile view de teste: margem horizontal (eixos e rotulos alem das estacas), altura
    /// alem do grafico (bandas) e exagero vertical. Com isso a capacidade do perfil e as cotas saem
    /// certas para o estilo e o band set escolhidos.
    /// </summary>
    private static void Calibrar(Database db, SheetSeries s, SheetTemplate m, double ini, double fim, Point3d origem, List<string> avisos)
    {
        double lt = Math.Min(200, fim - ini);
        ObjectId pv = ObjectId.Null;
        try
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                pv = CriarProfileView(tr, db, s.Eixo, origem + new Vector3d(0, m.AlturaPerfil * 3, 0), "C3D calibracao " + DateTime.Now.Ticks, s.EstiloPerfil, s.BandasPerfil);
                tr.Commit();
            }
            DefinirEstacasPerfil(db, pv, ini, ini + lt);

            PropertyInfo? modo = null, pMin = null, pMax = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject o = tr.GetObject(pv, OpenMode.ForWrite, false, true);
                modo = Prop(o, "ElevationRangeMode");
                pMin = Prop(o, "ElevationMin", "MinimumElevation");
                pMax = Prop(o, "ElevationMax", "MaximumElevation");
                if (modo is not null)
                    Chamar(() => DefinirEnum(o, modo, "Auto"), "modo de cotas");
                tr.Commit();
            }

            double w, h, emin = double.NaN, emax = double.NaN, ve = double.NaN;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var e = (Entity)tr.GetObject(pv, OpenMode.ForRead);
                Extents3d ext = e.GeometricExtents;
                w = ext.MaxPoint.X - ext.MinPoint.X;
                h = ext.MaxPoint.Y - ext.MinPoint.Y;
                if (pMin is not null && pMax is not null)
                {
                    emin = Convert.ToDouble(pMin.GetValue(e), Inv);
                    emax = Convert.ToDouble(pMax.GetValue(e), Inv);
                }
                ve = ExageroDoEstilo(tr, e);
                tr.Commit();
            }
            s.MargemPerfil = Math.Max(0, w - lt);

            if ((double.IsNaN(ve) || ve <= 0) && modo is not null && pMin is not null && pMax is not null && !double.IsNaN(emin))
            {
                // Sem exagero no estilo: mede abrindo a faixa de cotas em 10 m.
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    DBObject o = tr.GetObject(pv, OpenMode.ForWrite, false, true);
                    Chamar(() => DefinirEnum(o, modo, "User"), "modo de cotas");
                    Chamar(() => DefinirIntervalo(o, pMin, pMax, emin, emax + 10), "intervalo de cotas");
                    tr.Commit();
                }
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var e = (Entity)tr.GetObject(pv, OpenMode.ForRead);
                    Extents3d ext = e.GeometricExtents;
                    ve = (ext.MaxPoint.Y - ext.MinPoint.Y - h) / 10.0;
                    tr.Commit();
                }
            }

            if (ve > 0 && !double.IsNaN(emin))
            {
                s.Exagero = ve;
                s.SobraVertical = Math.Max(0, h - ve * (emax - emin));
            }
            else
            {
                avisos.Add("exagero vertical nao identificado: cotas ficam automaticas");
            }
            Calibracao = $"calibracao do perfil: {lt:0} m de eixo ocupam {w:0.#} m (margem {s.MargemPerfil:0.#} m), altura {h:0.#} m, exagero {(double.IsNaN(s.Exagero) ? "?" : s.Exagero.ToString("0.##", Inv))}";
        }
        catch (System.Exception ex)
        {
            avisos.Add($"calibracao do perfil falhou ({ex.Message}); usando margem estimada");
        }
        finally
        {
            if (!pv.IsNull)
            {
                using Transaction tr = db.TransactionManager.StartTransaction();
                Apagar(tr, pv);
                tr.Commit();
            }
        }
    }

    /// <summary>Ultima medicao da calibracao (mostrada na linha de comando ao gerar).</summary>
    private static string Calibracao = "";

    private static double ExageroDoEstilo(Transaction tr, DBObject pv)
    {
        try
        {
            if (pv.GetType().GetProperty("StyleId")?.GetValue(pv) is not ObjectId sid || !Valido(sid))
                return double.NaN;
            DBObject st = tr.GetObject(sid, OpenMode.ForRead);
            object? graph = st.GetType().GetProperty("GraphStyle")?.GetValue(st);
            object? ve = graph?.GetType().GetProperty("VerticalExaggeration")?.GetValue(graph);
            return ve is null ? double.NaN : Convert.ToDouble(ve, Inv);
        }
        catch (System.Exception)
        {
            return double.NaN;
        }
    }

    private static PropertyInfo? Prop(object o, params string[] nomes)
    {
        foreach (string n in nomes)
        {
            PropertyInfo? p = o.GetType().GetProperty(n);
            if (p is not null)
                return p;
        }
        return null;
    }

    private static InvalidOperationException Faltando(object o, string nome, string contendo) =>
        new($"{o.GetType().Name} nao tem {nome} (propriedades com \"{contendo}\": " +
            string.Join(", ", o.GetType().GetProperties().Where(p => p.Name.Contains(contendo, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name)) + ")");

    private static void DefinirEnum(object o, PropertyInfo p, string contendo)
    {
        if (!p.PropertyType.IsEnum)
            throw new InvalidOperationException($"{p.Name} nao e enum ({p.PropertyType.Name})");
        string? nome = Enum.GetNames(p.PropertyType).FirstOrDefault(n => n.Contains(contendo, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"{p.PropertyType.Name} sem valor \"{contendo}\" ({string.Join(", ", Enum.GetNames(p.PropertyType))})");
        object valor = Enum.Parse(p.PropertyType, nome);
        if (!Equals(p.GetValue(o), valor))
            p.SetValue(o, valor);
    }

    /// <summary>Muda um intervalo sem passar por inicio maior que fim (a API recusa).</summary>
    private static void DefinirIntervalo(object o, PropertyInfo pMin, PropertyInfo pMax, double min, double max)
    {
        double atualMax = Convert.ToDouble(pMax.GetValue(o), Inv);
        if (min >= atualMax)
        {
            pMax.SetValue(o, max);
            pMin.SetValue(o, min);
        }
        else
        {
            pMin.SetValue(o, min);
            pMax.SetValue(o, max);
        }
    }

    private static void Chamar(Action acao, string oque)
    {
        try
        {
            acao();
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException($"{oque} recusado: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Encaixe dos frames ao longo do eixo
    // ------------------------------------------------------------------

    private sealed record FrameGeo(double E0, double E1, Point3d Centro, Vector3d U);

    /// <summary>Acesso ao eixo: ponto, tangente e estaca de um ponto.</summary>
    private sealed class Eixo
    {
        private readonly CivilDb.Alignment _a;

        public Eixo(CivilDb.Alignment a)
        {
            _a = a;
            Ini = a.StartingStation;
            Fim = a.EndingStation;
        }

        public double Ini { get; }
        public double Fim { get; }

        public double Limitar(double s) => Math.Clamp(s, Ini, Fim);

        public Point3d Ponto(double s)
        {
            double e = 0, n = 0;
            _a.PointLocation(Limitar(s), 0.0, ref e, ref n);
            return new Point3d(e, n, 0);
        }

        public Vector3d Tangente(double s)
        {
            double d = Math.Min(0.5, (Fim - Ini) / 4);
            double a = Limitar(s - d), b = Limitar(s + d);
            Vector3d v = Ponto(b) - Ponto(a);
            return v.Length < 1e-12 ? Vector3d.XAxis : v.GetNormal();
        }

        public double Estaca(Point3d p)
        {
            try
            {
                double st = 0, off = 0;
                bool fora = false;
                _a.StationOffsetAcceptOutOfRange(p.X, p.Y, ref st, ref off, ref fora);
                return Limitar(st);
            }
            catch (System.Exception)
            {
                // Ponto que a API nao projeta: busca o ponto mais proximo.
                double melhor = Ini, dist = double.MaxValue;
                int n = 400;
                for (int i = 0; i <= n; i++)
                {
                    double s = Ini + (Fim - Ini) * i / n;
                    double d = Ponto(s).DistanceTo(p);
                    if (d < dist)
                    {
                        dist = d;
                        melhor = s;
                    }
                }
                double passo = (Fim - Ini) / n;
                for (int k = 0; k < 30 && passo > 1e-4; k++)
                {
                    double a = Limitar(melhor - passo), b = Limitar(melhor + passo);
                    if (Ponto(a).DistanceTo(p) < dist) { melhor = a; dist = Ponto(a).DistanceTo(p); }
                    else if (Ponto(b).DistanceTo(p) < dist) { melhor = b; dist = Ponto(b).DistanceTo(p); }
                    else passo /= 2;
                }
                return melhor;
            }
        }
    }

    /// <summary>
    /// Divide o trecho em frames: cada frame vai o mais longe possivel com o eixo dentro da area
    /// util (frame menos a folga) e sem passar da capacidade do perfil; o corte e arredondado
    /// para baixo (multiplos de "arred", ex.: 20 m = 1 estaca).
    /// </summary>
    private static List<FrameGeo> Encaixar(Eixo ax, double ini, double fim, double w, double h, double folga, double arred, double maxPerfil, bool girar)
    {
        double uw = w - 2 * folga, uh = h - 2 * folga;
        if (uw <= 1 || uh <= 1)
            throw new InvalidOperationException("A folga e maior que o frame: reduza a folga ou aumente a escala da viewport de planta.");

        var lista = new List<FrameGeo>();
        double s0 = ini;
        double minimo = Math.Max(1.0, Math.Min(uw * 0.05, arred > 0 ? arred : 5));
        for (int guarda = 0; fim - s0 > 0.01 && guarda < 5000; guarda++)
        {
            double teto = Math.Min(fim, s0 + Math.Min(maxPerfil, uw * 2.5));
            double s1;
            if (Cabe(ax, s0, teto, uw, uh, girar, out _))
            {
                s1 = teto;
            }
            else
            {
                double lo = s0, hi = teto;
                for (int k = 0; k < 40 && hi - lo > 0.01; k++)
                {
                    double meio = (lo + hi) / 2;
                    if (Cabe(ax, s0, meio, uw, uh, girar, out _))
                        lo = meio;
                    else
                        hi = meio;
                }
                s1 = lo;
            }

            if (fim - s1 > 0.01 && arred > 0)
            {
                double r = Math.Floor((s1 + 1e-6) / arred) * arred;
                if (r > s0 + 1)
                    s1 = r;
            }
            // Sobra pequena no fim: estica este frame ate o fim se couber; senao recua um modulo
            // para a ultima folha nao sair quase vazia.
            if (fim - s1 > 0.01 && fim - s1 < minimo)
            {
                if (fim - s0 <= Math.Min(maxPerfil, uw * 2.5) && Cabe(ax, s0, fim, uw, uh, girar, out _))
                    s1 = fim;
                else if (arred > 0 && s1 - arred > s0 + 1)
                    s1 -= arred;
            }
            if (s1 - s0 < 1 && fim - s0 < 1)
                break;
            if (s1 - s0 < 1)
                throw new InvalidOperationException($"O eixo nao cabe no frame a partir da estaca {LayoutViewFrames.Estaca(s0)}: aumente a viewport de planta, reduza a escala ou a folga.");

            Cabe(ax, s0, s1, uw, uh, girar, out FrameGeo geo);
            lista.Add(geo);
            s0 = s1;
        }
        return lista;
    }

    private static bool Cabe(Eixo ax, double s0, double s1, double uw, double uh, bool girar, out FrameGeo geo)
    {
        int n = (int)Math.Clamp((s1 - s0) / 2.0, 8, 240);
        var pts = new Point3d[n + 1];
        for (int i = 0; i <= n; i++)
            pts[i] = ax.Ponto(s0 + (s1 - s0) * i / n);

        Vector3d u = Vector3d.XAxis;
        if (girar)
        {
            Vector3d corda = pts[n] - pts[0];
            u = corda.Length > 1e-6 ? corda.GetNormal() : ax.Tangente(s0);
        }
        var nn = new Vector3d(-u.Y, u.X, 0);
        double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
        foreach (Point3d p in pts)
        {
            Vector3d d = p - pts[0];
            double a = d.DotProduct(u), b = d.DotProduct(nn);
            umin = Math.Min(umin, a); umax = Math.Max(umax, a);
            vmin = Math.Min(vmin, b); vmax = Math.Max(vmax, b);
        }
        Point3d centro = pts[0] + u * ((umin + umax) / 2) + nn * ((vmin + vmax) / 2);
        geo = new FrameGeo(s0, s1, centro, u);
        return umax - umin <= uw + 1e-9 && vmax - vmin <= uh + 1e-9;
    }

    /// <summary>Retangulo: 0-1 ao longo do eixo (embaixo), 1-2 e 3-0 nas pontas.</summary>
    private static Point3d[] Cantos(Point3d c, Vector3d u, double w, double h)
    {
        var n = new Vector3d(-u.Y, u.X, 0);
        return new[]
        {
            c - u * (w / 2) - n * (h / 2), c + u * (w / 2) - n * (h / 2),
            c + u * (w / 2) + n * (h / 2), c - u * (w / 2) + n * (h / 2)
        };
    }

    private static Point3d[]? Vertices(Polyline pl)
    {
        if (pl.NumberOfVertices < 4)
            return null;
        return new[] { pl.GetPoint3dAt(0), pl.GetPoint3dAt(1), pl.GetPoint3dAt(2), pl.GetPoint3dAt(3) };
    }

    /// <summary>Coordenadas (s, t) de p no paralelogramo v0 + s(v1-v0) + t(v3-v0) (sistema 2x2 exato).</summary>
    private static (double S, double T) Parametros(Point3d[] v, Point3d p)
    {
        Vector3d a = v[1] - v[0], b = v[3] - v[0], d = p - v[0];
        double det = a.X * b.Y - a.Y * b.X;
        if (Math.Abs(det) < 1e-12)
            return (0, 0.5);
        return ((d.X * b.Y - d.Y * b.X) / det, (a.X * d.Y - a.Y * d.X) / det);
    }

    private static Point2d CorteLocal(Point3d[] v, Point3d p, bool inicio)
    {
        (double sp, double tp) = Parametros(v, p);
        double w = v[0].DistanceTo(v[1]);
        return new Point2d(inicio ? sp * w : (1 - sp) * w, tp);
    }

    private static Point3d CorteMundo(Point3d[] v, Point2d f, bool inicio)
    {
        double w = Math.Max(v[0].DistanceTo(v[1]), 1e-9);
        double sp = inicio ? f.X / w : 1 - f.X / w;
        return v[0] + (v[1] - v[0]) * sp + (v[3] - v[0]) * f.Y;
    }

    // ------------------------------------------------------------------
    // Rotulos e linhas de corte
    // ------------------------------------------------------------------

    private static ObjectId Rotulo(Transaction tr, BlockTableRecord ms, SheetFrameInfo f, Point3d[] v, double escala, ObjectId existente)
    {
        Vector3d u = (v[1] - v[0]).GetNormal(), n = (v[3] - v[0]).GetNormal();
        double alt = escala > 0 ? 3.0 / escala : v[0].DistanceTo(v[3]) / 60;
        MText t;
        if (Valido(existente) && tr.GetObject(existente, OpenMode.ForWrite, false, true) is MText mt)
        {
            t = mt;
        }
        else
        {
            t = new MText { Layer = LayerFrame };
            ms.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
        }
        t.Attachment = AttachmentPoint.TopLeft;
        t.TextHeight = alt;
        t.Location = v[3] + u * (alt * 0.8) - n * (alt * 0.8);
        t.Rotation = Math.Atan2(u.Y, u.X);
        t.Contents = $"{f.LayoutNome}\\P{LayoutViewFrames.Estaca(f.E0)} a {LayoutViewFrames.Estaca(f.E1)}";
        return t.ObjectId;
    }

    private static void RefazerCortes(Transaction tr, Database db, SheetSeries s, List<SheetFrameInfo> frames, Eixo ax, double escala)
    {
        var restantes = s.Cortes.Where(id => !Apagar(tr, id) && Valido(id)).ToList();
        s.Cortes.Clear();
        s.Cortes.AddRange(restantes);
        GarantirLayer(tr, db, LayerCorte, 1, plotar: true);
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite, false, true);
        List<SheetFrameInfo> ordem = frames.OrderBy(f => f.E0).ToList();

        for (int i = 0; i + 1 < ordem.Count; i++)
        {
            SheetFrameInfo a = ordem[i], b = ordem[i + 1];
            double altura = AlturaFrame(tr, a);
            if (Math.Abs(a.E1 - b.E0) <= 0.5)
            {
                Corte(tr, ms, s, ax, (a.E1 + b.E0) / 2, a.LayoutNome, b.LayoutNome, altura, escala);
            }
            else
            {
                Corte(tr, ms, s, ax, a.E1, a.LayoutNome, b.LayoutNome, altura, escala);
                Corte(tr, ms, s, ax, b.E0, a.LayoutNome, b.LayoutNome, altura, escala);
            }
        }
    }

    private static double AlturaFrame(Transaction tr, SheetFrameInfo f)
    {
        if (tr.GetObject(f.Id, OpenMode.ForRead) is Polyline pl && Vertices(pl) is Point3d[] v)
            return v[0].DistanceTo(v[3]);
        return 50;
    }

    private static void Corte(Transaction tr, BlockTableRecord ms, SheetSeries s, Eixo ax, double estaca, string antes, string depois, double alturaFrame, double escala)
    {
        Point3d p = ax.Ponto(estaca);
        Vector3d t = ax.Tangente(estaca);
        var n = new Vector3d(-t.Y, t.X, 0);
        double meia = alturaFrame * 0.42;
        double alt = escala > 0 ? 2.5 / escala : alturaFrame / 80;
        double rot = Math.Atan2(n.Y, n.X);

        var linha = new Line(p - n * meia, p + n * meia) { Layer = LayerCorte };
        Marcar(tr, ms, s, linha);

        MText Texto(string conteudo, Point3d onde)
        {
            var m = new MText
            {
                Layer = LayerCorte,
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = alt,
                Location = onde,
                Rotation = rot,
                Contents = conteudo
            };
            Marcar(tr, ms, s, m);
            return m;
        }

        Texto($"LINHA DE CORTE - EST. {LayoutViewFrames.Estaca(estaca)}", p + n * (meia + alt * 1.5));
        Texto($"VER FOLHA {antes}", p + n * (meia * 0.55) - t * (alt * 1.3));
        Texto($"VER FOLHA {depois}", p + n * (meia * 0.55) + t * (alt * 1.3));
    }

    private static void Marcar(Transaction tr, BlockTableRecord ms, SheetSeries s, Entity e)
    {
        ms.AppendEntity(e);
        tr.AddNewlyCreatedDBObject(e, true);
        e.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "S=" + H(s.Eixo)),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "K=corte"));
        s.Cortes.Add(e.ObjectId);
    }

    // ------------------------------------------------------------------
    // Utilitarios
    // ------------------------------------------------------------------

    /// <summary>"17+10,00" (estacas de 20 m) ou metros ("350", "350,5").</summary>
    public static bool TentarEstaca(string texto, out double estaca)
    {
        estaca = double.NaN;
        string t = texto.Trim().Replace(" ", "");
        if (t.Length == 0)
            return false;
        int mais = t.IndexOf('+');
        if (mais >= 0)
        {
            if (!double.TryParse(t[..mais].Replace(',', '.'), NumberStyles.Float, Inv, out double est) ||
                !double.TryParse(t[(mais + 1)..].Replace(',', '.'), NumberStyles.Float, Inv, out double resto))
                return false;
            estaca = est * 20 + resto;
            return true;
        }
        return double.TryParse(t.Replace(',', '.'), NumberStyles.Float, Inv, out estaca);
    }

    public static bool Valido(ObjectId id) => !id.IsNull && id.IsValid && !id.IsErased;

    private static string H(ObjectId id) => id.IsNull ? "" : id.Handle.ToString();

    private static string D(double v) => double.IsNaN(v) ? "" : v.ToString("R", Inv);

    private static double Num(string s, double padrao = double.NaN) =>
        double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : padrao;

    private static Point2d Par(string s)
    {
        string[] p = s.Split(';');
        return p.Length == 2 ? new Point2d(Num(p[0], 0), Num(p[1], 0)) : new Point2d(0, 0.5);
    }

    private static Dictionary<string, string> Kv(ResultBuffer? rb)
    {
        var kv = new Dictionary<string, string>();
        if (rb is null)
            return kv;
        using (rb)
        {
            foreach (TypedValue tv in rb.AsArray())
            {
                if (tv.TypeCode != (int)DxfCode.ExtendedDataAsciiString || tv.Value is not string t)
                    continue;
                int i = t.IndexOf('=');
                if (i > 0)
                    kv[t[..i]] = t[(i + 1)..];
            }
        }
        return kv;
    }

    private static ObjectId DoHandle(Database db, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return ObjectId.Null;
        try
        {
            return db.GetObjectId(false, new Handle(Convert.ToInt64(handle, 16)), 0);
        }
        catch (System.Exception)
        {
            return ObjectId.Null;
        }
    }

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

    private static void GarantirLayer(Transaction tr, Database db, string nome, short cor, bool plotar)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(nome))
            return;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = nome, Color = AcColor.FromColorIndex(AcColorMethod.ByAci, cor), IsPlottable = plotar };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }
}

/// <summary>
/// Atualizacao automatica das series: mover, girar, esticar ou apagar um frame, ou editar o eixo,
/// realinha as folhas daquele eixo no proximo Idle (fora de comando).
/// </summary>
internal static class SheetSeriesSync
{
    private static readonly Dictionary<Database, Dictionary<ObjectId, ObjectId>> FramesPorDb = new();
    private static readonly Dictionary<Database, HashSet<ObjectId>> EixosPorDb = new();
    private static readonly Dictionary<Database, Dictionary<ObjectId, HashSet<ObjectId>?>> Sujos = new();
    private static readonly HashSet<Database> Ligados = new();
    private static bool _iniciado;
    private static int _suspenso;

    /// <summary>Disparado depois de uma atualizacao automatica (o painel se rele).</summary>
    public static event EventHandler? Changed;

    public static void Initialize()
    {
        if (_iniciado)
            return;
        _iniciado = true;
        AcApp.DocumentManager.DocumentCreated += (_, e) => Ligar(e.Document);
        foreach (Document d in AcApp.DocumentManager)
            Ligar(d);
        AcApp.Idle += OnIdle;
    }

    public static void Terminate()
    {
        if (!_iniciado)
            return;
        AcApp.Idle -= OnIdle;
        _iniciado = false;
    }

    /// <summary>Enquanto vivo, mudancas feitas pelo proprio plugin nao disparam atualizacao.</summary>
    public static IDisposable Suspender()
    {
        _suspenso++;
        return new Fim();
    }

    private sealed class Fim : IDisposable
    {
        private bool _feito;

        public void Dispose()
        {
            if (_feito)
                return;
            _feito = true;
            _suspenso--;
        }
    }

    /// <summary>Rele quais frames e eixos observar (chamado ao gerar, remover e mudar opcoes).</summary>
    public static void Recarregar(Database db)
    {
        var frames = new Dictionary<ObjectId, ObjectId>();
        var eixos = new HashSet<ObjectId>();
        try
        {
            foreach (SheetSeries s in LayoutSheetSeries.LerSeries(db).Where(s => s.Automatico))
            {
                eixos.Add(s.Eixo);
                foreach (ObjectId f in s.Frames)
                    frames[f] = s.Eixo;
            }
        }
        catch (System.Exception)
        {
            // Desenho sem Civil ou NOD ilegivel: nada a observar.
        }
        FramesPorDb[db] = frames;
        EixosPorDb[db] = eixos;
    }

    private static void Ligar(Document? doc)
    {
        if (doc is null)
            return;
        Database db = doc.Database;
        if (!Ligados.Add(db))
            return;
        db.ObjectModified += OnObjeto;
        db.ObjectErased += OnApagado;
        doc.CommandEnded += OnComando;
        doc.BeginDocumentClose += (_, _) => Desligar(doc);
        Recarregar(db);
    }

    private static void Desligar(Document doc)
    {
        Database db = doc.Database;
        if (!Ligados.Remove(db))
            return;
        db.ObjectModified -= OnObjeto;
        db.ObjectErased -= OnApagado;
        doc.CommandEnded -= OnComando;
        FramesPorDb.Remove(db);
        EixosPorDb.Remove(db);
        Sujos.Remove(db);
    }

    // Desfazer/refazer pode trazer de volta (ou tirar) uma serie inteira: rele o que observar.
    private static readonly HashSet<string> Desfazer = new(StringComparer.OrdinalIgnoreCase) { "U", "UNDO", "REDO", "MREDO", "OOPS" };

    private static void OnComando(object? sender, CommandEventArgs e)
    {
        if (sender is Document doc && Desfazer.Contains(e.GlobalCommandName ?? ""))
        {
            Recarregar(doc.Database);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void Marcar(Database db, ObjectId eixo, ObjectId? frame)
    {
        if (!Sujos.TryGetValue(db, out Dictionary<ObjectId, HashSet<ObjectId>?>? porEixo))
            Sujos[db] = porEixo = new Dictionary<ObjectId, HashSet<ObjectId>?>();
        if (frame is null)
        {
            porEixo[eixo] = null; // tudo
            return;
        }
        if (porEixo.TryGetValue(eixo, out HashSet<ObjectId>? set))
        {
            set?.Add(frame.Value);
            return;
        }
        porEixo[eixo] = new HashSet<ObjectId> { frame.Value };
    }

    private static void OnObjeto(object? sender, ObjectEventArgs e)
    {
        // Durante o UNDO o banco volta a um estado coerente sozinho: nao refaz nada.
        if (_suspenso > 0 || sender is not Database db || e.DBObject.IsUndoing)
            return;
        ObjectId id = e.DBObject.ObjectId;
        if (FramesPorDb.TryGetValue(db, out Dictionary<ObjectId, ObjectId>? frames) && frames.TryGetValue(id, out ObjectId eixo))
            Marcar(db, eixo, id);
        else if (EixosPorDb.TryGetValue(db, out HashSet<ObjectId>? eixos) && eixos.Contains(id))
            Marcar(db, id, null);
    }

    private static void OnApagado(object? sender, ObjectErasedEventArgs e)
    {
        if (_suspenso > 0 || sender is not Database db || e.DBObject.IsUndoing)
            return;
        ObjectId id = e.DBObject.ObjectId;
        if (FramesPorDb.TryGetValue(db, out Dictionary<ObjectId, ObjectId>? frames) && frames.TryGetValue(id, out ObjectId eixo))
            Marcar(db, eixo, null);
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (Sujos.Count == 0 || _suspenso > 0)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !Sujos.TryGetValue(doc.Database, out Dictionary<ObjectId, HashSet<ObjectId>?>? porEixo) || !doc.Editor.IsQuiescent)
            return;
        Sujos.Remove(doc.Database);

        foreach ((ObjectId eixo, HashSet<ObjectId>? frames) in porEixo)
        {
            try
            {
                OpResult r = LayoutSheetSeries.Atualizar(doc, eixo, frames);
                doc.Editor.WriteMessage($"\n[Pranchas] {r.Mensagem}");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\n[Pranchas] Atualizacao automatica falhou: {ex.Message}");
            }
        }
        Recarregar(doc.Database);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
