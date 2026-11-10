using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Aparencia do mapa-chave (pode ser propria de um layout).</summary>
internal sealed class KeyMapEstilo
{
    /// <summary>0 = numero da prancha, 1 = nome do layout, 2 = os dois, 3 = sem texto nas folhas.</summary>
    public int Rotulo { get; set; }
    public short CorDestaque { get; set; } = 1;
    public int Transparencia { get; set; } = 50;
    public string Padrao { get; set; } = "SOLID";
    public double EscalaHachura { get; set; } = 1.0;
    public double AnguloHachura { get; set; }
    public short CorFundo { get; set; } = -1;
    public double FatorTexto { get; set; } = 1.0;
    public bool Titulo { get; set; } = true;

    /// <summary>Hachura que destaca a folha da propria prancha (false = foi apagada no mapa copiado).</summary>
    public bool Destaque { get; set; } = true;

    /// <summary>
    /// Nome do bloco com o mapa copiado de um layout depois de editado a mao (contornos, textos,
    /// titulo e o que mais houver, em coordenadas do Model). Com molde, o plugin desenha o mapa a
    /// partir dele em vez de gerar contornos/textos/titulo: o que foi apagado nao volta.
    /// </summary>
    public string Molde { get; set; } = "";

    /// <summary>Nome do estilo salvo de onde esta aparencia veio (so para mostrar na lista).</summary>
    public string Nome { get; set; } = "";

    /// <summary>Texto de folha movido a mao: nome do layout da folha -> posicao no Model (vale em qualquer caixa).</summary>
    public Dictionary<string, Point2d> PosTexto { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KeyMapEstilo Copia()
    {
        var c = new KeyMapEstilo
        {
            Rotulo = Rotulo, CorDestaque = CorDestaque, Transparencia = Transparencia, Padrao = Padrao, EscalaHachura = EscalaHachura,
            AnguloHachura = AnguloHachura, CorFundo = CorFundo, FatorTexto = FatorTexto, Titulo = Titulo,
            Destaque = Destaque, Molde = Molde, Nome = Nome
        };
        foreach (KeyValuePair<string, Point2d> kv in PosTexto)
            c.PosTexto[kv.Key] = kv.Value;
        return c;
    }
}

/// <summary>Configuracao do mapa-chave, guardada no desenho (NOD, Xrecord C3D_MAPACHAVE).</summary>
internal sealed class KeyMapConfig
{
    public List<ObjectId> Base { get; } = new();
    public Point2d? CaixaMin { get; set; }
    public Point2d? CaixaMax { get; set; }

    /// <summary>0 = numero da prancha, 1 = nome do layout, 2 = os dois.</summary>
    public int Rotulo { get; set; }

    public bool Automatico { get; set; } = true;
    public short CorDestaque { get; set; } = 1;
    public int Transparencia { get; set; } = 50;

    /// <summary>Padrao da hachura da folha atual: SOLID, ANSI31, ANSI37, DOTS...</summary>
    public string Padrao { get; set; } = "SOLID";
    public double EscalaHachura { get; set; } = 1.0;
    public double AnguloHachura { get; set; }       // graus, somado ao giro da folha

    /// <summary>Cor de fundo da hachura de padrao (-1 = sem fundo).</summary>
    public short CorFundo { get; set; } = -1;

    /// <summary>Multiplica a altura do texto calculada como na LISP (1 = igual).</summary>
    public double FatorTexto { get; set; } = 1.0;

    /// <summary>Numero/total dos textos marcados do carimbo (senao ordem/quantidade de layouts).</summary>
    public bool UsarCarimbo { get; set; } = true;

    /// <summary>Total informado a mao (0 = quantidade de layouts com folha).</summary>
    public int TotalManual { get; set; }
    public bool Titulo { get; set; } = true;

    /// <summary>Formato antigo (ate 2.95.4): lista "so nestes layouts". Convertida em Fora/Extras ao ler.</summary>
    public List<string> Layouts { get; } = new();

