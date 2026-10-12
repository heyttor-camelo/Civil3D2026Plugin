using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcColors = Autodesk.AutoCAD.Colors;
using AcGi = Autodesk.AutoCAD.GraphicsInterface;
using AutoCorridorFeatureLine = Autodesk.Civil.DatabaseServices.AutoCorridorFeatureLine;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using FeatureLinePointType = Autodesk.Civil.FeatureLinePointType;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>
/// C3DSOLIDLIGAR: liga duas linhas quaisquer (Line, Arc, Polyline, Polyline2d,
/// Polyline3d, Spline, Feature Line ou AutoCorridorFeatureLine) por estais
/// tubulares (Solid3d) entre N pontos igualmente espacados em cada linha.
///
/// Regras principais:
/// - cada linha pode ter um percentual descontado no inicio e no fim; os N
///   pontos (incluindo os extremos do trecho util) ficam no miolo restante;
/// - o espacamento e medido pelo comprimento 3D de cada linha, independentemente;
/// - o ponto i da linha 1 liga ao ponto i da linha 2 (ou N-1-i, se invertido);
/// - antes de gerar, mostra previa temporaria das ligacoes com opcao [Inverter];
/// - um Solid3d por estai, em layer proprio, com Property Set C3D_ESTAI
///   (material, comprimento, massa, cargas de escoamento/ruptura etc.);
/// - o conjunto fica persistido no DWG e e reconstruido por
///   C3DSOLIDLIGARATUALIZAR apos alteracao das linhas.
/// </summary>
public sealed class C3DSolidLinkCommands
{
    public const string Versao = C3DVersions.SolidLink;

    private const string RootDictionaryName = "C3D_SOLID_LIGAR_V1";
    private const string SettingsKey = "SETTINGS";
    private const string SetKeyPrefix = "SET_";
    private const string TagKey = "C3D_SOLID_LIGAR_TAG";
    private const string RecordSignatureV1 = "C3DSOLIDLIGAR_V1";
    private const string RecordSignatureV2 = "C3DSOLIDLIGAR_V2";
    private const string SettingsSignatureV2 = "C3DSOLIDLIGAR_SETTINGS_V2";

    private const string DefaultLayer = "C3D-ESTAIS";
    private const short DefaultLayerColor = 4; // ciano

    private const double Epsilon = 1e-9;
    private const double GeometryTolerance = 1e-4;
    private const double MaxTrimPercent = 95.0;
    private const int MaxPoints = 5000;

    /// <summary>Parametros escolhidos pelo usuario (padrao do desenho e de cada conjunto).</summary>
    private sealed class LinkParameters
    {
        public int Count { get; set; } = 10;
        public double TrimStartA { get; set; }
        public double TrimEndA { get; set; }
        public double TrimStartB { get; set; }
        public double TrimEndB { get; set; }
        public double Diameter { get; set; } = 0.10;
        public SolidLinkMaterial Material { get; set; } = SolidLinkMaterials.Default;

        /// <summary>Area metalica efetiva em cm2; 0 = calcular pelo diametro (pi.D2/4).</summary>
        public double AreaCm2Override { get; set; }

        public string Layer { get; set; } = DefaultLayer;

        public LinkParameters Clone() => (LinkParameters)MemberwiseClone();
    }

    private sealed class LinkSetData
    {
        public string GroupId { get; set; } = string.Empty;
        public string HandleA { get; set; } = string.Empty;
        public string HandleB { get; set; } = string.Empty;
        public bool Reversed { get; set; }
        public LinkParameters Parameters { get; set; } = new();
        public List<string> GeneratedHandles { get; } = new();
    }

    private sealed class GenerationResult
    {
        public int Created { get; set; }
        public int Skipped { get; set; }
        public int PropertySetFailures { get; set; }
        public string PropertySetMessage { get; set; } = string.Empty;
        public double TotalLengthM { get; set; }
        public double TotalMassKg { get; set; }
    }

