using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;

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
    /// <summary>Ultima folha cheia, contada do fim do eixo para tras (sobrepoe a anterior).</summary>
    public bool UltimaCheia { get; set; } = true;
    /// <summary>Profile views lado a lado (true) ou empilhados para baixo (false).</summary>
    public bool PerfisHorizontal { get; set; }
    /// <summary>Layer state nomeado para as viewports de planta/perfil ("" = copiar do layout base).</summary>
    public string LayerStatePlanta { get; set; } = "";
    public string LayerStatePerfil { get; set; } = "";
    /// <summary>Page setup nomeado ("" = copiar do layout base).</summary>
    public string PageSetup { get; set; } = "";

    /// <summary>Draw e label set de cada profile do alignment nos profile views da serie (os que nao estao aqui ficam como o Civil faz).</summary>
    public List<ProfileOption> Perfis { get; } = new();
    /// <summary>Profile1 e Profile2 das bandas do band set (Null = o Civil escolhe).</summary>
    public ObjectId Profile1 { get; set; }
    public ObjectId Profile2 { get; set; }

    /// <summary>Mm de papel da borda esquerda da viewport de perfil ate a origem do profile view (inicio do grafico; os titulos das bandas ficam nesse espaco).</summary>
    public double MargemOrigem { get; set; } = LayoutSheetSeries.MargemOrigemPadrao;

    // Calibracao do profile view (medida ao criar os frames).
    public double MargemPerfil { get; set; } = double.NaN;   // largura alem das estacas (eixos, rotulos), unidades de desenho
    public double MargemDireita { get; set; } = double.NaN;  // largura alem do fim do grafico (cotas a direita), unidades de desenho
    public double SobraVertical { get; set; } = double.NaN;  // altura alem do grafico (bandas, titulos)
    public double Exagero { get; set; } = double.NaN;        // unidades de desenho por metro de cota
    /// <summary>Estilo e band set com que a calibracao foi medida (e que os profile views da serie tem).</summary>
    public ObjectId CalEstilo { get; set; }
    public ObjectId CalBandas { get; set; }

    /// <summary>Copia a calibracao (e com que estilo/band set ela foi feita).</summary>
    public void CopiarCalibracao(SheetSeries de)
    {
        MargemPerfil = de.MargemPerfil;
        MargemDireita = de.MargemDireita;
        SobraVertical = de.SobraVertical;
        Exagero = de.Exagero;
        CalEstilo = de.CalEstilo;
        CalBandas = de.CalBandas;
    }

    public void LimparCalibracao()
    {
        MargemPerfil = MargemDireita = SobraVertical = Exagero = double.NaN;
        CalEstilo = CalBandas = ObjectId.Null;
    }

    public List<ObjectId> Frames { get; } = new();
    public List<ObjectId> Cortes { get; } = new();
    /// <summary>Frames da lista que o usuario apagou (para limpar os rotulos deles).</summary>
    public List<ObjectId> FramesApagados { get; } = new();

    public bool ComPerfil => !VpPerfil.IsNull;

    public string Chave => "EIXO_" + Eixo.Handle.ToString();

    public string NomeFolha(int indice) =>
        Prefixo + (Inicio + indice).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, Digitos), '0');

    public ProfileOption? Opcao(ObjectId perfil) => Perfis.FirstOrDefault(p => p.Perfil == perfil);
}

/// <summary>Como um profile do alignment aparece nos profile views da serie (aba Profiles do Civil).</summary>
internal sealed class ProfileOption
{
    public ObjectId Perfil { get; set; }

    /// <summary>Draw: o profile e desenhado nos profile views da serie.</summary>
    public bool Draw { get; set; } = true;

    /// <summary>Label set aplicado ao profile; Null = labels como o Civil deixar (nao mexe).</summary>
    public ObjectId LabelSet { get; set; }

    /// <summary>Sem labels: apaga os label groups do profile nos profile views da serie.</summary>
    public bool SemLabels { get; set; }

    public bool MexeNasLabels => SemLabels || !LabelSet.IsNull;

