using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcColors = Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Persistencia do modulo Viario no DWG.
///
/// - Eixos: XData "C3DVIARIO" = (EIXO, id da secao, raio do eixo ou vazio).
///   Copiar um eixo copia o XData: a copia ja entra na rede.
/// - Linhas geradas: XData "C3DVIARIO" = (GERADO, elemento). Refeitas a cada
///   atualizacao; edicoes manuais nelas sao substituidas (use Desvincular).
/// - Secoes, raios por esquina, linhas soltas e configuracao: dicionario
///   C3D_VIARIO_V1 no NOD (entram no UNDO).
/// </summary>
internal static class ViarioStore
{
    public const string RegApp = "C3DVIARIO";
    public const string TagAxis = "EIXO";
    public const string TagGenerated = "GERADO";

    public const string LayerAxis = "VIARIO-EIXO";

    private const string RootName = "C3D_VIARIO_V1";
    private const string SectionPrefix = "SECAO_";
    private const string LegacyProfilePrefix = "PERFIL_";
    private const string SettingsKey = "CONFIG";
    private const string RadiiKey = "RAIOS";
    private const string ReleasedKey = "SOLTAS";
    private const string RemovedIslandsKey = "ILHASOFF";
    private const string RemovedIslandsSignature = "VILHAOFF1";
    private const string IslandRadiiKey = "ILHARAIO";
    private const string IslandRadiiSignature = "VILHARAIO1";
    private const string SlipKey = "LIVRES";
    private const string SlipSignature = "VLIVRE1";
    private const string SectionSignature = "VSEC1";
    private const string LegacyProfileSignature = "VPERF1";
    private const string SettingsSignature = "VCFG1";
    private const string RadiiSignature = "VRAIO1";
    private const string ReleasedSignature = "VSOLTA1";

    /// <summary>Layer e cor de cada elemento gerado.</summary>
    private static readonly Dictionary<EdgeLevel, (string Layer, short Color, string Label)> Elements = new()
    {
        [EdgeLevel.Pista] = ("VIARIO-PISTA", 7, "bordo da pista"),
        [EdgeLevel.FaceMF] = ("VIARIO-MF-FACE", 4, "face do meio-fio"),
        [EdgeLevel.CostasMF] = ("VIARIO-MF-COSTAS", 3, "costas do meio-fio"),
        [EdgeLevel.Alinhamento] = ("VIARIO-ALINHAMENTO", 8, "alinhamento"),
        [EdgeLevel.Canteiro] = ("VIARIO-CANTEIRO", 30, "canteiro central"),
        [EdgeLevel.Ilha] = ("VIARIO-ILHA", 6, "ilha canalizadora")
    };

    internal sealed class Settings
    {
        public string CurrentSectionId { get; set; } = "P1";

        /// <summary>Tolerancia para ponta de eixo encostar em outro eixo.</summary>
        public double SnapTolerance { get; set; } = 0.05;

        /// <summary>Captura das pontas ao lancar eixos (puxa para o eixo vizinho).</summary>
        public double Capture { get; set; } = 1.0;

        /// <summary>Recuo do nariz do canteiro em relacao a pista transversal.</summary>
        public double MedianSetback { get; set; } = 1.0;

        /// <summary>Estilo de cota do C3DVIARIOCOTAR (vazio = corrente).</summary>
        public string CotaEstilo { get; set; } = "";

        /// <summary>DIMSCALE das cotas do C3DVIARIOCOTAR (0 = o do estilo/desenho).</summary>
        public double CotaEscala { get; set; }

        /// <summary>Hachura do pavimento: padrao (acad.pat), escala, angulo; ou solido com transparencia.</summary>
        public string HachuraPadrao { get; set; } = "ANSI31";
        public double HachuraEscala { get; set; } = 20.0;
        public double HachuraAngulo { get; set; }
        public bool HachuraSolida { get; set; }
        public int HachuraTransparencia { get; set; } = 70;
    }

    // ------------------------------------------------------------------
    // XData
    // ------------------------------------------------------------------

    /// <summary>Valores texto do XData do modulo (tag primeiro), ou null.</summary>
    public static List<string>? ReadValues(AcadDb.DBObject obj)
    {
        AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;

        using (rb)
        {
            List<string> values = new();
            foreach (AcadDb.TypedValue tv in rb.AsArray())
            {
                if (tv.TypeCode == (short)AcadDb.DxfCode.ExtendedDataAsciiString)
                    values.Add(tv.Value as string ?? string.Empty);
            }

            return values.Count == 0 ? null : values;
        }
    }

