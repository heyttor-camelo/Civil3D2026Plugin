using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using AcColors = Autodesk.AutoCAD.Colors;

namespace Civil3D2026Plugin.Modules.Secao;

/// <summary>
/// Persistencia do modulo: a caixa de corte fica numa polilinha (layer SECAO-CORTE)
/// com XData [CORTE, nome, frente, atras, zmin, zmax, exagero]; as preferencias
/// (conteudo mostrado, padroes) em %APPDATA%\Civil3D2026Plugin\secao.txt.
/// </summary>
internal static class SecaoStore
{
    public const string RegApp = "C3DSECAO";
    public const string TagCut = "CORTE";
    public const string LayerCut = "SECAO-CORTE";
    public const string LayerDraw = "SECAO-DESENHO";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------
    // XData da caixa
    // ------------------------------------------------------------------

    public static bool IsCut(AcadDb.DBObject obj)
    {
        List<string>? v = ReadValues(obj);
        return v is not null && v.Count > 0 && v[0] == TagCut;
    }

    /// <summary>Le a caixa de uma polilinha marcada (geometria + parametros).</summary>
    public static CutBox? ReadCut(AcadDb.Polyline pl)
    {
        List<string>? v = ReadValues(pl);
        if (v is null || v.Count == 0 || v[0] != TagCut)
            return null;

        CutBox box = new() { Line = Vertices(pl) };
        box.Name = v.Count > 1 && v[1].Length > 0 ? v[1] : "Corte";
        box.Frente = v.Count > 2 ? D(v[2], 10) : 10;
        box.Atras = v.Count > 3 ? D(v[3], 5) : 5;
        box.ZMin = v.Count > 4 && v[4].Length > 0 ? D(v[4], 0) : null;
        box.ZMax = v.Count > 5 && v[5].Length > 0 ? D(v[5], 0) : null;
        box.Exagero = v.Count > 6 ? Math.Max(0.1, D(v[6], 1)) : 1;
        return box;
    }

