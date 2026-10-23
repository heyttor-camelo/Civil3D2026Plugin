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
                list.Add((p, br));
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

        foreach (PlacaItem p in cat.Placas)
        {
            if (!ViarioPlacas.Link(p, axes, names, o.DistanciaMaxima))
                semEixo++;
            p.SuporteTipo = o.Suportes ? ViarioPlacas.SupportType(p.AreaSinC, p.LarguraSinC, o.AreaSimples, o.AreaDuplo, o.LarguraPortico) : "";

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
        PluginLog.Info(Modulo, $"Importadas {inseridas} placas ({semEixo} sem eixo, {semArquivo.Count} sem arquivo) de {cat.Fonte}.");
        return (inseridas, semEixo, semArquivo);
    }

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

    private static Point3d Local(PlacaItem p, V2 offset, double scale)
    {
        V2 d = ViarioPlacas.Rotate(offset * scale, p.Rotacao);
        return new Point3d(p.E + d.X, p.N + d.Y, 0.0);
    }

    private static void InsertOne(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord ms, AcadDb.ObjectId ldcId,
        AcadDb.ObjectId blockId, PlacaItem p, Opcoes o)
    {
        double rad = p.Rotacao * Math.PI / 180.0;

        // Linha de chamada no ponto de implantacao.
        AcadDb.BlockReference ldc = new(Local(p, OffsetLdc, 1.0), ldcId) { Rotation = rad };
        ldc.SetDatabaseDefaults(db);
        ldc.Layer = LayerLdc;
        ms.AppendEntity(ldc);
        tr.AddNewlyCreatedDBObject(ldc, true);
        ViarioStore.SetTag(ldc, TagLdc, p.Id);

        // Placa na ponta da linha de chamada.
        AcadDb.BlockReference br = new(Local(p, OffsetPlaca, o.Escala), blockId)
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
        if (o.Suportes && p.SuporteTipo.Length > 0)
        {
            AcadDb.ObjectId supId = EnsureSupportBlock(tr, db, p.SuporteTipo, p);
            AcadDb.BlockReference sup = new(new Point3d(p.E, p.N, 0.0), supId) { Rotation = rad };
            sup.SetDatabaseDefaults(db);
            sup.Layer = LayerSuporte;
            ms.AppendEntity(sup);
            tr.AddNewlyCreatedDBObject(sup, true);
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
        ["Altura"] = p.AlturaSuporte.Length > 0 ? p.AlturaSuporte.Replace('.', ',').TrimEnd('m') + "m" : "",
        ["Tipo"] = p.SuporteTipo,
        ["Nort"] = "N: " + Coord(p.N),
        ["Est"] = "E: " + Coord(p.E),
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
        ["Área"] = p.AreaDm2.ToString(CultureInfo.InvariantCulture),
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
    /// Bloco do suporte, gerado em codigo no mesmo molde do "Coluna Dupla_SVS" visto num desenho
    /// do SinC (geometria na layer sinC_Suporte; atributos Altura, Tipo, Nort, Est visiveis e
    /// Eixo, Estaca/KM, Lado, Material, Secao, Situacao invisiveis, em sinC_ATT_Suporte).
    /// Portico/semi-portico: o vao e tirado do afastamento da placa ao eixo.
    /// </summary>
    private static AcadDb.ObjectId EnsureSupportBlock(AcadDb.Transaction tr, AcadDb.Database db, string tipo, PlacaItem p)
    {
        string name = tipo switch
        {
            ViarioPlacas.SuporteSimples => "Coluna Simples_SVS",
            ViarioPlacas.SuporteDuplo => "Coluna Dupla_SVS",
            ViarioPlacas.SuporteSemiPortico => "Semi-Portico_SVS",
            _ => "Portico_SVS"
        };
        // Portico: um bloco por vao (arredondado ao metro), ja que os postes ficam dos dois lados da pista.
        double span = 0.0;
        if (tipo == ViarioPlacas.SuportePortico)
        {
            span = Math.Max(4.0, Math.Round(p.Vinculada ? 2.0 * p.Offset : p.LarguraSinC + 2.0));
            name += "_" + span.ToString("0", CultureInfo.InvariantCulture);
        }

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (bt.Has(name))
            return bt[name];

        bt.UpgradeOpen();
        AcadDb.BlockTableRecord btr = new() { Name = name, Origin = Point3d.Origin };
        AcadDb.ObjectId id = bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);

        void Post(double x, double y, double r)
        {
            AcadDb.Circle c = new(new Point3d(x, y, 0.0), Vector3d.ZAxis, r);
            c.SetDatabaseDefaults(db);
            c.Layer = LayerSuporteGeo;
            btr.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);
            AcadDb.Hatch h = new();
            h.SetDatabaseDefaults(db);
            h.Layer = LayerSuporteGeo;
            btr.AppendEntity(h);
            tr.AddNewlyCreatedDBObject(h, true);
            h.SetHatchPattern(AcadDb.HatchPatternType.PreDefined, "SOLID");
            h.AppendLoop(AcadDb.HatchLoopTypes.Outermost, new AcadDb.ObjectIdCollection { c.ObjectId });
            h.EvaluateHatch(true);
        }

        void Bar(double x0, double x1, double y, double t)
        {
            AcadDb.Polyline pl = new();
            pl.SetDatabaseDefaults(db);
            pl.Layer = LayerSuporteGeo;
            pl.AddVertexAt(0, new Point2d(x0, y), 0.0, 0.0, 0.0);
            pl.AddVertexAt(1, new Point2d(x1, y), 0.0, 0.0, 0.0);
            pl.AddVertexAt(2, new Point2d(x1, y - t), 0.0, 0.0, 0.0);
            pl.AddVertexAt(3, new Point2d(x0, y - t), 0.0, 0.0, 0.0);
            pl.Closed = true;
            btr.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        switch (tipo)
        {
            case ViarioPlacas.SuporteSimples:
                Post(0.0, 0.0, 0.15);
                break;
            case ViarioPlacas.SuporteDuplo:
                Post(-0.55, 0.0, 0.15);
                Post(0.55, 0.0, 0.15);
                Bar(-0.9, 0.9, -0.25, 0.025);
                break;
            case ViarioPlacas.SuporteSemiPortico:
                // Poste no ponto; braco sobre a pista (lado -X local = para o eixo).
                Post(0.0, 0.0, 0.22);
                Bar(-(p.LarguraSinC + 0.5), 0.0, 0.15, 0.30);
                break;
            default:
                Post(0.0, 0.0, 0.22);
                Post(-span, 0.0, 0.22);
                Bar(-span, 0.0, 0.15, 0.30);
                break;
        }

        double y2 = 0.0;
        foreach (string tag in SupVisibleTags)
        {
            AddAtt(tr, db, btr, tag, new Point3d(1.0, y2, 0.0), false, LayerAttSuporte);
            y2 -= 1.8;
        }

        foreach (string tag in SupInvisibleTags)
            AddAtt(tr, db, btr, tag, new Point3d(-15.0, -5.0, 0.0), true, LayerAttSuporte);

        return id;
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
            if (id.IsErased || id.ObjectClass.DxfName != "INSERT")
                continue;
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockReference br)
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
    internal sealed record SignShape(List<(List<List<V2>> Loops, byte R, byte G, byte B)> Hatches, double Width, double Height);

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
        List<(List<List<V2>>, byte, byte, byte)> hatches = new();
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
                        foreach (Curve2d c in loop.Curves)
                        {
                            Point2d[] sample = c.GetSamplePoints(c is LineSegment2d ? 2 : 10);
                            foreach (Point2d q in sample)
                                pts.Add(new V2(q.X, q.Y));
                        }
                    }
                }
                catch
                {
                    // Laco que a API nao devolve: pula.
                }

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
            hatches.Add((loops, c.R, c.G, c.B));
        }

        if (hatches.Count == 0)
            return null;
        SignShape shape = new(hatches, Math.Max(maxX - minX, 1e-6), Math.Max(maxY - minY, 1e-6));
        lock (ShapeCache)
            ShapeCache[blockName] = shape;
        return shape;
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

    /// <summary>Zoom numa placa (caixa de 60 m em volta do ponto).</summary>
    public static Box2 ZoomBox(PlacaItem p) => new(p.E - 40, p.N - 40, p.E + 40, p.N + 40);

    // ------------------------------------------------------------------
    // Comando C3DVIARIOPLACAS
    // ------------------------------------------------------------------

    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        int existing = Count(doc.Database);
        PromptKeywordOptions options = new(
            $"\nPlacas verticais ({existing} no desenho) [Importar/PAsta/ATualizar/NOmear/Listar/Remover/Painel] <Importar>: ",
            "Importar PAsta ATualizar NOmear Listar Remover Painel")
        {
            AllowNone = true
        };

        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        switch (r.Status == PromptStatus.OK ? r.StringResult : "Importar")
        {
            case "PAsta": ImportFolderCommand(doc); break;
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
        PromptOpenFileOptions fo = new("\nCatalogo das placas (JSON ou CSV gerado da planilha do projeto)")
        {
            Filter = "Catalogo de placas (*.json;*.csv)|*.json;*.csv|Todos (*.*)|*.*"
        };
        PromptFileNameResult fr = ed.GetFileNameForOpen(fo);
        if (fr.Status != PromptStatus.OK)
            return;

        PlacaCatalog cat;
        try
        {
            cat = ViarioPlacas.Load(fr.StringResult);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n  Nao foi possivel ler o catalogo: " + ex.Message);
            PluginLog.Erro(Modulo, "Falha ao ler " + fr.StringResult, ex);
            return;
        }

        if (cat.Placas.Count == 0)
        {
            ed.WriteMessage("\n  O catalogo nao tem placas.");
            return;
        }

        string pastaPadrao = Directory.Exists(cat.PastaSinC) ? cat.PastaSinC : (Path.GetDirectoryName(fr.StringResult) ?? "");
        PromptStringOptions po = new($"\nPasta dos T-xxx_SV.dwg do SinC <{pastaPadrao}>: ") { AllowSpaces = true };
        PromptResult pr = ed.GetString(po);
        if (pr.Status == PromptStatus.Cancel)
            return;
        string pasta = string.IsNullOrWhiteSpace(pr.StringResult) ? pastaPadrao : pr.StringResult.Trim().Trim('"');
        if (!Directory.Exists(pasta))
        {
            ed.WriteMessage("\n  Pasta nao encontrada: " + pasta);
            return;
        }

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
        ed.WriteMessage($"\n  {inseridas} placa(s) inserida(s) de {cat.Placas.Count}; {semEixo} sem eixo a menos de {o.DistanciaMaxima:0} m (ficam sem Eixo/Estaca/Lado).");
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
        PromptStringOptions po = new("\nPasta do SinC com os .pla e T-xxx_SV.dwg <C:\\sinC\\Local\\Placas\\Placas 2D>: ") { AllowSpaces = true };
        PromptResult pr = ed.GetString(po);
        if (pr.Status == PromptStatus.Cancel)
            return;
        string pasta = string.IsNullOrWhiteSpace(pr.StringResult) ? @"C:\sinC\Local\Placas\Placas 2D" : pr.StringResult.Trim().Trim('"');
        if (!Directory.Exists(pasta))
        {
            ed.WriteMessage("\n  Pasta nao encontrada: " + pasta);
            return;
        }

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
            if (file is null || (cr.Status == PromptStatus.OK && cr.StringResult == "Sim" && guess is null))
            {
                PromptOpenFileOptions fo = new("\nCatalogo do projeto (JSON ou CSV)") { Filter = "Catalogo de placas (*.json;*.csv)|*.json;*.csv|Todos (*.*)|*.*" };
                PromptFileNameResult fr = ed.GetFileNameForOpen(fo);
                if (fr.Status != PromptStatus.OK)
                    return;
                file = fr.StringResult;
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