    public static string? ReadTag(AcadDb.DBObject obj, out string value)
    {
        List<string>? values = ReadValues(obj);
        value = values is not null && values.Count > 1 ? values[1] : string.Empty;
        return values?[0];
    }

    public static bool IsAxis(AcadDb.DBObject obj, out string sectionId) =>
        ReadTag(obj, out sectionId) == TagAxis;

    public static bool ReadAxis(AcadDb.DBObject obj, out string sectionId, out double? radius) =>
        ReadAxis(obj, out sectionId, out radius, out _);

    /// <summary>Marca de eixo com concordancia mantida (retas e arcos tangentes entre si).</summary>
    public const string FlagChain = "CONC=1";

    public static bool ReadAxis(AcadDb.DBObject obj, out string sectionId, out double? radius, out List<AxisEvent> events) =>
        ReadAxis(obj, out sectionId, out radius, out events, out _);

    /// <summary>Se o eixo tem a concordancia mantida (so polilinhas lancadas assim).</summary>
    public static bool IsChained(AcadDb.DBObject obj)
    {
        List<string>? values = ReadValues(obj);
        return values is not null && values.Count > 0 && values[0] == TagAxis && values.Contains(FlagChain);
    }

    public static bool ReadAxis(AcadDb.DBObject obj, out string sectionId, out double? radius, out List<AxisEvent> events, out bool chained)
    {
        sectionId = string.Empty;
        radius = null;
        chained = false;
        events = new List<AxisEvent>();
        List<string>? values = ReadValues(obj);
        if (values is null || values[0] != TagAxis)
            return false;

        chained = values.Contains(FlagChain);

        sectionId = values.Count > 1 ? values[1] : string.Empty;
        if (values.Count > 2 &&
            double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r >= 0)
        {
            radius = r;
        }

        for (int i = 3; i < values.Count; i++)
        {
            if (AxisEvent.Parse(values[i]) is AxisEvent ev)
                events.Add(ev);
        }

        return true;
    }

    public static bool IsGenerated(AcadDb.DBObject obj) =>
        ReadTag(obj, out _) == TagGenerated;