    public ProfileOption Copia() => new() { Perfil = Perfil, Draw = Draw, LabelSet = LabelSet, SemLabels = SemLabels };
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
        bool temCal = false;
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
                case "UC": s.UltimaCheia = v == "1"; break;
                case "PH": s.PerfisHorizontal = v == "1"; break;
                case "LSP": s.LayerStatePlanta = v; break;
                case "LSF": s.LayerStatePerfil = v; break;
                case "PS": s.PageSetup = v; break;
                case "MP": s.MargemPerfil = Num(v); break;
                case "SV": s.SobraVertical = Num(v); break;
                case "EX": s.Exagero = Num(v); break;
                case "MD": s.MargemDireita = Num(v); break;
                case "MO": s.MargemOrigem = Num(v, MargemOrigemPadrao); break;
                case "CE": s.CalEstilo = DoHandle(db, v); temCal = true; break;
                case "CB": s.CalBandas = DoHandle(db, v); temCal = true; break;
                case "B1": s.Profile1 = SoValido(DoHandle(db, v)); break;
                case "B2": s.Profile2 = SoValido(DoHandle(db, v)); break;
                case "PO":
                {
                    // handle do profile | draw (1/0) | label set ("" = do Civil, "-" = sem labels, senao handle)
                    string[] p = v.Split('|');
                    ObjectId perfil = DoHandle(db, p[0]);
                    if (!Valido(perfil))
                        break;
                    var o = new ProfileOption { Perfil = perfil, Draw = p.Length < 2 || p[1] != "0" };
                    if (p.Length > 2 && p[2] == "-")
                        o.SemLabels = true;
                    else if (p.Length > 2)
                        o.LabelSet = SoValido(DoHandle(db, p[2]));
                    s.Perfis.Add(o);
                    break;
                }
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
        if (!temCal)
        {
            // Serie de antes da 1.7: os profile views foram criados com o estilo e o band set gravados.
            s.CalEstilo = s.EstiloPerfil;
            s.CalBandas = s.BandasPerfil;
        }
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
        Add("UC", s.UltimaCheia ? "1" : "0");
        Add("PH", s.PerfisHorizontal ? "1" : "0");
        Add("LSP", s.LayerStatePlanta);
        Add("LSF", s.LayerStatePerfil);
        Add("PS", s.PageSetup);
        Add("MP", D(s.MargemPerfil));
        Add("SV", D(s.SobraVertical));
        Add("EX", D(s.Exagero));
        Add("MD", D(s.MargemDireita));
        Add("MO", D(s.MargemOrigem));
        Add("CE", H(s.CalEstilo));
        Add("CB", H(s.CalBandas));
        Add("B1", H(s.Profile1));
        Add("B2", H(s.Profile2));
        foreach (ProfileOption o in s.Perfis.Where(o => Valido(o.Perfil)))
            Add("PO", $"{H(o.Perfil)}|{(o.Draw ? "1" : "0")}|{(o.SemLabels ? "-" : H(o.LabelSet))}");
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
        para.UltimaCheia = de.UltimaCheia;
        para.PerfisHorizontal = de.PerfisHorizontal;
        para.LayerStatePlanta = de.LayerStatePlanta;
        para.LayerStatePerfil = de.LayerStatePerfil;
        para.PageSetup = de.PageSetup;
        para.Profile1 = de.Profile1;
        para.Profile2 = de.Profile2;
        para.MargemOrigem = de.MargemOrigem;
        para.Perfis.Clear();
        para.Perfis.AddRange(de.Perfis.Select(o => o.Copia()));
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
                    sit.Add($"overlap de {-d:0.#} m com a anterior");
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
            f.PerfilNome = Ler(pv, "Name") as string ?? "perfil";
        }
        else
        {
            f.Perfil = ObjectId.Null;
        }
        return f;
    }

    /// <summary>Grava o vinculo no frame so se mudou (o frame fica intocado numa atualizacao sem mudancas).</summary>
    private static void GravarFrame(Transaction tr, ObjectId id, SheetFrameInfo f)
    {
        TypedValue[] dados = DadosFrame(f);
        var pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);
        using (ResultBuffer? atual = pl.GetXDataForApplication(App))
        {
            TypedValue[]? a = atual?.AsArray();
            if (a is not null && a.Length == dados.Length && a.Zip(dados).All(x => x.First.TypeCode == x.Second.TypeCode && Equals(x.First.Value, x.Second.Value)))
                return;
        }
        pl = (Polyline)tr.GetObject(id, OpenMode.ForWrite, false, true);
        pl.XData = new ResultBuffer(dados);
    }

    private static void GravarFrame(Polyline pl, SheetFrameInfo f) => pl.XData = new ResultBuffer(DadosFrame(f));

    private static TypedValue[] DadosFrame(SheetFrameInfo f)
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
        return dados.ToArray();
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

    public static List<(ObjectId Id, string Nome)> EstilosPerfil(Database db) => Estilos(db, civ => civ.Styles.ProfileViewStyles);

    public static List<(ObjectId Id, string Nome)> BandasPerfil(Database db) => Estilos(db, civ => civ.Styles.ProfileViewBandSetStyles);

    private static List<(ObjectId Id, string Nome)> Estilos(Database db, Func<CivilDocument, System.Collections.IEnumerable> colecao)
    {
        var lista = new List<(ObjectId, string)>();
        try
        {
            CivilDocument civ = CivilDocument.GetCivilDocument(db);
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (object o in colecao(civ))
            {
                if (o is not ObjectId id || !Valido(id))
                    continue;
                DBObject st = tr.GetObject(id, OpenMode.ForRead);
                string nome = st is CivilDb.Styles.StyleBase sb ? sb.Name : Ler(st, "Name") as string ?? id.Handle.ToString();
                lista.Add((id, nome));
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            ErroEstilos = ex.Message;
        }
        return lista.OrderBy(e => e.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Ultimo erro ao ler estilos (mostrado no painel quando a lista vem vazia).</summary>
    public static string ErroEstilos { get; private set; } = "";

    /// <summary>
    /// Estacas para as listas do painel: inicio, fim, pontos notaveis da geometria (PC, PT, TS, SC,
    /// CS, ST, PCC) e estacas inteiras (20 m; 100 m em eixos muito longos).
    /// </summary>
    public static List<(double Estaca, string Rotulo)> EstacasDoEixo(Database db, ObjectId eixo)
    {
        var pontos = new List<(double, string)>();
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(eixo, OpenMode.ForRead) is not CivilDb.Alignment al)
                return pontos;
            double ini = al.StartingStation, fim = al.EndingStation;
            pontos.Add((ini, "inicio"));
            pontos.Add((fim, "fim"));
            pontos.AddRange(PontosNotaveis(al));
            double passo = (fim - ini) / 20.0 > 2000 ? 100 : 20;
            for (double e = Math.Ceiling(ini / passo) * passo; e < fim - 1e-6; e += passo)
                pontos.Add((e, ""));
            tr.Commit();
        }
        catch (System.Exception)
        {
        }
        // Mesma estaca: fica a com rotulo.
        return pontos.OrderBy(p => p.Item1).ThenBy(p => p.Item2.Length == 0 ? 1 : 0)
            .Aggregate(new List<(double Estaca, string Rotulo)>(), (l, p) =>
            {
                if (l.Count > 0 && Math.Abs(l[^1].Estaca - p.Item1) < 1e-3)
                {
                    if (l[^1].Rotulo.Length == 0 || (p.Item2.Length > 0 && !l[^1].Rotulo.Contains(p.Item2)))
                        l[^1] = (l[^1].Estaca, string.Join("/", new[] { l[^1].Rotulo, p.Item2 }.Where(x => x.Length > 0)));
                    return l;
                }
                l.Add(p);
                return l;
            });
    }

    /// <summary>Pontos de mudanca de elemento (reta, curva, espiral) pelas subentidades do eixo.</summary>
    private static IEnumerable<(double, string)> PontosNotaveis(CivilDb.Alignment al)
    {
        var subs = new List<(string Tipo, double Ini, double Fim)>();
        try
        {
            object? ents = Ler(al, "Entities");
            if (ents is null)
                return Array.Empty<(double, string)>();
            int n = Convert.ToInt32(Ler(ents, "Count") ?? 0, Inv);
            MethodInfo? porOrdem = ents.GetType().GetMethod("GetEntityByOrder", new[] { typeof(int) });
            for (int i = 0; i < n && porOrdem is not null; i++)
            {
                object? ent = porOrdem.Invoke(ents, new object[] { i });
                if (ent is null)
                    continue;
                int ns = Convert.ToInt32(Ler(ent, "SubEntityCount") ?? 0, Inv);
                MethodInfo? item = ent.GetType().GetMethod("get_Item", new[] { typeof(int) });
                for (int k = 0; k < ns && item is not null; k++)
                {
                    object? sub = item.Invoke(ent, new object[] { k });
                    if (sub is null)
                        continue;
                    string tipo = Ler(sub, "SubEntityType")?.ToString() ?? "";
                    double a = Convert.ToDouble(Ler(sub, "StartStation") ?? double.NaN, Inv);
                    double b = Convert.ToDouble(Ler(sub, "EndStation") ?? double.NaN, Inv);
                    if (!double.IsNaN(a) && !double.IsNaN(b))
                        subs.Add((tipo, a, b));
                }
            }
        }
        catch (System.Exception)
        {
            return Array.Empty<(double, string)>();
        }

        static char Tipo(string t) => t.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? 'C'
            : t.Contains("Spiral", StringComparison.OrdinalIgnoreCase) ? 'E' : 'R';
        var lista = new List<(double, string)>();
        subs = subs.OrderBy(x => x.Ini).ToList();
        for (int i = 0; i + 1 < subs.Count; i++)
        {
            char a = Tipo(subs[i].Tipo), b = Tipo(subs[i + 1].Tipo);
            string nome = (a, b) switch
            {
                ('R', 'C') => "PC",
                ('C', 'R') => "PT",
                ('R', 'E') => "TS",
                ('E', 'C') => "SC",
                ('C', 'E') => "CS",
                ('E', 'R') => "ST",
                ('C', 'C') => "PCC",
                ('E', 'E') => "SS",
                _ => ""
            };
            if (nome.Length > 0)
                lista.Add((subs[i].Fim, nome));
        }
        return lista;
    }

    /// <summary>Layouts (de papel) de um DWG/DWT, na ordem das abas.</summary>
    public static List<string> LayoutsDoArquivo(string arquivo)
    {
        var lista = new List<(int, string)>();
        using var origem = new Database(false, true);
        origem.ReadDwgFile(arquivo, System.IO.FileShare.Read, true, "");
        origem.CloseInput(true);
        using Transaction tr = origem.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(origem.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (!lay.ModelType)
                lista.Add((lay.TabOrder, lay.LayoutName));
        }
        tr.Commit();
        return lista.OrderBy(x => x.Item1).Select(x => x.Item2).ToList();
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
            return new SheetCapacity(planta, perfil, !double.IsNaN(s.MargemDireita) || !double.IsNaN(s.MargemPerfil), comp, n);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Metros de alignment por sheet no profile view: da origem (a MargemOrigem mm da borda
    /// esquerda) ate 10 mm da borda direita, menos o que o profile view desenha depois do fim do
    /// grafico (cotas do lado direito, medido na calibracao).
    /// </summary>
    private static double CapacidadePerfil(SheetSeries s, SheetTemplate m)
    {
        double w = Math.Max(0, m.WPerfil - Math.Max(0, s.MargemOrigem) - MargemPapel) / m.EscalaPerfil;
        double direita = !double.IsNaN(s.MargemDireita) ? s.MargemDireita
            : !double.IsNaN(s.MargemPerfil) ? s.MargemPerfil / 2
            : m.LarguraPerfil * 0.06;
        return w - direita;
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
        using IDisposable ___ = PluginLog.Medir(Mod, $"Pranchas: {(criarPranchas ? "gerar sheets" : "gerar frames")} de {s.EixoNome} (total)");
        PluginLog.Info(Mod, $"Pranchas: gerar {(criarPranchas ? "sheets" : "frames")} de {s.EixoNome}; layout base {s.Modelo}, estilo {NomeDe(db, s.EstiloPerfil)}, band set {NomeDe(db, s.BandasPerfil)}, " +
                            $"overlap {s.Folga:0.##} m, match lines {s.Arredondar:0.##} m, ultima {(s.UltimaCheia ? "cheia" : "curta")}, {s.Perfis.Count} opcao(oes) de profile.");
        Progresso.Mostrar("Lendo o layout base e o alignment...", 0);

        SheetTemplate m = LerModelo(db, s);
        PluginLog.Diag(Mod, $"Pranchas: layout base {m.Nome}: planta {m.WPlanta:0.#} x {m.HPlanta:0.#} mm a {m.EscalaPlanta:0.####} (frame {m.LarguraFrame:0.#} x {m.AlturaFrame:0.#}); " +
                            (m.TemPerfil ? $"perfil {m.WPerfil:0.#} x {m.HPerfil:0.#} mm a {m.EscalaPerfil:0.####} ({m.LarguraPerfil:0.#} x {m.AlturaPerfil:0.#})" : "sem perfil"));
        var avisos = new List<string>();
        SheetSeries? gravada = LerSerie(db, s.Eixo);
        if (gravada is not null)
        {
            s.Frames.Clear();
            s.Frames.AddRange(gravada.Frames);
            s.Cortes.Clear();
            s.Cortes.AddRange(gravada.Cortes);
            if (double.IsNaN(s.MargemPerfil) && double.IsNaN(s.Exagero))
                s.CopiarCalibracao(gravada);
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
            {
                using (Progresso.Faixa(0, 0.05))
                using (PluginLog.Medir(Mod, "Pranchas: calibracao do profile view"))
                {
                    Progresso.Mostrar("Medindo o profile view (estilo e band set)...", 0);
                    Calibrar(db, s, m, ini, fim, origemPerfis, avisos);
                }
            }
            if (Calibracao.Length > 0)
            {
                PluginLog.Info(Mod, "Pranchas: " + Calibracao);
                Escrever(doc, Calibracao);
            }
            double maxPerfil = s.ComPerfil && m.TemPerfil ? CapacidadePerfil(s, m) : double.PositiveInfinity;
            if (maxPerfil < 5)
                throw new InvalidOperationException("A viewport de perfil comporta menos de 5 m de eixo: aumente a viewport ou mude a escala dela.");

            using IDisposable faixaFrames = Progresso.Faixa(0.05, 0.1);
            using IDisposable tempoFrames = PluginLog.Medir(Mod, "Pranchas: frames (encaixe ao longo do alignment e criacao)");
            Progresso.Mostrar("Encaixando os frames ao longo do alignment...", 0);
            using Transaction tr = db.TransactionManager.StartTransaction();
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            List<FrameGeo> geos = Encaixar(ax, ini, fim, m.LarguraFrame, m.AlturaFrame, s.Folga, s.Arredondar, maxPerfil, s.Girar, s.UltimaCheia);
            PluginLog.Diag(Mod, $"Pranchas: {geos.Count} frame(s) de {Estaca(ini)} a {Estaca(fim)} (planta ate {m.LarguraFrame - 2 * s.Folga:0.#} m, perfil ate {maxPerfil:0.#} m por sheet): " +
                                string.Join(", ", geos.Select(g => $"{Estaca(g.E0)}-{Estaca(g.E1)}")));

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
                f.Rotulo = Rotulo(tr, ms.ObjectId, f, v, m.EscalaPlanta, ObjectId.Null);
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

        int novas = 0, perfis = 0, pendentes = 0;
        bool cancelado = false;
        List<string> nomesNovos = new();
        HashSet<ObjectId>? tocados = null;
        if (criarPranchas)
        {
            using (Progresso.Faixa(0.1, 0.62))
            {
                PranchasCriadas c = CriarPranchas(doc, s, m, frames, origemPerfis, avisos);
                (novas, perfis, nomesNovos, cancelado, pendentes) = (c.Pranchas, c.Perfis, c.Nomes, c.Cancelado, c.Pendentes);
                tocados = c.Tocados;
            }
            if (nomesNovos.Count > 0)
            {
                using (Progresso.Faixa(0.62, 0.67))
                using (PluginLog.Medir(Mod, $"Pranchas: layout base em {nomesNovos.Count} sheet(s) (page setup e camadas)"))
                {
                    Progresso.Mostrar("Page setup e camadas do layout base nas sheets novas...", 0);
                    AplicarLayoutBase(db, s, m, LerFrames(db, s).Where(f => nomesNovos.Contains(f.LayoutNome)).ToList(), avisos);
                }
            }
        }
        else if (novosFrames == 0)
            return new OpResult(0, 0, "A serie ja tem frames. Use Atualizar para realinhar, ou Remover para gerar de novo.");

        SheetSeriesSync.Recarregar(db);
        // Serie nova: alinha tudo. Completar: so as sheets que ganharam layout ou profile view agora.
        OpResult r;
        using (Progresso.Faixa(criarPranchas ? 0.67 : 0.1, 0.97))
            r = AtualizarInterno(doc, s.Eixo, novosFrames > 0 ? null : tocados ?? new HashSet<ObjectId>(), avisos, cancelavel: false);

        if (novas > 0)
        {
            using (Progresso.Faixa(0.97, 1))
            {
                if (s.Numerar)
                {
                    Progresso.Mostrar("Numeracao das sheets...", 0);
                    using (PluginLog.Medir(Mod, "Pranchas: numeracao"))
                        NumerarSerie(doc, s);
                }
                try
                {
                    if (LayoutKeyMap.Existe(db) && !LayoutKeyMap.Ler(db).Automatico)
                    {
                        Progresso.Mostrar("Mapa-chave...", 0.5);
                        using (PluginLog.Medir(Mod, "Pranchas: mapa-chave"))
                            LayoutKeyMap.Gerar(doc, false);
                    }
                }
                catch (System.Exception ex)
                {
                    avisos.Add("mapa-chave: " + ex.Message);
                }
            }
        }

        var msg = new List<string>();
        if (novosFrames > 0)
            msg.Add($"{novosFrames} frame(s) ao longo de {s.EixoNome}");
        if (criarPranchas)
            msg.Add(novas > 0 ? $"{novas} sheet(s) ({nomesNovos.First()} a {nomesNovos.Last()})" : "nenhuma sheet nova");
        if (perfis > 0)
            msg.Add($"{perfis} profile view(s)");
        string texto = string.Join(", ", msg) + ".";
        if (cancelado)
            texto = $"Cancelado: {texto} Faltam {pendentes} sheet(s): Completar cria as que faltam.";
        if (!criarPranchas)
            texto += " Ajuste os frames no Model se quiser e clique em Gerar sheets.";
        if (avisos.Count > 0)
        {
            texto += " Avisos: " + string.Join("; ", avisos.Distinct().Take(3)) + (avisos.Count > 3 ? " (todos no log: Ctrl+Shift+L)" : "");
            Relatar(doc, avisos);
        }
        return new OpResult(novosFrames + novas, r.Falhas, texto);
    }

    /// <summary>Resultado de CriarPranchas.</summary>
    private sealed record PranchasCriadas(int Pranchas, int Perfis, List<string> Nomes, HashSet<ObjectId> Tocados, bool Cancelado, int Pendentes);

    private static PranchasCriadas CriarPranchas(Document doc, SheetSeries s, SheetTemplate m,
        List<SheetFrameInfo> frames, Point3d origemPerfis, List<string> avisos)
    {
        Database db = doc.Database;
        LayoutManager lm = LayoutManager.Current;
        List<string> ordem = LayoutOps.OrdemAtual(db);
        var existentes = new HashSet<string>(ordem, StringComparer.OrdinalIgnoreCase);
        int pranchas = 0, perfis = 0;
        var nomes = new List<string>();
        var tocados = new HashSet<ObjectId>();
        bool cancelado = false;
        double passo = m.TemPerfil ? m.AlturaPerfil * 1.6 : 0;
        double passoX = m.TemPerfil ? m.LarguraPerfil * 1.15 : 0;
        List<SheetFrameInfo> lista = frames.OrderBy(f => f.E0).ToList();
        bool Pendente(SheetFrameInfo f) => !Valido(f.Layout) || (s.ComPerfil && m.TemPerfil && !Valido(f.Perfil));
        int total = lista.Count(Pendente), feitos = 0;
        var tCopia = new System.Diagnostics.Stopwatch();
        var tPerfil = new System.Diagnostics.Stopwatch();
        var tLabels = new System.Diagnostics.Stopwatch();

        for (int i = 0; i < lista.Count; i++)
        {
            SheetFrameInfo f = lista[i];
            bool precisaLayout = !Valido(f.Layout);
            bool precisaPerfil = s.ComPerfil && m.TemPerfil && !Valido(f.Perfil);
            if (!precisaLayout && !precisaPerfil)
                continue;
            if (Progresso.Cancelado)
            {
                cancelado = true;
                PluginLog.Aviso(Mod, $"Pranchas: cancelado pelo usuario depois de {feitos} de {total} sheet(s).");
                break;
            }

            string nome = f.LayoutNome;
            long copia0 = tCopia.ElapsedMilliseconds, perfil0 = tPerfil.ElapsedMilliseconds;
            if (precisaLayout)
            {
                nome = s.NomeFolha(f.Indice);
                for (int k = 2; existentes.Contains(nome); k++)
                    nome = $"{s.NomeFolha(f.Indice)} ({k})";
                Progresso.Mostrar($"Sheet {nome} ({feitos + 1} de {total}): copiando o layout base", (double)feitos / Math.Max(1, total));
                try
                {
                    tCopia.Start();
                    lm.CopyLayout(s.Modelo, nome);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    avisos.Add($"copiar o layout base para {nome}: {ex.Message}");
                    continue;
                }
                finally
                {
                    tCopia.Stop();
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
                    avisos.Add($"{nome}: viewport de planta nao encontrada na sheet");
                GravarFrame(tr, f.Id, f);
                tr.Commit();
                tocados.Add(f.Id);
            }
            catch (System.Exception ex)
            {
                avisos.Add($"{nome}: vinculo da sheet nao gravado ({ex.Message})");
                continue;
            }

            if (precisaPerfil && Valido(f.VpPerfil))
            {
                Progresso.Mostrar($"Sheet {nome} ({feitos + 1} de {total}): profile view", (feitos + 0.4) / Math.Max(1, total));
                try
                {
                    Point3d origem = origemPerfis + (s.PerfisHorizontal
                        ? new Vector3d(passoX * f.Indice, 0, 0)
                        : new Vector3d(0, -passo * f.Indice, 0));
                    tPerfil.Start();
                    try
                    {
                        // Cria ja com estacas, Draw dos profiles, bandas e cotas: o profile view so
                        // regenera uma vez, no trecho da sheet (criado sozinho ele nasce com o
                        // alignment inteiro). A configuracao vai numa transacao aninhada: se falhar,
                        // o profile view criado fica, ligado ao frame, e e configurado de novo
                        // depois de gravado (como era antes).
                        bool configurado = false, opcoesAplicadas = false;
                        string? aviso = null;
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            f.Perfil = CriarProfileView(tr, db, s.Eixo, origem, nome, s.EstiloPerfil, s.BandasPerfil);
                            try
                            {
                                using Transaction cfg = db.TransactionManager.StartTransaction();
                                opcoesAplicadas = AplicarDrawEBandas(cfg, f.Perfil, s, avisos, nome);
                                aviso = ConfigurarPerfil(cfg, f.Perfil, s, m, f.E0, f.E1);
                                cfg.Commit();
                                configurado = true;
                            }
                            catch (System.Exception ex)
                            {
                                PluginLog.Diag(Mod, $"Pranchas: {nome}: configuracao junto com a criacao do profile view falhou ({Msg(ex)}); refazendo depois de gravar.");
                            }
                            GravarFrame(tr, f.Id, f);
                            tr.Commit();
                        }
                        if (!configurado || !opcoesAplicadas)
                        {
                            // Falhou junto com a criacao, ou o profile view recem-criado ainda nao
                            // listava os profiles: de novo, com ele ja gravado.
                            try
                            {
                                using Transaction tr = db.TransactionManager.StartTransaction();
                                AplicarDrawEBandas(tr, f.Perfil, s, avisos, nome);
                                aviso = ConfigurarPerfil(tr, f.Perfil, s, m, f.E0, f.E1);
                                tr.Commit();
                            }
                            catch (System.Exception ex)
                            {
                                aviso = $"profile view criado, mas sem estacas/opcoes ({Msg(ex)})";
                                PluginLog.Erro(Mod, $"Pranchas: {nome}: configuracao do profile view", ex);
                            }
                        }
                        if (aviso is not null)
                            avisos.Add($"{nome}: {aviso}");
                        ConferirEstacas(db, f.Perfil, f.E0, f.E1, nome);
                    }
                    finally
                    {
                        tPerfil.Stop();
                    }
                    if (s.Perfis.Any(o => o.MexeNasLabels))
                    {
                        tLabels.Start();
                        try
                        {
                            AplicarLabels(db, f.Perfil, s, avisos, nome);
                        }
                        finally
                        {
                            tLabels.Stop();
                        }
                    }
                    perfis++;
                    tocados.Add(f.Id);
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"{nome}: profile view nao criado ({ex.Message})");
                    PluginLog.Erro(Mod, $"Pranchas: {nome}: profile view nao criado", ex);
                }
            }
            feitos++;
            PluginLog.Diag(Mod, $"Pranchas: {nome}: copia do layout {(tCopia.ElapsedMilliseconds - copia0) / 1000.0:0.00} s, profile view {(tPerfil.ElapsedMilliseconds - perfil0) / 1000.0:0.00} s.");
        }

        if (pranchas > 0)
        {
            using (PluginLog.Medir(Mod, "Pranchas: ordem das abas"))
                LayoutOps.GravarOrdem(db, ordem);
        }
        if (pranchas + perfis > 0)
            PluginLog.Tempo(Mod, $"Pranchas: {pranchas} sheet(s): copia dos layouts {tCopia.Elapsed.TotalSeconds:0.00} s; {perfis} profile view(s): {tPerfil.Elapsed.TotalSeconds:0.00} s" +
                                 (tLabels.ElapsedMilliseconds > 0 ? $"; labels {tLabels.Elapsed.TotalSeconds:0.00} s" : "") + ".");
        int pendentes = LerFrames(db, s).Count(Pendente);
        return new PranchasCriadas(pranchas, perfis, nomes, tocados, cancelado, pendentes);
    }

    /// <summary>
    /// Leva o layout base para as folhas: page setup (o nomeado escolhido, ou o do proprio layout base)
    /// e camadas das viewports de planta e perfil (o layer state escolhido, ou os overrides e o VP
    /// freeze da viewport correspondente do layout base, por um layer state temporario).
    /// </summary>
    private static void AplicarLayoutBase(Database db, SheetSeries s, SheetTemplate m, List<SheetFrameInfo> frames, List<string> avisos)
    {
        if (frames.Count == 0)
            return;

        // Page setup.
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            PlotSettings? fonte = null;
            bool temporaria = false;
            var psDic = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            if (s.PageSetup.Length > 0 && psDic.Contains(s.PageSetup))
            {
                fonte = (PlotSettings)tr.GetObject(psDic.GetAt(s.PageSetup), OpenMode.ForRead);
            }
            else
            {
                if (s.PageSetup.Length > 0)
                    avisos.Add($"page setup \"{s.PageSetup}\" nao existe; usado o do layout base");
                var baseLay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(m.Layout, OpenMode.ForRead);
                fonte = new PlotSettings(false);
                fonte.CopyFrom(baseLay);
                temporaria = true;
            }
            foreach (SheetFrameInfo f in frames.Where(f => Valido(f.Layout)))
            {
                try
                {
                    var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(f.Layout, OpenMode.ForWrite, false, true);
                    lay.CopyFrom(fonte);
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"{f.LayoutNome}: page setup nao aplicado ({ex.Message})");
                }
            }
            if (temporaria)
                fonte.Dispose();
            tr.Commit();
        }

        // Camadas das viewports.
        Camadas(db, s.LayerStatePlanta, m.VpPlanta, frames.Select(f => f.VpPlanta), "planta", avisos);
        if (m.TemPerfil)
            Camadas(db, s.LayerStatePerfil, m.VpPerfil, frames.Select(f => f.VpPerfil), "perfil", avisos);
    }

    internal static void Camadas(Database db, string nomeado, ObjectId vpBase, IEnumerable<ObjectId> destinos, string qual, List<string> avisos, string? registro = null)
    {
        var aplicadas = new List<ObjectId>();
        List<ObjectId> alvo = destinos.Where(Valido).ToList();
        if (alvo.Count == 0)
            return;
        LayerStateManager lsm = db.LayerStateManager;
        string estado = nomeado;
        bool temporario = false;
        LayerStateMasks mascara;
        try
        {
            if (estado.Length > 0 && lsm.HasLayerState(estado))
            {
                mascara = lsm.GetLayerStateMask(estado);
            }
            else
            {
                if (estado.Length > 0)
                    avisos.Add($"layer state \"{estado}\" nao existe; copiadas as camadas da viewport de {qual} do layout base");
                // Overrides e VP freeze da viewport do layout base, guardados num layer state temporario.
                estado = "C3D_TMP_" + vpBase.Handle;
                temporario = true;
                mascara = LayerStateMasks.CurrentViewport | LayerStateMasks.Color | LayerStateMasks.LineType |
                          LayerStateMasks.LineWeight | LayerStateMasks.PlotStyle | LayerStateMasks.Transparency;
                if (lsm.HasLayerState(estado))
                    lsm.DeleteLayerState(estado);
                lsm.SaveLayerState(estado, mascara, vpBase);
            }

            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (ObjectId vp in alvo)
            {
                try
                {
                    lsm.RestoreLayerState(estado, vp, 4, mascara);
                    aplicadas.Add(vp);
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"camadas da viewport de {qual}: {ex.Message}");
                }
            }
            tr.Commit();
            LayoutOps.RegistrarLayerState(db, aplicadas, registro ?? (temporario ? $"camadas da viewport de {qual} do layout base" : estado));
        }
        catch (System.Exception ex)
        {
            avisos.Add($"camadas da viewport de {qual} nao copiadas ({ex.Message})");
        }
        finally
        {
            if (temporario)
            {
                try
                {
                    if (lsm.HasLayerState(estado))
                        lsm.DeleteLayerState(estado);
                }
                catch (System.Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Colar layer state: leva o VP freeze e os overrides (cor, linetype, lineweight, plot style,
    /// transparencia) da viewport de origem para as viewports de destino.
    /// </summary>
    public static OpResult ColarCamadas(Document doc, ObjectId vpOrigem, IReadOnlyList<ObjectId> destinos, string nomeOrigem)
    {
        if (!Valido(vpOrigem))
            throw new InvalidOperationException("A viewport copiada nao existe mais; copie de novo.");
        List<ObjectId> alvo = destinos.Where(d => d != vpOrigem).ToList();
        var avisos = new List<string>();
        using (doc.LockDocument())
            Camadas(doc.Database, "", vpOrigem, alvo, "origem", avisos, "copia de " + nomeOrigem);
        foreach (string a in avisos)
            PluginLog.Aviso(Mod, "Colar layer state: " + a);
        return new OpResult(alvo.Count - avisos.Count, avisos.Count,
            $"Layer state colado em {alvo.Count} viewport(s)" + (avisos.Count > 0 ? $"; {avisos.Count} aviso(s) no log (Ctrl+Shift+L)." : "."));
    }

    /// <summary>Reaplica o layout base (page setup e camadas das viewports) em todas as folhas da serie.</summary>
    public static OpResult ReaplicarLayoutBase(Document doc, ObjectId eixo)
    {
        Database db = doc.Database;
        SheetSeries s = LerSerie(db, eixo) ?? throw new InvalidOperationException("Serie nao encontrada.");
        SheetTemplate m = LerModelo(db, s);
        List<SheetFrameInfo> frames = LerFrames(db, s).Where(f => Valido(f.Layout)).ToList();
        var avisos = new List<string>();
        using (SheetSeriesSync.Suspender())
        using (doc.LockDocument())
        using (PluginLog.Medir(Mod, $"Pranchas: reaplicar layout base em {frames.Count} sheet(s)"))
        {
            Progresso.Mostrar($"Page setup e camadas em {frames.Count} sheet(s)...", 0.1);
            AplicarLayoutBase(db, s, m, frames, avisos);
        }
        Relatar(doc, avisos);
        return new OpResult(frames.Count, avisos.Count,
            $"Layout base reaplicado em {frames.Count} sheet(s) (page setup e camadas das viewports)" + (avisos.Count > 0 ? $"; {avisos.Count} aviso(s) no log (Ctrl+Shift+L)." : "."));
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
            // Mesmo formato da ultima numeracao do painel (ex.: "PRANCHA: {000}/{T000}"); sem ela, os digitos da serie.
            NumberingRule regra = LayoutNumbering.FormatoGravado(doc.Database) ?? NumberingRule.Padrao(s.Digitos);
            regra.Inicio = s.Inicio;
            regra.Total = nomes.Count;
            regra.SoLayoutsMarcados = true;
            regra.Marcados = nomes;
            LayoutNumbering.Numerar(doc, regra);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "Pranchas: numeracao das sheets", ex);
            Escrever(doc, "Numeracao: " + ex.Message);
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
        OpResult r;
        using (PluginLog.Medir(Mod, $"Pranchas: atualizar {(soFrames is null ? "todas as sheets" : $"{soFrames.Count} sheet(s)")}"))
            r = AtualizarInterno(doc, eixo, soFrames, avisos, cancelavel: true);
        Relatar(doc, avisos);
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
        for (int i = 0; i < series.Count; i++)
        {
            if (Progresso.Cancelado)
                break;
            SheetSeries s = series[i];
            OpResult r;
            using (Progresso.Faixa((double)i / series.Count, (double)(i + 1) / series.Count))
                r = Atualizar(doc, s.Eixo, null);
            ok += r.Ok;
            falhas += r.Falhas;
            msgs.Add($"{s.EixoNome}: {r.Mensagem}");
        }
        return new OpResult(ok, falhas, string.Join(" ", msgs));
    }

    private static OpResult AtualizarInterno(Document doc, ObjectId eixo, ICollection<ObjectId>? soFrames, List<string> avisos, bool cancelavel)
    {
        Database db = doc.Database;
        SheetSeries? s = LerSerie(db, eixo);
        if (s is null)
            return new OpResult(0, 0, "Serie de pranchas nao encontrada.") { Mudou = false };
        // Conta o que a atualizacao mexeu de fato no desenho (a gravacao da serie nao conta).
        int mexidos = 0;
        void Mexeu(object? _, ObjectEventArgs e)
        {
            if (e.DBObject is not Xrecord and not DBDictionary)
                mexidos++;
        }
        void Apagou(object? _, ObjectErasedEventArgs e) => mexidos++;
        db.ObjectModified += Mexeu;
        db.ObjectAppended += Mexeu;
        db.ObjectErased += Apagou;
        try
        {
            OpResult r = AtualizarSerie(doc, db, s, soFrames, avisos, cancelavel);
            return mexidos == 0 && r.Falhas == 0
                ? new OpResult(r.Ok, 0, $"{s.EixoNome}: {r.Ok} sheet(s) conferida(s), nada mudou.") { Mudou = false }
                : r;
        }
        finally
        {
            db.ObjectModified -= Mexeu;
            db.ObjectAppended -= Mexeu;
            db.ObjectErased -= Apagou;
        }
    }

    private static OpResult AtualizarSerie(Document doc, Database db, SheetSeries s, ICollection<ObjectId>? soFrames, List<string> avisos, bool cancelavel)
    {
        SheetTemplate? m = null;
        try
        {
            m = LerModelo(db, s);
        }
        catch (System.Exception ex)
        {
            // Sem modelo (apagado/renomeado): atualiza mesmo assim, sem ajuste de cotas.
            PluginLog.Diag(Mod, $"Pranchas: {s.EixoNome}: layout base ilegivel ({ex.Message}); cotas sem ajuste.");
        }

        List<SheetFrameInfo> frames = LerFrames(db, s);
        bool Alvo(SheetFrameInfo f) => soFrames is null || soFrames.Contains(f.Id);
        int ok = 0, falhas = 0;
        bool parou = false;
        double escalaPlanta = m?.EscalaPlanta ?? 0;
        foreach (SheetFrameInfo f in frames.Where(f => f.LayoutNome.Length == 0))
            f.LayoutNome = s.NomeFolha(f.Indice);
        List<SheetFrameInfo> alvos = frames.Where(Alvo).ToList();
        // Avisos de antes: voltam nas sheets que a atualizacao nao chegar a conferir (cancelar).
        Dictionary<ObjectId, string> avisosAntes = alvos.ToDictionary(f => f.Id, f => f.Aviso);

        // 1. Estacas e planta.
        using (Progresso.Faixa(0, 0.1))
        using (PluginLog.Medir(Mod, $"Pranchas: {s.EixoNome}: estacas e viewports de planta ({alvos.Count})"))
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Progresso.Mostrar($"Estacas e viewports de planta ({alvos.Count} sheet(s))...", 0);
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            ObjectId ms = SymbolUtilityServices.GetBlockModelSpaceId(db);
            foreach (SheetFrameInfo f in alvos)
            {
                double e0Antes = f.E0, e1Antes = f.E1;
                ObjectId rotuloAntes = f.Rotulo;
                try
                {
                    // Uma transacao por sheet: se uma falhar no meio, nada dela fica pela metade.
                    using Transaction tf = db.TransactionManager.StartTransaction();
                    var pl = (Polyline)tf.GetObject(f.Id, OpenMode.ForRead);
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
                    if (Valido(f.VpPlanta))
                    {
                        escala = AlinharPlanta(tf, f.VpPlanta, v);
                        if (escalaPlanta <= 0)
                            escalaPlanta = escala;
                    }
                    f.Rotulo = Rotulo(tf, ms, f, v, escala, f.Rotulo);
                    GravarFrame(tf, f.Id, f);
                    tf.Commit();
                    ok++;
                }
                catch (System.Exception ex)
                {
                    (f.E0, f.E1, f.Rotulo, f.Aviso) = (e0Antes, e1Antes, rotuloAntes, avisosAntes[f.Id]);
                    avisos.Add($"{f.LayoutNome}: planta nao atualizada ({ex.Message})");
                    falhas++;
                }
            }
            foreach (ObjectId apagado in s.FramesApagados)
                Apagar(tr, RotuloDeApagado(tr, db, apagado));
            tr.Commit();
        }

        // 2. Profile views: estacas e cotas. So escreve o que mudou (cada escrita regenera o profile view).
        List<SheetFrameInfo> comPerfil = alvos.Where(f => Valido(f.Perfil)).ToList();
        using (Progresso.Faixa(0.1, 0.92))
        using (PluginLog.Medir(Mod, $"Pranchas: {s.EixoNome}: profile views ({comPerfil.Count})"))
        {
            for (int i = 0; i < comPerfil.Count; i++)
            {
                SheetFrameInfo f = comPerfil[i];
                if (cancelavel && Progresso.Cancelado)
                {
                    parou = true;
                    avisos.Add($"atualizacao cancelada: {comPerfil.Count - i} profile view(s) ficaram sem atualizar");
                    PluginLog.Aviso(Mod, $"Pranchas: atualizacao cancelada pelo usuario em {f.LayoutNome}.");
                    // As que nao foram conferidas mantem os avisos de antes.
                    foreach (SheetFrameInfo resto in comPerfil.Skip(i))
                        if (resto.Aviso.Length == 0)
                            resto.Aviso = avisosAntes.GetValueOrDefault(resto.Id, "");
                    break;
                }
                Progresso.Passo($"Profile view {f.LayoutNome} ({i + 1} de {comPerfil.Count})", i, comPerfil.Count);
                try
                {
                    string? aviso;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        aviso = ConfigurarPerfil(tr, f.Perfil, s, m, f.E0, f.E1);
                        tr.Commit();
                    }
                    if (Valido(f.VpPerfil))
                        aviso = Juntar(aviso, AlinharPerfil(db, f.VpPerfil, f.Perfil, s.MargemOrigem));
                    if (aviso is not null)
                    {
                        f.Aviso = aviso;
                        avisos.Add($"{f.LayoutNome}: {aviso}");
                    }
                }
                catch (System.Exception ex)
                {
                    f.Aviso = "profile view: " + ex.Message;
                    avisos.Add($"{f.LayoutNome}: profile view nao atualizado ({ex.Message})");
                    PluginLog.Erro(Mod, $"Pranchas: {f.LayoutNome}: profile view nao atualizado", ex);
                    falhas++;
                }
            }
        }

        // 3. Linhas de corte, avisos nos frames e serie (frames apagados saem da lista).
        using (Progresso.Faixa(0.92, 1))
        using (PluginLog.Medir(Mod, $"Pranchas: {s.EixoNome}: match lines"))
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Progresso.Mostrar("Match lines...", 0);
            var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
            var ax = new Eixo(al);
            foreach (SheetFrameInfo f in alvos)
            {
                try
                {
                    GravarFrame(tr, f.Id, f);
                }
                catch (System.Exception)
                {
                    // Aviso fica so no log.
                }
            }
            if (escalaPlanta <= 0)
                escalaPlanta = EscalaDeQualquerPlanta(tr, frames);
            RefazerCortes(tr, db, s, frames, ax, escalaPlanta);
            try
            {
                // Seta do norte das sheets (copiada do layout base) acompanha o giro da viewport de planta.
                var espacos = alvos.Where(f => Valido(f.Layout))
                    .Select(f => ((Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(f.Layout, OpenMode.ForRead)).BlockTableRecordId).ToList();
                (int girados, _) = LayoutNorte.Girar(tr, espacos);
                if (girados > 0)
                    PluginLog.Diag(Mod, $"Pranchas: {s.EixoNome}: {girados} seta(s) do norte girada(s) pela viewport.");
            }
            catch (System.Exception ex)
            {
                PluginLog.Erro(Mod, "Pranchas: seta do norte", ex);
            }
            s.Frames.Clear();
            s.Frames.AddRange(frames.OrderBy(f => f.E0).Select(f => f.Id));
            Gravar(tr, db, s);
            tr.Commit();
        }

        string fim = parou ? " (cancelado antes do fim)" : "";
        return new OpResult(ok, falhas, $"{ok} sheet(s) de {s.EixoNome} alinhada(s){fim}" + (falhas > 0 ? $"; {falhas} falha(s) (detalhes no log: Ctrl+Shift+L)." : "."));
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
        using IDisposable tempo = PluginLog.Medir(Mod, $"Pranchas: remover serie de {s.EixoNome} ({frames.Count} frame(s){(tudo ? ", com sheets e profile views" : "")})");
        Progresso.Mostrar($"Removendo frames e match lines de {s.EixoNome}...", 0.1);
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
        {
            Progresso.Mostrar($"Excluindo {layouts.Count} sheet(s)...", 0.5);
            extra = " " + LayoutOps.Excluir(doc, layouts).Mensagem;
        }
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

    /// <summary>Folga (mm de papel) entre o profile view e as bordas da viewport de perfil (em cima, embaixo e a direita).</summary>
    public const double MargemPapel = 10;

    /// <summary>Padrao da distancia da borda esquerda ate a origem do profile view (mm de papel).</summary>
    public const double MargemOrigemPadrao = 20;

    /// <summary>
    /// Centro do frame no centro da viewport; aresta 0-1 do frame na horizontal do papel. So escreve
    /// se mudou (a viewport mexida regenera e dispara o mapa-chave). Retorna a escala da viewport.
    /// </summary>
    private static double AlinharPlanta(Transaction tr, ObjectId vpId, Point3d[] v)
    {
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        Vector3d u = v[1] - v[0];
        double twist = -Math.Atan2(u.Y, u.X);
        if (twist < 0)
            twist += 2 * Math.PI;
        var centro = new Point3d((v[0].X + v[2].X) / 2, (v[0].Y + v[2].Y) / 2, 0);
        Point3d c = centro.TransformBy(Dcs2Wcs(vp, twist).Inverse());
        var alvo = new Point2d(c.X, c.Y);
        if (Math.Abs(Math.IEEERemainder(vp.TwistAngle - twist, 2 * Math.PI)) > 1e-9 || vp.ViewCenter.GetDistanceTo(alvo) > 1e-6)
        {
            vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite, false, true);
            bool travada = vp.Locked;
            vp.Locked = false;
            try
            {
                vp.TwistAngle = twist;
                vp.ViewCenter = alvo;
            }
            finally
            {
                vp.Locked = travada;
            }
        }
        return vp.CustomScale;
    }

    /// <summary>
    /// Viewport de perfil: origem do profile view (inicio do grafico) a margemOrigem mm (papel) da
    /// borda esquerda, deixando os titulos das bandas e as cotas da esquerda nesse espaco (as
    /// extensoes do profile view nao incluem os titulos). Na altura, centrado se cabe com 10 mm de
    /// folga em cima e embaixo, senao encostado embaixo (bandas visiveis) com o topo cortado.
    /// Retorna o aviso quando nao cabe.
    /// </summary>
    private static string? AlinharPerfil(Database db, ObjectId vpId, ObjectId pvId, double margemOrigem)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var pv = (Entity)tr.GetObject(pvId, OpenMode.ForRead);
        Extents3d ext = pv.GeometricExtents;
        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
        if (vp.CustomScale <= 0)
        {
            tr.Commit();
            return null;
        }
        double esc = vp.CustomScale;
        double w = vp.Width / esc, h = vp.Height / esc, mg = MargemPapel / esc;
        double mgOrigem = Math.Max(0, double.IsNaN(margemOrigem) ? MargemOrigemPadrao : margemOrigem) / esc;
        double altura = ext.MaxPoint.Y - ext.MinPoint.Y;
        double x0;
        if (Ler(pv, "Location") is Point3d origem)
        {
            x0 = origem.X;
        }
        else
        {
            // Sem a origem pela API: o comeco das extensoes faz as vezes dela.
            x0 = ext.MinPoint.X;
            DiagUmaVez("pv-location", "ProfileView.Location ilegivel; a viewport de perfil usa o inicio das extensoes do profile view.");
        }
        double esquerda = x0 - mgOrigem;
        string? aviso = null;
        double y;
        if (altura <= h - 2 * mg + 1e-6)
        {
            y = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2;
        }
        else
        {
            y = ext.MinPoint.Y - mg + h / 2;
            aviso = $"profile view mais alto que a viewport ({altura * esc:0} mm, cabem {vp.Height - 2 * MargemPapel:0} mm): bandas visiveis e topo cortado; " +
                    "reduza o exagero vertical (estilo) ou aumente a viewport";
        }
        if (ext.MinPoint.X < esquerda - 1e-6)
            aviso = Juntar(aviso, $"profile view passa da viewport a esquerda ({(esquerda - ext.MinPoint.X) * esc:0} mm); aumente o inicio do grafico (passo 3)");
        double direita = ext.MaxPoint.X - esquerda; // da borda esquerda ate o fim do profile view
        if (direita > w - mg + 1e-6)
            aviso = Juntar(aviso, $"profile view passa da viewport a direita ({(direita - w + mg) * esc:0} mm alem da folga de {MargemPapel:0} mm); " +
                                  "diminua o inicio do grafico (passo 3) ou o trecho da sheet");

        var centro = new Point3d(esquerda + w / 2, y, 0);
        Point3d c = centro.TransformBy(Dcs2Wcs(vp, 0).Inverse());
        var alvo = new Point2d(c.X, c.Y);
        if (Math.Abs(Math.IEEERemainder(vp.TwistAngle, 2 * Math.PI)) > 1e-9 || vp.ViewCenter.GetDistanceTo(alvo) > 1e-6)
        {
            vp = (Viewport)tr.GetObject(vpId, OpenMode.ForWrite, false, true);
            bool travada = vp.Locked;
            vp.Locked = false;
            try
            {
                vp.TwistAngle = 0;
                vp.ViewCenter = alvo;
            }
            finally
            {
                vp.Locked = travada;
            }
        }
        tr.Commit();
        return aviso;
    }

    private static Point3d CentroDaVista(Viewport vp) =>
        new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0).TransformBy(Dcs2Wcs(vp, vp.TwistAngle));

    /// <summary>DCS da viewport para WCS, com o giro dado (o atual ou o que vai ser aplicado).</summary>
    private static Matrix3d Dcs2Wcs(Viewport vp, double twist) =>
        Matrix3d.Rotation(-twist, vp.ViewDirection, vp.ViewTarget) *
        Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin) *
        Matrix3d.PlaneToWorld(vp.ViewDirection);

    // ------------------------------------------------------------------
    // Profile view (por reflexao: o modulo nao depende da forma exata da API)
    // ------------------------------------------------------------------

    private static ObjectId CriarProfileView(Transaction tr, Database db, ObjectId eixo, Point3d origem, string nome, ObjectId estilo, ObjectId bandas)
    {
        Type tipo = TipoCivil("ProfileView") ?? throw new InvalidOperationException("tipo ProfileView nao encontrado na API do Civil");
        if (bandas.IsNull)
            bandas = BandasPerfil(db).Select(b => b.Id).FirstOrDefault();
        if (estilo.IsNull)
            estilo = EstilosPerfil(db).Select(b => b.Id).FirstOrDefault();

        // Mais nova primeiro: Create(alignmentId, insertPosition, name, bandSetId, styleId).
        var metodos = tipo.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(mt => mt.Name == "Create")
            .OrderBy(mt => mt.GetParameters().FirstOrDefault()?.ParameterType == typeof(ObjectId) ? 0 : 1).ToList();
        string ultimoErro = "nenhuma assinatura de ProfileView.Create reconhecida";
        var tentativas = new List<string>();
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
                            Propriedade(pv.GetType(), "StyleId")?.SetValue(pv, estilo);
                        }
                        if (tentativas.Count > 0 || !_logouCreate)
                        {
                            _logouCreate = true;
                            PluginLog.Diag(Mod, $"Pranchas: ProfileView.Create({Assinatura(mt)}) criou \"{candidato}\"" +
                                                (tentativas.Count > 0 ? $" depois de: {string.Join("; ", tentativas)}" : "."));
                        }
                        return id;
                    }
                }
                catch (TargetInvocationException ex)
                {
                    ultimoErro = ex.InnerException?.Message ?? ex.Message;
                    tentativas.Add($"{Assinatura(mt)} \"{candidato}\": {ultimoErro}");
                }
            }
        }
        PluginLog.Aviso(Mod, "Pranchas: ProfileView.Create falhou: " + string.Join("; ", tentativas.DefaultIfEmpty(ultimoErro)));
        throw new InvalidOperationException("ProfileView.Create: " + ultimoErro);
    }

    private static bool _logouCreate;

    private static string Assinatura(MethodInfo mt) => string.Join(", ", mt.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));

    /// <summary>Tipo da API do Civil pelo nome curto (Autodesk.Civil.DatabaseServices.*).</summary>
    private static Type? TipoCivil(string nome) =>
        typeof(CivilDocument).Assembly.GetType("Autodesk.Civil.DatabaseServices." + nome);

    /// <summary>Estacas do profile view (modo User e intervalo); so escreve se mudaram.</summary>
    private static void DefinirEstacas(Transaction tr, ObjectId pvId, double e0, double e1)
    {
        DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
        PropertyInfo modo = Prop(pv, "StationRangeMode") ?? throw Faltando(pv, "StationRangeMode", "Station");
        PropertyInfo ini = Prop(pv, "StationStart", "StartStation") ?? throw Faltando(pv, "StationStart", "Station");
        PropertyInfo fim = Prop(pv, "StationEnd", "EndStation") ?? throw Faltando(pv, "StationEnd", "Station");
        if (EnumE(pv, modo, "User") && Igual(ini.GetValue(pv), e0) && Igual(fim.GetValue(pv), e1))
            return;
        DBObject w = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
        Chamar(() => DefinirEnum(w, modo, "User"), "modo de estacas");
        Chamar(() => DefinirIntervalo(w, ini, fim, e0, e1), "intervalo de estacas");
    }

    /// <summary>O profile view esta com o trecho de estacas pedido (o Civil respeitou o intervalo).</summary>
    private static bool EstacasConferem(Database db, ObjectId pvId, double e0, double e1)
    {
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
            PropertyInfo? ini = Prop(pv, "StationStart", "StartStation"), fim = Prop(pv, "StationEnd", "EndStation");
            bool ok = ini is not null && fim is not null && Math.Abs(Convert.ToDouble(ini.GetValue(pv), Inv) - e0) < 1e-3 && Math.Abs(Convert.ToDouble(fim.GetValue(pv), Inv) - e1) < 1e-3;
            tr.Commit();
            return ok;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>Registra no log quando o profile view nao ficou com o trecho pedido (o Atualizar corrige).</summary>
    private static void ConferirEstacas(Database db, ObjectId pvId, double e0, double e1, string nome)
    {
        if (!EstacasConferem(db, pvId, e0, e1))
            DiagUmaVez("estacas:" + nome, $"{nome}: o profile view nao ficou com as estacas {Estaca(e0)} a {Estaca(e1)} na criacao; a atualizacao seguinte corrige.");
    }

    /// <summary>Mensagem da excecao (a de dentro, quando veio por reflexao).</summary>
    private static string Msg(System.Exception ex) => (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;

    /// <summary>
    /// Estacas e cotas do profile view na mesma transacao (uma regeneracao so). As cotas saem dos
    /// profiles desenhados no trecho, sem passar pelo automatico do Civil. Retorna um aviso.
    /// </summary>
    private static string? ConfigurarPerfil(Transaction tr, ObjectId pvId, SheetSeries s, SheetTemplate? m, double e0, double e1)
    {
        DefinirEstacas(tr, pvId, e0, e1);
        return AjustarCotas(tr, pvId, s, m, e0, e1);
    }

    /// <summary>
    /// Cotas: faixa dos profiles desenhados no trecho, aberta ate encher a altura util da viewport
    /// de perfil (altura menos 10 mm em cima e embaixo). Se nao couber, mostra os dados inteiros
    /// (AlinharPerfil deixa as bandas visiveis e avisa). Com a opcao "Automaticas (Civil)" nao mexe.
    /// </summary>
    private static string? AjustarCotas(Transaction tr, ObjectId pvId, SheetSeries s, SheetTemplate? m, double e0, double e1)
    {
        if (!s.AjustarCotas || m is null || !m.TemPerfil)
            return null;
        DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
        PropertyInfo? modo = Prop(pv, "ElevationRangeMode");
        PropertyInfo? pMin = Prop(pv, "ElevationMin", "MinimumElevation");
        PropertyInfo? pMax = Prop(pv, "ElevationMax", "MaximumElevation");
        if (modo is null || pMin is null || pMax is null)
            return "cotas automaticas (API sem ElevationRangeMode)";

        void Automatico()
        {
            if (EnumE(pv, modo, "Auto"))
                return;
            DBObject w = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
            Chamar(() => DefinirEnum(w, modo, "Auto"), "modo de cotas");
        }

        double a = s.SobraVertical, b = s.Exagero;
        if (double.IsNaN(a) || double.IsNaN(b) || b <= 0)
        {
            Automatico();
            return "cotas automaticas (profile view nao calibrado)";
        }
        (double emin, double emax) = FaixaDosPerfis(tr, PerfisDesenhados(tr, pv, s), e0, e1);
        if (double.IsNaN(emin))
        {
            // Nenhum profile com dados no trecho: o Civil decide.
            Automatico();
            return null;
        }

        double disponivel = (m.HPerfil - 2 * MargemPapel) / m.EscalaPerfil;
        double faixa = (disponivel - a) / b;
        double dados = emax - emin;
        double lo, hi;
        if (faixa < dados - 1e-6)
        {
            lo = Math.Floor(emin);
            hi = Math.Ceiling(emax);
            PluginLog.Diag(Mod, $"Pranchas: cotas {emin:0.##} a {emax:0.##} precisam de {(a + b * dados) * m.EscalaPerfil:0} mm; a viewport de perfil tem {m.HPerfil:0} mm.");
        }
        else
        {
            double meio = (emin + emax) / 2;
            lo = Math.Floor(meio - faixa / 2);
            hi = lo + Math.Floor(faixa);
            if (lo > emin)
                lo = Math.Floor(emin);
            if (hi < emax)
                hi = Math.Ceiling(emax);
            if (hi - lo > faixa + 1e-6)
            {
                lo = emin;
                hi = emin + faixa;
            }
        }
        if (EnumE(pv, modo, "User") && Igual(pMin.GetValue(pv), lo) && Igual(pMax.GetValue(pv), hi))
            return null;
        DBObject pvW = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
        Chamar(() => DefinirEnum(pvW, modo, "User"), "modo de cotas");
        Chamar(() => DefinirIntervalo(pvW, pMin, pMax, lo, hi), "intervalo de cotas");
        return null;
    }

    /// <summary>Profiles desenhados no profile view (Draw da aba Profiles); sem acesso, os do alignment menos os escondidos.</summary>
    private static List<ObjectId> PerfisDesenhados(Transaction tr, DBObject pv, SheetSeries s)
    {
        var lista = new List<ObjectId>();
        bool lido = false;
        try
        {
            if (Ler(pv, "GraphOverrides") is System.Collections.IEnumerable itens)
            {
                foreach (object po in itens)
                {
                    lido = true;
                    if (Ler(po, "ProfileId") is ObjectId id && Valido(id) && Ler(po, "Draw") is not false)
                        lista.Add(id);
                }
            }
        }
        catch (System.Exception ex)
        {
            DiagUmaVez("GraphOverrides", "GraphOverrides ilegivel: " + ex.Message);
        }
        if (lido)
            return lista;
        if (tr.GetObject(s.Eixo, OpenMode.ForRead) is CivilDb.Alignment al)
            foreach (ObjectId id in al.GetProfileIds())
                if (Valido(id) && s.Opcao(id) is not { Draw: false })
                    lista.Add(id);
        return lista;
    }

    /// <summary>Menor e maior cota dos profiles no trecho (amostras a cada 0,25 a 2 m); NaN sem dados.</summary>
    private static (double Min, double Max) FaixaDosPerfis(Transaction tr, IEnumerable<ObjectId> perfis, double e0, double e1)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        double passo = Math.Clamp((e1 - e0) / 400, 0.25, 2.0);
        foreach (ObjectId id in perfis)
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Profile p)
                    continue;
                double a = Math.Max(e0, p.StartingStation), b = Math.Min(e1, p.EndingStation);
                if (double.IsNaN(a) || double.IsNaN(b) || b < a)
                    continue;
                int seguidas = 0, falhas = 0;
                for (double st = a; ; st += seguidas > 0 ? passo * 8 : passo)
                {
                    double x = Math.Min(st, b);
                    try
                    {
                        double z = p.ElevationAt(x);
                        seguidas = 0;
                        if (!double.IsNaN(z) && Math.Abs(z) < 1e7)
                        {
                            min = Math.Min(min, z);
                            max = Math.Max(max, z);
                        }
                    }
                    catch (System.Exception)
                    {
                        // Trecho sem dados (superficie com buraco): anda mais rapido ate voltar a ter cota;
                        // muitas falhas, desiste deste profile (excecao em laco custa caro).
                        seguidas++;
                        if (++falhas > 60)
                            break;
                    }
                    if (x >= b)
                        break;
                }
            }
            catch (System.Exception ex)
            {
                DiagUmaVez("FaixaDosPerfis", "cotas de um profile ilegiveis: " + ex.Message);
            }
        }
        return min <= max ? (min, max) : (double.NaN, double.NaN);
    }

    // ------------------------------------------------------------------
    // Profiles nos profile views: Draw, label sets e Profile1/Profile2 das bandas
    // ------------------------------------------------------------------

    /// <summary>Profiles do alignment (nome e tipo) para o passo 3 do painel.</summary>
    public static List<(ObjectId Id, string Nome, string Tipo)> PerfisDoEixo(Database db, ObjectId eixo)
    {
        var lista = new List<(ObjectId, string, string)>();
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            if (Valido(eixo) && tr.GetObject(eixo, OpenMode.ForRead) is CivilDb.Alignment al)
            {
                foreach (ObjectId id in al.GetProfileIds())
                {
                    if (!Valido(id) || tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Profile p)
                        continue;
                    lista.Add((id, p.Name, TipoPerfil(Ler(p, "ProfileType")?.ToString() ?? "")));
                }
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag(Mod, "Pranchas: profiles do alignment ilegiveis: " + ex.Message);
        }
        return lista.OrderBy(p => p.Item3 == "surface" ? 0 : p.Item3 == "layout" ? 1 : 2).ThenBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string TipoPerfil(string t) => t switch
    {
        "EG" => "surface",
        "FG" => "layout",
        "OffsetProfile" => "offset",
        "CurbReturn" => "curb return",
        "File" => "file",
        "Quick" => "quick",
        "" => "?",
        _ => t
    };

    /// <summary>Label sets de profile do desenho.</summary>
    public static List<(ObjectId Id, string Nome)> LabelSetsPerfil(Database db) => Estilos(db, civ =>
        Ler(civ.Styles, "LabelSetStyles") is object ls && Ler(ls, "ProfileLabelSetStyles") is System.Collections.IEnumerable e
            ? e
            : Array.Empty<object>());

    /// <summary>
    /// Draw de cada profile (GraphOverrides do profile view) e Profile1/Profile2 das bandas, conforme
    /// as opcoes da serie. So escreve o que muda. Retorna false se o profile view ainda nao lista os
    /// profiles (recem-criado), para tentar de novo depois de gravado.
    /// </summary>
    private static bool AplicarDrawEBandas(Transaction tr, ObjectId pvId, SheetSeries s, List<string> avisos, string nome)
    {
        bool listou = true;
        List<ProfileOption> ops = s.Perfis.Where(o => Valido(o.Perfil)).ToList();
        if (ops.Count > 0)
        {
            DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
            if (Ler(pv, "GraphOverrides") is not System.Collections.IEnumerable itens)
            {
                AvisoUmaVez(avisos, nome, "Draw dos profiles nao aplicado: a API nao tem ProfileView.GraphOverrides");
            }
            else
            {
                bool algum = false, mudar = false;
                foreach (object po in itens)
                {
                    algum = true;
                    if (Ler(po, "ProfileId") is ObjectId id && ops.FirstOrDefault(o => o.Perfil == id) is ProfileOption op && Ler(po, "Draw") is bool d && d != op.Draw)
                        mudar = true;
                }
                listou = algum;
                if (mudar)
                {
                    DBObject pvW = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
                    if (Ler(pvW, "GraphOverrides") is System.Collections.IEnumerable itensW)
                    {
                        foreach (object po in itensW)
                        {
                            if (Ler(po, "ProfileId") is not ObjectId id || ops.FirstOrDefault(o => o.Perfil == id) is not ProfileOption op)
                                continue;
                            PropertyInfo? draw = Propriedade(po.GetType(), "Draw");
                            if (draw is null || !draw.CanWrite)
                            {
                                AvisoUmaVez(avisos, nome, "Draw dos profiles nao aplicado: ProfileOverride sem Draw gravavel");
                                break;
                            }
                            try
                            {
                                if (!Equals(draw.GetValue(po), op.Draw))
                                    draw.SetValue(po, op.Draw);
                            }
                            catch (System.Exception ex)
                            {
                                avisos.Add($"{nome}: Draw de {Ler(po, "ProfileName")} recusado ({Msg(ex)})");
                            }
                        }
                    }
                }
            }
        }
        AplicarBandas(tr, pvId, s, avisos, nome);
        return listou;
    }

    /// <summary>Profile1 e Profile2 de todas as bandas (embaixo e em cima) do profile view.</summary>
    private static void AplicarBandas(Transaction tr, ObjectId pvId, SheetSeries s, List<string> avisos, string nome)
    {
        if (!Valido(s.Profile1) && !Valido(s.Profile2))
            return;
        foreach (bool escrever in new[] { false, true })
        {
            DBObject pv = tr.GetObject(pvId, escrever ? OpenMode.ForWrite : OpenMode.ForRead, false, true);
            object? bandas = Ler(pv, "Bands");
            if (bandas is null)
            {
                AvisoUmaVez(avisos, nome, "Profile1/Profile2 nao aplicados: a API nao tem ProfileView.Bands");
                return;
            }
            bool mudar = false;
            foreach (string lado in new[] { "Bottom", "Top" })
            {
                MethodInfo? ler = bandas.GetType().GetMethod($"Get{lado}BandItems", Type.EmptyTypes);
                MethodInfo? gravar = ler is null ? null : bandas.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == $"Set{lado}BandItems" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(ler.ReturnType));
                if (ler is null || gravar is null)
                {
                    DiagUmaVez("Bands" + lado, $"ProfileViewBandSet sem Get/Set{lado}BandItems: " +
                                               string.Join(", ", bandas.GetType().GetMethods().Select(m => m.Name).Where(n => n.Contains("Band")).Distinct()));
                    continue;
                }
                object? lista;
                try
                {
                    lista = ler.Invoke(bandas, null);
                }
                catch (System.Exception ex)
                {
                    DiagUmaVez("Bands" + lado + "Ler", $"Get{lado}BandItems: {Msg(ex)}");
                    continue;
                }
                if (lista is not System.Collections.IEnumerable itens)
                    continue;
                bool mudou = false;
                foreach (object it in itens)
                {
                    if (Valido(s.Profile1))
                        mudou |= escrever ? DefinirId(it, "Profile1Id", s.Profile1) : !(Ler(it, "Profile1Id") is ObjectId a1 && a1 == s.Profile1) && Propriedade(it.GetType(), "Profile1Id") is not null;
                    if (Valido(s.Profile2))
                        mudou |= escrever ? DefinirId(it, "Profile2Id", s.Profile2) : !(Ler(it, "Profile2Id") is ObjectId a2 && a2 == s.Profile2) && Propriedade(it.GetType(), "Profile2Id") is not null;
                }
                if (!escrever)
                {
                    mudar |= mudou;
                    continue;
                }
                if (!mudou)
                    continue;
                try
                {
                    gravar.Invoke(bandas, new[] { lista });
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"{nome}: Profile1/Profile2 das bandas recusados ({Msg(ex)})");
                }
            }
            if (!escrever && !mudar)
                return;
        }
    }

    /// <summary>Muda uma propriedade ObjectId se existir e for diferente. Retorna se mudou.</summary>
    private static bool DefinirId(object o, string nome, ObjectId valor)
    {
        PropertyInfo? p = Propriedade(o.GetType(), nome);
        if (p is null || !p.CanWrite || p.PropertyType != typeof(ObjectId))
            return false;
        try
        {
            if (p.GetValue(o) is ObjectId atual && atual == valor)
                return false;
            p.SetValue(o, valor);
            return true;
        }
        catch (System.Exception ex)
        {
            DiagUmaVez("DefinirId" + nome, $"{o.GetType().Name}.{nome} recusado: {Msg(ex)}");
            return false;
        }
    }

    /// <summary>
    /// Label sets dos profiles num profile view, como "Import label set" do Civil: apaga os label
    /// groups do profile naquele profile view e cria os do label set. Sem labels: so apaga.
    /// </summary>
    private static void AplicarLabels(Database db, ObjectId pvId, SheetSeries s, List<string> avisos, string nome)
    {
        foreach (ProfileOption op in s.Perfis.Where(o => o.MexeNasLabels && Valido(o.Perfil)))
        {
            try
            {
                using Transaction tr = db.TransactionManager.StartTransaction();
                List<ObjectId>? existentes = LabelGroups(tr, pvId, op.Perfil);
                if (existentes is null)
                {
                    // Sem como listar os labels atuais: criar duplicaria a cada aplicacao.
                    AvisoUmaVez(avisos, nome, "labels dos profiles nao aplicados: a API nao lista os label groups do profile view");
                    continue;
                }
                bool criar = !op.SemLabels && op.Draw && Valido(op.LabelSet);
                int apagados = existentes.Count(g => Apagar(tr, g));
                int criados = criar ? CriarLabels(tr, pvId, op.Perfil, op.LabelSet, avisos, nome) : 0;
                if (criar && criados == 0)
                {
                    // Nada criado: desfaz (os labels que estavam ficam).
                    avisos.Add($"{nome}: o label set de {NomeDe(db, op.Perfil)} nao criou label group; os labels anteriores ficaram");
                    continue;
                }
                tr.Commit();
                PluginLog.Diag(Mod, $"Pranchas: {nome}: labels de {NomeDe(db, op.Perfil)}: {apagados} label group(s) apagado(s), {criados} criado(s).");
            }
            catch (System.Exception ex)
            {
                avisos.Add($"{nome}: labels de {NomeDe(db, op.Perfil)} nao aplicados ({ex.Message})");
                PluginLog.Erro(Mod, $"Pranchas: {nome}: labels de {NomeDe(db, op.Perfil)}", ex);
            }
        }
    }

    /// <summary>Label groups de um profile num profile view; null se a API nao tem como listar.</summary>
    private static List<ObjectId>? LabelGroups(Transaction tr, ObjectId pvId, ObjectId perfil)
    {
        var ids = new List<ObjectId>();
        bool listou = false;
        // 1. ProfileOverride.GetProfileLabelGroupIds (o profile naquele profile view).
        try
        {
            DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
            if (Ler(pv, "GraphOverrides") is System.Collections.IEnumerable itens)
            {
                foreach (object po in itens)
                {
                    if (Ler(po, "ProfileId") is not ObjectId id || id != perfil)
                        continue;
                    MethodInfo? mt = po.GetType().GetMethod("GetProfileLabelGroupIds", Type.EmptyTypes) ?? po.GetType().GetMethod("GetLabelGroupIds", Type.EmptyTypes);
                    if (mt?.Invoke(po, null) is ObjectIdCollection col)
                    {
                        foreach (ObjectId g in col)
                            if (Valido(g))
                                ids.Add(g);
                        return ids;
                    }
                    if (mt is not null)
                        return ids; // metodo existe e nao devolveu nada: sem labels
                }
            }
        }
        catch (System.Exception ex)
        {
            DiagUmaVez("LabelGroups1", "GetProfileLabelGroupIds: " + ex.Message);
        }
        // 2. ProfileLabelGroup.GetAvailableLabelGroupIds(RXClass, profileView, profile, bool).
        try
        {
            Type? t = TipoCivil("ProfileLabelGroup");
            MethodInfo? mt = t?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetAvailableLabelGroupIds" && m.GetParameters().Length == 4);
            if (t is not null && mt is not null)
            {
                object? r = mt.Invoke(null, new object[] { Autodesk.AutoCAD.Runtime.RXObject.GetClass(t), pvId, perfil, true });
                listou = true;
                if (r is ObjectIdCollection col)
                    foreach (ObjectId g in col)
                        if (Valido(g))
                            ids.Add(g);
            }
        }
        catch (System.Exception ex)
        {
            listou = false;
            DiagUmaVez("LabelGroups2", "GetAvailableLabelGroupIds: " + Msg(ex));
        }
        return listou ? ids : null;
    }

    /// <summary>Cria os label groups dos itens do label set para o profile no profile view.</summary>
    private static int CriarLabels(Transaction tr, ObjectId pvId, ObjectId perfil, ObjectId labelSet, List<string> avisos, string nome)
    {
        DBObject set = tr.GetObject(labelSet, OpenMode.ForRead);
        string nomeSet = Ler(set, "Name") as string ?? "label set";
        int n = Convert.ToInt32(Ler(set, "Count") ?? 0, Inv);
        MethodInfo? item = set.GetType().GetMethod("get_Item", new[] { typeof(int) });
        if (item is null)
        {
            AvisoUmaVez(avisos, nome, $"label set {nomeSet} ilegivel (sem indexador)");
            return 0;
        }
        var itens = new List<object>();
        for (int i = 0; i < n; i++)
            if (item.Invoke(set, new object[] { i }) is object it)
                itens.Add(it);

        (double major, double minor) = IntervalosDoEstilo(tr, pvId);
        ObjectId grupoMajor = ObjectId.Null;
        int criados = 0;
        // Major antes de minor (o minor pode pedir o major); o resto na ordem do label set.
        foreach (object it in itens.OrderBy(x => (Ler(x, "LabelStyleType")?.ToString() ?? "").Contains("Minor", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            string tipo = Ler(it, "LabelStyleType")?.ToString() ?? "";
            ObjectId estilo = Ler(it, "LabelStyleId") is ObjectId e ? e : ObjectId.Null;
            double incremento = Ler(it, "Increment") is object inc && Convert.ToDouble(inc, Inv) > 0 ? Convert.ToDouble(inc, Inv)
                              : tipo.Contains("Minor", StringComparison.OrdinalIgnoreCase) ? minor : major;
            (string Classe, string Metodo)? alvo = GrupoDoTipo(tipo);
            if (alvo is null || !Valido(estilo))
            {
                DiagUmaVez("LabelTipo" + tipo, $"item de label set nao suportado: {tipo}");
                continue;
            }
            ObjectId grupo = CriarGrupo(alvo.Value.Classe, alvo.Value.Metodo, pvId, perfil, estilo, incremento, grupoMajor, out string erro);
            if (grupo.IsNull)
            {
                avisos.Add($"{nome}: label {tipo} de {nomeSet} nao criado ({erro})");
                continue;
            }
            criados++;
            if (tipo.Contains("Major", StringComparison.OrdinalIgnoreCase))
                grupoMajor = grupo;
            CopiarAncora(tr, grupo, it);
            if (tipo.Contains("HorizontalGeometry", StringComparison.OrdinalIgnoreCase))
                CopiarPontosGeometria(tr, grupo, it);
        }
        return criados;
    }

    private static (string Classe, string Metodo)? GrupoDoTipo(string tipo)
    {
        string t = tipo.ToLowerInvariant();
        if (t.Contains("minorstation")) return ("ProfileMinorStationLabelGroup", "Create");
        if (t.Contains("majorstation")) return ("ProfileStationLabelGroup", "CreateMajor");
        if (t.Contains("gradebreak")) return ("ProfilePVILabelGroup", "Create");
        if (t.Contains("crest")) return ("ProfileCrestCurveLabelGroup", "Create");
        if (t.Contains("sag")) return ("ProfileSagCurveLabelGroup", "Create");
        if (t.Contains("horizontalgeometry")) return ("ProfileHorizontalGeometryPointLabelGroup", "Create");
        if (t.Contains("depth")) return ("ProfileDepthLabelGroup", "Create");
        if (t.Contains("line")) return ("ProfileLineLabelGroup", "Create");
        return null;
    }

    /// <summary>
    /// Chama o Create do label group pelo nome dos parametros (profileViewId, profileId, styleId,
    /// major...; double = incremento), para nao depender da ordem exata de cada versao da API.
    /// </summary>
    private static ObjectId CriarGrupo(string classe, string metodo, ObjectId pv, ObjectId perfil, ObjectId estilo, double incremento, ObjectId major, out string erro)
    {
        erro = $"{classe}.{metodo} nao encontrado";
        Type? t = TipoCivil(classe);
        if (t is null)
            return ObjectId.Null;
        foreach (MethodInfo mt in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == metodo).OrderByDescending(m => m.GetParameters().Length))
        {
            ParameterInfo[] ps = mt.GetParameters();
            var args = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                string nm = (ps[i].Name ?? "").ToLowerInvariant();
                Type pt = ps[i].ParameterType;
                if (pt == typeof(ObjectId))
                {
                    if (nm.Contains("major"))
                        args[i] = major;
                    else if (nm.Contains("view"))
                        args[i] = pv;
                    else if (nm.Contains("style"))
                        args[i] = estilo;
                    else if (nm.Contains("profile"))
                        args[i] = perfil;
                    else
                        ok = false;
                }
                else if (pt == typeof(double))
                {
                    args[i] = incremento > 0 ? incremento : 20.0;
                }
                else
                {
                    ok = false;
                }
            }
            if (!ok)
                continue;
            if (ps.Any(p => (p.Name ?? "").Contains("major", StringComparison.OrdinalIgnoreCase)) && major.IsNull)
            {
                erro = "minor sem major station no label set";
                continue;
            }
            try
            {
                if (mt.Invoke(null, args) is ObjectId id && !id.IsNull)
                    return id;
                erro = $"{classe}.{metodo}({Assinatura(mt)}) nao criou";
            }
            catch (TargetInvocationException ex)
            {
                erro = $"{classe}.{metodo}({Assinatura(mt)}): {ex.InnerException?.Message ?? ex.Message}";
            }
        }
        PluginLog.Diag(Mod, "Pranchas: " + erro);
        return ObjectId.Null;
    }

    /// <summary>Major/minor do eixo inferior do estilo do profile view (padrao 100/20 m).</summary>
    private static (double Major, double Minor) IntervalosDoEstilo(Transaction tr, ObjectId pvId)
    {
        double major = 100, minor = 20;
        try
        {
            DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
            if (Ler(pv, "StyleId") is ObjectId sid && Valido(sid))
            {
                object? eixo = Ler(tr.GetObject(sid, OpenMode.ForRead), "BottomAxis");
                object? ma = eixo is null ? null : Ler(eixo, "MajorTickStyle");
                object? mi = eixo is null ? null : Ler(eixo, "MinorTickStyle");
                if (ma is not null && Ler(ma, "Interval") is object a && Convert.ToDouble(a, Inv) > 0)
                    major = Convert.ToDouble(a, Inv);
                if (mi is not null && Ler(mi, "Interval") is object b && Convert.ToDouble(b, Inv) > 0)
                    minor = Convert.ToDouble(b, Inv);
            }
        }
        catch (System.Exception ex)
        {
            DiagUmaVez("Intervalos", "intervalos do eixo do estilo ilegiveis: " + ex.Message);
        }
        return (major, minor);
    }

    /// <summary>Ancora da cota (DimensionAnchorOption/Value) do item do label set no label group.</summary>
    private static void CopiarAncora(Transaction tr, ObjectId grupo, object item)
    {
        try
        {
            object? opcao = Ler(item, "DimensionAnchorOption");
            object? valor = Ler(item, "DimensionAnchorValue");
            if (opcao is null && valor is null)
                return;
            DBObject g = tr.GetObject(grupo, OpenMode.ForWrite, false, true);
            PropertyInfo? pOp = Propriedade(g.GetType(), "DefaultDimensionAnchorOption");
            PropertyInfo? pVal = Propriedade(g.GetType(), "DefaultDimensionAnchorValue");
            if (opcao is not null && pOp is { CanWrite: true })
            {
                object? v = pOp.PropertyType.IsInstanceOfType(opcao) ? opcao
                    : pOp.PropertyType.IsEnum && Enum.GetNames(pOp.PropertyType).Contains(opcao.ToString()) ? Enum.Parse(pOp.PropertyType, opcao.ToString()!)
                    : pOp.PropertyType.IsEnum ? Enum.ToObject(pOp.PropertyType, Convert.ToInt32(opcao, Inv)) : null;
                if (v is not null)
                    pOp.SetValue(g, v);
            }
            if (valor is not null && pVal is { CanWrite: true })
                pVal.SetValue(g, Convert.ToDouble(valor, Inv));
        }
        catch (System.Exception ex)
        {
            DiagUmaVez("Ancora", "ancora do label nao copiada: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>Pontos de geometria marcados no item do label set (labels de geometria horizontal).</summary>
    private static void CopiarPontosGeometria(Transaction tr, ObjectId grupo, object item)
    {
        try
        {
            DBObject g = tr.GetObject(grupo, OpenMode.ForWrite, false, true);
            MethodInfo? lerOpcoes = g.GetType().GetMethod("GetGeometryPointsOptions", Type.EmptyTypes);
            MethodInfo? gravarOpcoes = g.GetType().GetMethods().FirstOrDefault(m => m.Name == "SetGeometryPointsOptions" && m.GetParameters().Length == 1);
            MethodInfo? doItem = item.GetType().GetMethod("GetLabeledAlignmentGeometryPoints", Type.EmptyTypes);
            if (lerOpcoes is null || gravarOpcoes is null || doItem is null)
                return;
            object? opcoes = lerOpcoes.Invoke(g, null);
            if (opcoes is null || doItem.Invoke(item, null) is not System.Collections.IEnumerable marcados)
                return;
            opcoes.GetType().GetMethod("UnSelectAll", Type.EmptyTypes)?.Invoke(opcoes, null);
            PropertyInfo? indice = opcoes.GetType().GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1);
            foreach (object par in marcados)
            {
                object? chave = Ler(par, "Key");
                if (chave is null || indice is null)
                    continue;
                object? op = indice.GetValue(opcoes, new[] { chave });
                if (op is not null)
                    Propriedade(op.GetType(), "Selected")?.SetValue(op, true);
            }
            gravarOpcoes.Invoke(g, new[] { opcoes });
        }
        catch (System.Exception ex)
        {
            DiagUmaVez("PontosGeometria", "pontos de geometria do label nao copiados: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>
    /// Reaplica o passo 3 em todos os profile views da serie (so nos criados pelo plugin): estilo,
    /// band set, Draw, label sets e Profile1/Profile2; refaz as cotas e a viewport de perfil. Se o
    /// estilo ou o band set mudou, mede o profile view de novo antes (exagero e altura das bandas).
    /// </summary>
    public static OpResult AplicarOpcoesPerfis(Document doc, ObjectId eixo)
    {
        Database db = doc.Database;
        SheetSeries s = LerSerie(db, eixo) ?? throw new InvalidOperationException("Serie nao encontrada: gere as sheets primeiro.");
        SheetTemplate? m = null;
        try
        {
            m = LerModelo(db, s);
        }
        catch (System.Exception)
        {
            // Sem layout base: aplica sem ajuste de cotas.
        }
        List<SheetFrameInfo> frames = LerFrames(db, s).Where(f => Valido(f.Perfil)).ToList();
        var avisos = new List<string>();
        int ok = 0;
        bool parou = false;
        int incompletos = 0;
        bool trocarEstilo = Valido(s.EstiloPerfil) && s.EstiloPerfil != s.CalEstilo;
        bool trocarBandas = Valido(s.BandasPerfil) && s.BandasPerfil != s.CalBandas;
        PluginLog.Info(Mod, $"Pranchas: passo 3 em {frames.Count} profile view(s) de {s.EixoNome}: " +
                            $"estilo {NomeDe(db, s.EstiloPerfil)}{(trocarEstilo ? " (novo)" : "")}, band set {NomeDe(db, s.BandasPerfil)}{(trocarBandas ? " (novo)" : "")}; " +
                            string.Join("; ", s.Perfis.Select(o => $"{NomeDe(db, o.Perfil)} draw={(o.Draw ? 1 : 0)} labels={(o.SemLabels ? "nenhum" : Valido(o.LabelSet) ? NomeDe(db, o.LabelSet) : "do Civil")}")) +
                            $"; Profile1={(Valido(s.Profile1) ? NomeDe(db, s.Profile1) : "do band set")}, Profile2={(Valido(s.Profile2) ? NomeDe(db, s.Profile2) : "do band set")}; " +
                            $"inicio do grafico a {s.MargemOrigem:0.#} mm.");
        using (SheetSeriesSync.Suspender())
        using (doc.LockDocument())
        using (PluginLog.Medir(Mod, $"Pranchas: passo 3 em {frames.Count} profile view(s)"))
        {
            if ((trocarEstilo || trocarBandas) && m is { TemPerfil: true })
            {
                // Estilo ou band set novo: exagero e altura das bandas mudam, entao mede de novo.
                using (Progresso.Faixa(0, 0.1))
                using (PluginLog.Medir(Mod, "Pranchas: calibracao do profile view (estilo/band set novo)"))
                {
                    Progresso.Mostrar("Medindo o profile view com o estilo e o band set novos...", 0);
                    double ini, fim;
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        var al = (CivilDb.Alignment)tr.GetObject(s.Eixo, OpenMode.ForRead);
                        ini = double.IsNaN(s.EstacaInicial) ? al.StartingStation : Math.Clamp(s.EstacaInicial, al.StartingStation, al.EndingStation);
                        fim = double.IsNaN(s.EstacaFinal) ? al.EndingStation : Math.Clamp(s.EstacaFinal, al.StartingStation, al.EndingStation);
                        tr.Commit();
                    }
                    Calibracao = "";
                    Calibrar(db, s, m, ini, fim, BasePadraoPerfis(db, m), avisos);
                    if (Calibracao.Length > 0)
                        PluginLog.Info(Mod, "Pranchas: " + Calibracao);
                }
            }
            using (Progresso.Faixa(trocarEstilo || trocarBandas ? 0.1 : 0, 1))
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    SheetFrameInfo f = frames[i];
                    if (Progresso.Cancelado)
                    {
                        parou = true;
                        break;
                    }
                    Progresso.Passo($"Profile view {f.LayoutNome} ({i + 1} de {frames.Count})", i, frames.Count);
                    try
                    {
                        string? aviso;
                        // Estilo e band set primeiro, gravados: as bandas importadas e que recebem Profile1/Profile2.
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            if (!TrocarEstiloEBandas(tr, f.Perfil, s, trocarBandas, avisos, f.LayoutNome))
                                incompletos++;
                            tr.Commit();
                        }
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            AplicarDrawEBandas(tr, f.Perfil, s, avisos, f.LayoutNome);
                            aviso = AjustarCotas(tr, f.Perfil, s, m, f.E0, f.E1);
                            tr.Commit();
                        }
                        AplicarLabels(db, f.Perfil, s, avisos, f.LayoutNome);
                        if (Valido(f.VpPerfil))
                            aviso = Juntar(aviso, AlinharPerfil(db, f.VpPerfil, f.Perfil, s.MargemOrigem));
                        f.Aviso = aviso ?? "";
                        if (aviso is not null)
                            avisos.Add($"{f.LayoutNome}: {aviso}");
                        ok++;
                    }
                    catch (System.Exception ex)
                    {
                        incompletos++;
                        avisos.Add($"{f.LayoutNome}: opcoes nao aplicadas ({ex.Message})");
                        PluginLog.Erro(Mod, $"Pranchas: {f.LayoutNome}: opcoes dos profiles", ex);
                    }
                }
            }
            // Calibracao nova (e com que estilo/band set) e avisos das sheets.
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SheetFrameInfo f in frames)
                {
                    try
                    {
                        GravarFrame(tr, f.Id, f);
                    }
                    catch (System.Exception)
                    {
                        // Aviso fica so no log.
                    }
                }
                if (!parou && incompletos == 0)
                {
                    // Todos com o estilo e o band set da serie (cancelado ou com falha: tenta de novo no proximo Aplicar).
                    s.CalEstilo = s.EstiloPerfil;
                    s.CalBandas = s.BandasPerfil;
                }
                Gravar(tr, db, s);
                tr.Commit();
            }
        }
        Relatar(doc, avisos);
        return new OpResult(ok, avisos.Count, $"Passo 3 aplicado em {ok} de {frames.Count} profile view(s)" +
                                              (parou ? " (cancelado)" : "") + (avisos.Count > 0 ? $"; {avisos.Count} aviso(s) no log (Ctrl+Shift+L)." : "."));
    }

    /// <summary>
    /// Estilo do profile view (se diferente do da serie) e, se o band set da serie mudou, importa o
    /// band set novo (as bandas voltam as do band set; Profile1/Profile2 vem depois).
    /// </summary>
    /// <returns>false se o estilo ou o band set nao puderam ser aplicados (falta na API).</returns>
    private static bool TrocarEstiloEBandas(Transaction tr, ObjectId pvId, SheetSeries s, bool importarBandas, List<string> avisos, string nome)
    {
        bool ok = true;
        DBObject pv = tr.GetObject(pvId, OpenMode.ForRead);
        if (Valido(s.EstiloPerfil) && Ler(pv, "StyleId") is ObjectId atual && atual != s.EstiloPerfil)
        {
            DBObject w = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
            PropertyInfo? estilo = Propriedade(w.GetType(), "StyleId");
            if (estilo is null || !estilo.CanWrite)
            {
                AvisoUmaVez(avisos, nome, "estilo do profile view nao trocado: a API nao tem ProfileView.StyleId gravavel");
                ok = false;
            }
            else
            {
                Chamar(() => estilo.SetValue(w, s.EstiloPerfil), "estilo do profile view");
            }
        }
        if (!importarBandas || !Valido(s.BandasPerfil))
            return ok;
        DBObject pvW = tr.GetObject(pvId, OpenMode.ForWrite, false, true);
        object? bandas = Ler(pvW, "Bands");
        MethodInfo? importar = bandas?.GetType().GetMethod("ImportBandSetStyle", new[] { typeof(ObjectId) });
        if (bandas is null || importar is null)
        {
            AvisoUmaVez(avisos, nome, "band set nao trocado: a API nao tem ProfileView.Bands.ImportBandSetStyle");
            return false;
        }
        Chamar(() => importar.Invoke(bandas, new object[] { s.BandasPerfil }), "importar o band set");
        return ok;
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
            // Criado ja com o trecho de teste e cotas automaticas (uma regeneracao); se a
            // configuracao falhar junto com a criacao, ou o Civil nao respeitar o trecho, vai de
            // novo com o profile view ja gravado.
            PropertyInfo? modo = null, pMin = null, pMax = null;
            bool configurado = false;
            void Configurar(Transaction t)
            {
                DefinirEstacas(t, pv, ini, ini + lt);
                AplicarDrawEBandas(t, pv, s, new List<string>(), "calibracao");
                DBObject o = t.GetObject(pv, OpenMode.ForWrite, false, true);
                modo = Prop(o, "ElevationRangeMode");
                pMin = Prop(o, "ElevationMin", "MinimumElevation");
                pMax = Prop(o, "ElevationMax", "MaximumElevation");
                if (modo is not null)
                    Chamar(() => DefinirEnum(o, modo, "Auto"), "modo de cotas");
            }
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                pv = CriarProfileView(tr, db, s.Eixo, origem + new Vector3d(0, m.AlturaPerfil * 3, 0), "C3D calibracao " + DateTime.Now.Ticks, s.EstiloPerfil, s.BandasPerfil);
                try
                {
                    using Transaction cfg = db.TransactionManager.StartTransaction();
                    Configurar(cfg);
                    cfg.Commit();
                    configurado = true;
                }
                catch (System.Exception ex)
                {
                    PluginLog.Diag(Mod, $"Pranchas: calibracao: configuracao junto com a criacao falhou ({Msg(ex)}); refazendo.");
                }
                tr.Commit();
            }
            if (!configurado || !EstacasConferem(db, pv, ini, ini + lt))
            {
                using Transaction tr = db.TransactionManager.StartTransaction();
                Configurar(tr);
                tr.Commit();
            }

            double w, h, emin = double.NaN, emax = double.NaN, ve = double.NaN, dir = double.NaN;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var e = (Entity)tr.GetObject(pv, OpenMode.ForRead);
                Extents3d ext = e.GeometricExtents;
                w = ext.MaxPoint.X - ext.MinPoint.X;
                h = ext.MaxPoint.Y - ext.MinPoint.Y;
                if (Ler(e, "Location") is Point3d loc)
                    dir = Math.Max(0, ext.MaxPoint.X - (loc.X + lt)); // cotas e rotulos depois do fim do grafico
                if (pMin is not null && pMax is not null)
                {
                    emin = Convert.ToDouble(pMin.GetValue(e), Inv);
                    emax = Convert.ToDouble(pMax.GetValue(e), Inv);
                }
                ve = ExageroDoEstilo(tr, e);
                tr.Commit();
            }
            s.MargemPerfil = Math.Max(0, w - lt);
            s.MargemDireita = dir;
            if (s.MargemPerfil > m.LarguraPerfil * 0.5 || !EstacasConferem(db, pv, ini, ini + lt))
            {
                // Medicao fora do esperado (o trecho de teste nao foi respeitado?): margem estimada.
                avisos.Add($"calibracao do profile view: margem medida de {s.MargemPerfil:0.#} m fora do esperado; usando a estimativa");
                PluginLog.Aviso(Mod, $"Pranchas: calibracao: largura {w:0.#} m para {lt:0} m de trecho; estacas do PV de teste {(EstacasConferem(db, pv, ini, ini + lt) ? "ok" : "diferentes do pedido")}.");
                s.MargemPerfil = double.NaN;
                s.MargemDireita = double.NaN;
            }
            else if (!double.IsNaN(dir) && dir > m.LarguraPerfil * 0.3)
            {
                s.MargemDireita = double.NaN;
            }
            s.CalEstilo = s.EstiloPerfil;
            s.CalBandas = s.BandasPerfil;

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
            Calibracao = $"calibracao do perfil: {lt:0} m de eixo ocupam {w:0.#} m (margem {(double.IsNaN(s.MargemPerfil) ? "estimada" : s.MargemPerfil.ToString("0.#", Inv) + " m")}, " +
                         $"depois do fim do grafico {(double.IsNaN(s.MargemDireita) ? "estimada" : s.MargemDireita.ToString("0.#", Inv) + " m")}), altura {h:0.#} m, exagero {(double.IsNaN(s.Exagero) ? "?" : s.Exagero.ToString("0.##", Inv))}";
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
            if (Ler(pv, "StyleId") is not ObjectId sid || !Valido(sid))
                return double.NaN;
            DBObject st = tr.GetObject(sid, OpenMode.ForRead);
            object? graph = Ler(st, "GraphStyle");
            object? ve = graph is null ? null : Ler(graph, "VerticalExaggeration");
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
            PropertyInfo? p = Propriedade(o.GetType(), n);
            if (p is not null)
                return p;
        }
        return null;
    }

    /// <summary>
    /// Propriedade pelo nome sem AmbiguousMatchException (os tipos do Civil redeclaram propriedades
    /// com "new"): fica a declarada no tipo mais derivado.
    /// </summary>
    internal static PropertyInfo? Propriedade(Type t, string nome) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name == nome && p.GetIndexParameters().Length == 0)
            .OrderByDescending(p => Profundidade(p.DeclaringType))
            .FirstOrDefault();

    private static int Profundidade(Type? t)
    {
        int n = 0;
        for (; t is not null; t = t.BaseType)
            n++;
        return n;
    }

    internal static object? Ler(object o, string nome)
    {
        try
        {
            return Propriedade(o.GetType(), nome)?.GetValue(o);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static InvalidOperationException Faltando(object o, string nome, string contendo) =>
        new($"{o.GetType().Name} nao tem {nome} (propriedades com \"{contendo}\": " +
            string.Join(", ", o.GetType().GetProperties().Where(p => p.Name.Contains(contendo, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name)) + ")");

    private static void DefinirEnum(object o, PropertyInfo p, string contendo)
    {
        object valor = ValorEnum(p, contendo);
        if (!Equals(p.GetValue(o), valor))
            p.SetValue(o, valor);
    }

    private static object ValorEnum(PropertyInfo p, string contendo)
    {
        if (!p.PropertyType.IsEnum)
            throw new InvalidOperationException($"{p.Name} nao e enum ({p.PropertyType.Name})");
        string nome = Enum.GetNames(p.PropertyType).FirstOrDefault(n => n.Contains(contendo, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"{p.PropertyType.Name} sem valor \"{contendo}\" ({string.Join(", ", Enum.GetNames(p.PropertyType))})");
        return Enum.Parse(p.PropertyType, nome);
    }

    /// <summary>A propriedade enum ja esta no valor que DefinirEnum escolheria.</summary>
    private static bool EnumE(object o, PropertyInfo p, string contendo)
    {
        try
        {
            return Equals(p.GetValue(o), ValorEnum(p, contendo));
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    private static bool Igual(object? valor, double x)
    {
        try
        {
            return valor is not null && Math.Abs(Convert.ToDouble(valor, Inv) - x) < 1e-6;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>Muda um intervalo sem passar por inicio maior que fim (a API recusa); igual, nao escreve.</summary>
    private static void DefinirIntervalo(object o, PropertyInfo pMin, PropertyInfo pMax, double min, double max)
    {
        if (Igual(pMin.GetValue(o), min) && Igual(pMax.GetValue(o), max))
            return;
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
    /// <summary>
    /// Resumo da geometria do alignment (estacas, comprimento e 33 pontos ao longo dele): muda
    /// quando o eixo muda de verdade, e nao quando o Civil so regenera o objeto. "" = ilegivel.
    /// </summary>
    public static string AssinaturaEixo(Database db, ObjectId eixo)
    {
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            string r = "";
            if (Valido(eixo) && tr.GetObject(eixo, OpenMode.ForRead) is CivilDb.Alignment al)
            {
                var sb = new System.Text.StringBuilder();
                double a = al.StartingStation, b = al.EndingStation;
                sb.Append(a.ToString("F4", Inv)).Append('|').Append(b.ToString("F4", Inv)).Append('|').Append(al.Length.ToString("F4", Inv));
                var ax = new Eixo(al);
                const int n = 32;
                for (int i = 0; i <= n; i++)
                {
                    Point3d p = ax.Ponto(a + (b - a) * i / n);
                    sb.Append('|').Append(p.X.ToString("F4", Inv)).Append(',').Append(p.Y.ToString("F4", Inv));
                }
                // Profiles (greide, terreno): editar um deles tambem pede elevations novas.
                foreach (ObjectId pid in al.GetProfileIds())
                {
                    if (tr.GetObject(pid, OpenMode.ForRead) is not CivilDb.Profile pf)
                        continue;
                    double p0 = pf.StartingStation, p1 = pf.EndingStation;
                    sb.Append("|P").Append(pid.Handle.ToString()).Append(':').Append(p0.ToString("F3", Inv)).Append('-').Append(p1.ToString("F3", Inv));
                    for (int i = 0; i <= n && p1 > p0; i++)
                    {
                        try
                        {
                            sb.Append(',').Append(pf.ElevationAt(p0 + (p1 - p0) * i / n).ToString("F4", Inv));
                        }
                        catch (System.Exception)
                        {
                            sb.Append(",-");
                        }
                    }
                }
                r = sb.ToString();
            }
            tr.Commit();
            return r;
        }
        catch (System.Exception)
        {
            return "";
        }
    }

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

        private readonly Dictionary<long, Point3d> _grade = new();
        private const double PassoGrade = 0.5;

        /// <summary>
        /// Ponto na estaca da grade de 0,5 m mais proxima, guardado: o encaixe dos frames testa o
        /// mesmo trecho muitas vezes (busca binaria) e cada PointLocation passa pela API do Civil.
        /// Continua sendo um ponto do eixo (so a estaca anda ate 0,25 m); as pontas usam Ponto().
        /// </summary>
        public Point3d PontoRapido(double s)
        {
            long k = (long)Math.Round(Limitar(s) / PassoGrade);
            if (!_grade.TryGetValue(k, out Point3d p))
                _grade[k] = p = Ponto(k * PassoGrade);
            return p;
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
    private static List<FrameGeo> Encaixar(Eixo ax, double ini, double fim, double w, double h, double folga, double arred, double maxPerfil, bool girar, bool ultimaCheia)
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
            // Sobra pequena no fim: estica este frame ate o fim se couber; senao (folha curta
            // permitida) recua um modulo para a ultima folha nao sair quase vazia.
            if (fim - s1 > 0.01 && fim - s1 < minimo)
            {
                if (fim - s0 <= Math.Min(maxPerfil, uw * 2.5) && Cabe(ax, s0, fim, uw, uh, girar, out _))
                    s1 = fim;
                else if (!ultimaCheia && arred > 0 && s1 - arred > s0 + 1)
                    s1 -= arred;
            }
            if (s1 - s0 < 1 && fim - s0 < 1)
                break;
            if (s1 - s0 < 1)
                throw new InvalidOperationException($"O eixo nao cabe no frame a partir da estaca {Estaca(s0)}: aumente a viewport de planta, reduza a escala ou a folga.");

            double inicioFolha = s0;
            if (ultimaCheia && lista.Count > 0 && fim - s1 <= 0.01)
            {
                // Ultima folha cheia: conta do fim para tras ate o frame encher; o trecho antes do
                // corte (s0) repete o fim da folha anterior.
                double baixo = Math.Max(ini, fim - Math.Min(maxPerfil, uw * 2.5));
                double alto = s0;
                if (Cabe(ax, baixo, fim, uw, uh, girar, out _))
                {
                    alto = baixo;
                }
                else
                {
                    for (int k = 0; k < 40 && alto - baixo > 0.01; k++)
                    {
                        double meio = (baixo + alto) / 2;
                        if (Cabe(ax, meio, fim, uw, uh, girar, out _))
                            alto = meio;
                        else
                            baixo = meio;
                    }
                }
                if (arred > 0)
                {
                    double r = Math.Ceiling(alto / arred - 1e-9) * arred;
                    if (r < s0)
                        alto = r;
                }
                inicioFolha = Math.Max(ini, Math.Min(alto, s0));
            }

            Cabe(ax, inicioFolha, s1, uw, uh, girar, out FrameGeo geo);
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
            pts[i] = i == 0 || i == n ? ax.Ponto(s0 + (s1 - s0) * i / n) : ax.PontoRapido(s0 + (s1 - s0) * i / n);

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

    /// <summary>Rotulo do frame (nome da sheet e estacas); o existente so e reescrito se mudou.</summary>
    private static ObjectId Rotulo(Transaction tr, ObjectId msId, SheetFrameInfo f, Point3d[] v, double escala, ObjectId existente)
    {
        Vector3d u = (v[1] - v[0]).GetNormal(), n = (v[3] - v[0]).GetNormal();
        double alt = escala > 0 ? 3.0 / escala : v[0].DistanceTo(v[3]) / 60;
        Point3d onde = v[3] + u * (alt * 0.8) - n * (alt * 0.8);
        double rot = Math.Atan2(u.Y, u.X);
        string texto = $"{f.LayoutNome}\\P{Estaca(f.E0)} a {Estaca(f.E1)}";
        MText t;
        if (Valido(existente) && tr.GetObject(existente, OpenMode.ForRead) is MText mt)
        {
            if (mt.Contents == texto && mt.Attachment == AttachmentPoint.TopLeft && Math.Abs(mt.TextHeight - alt) < 1e-9 &&
                mt.Location.DistanceTo(onde) < 1e-6 && Math.Abs(Math.IEEERemainder(mt.Rotation - rot, 2 * Math.PI)) < 1e-9)
                return existente;
            t = (MText)tr.GetObject(existente, OpenMode.ForWrite, false, true);
        }
        else
        {
            t = new MText { Layer = LayerFrame };
            var ms = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForWrite);
            ms.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
        }
        t.Attachment = AttachmentPoint.TopLeft;
        t.TextHeight = alt;
        t.Location = onde;
        t.Rotation = rot;
        t.Contents = texto;
        return t.ObjectId;
    }

    /// <summary>
    /// Match lines entre sheets vizinhas. Monta o que deveria existir e so troca as entidades se
    /// algo mudou (apagar e recriar sempre regenerava o Model a cada atualizacao).
    /// </summary>
    private static void RefazerCortes(Transaction tr, Database db, SheetSeries s, List<SheetFrameInfo> frames, Eixo ax, double escala)
    {
        // A layer tem que existir antes de montar as entidades (Layer de entidade fora do banco).
        GarantirLayer(tr, db, LayerCorte, 1, plotar: true);
        var novas = new List<Entity>();
        List<SheetFrameInfo> ordem = frames.OrderBy(f => f.E0).ToList();
        try
        {
            MontarCortes(tr, novas, ordem, ax, escala);
        }
        catch
        {
            // Entidade fora do banco sem Dispose e finalizada em outra thread (derruba o AutoCAD).
            foreach (Entity e in novas)
                e.Dispose();
            throw;
        }

        List<ObjectId> atuais = s.Cortes.Where(Valido).ToList();
        if (atuais.Count == novas.Count && atuais.Count == s.Cortes.Count &&
            atuais.Zip(novas).All(p => IgualCorte(tr.GetObject(p.First, OpenMode.ForRead), p.Second)))
        {
            foreach (Entity e in novas)
                e.Dispose();
            return;
        }

        var restantes = s.Cortes.Where(id => !Apagar(tr, id) && Valido(id)).ToList();
        s.Cortes.Clear();
        s.Cortes.AddRange(restantes);
        if (novas.Count == 0)
            return;
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite, false, true);
        foreach (Entity e in novas)
            Marcar(tr, ms, s, e);
    }

    private static void MontarCortes(Transaction tr, List<Entity> novas, List<SheetFrameInfo> ordem, Eixo ax, double escala)
    {
        for (int i = 0; i + 1 < ordem.Count; i++)
        {
            SheetFrameInfo a = ordem[i], b = ordem[i + 1];
            double altura = AlturaFrame(tr, a);
            if (Math.Abs(a.E1 - b.E0) <= 0.5)
            {
                Corte(novas, ax, (a.E1 + b.E0) / 2, a.LayoutNome, b.LayoutNome, altura, escala);
            }
            else if (b.E0 < a.E1 && b.E1 > a.E1)
            {
                // Sobreposicao (ex.: ultima folha cheia): o corte fica no fim da folha anterior.
                Corte(novas, ax, a.E1, a.LayoutNome, b.LayoutNome, altura, escala);
            }
            else
            {
                Corte(novas, ax, a.E1, a.LayoutNome, b.LayoutNome, altura, escala);
                Corte(novas, ax, b.E0, a.LayoutNome, b.LayoutNome, altura, escala);
            }
        }
    }

    /// <summary>A entidade do desenho e igual a que seria criada (posicao, texto, tamanho e layer).</summary>
    private static bool IgualCorte(DBObject atual, Entity nova)
    {
        const double tol = 1e-6;
        switch (atual, nova)
        {
            case (Line a, Line b):
                return a.Layer == b.Layer && a.StartPoint.DistanceTo(b.StartPoint) < tol && a.EndPoint.DistanceTo(b.EndPoint) < tol;
            case (MText a, MText b):
                return a.Layer == b.Layer && a.Contents == b.Contents && a.Attachment == b.Attachment &&
                       a.Location.DistanceTo(b.Location) < tol && Math.Abs(a.TextHeight - b.TextHeight) < tol &&
                       Math.Abs(Math.IEEERemainder(a.Rotation - b.Rotation, 2 * Math.PI)) < 1e-9;
            default:
                return false;
        }
    }

    private static double AlturaFrame(Transaction tr, SheetFrameInfo f)
    {
        if (tr.GetObject(f.Id, OpenMode.ForRead) is Polyline pl && Vertices(pl) is Point3d[] v)
            return v[0].DistanceTo(v[3]);
        return 50;
    }

    private static void Corte(List<Entity> novas, Eixo ax, double estaca, string antes, string depois, double alturaFrame, double escala)
    {
        Point3d p = ax.Ponto(estaca);
        Vector3d t = ax.Tangente(estaca);
        var n = new Vector3d(-t.Y, t.X, 0);
        double meia = alturaFrame * 0.42;
        double alt = escala > 0 ? 2.5 / escala : alturaFrame / 80;
        double rot = Math.Atan2(n.Y, n.X);

        novas.Add(new Line(p - n * meia, p + n * meia) { Layer = LayerCorte });

        void Texto(string conteudo, Point3d onde) => novas.Add(new MText
        {
            Layer = LayerCorte,
            Attachment = AttachmentPoint.MiddleCenter,
            TextHeight = alt,
            Location = onde,
            Rotation = rot,
            Contents = conteudo
        });

        Texto($"LINHA DE CORTE - EST. {Estaca(estaca)}", p + n * (meia + alt * 1.5));
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

    /// <summary>Modulo no log (o mesmo do painel, para a aba Log escondida mostrar tudo junto).</summary>
    private const string Mod = "Layouts";

    /// <summary>Avisos de uma operacao: na linha de comando e no log.</summary>
    internal static void Relatar(Document doc, IEnumerable<string> avisos)
    {
        List<string> lista = avisos.Distinct().ToList();
        if (lista.Count == 0)
            return;
        foreach (string a in lista)
            PluginLog.Aviso(Mod, "Pranchas: " + a);
        Escrever(doc, string.Join("\n[Pranchas] ", lista));
    }

    private static void Escrever(Document doc, string texto)
    {
        try
        {
            doc.Editor.WriteMessage("\n[Pranchas] " + texto);
        }
        catch (System.Exception)
        {
            // Sem linha de comando (fechando o desenho): fica so no log.
        }
    }

    private static string? Juntar(string? a, string? b) =>
        string.IsNullOrEmpty(a) ? (string.IsNullOrEmpty(b) ? null : b) : string.IsNullOrEmpty(b) ? a : a + "; " + b;

    private static ObjectId SoValido(ObjectId id) => Valido(id) ? id : ObjectId.Null;

    private static readonly HashSet<string> JaDiagnosticado = new();

    /// <summary>Diagnostico de API (membro que falta, assinatura diferente) uma vez por sessao.</summary>
    private static void DiagUmaVez(string chave, string texto)
    {
        if (JaDiagnosticado.Add(chave))
            PluginLog.Diag(Mod, "Pranchas: " + texto);
    }

    /// <summary>Aviso que valeria para todas as sheets: entra uma vez na operacao.</summary>
    private static void AvisoUmaVez(List<string> avisos, string nome, string texto)
    {
        if (!avisos.Contains(texto))
            avisos.Add(texto);
        DiagUmaVez("aviso:" + texto, $"{nome}: {texto}");
    }

    /// <summary>Nome de um objeto do desenho (estilo, profile, label set) para o log.</summary>
    private static string NomeDe(Database db, ObjectId id)
    {
        if (!Valido(id))
            return "(nenhum)";
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            string nome = Ler(tr.GetObject(id, OpenMode.ForRead), "Name") as string ?? id.Handle.ToString();
            tr.Commit();
            return nome;
        }
        catch (System.Exception)
        {
            return id.Handle.ToString();
        }
    }

    /// <summary>Estaca de 20 m: E + fracao (ex.: 12+5,30), como no QTO.</summary>
    public static string Estaca(double v)
    {
        if (double.IsNaN(v))
            return "";
        // Arredonda ao centimetro antes de separar a estaca, senao 239,99999 vira "11+20,00".
        v = Math.Round(v, 2);
        int est = (int)Math.Floor(v / 20.0 + 1e-9);
        double resto = v - est * 20.0;
        return $"{est}+{resto.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"))}";
    }

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
    /// <summary>Geometria do alignment na ultima atualizacao: o Civil "mexe" no alignment ao regenerar profile views e labels (ao abrir um layout, por exemplo) sem mudar nada.</summary>
    private static readonly Dictionary<Database, Dictionary<ObjectId, string>> Assinaturas = new();
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
        if (!Assinaturas.TryGetValue(db, out Dictionary<ObjectId, string>? ass))
            Assinaturas[db] = ass = new Dictionary<ObjectId, string>();
        foreach (ObjectId e in ass.Keys.Where(k => !eixos.Contains(k)).ToList())
            ass.Remove(e);
        foreach (ObjectId e in eixos.Where(k => !ass.ContainsKey(k)))
            ass[e] = LayoutSheetSeries.AssinaturaEixo(db, e);
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
        Assinaturas.Remove(db);
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

    private static void Marcar(Database db, ObjectId eixo, ObjectId? frame, string motivo)
    {
        if (!Sujos.TryGetValue(db, out Dictionary<ObjectId, HashSet<ObjectId>?>? porEixo))
        {
            Sujos[db] = porEixo = new Dictionary<ObjectId, HashSet<ObjectId>?>();
            PluginLog.Diag("Layouts", "Pranchas (auto-update): disparado por " + motivo + ".");
        }
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
            Marcar(db, eixo, id, $"frame {id.Handle} modificado");
        else if (EixosPorDb.TryGetValue(db, out HashSet<ObjectId>? eixos) && eixos.Contains(id))
            Marcar(db, id, null, $"alignment {id.Handle} modificado");
    }

    private static void OnApagado(object? sender, ObjectErasedEventArgs e)
    {
        if (_suspenso > 0 || sender is not Database db || e.DBObject.IsUndoing)
            return;
        ObjectId id = e.DBObject.ObjectId;
        // Frame apagado: a atualizacao so dele limpa o rotulo e refaz as match lines.
        if (FramesPorDb.TryGetValue(db, out Dictionary<ObjectId, ObjectId>? frames) && frames.TryGetValue(id, out ObjectId eixo))
            Marcar(db, eixo, id, $"frame {id.Handle} apagado");
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (Sujos.Count == 0 || _suspenso > 0)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !Sujos.TryGetValue(doc.Database, out Dictionary<ObjectId, HashSet<ObjectId>?>? porEixo) || !doc.Editor.IsQuiescent)
            return;
        Sujos.Remove(doc.Database);

        Assinaturas.TryGetValue(doc.Database, out Dictionary<ObjectId, string>? ass);
        foreach ((ObjectId eixo, HashSet<ObjectId>? frames) in porEixo)
        {
            try
            {
                if (frames is null)
                {
                    // So o alignment foi tocado: segue so se a geometria mudou de verdade.
                    string agora = LayoutSheetSeries.AssinaturaEixo(doc.Database, eixo);
                    if (ass is not null && ass.TryGetValue(eixo, out string? antes) && antes == agora && agora.Length > 0)
                    {
                        PluginLog.Diag("Layouts", "Pranchas (auto-update): alignment modificado pelo Civil sem mudar a geometria nem os profiles; nada a fazer.");
                        continue;
                    }
                }
                OpResult r = LayoutSheetSeries.Atualizar(doc, eixo, frames);
                // Geometria guardada so depois de uma atualizacao completa (com falha, a proxima tentativa refaz).
                if (ass is not null && r.Falhas == 0)
                    ass[eixo] = LayoutSheetSeries.AssinaturaEixo(doc.Database, eixo);
                PluginLog.Info("Layouts", "Pranchas (auto-update): " + r.Mensagem);
                // Sem nada mudado, a linha de comando fica quieta (so o log registra).
                if (r.Mudou || r.Falhas > 0)
                    doc.Editor.WriteMessage($"\n[Pranchas] {r.Mensagem}");
            }
            catch (System.Exception ex)
            {
                PluginLog.Erro("Layouts", "Pranchas: atualizacao automatica falhou", ex);
                doc.Editor.WriteMessage($"\n[Pranchas] Atualizacao automatica falhou: {ex.Message} (detalhes: {C3DCommands.Core.Log})");
            }
        }
        Recarregar(doc.Database);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
