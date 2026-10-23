using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Placa de sinalizacao vertical (projeto de sinalizacao turistica, placas do SinC)
/// amarrada a um eixo da rede: estaca, lado e eixo sao calculados pela rede viaria.
/// </summary>
internal sealed class PlacaItem
{
    // --- dados do projeto ---
    public string Id { get; set; } = "";
    /// <summary>Codigo/arquivo da placa no SinC (T-001 -> T-001_SV.dwg).</summary>
    public string Codigo { get; set; } = "";
    public string Trecho { get; set; } = "";
    public string Rota { get; set; } = "";
    public string TrechoDescricao { get; set; } = "";
    public string Sentido { get; set; } = "";
    public int Item { get; set; }
    public int? PaginaPdf { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public List<string> Mensagens { get; set; } = new();
    public string DimensaoProjeto { get; set; } = "";
    public string AreaProjeto { get; set; } = "";
    public string Suporte { get; set; } = "";
    public string AlturaSuporte { get; set; } = "";
    /// <summary>Dimensoes da placa no SinC (m); o bloco T-xxx_SV esta em escala 5.</summary>
    public double LarguraSinC { get; set; }
    public double AlturaSinC { get; set; }

    // --- vinculo com a rede ---
    public string AxisKey { get; set; } = "";
    public string AxisName { get; set; } = "";
    /// <summary>Estaca (m ao longo do eixo).</summary>
    public double S { get; set; }
    /// <summary>Afastamento do eixo (m, sempre positivo).</summary>
    public double Offset { get; set; }
    /// <summary>1 = esquerda, -1 = direita, 0 = sem eixo.</summary>
    public int Lado { get; set; }
    /// <summary>Rotacao do bloco (graus), como o SinC: perpendicular a via.</summary>
    public double Rotacao { get; set; }

    /// <summary>Escala do bloco da placa no desenho (lida do bloco; 1 = padrao do SinC).</summary>
    public double Escala { get; set; } = 1.0;

    /// <summary>Tipo de suporte escolhido pela area (Coluna Simples, Coluna Dupla, Semi-Portico, Portico) ou "".</summary>
    public string SuporteTipo { get; set; } = "";

    /// <summary>Ponta da linha de chamada (= origem da placa) em relacao a saida, no sistema local da placa e sem escala.</summary>
    public static readonly V2 ChamadaPadrao = new(5.656871, -6.156838);

    /// <summary>Ponta da chamada desta placa (padrao do SinC ou esticada com Arrumar/Esticar).</summary>
    public V2 Chamada { get; set; } = ChamadaPadrao;

    public bool ChamadaEsticada => V2.Dist(Chamada, ChamadaPadrao) > 1e-3;

    /// <summary>Area da placa no SinC (m2).</summary>
    public double AreaSinC => LarguraSinC * AlturaSinC;

    /// <summary>Area do projeto (m2) quando informada, senao a do SinC.</summary>
    public double AreaProjetoM2
    {
        get
        {
            string t = (AreaProjeto ?? "").Trim().Replace(" m²", "").Replace("m2", "").Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
                return v;
            if (double.TryParse(t.Replace('.', '_').Replace(',', '.').Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                return v;
            return 0.0;
        }
    }

    /// <summary>Area usada na regra do suporte: a do projeto, se houver; senao a do SinC.</summary>
    public double AreaRegra => AreaProjetoM2 > 0 ? AreaProjetoM2 : AreaSinC;

    public bool Vinculada => Lado != 0 && AxisKey.Length > 0;
    public V2 Ponto => new(E, N);

    public string LadoTexto => Lado switch { 1 => "LE", -1 => "LD", _ => "" };
    public string EstacaTexto => Vinculada ? ViarioPlacas.Estaca(S) : "";
    public string EstacaKm => Vinculada ? $"{ViarioPlacas.Estaca(S)} (km {(S / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)})" : "";

    /// <summary>Area em dm2, como o SinC grava no atributo Area (5,1 x 1,8 m -> 918).</summary>
    public int AreaDm2 => (int)Math.Round(LarguraSinC * AlturaSinC * 100.0);

    /// <summary>Atributo Dimensao do SinC: "L = 5,1x1,8m".</summary>
    public string DimensaoSinC =>
        $"L = {LarguraSinC.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',')}x{AlturaSinC.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',')}m";

    public string MensagemResumo => string.Join(" | ", Mensagens);

    /// <summary>Separador dos campos no XData (nao aparece em nomes nem mensagens).</summary>
    private const string Sep = "~|~";

    /// <summary>Texto curto para o XData (sem mensagens, que ficam so no catalogo).</summary>
    public string Serialize() => string.Join(Sep, new[]
    {
        Id, Codigo, Trecho, Rota, Sentido, Item.ToString(CultureInfo.InvariantCulture),
        E.ToString("0.####", CultureInfo.InvariantCulture), N.ToString("0.####", CultureInfo.InvariantCulture),
        LarguraSinC.ToString("0.###", CultureInfo.InvariantCulture), AlturaSinC.ToString("0.###", CultureInfo.InvariantCulture),
        AxisKey, S.ToString("0.###", CultureInfo.InvariantCulture), Offset.ToString("0.###", CultureInfo.InvariantCulture),
        Lado.ToString(CultureInfo.InvariantCulture), Rotacao.ToString("0.###", CultureInfo.InvariantCulture),
        Suporte, DimensaoProjeto, AreaProjeto, TrechoDescricao, MensagemResumo, SuporteTipo, AlturaSuporte,
        ChamadaEsticada ? Chamada.X.ToString("0.####", CultureInfo.InvariantCulture) + ";" + Chamada.Y.ToString("0.####", CultureInfo.InvariantCulture) : ""
    });

    public static PlacaItem? Deserialize(string text)
    {
        string[] f = text.Split(Sep);
        if (f.Length < 15)
            return null;
        PlacaItem p = new()
        {
            Id = f[0], Codigo = f[1], Trecho = f[2], Rota = f[3], Sentido = f[4],
            Item = Int(f[5]), E = Dbl(f[6]), N = Dbl(f[7]), LarguraSinC = Dbl(f[8]), AlturaSinC = Dbl(f[9]),
            AxisKey = f[10], S = Dbl(f[11]), Offset = Dbl(f[12]), Lado = Int(f[13]), Rotacao = Dbl(f[14])
        };
        if (f.Length > 15) p.Suporte = f[15];
        if (f.Length > 16) p.DimensaoProjeto = f[16];
        if (f.Length > 17) p.AreaProjeto = f[17];
        if (f.Length > 18) p.TrechoDescricao = f[18];
        if (f.Length > 19 && f[19].Length > 0)
            p.Mensagens = f[19].Split(" | ").ToList();
        if (f.Length > 20) p.SuporteTipo = f[20];
        if (f.Length > 21) p.AlturaSuporte = f[21];
        if (f.Length > 22 && f[22].Split(';') is { Length: 2 } ch)
            p.Chamada = new V2(Dbl(ch[0]), Dbl(ch[1]));
        return p;
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
    private static double Dbl(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
}

/// <summary>Catalogo de placas lido do arquivo do projeto.</summary>
internal sealed class PlacaCatalog
{
    public string Fonte { get; set; } = "";
    public string Crs { get; set; } = "";
    public string PastaSinC { get; set; } = "";
    public List<PlacaItem> Placas { get; } = new();
}

/// <summary>Leitura do catalogo e amarracao das placas aos eixos (logica pura, sem AutoCAD).</summary>
internal static class ViarioPlacas
{
    /// <summary>Estaca de 20 m: "12+5.30" (so "12" quando cai na estaca inteira).</summary>
    public static string Estaca(double s)
    {
        int e = (int)Math.Floor((s / 20.0) + 1e-9);
        double r = s - (20.0 * e);
        if (r < 0.005)
            return e.ToString(CultureInfo.InvariantCulture);
        return $"{e}+{r.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    // ------------------------------------------------------------------
    // Catalogo (JSON gerado da planilha do projeto: placas_chapada.json)
    // ------------------------------------------------------------------

    public static PlacaCatalog Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".csv" ? LoadCsv(path) : LoadJson(path);
    }

    private static PlacaCatalog LoadJson(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;
        PlacaCatalog cat = new()
        {
            Fonte = Str(root, "fonte"),
            Crs = Str(root, "crs"),
            PastaSinC = Str(root, "pastaSinC")
        };

        if (!root.TryGetProperty("placas", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("O arquivo nao tem a lista \"placas\".");

        foreach (JsonElement j in list.EnumerateArray())
        {
            PlacaItem p = new()
            {
                Id = Str(j, "Id"),
                Trecho = Str(j, "Trecho"),
                Rota = Str(j, "Rota"),
                TrechoDescricao = Str(j, "TrechoDescricao"),
                Sentido = Str(j, "Sentido"),
                Item = (int)Num(j, "Item"),
                E = Num(j, "E"),
                N = Num(j, "N"),
                DimensaoProjeto = Str(j, "DimensoesProjeto"),
                AreaProjeto = Str(j, "AreaProjetoM2"),
                Suporte = Str(j, "Suporte"),
                AlturaSuporte = Str(j, "AlturaSuporte")
            };
            if (j.TryGetProperty("PaginaPdf", out JsonElement pg) && pg.ValueKind == JsonValueKind.Number)
                p.PaginaPdf = pg.GetInt32();

            if (j.TryGetProperty("Linhas", out JsonElement linhas) && linhas.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement l in linhas.EnumerateArray())
                {
                    string m = Str(l, "Mensagem");
                    if (m.Length > 0)
                        p.Mensagens.Add(m);
                }
            }

            if (j.TryGetProperty("SinC", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
            {
                p.Codigo = Str(s, "Arquivo");
                p.LarguraSinC = Num(s, "LarguraM");
                p.AlturaSinC = Num(s, "AlturaM");
            }

            if (p.Codigo.Length == 0)
                p.Codigo = p.Id;
            cat.Placas.Add(p);
        }

        return cat;
    }

    /// <summary>CSV ";" com cabecalho (placas_projeto_x_sinc.csv): id_projeto, trecho, rota, sentido_item, sinc, dim_sinc, E, N...</summary>
    private static PlacaCatalog LoadCsv(string path)
    {
        PlacaCatalog cat = new() { Fonte = Path.GetFileName(path) };
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2)
            return cat;

        string[] head = lines[0].TrimStart('﻿').Split(';');
        int Col(string name) => Array.FindIndex(head, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
        int iId = Col("id_projeto"), iTr = Col("trecho"), iRota = Col("rota"), iSi = Col("sentido_item"), iSinc = Col("sinc"),
            iDimS = Col("dim_sinc"), iDimP = Col("dim_projeto"), iArea = Col("area_m2"), iE = Col("E"), iN = Col("N"),
            iMsg = Col("mensagens_projeto"), iPag = Col("pag_pdf");
        if (iId < 0 || iE < 0 || iN < 0)
            throw new InvalidDataException("CSV sem as colunas id_projeto, E e N.");

        foreach (string line in lines.Skip(1))
        {
            if (line.Trim().Length == 0)
                continue;
            string[] c = line.Split(';');
            string Get(int i) => i >= 0 && i < c.Length ? c[i].Trim() : "";
            PlacaItem p = new()
            {
                Id = Get(iId), Trecho = Get(iTr), Rota = Get(iRota), Codigo = Get(iSinc),
                DimensaoProjeto = Get(iDimP), AreaProjeto = Get(iArea),
                E = PtBr(Get(iE)), N = PtBr(Get(iN))
            };
            string si = Get(iSi);
            int dash = si.IndexOf('-');
            if (dash > 0)
            {
                p.Sentido = si[..dash];
                p.Item = int.TryParse(si[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int it) ? it : 0;
            }

            if (int.TryParse(Get(iPag), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pg))
                p.PaginaPdf = pg;
            string msg = Get(iMsg);
            if (msg.Length > 0)
                p.Mensagens = msg.Split(" | ").ToList();
            string dim = Get(iDimS).ToLowerInvariant();
            int x = dim.IndexOf('x');
            if (x > 0)
            {
                p.LarguraSinC = PtBr(dim[..x]);
                p.AlturaSinC = PtBr(dim[(x + 1)..]);
            }

            if (p.Codigo.Length == 0)
                p.Codigo = p.Id;
            cat.Placas.Add(p);
        }

        return cat;
    }

    private static double PtBr(string s)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        return double.TryParse(s.Replace('.', '_').Replace(',', '.').Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText()
        } : "";

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v))
            return 0.0;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return v.ValueKind == JsonValueKind.String ? PtBr(v.GetString() ?? "") : 0.0;
    }

    // ------------------------------------------------------------------
    // Amarracao ao eixo
    // ------------------------------------------------------------------

    /// <summary>
    /// Eixo mais proximo do ponto da placa (ate <paramref name="maxDist"/> m): estaca, lado e
    /// rotacao do bloco (perpendicular a via, como o SinC). Sem eixo perto, a placa fica
    /// desvinculada (Lado = 0) com a rotacao que ja tinha.
    /// </summary>
    public static bool Link(PlacaItem p, IReadOnlyList<AxisInput> axes, IReadOnlyDictionary<string, string> names, double maxDist)
    {
        V2 pt = p.Ponto;
        AxisInput? best = null;
        double bestS = 0, bestDist = double.MaxValue;
        foreach (AxisInput a in axes)
        {
            if (a.Path.IsEmpty)
                continue;
            Box2 box = a.Path.Bounds().Inflate(maxDist);
            if (pt.X < box.MinX || pt.X > box.MaxX || pt.Y < box.MinY || pt.Y > box.MaxY)
                continue;
            (double s, _, double dist) = a.Path.Project(pt);
            if (dist < bestDist)
            {
                best = a;
                bestS = s;
                bestDist = dist;
            }
        }

        if (best is null || bestDist > maxDist)
        {
            p.AxisKey = "";
            p.AxisName = "";
            p.Lado = 0;
            p.Offset = bestDist < double.MaxValue ? bestDist : 0.0;
            return false;
        }

        V2 tangent = best.Path.TangentAt(bestS).Unit();
        V2 foot = best.Path.PointAt(bestS);
        double cross = V2.Cross(tangent, pt - foot);
        p.AxisKey = best.Key;
        p.AxisName = names.TryGetValue(best.Key, out string? n) ? n : "";
        p.S = bestS;
        p.Offset = bestDist;
        p.Lado = cross >= 0 ? 1 : -1;
        p.Rotacao = Rotation(tangent, p.Lado);
        return true;
    }

    /// <summary>
    /// Rotacao do bloco (graus): o SinC alinha a placa perpendicular a via; com o eixo X do
    /// bloco apontando para o lado da placa, a linha de chamada sai do ponto para fora da pista.
    /// </summary>
    public static double Rotation(V2 tangent, int lado)
    {
        double heading = tangent.Angle * 180.0 / Math.PI;
        double rot = lado > 0 ? heading + 90.0 : heading - 90.0;
        rot %= 360.0;
        return rot < 0 ? rot + 360.0 : rot;
    }

    /// <summary>Rotacao com a placa de frente para quem vem no sentido do eixo (opcional, sem uso por ora).</summary>
    public static V2 Rotate(V2 v, double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        double c = Math.Cos(a), s = Math.Sin(a);
        return new V2((v.X * c) - (v.Y * s), (v.X * s) + (v.Y * c));
    }

    /// <summary>
    /// Nome sugerido para cada eixo a partir das placas amarradas a ele: rota e trecho do
    /// projeto (ex.: "MT-020/251 - Entr. MT-351 (Lago do Manso) ao Trevo da Chapada").
    /// </summary>
    public static Dictionary<string, string> SuggestAxisNames(IEnumerable<PlacaItem> placas)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (IGrouping<string, PlacaItem> g in placas.Where(p => p.Vinculada).GroupBy(p => p.AxisKey))
        {
            List<string> rotas = g.GroupBy(p => p.Rota).OrderByDescending(x => x.Count()).Select(x => x.Key).Where(r => r.Length > 0).ToList();
            List<string> trechos = g.GroupBy(p => p.TrechoDescricao).OrderByDescending(x => x.Count()).Select(x => x.Key).Where(t => t.Length > 0).ToList();
            string name = rotas.Count == 0 ? "" : string.Join(" / ", rotas.Take(2));
            if (trechos.Count > 0)
                name = name.Length == 0 ? trechos[0] : $"{name} - {trechos[0]}";
            if (name.Length > 250)
                name = name[..250];
            if (name.Length > 0)
                names[g.Key] = name;
        }

        return names;
    }

    // ------------------------------------------------------------------
    // Pasta do SinC (arquivos .pla: zip com INFO.json, COD.placa, CAD_2D.dwg...)
    // ------------------------------------------------------------------

    /// <summary>
    /// Le todos os .pla de uma pasta (os pacotes exportados pelo SinC): codigo (nome do arquivo),
    /// dimensoes, textos das mensagens e, quando a descricao traz "Local: E (E), N (N)", as
    /// coordenadas. Placas sem coordenada ficam com E = N = 0 (completar com <see cref="Merge"/>).
    /// </summary>
    public static PlacaCatalog LoadSinCFolder(string folder, List<string> avisos)
    {
        PlacaCatalog cat = new() { Fonte = folder, PastaSinC = folder };
        foreach (string file in Directory.EnumerateFiles(folder, "*.pla", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string code = Path.GetFileNameWithoutExtension(file);
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(file);
                string info = ReadEntry(zip, "INFO.json");
                string cod = ReadEntry(zip, "COD.placa");
                PlacaItem p = new() { Id = code, Codigo = code };

                if (info.Length > 0)
                {
                    using JsonDocument doc = JsonDocument.Parse(info);
                    JsonElement root = doc.RootElement;
                    string dim = Str(root, "Dimension").ToLowerInvariant();
                    int x = dim.IndexOf('x');
                    if (x > 0)
                    {
                        p.LarguraSinC = PtBr(dim[..x]);
                        p.AlturaSinC = PtBr(dim[(x + 1)..]);
                    }

                    string desc = Str(root, "Description");
                    Match m = Regex.Match(desc, @"(\d{5,7}[.,]\d+)\s*\(E\)\s*,?\s*(\d{6,8}[.,]\d+)\s*\(N\)");
                    if (m.Success)
                    {
                        p.E = PtBr(m.Groups[1].Value);
                        p.N = PtBr(m.Groups[2].Value);
                    }

                    Match t = Regex.Match(desc, @"Trecho:\s*(.+?)\s*(/|$)");
                    if (t.Success)
                        p.TrechoDescricao = t.Groups[1].Value.Trim();
                }

                if (cod.Length > 0)
                {
                    foreach (Match m in Regex.Matches(cod, "<Texto [^>]*?Texto=\"([^\"]*)\""))
                    {
                        string text = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                        string first = text.Split('\n')[0].Trim();
                        if (first.Length > 0)
                            p.Mensagens.Add(first);
                    }

                    Match nome = Regex.Match(cod, "<Placa [^>]*?Nome=\"([^\"]*)\"");
                    if (nome.Success && nome.Groups[1].Value.Trim().Length > 0)
                        p.Codigo = nome.Groups[1].Value.Trim();
                }

                cat.Placas.Add(p);
            }
            catch (System.Exception ex)
            {
                avisos.Add($"{code}: {ex.Message}");
            }
        }

        return cat;
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return "";
        using StreamReader reader = new(entry.Open(), System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Completa as placas lidas da pasta do SinC com os dados do catalogo do projeto (mesmo
    /// codigo SinC): coordenadas (quando faltam ou sao zero), id, trecho, rota, sentido, item,
    /// pagina, suporte, dimensao/area do projeto. Retorna quantas foram completadas.
    /// </summary>
    public static int Merge(PlacaCatalog sinc, PlacaCatalog projeto)
    {
        ILookup<string, PlacaItem> byCode = projeto.Placas.ToLookup(p => p.Codigo, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        List<PlacaItem> extra = new();
        foreach (PlacaItem p in sinc.Placas.ToList())
        {
            List<PlacaItem> matches = byCode[p.Codigo].ToList();
            if (matches.Count == 0)
                continue;

            // Um arquivo do SinC pode servir a mais de uma placa do projeto (mesmo texto):
            // a primeira fica neste item, as demais viram copias.
            for (int i = 0; i < matches.Count; i++)
            {
                PlacaItem src = matches[i];
                PlacaItem dst = i == 0 ? p : Clone(p);
                if ((Math.Abs(dst.E) < 1.0 && Math.Abs(dst.N) < 1.0) || i > 0)
                {
                    dst.E = src.E;
                    dst.N = src.N;
                }

                dst.Id = src.Id;
                dst.Trecho = src.Trecho;
                dst.Rota = src.Rota;
                dst.Sentido = src.Sentido;
                dst.Item = src.Item;
                dst.PaginaPdf = src.PaginaPdf;
                if (dst.TrechoDescricao.Length == 0)
                    dst.TrechoDescricao = src.TrechoDescricao;
                dst.Suporte = src.Suporte;
                dst.AlturaSuporte = src.AlturaSuporte;
                dst.DimensaoProjeto = src.DimensaoProjeto;
                dst.AreaProjeto = src.AreaProjeto;
                if (dst.Mensagens.Count == 0)
                    dst.Mensagens = new List<string>(src.Mensagens);
                if (i > 0)
                    extra.Add(dst);
                n++;
            }
        }

        sinc.Placas.AddRange(extra);
        if (projeto.PastaSinC.Length > 0 && sinc.PastaSinC.Length == 0)
            sinc.PastaSinC = projeto.PastaSinC;
        return n;
    }

    private static PlacaItem Clone(PlacaItem p) => new()
    {
        Id = p.Id, Codigo = p.Codigo, Trecho = p.Trecho, Rota = p.Rota, TrechoDescricao = p.TrechoDescricao, Sentido = p.Sentido,
        Item = p.Item, PaginaPdf = p.PaginaPdf, E = p.E, N = p.N, Mensagens = new List<string>(p.Mensagens),
        DimensaoProjeto = p.DimensaoProjeto, AreaProjeto = p.AreaProjeto, Suporte = p.Suporte, AlturaSuporte = p.AlturaSuporte,
        LarguraSinC = p.LarguraSinC, AlturaSinC = p.AlturaSinC
    };

    // ------------------------------------------------------------------
    // Suporte pela geometria da placa
    // ------------------------------------------------------------------

    public const string SuporteSimples = "Coluna Simples";
    public const string SuporteDuplo = "Coluna Dupla";
    public const string SuporteTriplo = "Coluna Tripla";
    public const string SuporteBraco = "Braço Projetado";
    public const string SuporteSemiPortico = "Semi Pórtico Simples";
    public const string SuportePortico = "Pórtico";
    public static readonly string[] SuporteTipos = { SuporteSimples, SuporteDuplo, SuporteTriplo, SuporteBraco, SuporteSemiPortico, SuportePortico };

    /// <summary>
    /// Tipo de suporte pela area da placa: ate 2,00 m2 coluna simples; de 2,01 a 6,00 m2 coluna
    /// dupla; acima de 6,00 m2 semi-portico, ou portico quando a placa e mais larga que
    /// <paramref name="larguraPortico"/> (m). Limites em m2.
    /// </summary>
    public static string SupportType(double area, double largura, double limiteSimples = 2.0, double limiteDuplo = 6.0, double larguraPortico = 6.0)
    {
        if (area <= limiteSimples + 1e-9)
            return SuporteSimples;
        if (area <= limiteDuplo + 1e-9)
            return SuporteDuplo;
        return largura > larguraPortico ? SuportePortico : SuporteSemiPortico;
    }
}