    // ------------------------------------------------------------------
    // Comandos
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Corridor.SolidLigar, CommandFlags.Modal)]
    public void Ligar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            ed.WriteMessage(
                "\n" + C3DCommands.Corridor.SolidLigar + " v" + Versao +
                " - estais tubulares entre duas linhas.");

            ObjectIdPair? picked = PickTwoLines(ed);
            if (picked is null)
                return;

            LinkParameters defaults;
            using (AcadDb.Transaction trSettings = db.TransactionManager.StartTransaction())
            {
                defaults = LoadSettings(trSettings, db);
                trSettings.Commit();
            }

            LinkParameters? parameters = AskParameters(ed, db, defaults, line2DefaultsFromLine1: true);
            if (parameters is null)
                return;

            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

            PathSampler? pathA = PathSampler.TryCreate(tr, picked.Value.A, out string errorA);
            PathSampler? pathB = PathSampler.TryCreate(tr, picked.Value.B, out string errorB);

            if (pathA is null || pathB is null)
            {
                ed.WriteMessage("\n[ERRO] " + (pathA is null ? errorA : errorB));
                return;
            }

            ed.WriteMessage(
                "\nLinha 1: " + pathA.Description + " | L=" + FormatNumber(pathA.Length) +
                " | desconto " + FormatTrim(parameters.TrimStartA, parameters.TrimEndA) +
                "\nLinha 2: " + pathB.Description + " | L=" + FormatNumber(pathB.Length) +
                " | desconto " + FormatTrim(parameters.TrimStartB, parameters.TrimEndB));

            List<Point3d> pointsA = pathA.Distribute(parameters.Count, parameters.TrimStartA, parameters.TrimEndA);
            List<Point3d> pointsB = pathB.Distribute(parameters.Count, parameters.TrimStartB, parameters.TrimEndB);

            bool initialReversed = SuggestReversed(pointsA, pointsB);
            bool? reversed = ConfirmPairing(ed, pointsA, pointsB, initialReversed);
            if (reversed is null)
            {
                ed.WriteMessage("\nComando cancelado. Nada foi criado.");
                return;
            }

            LinkSetData data = new()
            {
                GroupId = Guid.NewGuid().ToString("N"),
                HandleA = picked.Value.A.Handle.ToString(),
                HandleB = picked.Value.B.Handle.ToString(),
                Reversed = reversed.Value,
                Parameters = parameters
            };

            GenerationResult result = GenerateSolids(tr, db, pointsA, pointsB, data);

            SaveSet(tr, db, data);
            SaveSettings(tr, db, parameters);

            tr.Commit();

            ed.WriteMessage("\nConjunto criado. ID=" + ShortId(data.GroupId));
            ReportGeneration(ed, data, result, db);

            ed.WriteMessage(
                "\nApos alterar as linhas, use " + C3DCommands.Corridor.SolidLigarAtualizar +
                ". Para mudar parametros ou sentido, use " + C3DCommands.Corridor.SolidLigarEditar + ".");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Corridor.SolidLigar + "] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidLigarEditar, CommandFlags.Modal)]
    public void Editar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            PromptEntityOptions options = new("\nSelecione um estai do conjunto: ");
            options.SetRejectMessage("\nSelecione um 3D Solid gerado por " + C3DCommands.Corridor.SolidLigar + ".");
            options.AddAllowedClass(typeof(AcadDb.Solid3d), true);

            PromptEntityResult picked = ed.GetEntity(options);
            if (picked.Status != PromptStatus.OK)
                return;

            LinkSetData? data;
            using (AcadDb.Transaction trRead = db.TransactionManager.StartTransaction())
            {
                string? groupId = TryReadTag(trRead, picked.ObjectId);
                data = groupId is null ? null : LoadSet(trRead, db, groupId);
                trRead.Commit();
            }

            if (data is null)
            {
                ed.WriteMessage("\n[ERRO] O solido selecionado nao pertence a um conjunto " + C3DCommands.Corridor.SolidLigar + ".");
                return;
            }

            LinkParameters? parameters = AskParameters(ed, db, data.Parameters, line2DefaultsFromLine1: false);
            if (parameters is null)
                return;

            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

            if (!TryOpenPaths(tr, db, data, ed, out PathSampler? pathA, out PathSampler? pathB))
                return;

            List<Point3d> pointsA = pathA!.Distribute(parameters.Count, parameters.TrimStartA, parameters.TrimEndA);
            List<Point3d> pointsB = pathB!.Distribute(parameters.Count, parameters.TrimStartB, parameters.TrimEndB);

            bool? reversed = ConfirmPairing(ed, pointsA, pointsB, data.Reversed);
            if (reversed is null)
            {
                ed.WriteMessage("\nEdicao cancelada. O conjunto nao foi alterado.");
                return;
            }

            data.Parameters = parameters;
            data.Reversed = reversed.Value;

            if (!EraseGenerated(tr, db, data, ed))
                return;

            GenerationResult result = GenerateSolids(tr, db, pointsA, pointsB, data);
            SaveSet(tr, db, data);
            tr.Commit();

            ed.WriteMessage("\nConjunto " + ShortId(data.GroupId) + " recriado.");
            ReportGeneration(ed, data, result, db);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Corridor.SolidLigarEditar + "] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidLigarAtualizar, CommandFlags.Modal)]
    public void Atualizar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            PromptKeywordOptions modeOptions = new(
                "\nAtualizar quais conjuntos? [Selecionar/Todos] <Selecionar>: ",
                "Selecionar Todos")
            {
                AllowNone = true
            };

            PromptResult modeResult = ed.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel || modeResult.Status == PromptStatus.Error)
                return;

            bool all = modeResult.Status == PromptStatus.OK &&
                       string.Equals(modeResult.StringResult, "Todos", StringComparison.OrdinalIgnoreCase);

            List<AcadDb.ObjectId> selection = new();
            if (!all)
            {
                PromptSelectionOptions selOptions = new()
                {
                    MessageForAdding = "\nSelecione estais gerados e/ou as linhas de origem: "
                };

                PromptSelectionResult selResult = ed.GetSelection(selOptions);
                if (selResult.Status != PromptStatus.OK)
                    return;

                selection.AddRange(selResult.Value.GetObjectIds());
            }

            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

            List<LinkSetData> allSets = LoadAllSets(tr, db);
            if (allSets.Count == 0)
            {
                ed.WriteMessage("\nNenhum conjunto " + C3DCommands.Corridor.SolidLigar + " encontrado neste desenho.");
                return;
            }

            List<LinkSetData> targets = all
                ? allSets
                : ResolveSetsFromSelection(tr, allSets, selection);

            if (targets.Count == 0)
            {
                ed.WriteMessage("\nA selecao nao contem estais nem linhas de conjuntos " + C3DCommands.Corridor.SolidLigar + ".");
                return;
            }

            int updated = 0;
            int failed = 0;
            int propertySetFailures = 0;
            string lastPropertySetMessage = string.Empty;

            foreach (LinkSetData data in targets)
            {
                if (!TryOpenPaths(tr, db, data, ed, out PathSampler? pathA, out PathSampler? pathB))
                {
                    failed++;
                    continue;
                }

                LinkParameters p = data.Parameters;
                List<Point3d> pointsA = pathA!.Distribute(p.Count, p.TrimStartA, p.TrimEndA);
                List<Point3d> pointsB = pathB!.Distribute(p.Count, p.TrimStartB, p.TrimEndB);

                if (!EraseGenerated(tr, db, data, ed))
                {
                    failed++;
                    continue;
                }

                GenerationResult result = GenerateSolids(tr, db, pointsA, pointsB, data);
                SaveSet(tr, db, data);

                propertySetFailures += result.PropertySetFailures;
                if (result.PropertySetFailures > 0)
                    lastPropertySetMessage = result.PropertySetMessage;

                updated++;
            }

            tr.Commit();

            ed.WriteMessage(
                "\nConjuntos atualizados: " + updated +
                (failed > 0 ? " | nao atualizados: " + failed + " (ver mensagens acima)." : "."));

            if (propertySetFailures > 0)
            {
                ed.WriteMessage(
                    "\n[AVISO] Property Set " + SolidLinkPropertySet.DefinitionName + " nao aplicado em " +
                    propertySetFailures + " estai(s): " + lastPropertySetMessage);
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Corridor.SolidLigarAtualizar + "] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidLigarHelp, CommandFlags.Modal)]
    public void Help()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;

        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidLigar + " v" + Versao);
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidLigar);
        ed.WriteMessage("\n  1) selecione a primeira e a segunda linha;");
        ed.WriteMessage("\n  2) quantidade de pontos N (inclui os extremos do trecho util);");
        ed.WriteMessage("\n  3) desconto % no inicio e no fim de cada linha (Enter na linha 2");
        ed.WriteMessage("\n     repete os valores da linha 1);");
        ed.WriteMessage("\n  4) diametro do estai (unidades do desenho);");
        ed.WriteMessage("\n  5) material: " + SolidLinkMaterials.KeywordDisplay + ";");
        ed.WriteMessage("\n  6) area metalica efetiva em cm2 (Enter = pi.D2/4 do solido);");
        ed.WriteMessage("\n  7) layer dos estais (criado se nao existir);");
        ed.WriteMessage("\n  8) previa temporaria; [Inverter] troca o sentido, Enter aceita.");
        ed.WriteMessage("\n  Linhas aceitas: Line, Arc, Polyline, Polyline 2D/3D, Spline,");
        ed.WriteMessage("\n  Feature Line e AutoCorridorFeatureLine.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidLigarEditar);
        ed.WriteMessage("\n  Selecione um estai e altere qualquer parametro ou o sentido.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidLigarAtualizar);
        ed.WriteMessage("\n  Recria os conjuntos apos alterar as linhas de origem.");
        ed.WriteMessage("\n  Selecione estais ou linhas, ou use a opcao Todos.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\nPROPERTY SET " + SolidLinkPropertySet.DefinitionName + " (por estai):");
        ed.WriteMessage("\n  codigo, material, diametro, area metalica, comprimento, massa linear,");
        ed.WriteMessage("\n  massa, densidade, fyk, fuk, E, cargas de escoamento e ruptura,");
        ed.WriteMessage("\n  inclinacao, cotas, pontos de ancoragem e descontos aplicados.");
        ed.WriteMessage("\n  Valores do catalogo sao nominais de referencia: confirme com a");
        ed.WriteMessage("\n  especificacao do projeto ou use a opcao Outro.");
        ed.WriteMessage("\n  Para cordoalhas, informe a area metalica nominal real (ex.: n x 1,40 cm2");
        ed.WriteMessage("\n  para cordoalhas de 15,2 mm), pois o solido e um cilindro cheio.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\nREGRAS:");
        ed.WriteMessage("\n  Espacamento igual pelo comprimento 3D de cada linha.");
        ed.WriteMessage("\n  Unidades em m/kg dependem de INSUNITS (indefinido = metro).");
        ed.WriteMessage("\n  Estais apagados manualmente voltam ao atualizar o conjunto.");
        ed.WriteMessage("\n============================================================");
    }

    // ------------------------------------------------------------------
    // Interacao
    // ------------------------------------------------------------------

    private readonly record struct ObjectIdPair(AcadDb.ObjectId A, AcadDb.ObjectId B);

    private static ObjectIdPair? PickTwoLines(Editor ed)
    {
        AcadDb.ObjectId first = PickLine(ed, "\nSelecione a PRIMEIRA linha: ", AcadDb.ObjectId.Null);
        if (first.IsNull)
            return null;

        AcadDb.ObjectId second = PickLine(ed, "\nSelecione a SEGUNDA linha: ", first);
        if (second.IsNull)
            return null;

        return new ObjectIdPair(first, second);
    }

    private static AcadDb.ObjectId PickLine(Editor ed, string message, AcadDb.ObjectId exclude)
    {
        while (true)
        {
            // O tipo e validado apos o clique (IsSupportedClass) para aceitar
            // tanto Curves AutoCAD quanto Feature Lines Civil 3D.
            PromptEntityOptions options = new(message);

            PromptEntityResult result = ed.GetEntity(options);
            if (result.Status != PromptStatus.OK)
                return AcadDb.ObjectId.Null;

            if (result.ObjectId == exclude)
            {
                ed.WriteMessage("\nA segunda linha precisa ser diferente da primeira.");
                continue;
            }

            if (!PathSampler.IsSupportedClass(result.ObjectId.ObjectClass))
            {
                ed.WriteMessage(
                    "\nTipo nao suportado (" + result.ObjectId.ObjectClass.DxfName +
                    "). Use Line, Arc, Polyline, Polyline 2D/3D, Spline ou Feature Line.");
                continue;
            }

            return result.ObjectId;
        }
    }

    /// <summary>
    /// Pergunta todos os parametros do conjunto. Retorna null se o usuario cancelar.
    /// line2DefaultsFromLine1: na criacao, Enter nos descontos da linha 2 repete a linha 1.
    /// </summary>
    private static LinkParameters? AskParameters(
        Editor ed,
        AcadDb.Database db,
        LinkParameters current,
        bool line2DefaultsFromLine1)
    {
        LinkParameters p = current.Clone();

        int? count = AskCount(ed, p.Count);
        if (count is null)
            return null;
        p.Count = count.Value;

        if (!AskTrims(ed, "linha 1", p.TrimStartA, p.TrimEndA, out double startA, out double endA))
            return null;
        p.TrimStartA = startA;
        p.TrimEndA = endA;

        double defaultStartB = line2DefaultsFromLine1 ? startA : p.TrimStartB;
        double defaultEndB = line2DefaultsFromLine1 ? endA : p.TrimEndB;

        if (!AskTrims(ed, "linha 2", defaultStartB, defaultEndB, out double startB, out double endB))
            return null;
        p.TrimStartB = startB;
        p.TrimEndB = endB;

        double? diameter = AskDiameter(ed, p.Diameter);
        if (diameter is null)
            return null;
        p.Diameter = diameter.Value;

        SolidLinkMaterial? material = AskMaterial(ed, p.Material);
        if (material is null)
            return null;
        p.Material = material;

        double? area = AskArea(ed, db, p);
        if (area is null)
            return null;
        p.AreaCm2Override = area.Value;

        string? layer = AskLayer(ed, p.Layer);
        if (layer is null)
            return null;
        p.Layer = layer;

        return p;
    }

    private static int? AskCount(Editor ed, int current)
    {
        current = Math.Max(2, current);

        // Valor padrao exibido manualmente e aplicado no Enter (mesmo padrao do C3DSOLIDARRAY).
        PromptIntegerOptions options = new(
            "\nQuantidade de pontos em cada linha <" + current + ">: ")
        {
            AllowNone = true,
            LowerLimit = 2,
            UpperLimit = MaxPoints,
            AllowNegative = false,
            AllowZero = false
        };

        PromptIntegerResult result = ed.GetInteger(options);

        if (result.Status == PromptStatus.None)
            return current;

        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static bool AskTrims(
        Editor ed,
        string label,
        double currentStart,
        double currentEnd,
        out double start,
        out double end)
    {
        start = currentStart;
        end = currentEnd;

        while (true)
        {
            double? s = AskPercent(ed, "\nDesconto no INICIO da " + label + " (%)", currentStart);
            if (s is null)
                return false;

            double? e = AskPercent(ed, "\nDesconto no FIM da " + label + " (%)", currentEnd);
            if (e is null)
                return false;

            if (s.Value + e.Value >= 100.0 - Epsilon)
            {
                ed.WriteMessage("\nA soma dos descontos da " + label + " precisa ser menor que 100%.");
                continue;
            }

            start = s.Value;
            end = e.Value;
            return true;
        }
    }

    private static double? AskPercent(Editor ed, string message, double current)
    {
        PromptDoubleOptions options = new(message + " <" + FormatNumber(current) + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = true
        };

        while (true)
        {
            PromptDoubleResult result = ed.GetDouble(options);

            if (result.Status == PromptStatus.None)
                return current;

            if (result.Status != PromptStatus.OK)
                return null;

            if (result.Value > MaxTrimPercent)
            {
                ed.WriteMessage("\nInforme um valor entre 0 e " + FormatNumber(MaxTrimPercent) + "%.");
                continue;
            }

            return result.Value;
        }
    }

    private static double? AskDiameter(Editor ed, double current)
    {
        if (current <= Epsilon)
            current = 0.10;

        PromptDistanceOptions options = new(
            "\nDiametro do estai (unidades do desenho) <" + FormatNumber(current) + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            Only2d = false
        };

        PromptDoubleResult result = ed.GetDistance(options);

        if (result.Status == PromptStatus.None)
            return current;

        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static SolidLinkMaterial? AskMaterial(Editor ed, SolidLinkMaterial current)
    {
        ed.WriteMessage("\nMateriais disponiveis:");
        foreach (SolidLinkMaterial m in SolidLinkMaterials.Catalog)
        {
            ed.WriteMessage(
                "\n  " + m.Key.PadRight(6) + " " + m.Name +
                " | fyk=" + FormatNumber(m.YieldMPa) + " fuk=" + FormatNumber(m.UltimateMPa) + " MPa");
        }
        ed.WriteMessage("\n  " + SolidLinkMaterials.CustomKey.PadRight(6) + " informar nome e propriedades");

        string currentKey = SolidLinkMaterials.Find(current.Key) is null ? SolidLinkMaterials.CustomKey : current.Key;
        string currentLabel = currentKey == SolidLinkMaterials.CustomKey ? current.Name : current.Key;

        PromptKeywordOptions options = new(
            "\nMaterial [" + SolidLinkMaterials.KeywordDisplay + "] <" + currentLabel + ">: ",
            SolidLinkMaterials.KeywordList)
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);

        if (result.Status == PromptStatus.None)
            return current;

        if (result.Status != PromptStatus.OK)
            return null;

        SolidLinkMaterial? preset = SolidLinkMaterials.Find(result.StringResult);
        if (preset is not null)
            return preset;

        return AskCustomMaterial(ed, current);
    }

    private static SolidLinkMaterial? AskCustomMaterial(Editor ed, SolidLinkMaterial current)
    {
        PromptStringOptions nameOptions = new("\nNome do material <" + current.Name + ">: ")
        {
            AllowSpaces = true
        };

        PromptResult nameResult = ed.GetString(nameOptions);
        if (nameResult.Status != PromptStatus.OK)
            return null;

        string name = string.IsNullOrWhiteSpace(nameResult.StringResult)
            ? current.Name
            : nameResult.StringResult.Trim();

        double? density = AskPositive(ed, "\nDensidade (kg/m3)", current.Density);
        if (density is null)
            return null;

        double? fy = AskPositive(ed, "\nResistencia de escoamento fyk (MPa)", current.YieldMPa);
        if (fy is null)
            return null;

        double? fu = AskPositive(ed, "\nResistencia de ruptura fuk (MPa)", Math.Max(current.UltimateMPa, fy.Value));
        if (fu is null)
            return null;

        double? e = AskPositive(ed, "\nModulo de elasticidade E (GPa)", current.ElasticModulusGPa);
        if (e is null)
            return null;

        return new SolidLinkMaterial(SolidLinkMaterials.CustomKey, name, density.Value, fy.Value, fu.Value, e.Value);
    }

    private static double? AskPositive(Editor ed, string message, double current)
    {
        PromptDoubleOptions options = new(message + " <" + FormatNumber(current) + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false
        };

        PromptDoubleResult result = ed.GetDouble(options);

        if (result.Status == PromptStatus.None)
            return current;

        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    /// <summary>Retorna a area informada em cm2, ou 0 para "calcular pelo diametro".</summary>
    private static double? AskArea(Editor ed, AcadDb.Database db, LinkParameters p)
    {
        double autoAreaCm2 = CircleAreaCm2(p.Diameter, db);
        string current = p.AreaCm2Override > Epsilon
            ? FormatNumber(p.AreaCm2Override)
            : "Auto=" + FormatNumber(autoAreaCm2, "0.####");

        PromptDoubleOptions options = new(
            "\nArea metalica efetiva por estai (cm2), 0 = Auto pelo diametro <" + current + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = true
        };

        PromptDoubleResult result = ed.GetDouble(options);

        if (result.Status == PromptStatus.None)
            return p.AreaCm2Override;

        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static string? AskLayer(Editor ed, string current)
    {
        if (string.IsNullOrWhiteSpace(current))
            current = DefaultLayer;

        while (true)
        {
            PromptStringOptions options = new("\nLayer dos estais <" + current + ">: ")
            {
                AllowSpaces = true
            };

            PromptResult result = ed.GetString(options);
            if (result.Status != PromptStatus.OK)
                return null;

            string name = string.IsNullOrWhiteSpace(result.StringResult)
                ? current
                : result.StringResult.Trim();

            try
            {
                AcadDb.SymbolUtilityServices.ValidateSymbolName(name, false);
                return name;
            }
            catch
            {
                ed.WriteMessage("\nNome de layer invalido: " + name);
            }
        }
    }

    /// <summary>
    /// Mostra as ligacoes como graficos temporarios e pergunta Inverter/Aceitar.
    /// Retorna o sentido escolhido ou null se o usuario cancelar.
    /// </summary>
    private static bool? ConfirmPairing(
        Editor ed,
        IReadOnlyList<Point3d> pointsA,
        IReadOnlyList<Point3d> pointsB,
        bool reversed)
    {
        List<AcadDb.Entity> transients = new();
        AcGi.TransientManager manager = AcGi.TransientManager.CurrentTransientManager;

        try
        {
            while (true)
            {
                ClearTransients(manager, transients);
                AddPreview(manager, transients, pointsA, pointsB, reversed);
                ed.UpdateScreen();

                PromptKeywordOptions options = new(
                    "\nPrevia das ligacoes (" + (reversed ? "invertida" : "sentido do desenho") +
                    "). [Inverter/Aceitar] <Aceitar>: ",
                    "Inverter Aceitar")
                {
                    AllowNone = true
                };

                PromptResult result = ed.GetKeywords(options);

                if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                    return null;

                if (result.Status == PromptStatus.OK &&
                    string.Equals(result.StringResult, "Inverter", StringComparison.OrdinalIgnoreCase))
                {
                    reversed = !reversed;
                    continue;
                }

                return reversed;
            }
        }
        finally
        {
            ClearTransients(manager, transients);
            ed.UpdateScreen();
        }
    }

    private static void AddPreview(
        AcGi.TransientManager manager,
        List<AcadDb.Entity> transients,
        IReadOnlyList<Point3d> pointsA,
        IReadOnlyList<Point3d> pointsB,
        bool reversed)
    {
        int count = Math.Min(pointsA.Count, pointsB.Count);
        IntegerCollection viewports = new();

        for (int i = 0; i < count; i++)
        {
            Point3d a = pointsA[i];
            Point3d b = pointsB[reversed ? count - 1 - i : i];

            if (a.DistanceTo(b) <= GeometryTolerance)
                continue;

            AcadDb.Line line = new(a, b)
            {
                ColorIndex = i == 0 ? 1 : 4 // primeira ligacao em vermelho, demais em ciano
            };

            manager.AddTransient(line, AcGi.TransientDrawingMode.Highlight, 128, viewports);
            transients.Add(line);
        }
    }

    private static void ClearTransients(AcGi.TransientManager manager, List<AcadDb.Entity> transients)
    {
        IntegerCollection viewports = new();

        foreach (AcadDb.Entity entity in transients)
        {
            try
            {
                manager.EraseTransient(entity, viewports);
            }
            catch
            {
                // Transient ja removido; seguir limpando os demais.
            }

            entity.Dispose();
        }

        transients.Clear();
    }

    /// <summary>
    /// Sugere o sentido que evita estais cruzados: compara a soma das
    /// distancias entre extremidades nas duas combinacoes possiveis.
    /// </summary>
    private static bool SuggestReversed(IReadOnlyList<Point3d> pointsA, IReadOnlyList<Point3d> pointsB)
    {
        if (pointsA.Count < 2 || pointsB.Count < 2)
            return false;

        double direct = pointsA[0].DistanceTo(pointsB[0]) + pointsA[^1].DistanceTo(pointsB[^1]);
        double crossed = pointsA[0].DistanceTo(pointsB[^1]) + pointsA[^1].DistanceTo(pointsB[0]);

        return crossed + GeometryTolerance < direct;
    }

    private static void ReportGeneration(Editor ed, LinkSetData data, GenerationResult result, AcadDb.Database db)
    {
        LinkParameters p = data.Parameters;
        GetMetersPerUnit(db, out string unitLabel);

        ed.WriteMessage(
            "\n  " + result.Created + " estai(s) | D=" + FormatNumber(p.Diameter) +
            " | " + p.Material.Name + " | layer " + p.Layer);

        ed.WriteMessage(
            "\n  Comprimento total=" + FormatNumber(result.TotalLengthM, "0.00") + " m" +
            " | massa total=" + FormatNumber(result.TotalMassKg, "0.0") + " kg" +
            " | unidade do desenho: " + unitLabel);

        if (result.Skipped > 0)
        {
            ed.WriteMessage(
                "\n[AVISO] " + result.Skipped + " ligacao(oes) com comprimento nulo foram ignoradas" +
                " (pontos coincidentes nas duas linhas).");
        }

        if (result.PropertySetFailures > 0)
        {
            ed.WriteMessage(
                "\n[AVISO] Property Set " + SolidLinkPropertySet.DefinitionName + " nao aplicado em " +
                result.PropertySetFailures + " estai(s): " + result.PropertySetMessage +
                " A geometria foi criada normalmente.");
        }
    }

    // ------------------------------------------------------------------
    // Geracao dos solidos
    // ------------------------------------------------------------------

    private static GenerationResult GenerateSolids(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        IReadOnlyList<Point3d> pointsA,
        IReadOnlyList<Point3d> pointsB,
        LinkSetData data)
    {
        LinkParameters p = data.Parameters;
        GenerationResult result = new();

        AcadDb.BlockTableRecord space = (AcadDb.BlockTableRecord)tr.GetObject(
            db.CurrentSpaceId,
            AcadDb.OpenMode.ForWrite);

        AcadDb.ObjectId layerId = EnsureLayer(tr, db, p.Layer);
        double metersPerUnit = GetMetersPerUnit(db, out _);

        double radius = p.Diameter / 2.0;
        double areaCm2 = p.AreaCm2Override > Epsilon ? p.AreaCm2Override : CircleAreaCm2(p.Diameter, db);
        double areaM2 = areaCm2 / 10000.0;
        double linearMass = areaM2 * p.Material.Density;

        int count = Math.Min(pointsA.Count, pointsB.Count);
        int digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);

        data.GeneratedHandles.Clear();

        for (int i = 0; i < count; i++)
        {
            Point3d a = pointsA[i];
            Point3d b = pointsB[data.Reversed ? count - 1 - i : i];

            AcadDb.Solid3d? solid = CreateTube(db, a, b, radius);
            if (solid is null)
            {
                result.Skipped++;
                continue;
            }

            solid.LayerId = layerId;
            space.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
            TagSolid(tr, solid, data.GroupId, i);

            data.GeneratedHandles.Add(solid.Handle.ToString());
            result.Created++;

            Vector3d axis = b - a;
            double lengthM = axis.Length * metersPerUnit;
            double horizontal = Math.Sqrt((axis.X * axis.X) + (axis.Y * axis.Y));
            double inclination = Math.Atan2(Math.Abs(axis.Z), horizontal) * 180.0 / Math.PI;
            double massKg = linearMass * lengthM;

            result.TotalLengthM += lengthM;
            result.TotalMassKg += massKg;

            SolidLinkStayValues stay = new(
                GroupId: data.GroupId,
                Code: "E" + (i + 1).ToString(new string('0', digits), CultureInfo.InvariantCulture),
                Number: i + 1,
                Material: p.Material.Name,
                DiameterMm: p.Diameter * metersPerUnit * 1000.0,
                AreaCm2: areaCm2,
                LengthM: lengthM,
                LinearMassKgM: linearMass,
                MassKg: massKg,
                DensityKgM3: p.Material.Density,
                YieldMPa: p.Material.YieldMPa,
                UltimateMPa: p.Material.UltimateMPa,
                ElasticModulusGPa: p.Material.ElasticModulusGPa,
                YieldLoadKN: areaM2 * p.Material.YieldMPa * 1000.0,
                BreakingLoadKN: areaM2 * p.Material.UltimateMPa * 1000.0,
                InclinationDeg: inclination,
                TopElevationM: Math.Max(a.Z, b.Z) * metersPerUnit,
                BottomElevationM: Math.Min(a.Z, b.Z) * metersPerUnit,
                StartPoint: FormatPoint(a),
                EndPoint: FormatPoint(b),
                TrimLine1: FormatTrim(p.TrimStartA, p.TrimEndA),
                TrimLine2: FormatTrim(p.TrimStartB, p.TrimEndB),
                Line1Handle: data.HandleA,
                Line2Handle: data.HandleB);

            if (!SolidLinkPropertySet.TryApply(tr, db, solid, stay, out string psMessage))
            {
                result.PropertySetFailures++;
                result.PropertySetMessage = psMessage;
            }
        }

        return result;
    }

    /// <summary>
    /// Cilindro com eixo de A ate B. CreateFrustum cria o solido centrado na
    /// origem com altura ao longo de Z; em seguida o eixo Z e alinhado a A->B.
    /// </summary>
    private static AcadDb.Solid3d? CreateTube(AcadDb.Database db, Point3d a, Point3d b, double radius)
    {
        Vector3d axis = b - a;
        double length = axis.Length;

        if (length <= GeometryTolerance || radius <= Epsilon)
            return null;

        Vector3d zAxis = axis / length;
        Vector3d xAxis = zAxis.GetPerpendicularVector().GetNormal();
        Vector3d yAxis = zAxis.CrossProduct(xAxis).GetNormal();
        Point3d middle = a + (axis * 0.5);

        AcadDb.Solid3d solid = new();
        solid.SetDatabaseDefaults(db);
        solid.CreateFrustum(length, radius, radius, radius);
        solid.TransformBy(Matrix3d.AlignCoordinateSystem(
            Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
            middle, xAxis, yAxis, zAxis));

        return solid;
    }

    /// <summary>
    /// Apaga os estais atuais do conjunto. Retorna false (sem apagar nada) se
    /// algum estai estiver em layer travado.
    /// </summary>
    private static bool EraseGenerated(AcadDb.Transaction tr, AcadDb.Database db, LinkSetData data, Editor ed)
    {
        List<AcadDb.Entity> toErase = new();

        foreach (string handle in data.GeneratedHandles)
        {
            AcadDb.ObjectId id = ResolveHandle(db, handle);
            if (id.IsNull || id.IsErased)
                continue;

            if (!string.Equals(TryReadTag(tr, id), data.GroupId, StringComparison.Ordinal))
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity entity)
                continue;

            AcadDb.LayerTableRecord layer = (AcadDb.LayerTableRecord)tr.GetObject(entity.LayerId, AcadDb.OpenMode.ForRead);
            if (layer.IsLocked)
            {
                ed.WriteMessage(
                    "\n[AVISO] Conjunto " + ShortId(data.GroupId) + ": o layer '" + layer.Name +
                    "' esta travado. Destrave-o para recriar os estais.");
                return false;
            }

            toErase.Add(entity);
        }

        foreach (AcadDb.Entity entity in toErase)
        {
            entity.UpgradeOpen();
            entity.Erase();
        }

        data.GeneratedHandles.Clear();
        return true;
    }

    private static AcadDb.ObjectId EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name)
    {
        AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);

        if (table.Has(name))
            return table[name];

        table.UpgradeOpen();

        AcadDb.LayerTableRecord record = new()
        {
            Name = name,
            Color = AcColors.Color.FromColorIndex(AcColors.ColorMethod.ByAci, DefaultLayerColor)
        };

        AcadDb.ObjectId id = table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    private static bool TryOpenPaths(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        LinkSetData data,
        Editor ed,
        out PathSampler? pathA,
        out PathSampler? pathB)
    {
        pathA = null;
        pathB = null;

        AcadDb.ObjectId idA = ResolveHandle(db, data.HandleA);
        AcadDb.ObjectId idB = ResolveHandle(db, data.HandleB);

        if (idA.IsNull || idA.IsErased || idB.IsNull || idB.IsErased)
        {
            ed.WriteMessage(
                "\n[AVISO] Conjunto " + ShortId(data.GroupId) +
                ": uma das linhas de origem foi apagada. Os estais existentes foram mantidos.");
            return false;
        }

        pathA = PathSampler.TryCreate(tr, idA, out string errorA);
        pathB = PathSampler.TryCreate(tr, idB, out string errorB);

        if (pathA is null || pathB is null)
        {
            ed.WriteMessage(
                "\n[AVISO] Conjunto " + ShortId(data.GroupId) + ": " +
                (pathA is null ? errorA : errorB));
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Unidades
    // ------------------------------------------------------------------

    private static double GetMetersPerUnit(AcadDb.Database? db, out string label)
    {
        AcadDb.UnitsValue units = db?.Insunits ?? AcadDb.UnitsValue.Undefined;

        switch (units)
        {
            case AcadDb.UnitsValue.Millimeters:
                label = "mm";
                return 0.001;
            case AcadDb.UnitsValue.Centimeters:
                label = "cm";
                return 0.01;
            case AcadDb.UnitsValue.Meters:
                label = "m";
                return 1.0;
            case AcadDb.UnitsValue.Kilometers:
                label = "km";
                return 1000.0;
            case AcadDb.UnitsValue.Inches:
                label = "pol";
                return 0.0254;
            case AcadDb.UnitsValue.Feet:
                label = "pe";
                return 0.3048;
            default:
                label = "indefinida (assumido m)";
                return 1.0;
        }
    }

    private static double CircleAreaCm2(double diameterDrawingUnits, AcadDb.Database? db)
    {
        double dM = diameterDrawingUnits * GetMetersPerUnit(db, out _);
        return Math.PI * dM * dM / 4.0 * 10000.0;
    }

    // ------------------------------------------------------------------
    // Amostragem das linhas
    // ------------------------------------------------------------------

    /// <summary>
    /// Percorre uma linha por distancia 3D. Usa a API de Curve quando ela
    /// representa a geometria 3D real; para Feature Lines, valida a Curve pelos
    /// vertices e, se nao for confiavel, usa a polilinha 3D dos vertices.
    /// </summary>
    private sealed class PathSampler
    {
        private readonly AcadDb.Curve? _curve;
        private readonly double _curveStartDistance;
        private readonly List<Point3d>? _vertices;
        private readonly List<double>? _cumulative;

        public double Length { get; }
        public string Description { get; }

        private PathSampler(AcadDb.Curve curve, double startDistance, double length, string description)
        {
            _curve = curve;
            _curveStartDistance = startDistance;
            Length = length;
            Description = description;
        }

        private PathSampler(List<Point3d> vertices, string description)
        {
            _vertices = vertices;
            _cumulative = new List<double>(vertices.Count) { 0.0 };

            for (int i = 1; i < vertices.Count; i++)
                _cumulative.Add(_cumulative[i - 1] + vertices[i - 1].DistanceTo(vertices[i]));

            Length = _cumulative[^1];
            Description = description;
        }

        public static bool IsSupportedClass(RXClass objectClass)
        {
            if (objectClass.IsDerivedFrom(RXObject.GetClass(typeof(AutoCorridorFeatureLine))) ||
                objectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilFeatureLine))))
            {
                return true;
            }

            // Demais objetos Civil 3D (Alignment, Profile etc.) derivam de Curve,
            // mas nao representam uma linha 3D utilizavel aqui.
            if (objectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Entity))))
                return false;

            return objectClass.IsDerivedFrom(RXObject.GetClass(typeof(AcadDb.Curve)));
        }

        public static PathSampler? TryCreate(AcadDb.Transaction tr, AcadDb.ObjectId id, out string error)
        {
            error = string.Empty;

            AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            string typeName = obj.GetRXClass().DxfName;

            List<Point3d>? featureVertices = obj switch
            {
                AutoCorridorFeatureLine auto => CleanPoints(auto.GetPoints(FeatureLinePointType.AllPoints)),
                CivilFeatureLine site => CleanPoints(site.GetPoints(FeatureLinePointType.AllPoints)),
                _ => null
            };

            if (featureVertices is not null)
            {
                if (featureVertices.Count < 2)
                {
                    error = "A Feature Line selecionada nao possui pontos suficientes.";
                    return null;
                }

                if (obj is AcadDb.Curve flCurve &&
                    TryCreateFromCurve(flCurve, "Feature Line (curva)", out PathSampler? fromCurve) &&
                    CurveMatchesVertices(flCurve, featureVertices))
                {
                    return fromCurve;
                }

                PathSampler fromVertices = new(featureVertices, "Feature Line (vertices 3D)");
                if (fromVertices.Length <= GeometryTolerance)
                {
                    error = "A Feature Line selecionada tem comprimento nulo.";
                    return null;
                }

                return fromVertices;
            }

            if (obj is AcadDb.Curve curve &&
                TryCreateFromCurve(curve, typeName, out PathSampler? sampler))
            {
                return sampler;
            }

            error = "Nao foi possivel ler a geometria da linha (" + typeName + ").";
            return null;
        }

        private static bool TryCreateFromCurve(AcadDb.Curve curve, string description, out PathSampler? sampler)
        {
            sampler = null;

            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                double length = end - start;

                if (length <= GeometryTolerance)
                    return false;

                sampler = new PathSampler(curve, start, length, description);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Garante que a API de Curve da Feature Line passa pelos vertices 3D
        /// (inclusive cotas). Se algum vertice ficar fora, usa a polilinha.
        /// </summary>
        private static bool CurveMatchesVertices(AcadDb.Curve curve, IReadOnlyList<Point3d> vertices)
        {
            try
            {
                const double tolerance = 1e-3;

                if (curve.StartPoint.DistanceTo(vertices[0]) > tolerance ||
                    curve.EndPoint.DistanceTo(vertices[^1]) > tolerance)
                {
                    return false;
                }

                foreach (Point3d vertex in vertices)
                {
                    Point3d closest = curve.GetClosestPointTo(vertex, false);
                    if (closest.DistanceTo(vertex) > tolerance)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<Point3d> CleanPoints(Point3dCollection collection)
        {
            List<Point3d> points = new(collection.Count);

            foreach (Point3d point in collection)
            {
                if (points.Count == 0 || points[^1].DistanceTo(point) > Epsilon)
                    points.Add(point);
            }

            return points;
        }

        /// <summary>
        /// N pontos igualmente espacados no trecho util, que comeca em
        /// startPercent do comprimento e termina em (100 - endPercent).
        /// Os extremos do trecho util sao incluidos.
        /// </summary>
        public List<Point3d> Distribute(int count, double startPercent, double endPercent)
        {
            double from = Length * Math.Clamp(startPercent, 0.0, 100.0) / 100.0;
            double to = Length * (1.0 - (Math.Clamp(endPercent, 0.0, 100.0) / 100.0));

            if (to < from)
                to = from;

            List<Point3d> result = new(count);

            for (int i = 0; i < count; i++)
            {
                double distance = count == 1 ? from : from + ((to - from) * i / (count - 1));
                bool isStart = i == 0 && startPercent <= Epsilon;
                bool isEnd = i == count - 1 && endPercent <= Epsilon;
                result.Add(PointAt(distance, isStart, isEnd));
            }

            return result;
        }

        private Point3d PointAt(double distance, bool isStart, bool isEnd)
        {
            if (_curve is not null)
            {
                if (isStart)
                    return _curve.StartPoint;
                if (isEnd)
                    return _curve.EndPoint;

                double d = Math.Clamp(_curveStartDistance + distance, _curveStartDistance, _curveStartDistance + Length);
                return _curve.GetPointAtDist(d);
            }

            List<Point3d> vertices = _vertices!;
            List<double> cumulative = _cumulative!;

            if (isStart || distance <= 0.0)
                return vertices[0];
            if (isEnd || distance >= Length)
                return vertices[^1];

            int index = cumulative.BinarySearch(distance);
            if (index >= 0)
                return vertices[index];

            int upper = ~index;
            int lower = upper - 1;
            double segment = cumulative[upper] - cumulative[lower];
            double t = segment <= Epsilon ? 0.0 : (distance - cumulative[lower]) / segment;

            return vertices[lower] + ((vertices[upper] - vertices[lower]) * t);
        }
    }

    // ------------------------------------------------------------------
    // Persistencia
    // ------------------------------------------------------------------

    private static void TagSolid(AcadDb.Transaction tr, AcadDb.Entity entity, string groupId, int index)
    {
        if (entity.ExtensionDictionary.IsNull)
            entity.CreateExtensionDictionary();

        AcadDb.DBDictionary dictionary = (AcadDb.DBDictionary)tr.GetObject(
            entity.ExtensionDictionary,
            AcadDb.OpenMode.ForWrite);

        AcadDb.ResultBuffer buffer = new(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, groupId),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, index));

        if (dictionary.Contains(TagKey))
        {
            AcadDb.Xrecord existing = (AcadDb.Xrecord)tr.GetObject(dictionary.GetAt(TagKey), AcadDb.OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        AcadDb.Xrecord record = new() { Data = buffer };
        dictionary.SetAt(TagKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static string? TryReadTag(AcadDb.Transaction tr, AcadDb.ObjectId id)
    {
        try
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity entity ||
                entity.ExtensionDictionary.IsNull)
            {
                return null;
            }

            AcadDb.DBDictionary dictionary = (AcadDb.DBDictionary)tr.GetObject(
                entity.ExtensionDictionary,
                AcadDb.OpenMode.ForRead);

            if (!dictionary.Contains(TagKey))
                return null;

            AcadDb.Xrecord record = (AcadDb.Xrecord)tr.GetObject(dictionary.GetAt(TagKey), AcadDb.OpenMode.ForRead);
            AcadDb.TypedValue[]? values = record.Data?.AsArray();

            return values is { Length: > 0 } ? values[0].Value as string : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<LinkSetData> ResolveSetsFromSelection(
        AcadDb.Transaction tr,
        List<LinkSetData> allSets,
        IEnumerable<AcadDb.ObjectId> selected)
    {
        HashSet<string> groupIds = new(StringComparer.Ordinal);

        foreach (AcadDb.ObjectId id in selected)
        {
            string? tag = TryReadTag(tr, id);
            if (tag is not null)
            {
                groupIds.Add(tag);
                continue;
            }

            string handle = id.Handle.ToString();
            foreach (LinkSetData set in allSets)
            {
                if (string.Equals(set.HandleA, handle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(set.HandleB, handle, StringComparison.OrdinalIgnoreCase))
                {
                    groupIds.Add(set.GroupId);
                }
            }
        }

        return allSets.Where(set => groupIds.Contains(set.GroupId)).ToList();
    }

    /// <summary>Serializa os parametros (14 valores, em ordem fixa).</summary>
    private static IEnumerable<AcadDb.TypedValue> WriteParameters(LinkParameters p)
    {
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, p.Count);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.TrimStartA);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.TrimEndA);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.TrimStartB);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.TrimEndB);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Diameter);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, p.Material.Key);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, p.Material.Name);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Material.Density);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Material.YieldMPa);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Material.UltimateMPa);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.Material.ElasticModulusGPa);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, p.AreaCm2Override);
        yield return new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, p.Layer);
    }

    private const int ParameterValueCount = 14;

    private static LinkParameters ReadParameters(AcadDb.TypedValue[] values, int offset)
    {
        LinkParameters p = new()
        {
            Count = Math.Max(2, AsInt(values[offset], 10)),
            TrimStartA = AsDouble(values[offset + 1], 0.0),
            TrimEndA = AsDouble(values[offset + 2], 0.0),
            TrimStartB = AsDouble(values[offset + 3], 0.0),
            TrimEndB = AsDouble(values[offset + 4], 0.0),
            Diameter = AsDouble(values[offset + 5], 0.10),
            AreaCm2Override = AsDouble(values[offset + 12], 0.0),
            Layer = AsText(values[offset + 13], DefaultLayer)
        };

        string key = AsText(values[offset + 6], SolidLinkMaterials.Default.Key);
        p.Material = SolidLinkMaterials.Find(key) ?? new SolidLinkMaterial(
            SolidLinkMaterials.CustomKey,
            AsText(values[offset + 7], "Material personalizado"),
            AsDouble(values[offset + 8], 7850.0),
            AsDouble(values[offset + 9], 250.0),
            AsDouble(values[offset + 10], 400.0),
            AsDouble(values[offset + 11], 200.0));

        return p;
    }

    private static LinkParameters LoadSettings(AcadDb.Transaction tr, AcadDb.Database db)
    {
        LinkParameters settings = new();
        AcadDb.DBDictionary? root = TryGetRootDictionary(tr, db);

        if (root is null || !root.Contains(SettingsKey))
            return settings;

        AcadDb.Xrecord record = (AcadDb.Xrecord)tr.GetObject(root.GetAt(SettingsKey), AcadDb.OpenMode.ForRead);
        AcadDb.TypedValue[]? values = record.Data?.AsArray();

        if (values is null || values.Length == 0)
            return settings;

        if (values[0].Value is string signature &&
            signature == SettingsSignatureV2 &&
            values.Length >= 1 + ParameterValueCount)
        {
            return ReadParameters(values, 1);
        }

        // v1.0: [Count, Diameter]
        if (values.Length >= 2)
        {
            if (values[0].Value is int count && count >= 2)
                settings.Count = count;
            if (values[1].Value is double diameter && diameter > Epsilon)
                settings.Diameter = diameter;
        }

        return settings;
    }

    private static void SaveSettings(AcadDb.Transaction tr, AcadDb.Database db, LinkParameters settings)
    {
        List<AcadDb.TypedValue> values = new()
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, SettingsSignatureV2)
        };
        values.AddRange(WriteParameters(settings));

        WriteRecord(tr, db, SettingsKey, new AcadDb.ResultBuffer(values.ToArray()));
    }

    private static void SaveSet(AcadDb.Transaction tr, AcadDb.Database db, LinkSetData data)
    {
        List<AcadDb.TypedValue> values = new()
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, RecordSignatureV2),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.GroupId),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.HandleA),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.HandleB),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, data.Reversed ? 1 : 0)
        };

        values.AddRange(WriteParameters(data.Parameters));

        foreach (string handle in data.GeneratedHandles)
            values.Add(new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, handle));

        WriteRecord(tr, db, SetKeyPrefix + data.GroupId, new AcadDb.ResultBuffer(values.ToArray()));
    }

    private static LinkSetData? LoadSet(AcadDb.Transaction tr, AcadDb.Database db, string groupId)
    {
        AcadDb.DBDictionary? root = TryGetRootDictionary(tr, db);
        string key = SetKeyPrefix + groupId;

        if (root is null || !root.Contains(key))
            return null;

        AcadDb.Xrecord record = (AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForRead);
        return ParseSet(record);
    }

    private static List<LinkSetData> LoadAllSets(AcadDb.Transaction tr, AcadDb.Database db)
    {
        List<LinkSetData> sets = new();
        AcadDb.DBDictionary? root = TryGetRootDictionary(tr, db);

        if (root is null)
            return sets;

        foreach (AcadDb.DBDictionaryEntry entry in root)
        {
            if (!entry.Key.StartsWith(SetKeyPrefix, StringComparison.Ordinal))
                continue;

            if (tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead) is AcadDb.Xrecord record &&
                ParseSet(record) is LinkSetData data)
            {
                sets.Add(data);
            }
        }

        return sets;
    }

    private static LinkSetData? ParseSet(AcadDb.Xrecord record)
    {
        AcadDb.TypedValue[]? values = record.Data?.AsArray();
        if (values is null || values.Length < 4)
            return null;

        string signature = values[0].Value as string ?? string.Empty;

        LinkSetData data = new()
        {
            GroupId = AsText(values[1], string.Empty),
            HandleA = AsText(values[2], string.Empty),
            HandleB = AsText(values[3], string.Empty)
        };

        int handlesStart;

        if (signature == RecordSignatureV2 && values.Length >= 5 + ParameterValueCount)
        {
            data.Reversed = AsInt(values[4], 0) != 0;
            data.Parameters = ReadParameters(values, 5);
            handlesStart = 5 + ParameterValueCount;
        }
        else if (signature == RecordSignatureV1 && values.Length >= 7)
        {
            // v1.0: [sig, group, A, B, Count, Diameter, Reversed, handles...]
            data.Parameters = new LinkParameters
            {
                Count = Math.Max(2, AsInt(values[4], 2)),
                Diameter = AsDouble(values[5], 0.10)
            };
            data.Reversed = AsInt(values[6], 0) != 0;
            handlesStart = 7;
        }
        else
        {
            return null;
        }

        for (int i = handlesStart; i < values.Length; i++)
        {
            if (values[i].Value is string handle && !string.IsNullOrWhiteSpace(handle))
                data.GeneratedHandles.Add(handle);
        }

        return string.IsNullOrWhiteSpace(data.GroupId) ? null : data;
    }

    private static int AsInt(AcadDb.TypedValue value, int fallback) =>
        value.Value switch
        {
            int i => i,
            short s => s,
            long l => (int)l,
            _ => fallback
        };

    private static double AsDouble(AcadDb.TypedValue value, double fallback) =>
        value.Value is double d ? d : fallback;

    private static string AsText(AcadDb.TypedValue value, string fallback) =>
        value.Value is string s && !string.IsNullOrWhiteSpace(s) ? s : fallback;

    private static void WriteRecord(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.ResultBuffer buffer)
    {
        AcadDb.DBDictionary root = GetOrCreateRootDictionary(tr, db);

        if (root.Contains(key))
        {
            AcadDb.Xrecord existing = (AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        AcadDb.Xrecord record = new() { Data = buffer };
        root.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static AcadDb.DBDictionary? TryGetRootDictionary(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);

        return nod.Contains(RootDictionaryName)
            ? (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForRead)
            : null;
    }

    private static AcadDb.DBDictionary GetOrCreateRootDictionary(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);

        if (nod.Contains(RootDictionaryName))
            return (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForWrite);

        nod.UpgradeOpen();

        AcadDb.DBDictionary root = new();
        nod.SetAt(RootDictionaryName, root);
        tr.AddNewlyCreatedDBObject(root, true);

        return root;
    }

    private static AcadDb.ObjectId ResolveHandle(AcadDb.Database db, string handleText)
    {
        if (string.IsNullOrWhiteSpace(handleText))
            return AcadDb.ObjectId.Null;

        try
        {
            long value = Convert.ToInt64(handleText, 16);
            return db.GetObjectId(false, new AcadDb.Handle(value), 0);
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    // ------------------------------------------------------------------
    // Formatacao
    // ------------------------------------------------------------------

    private static string FormatNumber(double value, string format = "0.###") =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatTrim(double start, double end) =>
        "ini " + FormatNumber(start, "0.##") + "% / fim " + FormatNumber(end, "0.##") + "%";

    private static string FormatPoint(Point3d p) =>
        FormatNumber(p.X, "0.000") + ";" + FormatNumber(p.Y, "0.000") + ";" + FormatNumber(p.Z, "0.000");

    private static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id) ? "?" : (id.Length <= 8 ? id : id[..8]);
}