    /// <summary>Layouts de onde o mapa foi tirado de proposito (ficam fora das atualizacoes).</summary>
    public HashSet<string> Fora { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Layouts que recebem o mapa mesmo sem viewport sobre a base (capa, indice).</summary>
    public HashSet<string> Extras { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aparencia propria por layout; quem nao esta aqui usa a geral (campos desta classe).</summary>
    public Dictionary<string, KeyMapEstilo> Estilos { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aparencia geral: destaque, molde e nome do estilo (ver KeyMapEstilo).</summary>
    public bool Destaque { get; set; } = true;
    public string Molde { get; set; } = "";
    public string NomeEstilo { get; set; } = "";

    /// <summary>Todo o mapa de cada layout num grupo do AutoCAD (seleciona e move tudo junto).</summary>
    public bool Agrupar { get; set; } = true;

    /// <summary>Estilos salvos com nome (presets), guardados no desenho.</summary>
    public Dictionary<string, KeyMapEstilo> Presets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Textos de folha movidos a mao na aparencia geral (ver KeyMapEstilo.PosTexto).</summary>
    public Dictionary<string, Point2d> PosTexto { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KeyMapEstilo Geral()
    {
        var g = new KeyMapEstilo
        {
            Rotulo = Rotulo, CorDestaque = CorDestaque, Transparencia = Transparencia, Padrao = Padrao, EscalaHachura = EscalaHachura,
            AnguloHachura = AnguloHachura, CorFundo = CorFundo, FatorTexto = FatorTexto, Titulo = Titulo,
            Destaque = Destaque, Molde = Molde, Nome = NomeEstilo
        };
        foreach (KeyValuePair<string, Point2d> kv in PosTexto)
            g.PosTexto[kv.Key] = kv.Value;
        return g;
    }

    public void DefinirGeral(KeyMapEstilo e)
    {
        Rotulo = e.Rotulo; CorDestaque = e.CorDestaque; Transparencia = e.Transparencia; Padrao = e.Padrao; EscalaHachura = e.EscalaHachura;
        AnguloHachura = e.AnguloHachura; CorFundo = e.CorFundo; FatorTexto = e.FatorTexto; Titulo = e.Titulo;
        Destaque = e.Destaque; Molde = e.Molde; NomeEstilo = e.Nome;
        PosTexto.Clear();
        foreach (KeyValuePair<string, Point2d> kv in e.PosTexto)
            PosTexto[kv.Key] = kv.Value;
    }

    /// <summary>Aparencia que vale para o layout: a propria dele ou a geral.</summary>
    public KeyMapEstilo Para(string layout) => Estilos.TryGetValue(layout, out KeyMapEstilo? e) ? e : Geral();

    /// <summary>Copia so com o que o desenho do mapa usa, ja com a aparencia do layout.</summary>
    public KeyMapConfig Efetivo(string layout)
    {
        var c = new KeyMapConfig { UsarCarimbo = UsarCarimbo, TotalManual = TotalManual, Automatico = Automatico, Agrupar = Agrupar };
        c.DefinirGeral(Para(layout));
        return c;
    }

    /// <summary>Caixa propria de um layout (quando o mapa foi movido/escalado so nele); os outros usam a caixa geral.</summary>
    public Dictionary<string, (Point2d Min, Point2d Max)> Caixas { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TemCaixa => CaixaMin.HasValue && CaixaMax.HasValue;
}

/// <summary>Resumo de um layout para a lista do painel.</summary>
internal sealed record KeyMapSheet(string Layout, int Ordem, int Regioes, bool TemMapa);

/// <summary>
/// Mapa-chave automatico. A base (objetos escolhidos no Model) vira o bloco C3D_MAPACHAVE.
/// Em cada layout com viewport sobre a base, o plugin insere o bloco na caixa escolhida,
/// desenha o contorno da area de cada folha com o numero/nome e hachura a area da propria
/// folha. Tudo o que o plugin cria leva XData C3D_MAPACHAVE e e refeito a cada atualizacao.
/// </summary>
internal static class LayoutKeyMap
{
    public const string App = "C3D_MAPACHAVE";
    public const string Bloco = "C3D_MAPACHAVE";
    public const string LayerBase = "MAPA_CHAVE_BASE";
    public const string LayerFolhas = "MAPA_CHAVE_FOLHAS";
    public const string LayerTexto = "MAPA_CHAVE_TEXTO";
    public const string LayerDestaque = "MAPA_CHAVE_DESTAQUE";

    /// <summary>Disparado depois de gerar, remover ou mudar a configuracao (o painel se atualiza).</summary>
    public static event EventHandler? Changed;

    internal static void AvisarMudanca() => Changed?.Invoke(null, EventArgs.Empty);

    // ------------------------------------------------------------------
    // Configuracao
    // ------------------------------------------------------------------

    public static KeyMapConfig Ler(Database db)
    {
        var cfg = new KeyMapConfig();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(App) && tr.GetObject(nod.GetAt(App), OpenMode.ForRead) is Xrecord xr && xr.Data is ResultBuffer rb)
        {
            string? caixaDe = null;
            Point2d? caixaMin = null;
            KeyMapEstilo? est = null; // depois de um code 6 (layout) ou 300 (estilo salvo), os codes de aparencia sao dele
            string? posDe = null;
            foreach (TypedValue tv in rb)
            {
                switch (tv.TypeCode)
                {
                    case 4: caixaDe = Convert.ToString(tv.Value); caixaMin = null; break;
                    case 12: { var p = (Point3d)tv.Value; caixaMin = new Point2d(p.X, p.Y); break; }
                    case 13:
                    {
                        var p = (Point3d)tv.Value;
                        if (caixaDe is { Length: > 0 } && caixaMin is Point2d mn)
                            cfg.Caixas[caixaDe] = (mn, new Point2d(p.X, p.Y));
                        break;
                    }
                    case 70: cfg.Rotulo = Convert.ToInt32(tv.Value); break;
                    case 71: cfg.Automatico = Convert.ToInt32(tv.Value) != 0; break;
                    case 62: cfg.CorDestaque = Convert.ToInt16(tv.Value); break;
                    case 72: cfg.Transparencia = Convert.ToInt32(tv.Value); break;
                    case 2: cfg.Padrao = Convert.ToString(tv.Value) ?? "SOLID"; break;
                    case 40: cfg.EscalaHachura = Convert.ToDouble(tv.Value); break;
                    case 50: cfg.AnguloHachura = Convert.ToDouble(tv.Value); break;
                    case 63: cfg.CorFundo = Convert.ToInt16(tv.Value); break;
                    case 41: cfg.FatorTexto = Convert.ToDouble(tv.Value); break;
                    case 73: cfg.UsarCarimbo = Convert.ToInt32(tv.Value) != 0; break;
                    case 74: cfg.TotalManual = Convert.ToInt32(tv.Value); break;
                    case 75: cfg.Titulo = Convert.ToInt32(tv.Value) != 0; break;
                    case 280: cfg.Agrupar = Convert.ToInt32(tv.Value) != 0; break;
                    case 79: { bool d = Convert.ToInt32(tv.Value) != 0; if (est is not null) est.Destaque = d; else cfg.Destaque = d; break; }
                    case 305: { string m = Convert.ToString(tv.Value) ?? ""; if (est is not null) est.Molde = m; else cfg.Molde = m; break; }
                    case 302: { string n = Convert.ToString(tv.Value) ?? ""; if (est is not null) est.Nome = n; else cfg.NomeEstilo = n; break; }
                    case 10: { var p = (Point3d)tv.Value; cfg.CaixaMin = new Point2d(p.X, p.Y); break; }
                    case 11: { var p = (Point3d)tv.Value; cfg.CaixaMax = new Point2d(p.X, p.Y); break; }
                    case 330: cfg.Base.Add((ObjectId)tv.Value); break;
                    case 3: if (Convert.ToString(tv.Value) is { Length: > 0 } ln) cfg.Layouts.Add(ln); break;
                    case 8: if (Convert.ToString(tv.Value) is { Length: > 0 } fo) cfg.Fora.Add(fo); break;
                    case 9: if (Convert.ToString(tv.Value) is { Length: > 0 } ex) cfg.Extras.Add(ex); break;
                    case 6:
                        est = null;
                        if (Convert.ToString(tv.Value) is { Length: > 0 } en)
                            cfg.Estilos[en] = est = new KeyMapEstilo();
                        break;
                    case 300:
                        est = null;
                        if (Convert.ToString(tv.Value) is { Length: > 0 } pn)
                            cfg.Presets[pn] = est = new KeyMapEstilo();
                        break;
                    case 301: posDe = Convert.ToString(tv.Value); break;
                    case 14:
                    {
                        var p = (Point3d)tv.Value;
                        if (posDe is { Length: > 0 })
                            (est?.PosTexto ?? cfg.PosTexto)[posDe] = new Point2d(p.X, p.Y);
                        break;
                    }
                    case 76: if (est is not null) est.Rotulo = Convert.ToInt32(tv.Value); break;
                    case 77: if (est is not null) est.Transparencia = Convert.ToInt32(tv.Value); break;
                    case 78: if (est is not null) est.Titulo = Convert.ToInt32(tv.Value) != 0; break;
                    case 64: if (est is not null) est.CorDestaque = Convert.ToInt16(tv.Value); break;
                    case 65: if (est is not null) est.CorFundo = Convert.ToInt16(tv.Value); break;
                    case 7: if (est is not null) est.Padrao = Convert.ToString(tv.Value) ?? "SOLID"; break;
                    case 42: if (est is not null) est.EscalaHachura = Convert.ToDouble(tv.Value); break;
                    case 43: if (est is not null) est.FatorTexto = Convert.ToDouble(tv.Value); break;
                    case 51: if (est is not null) est.AnguloHachura = Convert.ToDouble(tv.Value); break;
                }
            }
        }
        if (cfg.Layouts.Count > 0)
        {
            // Formato antigo "so nestes layouts": os outros viram "fora" e os da lista entram mesmo sem viewport.
            var dicL = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dicL)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType)
                    continue;
                if (cfg.Layouts.Contains(lay.LayoutName, StringComparer.OrdinalIgnoreCase))
                    cfg.Extras.Add(lay.LayoutName);
                else
                    cfg.Fora.Add(lay.LayoutName);
            }
            cfg.Layouts.Clear();
        }
        tr.Commit();
        return cfg;
    }

    public static bool Existe(Database db)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        bool ok = nod.Contains(App);
        tr.Commit();
        return ok;
    }

    public static void Gravar(Document doc, KeyMapConfig cfg)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
        Xrecord xr;
        if (nod.Contains(App))
        {
            xr = (Xrecord)tr.GetObject(nod.GetAt(App), OpenMode.ForWrite);
        }
        else
        {
            xr = new Xrecord();
            nod.SetAt(App, xr);
            tr.AddNewlyCreatedDBObject(xr, true);
        }

        var dados = new List<TypedValue>
        {
            new(1, "v1"),
            new(70, (short)cfg.Rotulo),
            new(71, (short)(cfg.Automatico ? 1 : 0)),
            new(62, cfg.CorDestaque),
            new(72, (short)cfg.Transparencia),
            new(2, cfg.Padrao),
            new(40, cfg.EscalaHachura),
            new(50, cfg.AnguloHachura),
            new(63, cfg.CorFundo),
            new(41, cfg.FatorTexto),
            new(73, (short)(cfg.UsarCarimbo ? 1 : 0)),
            new(74, (short)cfg.TotalManual),
            new(75, (short)(cfg.Titulo ? 1 : 0)),
            new(280, (short)(cfg.Agrupar ? 1 : 0)),
            new(79, (short)(cfg.Destaque ? 1 : 0)),
            new(305, cfg.Molde),
            new(302, cfg.NomeEstilo)
        };
        if (cfg.CaixaMin is Point2d a && cfg.CaixaMax is Point2d b)
        {
            dados.Add(new TypedValue(10, new Point3d(a.X, a.Y, 0)));
            dados.Add(new TypedValue(11, new Point3d(b.X, b.Y, 0)));
        }
        foreach (ObjectId id in cfg.Base.Where(i => !i.IsNull && !i.IsErased))
            dados.Add(new TypedValue(330, id));
        foreach (string fo in cfg.Fora)
            dados.Add(new TypedValue(8, fo));
        foreach (string ex in cfg.Extras)
            dados.Add(new TypedValue(9, ex));
        foreach (KeyValuePair<string, (Point2d Min, Point2d Max)> kv in cfg.Caixas)
        {
            dados.Add(new TypedValue(4, kv.Key));
            dados.Add(new TypedValue(12, new Point3d(kv.Value.Min.X, kv.Value.Min.Y, 0)));
            dados.Add(new TypedValue(13, new Point3d(kv.Value.Max.X, kv.Value.Max.Y, 0)));
        }
        void Posicoes(Dictionary<string, Point2d> pos)
        {
            foreach (KeyValuePair<string, Point2d> pt in pos)
            {
                dados.Add(new TypedValue(301, pt.Key));
                dados.Add(new TypedValue(14, new Point3d(pt.Value.X, pt.Value.Y, 0)));
            }
        }
        void Estilo(int abre, string nome, KeyMapEstilo e)
        {
            dados.Add(new TypedValue(abre, nome));
            dados.Add(new TypedValue(76, (short)e.Rotulo));
            dados.Add(new TypedValue(77, (short)e.Transparencia));
            dados.Add(new TypedValue(78, (short)(e.Titulo ? 1 : 0)));
            dados.Add(new TypedValue(64, e.CorDestaque));
            dados.Add(new TypedValue(65, e.CorFundo));
            dados.Add(new TypedValue(7, e.Padrao));
            dados.Add(new TypedValue(42, e.EscalaHachura));
            dados.Add(new TypedValue(43, e.FatorTexto));
            dados.Add(new TypedValue(51, e.AnguloHachura));
            dados.Add(new TypedValue(79, (short)(e.Destaque ? 1 : 0)));
            dados.Add(new TypedValue(305, e.Molde));
            dados.Add(new TypedValue(302, e.Nome));
            Posicoes(e.PosTexto);
        }
        // Ordem importa na leitura: geral primeiro; depois cada layout (code 6) e cada estilo salvo (code 300).
        Posicoes(cfg.PosTexto);
        foreach (KeyValuePair<string, KeyMapEstilo> le in cfg.Estilos)
            Estilo(6, le.Key, le.Value);
        foreach (KeyValuePair<string, KeyMapEstilo> pe in cfg.Presets)
            Estilo(300, pe.Key, pe.Value);
        xr.Data = new ResultBuffer(dados.ToArray());
        tr.Commit();