    public static void SetTag(AcadDb.Entity entity, params string[] values)
    {
        List<AcadDb.TypedValue> list = new()
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp)
        };
        foreach (string v in values)
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, v));

        entity.XData = new AcadDb.ResultBuffer(list.ToArray());
    }

    public static void SetAxis(AcadDb.Entity entity, string sectionId, double? radius, IEnumerable<AxisEvent>? events = null, bool chained = false)
    {
        List<string> values = new()
        {
            TagAxis,
            sectionId,
            radius is double r ? r.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty
        };

        if (chained)
            values.Add(FlagChain);
        if (events is not null)
            values.AddRange(events.Select(e => e.Serialize()));

        SetTag(entity, values.ToArray());
    }

    /// <summary>Muda a secao ou o raio do eixo mantendo os elementos e a concordancia dele.</summary>
    public static void UpdateAxis(AcadDb.Entity entity, string? sectionId = null, double? radius = null, bool clearRadius = false)
    {
        ReadAxis(entity, out string currentSection, out double? currentRadius, out List<AxisEvent> events, out bool chained);
        SetAxis(entity, sectionId ?? currentSection, clearRadius ? null : radius ?? currentRadius, events, chained);
    }

    /// <summary>Liga/desliga a concordancia mantida do eixo.</summary>
    public static void SetChained(AcadDb.Entity entity, bool chained)
    {
        if (!ReadAxis(entity, out string sectionId, out double? radius, out List<AxisEvent> events, out _))
            return;
        SetAxis(entity, sectionId, radius, events, chained);
    }

    /// <summary>Vertices e bulges de uma polilinha (bulge por trecho, n-1 para aberta).</summary>
    public static (List<V2> Points, List<double> Bulges) Vertices(AcadDb.Polyline pl)
    {
        List<V2> pts = new();
        List<double> bulges = new();
        int n = pl.NumberOfVertices;
        for (int i = 0; i < n; i++)
        {
            Point2d q = pl.GetPoint2dAt(i);
            pts.Add(new V2(q.X, q.Y));
            if (i < n - 1 || pl.Closed)
                bulges.Add(pl.GetBulgeAt(i));
        }

        return (pts, bulges);
    }

    // ------------------------------------------------------------------
    // Nome do eixo (RegApp proprio: sobrevive as regravacoes do XData do eixo)
    // ------------------------------------------------------------------

    /// <summary>RegApp do nome do eixo (separado: SetAxis/ClearTag nao o apagam).</summary>
    public const string RegAppName = "C3DVIARIONOME";

    /// <summary>Nome dado ao eixo ("" = sem nome).</summary>
    public static string AxisName(AcadDb.DBObject obj)
    {
        AcadDb.ResultBuffer? rb = obj.GetXDataForApplication(RegAppName);
        if (rb is null)
            return string.Empty;

        using (rb)
        {
            foreach (AcadDb.TypedValue tv in rb.AsArray())
            {
                if (tv.TypeCode == (short)AcadDb.DxfCode.ExtendedDataAsciiString)
                    return tv.Value as string ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>Grava (ou apaga, com nome vazio) o nome do eixo.</summary>
    public static void SetAxisName(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.Entity entity, string name)
    {
        EnsureRegApp(tr, db, RegAppName);
        name = (name ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (name.Length > 250)
            name = name[..250];
        entity.XData = name.Length == 0
            ? new AcadDb.ResultBuffer(new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegAppName))
            : new AcadDb.ResultBuffer(
                new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegAppName),
                new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataAsciiString, name));
    }

    /// <summary>Entidade pelo handle (chave do eixo), ou null.</summary>
    public static AcadDb.Entity? EntityByKey(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.OpenMode mode)
    {
        if (!long.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
            return null;
        if (!db.TryGetObjectId(new AcadDb.Handle(value), out AcadDb.ObjectId id) || id.IsErased)
            return null;
        return tr.GetObject(id, mode, false) as AcadDb.Entity;
    }

    /// <summary>Nomes dados aos eixos (chave = handle); so os que tem nome.</summary>
    public static Dictionary<string, string> ReadAxisNames(AcadDb.Database db, IEnumerable<string> keys)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (string key in keys)
        {
            try
            {
                if (EntityByKey(tr, db, key, AcadDb.OpenMode.ForRead) is AcadDb.Entity e && AxisName(e) is { Length: > 0 } name)
                    names[key] = name;
            }
            catch
            {
                // eixo apagado no meio do caminho
            }
        }

        return names;
    }

    public static void ClearTag(AcadDb.Entity entity)
    {
        entity.XData = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, RegApp));
    }

    public static void EnsureRegApp(AcadDb.Transaction tr, AcadDb.Database db) => EnsureRegApp(tr, db, RegApp);

    public static void EnsureRegApp(AcadDb.Transaction tr, AcadDb.Database db, string name)
    {
        AcadDb.RegAppTable table = (AcadDb.RegAppTable)tr.GetObject(db.RegAppTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(name))
            return;

        table.UpgradeOpen();
        AcadDb.RegAppTableRecord record = new() { Name = name };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    // ------------------------------------------------------------------
    // Layers
    // ------------------------------------------------------------------

    public const string LayerDims = "VIARIO-COTAS";

    public static void EnsureLayers(AcadDb.Transaction tr, AcadDb.Database db)
    {
        EnsureLayer(tr, db, LayerAxis, 1);
        EnsureLayer(tr, db, LayerDims, 2);
        foreach ((string layer, short color, _) in Elements.Values)
            EnsureLayer(tr, db, layer, color);
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

    public static string LayerFor(EdgeLevel level) => Elements[level].Layer;

    public static string LabelFor(EdgeLevel level) => Elements[level].Label;

    // ------------------------------------------------------------------
    // Secoes
    // ------------------------------------------------------------------

    public static RoadSection DefaultSection() =>
        new() { Id = "P1", Name = "Local", Tipo = "Local", Pista = 7.0, Faixas = 2, Velocidade = 30, Sarjeta = 0.30, MeioFio = 0.15, Calcada = 2.5, Raio = 6.0 };

    /// <summary>Tipos de via (CTB art. 60/61) oferecidos nas listas.</summary>
    public static readonly string[] Tipos = { "Local", "Coletora", "Arterial", "Transito rapido" };

    /// <summary>Secoes iniciais de um desenho novo (o codigo P1 continua o padrao dos eixos).</summary>
    public static List<RoadSection> Presets() => new()
    {
        DefaultSection(),
        new() { Id = "P2", Name = "Coletora", Tipo = "Coletora", Pista = 9.0, Faixas = 2, Velocidade = 40, Sarjeta = 0.30, MeioFio = 0.15, Calcada = 3.0, Raio = 8.0 },
        new() { Id = "P3", Name = "Arterial com canteiro", Tipo = "Arterial", Pista = 14.0, Canteiro = 4.0, Faixas = 4, Velocidade = 60, Sarjeta = 0.30, MeioFio = 0.15, Calcada = 3.5, Raio = 10.0 }
    };

    private static string GuessTipo(RoadSection s) =>
        s.Canteiro > 1e-3 || s.Pista >= 12.0 ? "Arterial" : s.Pista >= 9.0 ? "Coletora" : "Local";

    public static List<RoadSection> LoadSections(AcadDb.Database db)
    {
        Dictionary<string, RoadSection> result = new(StringComparer.Ordinal);

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        if (root is not null)
        {
            foreach (AcadDb.DBDictionaryEntry entry in root)
            {
                bool section = entry.Key.StartsWith(SectionPrefix, StringComparison.Ordinal);
                bool legacy = entry.Key.StartsWith(LegacyProfilePrefix, StringComparison.Ordinal);
                if (!section && !legacy)
                    continue;

                if (tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead) is not AcadDb.Xrecord xr)
                    continue;

                AcadDb.TypedValue[]? v = xr.Data?.AsArray();
                if (v is null || v.Length < 3)
                    continue;

                if (section && v[0].Value as string == SectionSignature && v.Length >= 9)
                {
                    RoadSection s = new()
                    {
                        Id = v[1].Value as string ?? "P?",
                        Name = v[2].Value as string ?? "",
                        Pista = Real(v[3], 7.0),
                        Canteiro = Real(v[4], 0.0),
                        Sarjeta = Real(v[5], 0.0),
                        MeioFio = Real(v[6], 0.0),
                        Calcada = Real(v[7], 0.0),
                        Raio = Real(v[8], 6.0)
                    };
                    if (v.Length >= 12)
                    {
                        s.Tipo = v[9].Value as string ?? "Local";
                        s.Faixas = v[10].Value is int faixas ? faixas : v[10].Value is short f16 ? f16 : 0;
                        s.Velocidade = Real(v[11], 40.0);
                    }
                    if (v.Length >= 13 && (v[12].Value is int lados || (v[12].Value is short l16 && (lados = l16) == l16)))
                        UnpackLados(s, lados);
                    else
                    {
                        s.Tipo = GuessTipo(s);
                    }

                    result[s.Id] = s;
                }
                else if (legacy && v[0].Value as string == LegacyProfileSignature && v.Length >= 6)
                {
                    // Perfil da v0.1: pista, calcada e raio (bordo = face do meio-fio).
                    string id = v[1].Value as string ?? "P?";
                    if (!result.ContainsKey(id))
                    {
                        result[id] = new RoadSection
                        {
                            Id = id,
                            Name = v[2].Value as string ?? "",
                            Pista = Real(v[3], 7.0),
                            Sarjeta = 0.0,
                            MeioFio = 0.0,
                            Calcada = Real(v[4], 2.5),
                            Raio = Real(v[5], 6.0)
                        };
                    }
                }
            }
        }

        if (result.Count == 0)
        {
            foreach (RoadSection preset in Presets())
                result[preset.Id] = preset;
        }

        return result.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    private static double Real(AcadDb.TypedValue tv, double fallback) =>
        tv.Value is double d && !double.IsNaN(d) && d >= 0 ? d : fallback;

    public static void SaveSection(AcadDb.Transaction tr, AcadDb.Database db, RoadSection s)
    {
        WriteRecord(tr, db, SectionPrefix + s.Id, new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, SectionSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.Id),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.Name),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Pista),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Canteiro),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Sarjeta),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.MeioFio),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Calcada),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Raio),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.Tipo),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, s.Faixas),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Velocidade),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, PackLados(s))));

        EraseRecord(tr, db, LegacyProfilePrefix + s.Id);
    }

    /// <summary>Lados de sarjeta, meio-fio e calcada num inteiro (2 bits cada).</summary>
    public static int PackLados(RoadSection s) =>
        (int)s.LadosSarjeta | ((int)s.LadosMeioFio << 2) | ((int)s.LadosCalcada << 4);

    public static void UnpackLados(RoadSection s, int packed)
    {
        s.LadosSarjeta = (Lados)(packed & 3);
        s.LadosMeioFio = (Lados)((packed >> 2) & 3);
        s.LadosCalcada = (Lados)((packed >> 4) & 3);
    }

    public static readonly (string Text, Lados Value)[] LadosOpcoes =
    {
        ("ambos", Lados.Ambos), ("esquerda", Lados.Esquerda), ("direita", Lados.Direita), ("nenhum", Lados.Nenhum)
    };

    public static string LadosNome(Lados l) => LadosOpcoes.First(o => o.Value == l).Text;

    public static Lados LadosDe(string text) =>
        LadosOpcoes.FirstOrDefault(o => o.Text.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase), LadosOpcoes[0]).Value;

    public static void DeleteSection(AcadDb.Transaction tr, AcadDb.Database db, string id)
    {
        EraseRecord(tr, db, SectionPrefix + id);
        EraseRecord(tr, db, LegacyProfilePrefix + id);
    }

    public static string NextSectionId(IEnumerable<RoadSection> sections)
    {
        int max = 0;
        foreach (RoadSection p in sections)
        {
            if (p.Id.Length > 1 && p.Id[0] == 'P' &&
                int.TryParse(p.Id[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                max = Math.Max(max, n);
            }
        }

        return "P" + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Configuracao
    // ------------------------------------------------------------------

    public static Settings LoadSettings(AcadDb.Database db)
    {
        Settings settings = new();
        AcadDb.TypedValue[]? v = ReadRecord(db, SettingsKey);
        if (v is null || v.Length < 4 || v[0].Value as string != SettingsSignature)
            return settings;

        settings.CurrentSectionId = v[1].Value as string ?? "P1";
        settings.SnapTolerance = v[2].Value is double snap && snap > 0 ? snap : 0.05;
        settings.Capture = v[3].Value is double capture && capture >= 0 ? capture : 1.0;
        if (v.Length >= 5 && v[4].Value is double setback && setback >= 0)
            settings.MedianSetback = setback;
        if (v.Length >= 6)
            settings.CotaEstilo = v[5].Value as string ?? "";
        if (v.Length >= 7 && v[6].Value is double escala && escala >= 0)
            settings.CotaEscala = escala;
        if (v.Length >= 12)
        {
            settings.HachuraPadrao = v[7].Value as string is { Length: > 0 } pat ? pat : "ANSI31";
            if (v[8].Value is double hs && hs > 0) settings.HachuraEscala = hs;
            if (v[9].Value is double ha) settings.HachuraAngulo = ha;
            settings.HachuraSolida = v[10].Value is int sol ? sol != 0 : v[10].Value is short s16 && s16 != 0;
            if (v[11].Value is int tp) settings.HachuraTransparencia = tp;
            else if (v[11].Value is short tp16) settings.HachuraTransparencia = tp16;
        }
        return settings;
    }

    public static void SaveSettings(AcadDb.Transaction tr, AcadDb.Database db, Settings s)
    {
        WriteRecord(tr, db, SettingsKey, new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, SettingsSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.CurrentSectionId),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.SnapTolerance),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.Capture),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.MedianSetback),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.CotaEstilo ?? ""),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.CotaEscala),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.HachuraPadrao ?? "ANSI31"),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.HachuraEscala),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.HachuraAngulo),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, s.HachuraSolida ? 1 : 0),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, s.HachuraTransparencia)));
    }

    // ------------------------------------------------------------------
    // Sinalizacao horizontal (opcoes como texto chave=valor)
    // ------------------------------------------------------------------

    private const string SignageKey = "SINAL";
    private const string SignageSignature = "VSINAL1";

    public static SignageOptions LoadSignage(AcadDb.Database db)
    {
        SignageOptions o = new();
        AcadDb.TypedValue[]? v = ReadRecord(db, SignageKey);
        if (v is null || v.Length < 2 || v[0].Value as string != SignageSignature)
            return o;

        foreach (AcadDb.TypedValue tv in v.Skip(1))
        {
            if (tv.Value is not string pair)
                continue;
            int eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            System.Reflection.PropertyInfo? prop = typeof(SignageOptions).GetProperty(pair[..eq]);
            string text = pair[(eq + 1)..];
            if (prop is null || !prop.CanWrite)
                continue;

            if (prop.PropertyType == typeof(bool))
                prop.SetValue(o, text == "1");
            else if (prop.PropertyType == typeof(double) &&
                     double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                prop.SetValue(o, d);
            else if (prop.PropertyType == typeof(string))
                prop.SetValue(o, text);
        }

        return o;
    }

    public static void SaveSignage(AcadDb.Transaction tr, AcadDb.Database db, SignageOptions o)
    {
        List<AcadDb.TypedValue> list = new() { new((int)AcadDb.DxfCode.Text, SignageSignature) };
        foreach (System.Reflection.PropertyInfo prop in typeof(SignageOptions).GetProperties())
        {
            if (!prop.CanWrite)
                continue;
            object? value = prop.GetValue(o);
            string text = value switch
            {
                bool b => b ? "1" : "0",
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                string str => str,
                _ => ""
            };
            list.Add(new((int)AcadDb.DxfCode.Text, prop.Name + "=" + text));
        }

        WriteRecord(tr, db, SignageKey, new AcadDb.ResultBuffer(list.ToArray()));
    }

    // ------------------------------------------------------------------
    // Raios por esquina
    // ------------------------------------------------------------------

    public static Dictionary<string, double> LoadCornerRadii(AcadDb.Database db)
    {
        Dictionary<string, double> result = new(StringComparer.Ordinal);
        AcadDb.TypedValue[]? v = ReadRecord(db, RadiiKey);
        if (v is null || v.Length < 1 || v[0].Value as string != RadiiSignature)
            return result;

        for (int i = 1; i + 1 < v.Length; i += 2)
        {
            if (v[i].Value is string key && v[i + 1].Value is double r)
                result[key] = r;
        }

        return result;
    }

    public static void SaveCornerRadii(AcadDb.Transaction tr, AcadDb.Database db, Dictionary<string, double> radii)
    {
        List<AcadDb.TypedValue> list = new() { new((int)AcadDb.DxfCode.Text, RadiiSignature) };
        foreach ((string key, double r) in radii)
        {
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, key));
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, r));
        }

        WriteRecord(tr, db, RadiiKey, new AcadDb.ResultBuffer(list.ToArray()));
    }

    // ------------------------------------------------------------------
    // Faixas livres por esquina
    // ------------------------------------------------------------------

    public static Dictionary<string, SlipLane> LoadSlipLanes(AcadDb.Database db)
    {
        Dictionary<string, SlipLane> result = new(StringComparer.Ordinal);
        AcadDb.TypedValue[]? v = ReadRecord(db, SlipKey);
        if (v is null || v.Length < 1 || v[0].Value as string != SlipSignature)
            return result;

        for (int i = 1; i + 1 < v.Length; i += 2)
        {
            if (v[i].Value is string key && v[i + 1].Value is string text && SlipLane.Parse(text) is SlipLane slip)
                result[key] = slip;
        }

        return result;
    }

    public static void SaveSlipLanes(AcadDb.Transaction tr, AcadDb.Database db, Dictionary<string, SlipLane> slips)
    {
        List<AcadDb.TypedValue> list = new() { new((int)AcadDb.DxfCode.Text, SlipSignature) };
        foreach ((string key, SlipLane slip) in slips)
        {
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, key));
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, slip.Serialize()));
        }

        WriteRecord(tr, db, SlipKey, new AcadDb.ResultBuffer(list.ToArray()));
    }

    // ------------------------------------------------------------------
    // Linhas soltas (o plugin nao recria)
    // ------------------------------------------------------------------

    public static HashSet<string> LoadReleased(AcadDb.Database db)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        AcadDb.TypedValue[]? v = ReadRecord(db, ReleasedKey);
        if (v is null || v.Length < 1 || v[0].Value as string != ReleasedSignature)
            return result;

        for (int i = 1; i < v.Length; i++)
        {
            if (v[i].Value is string key)
                result.Add(key);
        }

        return result;
    }

    public static void SaveReleased(AcadDb.Transaction tr, AcadDb.Database db, IEnumerable<string> keys)
    {
        List<AcadDb.TypedValue> list = new() { new((int)AcadDb.DxfCode.Text, ReleasedSignature) };
        foreach (string key in keys)
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, key));

        WriteRecord(tr, db, ReleasedKey, new AcadDb.ResultBuffer(list.ToArray()));
    }

    /// <summary>Pontos dentro das ilhas eliminadas pelo usuario.</summary>
    public static List<V2> LoadRemovedIslands(AcadDb.Database db)
    {
        List<V2> result = new();
        AcadDb.TypedValue[]? v = ReadRecord(db, RemovedIslandsKey);
        if (v is null || v.Length < 1 || v[0].Value as string != RemovedIslandsSignature)
            return result;

        for (int i = 1; i + 1 < v.Length; i += 2)
        {
            if (v[i].Value is double x && v[i + 1].Value is double y)
                result.Add(new V2(x, y));
        }

        return result;
    }

    /// <summary>Raio dos cantos das ilhas canalizadoras: ponto dentro da ilha + raio.</summary>
    public static List<(V2 Seed, double Radius)> LoadIslandRadii(AcadDb.Database db)
    {
        List<(V2, double)> result = new();
        AcadDb.TypedValue[]? v = ReadRecord(db, IslandRadiiKey);
        if (v is null || v.Length < 1 || v[0].Value as string != IslandRadiiSignature)
            return result;

        for (int i = 1; i + 2 < v.Length; i += 3)
        {
            if (v[i].Value is double x && v[i + 1].Value is double y && v[i + 2].Value is double r)
                result.Add((new V2(x, y), r));
        }

        return result;
    }

    public static void SaveIslandRadii(AcadDb.Transaction tr, AcadDb.Database db, IEnumerable<(V2 Seed, double Radius)> items)
    {
        List<AcadDb.TypedValue> list = new() { new((int)AcadDb.DxfCode.Text, IslandRadiiSignature) };
        foreach ((V2 p, double r) in items)
        {
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.X));
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Y));
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, r));
        }

        WriteRecord(tr, db, IslandRadiiKey, new AcadDb.ResultBuffer(list.ToArray()));
    }

    public static void SaveRemovedIslands(AcadDb.Transaction tr, AcadDb.Database db, IEnumerable<V2> seeds)
    {
        List<AcadDb.TypedValue> list = new() { new((int)AcadDb.DxfCode.Text, RemovedIslandsSignature) };
        foreach (V2 p in seeds)
        {
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.X));
            list.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Y));
        }

        WriteRecord(tr, db, RemovedIslandsKey, new AcadDb.ResultBuffer(list.ToArray()));
    }

    // ------------------------------------------------------------------
    // NOD
    // ------------------------------------------------------------------

    private static AcadDb.DBDictionary? TryRoot(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        return nod.Contains(RootName)
            ? (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootName), AcadDb.OpenMode.ForRead)
            : null;
    }

    // ------------------------------------------------------------------
    // Atualizacao automatica e assinatura dos registros
    // ------------------------------------------------------------------

    private const string AutoKey = "AUTO";

    /// <summary>Atualizacao automatica da rede neste desenho (padrao ligada).</summary>
    public static bool LoadAutoUpdate(AcadDb.Database db)
    {
        AcadDb.TypedValue[]? v = ReadRecord(db, AutoKey);
        if (v is null || v.Length < 1)
            return true;
        return v[0].Value switch
        {
            int i => i != 0,
            short s => s != 0,
            _ => true
        };
    }

    public static void SaveAutoUpdate(AcadDb.Transaction tr, AcadDb.Database db, bool on) =>
        WriteRecord(tr, db, AutoKey, new AcadDb.ResultBuffer(new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, on ? 1 : 0)));

    /// <summary>
    /// Assinatura (hash) dos registros do modulo que mudam a rede - secoes, raios de esquina,
    /// faixas livres, ilhas eliminadas e raios de ilha, e tolerancias da configuracao: muda
    /// quando qualquer um deles muda (inclusive por UNDO). Sinalizacao, linhas desvinculadas,
    /// cotas, hachura e a atualizacao automatica ficam de fora (nao mudam o calculo).
    /// </summary>
    public static string RecordsSignature(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        if (root is null)
            return "";

        ulong hash = 14695981039346656037UL;
        void Mix(string text)
        {
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }

            hash ^= 0x1F;
            hash *= 1099511628211UL;
        }

        foreach (AcadDb.DBDictionaryEntry entry in root)
        {
            bool config = entry.Key == SettingsKey;
            bool engine = config || entry.Key.StartsWith(SectionPrefix, StringComparison.Ordinal) ||
                          entry.Key is RadiiKey or SlipKey or RemovedIslandsKey or IslandRadiiKey;
            if (!engine || tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead) is not AcadDb.Xrecord xr ||
                xr.Data is not AcadDb.ResultBuffer data)
            {
                continue;
            }

            Mix(entry.Key);
            AcadDb.TypedValue[] values = data.AsArray();
            for (int i = 0; i < values.Length; i++)
            {
                // CONFIG: so a marca, tolerancia, captura e recuo do canteiro (0, 2 a 4) entram no motor.
                if (config && (i == 1 || i > 4))
                    continue;
                Mix(values[i].TypeCode.ToString(CultureInfo.InvariantCulture) + ":" +
                    Convert.ToString(values[i].Value, CultureInfo.InvariantCulture));
            }
        }

        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static AcadDb.TypedValue[]? ReadRecord(AcadDb.Database db, string key)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        if (root is null || !root.Contains(key))
            return null;

        return tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForRead) is AcadDb.Xrecord xr
            ? xr.Data?.AsArray()
            : null;
    }

    private static void EraseRecord(AcadDb.Transaction tr, AcadDb.Database db, string key)
    {
        AcadDb.DBDictionary? root = TryRoot(tr, db);
        if (root is not null && root.Contains(key))
            tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite).Erase();
    }

    private static void WriteRecord(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.ResultBuffer data)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        AcadDb.DBDictionary root;

        if (nod.Contains(RootName))
        {
            root = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootName), AcadDb.OpenMode.ForWrite);
        }
        else
        {
            nod.UpgradeOpen();
            root = new AcadDb.DBDictionary();
            nod.SetAt(RootName, root);
            tr.AddNewlyCreatedDBObject(root, true);
        }

        if (root.Contains(key))
        {
            ((AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite)).Data = data;
            return;
        }

        AcadDb.Xrecord record = new() { Data = data };
        root.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    // ------------------------------------------------------------------
    // Conversao AutoCAD <-> GPath
    // ------------------------------------------------------------------

    public static GPath? ToPath(AcadDb.Curve curve)
    {
        switch (curve)
        {
            case AcadDb.Polyline pl:
            {
                if (pl.Normal.Z < 0.999)
                    return null;

                List<Seg> segs = new();
                int n = pl.NumberOfVertices;
                int count = pl.Closed ? n : n - 1;
                for (int i = 0; i < count; i++)
                {
                    Point2d a = pl.GetPoint2dAt(i);
                    Point2d b = pl.GetPoint2dAt((i + 1) % n);
                    segs.Add(new Seg(new V2(a.X, a.Y), new V2(b.X, b.Y), pl.GetBulgeAt(i)));
                }

                GPath path = new(segs);
                return path.IsEmpty ? null : path;
            }

            case AcadDb.Line line:
            {
                GPath path = new(new[]
                {
                    Seg.Line(new V2(line.StartPoint.X, line.StartPoint.Y), new V2(line.EndPoint.X, line.EndPoint.Y))
                });
                return path.IsEmpty ? null : path;
            }

            case AcadDb.Arc arc:
            {
                if (arc.Normal.Z < 0.999)
                    return null;

                double sweep = GMath.Norm(arc.EndAngle - arc.StartAngle);
                if (sweep < 1e-9)
                    return null;

                return new GPath(new[]
                {
                    Seg.FromArc(new V2(arc.Center.X, arc.Center.Y), arc.Radius, arc.StartAngle, sweep)
                });
            }

            case AcadDb.Circle circle:
            {
                if (circle.Normal.Z < 0.999 || circle.Radius < 1e-6)
                    return null;

                V2 c = new(circle.Center.X, circle.Center.Y);
                return new GPath(new[]
                {
                    Seg.FromArc(c, circle.Radius, 0.0, Math.PI),
                    Seg.FromArc(c, circle.Radius, Math.PI, Math.PI)
                });
            }

            default:
                return null;
        }
    }

    public static AcadDb.Polyline ToPolyline(IReadOnlyList<Seg> segs, bool closed)
    {
        AcadDb.Polyline pl = new();
        int index = 0;
        foreach (Seg s in segs)
            pl.AddVertexAt(index++, new Point2d(s.A.X, s.A.Y), s.Bulge, 0, 0);

        if (closed)
        {
            pl.Closed = true;
        }
        else if (segs.Count > 0)
        {
            V2 end = segs[^1].B;
            pl.AddVertexAt(index, new Point2d(end.X, end.Y), 0, 0, 0);
        }

        return pl;
    }

    // ------------------------------------------------------------------
    // Chave geometrica (atualiza so o que mudou)
    // ------------------------------------------------------------------

    public static string GeometryKey(EdgeLevel level, IEnumerable<(double X, double Y, double Bulge)> vertices, bool closed)
    {
        List<string> points = new();
        List<string> bulges = new();

        foreach ((double x, double y, double b) in vertices)
        {
            points.Add(Math.Round(x, 3).ToString("0.000", CultureInfo.InvariantCulture) + "," +
                       Math.Round(y, 3).ToString("0.000", CultureInfo.InvariantCulture));
            if (Math.Abs(b) > 1e-9)
                bulges.Add(Math.Round(Math.Abs(b), 5).ToString("0.00000", CultureInfo.InvariantCulture));
        }

        points.Sort(StringComparer.Ordinal);
        bulges.Sort(StringComparer.Ordinal);
        return ((int)level).ToString(CultureInfo.InvariantCulture) + (closed ? "C" : "O") + "|" +
               string.Join(";", points) + "|" + string.Join(";", bulges);
    }

    public static string GeometryKey(OutputLine line)
    {
        List<(double, double, double)> vertices = line.Segs.Select(s => (s.A.X, s.A.Y, s.Bulge)).ToList();
        if (!line.Closed && line.Segs.Count > 0)
            vertices.Add((line.Segs[^1].B.X, line.Segs[^1].B.Y, 0.0));
        return GeometryKey(line.Level, vertices, line.Closed);
    }

    public static string GeometryKey(AcadDb.Polyline pl, EdgeLevel level)
    {
        List<(double, double, double)> vertices = new();
        for (int i = 0; i < pl.NumberOfVertices; i++)
        {
            Point2d p = pl.GetPoint2dAt(i);
            bool lastOpen = !pl.Closed && i == pl.NumberOfVertices - 1;
            vertices.Add((p.X, p.Y, lastOpen ? 0.0 : pl.GetBulgeAt(i)));
        }

        return GeometryKey(level, vertices, pl.Closed);
    }

    public static EdgeLevel ParseLevel(string text) =>
        Enum.TryParse(text, ignoreCase: true, out EdgeLevel level) ? level : EdgeLevel.FaceMF;
}
