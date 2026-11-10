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

    /// <summary>Layouts que recebem o mapa-chave (nomes). Vazio = todos os que tem viewport sobre a base.</summary>
    public List<string> Layouts { get; } = new();

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
                    case 10: { var p = (Point3d)tv.Value; cfg.CaixaMin = new Point2d(p.X, p.Y); break; }
                    case 11: { var p = (Point3d)tv.Value; cfg.CaixaMax = new Point2d(p.X, p.Y); break; }
                    case 330: cfg.Base.Add((ObjectId)tv.Value); break;
                    case 3: if (Convert.ToString(tv.Value) is { Length: > 0 } ln) cfg.Layouts.Add(ln); break;
                }
            }
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
            new(75, (short)(cfg.Titulo ? 1 : 0))
        };
        if (cfg.CaixaMin is Point2d a && cfg.CaixaMax is Point2d b)
        {
            dados.Add(new TypedValue(10, new Point3d(a.X, a.Y, 0)));
            dados.Add(new TypedValue(11, new Point3d(b.X, b.Y, 0)));
        }
        foreach (ObjectId id in cfg.Base.Where(i => !i.IsNull && !i.IsErased))
            dados.Add(new TypedValue(330, id));
        foreach (string ln in cfg.Layouts.Distinct(StringComparer.OrdinalIgnoreCase))
            dados.Add(new TypedValue(3, ln));
        foreach (KeyValuePair<string, (Point2d Min, Point2d Max)> kv in cfg.Caixas)
        {
            dados.Add(new TypedValue(4, kv.Key));
            dados.Add(new TypedValue(12, new Point3d(kv.Value.Min.X, kv.Value.Min.Y, 0)));
            dados.Add(new TypedValue(13, new Point3d(kv.Value.Max.X, kv.Value.Max.Y, 0)));
        }
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

        public static Encaixe De(Point2d c0, Point2d c1, Extents3d ext)
        {
            double bw = Math.Abs(c1.X - c0.X), bh = Math.Abs(c1.Y - c0.Y);
            double ew = Math.Max(1e-9, ext.MaxPoint.X - ext.MinPoint.X);
            double eh = Math.Max(1e-9, ext.MaxPoint.Y - ext.MinPoint.Y);
            double s = Math.Min(bw / ew, bh / eh) * 0.96;
            return new Encaixe(c0, c1, s,
                new Point2d((c0.X + c1.X) / 2, (c0.Y + c1.Y) / 2),
                new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2));
        }
    }

    /// <summary>
    /// Refaz o mapa-chave nos layouts escolhidos na configuracao (lista vazia = todos os que tem
    /// folha sobre a base). Layouts fora da lista perdem o mapa. reconstruirBase = refaz o bloco a
    /// partir dos objetos do Model. As previas nao sao tocadas.
    /// </summary>
    public static OpResult Gerar(Document doc, bool reconstruirBase)
    {
        Database db = doc.Database;
        KeyMapConfig cfg = Ler(db);
        if (cfg.Base.Count(i => !i.IsNull && !i.IsErased) == 0)
            return new OpResult(0, 0, "Escolha primeiro os objetos da base no Model (Escolher base).");
        if (!cfg.TemCaixa)
            return new OpResult(0, 0, "Escolha primeiro a area do mapa no layout (Escolher area).");

        using DocumentLock _ = doc.LockDocument();
        int layouts = 0, regioesTotal = 0, foraDaLista = 0, movidos = 0;
        var escolhidos = new HashSet<string>(cfg.Layouts, StringComparer.OrdinalIgnoreCase);
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            (ObjectId blocoId, Extents3d? ext, DBDictionary dic) = Preparar(tr, db, cfg, reconstruirBase);
            if (ext is null)
                return new OpResult(0, 0, "A base do mapa-chave ficou vazia (os objetos escolhidos nao puderam ser copiados).");

            List<(string Nome, int Ordem, ObjectId Btr, List<Point2d[]> Regioes)> folhas = Folhas(tr, db, ext.Value);
            var encGeral = Encaixe.De(cfg.CaixaMin!.Value, cfg.CaixaMax!.Value, ext.Value);
            foreach (var folha in folhas)
            {
                var btr = (BlockTableRecord)tr.GetObject(folha.Btr, OpenMode.ForWrite);
                // Caixa deste layout: a propria (movido/escalado nele) ou a geral. Se o usuario moveu ou
                // escalou a base do mapa com MOVE/SCALE, a caixa acompanha e fica guardada para o layout.
                Encaixe enc = cfg.Caixas.TryGetValue(folha.Nome, out (Point2d Min, Point2d Max) cx) ? Encaixe.De(cx.Min, cx.Max, ext.Value) : encGeral;
                if (DetectarMovimento(tr, btr, blocoId, enc, ext.Value) is (Point2d Min, Point2d Max) nova)
                {
                    cfg.Caixas[folha.Nome] = nova;
                    enc = Encaixe.De(nova.Min, nova.Max, ext.Value);
                    movidos++;
                }
                int apagados = Limpar(tr, btr, incluirPrevias: false);
                if (folha.Regioes.Count == 0)
                    continue;
                if (escolhidos.Count > 0 && !escolhidos.Contains(folha.Nome))
                {
                    if (apagados > 0)
                        foraDaLista++;
                    continue;
                }
                regioesTotal += Desenhar(tr, dic, btr, folha, folhas, cfg, blocoId, enc, previa: false);
                layouts++;
            }
            tr.Commit();
        }
        if (movidos > 0)
            Gravar(doc, cfg);

        AvisarMudanca();
        string extra = (movidos > 0 ? $"; {movidos} com o mapa movido a mao (posicao guardada para a prancha)" : "") + (escolhidos.Count > 0 ? $" (so nas {escolhidos.Count} prancha(s) escolhidas" + (foraDaLista > 0 ? $"; apagado de {foraDaLista} fora da lista)" : ")") : "");
        return new OpResult(layouts, 0,
            layouts > 0
                ? $"Mapa-chave em {layouts} layout(s) ({regioesTotal} area(s) de folha){extra}."
                : escolhidos.Count > 0
                    ? "Nenhuma das pranchas escolhidas mostra a base: confira a lista de pranchas do mapa-chave."
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
            var enc = Encaixe.De(new Point2d(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y)), new Point2d(Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y)), ext.Value);
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
    /// Grava quais layouts recebem o mapa-chave e refaz (lista vazia = todos). Os que sairam da
    /// lista perdem o mapa.
    /// </summary>
    public static OpResult DefinirLayouts(Document doc, IEnumerable<string> layouts)
    {
        KeyMapConfig cfg = Ler(doc.Database);
        cfg.Layouts.Clear();
        cfg.Layouts.AddRange(layouts.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase));
        Gravar(doc, cfg);
        if (!cfg.TemCaixa || cfg.Base.Count == 0)
            return new OpResult(0, 0, (cfg.Layouts.Count == 0 ? "Mapa-chave em todas as pranchas" : $"Mapa-chave em {cfg.Layouts.Count} prancha(s)") +
                                      " (gravado). Falta escolher a base no Model e a area no layout para gerar.") { Mudou = false };
        return Gerar(doc, false);
    }

    /// <summary>Tira um layout da lista do mapa-chave e apaga o mapa dele (as previas ficam).</summary>
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
            var todos = new List<string>();
            foreach (DBDictionaryEntry e in dic)
            {
                var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (lay.ModelType)
                    continue;
                todos.Add(lay.LayoutName);
                if (!tirar.Contains(lay.LayoutName))
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                n += Limpar(tr, btr, incluirPrevias: false);
            }
            // Lista vazia significa "todos": vira a lista de todos menos os tirados.
            if (cfg.Layouts.Count == 0)
                cfg.Layouts.AddRange(todos);
            cfg.Layouts.RemoveAll(tirar.Contains);
            tr.Commit();
        }
        Gravar(doc, cfg);
        AvisarMudanca();
        return new OpResult(n, 0, $"Mapa-chave tirado de {tirar.Count} layout(s) ({n} objeto(s)); eles ficam fora das proximas atualizacoes.");
    }

    /// <summary>
    /// A base do mapa (bloco, nao previa) saiu do lugar/tamanho que a caixa manda? Entao o usuario
    /// deu MOVE/SCALE nela: devolve a caixa equivalente (mesma proporcao de encaixe), senao null.
    /// </summary>
    private static (Point2d Min, Point2d Max)? DetectarMovimento(Transaction tr, BlockTableRecord btr, ObjectId blocoId, Encaixe enc, Extents3d ext)
    {
        foreach (ObjectId id in btr)
        {
            if (id.ObjectClass.DxfName != "INSERT" || tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br.BlockTableRecord != blocoId)
                continue;
            using ResultBuffer? rb = br.GetXDataForApplication(App);
            if (rb is null || EhPrevia(rb))
                continue;
            double sNovo = Math.Abs(br.ScaleFactors.X);
            var esperado = new Point2d(enc.CentroCaixa.X - enc.CentroBase.X * enc.S, enc.CentroCaixa.Y - enc.CentroBase.Y * enc.S);
            var pos = new Point2d(br.Position.X, br.Position.Y);
            if (sNovo <= 0 || (pos.GetDistanceTo(esperado) <= 1e-4 && Math.Abs(sNovo - enc.S) <= 1e-9 * Math.Max(1, enc.S)))
                return null;
            double k = sNovo / enc.S;
            var centro = new Point2d(pos.X + enc.CentroBase.X * sNovo, pos.Y + enc.CentroBase.Y * sNovo);
            double w = enc.Bw * k / 2, h = enc.Bh * k / 2;
            return (new Point2d(centro.X - w, centro.Y - h), new Point2d(centro.X + w, centro.Y + h));
        }
        return null;
    }

    /// <summary>Caixa propria de um layout (mover/redimensionar o mapa so nele) e refaz.</summary>
    public static OpResult MoverEm(Document doc, string layout, Point2d c0, Point2d c1)
    {
        KeyMapConfig cfg = Ler(doc.Database);
        cfg.Caixas[layout] = (new Point2d(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y)), new Point2d(Math.Max(c0.X, c1.X), Math.Max(c0.Y, c1.Y)));
        if (!cfg.TemCaixa)
        {
            cfg.CaixaMin = cfg.Caixas[layout].Min;
            cfg.CaixaMax = cfg.Caixas[layout].Max;
        }
        Gravar(doc, cfg);
        OpResult r = Gerar(doc, false);
        return r with { Mensagem = $"Mapa-chave de {layout} movido (posicao guardada so para essa prancha). " + r.Mensagem };
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
        void Add(Entity e) => Adicionar(tr, btr, e, previa);
        double s = enc.S, bw = enc.Bw, bh = enc.Bh;
        Point2d c0 = enc.C0, c1 = enc.C1, centroCaixa = enc.CentroCaixa, centroBase = enc.CentroBase;
        Point2d Papel(Point2d p) => enc.Papel(p);

        // Base.
        Add(new BlockReference(new Point3d(centroCaixa.X - centroBase.X * s, centroCaixa.Y - centroBase.Y * s, 0), blocoId)
        {
            ScaleFactors = new Scale3d(s),
            Layer = LayerBase
        });

        // Contorno e rotulo de todas as folhas (rotulo como na LISP: MTEXT acima da aresta
        // superior da viewport, centralizado e girado com a folha).
        int totalFolhas = cfg.TotalManual > 0 ? cfg.TotalManual : folhas.Count(f => f.Regioes.Count > 0);
        foreach (var outra in folhas.Where(f => f.Regioes.Count > 0))
        {
            foreach (Point2d[] reg in outra.Regioes)
            {
                var pl = new Polyline { Layer = LayerFolhas, Closed = true };
                for (int i = 0; i < reg.Length; i++)
                    pl.AddVertexAt(i, Papel(reg[i]), 0, 0, 0);
                Add(pl);
            }

            Point2d[] maior = outra.Regioes.OrderByDescending(Area).First().Select(Papel).ToArray();
            (Point2d pos, double ang, double alt, double larg) = GeometriaRotulo(maior, cfg.Rotulo == 2);
            alt *= Math.Max(0.1, cfg.FatorTexto);
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
            });
        }

        // Hachura da propria folha (so se ela mostra a base).
        if (folha.Regioes.Count > 0)
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

        if (cfg.Titulo)
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
            Add(titulo);
            double y = Math.Max(c0.Y, c1.Y) - tituloAlt * 1.75;
            Add(new Line(new Point3d(centroCaixa.X - titulo.Width / 2, y, 0), new Point3d(centroCaixa.X + titulo.Width / 2, y, 0)) { Layer = LayerTexto });
        }
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
                if (tr.GetObject(id, OpenMode.ForRead) is Entity en && en.GetXDataForApplication(App) is ResultBuffer rb)
                {
                    rb.Dispose();
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

    private static void Adicionar(Transaction tr, BlockTableRecord btr, Entity ent, bool previa = false)
    {
        btr.AppendEntity(ent);
        tr.AddNewlyCreatedDBObject(ent, true);
        ent.XData = previa
            ? new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App), new TypedValue((int)DxfCode.ExtendedDataAsciiString, MarcaPrevia))
            : new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App));
    }

    private static bool EhPrevia(ResultBuffer rb) =>
        rb.AsArray().Skip(1).Any(v => v.Value is string t && t == MarcaPrevia);

    /// <summary>Apaga o que o plugin criou no layout (previas so se pedido). Retorna quantos objetos.</summary>
    private static int Limpar(Transaction tr, BlockTableRecord btr, bool incluirPrevias = false, bool soPrevias = false)
    {
        int n = 0;
        foreach (ObjectId id in btr)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                continue;
            using ResultBuffer? rb = e.GetXDataForApplication(App);
            if (rb is null)
                continue;
            bool previa = EhPrevia(rb);
            if ((previa && !incluirPrevias) || (!previa && soPrevias))
                continue;
            tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
            n++;
        }
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