        KeyMapSync.Atualizar(db, cfg.Automatico);
    }

    // ------------------------------------------------------------------
    // Gerar / remover
    // ------------------------------------------------------------------

    /// <summary>Valor no XData que marca uma previa (fica de fora da atualizacao normal).</summary>
    private const string MarcaPrevia = "previa";

    /// <summary>Caixa (papel) e escala/centro para levar a base ao papel.</summary>
    private sealed record Encaixe(Point2d C0, Point2d C1, double S, Point2d CentroCaixa, Point2d CentroBase)
    {
        public double Bw => Math.Abs(C1.X - C0.X);
        public double Bh => Math.Abs(C1.Y - C0.Y);
        public Point2d Papel(Point2d p) => new(CentroCaixa.X + (p.X - CentroBase.X) * S, CentroCaixa.Y + (p.Y - CentroBase.Y) * S);

        /// <summary>Com titulo, a base fica abaixo da faixa do titulo (12% do alto da caixa).</summary>
        public bool Titulo { get; init; }

        /// <summary>Quanto o centro da base fica abaixo do centro da caixa (negativo com titulo).</summary>
        public double Dy => CentroCaixa.Y - (C0.Y + C1.Y) / 2;

        public static Encaixe De(Point2d c0, Point2d c1, Extents3d ext, bool titulo = false)
        {
            double bw = Math.Abs(c1.X - c0.X), bh = Math.Abs(c1.Y - c0.Y);
            double ew = Math.Max(1e-9, ext.MaxPoint.X - ext.MinPoint.X);
            double eh = Math.Max(1e-9, ext.MaxPoint.Y - ext.MinPoint.Y);
            double util = titulo ? bh * 0.88 : bh;
            double s = Math.Min(bw / ew, util / eh) * 0.96;
            return new Encaixe(c0, c1, s,
                new Point2d((c0.X + c1.X) / 2, (c0.Y + c1.Y) / 2 - (titulo ? bh * 0.06 : 0)),
                new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2)) { Titulo = titulo };
        }
    }

    /// <summary>
    /// Refaz o mapa-chave em todos os layouts que mostram a base (mais os incluidos de proposito,
    /// como a capa), menos os que tiveram o mapa tirado. Cada layout usa a aparencia propria, se
    /// tiver, ou a geral. reconstruirBase = refaz o bloco a partir dos objetos do Model.
    /// </summary>
    public static OpResult Gerar(Document doc, bool reconstruirBase)
    {
        Database db = doc.Database;
        KeyMapConfig cfg = Ler(db);
        if (cfg.Base.Count(i => !i.IsNull && !i.IsErased) == 0)
            return new OpResult(0, 0, "Escolha primeiro os objetos da base no Model (Escolher base).");
        if (!cfg.TemCaixa)
            return new OpResult(0, 0, "Escolha primeiro o lugar do mapa no layout (Escolher lugar).");

        using DocumentLock _ = doc.LockDocument();
        int layouts = 0, regioesTotal = 0, fora = 0, movidos = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            (ObjectId blocoId, Extents3d? ext, DBDictionary dic) = Preparar(tr, db, cfg, reconstruirBase);
            if (ext is null)
                return new OpResult(0, 0, "A base do mapa-chave ficou vazia (os objetos escolhidos nao puderam ser copiados).");

            List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> folhas = Folhas(tr, db, ext.Value);
            foreach (var folha in folhas)
            {
                var btr = (BlockTableRecord)tr.GetObject(folha.Btr, OpenMode.ForWrite);
                // Caixa deste layout: a propria (movido/escalado nele) ou a geral. Se o usuario moveu ou
                // escalou a base do mapa com MOVE/SCALE, a caixa acompanha e fica guardada para o layout.
                bool titulo = cfg.Para(folha.Nome).Titulo;
                Encaixe enc = cfg.Caixas.TryGetValue(folha.Nome, out (Point2d Min, Point2d Max) cx)
                    ? Encaixe.De(cx.Min, cx.Max, ext.Value, titulo)
                    : Encaixe.De(cfg.CaixaMin!.Value, cfg.CaixaMax!.Value, ext.Value, titulo);
                if (DetectarMovimento(tr, btr, blocoId) is (Point2d Min, Point2d Max) nova)
                {
                    cfg.Caixas[folha.Nome] = nova;
                    enc = Encaixe.De(nova.Min, nova.Max, ext.Value, titulo);
                    movidos++;
                }
                Limpar(tr, btr, incluirPrevias: false);
                if (cfg.Fora.Contains(folha.Nome))
                {
                    fora++;
                    continue;
                }
                // Sem viewport sobre a base so entra se foi incluida de proposito (capa, indice):
                // recebe a base e o contorno de todas as folhas, sem destaque.
                if (folha.Regioes.Count == 0 && !cfg.Extras.Contains(folha.Nome))
                    continue;
                regioesTotal += Desenhar(tr, dic, btr, folha, folhas, cfg, blocoId, enc, previa: false);
                layouts++;
            }
            tr.Commit();
        }
        if (movidos > 0)
            Gravar(doc, cfg);

        AvisarMudanca();
        string extra = (movidos > 0 ? $"; {movidos} com o mapa movido a mao (lugar guardado para a prancha)" : "") +
                       (fora > 0 ? $"; {fora} layout(s) sem mapa por escolha sua" : "") +
                       (cfg.Estilos.Count > 0 ? $"; {cfg.Estilos.Count} com aparencia propria" : "");
        return new OpResult(layouts, 0,
            layouts > 0
                ? $"Mapa-chave em {layouts} layout(s) ({regioesTotal} area(s) de folha){extra}."
                : "Nenhuma viewport mostra a base: confira a base escolhida e as viewports das folhas.");
    }

    /// <summary>
    /// Previa: o mapa-chave completo (base, todas as folhas e a hachura da folha deste layout, se
    /// houver) desenhado na caixa dada, no layout dado, sem mexer na configuracao. Fica marcado
    /// como previa: a atualizacao normal nao apaga nem refaz; "Remover previas" tira.
    /// </summary>
    public static OpResult Previa(Document doc, string layout, Point2d c0, Point2d c1)
    {
        Database db = doc.Database;
        KeyMapConfig cfg = Ler(db);
        if (cfg.Base.Count(i => !i.IsNull && !i.IsErased) == 0)
            return new OpResult(0, 0, "Escolha primeiro os objetos da base no Model (Escolher base).");
        if (Math.Abs(c1.X - c0.X) < 1e-6 || Math.Abs(c1.Y - c0.Y) < 1e-6)
            return new OpResult(0, 1, "A caixa da previa precisa ter largura e altura.");

        using DocumentLock _ = doc.LockDocument();
        int regioes;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            (ObjectId blocoId, Extents3d? ext, DBDictionary dic) = Preparar(tr, db, cfg, reconstruirBase: false);
            if (ext is null)
                return new OpResult(0, 0, "A base do mapa-chave ficou vazia (os objetos escolhidos nao puderam ser copiados).");
            List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> folhas = Folhas(tr, db, ext.Value);
            int i = folhas.FindIndex(f => f.Nome.Equals(layout, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                return new OpResult(0, 1, $"Layout \"{layout}\" nao encontrado.");
            var btr = (BlockTableRecord)tr.GetObject(folhas[i].Btr, OpenMode.ForWrite);
            var enc = Encaixe.De(new Point2d(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y)), new Point2d(Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y)), ext.Value, cfg.Para(layout).Titulo);
            regioes = Desenhar(tr, dic, btr, folhas[i], folhas, cfg, blocoId, enc, previa: true);
            tr.Commit();
        }
        AvisarMudanca();
        return new OpResult(1, 0, $"Previa do mapa-chave em {layout} ({Destaque(regioes)}). Para tirar: Mapa-chave > Remover previas. " +
                                  "Gostou do lugar? Use \"Escolher area\" na mesma caixa para valer em todas as pranchas.");

        static string Destaque(int r) => r > 0 ? $"{r} area(s) desta folha em destaque" : "esta prancha nao mostra a base: sem destaque";
    }

    /// <summary>Apaga so as previas (de todos os layouts).</summary>
    public static OpResult RemoverPrevias(Document doc)
    {
        int n = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType)
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                n += Limpar(tr, btr, incluirPrevias: true, soPrevias: true);
            }
            tr.Commit();
        }
        AvisarMudanca();
        return new OpResult(n, 0, n == 0 ? "Nenhuma previa do mapa-chave no desenho." : $"{n} objeto(s) de previa removido(s).");
    }

    /// <summary>
    /// Poe (ou refaz) o mapa-chave nos layouts dados, com a aparencia dada so para eles; os outros
    /// layouts ficam como estao. Sem layouts: a aparencia vira a geral, de todas as pranchas, e as
    /// proprias sao apagadas. estilo null = nao mexe na aparencia (so inclui os layouts de volta).
    /// </summary>
    public static OpResult Aplicar(Document doc, IReadOnlyCollection<string> layouts, KeyMapEstilo? estilo)
    {
        KeyMapConfig cfg = Ler(doc.Database);
        if (layouts.Count == 0)
        {
            if (estilo is not null)
            {
                cfg.DefinirGeral(estilo);
                cfg.Estilos.Clear();
            }
        }
        else
        {
            foreach (string l in layouts.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                cfg.Fora.Remove(l);
                cfg.Extras.Add(l);
                if (estilo is not null)
                    cfg.Estilos[l] = estilo;
            }
        }
        Gravar(doc, cfg);
        if (!cfg.TemCaixa || cfg.Base.Count == 0)
        {
            AvisarMudanca();
            return new OpResult(0, 0, "Configuracao gravada. Falta escolher a base no Model e o lugar do mapa no layout para gerar.") { Mudou = false };
        }
        OpResult r = Gerar(doc, false);
        return layouts.Count == 0 ? r : r with { Mensagem = $"Aplicado em {layouts.Count} layout(s) marcado(s). " + r.Mensagem };
    }

    /// <summary>
    /// Copia o mapa-chave como esta desenhado no layout, depois de editado a mao. Tudo o que e do
    /// mapa (contornos, textos, titulo e o que mais estiver nas layers MAPA_CHAVE_*) vai para um
    /// bloco-molde em coordenadas do Model: o que foi apagado, movido ou alterado fica assim no
    /// estilo. A hachura do destaque e lida (padrao, escala, angulo, cor, fundo, transparencia) e
    /// refeita em cada prancha; se foi apagada, o estilo fica sem destaque. Cria so o bloco-molde.
    /// </summary>
    public static KeyMapEstilo? LerDoLayout(Document doc, string layout, out string mensagem)
    {
        Database db = doc.Database;
        KeyMapConfig cfg = Ler(db);
        KeyMapEstilo est = cfg.Para(layout).Copia();
        est.PosTexto.Clear();
        est.Nome = "";
        using DocumentLock _ = doc.LockDocument();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Extents3d? ext = bt.Has(Bloco) ? Extensao(tr, bt[Bloco]) : null;
        if (ext is null)
        {
            mensagem = "Ainda nao ha mapa-chave neste desenho.";
            return null;
        }
        ObjectId blocoId = bt[Bloco];
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> folhas = Folhas(tr, db, ext.Value);
        int i = folhas.FindIndex(f => f.Nome.Equals(layout, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            mensagem = $"Layout \"{layout}\" nao encontrado (abra o layout que tem o mapa editado).";
            return null;
        }

        BlockReference? baseRef = null;
        Hatch? hachura = null;
        var itens = new List<Entity>();
        var btr = (BlockTableRecord)tr.GetObject(folhas[i].Btr, OpenMode.ForRead);
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity en || en is Viewport)
                continue;
            bool doMapa;
            using (ResultBuffer? rb = en.GetXDataForApplication(App))
            {
                if (rb is not null && EhPrevia(rb))
                    continue;
                doMapa = rb is not null || en.Layer.StartsWith(PrefixoLayer, StringComparison.OrdinalIgnoreCase);
            }
            if (!doMapa)
                continue;
            if (en is BlockReference b && b.BlockTableRecord == blocoId)
                baseRef ??= b;
            else if (en is Hatch h && en.Layer.Equals(LayerDestaque, StringComparison.OrdinalIgnoreCase))
                hachura ??= h;
            else
                itens.Add(en);
        }
        if (baseRef is null)
        {
            mensagem = $"O layout \"{layout}\" nao tem a base do mapa-chave. Aplique o mapa nele primeiro (a base nao pode ser apagada).";
            return null;
        }

        double s = Math.Abs(baseRef.ScaleFactors.X);
        if (s < 1e-12)
        {
            mensagem = "A base do mapa-chave deste layout esta com escala zero.";
            return null;
        }
        var o = new Point2d(baseRef.Position.X, baseRef.Position.Y);
        Point2d Papel(Point2d p) => new(o.X + p.X * s, o.Y + p.Y * s);

        // Bloco-molde: os itens do mapa levados do papel para o Model.
        string nomeMolde = "";
        for (int n = 1; n < 100000; n++)
        {
            nomeMolde = "C3D_MAPACHAVE_M" + n;
            if (!bt.Has(nomeMolde))
                break;
        }
        bt.UpgradeOpen();
        var molde = new BlockTableRecord { Name = nomeMolde, Origin = Point3d.Origin };
        bt.Add(molde);
        tr.AddNewlyCreatedDBObject(molde, true);
        Matrix3d paraModel = Matrix3d.Scaling(1.0 / s, Point3d.Origin) * Matrix3d.Displacement(new Vector3d(-o.X, -o.Y, 0));
        KeyMapConfig cfgLayout = cfg.Efetivo(layout);
        int totalFolhas = cfg.TotalManual > 0 ? cfg.TotalManual : folhas.Count(f => f.Regioes.Count > 0);
        int copiados = 0, textos = 0, textosFixos = 0;
        bool temTitulo = false;
        foreach (Entity en in itens)
        {
            try
            {
                string? tag = TagDe(en);
                if (tag == TagTitulo)
                    temTitulo = true;
                if (en is MText mt && tag is not null && tag.StartsWith(TagFolha, StringComparison.Ordinal))
                {
                    textos++;
                    // Texto reescrito a mao fica fixo; o que ainda e o numero/nome da folha continua automatico.
                    string nomeFolha = tag.Substring(TagFolha.Length);
                    int k = folhas.FindIndex(f => f.Nome.Equals(nomeFolha, StringComparison.OrdinalIgnoreCase));
                    if (k < 0 || mt.Contents != RotuloFolha(tr, dic, cfgLayout, folhas[k].Nome, folhas[k].Ordem, totalFolhas))
                    {
                        tag = null;
                        textosFixos++;
                    }
                }
                else if (en is MText or DBText)
                {
                    textos++;
                    textosFixos++;
                }
                var copia = (Entity)en.Clone();
                copia.TransformBy(paraModel);
                Adicionar(tr, molde, copia, false, tag);
                copiados++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }
        est.Molde = nomeMolde;
        est.Titulo = temTitulo;

        est.Destaque = hachura is not null || folhas[i].Regioes.Count == 0;
        if (hachura is not null)
        {
            est.Padrao = string.IsNullOrWhiteSpace(hachura.PatternName) ? "SOLID" : hachura.PatternName.ToUpperInvariant();
            if (hachura.Color.IsByAci)
                est.CorDestaque = hachura.Color.ColorIndex;
            if (hachura.Transparency.IsByAlpha)
                est.Transparencia = Math.Clamp((int)Math.Round((1 - hachura.Transparency.Alpha / 255.0) * 100), 0, 90);
            if (est.Padrao != "SOLID" && folhas[i].Regioes.Count > 0)
            {
                Point2d[] ref0 = folhas[i].Regioes[0].Select(Papel).ToArray();
                double fator = Math.Max(ref0[0].GetDistanceTo(ref0[1]) / 25.0, 0.05) * 0.25;
                est.EscalaHachura = Math.Round(hachura.PatternScale / fator, 3);
                est.AnguloHachura = Math.Round((hachura.PatternAngle - AnguloMaior(ref0)) * 180.0 / Math.PI, 1);
                AcColor fundo = hachura.BackgroundColor;
                est.CorFundo = fundo.IsNone || !fundo.IsByAci ? (short)-1 : fundo.ColorIndex;
            }
        }
        tr.Commit();
        mensagem = $"Mapa de \"{layout}\" copiado como esta: {copiados} objeto(s), {textos} texto(s)" +
                   (textosFixos > 0 ? $" ({textosFixos} com texto fixo, sem numeracao automatica)" : "") +
                   (est.Destaque ? $", destaque {est.Padrao}." : ", sem destaque (hachura apagada).");
        return est;
    }

    /// <summary>Guarda (ou troca) um estilo salvo com nome, no desenho.</summary>
    public static void SalvarPreset(Document doc, string nome, KeyMapEstilo estilo)
    {
        KeyMapConfig cfg = Ler(doc.Database);
        KeyMapEstilo salvo = estilo.Copia();
        salvo.Nome = nome;
        cfg.Presets[nome] = salvo;
        Gravar(doc, cfg);
    }

    public static bool ExcluirPreset(Document doc, string nome)
    {
        KeyMapConfig cfg = Ler(doc.Database);
        if (!cfg.Presets.Remove(nome))
            return false;
        Gravar(doc, cfg);
        return true;
    }

    /// <summary>Apaga o mapa dos layouts dados e deixa-os fora das proximas atualizacoes.</summary>
    public static OpResult RemoverDe(Document doc, IEnumerable<string> layouts)
    {
        var tirar = new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);
        if (tirar.Count == 0)
            return new OpResult(0, 0, "Marque os layouts de onde tirar o mapa-chave.") { Mudou = false };
        Database db = doc.Database;
        KeyMapConfig cfg = Ler(db);
        int n = 0;
        using (doc.LockDocument())
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType || !tirar.Contains(lay.LayoutName))
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                int k = Limpar(tr, btr, incluirPrevias: true);
                Civil3D2026Plugin.Infrastructure.PluginLog.Info("Layouts", $"Mapa-chave: removido de {lay.LayoutName} ({k} objeto(s)).");
                n += k;
            }
            tr.Commit();
        }
        foreach (string l in tirar)
        {
            cfg.Fora.Add(l);
            cfg.Extras.Remove(l);
        }
        Gravar(doc, cfg);
        AvisarMudanca();
        return new OpResult(n, 0, $"Mapa-chave tirado de {tirar.Count} layout(s) ({n} objeto(s)); eles ficam sem mapa ate voce aplicar neles de novo.");
    }

    /// <summary>Marca, no XData da base, de onde ela foi desenhada (posicao, escala e caixa).</summary>
    private const string TagLugar = "lugar";

    /// <summary>
    /// A base do mapa (bloco, nao previa) saiu de onde o plugin a desenhou? Entao o usuario deu
    /// MOVE/SCALE nela (ou no grupo): devolve a caixa equivalente, senao null. A comparacao e com
    /// a posicao/escala gravadas na propria base quando foi desenhada, e nao com a caixa nova da
    /// configuracao: assim, trocar o lugar pelo painel nao e confundido com mover a mao. Base sem
    /// essa marca (mapa de versao anterior) conta como nao movida.
    /// </summary>
    private static (Point2d Min, Point2d Max)? DetectarMovimento(Transaction tr, BlockTableRecord btr, ObjectId blocoId)
    {
        foreach (ObjectId id in btr)
        {
            if (id.ObjectClass.DxfName != "INSERT" || tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br.BlockTableRecord != blocoId)
                continue;
            using ResultBuffer? rb = br.GetXDataForApplication(App);
            if (rb is null || EhPrevia(rb))
                continue;
            TypedValue[] v = rb.AsArray();
            int j = Array.FindIndex(v, x => x.Value is string t && t == TagLugar);
            if (j < 0 || j + 7 >= v.Length)
                return null;
            double[] n = new double[7];
            for (int q = 0; q < 7; q++)
            {
                if (v[j + 1 + q].Value is not double d)
                    return null;
                n[q] = d;
            }
            double sVelho = n[2], sNovo = Math.Abs(br.ScaleFactors.X);
            var posVelha = new Point2d(n[0], n[1]);
            var pos = new Point2d(br.Position.X, br.Position.Y);
            if (sVelho <= 0 || sNovo <= 0)
                return null;
            double tol = Math.Max(1e-4, 1e-6 * (Math.Abs(n[5] - n[3]) + Math.Abs(n[6] - n[4])));
            if (pos.GetDistanceTo(posVelha) <= tol && Math.Abs(sNovo - sVelho) <= 1e-6 * sVelho)
                return null;
            // A caixa acompanha a base: mesmo deslocamento e mesma escala.
            double k = sNovo / sVelho;
            var a = new Point2d(pos.X + (n[3] - posVelha.X) * k, pos.Y + (n[4] - posVelha.Y) * k);
            var b = new Point2d(pos.X + (n[5] - posVelha.X) * k, pos.Y + (n[6] - posVelha.Y) * k);
            return (new Point2d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point2d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }
        return null;
    }

    /// <summary>
    /// Insere o mapa-chave so no layout dado, na caixa clicada: a caixa fica guardada para essa
    /// prancha, ela entra na lista de pranchas escolhidas (as que ja tinham mapa continuam) e o
    /// mapa e refeito. Vale tambem para prancha sem viewport (capa, indice): recebe o mapa inteiro
    /// sem destaque. Se ainda nao havia caixa geral, esta passa a ser a geral.
    /// </summary>
    public static OpResult InserirEm(Document doc, string layout, Point2d c0, Point2d c1)
    {
        if (Math.Abs(c1.X - c0.X) < 1e-6 || Math.Abs(c1.Y - c0.Y) < 1e-6)
            return new OpResult(0, 1, "A caixa do mapa-chave precisa ter largura e altura.");
        KeyMapConfig cfg = Ler(doc.Database);
        if (cfg.Base.Count(i => !i.IsNull && !i.IsErased) == 0)
            return new OpResult(0, 0, "Escolha primeiro os objetos da base no Model (Escolher base).");
        var caixa = (new Point2d(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y)), new Point2d(Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y)));
        cfg.Caixas[layout] = caixa;
        if (!cfg.TemCaixa)
        {
            cfg.CaixaMin = caixa.Item1;
            cfg.CaixaMax = caixa.Item2;
        }
        cfg.Fora.Remove(layout);
        cfg.Extras.Add(layout);
        Gravar(doc, cfg);
        OpResult r = Gerar(doc, false);
        return r with { Mensagem = $"Mapa-chave de {layout} no lugar clicado (guardado so para essa prancha). " + r.Mensagem };
    }

    /// <summary>Layouts dados voltam a usar a caixa geral e o mapa e refeito.</summary>
    public static OpResult PosicaoPadrao(Document doc, IEnumerable<string> layouts)
    {
        KeyMapConfig cfg = Ler(doc.Database);
        int n = 0;
        foreach (string l in layouts)
            if (cfg.Caixas.Remove(l))
                n++;
        if (n == 0)
            return new OpResult(0, 0, "Esses layouts ja usam a posicao geral do mapa-chave.") { Mudou = false };
        Gravar(doc, cfg);
        OpResult r = Gerar(doc, false);
        return r with { Mensagem = $"{n} layout(s) de volta a posicao geral do mapa-chave. " + r.Mensagem };
    }

    /// <summary>Layers, regapp, bloco da base e extensao dela.</summary>
    private static (ObjectId Bloco, Extents3d? Ext, DBDictionary Dic) Preparar(Transaction tr, Database db, KeyMapConfig cfg, bool reconstruirBase)
    {
        GarantirApp(tr, db);
        foreach (string l in new[] { LayerBase, LayerFolhas, LayerTexto, LayerDestaque })
            GarantirLayer(tr, db, l, l == LayerDestaque ? cfg.CorDestaque : l == LayerTexto ? (short)7 : (short)8);
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        ObjectId blocoId = bt.Has(Bloco) ? bt[Bloco] : ObjectId.Null;
        if (blocoId.IsNull || reconstruirBase)
            blocoId = MontarBloco(tr, db, cfg);
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        return (blocoId, Extensao(tr, blocoId), dic);
    }

    /// <summary>Desenha o mapa-chave de uma folha no layout dela, na caixa dada. Retorna as areas da folha.</summary>
    private static int Desenhar(Transaction tr, DBDictionary dic, BlockTableRecord btr,
        (string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes) folha,
        List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> folhas,
        KeyMapConfig cfg, ObjectId blocoId, Encaixe enc, bool previa)
    {
        cfg = cfg.Efetivo(folha.Nome); // aparencia propria do layout, se tiver
        var criados = new ObjectIdCollection();
        void Add(Entity e, string? tag = null)
        {
            Adicionar(tr, btr, e, previa, tag);
            criados.Add(e.ObjectId);
        }
        double s = enc.S, bw = enc.Bw, bh = enc.Bh;
        Point2d c0 = enc.C0, c1 = enc.C1, centroCaixa = enc.CentroCaixa, centroBase = enc.CentroBase;
        Point2d Papel(Point2d p) => enc.Papel(p);

        // Base. Leva no XData de onde foi desenhada (posicao, escala, caixa), para o plugin saber
        // depois se foi movida/escalada a mao (os valores 1040 nao mudam com MOVE/SCALE).
        var baseRef = new BlockReference(new Point3d(centroCaixa.X - centroBase.X * s, centroCaixa.Y - centroBase.Y * s, 0), blocoId)
        {
            ScaleFactors = new Scale3d(s),
            Layer = LayerBase
        };
        Add(baseRef);
        baseRef.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, previa ? MarcaPrevia : "mapa"),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, TagLugar),
            new TypedValue((int)DxfCode.ExtendedDataReal, baseRef.Position.X),
            new TypedValue((int)DxfCode.ExtendedDataReal, baseRef.Position.Y),
            new TypedValue((int)DxfCode.ExtendedDataReal, s),
            new TypedValue((int)DxfCode.ExtendedDataReal, Math.Min(c0.X, c1.X)),
            new TypedValue((int)DxfCode.ExtendedDataReal, Math.Min(c0.Y, c1.Y)),
            new TypedValue((int)DxfCode.ExtendedDataReal, Math.Max(c0.X, c1.X)),
            new TypedValue((int)DxfCode.ExtendedDataReal, Math.Max(c0.Y, c1.Y)));

        // Contorno e rotulo de todas as folhas (rotulo como na LISP: MTEXT acima da aresta
        // superior da viewport, centralizado e girado com a folha).
        int totalFolhas = cfg.TotalManual > 0 ? cfg.TotalManual : folhas.Count(f => f.Regioes.Count > 0);

        // Estilo copiado de um mapa editado a mao: contornos, textos e titulo vem do molde, como
        // foram deixados (o que foi apagado nao volta). So o numero das folhas e atualizado.
        BlockTableRecord? molde = null;
        if (cfg.Molde.Length > 0)
        {
            var btM = (BlockTable)tr.GetObject(btr.Database.BlockTableId, OpenMode.ForRead);
            if (btM.Has(cfg.Molde))
                molde = (BlockTableRecord)tr.GetObject(btM[cfg.Molde], OpenMode.ForRead);
        }
        if (molde is not null)
        {
            Matrix3d paraPapel = Matrix3d.Displacement(new Vector3d(centroCaixa.X - centroBase.X * s, centroCaixa.Y - centroBase.Y * s, 0)) *
                                 Matrix3d.Scaling(s, Point3d.Origin);
            foreach (ObjectId idM in molde)
            {
                if (tr.GetObject(idM, OpenMode.ForRead) is not Entity origem)
                    continue;
                try
                {
                    string? tagM = TagDe(origem);
                    var copia = (Entity)origem.Clone();
                    copia.TransformBy(paraPapel);
                    if (copia is MText mtM && tagM is not null && tagM.StartsWith(TagFolha, StringComparison.Ordinal))
                    {
                        string nomeM = tagM.Substring(TagFolha.Length);
                        int kM = folhas.FindIndex(f => f.Nome.Equals(nomeM, StringComparison.OrdinalIgnoreCase));
                        if (kM >= 0)
                            mtM.Contents = RotuloFolha(tr, dic, cfg, folhas[kM].Nome, folhas[kM].Ordem, totalFolhas);
                    }
                    Add(copia, tagM);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                }
            }
        }

        foreach (var outra in folhas.Where(f => molde is null && f.Regioes.Count > 0))
        {
            foreach (Point2d[] reg in outra.Regioes)
            {
                var pl = new Polyline { Layer = LayerFolhas, Closed = true };
                for (int i = 0; i < reg.Length; i++)
                    pl.AddVertexAt(i, Papel(reg[i]), 0, 0, 0);
                Add(pl);
            }

            if (cfg.Rotulo == 3)
                continue; // sem texto nas folhas
            Point2d[] maior = outra.Regioes.OrderByDescending(Area).First().Select(Papel).ToArray();
            (Point2d pos, double ang, double alt, double larg) = GeometriaRotulo(maior, cfg.Rotulo == 2);
            alt *= Math.Max(0.1, cfg.FatorTexto);
            if (cfg.PosTexto.TryGetValue(outra.Nome, out Point2d movido))
                pos = Papel(movido); // texto movido a mao e copiado para o estilo
            string texto = RotuloFolha(tr, dic, cfg, outra.Nome, outra.Ordem, totalFolhas);
            Add(new MText
            {
                Contents = texto,
                TextHeight = alt,
                Layer = LayerTexto,
                Attachment = AttachmentPoint.BottomCenter,
                Location = new Point3d(pos.X, pos.Y, 0),
                Rotation = ang,
                Width = Math.Max(larg, alt * 4)
            }, TagFolha + outra.Nome);
        }

        // Hachura da propria folha (so se ela mostra a base).
        if (folha.Regioes.Count > 0 && cfg.Destaque)
        {
            var h = new Hatch { Layer = LayerDestaque };
            Add(h);
            bool solida = string.Equals(cfg.Padrao, "SOLID", StringComparison.OrdinalIgnoreCase);
            h.SetHatchPattern(HatchPatternType.PreDefined, solida ? "SOLID" : cfg.Padrao.ToUpperInvariant());
            h.Associative = false;
            h.Color = AcColor.FromColorIndex(AcColorMethod.ByAci, cfg.CorDestaque);
            if (!solida)
            {
                Point2d[] ref0 = folha.Regioes[0].Select(Papel).ToArray();
                h.PatternScale = Math.Max(0.001, cfg.EscalaHachura * Math.Max(ref0[0].GetDistanceTo(ref0[1]) / 25.0, 0.05) * 0.25);
                h.PatternAngle = AnguloMaior(ref0) + cfg.AnguloHachura * Math.PI / 180.0;
                if (cfg.CorFundo >= 0)
                    h.BackgroundColor = AcColor.FromColorIndex(AcColorMethod.ByAci, cfg.CorFundo);
            }
            h.Transparency = new Autodesk.AutoCAD.Colors.Transparency((byte)Math.Round(255 * (1 - Math.Clamp(cfg.Transparencia, 0, 90) / 100.0)));
            foreach (Point2d[] reg in folha.Regioes)
            {
                var pts = new Point2dCollection(reg.Select(Papel).Append(Papel(reg[0])).ToArray());
                var bulges = new DoubleCollection(Enumerable.Repeat(0.0, pts.Count).ToArray());
                h.AppendLoop(HatchLoopTypes.Default, pts, bulges);
            }
            h.EvaluateHatch(true);
        }

        if (molde is null && cfg.Titulo)
        {
            double tituloAlt = Math.Min(bw, bh) * 0.05;
            var titulo = new MText
            {
                Contents = previa ? "MAPA CHAVE (PREVIA)" : "MAPA CHAVE",
                TextHeight = tituloAlt,
                Layer = LayerTexto,
                Attachment = AttachmentPoint.TopCenter,
                Location = new Point3d(centroCaixa.X, Math.Max(c0.Y, c1.Y) - tituloAlt * 0.3, 0),
                Width = Math.Min(bw * 0.62, tituloAlt * 14)
            };
            Add(titulo, TagTitulo);
            double y = Math.Max(c0.Y, c1.Y) - tituloAlt * 1.75;
            Add(new Line(new Point3d(centroCaixa.X - titulo.Width / 2, y, 0), new Point3d(centroCaixa.X + titulo.Width / 2, y, 0)) { Layer = LayerTexto });
        }
        if (cfg.Agrupar && !previa)
            Agrupar(tr, btr, criados);
        return folha.Regioes.Count;
    }

    /// <summary>Apaga o mapa-chave de todos os layouts (a configuracao fica guardada).</summary>
    public static OpResult Remover(Document doc)
    {
        int n = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType)
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                n += Limpar(tr, btr, incluirPrevias: true);
            }
            tr.Commit();
        }

        KeyMapConfig cfg = Ler(db);
        if (cfg.Automatico)
        {
            cfg.Automatico = false;
            Gravar(doc, cfg);
        }
        AvisarMudanca();
        return new OpResult(n, 0, $"Mapa-chave removido ({n} objeto(s)). Atualizacao automatica desligada.");
    }

    /// <summary>Layouts, area(s) de folha sobre a base e se ja tem mapa (lista do painel).</summary>
    public static List<KeyMapSheet> Resumo(Database db)
    {
        var lista = new List<KeyMapSheet>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Extents3d? ext = bt.Has(Bloco) ? Extensao(tr, bt[Bloco]) : null;
        foreach (var f in Folhas(tr, db, ext))
        {
            var btr = (BlockTableRecord)tr.GetObject(f.Btr, OpenMode.ForRead);
            bool tem = false;
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity en)
                    continue;
                using ResultBuffer? rb = en.GetXDataForApplication(App);
                if (rb is not null || en.Layer.StartsWith(PrefixoLayer, StringComparison.OrdinalIgnoreCase))
                {
                    tem = true;
                    break;
                }
            }
            lista.Add(new KeyMapSheet(f.Nome, f.Ordem, f.Regioes.Count, tem));
        }
        tr.Commit();
        return lista;
    }

    // ------------------------------------------------------------------
    // Base (bloco)
    // ------------------------------------------------------------------

    private static ObjectId MontarBloco(Transaction tr, Database db, KeyMapConfig cfg)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord btr;
        if (bt.Has(Bloco))
        {
            btr = (BlockTableRecord)tr.GetObject(bt[Bloco], OpenMode.ForWrite);
            foreach (ObjectId id in btr)
                tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
        }
        else
        {
            bt.UpgradeOpen();
            btr = new BlockTableRecord { Name = Bloco, Origin = Point3d.Origin };
            bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
        }

        foreach (ObjectId id in cfg.Base)
        {
            if (id.IsNull || id.IsErased || !id.IsValid)
                continue;
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent)
                continue;
            foreach (Entity copia in Simplificar(ent, 0, clonar: true))
            {
                btr.AppendEntity(copia);
                tr.AddNewlyCreatedDBObject(copia, true);
            }
        }
        return btr.ObjectId;
    }

    /// <summary>
    /// Copia de curvas/hachuras do AutoCAD; objetos do Civil e blocos sao explodidos
    /// (ate 3 niveis) e so as curvas sao mantidas.
    /// </summary>
    private static IEnumerable<Entity> Simplificar(Entity ent, int nivel, bool clonar)
    {
        bool nativo = ent.GetType().Namespace == "Autodesk.AutoCAD.DatabaseServices";
        if (nativo && ent is Curve and not Xline and not Ray)
        {
            yield return clonar ? (Entity)ent.Clone() : ent;
            yield break;
        }
        if (nativo && ent is Hatch)
        {
            yield return clonar ? (Entity)ent.Clone() : ent;
            yield break;
        }
        if (nivel >= 3)
        {
            if (!clonar)
                ent.Dispose();
            yield break;
        }

        var partes = new DBObjectCollection();
        try
        {
            ent.Explode(partes);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        if (!clonar)
            ent.Dispose();

        foreach (DBObject o in partes)
        {
            if (o is Entity e)
                foreach (Entity r in Simplificar(e, nivel + 1, clonar: false))
                    yield return r;
            else
                o.Dispose();
        }
    }

    private static Extents3d? Extensao(Transaction tr, ObjectId blocoId)
    {
        var btr = (BlockTableRecord)tr.GetObject(blocoId, OpenMode.ForRead);
        Extents3d? ext = null;
        foreach (ObjectId id in btr)
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                    continue;
                Extents3d x = e.GeometricExtents;
                if (ext is Extents3d a)
                {
                    a.AddExtents(x);
                    ext = a;
                }
                else
                {
                    ext = x;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
            }
        }
        return ext;
    }

    // ------------------------------------------------------------------
    // Regioes das folhas
    // ------------------------------------------------------------------

    /// <summary>
    /// Para cada layout (ordem das abas), as areas do Model mostradas pelas viewports de planta
    /// que cruzam a base. Viewports de perfil/detalhe fora da base ficam de fora.
    /// </summary>
    private static List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> Folhas(Transaction tr, Database db, Extents3d? baseExt)
    {
        var lista = new List<(string, int, ObjectId, List<Point2d[]>)>();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType)
                continue;

            var regioes = new List<Point2d[]>();
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            ObjectIdCollection vpsLayout = lay.GetViewports();
            ObjectId papel = vpsLayout.Count > 0 ? vpsLayout[0] : ObjectId.Null;
            bool primeiro = vpsLayout.Count == 0;

            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                if (id == papel || primeiro)
                {
                    primeiro = false;
                    continue;
                }
                Point2d[]? reg = Regiao(tr, vp);
                if (reg is null)
                    continue;
                if (baseExt is Extents3d be && !Cruza(reg, be))
                    continue;
                regioes.Add(reg);
            }
            lista.Add((lay.LayoutName, lay.TabOrder, lay.BlockTableRecordId, regioes));
        }
        return lista.OrderBy(f => f.Item2).ToList();
    }

    /// <summary>Contorno da viewport levado do papel para o Model (so vista em planta).</summary>
    private static Point2d[]? Regiao(Transaction tr, Viewport vp)
    {
        if (!vp.ViewDirection.IsParallelTo(Vector3d.ZAxis) || vp.CustomScale <= 0 || vp.Width <= 0 || vp.Height <= 0)
            return null;

        var papel = new List<Point3d>();
        if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull && tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) is Curve clip)
        {
            try
            {
                const int n = 48;
                for (int k = 0; k < n; k++)
                    papel.Add(clip.GetPointAtParameter(clip.StartParam + (clip.EndParam - clip.StartParam) * k / n));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                papel.Clear();
            }
        }
        if (papel.Count < 3)
        {
            Point3d c = vp.CenterPoint;
            double w = vp.Width / 2, h = vp.Height / 2;
            papel.AddRange(new[]
            {
                new Point3d(c.X - w, c.Y - h, 0), new Point3d(c.X + w, c.Y - h, 0),
                new Point3d(c.X + w, c.Y + h, 0), new Point3d(c.X - w, c.Y + h, 0)
            });
        }

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

    private static bool Cruza(Point2d[] reg, Extents3d e)
    {
        double minX = reg.Min(p => p.X), maxX = reg.Max(p => p.X), minY = reg.Min(p => p.Y), maxY = reg.Max(p => p.Y);
        return minX <= e.MaxPoint.X && maxX >= e.MinPoint.X && minY <= e.MaxPoint.Y && maxY >= e.MinPoint.Y;
    }

    // ------------------------------------------------------------------
    // Utilitarios
    // ------------------------------------------------------------------

    /// <summary>
    /// Posicao, rotacao, altura e largura do rotulo, como na LISP (MK:ViewportLabelGeom):
    /// os pontos vem na ordem BL, BR, TR, TL do papel; a aresta TL-TR e o topo da folha.
    /// </summary>
    private static (Point2d Pos, double Ang, double Alt, double Larg) GeometriaRotulo(Point2d[] p, bool ambos)
    {
        if (p.Length < 4)
        {
            Point2d c = Centroide(p);
            double d = Math.Sqrt(Area(p));
            return (c, 0, Math.Max(d * 0.08, 1e-3), d);
        }
        Point2d p0 = p[0], p1 = p[1], p2 = p[2], p3 = p[3];
        double l1 = p0.GetDistanceTo(p1), l2 = p0.GetDistanceTo(p3);
        double major = Math.Max(l1, l2), minor = Math.Min(l1, l2);
        if (major < 1e-12) major = 1;
        if (minor < 1e-12) minor = major * 0.2;

        var top = new Vector2d(p2.X - p3.X, p2.Y - p3.Y);
        if (top.Length < 1e-12) top = new Vector2d(p1.X - p0.X, p1.Y - p0.Y);
        double ang = Math.Atan2(top.Y, top.X);
        var topMid = new Point2d((p3.X + p2.X) / 2, (p3.Y + p2.Y) / 2);
        Point2d centro = Centroide(p);
        var fora = new Vector2d(topMid.X - centro.X, topMid.Y - centro.Y);
        fora = fora.Length < 1e-12 ? new Vector2d(-top.Y, top.X).GetNormal() : fora.GetNormal();

        double h = Math.Min(minor * (ambos ? 0.145 : 0.18), major * (ambos ? 0.065 : 0.08));
        h = Math.Max(h, major * 0.012);
        double offset = h * 0.22;
        var pos = new Point2d(topMid.X + fora.X * offset, topMid.Y + fora.Y * offset);
        return (pos, ang, h, Math.Max(major * 0.92, h * 4));
    }

    private static double AnguloMaior(Point2d[] p)
    {
        if (p.Length < 4) return 0;
        double l1 = p[0].GetDistanceTo(p[1]), l2 = p[0].GetDistanceTo(p[3]);
        Point2d a = p[0], b = l1 >= l2 ? p[1] : p[3];
        return Math.Atan2(b.Y - a.Y, b.X - a.X);
    }

    /// <summary>Numero/total do carimbo (textos marcados) ou ordem/quantidade de layouts.</summary>
    private static string RotuloFolha(Transaction tr, DBDictionary dic, KeyMapConfig cfg, string nome, int ordem, int total)
    {
        string numero = Fmt(ordem), tot = "/" + Fmt(total);
        if (cfg.UsarCarimbo && dic.Contains(nome))
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(nome), OpenMode.ForRead);
            (string? n, string? t) = LayoutNumbering.LerNumeracao(tr, lay);
            if (!string.IsNullOrWhiteSpace(n)) numero = n.Trim();
            if (!string.IsNullOrWhiteSpace(t))
                tot = t.Trim().StartsWith('/') ? t.Trim() : "/" + t.Trim();
        }
        string num = numero + tot;
        return cfg.Rotulo switch { 1 => nome, 2 => nome + "\\P" + num, _ => num };
    }

    private static string Fmt(int n) => n < 10 ? "0" + n : n.ToString();

    private static double Area(Point2d[] p)
    {
        double a = 0;
        for (int i = 0; i < p.Length; i++)
        {
            Point2d q = p[i], r = p[(i + 1) % p.Length];
            a += q.X * r.Y - r.X * q.Y;
        }
        return Math.Abs(a / 2);
    }

    private static Point2d Centroide(Point2d[] p) => new(p.Average(q => q.X), p.Average(q => q.Y));

    /// <summary>Marca de qual item e (texto de folha ou titulo), gravada no XData.</summary>
    private static string? TagDe(Entity e)
    {
        using ResultBuffer? rb = e.GetXDataForApplication(App);
        return rb?.AsArray().Skip(1).Select(v => v.Value as string)
            .FirstOrDefault(t => t is not null && (t == TagTitulo || t.StartsWith(TagFolha, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Poe todo o mapa do layout num grupo do AutoCAD (C3D_MAPACHAVE_handle do layout). O grupo
    /// antigo do layout e reaproveitado: os objetos dele ja foram apagados pelo Limpar.
    /// </summary>
    private static void Agrupar(Transaction tr, BlockTableRecord btr, ObjectIdCollection ids)
    {
        if (ids.Count == 0)
            return;
        try
        {
            Database db = btr.Database;
            var gd = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
            string nome = "C3D_MAPACHAVE_" + btr.Handle;
            Group g;
            if (gd.Contains(nome) && tr.GetObject(gd.GetAt(nome), OpenMode.ForWrite) is Group velho)
            {
                g = velho;
                g.Clear();
            }
            else
            {
                g = new Group("Mapa-chave do layout (Civil3D2026Plugin)", true);
                gd.UpgradeOpen();
                gd.SetAt(nome, g);
                tr.AddNewlyCreatedDBObject(g, true);
            }
            g.Append(ids);
        }
        catch (System.Exception ex)
        {
            Civil3D2026Plugin.Infrastructure.PluginLog.Aviso("Layouts", $"Mapa-chave: nao agrupado ({ex.Message}).");
        }
    }

    private const string TagFolha = "folha:";
    private const string TagTitulo = "titulo";

    private static void Adicionar(Transaction tr, BlockTableRecord btr, Entity ent, bool previa = false, string? tag = null)
    {
        btr.AppendEntity(ent);
        tr.AddNewlyCreatedDBObject(ent, true);
        // Sempre com um valor: XData so com o nome do regapp e o jeito do AutoCAD APAGAR o XData, entao
        // os mapas gerados ate a 2.95.1 ficaram sem marca (por isso Remover nao os achava).
        var xd = new List<TypedValue>
        {
            new((int)DxfCode.ExtendedDataRegAppName, App),
            new((int)DxfCode.ExtendedDataAsciiString, previa ? MarcaPrevia : "mapa")
        };
        if (tag is not null)
            xd.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, tag)); // qual texto e (para "Copiar do layout")
        ent.XData = new ResultBuffer(xd.ToArray());
    }

    private static bool EhPrevia(ResultBuffer rb) =>
        rb.AsArray().Skip(1).Any(v => v.Value is string t && t == MarcaPrevia);

    /// <summary>Prefixo das layers do mapa-chave (plugin e LISP MAPACHAVE: BASE, VIEWPORT, TEXTO, HATCH, FOLHAS, DESTAQUE).</summary>
    private const string PrefixoLayer = "MAPA_CHAVE_";

    /// <summary>
    /// Apaga o mapa-chave do layout: o que o plugin criou (XData) e tambem o que esta nas layers
    /// MAPA_CHAVE_* sem XData (mapas da LISP MAPACHAVE ou de versoes antigas). Previas so se
    /// pedido. Retorna quantos objetos.
    /// </summary>
    private static int Limpar(Transaction tr, BlockTableRecord btr, bool incluirPrevias = false, bool soPrevias = false)
    {
        int n = 0, semMarca = 0;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                continue;
            bool apagar;
            using (ResultBuffer? rb = e.GetXDataForApplication(App))
            {
                if (rb is not null)
                {
                    bool previa = EhPrevia(rb);
                    apagar = !((previa && !incluirPrevias) || (!previa && soPrevias));
                }
                else
                {
                    // Mapa feito pela LISP ou por versao antiga: reconhecido pela layer.
                    apagar = !soPrevias && e.Layer.StartsWith(PrefixoLayer, StringComparison.OrdinalIgnoreCase);
                    if (apagar)
                        semMarca++;
                }
            }
            if (!apagar)
                continue;
            tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
            n++;
        }
        if (semMarca > 0)
            Civil3D2026Plugin.Infrastructure.PluginLog.Diag("Layouts", $"Mapa-chave: {btr.Name}: {semMarca} objeto(s) nas layers MAPA_CHAVE_* sem marca do plugin (LISP/versao antiga) apagado(s).");
        return n;
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

    private static void GarantirLayer(Transaction tr, Database db, string nome, short cor)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(nome))
            return;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = nome, Color = AcColor.FromColorIndex(AcColorMethod.ByAci, cor) };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }
}

