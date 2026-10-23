using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcColors = Autodesk.AutoCAD.Colors;
using Civil3D2026Plugin.Infrastructure;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Placas de sinalizacao vertical no desenho, no mesmo padrao do SinC (para o SinC
/// reconhecer e para os quantitativos dele):
///
/// - bloco "T-xxx_SV": geometria do T-xxx_SV.dwg do SinC (escala 5: 4 m = 20 un.) mais os 15
///   atributos do SinC (Codigo, Situacao, Nort, Est visiveis a direita da placa; Area,
///   Dimensao, Eixo, Estaca/KM, Lado, Pelicula_Fundo, Pelicula_Orla, SI, Substrato,
///   Suporte, Handle_Suporte invisiveis), layer sinC_VERTICALP, atributos em sinC_ATT_Placa;
/// - bloco "sinC_LDC" (linha de chamada) no ponto de implantacao, layer sinC_LinhaDeChamada;
/// - a placa fica na ponta da linha de chamada: offset local (5,657; -6,432) do ponto,
///   girado pela rotacao (perpendicular a via), igual ao que o SinC faz.
///
/// O suporte (bloco *_SVS) nao e inserido: fica para o SinC. O vinculo com a rede viaria
/// (eixo, estaca, lado) e calculado pelos eixos e gravado no XData do bloco da placa
/// (RegApp C3DVIARIO, tag PLACA) e nos atributos Eixo, Estaca/KM e Lado.
/// </summary>
internal static class ViarioPlacaSync
{
    public const string Modulo = "VIARIO.PLACAS";
    public const string TagPlaca = "PLACA";
    public const string TagLdc = "PLACALDC";
    public const string TagSuporte = "PLACASUP";
    public const string LayerSuporte = "sinC_VERTICALS";
    public const string LayerSuporteGeo = "sinC_Suporte";
    public const string LayerAttSuporte = "sinC_ATT_Suporte";

    public const string LayerPlaca = "sinC_VERTICALP";
    public const string LayerAtt = "sinC_ATT_Placa";
    public const string LayerLdc = "sinC_LinhaDeChamada";
    public const string BlockLdc = "sinC_LDC";

    /// <summary>Os T-xxx_SV.dwg do SinC sao desenhados em escala 5 (4 m = 20 unidades).</summary>
    public const double FatorSinC = 5.0;

    /// <summary>Offset local do canto superior esquerdo da placa em relacao ao ponto (medido num desenho do SinC).</summary>
    private static readonly V2 OffsetPlaca = new(5.656871, -6.431838);
    private static readonly V2 OffsetLdc = new(0.0, -0.275);

    private static readonly string[] VisibleTags = { "Código", "Situação", "Nort", "Est" };
    private static readonly string[] InvisibleTags =
        { "Área", "Dimensão", "Eixo", "Estaca/KM", "Lado", "Película_Fundo", "Película_Orla", "SI", "Substrato", "Suporte", "Handle_Suporte" };

    /// <summary>Valores padrao dos atributos que o projeto nao traz (como no exemplo do SinC).</summary>
    internal sealed class Opcoes
    {
        public string Situacao { get; set; } = "IMP";
        public string PeliculaFundo { get; set; } = "Tipo III";
        public string PeliculaOrla { get; set; } = "Tipo X";
        public string SI { get; set; } = "Não";
        public string Substrato { get; set; } = "Aço";
        /// <summary>Escala da placa (como o campo "Escala da Placa" do SinC).</summary>
        public double Escala { get; set; } = 1.0;
        /// <summary>Distancia maxima do ponto ao eixo para amarrar (m).</summary>
        public double DistanciaMaxima { get; set; } = 60.0;

        /// <summary>Insere o bloco do suporte (tipo pela area da placa) e liga Handle_Suporte.</summary>
        public bool Suportes { get; set; } = true;
        /// <summary>Limites de area (m2): ate Simples = coluna simples; ate Duplo = coluna dupla; acima = semi-portico/portico.</summary>
        public double AreaSimples { get; set; } = 2.0;
        public double AreaDuplo { get; set; } = 6.0;
        /// <summary>Acima de AreaDuplo, placa mais larga que isto (m) vai para portico; senao semi-portico.</summary>
        public double LarguraPortico { get; set; } = 6.0;
        public string Material { get; set; } = "Metálico";
    }

    private static readonly string[] SupVisibleTags = { "Altura", "Tipo", "Nort", "Est" };
    private static readonly string[] SupInvisibleTags = { "Eixo", "Estaca/KM", "Lado", "Material", "Seção", "Situação" };

    // ------------------------------------------------------------------
    // Leitura
    // ------------------------------------------------------------------

    /// <summary>Placas do desenho (blocos com XData PLACA), na ordem do id.</summary>
    public static List<PlacaItem> Read(AcadDb.Database db)
    {
        List<PlacaItem> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach ((PlacaItem p, _) in Scan(tr, db))
            list.Add(p);
        return list.OrderBy(p => p.Trecho, StringComparer.Ordinal).ThenBy(p => p.Sentido, StringComparer.Ordinal).ThenBy(p => p.Item).ToList();
    }

    public static int Count(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        return Scan(tr, db).Count;
    }

    private static List<(PlacaItem Placa, AcadDb.BlockReference Ref)> Scan(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<(PlacaItem, AcadDb.BlockReference)> list = new();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || id.ObjectClass.DxfName != "INSERT")
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockReference br)
                continue;
            List<string>? values = ViarioStore.ReadValues(br);
            if (values is null || values.Count < 2 || values[0] != TagPlaca)
                continue;
            PlacaItem? p = PlacaItem.Deserialize(string.Concat(values.Skip(1)));
            if (p is not null)
            {
                p.Escala = Math.Abs(br.ScaleFactors.X) > 1e-9 ? Math.Abs(br.ScaleFactors.X) : 1.0;
                list.Add((p, br));
            }
        }

        return list;
    }

    /// <summary>XData em pedacos de ate 250 caracteres (limite de cada string do XData).</summary>
    private static void Tag(AcadDb.Entity entity, string tag, string text)
    {
        List<string> values = new() { tag };
        for (int i = 0; i < text.Length; i += 250)
            values.Add(text.Substring(i, Math.Min(250, text.Length - i)));
        ViarioStore.SetTag(entity, values.ToArray());
    }

    // ------------------------------------------------------------------
    // Importar
    // ------------------------------------------------------------------

    /// <summary>
    /// Insere as placas do catalogo. Retorna quantas entraram, quantas ficaram sem eixo e
    /// os codigos sem arquivo T-xxx_SV.dwg na pasta.
    /// </summary>
    public static (int Inseridas, int SemEixo, List<string> SemArquivo) Import(Document doc, PlacaCatalog cat, string pasta, Opcoes o)
    {
        AcadDb.Database db = doc.Database;
        List<AxisInput> axes = ViarioSync.CurrentAxesOnly(db);
        Dictionary<string, string> names = AxisLabels(db, axes);

        int inseridas = 0, semEixo = 0;
        List<string> semArquivo = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        ViarioStore.EnsureRegApp(tr, db);
        foreach (string layer in new[] { LayerPlaca, LayerAtt, LayerLdc, LayerSuporte, LayerSuporteGeo, LayerAttSuporte })
            ViarioStore.EnsureLayer(tr, db, layer, 7);
        AcadDb.ObjectId ldcId = EnsureLdcBlock(tr, db);

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
        Dictionary<string, AcadDb.ObjectId> blocks = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> existentes = new(Scan(tr, db).Select(x => x.Placa.Id), StringComparer.OrdinalIgnoreCase);
        int repetidas = 0;

        foreach (PlacaItem p in cat.Placas)
        {
            if (existentes.Contains(p.Id))
            {
                repetidas++;
                continue; // ja esta no desenho (use Remover antes para reinserir)
            }

            if (!ViarioPlacas.Link(p, axes, names, o.DistanciaMaxima))
                semEixo++;
            p.SuporteTipo = o.Suportes ? ViarioPlacas.SupportType(p.AreaRegra, p.LarguraSinC, o.AreaSimples, o.AreaDuplo, o.LarguraPortico) : "";

            string blockName = p.Codigo + "_SV";
            if (!blocks.TryGetValue(blockName, out AcadDb.ObjectId blockId))
            {
                string file = Path.Combine(pasta, blockName + ".dwg");
                blockId = EnsureSignBlock(tr, db, blockName, file, p.LarguraSinC * FatorSinC);
                blocks[blockName] = blockId;
                if (blockId.IsNull)
                    semArquivo.Add(p.Codigo);
            }

            if (blockId.IsNull)
                continue;

            InsertOne(tr, db, ms, ldcId, blockId, p, o);
            inseridas++;
        }

        tr.Commit();
        PluginLog.Info(Modulo, $"Importadas {inseridas} placas ({semEixo} sem eixo, {semArquivo.Count} sem arquivo, {repetidas} ja existiam) de {cat.Fonte}.");
        UltimasRepetidas = repetidas;
        return (inseridas, semEixo, semArquivo);
    }

    /// <summary>Placas puladas na ultima importacao por ja existirem no desenho.</summary>
    public static int UltimasRepetidas { get; private set; }

    private static Dictionary<string, string> AxisLabels(AcadDb.Database db, List<AxisInput> axes)
    {
        Dictionary<string, string> names = ViarioSync.AxisNames(db, axes.Select(a => a.Key));
        Dictionary<string, string> civil = ViarioCivil.IsActive(db) ? ViarioCivil.AxisNames(db) : new Dictionary<string, string>();
        for (int i = 0; i < axes.Count; i++)
        {
            string key = axes[i].Key;
            if (names.ContainsKey(key))
                continue;
            names[key] = civil.TryGetValue(key, out string? c) ? c : (axes[i].Path.Closed ? $"Rotatoria {i + 1}" : $"Eixo {i + 1}");
        }

        return names;
    }

    /// <summary>
    /// Atributo Área como o SinC grava: m2 com 2 casas e sem a virgula (9,18 -> "918"; 5,6 -> "560";
    /// 0,975 -> "98"). Antes saia com casas variaveis ("56", "0975") e o quantitativo do SinC somava errado.
    /// </summary>
    private static string AreaAtributo(PlacaItem p)
    {
        string t = Math.Round(p.AreaSinC, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0');
        return t.Length == 0 ? "0" : t;
    }

    private static Point3d Local(PlacaItem p, V2 offset, double scale)
    {
        V2 d = ViarioPlacas.Rotate(offset * scale, p.Rotacao);
        return new Point3d(p.E + d.X, p.N + d.Y, 0.0);
    }

    private static void InsertOne(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord ms, AcadDb.ObjectId ldcId,
        AcadDb.ObjectId blockId, PlacaItem p, Opcoes o)
    {
        double rad = p.Rotacao * Math.PI / 180.0;
        bool comSuporte = o.Suportes && p.SuporteTipo.Length > 0;
        V2 ldcOff = comSuporte ? LdcOffset(p.SuporteTipo) : OffsetLdc;
        V2 placaOff = ldcOff + ((OffsetPlaca - OffsetLdc) * o.Escala);

        // Linha de chamada: sai do suporte (ponto que o SinC usa para cada tipo).
        AcadDb.BlockReference ldc = new(Local(p, ldcOff, 1.0), ldcId) { Rotation = rad };
        ldc.SetDatabaseDefaults(db);
        ldc.Layer = LayerLdc;
        ms.AppendEntity(ldc);
        tr.AddNewlyCreatedDBObject(ldc, true);
        ViarioStore.SetTag(ldc, TagLdc, p.Id);

        // Placa na ponta da linha de chamada.
        AcadDb.BlockReference br = new(Local(p, placaOff, 1.0), blockId)
        {
            Rotation = rad,
            ScaleFactors = new Scale3d(o.Escala)
        };
        br.SetDatabaseDefaults(db);
        br.Layer = LayerPlaca;
        ms.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);

        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(blockId, AcadDb.OpenMode.ForRead);
        Dictionary<string, string> values = AttributeValues(p, o);
        foreach (AcadDb.ObjectId entId in def)
        {
            if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeDefinition ad || ad.Constant)
                continue;
            AcadDb.AttributeReference ar = new();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            ar.Layer = LayerAtt;
            ar.TextString = values.TryGetValue(ad.Tag, out string? v) ? v : ad.TextString;
            br.AttributeCollection.AppendAttribute(ar);
            tr.AddNewlyCreatedDBObject(ar, true);
            try
            {
                ar.AdjustAlignment(db);
            }
            catch
            {
                // Alinhamento cosmetico.
            }
        }

        // Suporte no ponto de implantacao; a placa guarda o handle dele (Handle_Suporte).
        AcadDb.ObjectId supId = comSuporte ? EnsureSupportBlock(tr, db, p.SuporteTipo) : AcadDb.ObjectId.Null;
        if (!supId.IsNull)
        {
            AcadDb.BlockReference sup = new(new Point3d(p.E, p.N, 0.0), supId) { Rotation = rad };
            sup.SetDatabaseDefaults(db);
            sup.Layer = LayerSuporte;
            ms.AppendEntity(sup);
            tr.AddNewlyCreatedDBObject(sup, true);
            SetVisibility(sup, "Implant");
            AcadDb.BlockTableRecord supDef = (AcadDb.BlockTableRecord)tr.GetObject(supId, AcadDb.OpenMode.ForRead);
            Dictionary<string, string> supValues = SupportAttributeValues(p, o);
            foreach (AcadDb.ObjectId entId in supDef)
            {
                if (tr.GetObject(entId, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeDefinition ad || ad.Constant)
                    continue;
                AcadDb.AttributeReference ar = new();
                ar.SetAttributeFromBlock(ad, sup.BlockTransform);
                ar.Layer = LayerAttSuporte;
                ar.TextString = supValues.TryGetValue(ad.Tag, out string? v) ? v : "";
                sup.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
            }

            ViarioStore.SetTag(sup, TagSuporte, p.Id);
            SetAttribute(tr, br, "Handle_Suporte", sup.Handle.ToString());
            SetAttribute(tr, br, "Suporte", p.SuporteTipo);
        }

        Tag(br, TagPlaca, p.Serialize());
    }

    private static void SetAttribute(AcadDb.Transaction tr, AcadDb.BlockReference br, string tag, string value)
    {
        foreach (AcadDb.ObjectId aid in br.AttributeCollection)
        {
            if (tr.GetObject(aid, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeReference ar || !string.Equals(ar.Tag, tag, StringComparison.OrdinalIgnoreCase))
                continue;
            ar.UpgradeOpen();
            ar.TextString = value;
        }
    }

    private static string Coord(double v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    private static Dictionary<string, string> SupportAttributeValues(PlacaItem p, Opcoes o) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Altura"] = p.AlturaSuporte.Length > 0 ? p.AlturaSuporte.Replace('.', ',').TrimEnd('m').Trim() + "m" : DefaultHeight(p.SuporteTipo),
        ["Tipo"] = p.SuporteTipo,
        ["Nort"] = "",
        ["Est"] = "",
        ["Eixo"] = p.AxisName,
        ["Estaca/KM"] = p.EstacaKm,
        ["Lado"] = p.LadoTexto,
        ["Material"] = o.Material,
        ["Seção"] = "",
        ["Situação"] = o.Situacao
    };

    private static Dictionary<string, string> AttributeValues(PlacaItem p, Opcoes o) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Código"] = p.Codigo,
        ["Situação"] = o.Situacao,
        ["Nort"] = "N: " + Coord(p.N),
        ["Est"] = "E: " + Coord(p.E),
        ["Área"] = AreaAtributo(p),
        ["Dimensão"] = p.DimensaoSinC,
        ["Eixo"] = p.AxisName,
        ["Estaca/KM"] = p.EstacaKm,
        ["Lado"] = p.LadoTexto,
        ["Película_Fundo"] = o.PeliculaFundo,
        ["Película_Orla"] = o.PeliculaOrla,
        ["SI"] = o.SI,
        ["Substrato"] = o.Substrato,
        ["Suporte"] = p.SuporteTipo.Length > 0 ? p.SuporteTipo : SuporteNome(p.Suporte),
        ["Handle_Suporte"] = ""
    };

    /// <summary>Codigo de suporte do projeto (CS, CD2...) no nome que o SinC usa.</summary>
    private static string SuporteNome(string codigo) => codigo.Trim().ToUpperInvariant() switch
    {
        "CS" or "CS1" => "Coluna Simples",
        "CD" or "CD2" => "Coluna Dupla",
        "" => "",
        _ => codigo.Trim()
    };

    // ------------------------------------------------------------------
    // Blocos
    // ------------------------------------------------------------------

    /// <summary>Bloco "sinC_LDC" (linha de chamada do SinC): 3 vertices, 0,5 para baixo e 8 m a 45 graus.</summary>
    private static AcadDb.ObjectId EnsureLdcBlock(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (bt.Has(BlockLdc))
            return bt[BlockLdc];

        bt.UpgradeOpen();
        AcadDb.BlockTableRecord btr = new() { Name = BlockLdc, Origin = Point3d.Origin };
        AcadDb.ObjectId id = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);

        AcadDb.Polyline pl = new();
        pl.SetDatabaseDefaults(db);
        pl.AddVertexAt(0, new Point2d(0.0, 0.0), 0.0, 0.0, 0.0);
        pl.AddVertexAt(1, new Point2d(0.0, -0.5), 0.0, 0.0, 0.0);
        pl.AddVertexAt(2, new Point2d(5.656871, -6.156838), 0.0, 0.0, 0.0);
        pl.Layer = "0";
        pl.ColorIndex = 256;
        btr.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        return id;
    }

    /// <summary>
    /// Nome do bloco do SinC para cada tipo de suporte (os blocos vem de um DXF gerado pelo
    /// SinC, embutido na DLL: Resources/sinC_suportes.dxf.gz, com visibilidade Implantar/
    /// Retirar/Remanejar/Manter).
    /// </summary>
    public static string SupportBlockName(string tipo) => tipo switch
    {
        ViarioPlacas.SuporteSimples => "Coluna Simples_SVS",
        ViarioPlacas.SuporteDuplo => "Coluna Dupla_SVS",
        ViarioPlacas.SuporteTriplo => "Coluna Tripla_SVS",
        ViarioPlacas.SuporteBraco => "Braço Projetado_SVS",
        ViarioPlacas.SuporteSemiPortico => "Semi Pórtico Simples_SVS",
        ViarioPlacas.SuportePortico => "Pórtico_SVS",
        _ => "Coluna Dupla_SVS"
    };

    /// <summary>Onde a linha de chamada sai do suporte (sistema local do bloco), medido nos desenhos do SinC.</summary>
    public static V2 LdcOffset(string tipo) => tipo switch
    {
        ViarioPlacas.SuporteBraco => new V2(-3.362, -0.165),
        ViarioPlacas.SuporteSemiPortico => new V2(-3.742, -0.387),
        ViarioPlacas.SuportePortico => new V2(2.42, -0.385),
        _ => new V2(0.0, -0.275)
    };

    /// <summary>Altura padrao do suporte no SinC (quando o projeto nao informa).</summary>
    private static string DefaultHeight(string tipo) => tipo is ViarioPlacas.SuporteBraco or ViarioPlacas.SuporteSemiPortico or ViarioPlacas.SuportePortico ? "5,5m" : "2,0m";

    private static string? _supportsDxf;

    /// <summary>Descompacta o DXF dos suportes (recurso da DLL) para %TEMP% uma vez por sessao.</summary>
    private static string? SupportsDxfPath()
    {
        if (_supportsDxf is not null && File.Exists(_supportsDxf))
            return _supportsDxf;
        try
        {
            using Stream? res = typeof(ViarioPlacaSync).Assembly.GetManifestResourceStream("Viario\\sinC_suportes.dxf.gz");
            if (res is null)
                return null;
            string path = Path.Combine(Path.GetTempPath(), "Civil3D2026Plugin", "sinC_suportes.dxf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (System.IO.Compression.GZipStream gz = new(res, System.IO.Compression.CompressionMode.Decompress))
                gz.CopyTo(fs);
            _supportsDxf = path;
            return path;
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Modulo, "Falha ao extrair o DXF dos suportes do SinC", ex);
            return null;
        }
    }

    /// <summary>
    /// Traz do DXF embutido o bloco do suporte (e os demais, de uma vez) para o desenho, com a
    /// definicao dinamica do SinC. Retorna ObjectId.Null se nao conseguir.
    /// </summary>
    private static AcadDb.ObjectId EnsureSupportBlock(AcadDb.Transaction tr, AcadDb.Database db, string tipo)
    {
        string name = SupportBlockName(tipo);
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (bt.Has(name))
            return bt[name];

        string? dxf = SupportsDxfPath();
        if (dxf is null)
            return AcadDb.ObjectId.Null;

        try
        {
            using AcadDb.Database side = new(false, true);
            side.DxfIn(dxf, null);
            AcadDb.ObjectIdCollection ids = new();
            using (AcadDb.Transaction st = side.TransactionManager.StartOpenCloseTransaction())
            {
                AcadDb.BlockTable sbt = (AcadDb.BlockTable)st.GetObject(side.BlockTableId, AcadDb.OpenMode.ForRead);
                foreach (AcadDb.ObjectId id in sbt)
                {
                    AcadDb.BlockTableRecord r = (AcadDb.BlockTableRecord)st.GetObject(id, AcadDb.OpenMode.ForRead);
                    if (r.Name.EndsWith("_SVS", StringComparison.OrdinalIgnoreCase) && !bt.Has(r.Name))
                        ids.Add(id);
                }
            }

            if (ids.Count > 0)
            {
                AcadDb.IdMapping map = new();
                db.WblockCloneObjects(ids, db.BlockTableId, map, AcadDb.DuplicateRecordCloning.Ignore, false);
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Modulo, "Falha ao importar os blocos de suporte do SinC", ex);
            return AcadDb.ObjectId.Null;
        }

        bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        return bt.Has(name) ? bt[name] : AcadDb.ObjectId.Null;
    }

    /// <summary>Estado de visibilidade "Implantar" nos blocos dinamicos do SinC (se existir).</summary>
    private static void SetVisibility(AcadDb.BlockReference br, string contains)
    {
        try
        {
            if (!br.IsDynamicBlock)
                return;
            foreach (AcadDb.DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)
            {
                if (prop.ReadOnly || (prop.PropertyTypeCode != 5 && !prop.PropertyName.StartsWith("Visib", StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (object v in prop.GetAllowedValues())
                {
                    if (v is string sv && sv.Contains(contains, StringComparison.OrdinalIgnoreCase))
                    {
                        prop.Value = sv;
                        return;
                    }
                }
            }
        }
        catch
        {
            // Bloco sem propriedade de visibilidade: fica no estado padrao.
        }
    }
    /// <summary>
    /// Bloco da placa: importa o T-xxx_SV.dwg do SinC como definicao de bloco (se ainda nao
    /// existe no desenho) e acrescenta os 15 atributos nas posicoes que o SinC usa.
    /// Retorna ObjectId.Null quando o arquivo nao existe.
    /// </summary>
    private static AcadDb.ObjectId EnsureSignBlock(AcadDb.Transaction tr, AcadDb.Database db, string blockName, string file, double larguraBloco)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.ObjectId id;
        if (bt.Has(blockName))
        {
            id = bt[blockName];
        }
        else
        {
            if (!File.Exists(file))
                return AcadDb.ObjectId.Null;

            using AcadDb.Database side = new(false, true);
            side.ReadDwgFile(file, AcadDb.FileOpenMode.OpenForReadAndAllShare, true, null);
            side.CloseInput(true);
            id = db.Insert(blockName, side, false);
        }

        AcadDb.BlockTableRecord btr = (AcadDb.BlockTableRecord)tr.GetObject(id, AcadDb.OpenMode.ForRead);
        if (btr.HasAttributeDefinitions)
            return id; // ja preparado (por este modulo ou pelo SinC)

        btr.UpgradeOpen();
        double w = BlockWidth(tr, btr);
        if (w <= 0.0)
            w = larguraBloco > 0.0 ? larguraBloco : 20.0;

        double y = -0.9;
        foreach (string tag in VisibleTags)
        {
            AddAtt(tr, db, btr, tag, new Point3d(w + 1.0, y, 0.0), false);
            y -= 1.8;
        }

        foreach (string tag in InvisibleTags)
            AddAtt(tr, db, btr, tag, new Point3d(-15.0, -5.0, 0.0), true);

        return id;
    }

    private static double BlockWidth(AcadDb.Transaction tr, AcadDb.BlockTableRecord btr)
    {
        double max = 0.0;
        foreach (AcadDb.ObjectId id in btr)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e || e is AcadDb.AttributeDefinition)
                continue;
            try
            {
                AcadDb.Extents3d ext = e.GeometricExtents;
                max = Math.Max(max, ext.MaxPoint.X);
            }
            catch
            {
                // Entidade sem extensao (texto vazio etc.).
            }
        }

        return max;
    }

    private static void AddAtt(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr, string tag, Point3d at, bool invisible, string layer = LayerAtt)
    {
        AcadDb.AttributeDefinition ad = new();
        ad.SetDatabaseDefaults(db);
        ad.Layer = layer;
        ad.Color = AcColors.Color.FromColorIndex(AcColors.ColorMethod.ByLayer, 256);
        ad.Tag = tag;
        ad.Prompt = "";
        ad.TextString = "";
        ad.Height = 1.0;
        ad.TextStyleId = db.Textstyle;
        ad.Invisible = invisible;
        ad.Position = at;
        btr.AppendEntity(ad);
        tr.AddNewlyCreatedDBObject(ad, true);
    }

    // ------------------------------------------------------------------
    // Atualizar vinculo / nomear eixos / remover
    // ------------------------------------------------------------------

    /// <summary>
    /// Recalcula eixo, estaca e lado de todas as placas (depois de editar ou nomear eixos) e
    /// regrava os atributos Eixo, Estaca/KM e Lado. A geometria nao muda.
    /// </summary>
    public static (int Atualizadas, int SemEixo) Relink(Document doc, double maxDist)
    {
        AcadDb.Database db = doc.Database;
        List<AxisInput> axes = ViarioSync.CurrentAxesOnly(db);
        Dictionary<string, string> names = AxisLabels(db, axes);
        int total = 0, semEixo = 0;

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach ((PlacaItem p, AcadDb.BlockReference br) in Scan(tr, db))
        {
            double rot = p.Rotacao;
            if (!ViarioPlacas.Link(p, axes, names, maxDist))
                semEixo++;
            p.Rotacao = rot; // a placa ja esta desenhada: nao gira
            br.UpgradeOpen();
            foreach (AcadDb.ObjectId aid in br.AttributeCollection)
            {
                if (tr.GetObject(aid, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeReference ar)
                    continue;
                string? value = ar.Tag switch
                {
                    "Eixo" => p.AxisName,
                    "Estaca/KM" => p.EstacaKm,
                    "Lado" => p.LadoTexto,
                    "Nort" => "N: " + Coord(p.N),
                    "Est" => "E: " + Coord(p.E),
                    _ => null
                };
                if (value is not null && ar.TextString != value)
                {
                    ar.UpgradeOpen();
                    ar.TextString = value;
                }
            }

            Tag(br, TagPlaca, p.Serialize());
            total++;
        }

        // Suportes: Eixo, Estaca/KM, Lado acompanham a placa de mesmo id.
        Dictionary<string, PlacaItem> byId = Scan(tr, db).GroupBy(x => x.Placa.Id).ToDictionary(g => g.Key, g => g.First().Placa, StringComparer.Ordinal);
        foreach (AcadDb.BlockReference sup in Supports(tr, db))
        {
            ViarioStore.ReadTag(sup, out string id);
            if (!byId.TryGetValue(id, out PlacaItem? p))
                continue;
            sup.UpgradeOpen();
            SetAttribute(tr, sup, "Eixo", p.AxisName);
            SetAttribute(tr, sup, "Estaca/KM", p.EstacaKm);
            SetAttribute(tr, sup, "Lado", p.LadoTexto);
        }

        tr.Commit();
        return (total, semEixo);
    }

    private static List<AcadDb.BlockReference> Supports(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<AcadDb.BlockReference> list = new();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || id.ObjectClass.DxfName != "INSERT")
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.BlockReference br && ViarioStore.ReadTag(br, out _) == TagSuporte)
                list.Add(br);
        }

        return list;
    }

    /// <summary>
    /// Da nome aos eixos a partir das placas amarradas (rota e trecho do projeto). Sem
    /// <paramref name="sobrescrever"/>, so os eixos ainda sem nome. Retorna (chave, nome) aplicados.
    /// </summary>
    public static List<(string Key, string Name)> NameAxes(Document doc, bool sobrescrever)
    {
        AcadDb.Database db = doc.Database;
        List<PlacaItem> placas = Read(db);
        Dictionary<string, string> suggested = ViarioPlacas.SuggestAxisNames(placas);
        List<(string, string)> applied = new();
        if (suggested.Count == 0)
            return applied;

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach ((string key, string name) in suggested)
        {
            if (ViarioStore.EntityByKey(tr, db, key, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e)
                continue;
            if (!sobrescrever && ViarioStore.AxisName(e).Length > 0)
                continue;
            e.UpgradeOpen();
            ViarioStore.SetAxisName(tr, db, e, name);
            applied.Add((key, name));
        }

        tr.Commit();
        ViarioSync.Forget(db);
        return applied;
    }

    /// <summary>Ids escolhidos no painel para o proximo comando (Escalar); null = perguntar/todas.</summary>
    public static HashSet<string>? PendingIds { get; set; }

    /// <summary>
    /// Escala placa, linha de chamada e suporte a partir do ponto de implantacao (escala absoluta,
    /// 1 = padrao do SinC). Com <paramref name="ids"/> nulo, todas. Retorna quantas placas mudaram.
    /// </summary>
    public static int SetScale(Document doc, double escala, HashSet<string>? ids)
    {
        AcadDb.Database db = doc.Database;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (Conjunto c in Conjuntos(tr, db, pecas: true))
        {
            if (ids is not null && !ids.Contains(c.Placa.Id))
                continue;
            if (c.ChamadaDesenho is V2 ch)
                c.Placa.Chamada = ch;
            Place(tr, c, escala);
            n++;
        }

        tr.Commit();
        return n;
    }

    /// <summary>Placa com a linha de chamada e o suporte dela (blocos marcados com o mesmo id).</summary>
    private sealed class Conjunto
    {
        public PlacaItem Placa = null!;
        public AcadDb.BlockReference Ref = null!;
        public AcadDb.BlockReference? Ldc;
        public AcadDb.BlockReference? Suporte;

        /// <summary>Linha de chamada explodida (polilinha/linhas encadeadas da ponta na placa ate a saida).</summary>
        public List<Peca> Pecas = new();
        /// <summary>Saida da chamada explodida (ponta oposta a placa), no desenho.</summary>
        public V2? Saida;

        /// <summary>Ponta da chamada como esta no desenho (origem da placa - saida, local, sem escala).</summary>
        public V2? ChamadaDesenho
        {
            get
            {
                if (Saida is not V2 sd || EscAntes < 1e-9)
                    return null;
                V2 loc = ViarioPlacas.Rotate(new V2(Ref.Position.X, Ref.Position.Y) - sd, -RotAntes * 180.0 / Math.PI) / EscAntes;
                return V2.Dist(loc, PlacaItem.ChamadaPadrao) < 0.01 ? PlacaItem.ChamadaPadrao : loc;
            }
        }
        /// <summary>Rotacao (rad) e escala do bloco da placa antes da mudanca (quadro das pecas).</summary>
        public double RotAntes;
        public double EscAntes = 1.0;
    }

    /// <summary>Pedaco da chamada explodida; <see cref="Fim"/> = vertice que encosta na placa (-1 = nenhum).</summary>
    private sealed class Peca
    {
        public AcadDb.ObjectId Id;
        public int Fim = -1;
    }

    private sealed class Curva
    {
        public AcadDb.ObjectId Id;
        public V2 A, B;
        public bool Ldc;
        public bool Usada;
        public int NumVert;
    }

    private static List<Conjunto> Conjuntos(AcadDb.Transaction tr, AcadDb.Database db, bool pecas = false)
    {
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        Dictionary<string, AcadDb.BlockReference> ldcs = new(StringComparer.Ordinal), sups = new(StringComparer.Ordinal);
        List<Conjunto> list = new();
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || id.ObjectClass.DxfName != "INSERT")
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockReference br)
                continue;
            List<string>? values = ViarioStore.ReadValues(br);
            if (values is null || values.Count < 2)
                continue;
            switch (values[0])
            {
                case TagLdc: ldcs[values[1]] = br; break;
                case TagSuporte: sups[values[1]] = br; break;
                case TagPlaca:
                    PlacaItem? p = PlacaItem.Deserialize(string.Concat(values.Skip(1)));
                    if (p is not null)
                    {
                        p.Escala = Math.Abs(br.ScaleFactors.X) > 1e-9 ? Math.Abs(br.ScaleFactors.X) : 1.0;
                        list.Add(new Conjunto { Placa = p, Ref = br });
                    }

                    break;
            }
        }

        foreach (Conjunto c in list)
        {
            if (ldcs.TryGetValue(c.Placa.Id, out AcadDb.BlockReference? l))
                c.Ldc = l;
            if (sups.TryGetValue(c.Placa.Id, out AcadDb.BlockReference? sp))
                c.Suporte = sp;
            c.RotAntes = c.Ref.Rotation;
            c.EscAntes = c.Placa.Escala;
        }

        if (pecas)
            FindExplodedLeaders(tr, ms, list);
        return list;
    }

    /// <summary>
    /// Acha a linha de chamada explodida de cada placa: polilinha ou linhas (layer sinC_LinhaDeChamada
    /// ou 0) que nascem na saida do suporte (ou no ponto dele); sem isso, a que encosta na origem da
    /// placa. Segue as pecas encadeadas (no maximo 3).
    /// </summary>
    private static void FindExplodedLeaders(AcadDb.Transaction tr, AcadDb.BlockTableRecord ms, List<Conjunto> list)
    {
        List<Curva> curvas = new();
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased)
                continue;
            string dxf = id.ObjectClass.DxfName;
            if (dxf != "LWPOLYLINE" && dxf != "LINE")
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Curve cv)
                continue;
            bool naLdc = string.Equals(cv.Layer, LayerLdc, StringComparison.OrdinalIgnoreCase);
            if (!naLdc && cv.Layer != "0")
                continue;
            string? marca = ViarioStore.ReadTag(cv, out _);
            if (marca is not null && marca != TagLdc)
                continue; // eixos e linhas geradas do Viario nunca entram
            int nv = cv is AcadDb.Polyline pl ? pl.NumberOfVertices : 2;
            if (nv < 2 || nv > 4 || (cv is AcadDb.Polyline { Closed: true }))
                continue;
            curvas.Add(new Curva
            {
                Id = id, A = new V2(cv.StartPoint.X, cv.StartPoint.Y), B = new V2(cv.EndPoint.X, cv.EndPoint.Y), Ldc = naLdc, NumVert = nv
            });
        }

        if (curvas.Count == 0)
            return;

        // 1a passada: ponta da chamada na saida do suporte (onde ela nasce). E o caso comum: a placa pode
        // ter sido movida/escalada e a outra ponta nao encostar mais nela.
        List<(double D, Conjunto C, Curva K, bool NaA)> cand = new();
        foreach (Conjunto c in list)
        {
            double esc = c.EscAntes;
            V2 ldcOff = (c.Placa.SuporteTipo.Length > 0 ? LdcOffset(c.Placa.SuporteTipo) : OffsetLdc) * esc;
            V2 saidaEsperada = c.Suporte is not null
                ? new V2(c.Suporte.Position.X, c.Suporte.Position.Y) + ViarioPlacas.Rotate(ldcOff, c.Suporte.Rotation * 180.0 / Math.PI)
                : c.Placa.Ponto + ViarioPlacas.Rotate(ldcOff, c.Placa.Rotacao);
            double tol = (0.5 + ldcOff.Length / Math.Max(esc, 1e-9)) * esc;
            // a chamada pode ter ficado no ponto de outro tipo de suporte (suporte trocado): vale tambem o proprio ponto
            V2 pontoSup = c.Suporte is not null ? new V2(c.Suporte.Position.X, c.Suporte.Position.Y) : c.Placa.Ponto;
            double Dist(V2 q) => Math.Min(V2.Dist(q, saidaEsperada), V2.Dist(q, pontoSup));
            foreach (Curva k in curvas)
            {
                double da = Dist(k.A), dbb = Dist(k.B);
                if (Math.Min(da, dbb) < tol)
                    cand.Add((Math.Min(da, dbb), c, k, da <= dbb));
            }
        }

        foreach (var (_, c, k, naA) in cand.OrderBy(x => x.D))
        {
            if (k.Usada || c.Saida is not null)
                continue;
            List<(Curva K, bool Frente)> cadeia = Walk(curvas, k, naA, c.EscAntes, out _);
            c.Saida = naA ? k.A : k.B;
            for (int i = 0; i < cadeia.Count; i++)
            {
                (Curva kk, bool frente) = cadeia[i];
                bool ultima = i == cadeia.Count - 1;
                c.Pecas.Add(new Peca { Id = kk.Id, Fim = ultima ? (frente ? kk.NumVert - 1 : 0) : -1 });
            }
        }

        // 2a passada: sem chamada achada pela saida, a que encosta na origem da placa.
        foreach (Conjunto c in list)
        {
            if (c.Saida is not null)
                continue;
            double esc = c.EscAntes;
            V2 origem = new(c.Ref.Position.X, c.Ref.Position.Y);
            Curva? first = null;
            bool firstAtA = false;
            double bestD = double.MaxValue;
            foreach (Curva k in curvas)
            {
                if (k.Usada)
                    continue;
                double tol = (k.Ldc ? 3.0 : 0.3) * esc;
                double da = V2.Dist(k.A, origem), dbb = V2.Dist(k.B, origem);
                double d = Math.Min(da, dbb);
                if (d < tol && d < bestD)
                {
                    bestD = d;
                    first = k;
                    firstAtA = da <= dbb;
                }
            }

            if (first is null)
                continue;

            List<(Curva K, bool Frente)> cadeia = Walk(curvas, first, firstAtA, esc, out V2 saida);
            for (int i = 0; i < cadeia.Count; i++)
                c.Pecas.Add(new Peca { Id = cadeia[i].K.Id, Fim = i == 0 ? (firstAtA ? 0 : first.NumVert - 1) : -1 });
            c.Saida = saida;
        }
    }

    /// <summary>
    /// Segue as pecas encadeadas a partir de <paramref name="first"/> (entrando pela ponta A quando
    /// <paramref name="fromA"/>), no maximo 3; <paramref name="ponta"/> = ponta livre no fim da cadeia.
    /// Frente = a peca e percorrida de A para B.
    /// </summary>
    private static List<(Curva K, bool Frente)> Walk(List<Curva> curvas, Curva first, bool fromA, double esc, out V2 ponta)
    {
        List<(Curva, bool)> cadeia = new() { (first, fromA) };
        first.Usada = true;
        ponta = fromA ? first.B : first.A;
        double junta = Math.Max(0.02 * esc, 1e-4);
        for (int guard = 0; guard < 2; guard++)
        {
            V2 p = ponta;
            Curva? next = curvas.FirstOrDefault(k => !k.Usada && (V2.Dist(k.A, p) < junta || V2.Dist(k.B, p) < junta));
            if (next is null)
                break;
            next.Usada = true;
            bool frente = V2.Dist(next.A, p) < junta;
            cadeia.Add((next, frente));
            ponta = frente ? next.B : next.A;
        }

        return cadeia;
    }

    /// <summary>
    /// Leva as pecas da chamada explodida do quadro antigo (saida, rotacao e escala de antes) para o
    /// novo (giro + translacao + escala, sem deformar) e encosta a ponta na origem nova da placa.
    /// </summary>
    private static void MoveExplodedLeader(AcadDb.Transaction tr, Conjunto c, Point3d saidaNova, double rotNova, double escNova, Point3d fimNovo)
    {
        if (c.Pecas.Count == 0 || c.Saida is not V2 s0)
            return;
        Point3d o = new(s0.X, s0.Y, 0.0);
        Matrix3d m = Matrix3d.Displacement(saidaNova - o)
            * Matrix3d.Rotation(rotNova - c.RotAntes, Vector3d.ZAxis, o)
            * Matrix3d.Scaling(c.EscAntes > 1e-9 ? escNova / c.EscAntes : 1.0, o);
        foreach (Peca pc in c.Pecas)
        {
            if (tr.GetObject(pc.Id, AcadDb.OpenMode.ForWrite, false) is not AcadDb.Curve cv)
                continue;
            if (!m.IsEqualTo(Matrix3d.Identity))
                cv.TransformBy(m);
            if (pc.Fim < 0)
                continue;
            switch (cv)
            {
                case AcadDb.Polyline pl:
                    pl.SetPointAt(pc.Fim, new Point2d(fimNovo.X, fimNovo.Y));
                    break;
                case AcadDb.Line ln when pc.Fim == 0:
                    ln.StartPoint = new Point3d(fimNovo.X, fimNovo.Y, ln.StartPoint.Z);
                    break;
                case AcadDb.Line ln:
                    ln.EndPoint = new Point3d(fimNovo.X, fimNovo.Y, ln.EndPoint.Z);
                    break;
            }
        }
    }

    /// <summary>
    /// Recoloca os tres blocos a partir de E/N, rotacao e escala da placa (como a importacao faz)
    /// e regrava atributos e XData. O suporte fica no ponto; a chamada e a placa, nas posicoes do SinC.
    /// </summary>
    private static void Place(AcadDb.Transaction tr, Conjunto c, double escala)
    {
        PlacaItem p = c.Placa;
        p.Escala = escala;
        double rad = p.Rotacao * Math.PI / 180.0;
        bool comSuporte = c.Suporte is not null && p.SuporteTipo.Length > 0;
        V2 ldcOff = (comSuporte ? LdcOffset(p.SuporteTipo) : OffsetLdc) * escala;
        V2 placaOff = ldcOff + (p.Chamada * escala);

        c.Ref.UpgradeOpen();
        c.Ref.Position = Local(p, placaOff, 1.0);
        c.Ref.ScaleFactors = new Scale3d(escala);
        c.Ref.Rotation = rad;
        ResyncAttributes(tr, c.Ref);
        SetAttribute(tr, c.Ref, "Nort", "N: " + Coord(p.N));
        SetAttribute(tr, c.Ref, "Est", "E: " + Coord(p.E));
        SetAttribute(tr, c.Ref, "Eixo", p.AxisName);
        SetAttribute(tr, c.Ref, "Estaca/KM", p.EstacaKm);
        SetAttribute(tr, c.Ref, "Lado", p.LadoTexto);
        if (p.AreaSinC > 0)
            SetAttribute(tr, c.Ref, "Área", AreaAtributo(p));
        Tag(c.Ref, TagPlaca, p.Serialize());

        MoveExplodedLeader(tr, c, Local(p, ldcOff, 1.0), rad, escala, Local(p, placaOff, 1.0));

        if (c.Ldc is not null && p.ChamadaEsticada)
        {
            // O bloco sinC_LDC nao estica: vira polilinha (como o EXPLODE deixaria), marcada com o id.
            AcadDb.BlockTableRecord owner = (AcadDb.BlockTableRecord)tr.GetObject(c.Ldc.OwnerId, AcadDb.OpenMode.ForWrite);
            AcadDb.Polyline pl = new();
            pl.SetDatabaseDefaults(c.Ldc.Database);
            V2[] loc = { ldcOff, ldcOff + (new V2(0.0, -0.5) * escala), placaOff };
            for (int i = 0; i < loc.Length; i++)
            {
                Point3d w = Local(p, loc[i], 1.0);
                pl.AddVertexAt(i, new Point2d(w.X, w.Y), 0.0, 0.0, 0.0);
            }

            pl.Layer = c.Ldc.Layer;
            pl.Color = c.Ldc.Color;
            owner.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            Tag(pl, TagLdc, p.Id);
            c.Ldc.UpgradeOpen();
            c.Ldc.Erase();
            c.Ldc = null;
        }
        else if (c.Ldc is not null)
        {
            c.Ldc.UpgradeOpen();
            c.Ldc.Position = Local(p, ldcOff, 1.0);
            c.Ldc.ScaleFactors = new Scale3d(escala);
            c.Ldc.Rotation = rad;
        }

        if (c.Suporte is not null)
        {
            c.Suporte.UpgradeOpen();
            c.Suporte.Position = new Point3d(p.E, p.N, 0.0);
            c.Suporte.ScaleFactors = new Scale3d(escala);
            c.Suporte.Rotation = rad;
            ResyncAttributes(tr, c.Suporte);
            SetAttribute(tr, c.Suporte, "Eixo", p.AxisName);
            SetAttribute(tr, c.Suporte, "Estaca/KM", p.EstacaKm);
            SetAttribute(tr, c.Suporte, "Lado", p.LadoTexto);
        }
    }

    /// <summary>
    /// Recalcula E/N e rotacao de cada placa pelo que esta no desenho: o suporte do conjunto (ou o
    /// bloco *_SVS mais proximo), senao a linha de chamada (bloco ou polilinha explodida na layer
    /// sinC_LinhaDeChamada cuja ponta cai na origem da placa). Depois reamarra ao eixo e regrava
    /// atributos. Retorna (recalculadas, sem referencia).
    /// </summary>
    public static (int Ok, int Sem) RecomputeFromDrawing(Document doc, double maxDist, HashSet<string>? ids)
    {
        AcadDb.Database db = doc.Database;
        List<AxisInput> axes = ViarioSync.CurrentAxesOnly(db);
        Dictionary<string, string> names = AxisLabels(db, axes);
        int ok = 0, sem = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);

        // Candidatos soltos: suportes do SinC sem vinculo (as chamadas explodidas vem em Conjuntos).
        List<(V2 P, double Rot)> suportes = new();
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased)
                continue;
            AcadDb.Entity? e = tr.GetObject(id, AcadDb.OpenMode.ForRead, false) as AcadDb.Entity;
            switch (e)
            {
                case AcadDb.BlockReference br when br.Name.EndsWith("_SVS", StringComparison.OrdinalIgnoreCase) || (br.IsDynamicBlock && DynName(tr, br).EndsWith("_SVS", StringComparison.OrdinalIgnoreCase)):
                    suportes.Add((new V2(br.Position.X, br.Position.Y), br.Rotation * 180.0 / Math.PI));
                    break;
            }
        }

        foreach (Conjunto c in Conjuntos(tr, db, pecas: true))
        {
            PlacaItem p = c.Placa;
            if (ids is not null && !ids.Contains(p.Id))
                continue;
            double esc = p.Escala;
            double rot = c.Ref.Rotation * 180.0 / Math.PI;
            V2 origem = new(c.Ref.Position.X, c.Ref.Position.Y);
            V2? ponto = null;

            // Chamada explodida: o comprimento/direcao dela (esticada a mao) vira a ponta da chamada da placa.
            if (c.ChamadaDesenho is V2 chDes)
                p.Chamada = chDes;

            // 1) suporte do conjunto
            if (c.Suporte is not null && !c.Suporte.IsErased)
            {
                ponto = new V2(c.Suporte.Position.X, c.Suporte.Position.Y);
                rot = c.Suporte.Rotation * 180.0 / Math.PI;
            }

            // 2) chamada do conjunto (bloco)
            if (ponto is null && c.Ldc is not null && !c.Ldc.IsErased)
            {
                V2 ldcOff = (p.SuporteTipo.Length > 0 ? LdcOffset(p.SuporteTipo) : OffsetLdc) * esc;
                rot = c.Ldc.Rotation * 180.0 / Math.PI;
                ponto = new V2(c.Ldc.Position.X, c.Ldc.Position.Y) - ViarioPlacas.Rotate(ldcOff, rot);
            }

            // 3) chamada explodida: a saida dela (ponta oposta a placa) da o ponto; a rotacao e a da placa
            if (ponto is null && c.Saida is V2 saida)
            {
                V2 ldcOff = (p.SuporteTipo.Length > 0 ? LdcOffset(p.SuporteTipo) : OffsetLdc) * esc;
                ponto = saida - ViarioPlacas.Rotate(ldcOff, rot);
            }

            // 4) suporte solto mais proximo da origem da placa
            if (ponto is null && suportes.Count > 0)
            {
                (V2 P, double Rot) near = suportes.OrderBy(sp => V2.Dist(sp.P, origem)).First();
                if (V2.Dist(near.P, origem) < 30.0 * esc)
                {
                    ponto = near.P;
                    rot = near.Rot;
                }
            }

            if (ponto is null)
            {
                sem++;
                continue;
            }

            p.E = ponto.Value.X;
            p.N = ponto.Value.Y;
            p.Rotacao = rot;
            ViarioPlacas.Link(p, axes, names, maxDist);
            p.Rotacao = rot; // Link recalcula a rotacao pela via; aqui vale a do desenho
            Place(tr, c, esc);
            ok++;
        }

        tr.Commit();
        return (ok, sem);
    }

    private static string DynName(AcadDb.Transaction tr, AcadDb.BlockReference br)
    {
        try
        {
            return ((AcadDb.BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, AcadDb.OpenMode.ForRead)).Name;
        }
        catch
        {
            return br.Name;
        }
    }

    /// <summary>
    /// Troca a geometria do bloco T-xxx_SV pela do T-xxx_SV.dwg atual da pasta (placas corrigidas
    /// no SinC). Atributos ficam; os visiveis seguem a largura nova; dimensoes/area das placas
    /// sao regravadas. Retorna (blocos trocados, sem arquivo).
    /// </summary>
    public static (int Trocados, List<string> SemArquivo) ReplaceBlocks(Document doc, string pasta, HashSet<string>? codigos)
    {
        AcadDb.Database db = doc.Database;
        int trocados = 0;
        List<string> semArquivo = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        List<Conjunto> conjuntos = Conjuntos(tr, db);
        Dictionary<string, (double W, double H)> novos = new(StringComparer.OrdinalIgnoreCase);

        foreach (string codigo in conjuntos.Select(c => c.Placa.Codigo).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (codigos is not null && !codigos.Contains(codigo))
                continue;
            string blockName = codigo + "_SV";
            string file = Path.Combine(pasta, blockName + ".dwg");
            if (!bt.Has(blockName) || !File.Exists(file))
            {
                semArquivo.Add(codigo);
                continue;
            }

            // Importa o DWG novo como bloco temporario e copia as entidades para a definicao existente.
            string tempName = blockName + "$NOVO";
            AcadDb.ObjectId tempId;
            using (AcadDb.Database side = new(false, true))
            {
                side.ReadDwgFile(file, AcadDb.FileOpenMode.OpenForReadAndAllShare, true, null);
                side.CloseInput(true);
                tempId = db.Insert(tempName, side, false);
            }

            AcadDb.BlockTableRecord btr = (AcadDb.BlockTableRecord)tr.GetObject(bt[blockName], AcadDb.OpenMode.ForWrite);
            foreach (AcadDb.ObjectId id in btr.Cast<AcadDb.ObjectId>().ToList())
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is AcadDb.AttributeDefinition)
                    continue;
                tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
            }

            AcadDb.BlockTableRecord temp = (AcadDb.BlockTableRecord)tr.GetObject(tempId, AcadDb.OpenMode.ForWrite);
            AcadDb.ObjectIdCollection ids = new();
            foreach (AcadDb.ObjectId id in temp)
                ids.Add(id);
            AcadDb.IdMapping map = new();
            db.DeepCloneObjects(ids, btr.ObjectId, map, false);
            temp.Erase();

            double w = BlockWidth(tr, btr), h = BlockHeight(tr, btr);
            double y = -0.9;
            foreach (AcadDb.ObjectId id in btr)
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeDefinition ad || ad.Invisible)
                    continue;
                ad.UpgradeOpen();
                ad.Position = new Point3d(w + 1.0, y, 0.0);
                y -= 1.8;
            }

            novos[codigo] = (w / FatorSinC, h / FatorSinC);
            trocados++;
        }

        // Placas desse codigo: dimensoes/area novas e atributos reposicionados.
        foreach (Conjunto c in conjuntos)
        {
            if (!novos.TryGetValue(c.Placa.Codigo, out (double W, double H) dim))
                continue;
            if (dim.W > 0.1 && dim.H > 0.1)
            {
                c.Placa.LarguraSinC = Math.Round(dim.W, 3);
                c.Placa.AlturaSinC = Math.Round(dim.H, 3);
            }

            c.Ref.UpgradeOpen();
            c.Ref.RecordGraphicsModified(true);
            ResyncAttributes(tr, c.Ref);
            SetAttribute(tr, c.Ref, "Dimensão", c.Placa.DimensaoSinC);
            SetAttribute(tr, c.Ref, "Área", AreaAtributo(c.Placa));
            Tag(c.Ref, TagPlaca, c.Placa.Serialize());
        }

        tr.Commit();
        lock (ShapeCache)
        {
            foreach (string codigo in novos.Keys)
                ShapeCache.Remove(codigo + "_SV");
        }

        lock (ImageCache)
        {
            foreach (string codigo in novos.Keys)
                ImageCache.Remove(codigo);
        }

        ViarioPanel.InvalidateSignCaches(novos.Keys);
        return (trocados, semArquivo);
    }

    private static double BlockHeight(AcadDb.Transaction tr, AcadDb.BlockTableRecord btr)
    {
        double min = 0.0;
        foreach (AcadDb.ObjectId id in btr)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e || e is AcadDb.AttributeDefinition)
                continue;
            try
            {
                min = Math.Min(min, e.GeometricExtents.MinPoint.Y);
            }
            catch
            {
                // sem extensao
            }
        }

        return -min;
    }

    /// <summary>
    /// Conjunto mais proximo de um ponto (placa, chamada ou suporte a ate <paramref name="maxDist"/> m), pelo id.
    /// </summary>
    public static string? NearestId(AcadDb.Database db, V2 pt, double maxDist)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        string? best = null;
        double bestD = maxDist;
        foreach (Conjunto c in Conjuntos(tr, db))
        {
            List<V2> pts = new() { c.Placa.Ponto, new V2(c.Ref.Position.X, c.Ref.Position.Y) };
            // centro da placa
            (_, List<Seg> rect, _) = Footprint(c.Placa);
            pts.Add((rect[0].A + rect[2].A) / 2.0);
            if (c.Ldc is not null)
                pts.Add(new V2(c.Ldc.Position.X, c.Ldc.Position.Y));
            double d = pts.Min(q => V2.Dist(q, pt));
            if (d < bestD)
            {
                bestD = d;
                best = c.Placa.Id;
            }
        }

        return best;
    }

    /// <summary>Aplica uma mudanca (rotacao, ponto, alinhamento) a um conjunto e recoloca os blocos.</summary>
    public static bool Adjust(Document doc, string id, Action<PlacaItem, List<AxisInput>, Dictionary<string, string>> change)
    {
        AcadDb.Database db = doc.Database;
        List<AxisInput> axes = ViarioSync.CurrentAxesOnly(db);
        Dictionary<string, string> names = AxisLabels(db, axes);
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        Conjunto? c = Conjuntos(tr, db, pecas: true).FirstOrDefault(x => x.Placa.Id == id);
        if (c is null)
            return false;
        if (c.ChamadaDesenho is V2 ch)
            c.Placa.Chamada = ch; // placa movida/esticada a mao no CAD: vale o que esta no desenho
        change(c.Placa, axes, names);
        Place(tr, c, c.Placa.Escala);
        tr.Commit();
        return true;
    }

    /// <summary>Reposiciona os atributos depois de mudar escala/rotacao do bloco (texto e mantido).</summary>
    private static void ResyncAttributes(AcadDb.Transaction tr, AcadDb.BlockReference br)
    {
        AcadDb.BlockTableRecord def = (AcadDb.BlockTableRecord)tr.GetObject(br.BlockTableRecord, AcadDb.OpenMode.ForRead);
        Dictionary<string, AcadDb.AttributeDefinition> defs = new(StringComparer.OrdinalIgnoreCase);
        foreach (AcadDb.ObjectId id in def)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is AcadDb.AttributeDefinition ad && !ad.Constant)
                defs[ad.Tag] = ad;
        }

        foreach (AcadDb.ObjectId aid in br.AttributeCollection)
        {
            if (tr.GetObject(aid, AcadDb.OpenMode.ForRead) is not AcadDb.AttributeReference ar || !defs.TryGetValue(ar.Tag, out AcadDb.AttributeDefinition? ad))
                continue;
            string text = ar.TextString;
            ar.UpgradeOpen();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            ar.TextString = text;
            try
            {
                ar.AdjustAlignment(br.Database);
            }
            catch
            {
                // cosmetico
            }
        }
    }

    /// <summary>Apaga as placas e linhas de chamada inseridas por este modulo (as definicoes de bloco ficam).</summary>
    public static int Remove(Document doc)
    {
        AcadDb.Database db = doc.Database;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
        foreach (AcadDb.ObjectId id in ms)
        {
            if (id.IsErased || (id.ObjectClass.DxfName != "INSERT" && id.ObjectClass.DxfName != "LWPOLYLINE"))
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity br)
                continue;
            string? tag = ViarioStore.ReadTag(br, out _);
            if (tag != TagPlaca && tag != TagLdc && tag != TagSuporte)
                continue;
            br.UpgradeOpen();
            br.Erase();
            if (tag == TagPlaca)
                n++;
        }

        tr.Commit();
        return n;
    }

    /// <summary>Desenho da placa para o painel: laços (em unidades do bloco, Y para baixo negativo) e cor RGB de cada hachura.</summary>
    /// <param name="Hatches">Lacos de cada hachura (simplificados), cor e se e detalhe (letras, simbolos pequenos).</param>
    internal sealed record SignShape(List<(List<List<V2>> Loops, byte R, byte G, byte B, bool Detail)> Hatches, double Width, double Height);

    private static readonly Dictionary<string, SignShape> ShapeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Geometria do bloco T-xxx_SV (hachuras solidas) para desenhar a placa no monitor do painel.</summary>
    public static SignShape? Shape(AcadDb.Database db, string codigo)
    {
        string blockName = codigo + "_SV";
        lock (ShapeCache)
        {
            if (ShapeCache.TryGetValue(blockName, out SignShape? cached))
                return cached;
        }

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (!bt.Has(blockName))
            return null;

        AcadDb.BlockTableRecord btr = (AcadDb.BlockTableRecord)tr.GetObject(bt[blockName], AcadDb.OpenMode.ForRead);
        List<(List<List<V2>>, byte, byte, byte, double)> raw = new();
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (AcadDb.ObjectId id in btr)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.Hatch h)
                continue;
            List<List<V2>> loops = new();
            for (int i = 0; i < h.NumberOfLoops; i++)
            {
                List<V2> pts = new();
                try
                {
                    AcadDb.HatchLoop loop = h.GetLoopAt(i);
                    if (loop.IsPolyline)
                    {
                        AcadDb.BulgeVertexCollection bv = loop.Polyline;
                        for (int k = 0; k < bv.Count; k++)
                        {
                            AcadDb.BulgeVertex a = bv[k];
                            AcadDb.BulgeVertex b = bv[(k + 1) % bv.Count];
                            pts.AddRange(BulgePoints(new V2(a.Vertex.X, a.Vertex.Y), new V2(b.Vertex.X, b.Vertex.Y), a.Bulge));
                        }
                    }
                    else
                    {
                        foreach (Curve2d curve in loop.Curves)
                        {
                            Point2d[] sample = curve.GetSamplePoints(curve is LineSegment2d ? 2 : 10);
                            foreach (Point2d q in sample)
                                pts.Add(new V2(q.X, q.Y));
                        }
                    }
                }
                catch
                {
                    // Laco que a API nao devolve: pula.
                }

                pts = Simplify(pts, 0.008);
                if (pts.Count < 3)
                    continue;
                loops.Add(pts);
                foreach (V2 q in pts)
                {
                    minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                    minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
                }
            }

            if (loops.Count == 0)
                continue;
            System.Drawing.Color c = h.Color.ColorValue;
            double hx0 = loops.Min(l => l.Min(v => v.X)), hx1 = loops.Max(l => l.Max(v => v.X));
            double hy0 = loops.Min(l => l.Min(v => v.Y)), hy1 = loops.Max(l => l.Max(v => v.Y));
            raw.Add((loops, c.R, c.G, c.B, (hx1 - hx0) * (hy1 - hy0)));
        }

        if (raw.Count == 0)
            return null;
        double total = Math.Max(maxX - minX, 1e-6) * Math.Max(maxY - minY, 1e-6);
        List<(List<List<V2>>, byte, byte, byte, bool)> hatches = raw
            .Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5 < 0.03 * total || x.Item1.Count > 8))
            .ToList();
        SignShape shape = new(hatches, Math.Max(maxX - minX, 1e-6), Math.Max(maxY - minY, 1e-6));
        lock (ShapeCache)
            ShapeCache[blockName] = shape;
        return shape;
    }

    /// <summary>Tira pontos a menos de <paramref name="tol"/> do anterior e os colineares (Douglas-Peucker simples).</summary>
    private static List<V2> Simplify(List<V2> pts, double tol)
    {
        if (pts.Count < 4)
            return pts;
        List<V2> outp = new() { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            V2 last = outp[^1];
            if (V2.Dist(pts[i], last) < tol)
                continue;
            // colinear com o anterior e o proximo: pula
            if (outp.Count >= 2 && i + 1 < pts.Count)
            {
                V2 a = outp[^2], b = pts[i], n = pts[i + 1];
                double cross = Math.Abs(V2.Cross(b - a, n - a));
                if (cross < tol * V2.Dist(a, n) * 0.1)
                    continue;
            }

            outp.Add(pts[i]);
        }

        return outp;
    }

    private static IEnumerable<V2> BulgePoints(V2 a, V2 b, double bulge)
    {
        yield return a;
        if (Math.Abs(bulge) < 1e-9)
            yield break;
        double theta = 4.0 * Math.Atan(bulge);
        double chord = V2.Dist(a, b);
        if (chord < 1e-9)
            yield break;
        double r = chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0));
        V2 mid = (a + b) / 2.0;
        V2 n = (b - a).Unit().Left;
        double d = Math.Sqrt(Math.Max(0.0, (r * r) - (chord * chord / 4.0))) * (bulge > 0 ? 1 : -1);
        V2 center = mid + (n * d);
        double a0 = (a - center).Angle;
        int n2 = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 12)));
        for (int k = 1; k < n2; k++)
        {
            double ang = a0 + (theta * k / n2);
            yield return center + (V2.FromAngle(ang) * r);
        }
    }

    /// <summary>
    /// Geometria da placa em planta, como inserida (WCS): linha de chamada (3 pontos) e o
    /// retangulo do bloco (escala 5 x escala da placa), girados pela rotacao a partir do ponto.
    /// </summary>
    public static (List<Seg> Ldc, List<Seg> Placa, V2 Suporte) Footprint(PlacaItem p)
    {
        bool comSuporte = p.SuporteTipo.Length > 0;
        V2 ldcOff = (comSuporte ? LdcOffset(p.SuporteTipo) : OffsetLdc) * p.Escala;
        V2 origem = ldcOff + (p.Chamada * p.Escala);
        double w = p.LarguraSinC * FatorSinC * p.Escala, h = p.AlturaSinC * FatorSinC * p.Escala;
        V2 W(V2 local) => p.Ponto + ViarioPlacas.Rotate(local, p.Rotacao);

        V2 l0 = W(ldcOff), l1 = W(ldcOff + new V2(0.0, -0.5 * p.Escala)), l2 = W(origem);
        V2 c0 = W(origem), c1 = W(origem + new V2(w, 0.0)), c2 = W(origem + new V2(w, -h)), c3 = W(origem + new V2(0.0, -h));
        return (new List<Seg> { Seg.Line(l0, l1), Seg.Line(l1, l2) },
                new List<Seg> { Seg.Line(c0, c1), Seg.Line(c1, c2), Seg.Line(c2, c3), Seg.Line(c3, c0) },
                p.Ponto);
    }

    private static readonly Dictionary<string, string?> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Imagem da placa gerada pelo SinC: T-xxx.jpg/.png ao lado dos arquivos ou IMG.png dentro do
    /// T-xxx.pla (extraida para %TEMP%). Null quando nao ha pasta do SinC nesta maquina.
    /// </summary>
    public static string? SignImagePath(string codigo, string? pasta = null)
    {
        lock (ImageCache)
        {
            if (ImageCache.TryGetValue(codigo, out string? cached) && (cached is null || File.Exists(cached)))
                return cached;
        }

        string? found = null;
        foreach (string dir in new[] { pasta ?? "", Remembered("pastaSinC"), @"C:\sinC\Local\Placas\Placas 2D" }.Where(d => d.Length > 0 && Directory.Exists(d)).Distinct())
        {
            foreach (string ext in new[] { ".png", ".jpg", ".jpeg" })
            {
                string f = Path.Combine(dir, codigo + ext);
                if (File.Exists(f))
                {
                    found = f;
                    break;
                }
            }

            if (found is not null)
                break;

            string pla = Path.Combine(dir, codigo + ".pla");
            if (!File.Exists(pla))
                continue;
            try
            {
                string outp = Path.Combine(Path.GetTempPath(), "Civil3D2026Plugin", "placas", codigo + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(outp)!);
                using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(pla);
                System.IO.Compression.ZipArchiveEntry? entry = zip.GetEntry("IMG.png") ?? zip.GetEntry("THUMB.png");
                if (entry is not null)
                {
                    using (Stream src = entry.Open())
                    using (FileStream dst = new(outp, FileMode.Create, FileAccess.Write, FileShare.None))
                        src.CopyTo(dst);
                    found = outp;
                    break;
                }
            }
            catch
            {
                // .pla sem imagem ou bloqueado
            }
        }

        lock (ImageCache)
            ImageCache[codigo] = found;
        return found;
    }

    /// <summary>Zoom numa placa (caixa de 60 m em volta do ponto).</summary>
    public static Box2 ZoomBox(PlacaItem p) => new(p.E - 40, p.N - 40, p.E + 40, p.N + 40);

    // ------------------------------------------------------------------
    // Caminhos lembrados (por usuario) e dialogos de arquivo/pasta
    // ------------------------------------------------------------------

    private static readonly string PathsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "viario-placas.txt");

    /// <summary>Ultimo caminho usado para uma chave (pastaSinC, catalogo, kmz), ou "".</summary>
    public static string Remembered(string key)
    {
        try
        {
            if (!File.Exists(PathsFile))
                return "";
            foreach (string line in File.ReadAllLines(PathsFile))
            {
                if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    return line[(key.Length + 1)..].Trim();
            }
        }
        catch
        {
            // conveniencia
        }

        return "";
    }

    public static void Remember(string key, string value)
    {
        try
        {
            Dictionary<string, string> all = new(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(PathsFile))
            {
                foreach (string line in File.ReadAllLines(PathsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        all[line[..eq]] = line[(eq + 1)..];
                }
            }

            all[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(PathsFile)!);
            File.WriteAllLines(PathsFile, all.Select(kv => kv.Key + "=" + kv.Value));
        }
        catch
        {
            // conveniencia
        }
    }

    /// <summary>Dialogo de pasta (Windows), comecando na ultima usada. Null = cancelou.</summary>
    public static string? AskFolder(string title, string key, string? suggestion = null)
    {
        string initial = suggestion is not null && Directory.Exists(suggestion) ? suggestion : Remembered(key);
        Microsoft.Win32.OpenFolderDialog dlg = new() { Title = title, Multiselect = false };
        if (initial.Length > 0 && Directory.Exists(initial))
            dlg.InitialDirectory = initial;
        if (dlg.ShowDialog() != true)
            return null;
        string folder = dlg.FolderName;
        if (!Directory.Exists(folder))
            return null;
        Remember(key, folder);
        return folder;
    }

    /// <summary>Dialogo de arquivo, comecando na pasta do ultimo usado. Null = cancelou.</summary>
    public static string? AskFile(Editor ed, string title, string filter, string key)
    {
        PromptOpenFileOptions fo = new("\n" + title) { Filter = filter };
        string last = Remembered(key);
        try
        {
            if (last.Length > 0)
            {
                string? dir = Path.GetDirectoryName(last);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    fo.InitialDirectory = dir;
                if (File.Exists(last))
                    fo.InitialFileName = Path.GetFileName(last);
            }
        }
        catch
        {
            // caminho invalido
        }

        PromptFileNameResult fr = ed.GetFileNameForOpen(fo);
        if (fr.Status != PromptStatus.OK)
            return null;
        Remember(key, fr.StringResult);
        return fr.StringResult;
    }

    // ------------------------------------------------------------------
    // Comando C3DVIARIOPLACAS
    // ------------------------------------------------------------------

    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        int existing = Count(doc.Database);
        PromptKeywordOptions options = new(
            $"\nPlacas verticais ({existing} no desenho) [Importar/PAsta/SUbstituir/ATualizar/COordenadas/ARrumar/NOmear/EScalar/Listar/Remover/Painel] <Importar>: ",
            "Importar PAsta SUbstituir ATualizar COordenadas ARrumar NOmear EScalar Listar Remover Painel")
        {
            AllowNone = true
        };

        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        switch (r.Status == PromptStatus.OK ? r.StringResult : "Importar")
        {
            case "PAsta": ImportFolderCommand(doc); break;
            case "EScalar": ScaleCommand(doc); break;
            case "SUbstituir": ReplaceCommand(doc); break;
            case "COordenadas": RecomputeCommand(doc); break;
            case "ARrumar": ArrangeCommand(doc); break;
            case "ATualizar": RelinkCommand(doc); break;
            case "NOmear": NameCommand(doc); break;
            case "Listar": ListCommand(doc); break;
            case "Remover": RemoveCommand(doc); break;
            case "Painel": ViarioPanel.ShowPanel("PLACAS"); break;
            default: ImportCommand(doc); break;
        }
    }

    private static void ImportCommand(Document doc)
    {
        Editor ed = doc.Editor;
        string? catalogo = AskFile(ed, "Catalogo das placas (JSON ou CSV gerado da planilha do projeto)", "Catalogo de placas (*.json;*.csv)|*.json;*.csv|Todos (*.*)|*.*", "catalogo");
        if (catalogo is null)
            return;

        PlacaCatalog cat;
        try
        {
            cat = ViarioPlacas.Load(catalogo);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n  Nao foi possivel ler o catalogo: " + ex.Message);
            PluginLog.Erro(Modulo, "Falha ao ler " + catalogo, ex);
            return;
        }

        if (cat.Placas.Count == 0)
        {
            ed.WriteMessage("\n  O catalogo nao tem placas.");
            return;
        }

        // Pasta dos T-xxx_SV.dwg: a do catalogo, se existir nesta maquina; senao a ultima usada (dialogo).
        string? pasta = Directory.Exists(cat.PastaSinC) && File.Exists(Path.Combine(cat.PastaSinC, cat.Placas[0].Codigo + "_SV.dwg")) ? cat.PastaSinC : null;
        pasta ??= AskFolder("Pasta do SinC com os T-xxx_SV.dwg", "pastaSinC", Path.GetDirectoryName(catalogo));
        if (pasta is null)
            return;
        ed.WriteMessage("\n  Pasta do SinC: " + pasta);

        Opcoes o = new();
        PromptDoubleOptions dmax = new($"\nDistancia maxima do ponto ao eixo para amarrar (m) <{o.DistanciaMaxima.ToString("0", CultureInfo.InvariantCulture)}>: ")
        {
            AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = o.DistanciaMaxima, UseDefaultValue = true
        };
        PromptDoubleResult dr = ed.GetDouble(dmax);
        if (dr.Status == PromptStatus.Cancel)
            return;
        if (dr.Status == PromptStatus.OK)
            o.DistanciaMaxima = dr.Value;

        PromptDoubleOptions esc = new("\nEscala da placa (como no SinC) <1>: ")
        {
            AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = 1.0, UseDefaultValue = true
        };
        PromptDoubleResult er = ed.GetDouble(esc);
        if (er.Status == PromptStatus.Cancel)
            return;
        if (er.Status == PromptStatus.OK)
            o.Escala = er.Value;

        PromptKeywordOptions sk = new("\nInserir suportes pela area da placa (ate 2 m2 coluna simples, ate 6 m2 dupla, acima semi-portico/portico) [Sim/Nao] <Sim>: ", "Sim Nao") { AllowNone = true };
        PromptResult sr = ed.GetKeywords(sk);
        if (sr.Status == PromptStatus.Cancel)
            return;
        o.Suportes = sr.Status != PromptStatus.OK || sr.StringResult == "Sim";

        RunImport(doc, cat, pasta, o);
    }

    private static void RunImport(Document doc, PlacaCatalog cat, string pasta, Opcoes o)
    {
        Editor ed = doc.Editor;
        (int inseridas, int semEixo, List<string> semArquivo) = Import(doc, cat, pasta, o);
        ed.WriteMessage($"\n  {inseridas} placa(s) inserida(s) de {cat.Placas.Count}; {semEixo} sem eixo a menos de {o.DistanciaMaxima:0} m (ficam sem Eixo/Estaca/Lado)." +
                        (UltimasRepetidas > 0 ? $" {UltimasRepetidas} ja estavam no desenho e foram puladas (Remover antes para reinserir)." : ""));
        if (semArquivo.Count > 0)
            ed.WriteMessage($"\n  Sem arquivo _SV.dwg na pasta ({semArquivo.Count}): {string.Join(", ", semArquivo.Take(20))}{(semArquivo.Count > 20 ? "..." : "")}");
        ed.WriteMessage("\n  Atributos no padrao do SinC (Codigo, Situacao, Nort, Est, Area, Dimensao, Eixo, Estaca/KM, Lado...). " +
                        (o.Suportes ? "Suportes inseridos pela area da placa (Handle_Suporte ligado). " : "Suporte: inserir no SinC. ") +
                        "Use NOmear para dar aos eixos o nome da rota/trecho das placas.");
        ViarioPanel.NotifyChanged(doc);
    }

    /// <summary>
    /// Importa direto da pasta do SinC (arquivos .pla + T-xxx_SV.dwg): as coordenadas e os dados
    /// do projeto vem de um catalogo JSON/CSV opcional, casado pelo codigo da placa.
    /// </summary>
    private static void ImportFolderCommand(Document doc)
    {
        Editor ed = doc.Editor;
        string? pasta = AskFolder("Pasta do SinC com os .pla e T-xxx_SV.dwg", "pastaSinC", @"C:\sinC\Local\Placas\Placas 2D");
        if (pasta is null)
            return;
        ed.WriteMessage("\n  Pasta: " + pasta);

        List<string> avisos = new();
        PlacaCatalog cat;
        try
        {
            cat = ViarioPlacas.LoadSinCFolder(pasta, avisos);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n  Nao foi possivel ler a pasta: " + ex.Message);
            return;
        }

        foreach (string a in avisos.Take(10))
            ed.WriteMessage("\n  aviso: " + a);
        int semCoord = cat.Placas.Count(p => Math.Abs(p.E) < 1.0 && Math.Abs(p.N) < 1.0);
        ed.WriteMessage($"\n  {cat.Placas.Count} arquivo(s) .pla lidos; {semCoord} sem coordenada na descricao.");
        if (cat.Placas.Count == 0)
            return;

        // Catalogo do projeto para completar coordenadas e dados (opcional).
        string? guess = null;
        foreach (string dir in new[] { Path.Combine(pasta, "_PROJETO"), pasta })
        {
            if (!Directory.Exists(dir))
                continue;
            string[] jsons = Directory.GetFiles(dir, "*.json");
            if (jsons.Length == 1)
            {
                guess = jsons[0];
                break;
            }
        }

        PromptKeywordOptions ck = new($"\nCompletar com o catalogo do projeto (coordenadas, trecho, suporte){(guess is null ? "" : " [" + Path.GetFileName(guess) + "]")} [Sim/Nao] <{(guess is null ? "Nao" : "Sim")}>: ", "Sim Nao") { AllowNone = true };
        PromptResult cr = ed.GetKeywords(ck);
        if (cr.Status == PromptStatus.Cancel)
            return;
        bool completar = cr.Status == PromptStatus.OK ? cr.StringResult == "Sim" : guess is not null;
        if (completar)
        {
            string? file = guess;
            if (file is null)
            {
                file = AskFile(ed, "Catalogo do projeto (JSON ou CSV)", "Catalogo de placas (*.json;*.csv)|*.json;*.csv|Todos (*.*)|*.*", "catalogo");
                if (file is null)
                    return;
            }

            try
            {
                int n = ViarioPlacas.Merge(cat, ViarioPlacas.Load(file!));
                ed.WriteMessage($"\n  {n} placa(s) completada(s) com {Path.GetFileName(file)}.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n  Nao foi possivel ler o catalogo: " + ex.Message);
                return;
            }
        }

        int ainda = cat.Placas.Count(p => Math.Abs(p.E) < 1.0 && Math.Abs(p.N) < 1.0);
        if (ainda > 0)
        {
            ed.WriteMessage($"\n  {ainda} placa(s) sem coordenada ficam de fora: " +
                            string.Join(", ", cat.Placas.Where(p => Math.Abs(p.E) < 1.0 && Math.Abs(p.N) < 1.0).Select(p => p.Codigo).Take(15)));
            cat.Placas.RemoveAll(p => Math.Abs(p.E) < 1.0 && Math.Abs(p.N) < 1.0);
        }

        Opcoes o = new();
        PromptKeywordOptions sk = new("\nInserir suportes pela area da placa [Sim/Nao] <Sim>: ", "Sim Nao") { AllowNone = true };
        PromptResult sr = ed.GetKeywords(sk);
        if (sr.Status == PromptStatus.Cancel)
            return;
        o.Suportes = sr.Status != PromptStatus.OK || sr.StringResult == "Sim";
        RunImport(doc, cat, pasta, o);
    }

    private static void ReplaceCommand(Document doc)
    {
        Editor ed = doc.Editor;
        HashSet<string>? codigos = PendingIds is null ? null : CodigosDe(doc.Database, PendingIds);
        PendingIds = null;
        string? pasta = AskFolder("Pasta com os T-xxx_SV.dwg corrigidos", "pastaSinC");
        if (pasta is null)
            return;
        (int trocados, List<string> sem) = ReplaceBlocks(doc, pasta, codigos);
        ed.WriteMessage($"\n  {trocados} bloco(s) de placa substituido(s) pela versao da pasta; as placas no desenho ja mostram o desenho novo.");
        if (sem.Count > 0)
            ed.WriteMessage($"\n  Sem arquivo na pasta ({sem.Count}): {string.Join(", ", sem.Take(20))}{(sem.Count > 20 ? "..." : "")}");
        ed.Regen();
        ViarioPanel.NotifyChanged(doc);
    }

    private static HashSet<string> CodigosDe(AcadDb.Database db, HashSet<string> ids)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlacaItem p in Read(db))
        {
            if (ids.Contains(p.Id))
                set.Add(p.Codigo);
        }

        return set;
    }

    private static void RecomputeCommand(Document doc)
    {
        Editor ed = doc.Editor;
        HashSet<string>? ids = PendingIds;
        PendingIds = null;
        Opcoes o = new();
        PromptDoubleOptions dmax = new($"\nDistancia maxima do ponto ao eixo (m) <{o.DistanciaMaxima:0}>: ")
        {
            AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = o.DistanciaMaxima, UseDefaultValue = true
        };
        PromptDoubleResult dr = ed.GetDouble(dmax);
        if (dr.Status == PromptStatus.Cancel)
            return;
        (int ok, int sem) = RecomputeFromDrawing(doc, dr.Status == PromptStatus.OK ? dr.Value : o.DistanciaMaxima, ids);
        ed.WriteMessage($"\n  {ok} placa(s) com coordenadas recalculadas pelo suporte/linha de chamada do desenho; {sem} sem referencia (ficaram como estavam).");
        ViarioPanel.NotifyChanged(doc);
    }

    /// <summary>Clique perto do conjunto e escolha: Rotacionar, Mover, Alinhar com a via, Girar 180, Lado.</summary>
    private static void ArrangeCommand(Document doc)
    {
        Editor ed = doc.Editor;
        string? id = PendingIds?.FirstOrDefault();
        PendingIds = null;
        while (true)
        {
            if (id is null)
            {
                PromptPointOptions po = new("\nClique perto da placa, da linha de chamada ou do suporte (Enter sai): ") { AllowNone = true };
                PromptPointResult pr = ed.GetPoint(po);
                if (pr.Status != PromptStatus.OK)
                    return;
                Point3d w = pr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                id = NearestId(doc.Database, new V2(w.X, w.Y), 40.0);
                if (id is null)
                {
                    ed.WriteMessage("\n  Nenhuma placa deste modulo a menos de 40 m.");
                    continue;
                }
            }

            PlacaItem? atual = Read(doc.Database).FirstOrDefault(p => p.Id == id);
            if (atual is null)
                return;
            ed.WriteMessage($"\n  Placa {atual.Id} ({atual.Codigo}) - rot. {atual.Rotacao:0.0} graus, {(atual.Vinculada ? atual.AxisName + " est. " + atual.EstacaTexto + " " + atual.LadoTexto : "sem eixo")}");
            PromptKeywordOptions ko = new("\nArrumar [Rotacionar/Mover/Esticar/Padrao/Alinhar/Girar180/Lado/Outra/Sair] <Sair>: ", "Rotacionar Mover Esticar Padrao Alinhar Girar180 Lado Outra Sair") { AllowNone = true };
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK || kr.StringResult == "Sair")
                break;

            string idAtual = id;
            switch (kr.StringResult)
            {
                case "Rotacionar":
                {
                    PromptAngleOptions ao = new("\nNova direcao do bloco (eixo X da placa; arraste a partir do ponto de implantacao): ")
                    {
                        UseBasePoint = true, BasePoint = new Point3d(atual.E, atual.N, 0.0), UseDashedLine = true
                    };
                    PromptDoubleResult ar = ed.GetAngle(ao);
                    if (ar.Status != PromptStatus.OK)
                        break;
                    double deg = ar.Value * 180.0 / Math.PI;
                    Adjust(doc, idAtual, (p, _, _) => p.Rotacao = ((deg % 360.0) + 360.0) % 360.0);
                    break;
                }

                case "Mover":
                {
                    PromptPointOptions mo = new("\nNovo ponto de implantacao (suporte): ") { UseBasePoint = true, BasePoint = new Point3d(atual.E, atual.N, 0.0), UseDashedLine = true };
                    PromptPointResult mr = ed.GetPoint(mo);
                    if (mr.Status != PromptStatus.OK)
                        break;
                    Point3d w = mr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    Adjust(doc, idAtual, (p, axes, names) =>
                    {
                        double rot = p.Rotacao;
                        p.E = w.X;
                        p.N = w.Y;
                        ViarioPlacas.Link(p, axes, names, 60.0);
                        p.Rotacao = rot;
                    });
                    break;
                }

                case "Esticar":
                {
                    // Placa e ponta da chamada andam juntas; suporte e saida da chamada ficam.
                    (List<Seg> ldcSeg, _, _) = Footprint(atual);
                    V2 ponta = ldcSeg[^1].B;
                    PromptPointOptions so = new("\nNova posicao da ponta da chamada (canto superior esquerdo da placa): ")
                    {
                        UseBasePoint = true, BasePoint = new Point3d(ponta.X, ponta.Y, 0.0), UseDashedLine = true
                    };
                    PromptPointResult sr = ed.GetPoint(so);
                    if (sr.Status != PromptStatus.OK)
                        break;
                    Point3d w = sr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    V2 saida = ldcSeg[0].A;
                    Adjust(doc, idAtual, (p, _, _) =>
                    {
                        double esc = p.Escala > 1e-9 ? p.Escala : 1.0;
                        p.Chamada = ViarioPlacas.Rotate(new V2(w.X, w.Y) - saida, -p.Rotacao) / esc;
                    });
                    break;
                }

                case "Padrao":
                    Adjust(doc, idAtual, (p, _, _) => p.Chamada = PlacaItem.ChamadaPadrao);
                    break;

                case "Alinhar":
                    Adjust(doc, idAtual, (p, axes, names) =>
                    {
                        if (!ViarioPlacas.Link(p, axes, names, 200.0))
                            ed.WriteMessage("\n  Sem eixo a menos de 200 m; rotacao mantida.");
                    });
                    break;

                case "Girar180":
                    Adjust(doc, idAtual, (p, _, _) => p.Rotacao = (p.Rotacao + 180.0) % 360.0);
                    break;

                case "Lado":
                    // Espelha o conjunto para o outro lado do eixo (ponto refletido no eixo, rotacao pela via).
                    Adjust(doc, idAtual, (p, axes, names) =>
                    {
                        AxisInput? a = axes.FirstOrDefault(x => x.Key == p.AxisKey);
                        if (a is null)
                        {
                            ed.WriteMessage("\n  Placa sem eixo vinculado.");
                            return;
                        }

                        (_, V2 pe, _) = a.Path.Project(p.Ponto);
                        V2 esp = pe - (p.Ponto - pe);
                        p.E = esp.X;
                        p.N = esp.Y;
                        ViarioPlacas.Link(p, axes, names, 200.0);
                    });
                    break;

                case "Outra":
                    id = null;
                    continue;
            }

            ed.UpdateScreen();
            ViarioPanel.NotifyChanged(doc);
        }
    }

    private static void ScaleCommand(Document doc)
    {
        Editor ed = doc.Editor;
        HashSet<string>? ids = PendingIds;
        PendingIds = null;
        if (ids is null)
        {
            PromptKeywordOptions ko = new("\nEscalar [Todas/Selecionar] <Todas>: ", "Todas Selecionar") { AllowNone = true };
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status == PromptStatus.Cancel || kr.Status == PromptStatus.Error)
                return;
            if (kr.Status == PromptStatus.OK && kr.StringResult == "Selecionar")
            {
                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelecione placas, suportes ou linhas de chamada: " });
                if (sr.Status != PromptStatus.OK)
                    return;
                ids = new HashSet<string>(StringComparer.Ordinal);
                using AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
                foreach (AcadDb.ObjectId id in sr.Value.GetObjectIds())
                {
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.BlockReference br)
                        continue;
                    List<string>? values = ViarioStore.ReadValues(br);
                    if (values is null || values.Count < 2)
                        continue;
                    if (values[0] == TagPlaca && PlacaItem.Deserialize(string.Concat(values.Skip(1))) is PlacaItem p)
                        ids.Add(p.Id);
                    else if (values[0] is TagLdc or TagSuporte)
                        ids.Add(values[1]);
                }

                if (ids.Count == 0)
                {
                    ed.WriteMessage("\n  Nenhuma placa deste modulo na selecao.");
                    return;
                }
            }
        }

        PromptDoubleOptions po = new($"\nEscala (1 = padrao do SinC){(ids is null ? ", todas as placas" : $", {ids.Count} placa(s)")}: ")
        {
            AllowNegative = false, AllowZero = false, DefaultValue = 1.0, UseDefaultValue = true
        };
        PromptDoubleResult pr = ed.GetDouble(po);
        if (pr.Status != PromptStatus.OK)
            return;
        int n = SetScale(doc, pr.Value, ids);
        ed.WriteMessage($"\n  {n} placa(s) com escala {pr.Value} (placa, linha de chamada e suporte, a partir do ponto de implantacao).");
        ViarioPanel.NotifyChanged(doc);
    }

    private static void RelinkCommand(Document doc)
    {
        Editor ed = doc.Editor;
        Opcoes o = new();
        PromptDoubleOptions dmax = new($"\nDistancia maxima do ponto ao eixo (m) <{o.DistanciaMaxima:0}>: ")
        {
            AllowNone = true, AllowNegative = false, AllowZero = false, DefaultValue = o.DistanciaMaxima, UseDefaultValue = true
        };
        PromptDoubleResult dr = ed.GetDouble(dmax);
        if (dr.Status == PromptStatus.Cancel)
            return;
        (int total, int semEixo) = Relink(doc, dr.Status == PromptStatus.OK ? dr.Value : o.DistanciaMaxima);
        ed.WriteMessage($"\n  {total} placa(s) atualizada(s); {semEixo} sem eixo.");
        ViarioPanel.NotifyChanged(doc);
    }

    private static void NameCommand(Document doc)
    {
        Editor ed = doc.Editor;
        PromptKeywordOptions ko = new("\nNomear eixos pelas placas [SoSemNome/Todos] <SoSemNome>: ", "SoSemNome Todos") { AllowNone = true };
        PromptResult kr = ed.GetKeywords(ko);
        if (kr.Status == PromptStatus.Cancel || kr.Status == PromptStatus.Error)
            return;
        bool all = kr.Status == PromptStatus.OK && kr.StringResult == "Todos";

        List<(string Key, string Name)> applied = NameAxes(doc, all);
        if (applied.Count == 0)
        {
            ed.WriteMessage("\n  Nenhum eixo nomeado (sem placas amarradas ou todos ja tem nome; use Todos para sobrescrever).");
            return;
        }

        foreach ((string key, string name) in applied)
            ed.WriteMessage($"\n  {key}: {name}");
        ed.WriteMessage($"\n  {applied.Count} eixo(s) nomeado(s). Rode ATualizar para regravar o atributo Eixo das placas.");

        // Regrava o atributo Eixo com o nome novo.
        Relink(doc, 1e9);
        ViarioPanel.NotifyChanged(doc);
    }

    private static void ListCommand(Document doc)
    {
        Editor ed = doc.Editor;
        List<PlacaItem> placas = Read(doc.Database);
        if (placas.Count == 0)
        {
            ed.WriteMessage("\n  Nao ha placas deste modulo no desenho.");
            return;
        }

        ed.WriteMessage($"\n  {"Id",-12} {"SinC",-8} {"Eixo",-32} {"Estaca",-12} {"Lado",-4} {"Afast.",7}  Mensagens");
        foreach (PlacaItem p in placas)
        {
            string eixo = p.AxisName.Length > 0 ? p.AxisName : (p.Vinculada ? p.AxisKey : "-");
            if (eixo.Length > 32)
                eixo = eixo[..31] + "…";
            ed.WriteMessage($"\n  {p.Id,-12} {p.Codigo,-8} {eixo,-32} {p.EstacaTexto,-12} {p.LadoTexto,-4} {p.Offset,7:0.0}  {p.MensagemResumo}");
        }

        ed.WriteMessage($"\n  {placas.Count} placa(s); {placas.Count(p => !p.Vinculada)} sem eixo.");
    }

    private static void RemoveCommand(Document doc)
    {
        Editor ed = doc.Editor;
        PromptKeywordOptions ko = new("\nRemover todas as placas e linhas de chamada deste modulo? [Sim/Nao] <Nao>: ", "Sim Nao") { AllowNone = true };
        PromptResult kr = ed.GetKeywords(ko);
        if (kr.Status != PromptStatus.OK || kr.StringResult != "Sim")
            return;
        int n = Remove(doc);
        ed.WriteMessage($"\n  {n} placa(s) removida(s).");
        ViarioPanel.NotifyChanged(doc);
    }
}
