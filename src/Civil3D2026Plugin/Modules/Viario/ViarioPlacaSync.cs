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
    }

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
        foreach (string layer in new[] { LayerPlaca, LayerAtt, LayerLdc, "sinC_VERTICALS", "sinC_ATT_Suporte" })
            ViarioStore.EnsureLayer(tr, db, layer, 7);
        AcadDb.ObjectId ldcId = EnsureLdcBlock(tr, db);

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
        Dictionary<string, AcadDb.ObjectId> blocks = new(StringComparer.OrdinalIgnoreCase);

        foreach (PlacaItem p in cat.Placas)
        {
            if (!ViarioPlacas.Link(p, axes, names, o.DistanciaMaxima))
                semEixo++;

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

        Tag(br, TagPlaca, p.Serialize());
    }

    private static Dictionary<string, string> AttributeValues(PlacaItem p, Opcoes o) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Código"] = p.Codigo,
        ["Situação"] = o.Situacao,
        ["Nort"] = p.N.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ','),
        ["Est"] = p.E.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ','),
        ["Área"] = p.AreaDm2.ToString(CultureInfo.InvariantCulture),
        ["Dimensão"] = p.DimensaoSinC,
        ["Eixo"] = p.AxisName,
        ["Estaca/KM"] = p.EstacaKm,
        ["Lado"] = p.LadoTexto,
        ["Película_Fundo"] = o.PeliculaFundo,
        ["Película_Orla"] = o.PeliculaOrla,
        ["SI"] = o.SI,
        ["Substrato"] = o.Substrato,
        ["Suporte"] = SuporteNome(p.Suporte),
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

    private static void AddAtt(AcadDb.Transaction tr, AcadDb.Database db, AcadDb.BlockTableRecord btr, string tag, Point3d at, bool invisible)
    {
        AcadDb.AttributeDefinition ad = new();
        ad.SetDatabaseDefaults(db);
        ad.Layer = LayerAtt;
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

        tr.Commit();
        return (total, semEixo);
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
            if (tag != TagPlaca && tag != TagLdc)
                continue;
            br.UpgradeOpen();
            br.Erase();
            if (tag == TagPlaca)
                n++;
        }

        tr.Commit();
        return n;
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
            $"\nPlacas verticais ({existing} no desenho) [Importar/ATualizar/NOmear/Listar/Remover/Painel] <Importar>: ",
            "Importar ATualizar NOmear Listar Remover Painel")
        {
            AllowNone = true
        };

        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        switch (r.Status == PromptStatus.OK ? r.StringResult : "Importar")
        {
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

        (int inseridas, int semEixo, List<string> semArquivo) = Import(doc, cat, pasta, o);
        ed.WriteMessage($"\n  {inseridas} placa(s) inserida(s) de {cat.Placas.Count}; {semEixo} sem eixo a menos de {o.DistanciaMaxima:0} m (ficam sem Eixo/Estaca/Lado).");
        if (semArquivo.Count > 0)
            ed.WriteMessage($"\n  Sem arquivo _SV.dwg na pasta ({semArquivo.Count}): {string.Join(", ", semArquivo.Take(20))}{(semArquivo.Count > 20 ? "..." : "")}");
        ed.WriteMessage("\n  Atributos no padrao do SinC (Codigo, Situacao, Nort, Est, Area, Dimensao, Eixo, Estaca/KM, Lado...). " +
                        "Suporte: inserir no SinC. Use NOmear para dar aos eixos o nome da rota/trecho das placas.");
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