/// <summary>
/// Atualizacao automatica do mapa-chave: quando uma viewport ou um layout muda num desenho
/// com mapa-chave automatico, o mapa e refeito no proximo Idle (fora de comando).
/// </summary>
internal static class KeyMapSync
{
    private static readonly HashSet<Database> Ativos = new();
    private static readonly HashSet<Database> Sujos = new();
    private static readonly HashSet<Database> Ligados = new();
    private static bool _iniciado;
    private static bool _gerando;

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

    /// <summary>Liga/desliga a atualizacao automatica de um desenho (chamado ao gravar a configuracao).</summary>
    public static void Atualizar(Database db, bool automatico)
    {
        if (automatico)
            Ativos.Add(db);
        else
            Ativos.Remove(db);
    }

    private static void Ligar(Document? doc)
    {
        if (doc is null)
            return;
        Database db = doc.Database;
        if (!Ligados.Add(db))
            return;

        db.ObjectModified += OnObjeto;
        db.ObjectAppended += OnObjeto;
        db.ObjectErased += OnApagado;
        try
        {
            if (LayoutKeyMap.Existe(db) && LayoutKeyMap.Ler(db).Automatico)
                Ativos.Add(db);
        }
        catch (System.Exception)
        {
        }
    }

    private static void OnObjeto(object? sender, ObjectEventArgs e)
    {
        if (_gerando || sender is not Database db || !Ativos.Contains(db))
            return;
        if (e.DBObject is Viewport or Autodesk.AutoCAD.DatabaseServices.Layout)
            Sujos.Add(db);
    }

    private static void OnApagado(object? sender, ObjectErasedEventArgs e)
    {
        if (_gerando || sender is not Database db || !Ativos.Contains(db))
            return;
        if (e.DBObject is Viewport or Autodesk.AutoCAD.DatabaseServices.Layout)
            Sujos.Add(db);
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (Sujos.Count == 0 || _gerando)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !Sujos.Contains(doc.Database) || !doc.Editor.IsQuiescent)
            return;

        Sujos.Remove(doc.Database);
        try
        {
            _gerando = true;
            using (Civil3D2026Plugin.Infrastructure.PluginLog.Medir("Layouts", "Mapa-chave automatico"))
                LayoutKeyMap.Gerar(doc, reconstruirBase: false);
        }
        catch (System.Exception ex)
        {
            Civil3D2026Plugin.Infrastructure.PluginLog.Erro("Layouts", "Mapa-chave: atualizacao automatica falhou", ex);
            doc.Editor.WriteMessage($"\n[Mapa-chave] Atualizacao automatica falhou: {ex.Message}");
        }
        finally
        {
            _gerando = false;
        }
    }
}