    public static void WriteCut(AcadDb.Entity entity, CutBox box)
    {
        List<AcadDb.TypedValue> list = new()
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp)
        };
        foreach (string s in new[]
                 {
                     TagCut, box.Name, F(box.Frente), F(box.Atras),
                     box.ZMin is double a ? F(a) : "", box.ZMax is double b ? F(b) : "", F(box.Exagero)
                 })
        {
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, s));
        }

        entity.XData = new AcadDb.ResultBuffer(list.ToArray());
    }

    public static List<P2> Vertices(AcadDb.Polyline pl)
    {
        List<P2> pts = new();
        for (int i = 0; i < pl.NumberOfVertices; i++)
        {
            Point2d p = pl.GetPoint2dAt(i);
            pts.Add(new P2(p.X, p.Y));
        }

        return pts;
    }

    private static List<string>? ReadValues(AcadDb.DBObject obj)
    {
        using AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;
        List<string> values = new();
        foreach (AcadDb.TypedValue tv in rb.AsArray())
        {
            if (tv.TypeCode == (short)AcadDb.DxfCode.ExtendedDataAsciiString)
                values.Add(tv.Value as string ?? string.Empty);
        }

        return values.Count == 0 ? null : values;
    }

    private static string F(double d) => d.ToString("0.###", Inv);

    private static double D(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Float, Inv, out double d) ? d : fallback;

    // ------------------------------------------------------------------
    // Tabelas
    // ------------------------------------------------------------------

    public static void EnsureRegApp(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.RegAppTable table = (AcadDb.RegAppTable)tr.GetObject(db.RegAppTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(RegApp))
            return;
        table.UpgradeOpen();
        AcadDb.RegAppTableRecord record = new() { Name = RegApp };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    public static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short color)
    {
        AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(name))
            return;
        table.UpgradeOpen();
        AcadDb.LayerTableRecord layer = new()
        {
            Name = name,
            Color = AcColors.Color.FromColorIndex(AcColors.ColorMethod.ByAci, color)
        };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>Todas as caixas de corte do model space.</summary>
    public static List<(AcadDb.ObjectId Id, CutBox Box)> ReadCuts(AcadDb.Database db)
    {
        List<(AcadDb.ObjectId, CutBox)> result = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (!id.ObjectClass.DxfName.Equals("LWPOLYLINE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Polyline pl && ReadCut(pl) is CutBox box && box.IsValid)
                result.Add((id, box));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Preferencias
    // ------------------------------------------------------------------

    /// <summary>O que entra na vista (por classe) e padroes de caixa nova.</summary>
    public sealed class Prefs
    {
        public bool Superficies = true, Corredores = true, Tubos = true, Solidos = true, Blocos = true, FeatureLines = true, Polilinhas3d = true, Pontos = true;
        public double FrentePadrao = 10, AtrasPadrao = 5, ExageroPadrao = 1;
        public bool Grade = true, Codigos = false;

        /// <summary>
        /// Faixa de vista por classe: "corte" (so o que cruza o plano), "faixa" (o corte mais
        /// o que esta na profundidade da caixa, projetado) ou "tudo" (ignora a profundidade:
        /// projeta tudo o que cai na largura da linha).
        /// </summary>
        public Dictionary<string, string> Faixas = new(StringComparer.OrdinalIgnoreCase);

        public static readonly (string Key, string Title)[] FaixaClasses =
        {
            ("corredor", "Corredores (estacas)"),
            ("tubos", "Tubos e estruturas"),
            ("solidos", "Solidos, malhas e blocos"),
            ("linhas", "Feature lines e polilinhas 3D"),
            ("pontos", "Pontos COGO")
        };

        public string Faixa(string cls) => Faixas.TryGetValue(cls, out string? v) && v is "corte" or "faixa" or "tudo" ? v : "faixa";

        /// <summary>Caixa usada para a classe: a propria ou, em "tudo", sem limite de profundidade.</summary>
        public CutBox BoxFor(string cls, CutBox box) => Faixa(cls) == "tudo" ? box.Unlimited() : box;

        private void Ghost(string cls, bool on) => Faixas[cls] = on ? (Faixa(cls) == "tudo" ? "tudo" : "faixa") : "corte";

        public bool FantasmaCorredor { get => Faixa("corredor") != "corte"; set => Ghost("corredor", value); }
        public bool FantasmaTubos { get => Faixa("tubos") != "corte"; set => Ghost("tubos", value); }
        public bool FantasmaSolidos { get => Faixa("solidos") != "corte"; set => Ghost("solidos", value); }
        public bool FantasmaLinhas { get => Faixa("linhas") != "corte"; set => Ghost("linhas", value); }
        public bool FantasmaPontos { get => Faixa("pontos") != "corte"; set => Ghost("pontos", value); }

        /// <summary>Textos da vista e do desenho (menu Textos).</summary>
        public static readonly (string Key, string Title)[] TextKinds =
        {
            ("nomes", "Nomes de superficies e corredores"),
            ("objetos", "Nomes de tubos, estruturas, solidos e blocos"),
            ("linhas", "Nomes das linhas cortadas (feature lines, polilinhas 3D)"),
            ("pontos", "Pontos COGO (numero e descricao)"),
            ("elevacao", "Elevacao junto dos rotulos"),
            ("fantasmas", "Rotulos tambem dos fantasmas"),
            ("reguas", "Reguas de elevacao e distancia"),
            ("titulo", "Titulo, exagero e escala"),
            ("legenda", "Legenda da comparacao")
        };

        public HashSet<string> Textos = new(StringComparer.OrdinalIgnoreCase) { "nomes", "objetos", "linhas", "pontos", "reguas", "titulo", "legenda" };

        public bool Texto(string key) => Textos.Contains(key);

        /// <summary>Algum rotulo de objeto ligado (compatibilidade com o antigo "Rotulos").</summary>
        public bool Rotulos => Textos.Overlaps(new[] { "nomes", "objetos", "linhas", "pontos" });

        /// <summary>Rotulos movidos com Ctrl+arrastar: chave (secao|item|parte) -> deslocamento (dS, dZ) em metros.</summary>
        public Dictionary<string, (double DS, double DZ)> Deslocamentos = new(StringComparer.Ordinal);
        public HashSet<string> SuperficiesOcultas = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Corredor: "links" (todos), "contorno" (so a envoltoria) ou "codigos" (links com os codigos dados).</summary>
        public string CorredorModo = "links";
        public string CorredorCodigos = "TOP,DATUM";

        /// <summary>Exportacao: escala do desenho (1:N) e altura do texto no papel [mm].</summary>
        public double EscalaDesenho = 100, TextoMm = 2.0;

        /// <summary>Comparacao: incluir os fantasmas de cada secao fixada.</summary>
        public bool CompararFantasmas;

        /// <summary>Estilo escolhido por item (chave do item -> estilo).</summary>
        public Dictionary<string, ItemStyle> Estilos = new(StringComparer.Ordinal);

        public ItemStyle StyleOf(SectionItem it) => Estilos.TryGetValue(it.Key, out ItemStyle st) ? st : ItemStyle.Normal;

        /// <summary>Codigos do filtro do corredor (separados por virgula/ponto e virgula/espaco).</summary>
        public HashSet<string> CodeFilter() =>
            new(CorredorCodigos.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    public static string PrefsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "secao.txt");

    public static Prefs LoadPrefs()
    {
        Prefs p = new();
        try
        {
            if (!File.Exists(PrefsPath))
                return p;
            bool textos = false;
            foreach (string line in File.ReadAllLines(PrefsPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                bool B() => v == "1";
                switch (k.ToLowerInvariant())
                {
                    case "superficies": p.Superficies = B(); break;
                    case "corredores": p.Corredores = B(); break;
                    case "tubos": p.Tubos = B(); break;
                    case "solidos": p.Solidos = B(); break;
                    case "featurelines": p.FeatureLines = B(); break;
                    case "polilinhas3d": p.Polilinhas3d = B(); break;
                    case "pontos": p.Pontos = B(); break;
                    case "blocos": p.Blocos = B(); break;
                    case "fantasmacorredor": if (!p.Faixas.ContainsKey("corredor")) p.FantasmaCorredor = B(); break;
                    case "fantasmatubos": if (!p.Faixas.ContainsKey("tubos")) p.FantasmaTubos = B(); break;
                    case "fantasmalinhas": if (!p.Faixas.ContainsKey("linhas")) p.FantasmaLinhas = B(); break;
                    case "fantasmapontos": if (!p.Faixas.ContainsKey("pontos")) p.FantasmaPontos = B(); break;
                    case "grade": p.Grade = B(); break;
                    case "codigos": p.Codigos = B(); break;
                    case "rotulos":
                        if (!B() && !textos)
                            p.Textos.ExceptWith(new[] { "nomes", "objetos", "linhas", "pontos" });
                        break;
                    case "textos":
                        textos = true;
                        p.Textos = new HashSet<string>(v.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                        break;
                    case "faixa":
                        int fc = v.IndexOf(':');
                        if (fc > 0)
                            p.Faixas[v[..fc]] = v[(fc + 1)..];
                        break;
                    case "rotulo":
                        // rotulo=dS;dZ:chave
                        int rc = v.IndexOf(':');
                        string[] dd = rc > 0 ? v[..rc].Split(';') : Array.Empty<string>();
                        if (dd.Length == 2)
                            p.Deslocamentos[v[(rc + 1)..]] = (D(dd[0], 0), D(dd[1], 0));
                        break;
                    case "frente": p.FrentePadrao = D(v, 10); break;
                    case "atras": p.AtrasPadrao = D(v, 5); break;
                    case "exagero": p.ExageroPadrao = D(v, 1); break;
                    case "corredormodo": p.CorredorModo = v.Length > 0 ? v : "links"; break;
                    case "corredorcodigos": p.CorredorCodigos = v; break;
                    case "escaladesenho": p.EscalaDesenho = Math.Max(1, D(v, 100)); break;
                    case "textomm": p.TextoMm = Math.Max(0.5, D(v, 2)); break;
                    case "compararfantasmas": p.CompararFantasmas = B(); break;
                    case "estilo":
                        int c = v.IndexOf(':');
                        if (c > 0 && int.TryParse(v[..c], out int st) && st is >= 1 and <= 3)
                            p.Estilos[v[(c + 1)..]] = (ItemStyle)st;
                        break;
                    case "superficiesocultas":
                        foreach (string s in v.Split('|', StringSplitOptions.RemoveEmptyEntries))
                            p.SuperficiesOcultas.Add(s);
                        break;
                }
            }
        }
        catch
        {
            // Preferencia e conveniencia.
        }

        return p;
    }

    public static void SavePrefs(Prefs p)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            static string B(bool b) => b ? "1" : "0";
            List<string> lines = new()
            {
                "# Preferencias do visualizador de secao (C3DSECAO)",
                "superficies=" + B(p.Superficies), "corredores=" + B(p.Corredores), "tubos=" + B(p.Tubos),
                "solidos=" + B(p.Solidos), "blocos=" + B(p.Blocos), "featurelines=" + B(p.FeatureLines), "polilinhas3d=" + B(p.Polilinhas3d), "pontos=" + B(p.Pontos),
                "grade=" + B(p.Grade), "codigos=" + B(p.Codigos), "textos=" + string.Join("|", p.Textos.OrderBy(t => t)),
                "frente=" + F(p.FrentePadrao), "atras=" + F(p.AtrasPadrao), "exagero=" + F(p.ExageroPadrao),
                "superficiesocultas=" + string.Join("|", p.SuperficiesOcultas.OrderBy(s => s)),
                "corredormodo=" + p.CorredorModo, "corredorcodigos=" + p.CorredorCodigos,
                "escaladesenho=" + F(p.EscalaDesenho), "textomm=" + F(p.TextoMm), "compararfantasmas=" + B(p.CompararFantasmas)
            };
            lines.AddRange(p.Faixas.Select(kv => "faixa=" + kv.Key + ":" + kv.Value));
            lines.AddRange(p.Estilos.Where(kv => kv.Value != ItemStyle.Normal).Take(500).Select(kv => "estilo=" + (int)kv.Value + ":" + kv.Key));
            lines.AddRange(p.Deslocamentos.Take(500).Select(kv => "rotulo=" + F(kv.Value.DS) + ";" + F(kv.Value.DZ) + ":" + kv.Key));
            File.WriteAllLines(PrefsPath, lines);
        }
        catch
        {
            // Sem permissao: vale so na sessao.
        }
    }
}
