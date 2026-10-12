using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcGi = Autodesk.AutoCAD.GraphicsInterface;
using AutoCorridorFeatureLine = Autodesk.Civil.DatabaseServices.AutoCorridorFeatureLine;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using FeatureLinePointType = Autodesk.Civil.FeatureLinePointType;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>
/// C3DSOLIDLIGAR: liga duas linhas quaisquer (Line, Arc, Polyline, Polyline2d,
/// Polyline3d, Spline, Feature Line ou AutoCorridorFeatureLine) por barras
/// tubulares (Solid3d) entre N pontos igualmente espacados em cada linha.
///
/// Regras principais:
/// - os N pontos incluem inicio e fim de cada linha;
/// - o espacamento e medido pelo comprimento 3D de cada linha, independentemente;
/// - o ponto i da linha A liga ao ponto i da linha B (ou N-1-i, se invertido);
/// - antes de gerar, mostra previa temporaria das ligacoes com opcao [Inverter];
/// - um Solid3d por barra, de secao circular com o diametro informado;
/// - o conjunto fica persistido no DWG (Named Objects Dictionary) e e
///   reconstruido por C3DSOLIDLIGARATUALIZAR apos alteracao das linhas.
/// </summary>
public sealed class C3DSolidLinkCommands
{
    public const string Versao = C3DVersions.SolidLink;

    private const string RootDictionaryName = "C3D_SOLID_LIGAR_V1";
    private const string SettingsKey = "SETTINGS";
    private const string SetKeyPrefix = "SET_";
    private const string TagKey = "C3D_SOLID_LIGAR_TAG";
    private const string RecordSignature = "C3DSOLIDLIGAR_V1";

    private const double Epsilon = 1e-9;
    private const double GeometryTolerance = 1e-4;
    private const int MaxPoints = 5000;

    private sealed class LinkSettings
    {
        public int Count { get; set; } = 10;
        public double Diameter { get; set; } = 0.10;
    }

    private sealed class LinkSetData
    {
        public string GroupId { get; set; } = string.Empty;
        public string HandleA { get; set; } = string.Empty;
        public string HandleB { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Diameter { get; set; }
        public bool Reversed { get; set; }
        public List<string> GeneratedHandles { get; } = new();
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
                " - barras tubulares entre duas linhas.");

            ObjectIdPair? picked = PickTwoLines(ed);
            if (picked is null)
                return;

            LinkSettings settings;
            using (AcadDb.Transaction trSettings = db.TransactionManager.StartTransaction())
            {
                settings = LoadSettings(trSettings, db);
                trSettings.Commit();
            }

            int? count = AskCount(ed, settings.Count);
            if (count is null)
                return;

            double? diameter = AskDiameter(ed, settings.Diameter);
            if (diameter is null)
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
                "\nLinha 2: " + pathB.Description + " | L=" + FormatNumber(pathB.Length));

            List<Point3d> pointsA = pathA.Distribute(count.Value);
            List<Point3d> pointsB = pathB.Distribute(count.Value);

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
                Count = count.Value,
                Diameter = diameter.Value,
                Reversed = reversed.Value
            };

            int skipped = GenerateSolids(tr, db, pointsA, pointsB, data);

            SaveSet(tr, db, data);
            settings.Count = count.Value;
            settings.Diameter = diameter.Value;
            SaveSettings(tr, db, settings);

            tr.Commit();

            ed.WriteMessage(
                "\nConjunto criado: " + data.GeneratedHandles.Count + " barra(s), D=" +
                FormatNumber(data.Diameter) + ". ID=" + ShortId(data.GroupId));

            if (skipped > 0)
            {
                ed.WriteMessage(
                    "\n[AVISO] " + skipped + " ligacao(oes) com comprimento nulo foram ignoradas" +
                    " (pontos coincidentes nas duas linhas).");
            }

            ed.WriteMessage(
                "\nApos alterar as linhas, use " + C3DCommands.Corridor.SolidLigarAtualizar +
                ". Para mudar N, diametro ou sentido, use " + C3DCommands.Corridor.SolidLigarEditar + ".");
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
            PromptEntityOptions options = new("\nSelecione uma barra do conjunto: ");
            options.SetRejectMessage("\nSelecione um 3D Solid gerado por " + C3DCommands.Corridor.SolidLigar + ".");
            options.AddAllowedClass(typeof(AcadDb.Solid3d), true);

            PromptEntityResult result = ed.GetEntity(options);
            if (result.Status != PromptStatus.OK)
                return;

            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

            string? groupId = TryReadTag(tr, result.ObjectId);
            LinkSetData? data = groupId is null ? null : LoadSet(tr, db, groupId);
            if (data is null)
            {
                ed.WriteMessage("\n[ERRO] O solido selecionado nao pertence a um conjunto " + C3DCommands.Corridor.SolidLigar + ".");
                return;
            }

            int? count = AskCount(ed, data.Count);
            if (count is null)
                return;

            double? diameter = AskDiameter(ed, data.Diameter);
            if (diameter is null)
                return;

            if (!TryOpenPaths(tr, db, data, ed, out PathSampler? pathA, out PathSampler? pathB))
                return;

            List<Point3d> pointsA = pathA!.Distribute(count.Value);
            List<Point3d> pointsB = pathB!.Distribute(count.Value);

            bool? reversed = ConfirmPairing(ed, pointsA, pointsB, data.Reversed);
            if (reversed is null)
            {
                ed.WriteMessage("\nEdicao cancelada. O conjunto nao foi alterado.");
                return;
            }

            data.Count = count.Value;
            data.Diameter = diameter.Value;
            data.Reversed = reversed.Value;

            EraseGenerated(tr, db, data);
            int skipped = GenerateSolids(tr, db, pointsA, pointsB, data);
            SaveSet(tr, db, data);
            tr.Commit();

            ed.WriteMessage(
                "\nConjunto " + ShortId(data.GroupId) + " recriado: " + data.GeneratedHandles.Count +
                " barra(s), D=" + FormatNumber(data.Diameter) + ".");

            if (skipped > 0)
                ed.WriteMessage("\n[AVISO] " + skipped + " ligacao(oes) com comprimento nulo foram ignoradas.");
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
                    MessageForAdding = "\nSelecione barras geradas e/ou as linhas de origem: "
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
                ed.WriteMessage("\nA selecao nao contem barras nem linhas de conjuntos " + C3DCommands.Corridor.SolidLigar + ".");
                return;
            }

            int updated = 0;
            int failed = 0;

            foreach (LinkSetData data in targets)
            {
                if (!TryOpenPaths(tr, db, data, ed, out PathSampler? pathA, out PathSampler? pathB))
                {
                    failed++;
                    continue;
                }

                List<Point3d> pointsA = pathA!.Distribute(data.Count);
                List<Point3d> pointsB = pathB!.Distribute(data.Count);

                EraseGenerated(tr, db, data);
                GenerateSolids(tr, db, pointsA, pointsB, data);
                SaveSet(tr, db, data);
                updated++;
            }

            tr.Commit();

            ed.WriteMessage(
                "\nConjuntos atualizados: " + updated +
                (failed > 0 ? " | nao atualizados: " + failed + " (ver mensagens acima)." : "."));
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
        ed.WriteMessage("\n  1) selecione a primeira linha;");
        ed.WriteMessage("\n  2) selecione a segunda linha;");
        ed.WriteMessage("\n  3) informe a quantidade de pontos N (inclui inicio e fim);");
        ed.WriteMessage("\n  4) informe o diametro do tubo (unidades do desenho);");
        ed.WriteMessage("\n  5) confira a previa temporaria; [Inverter] troca o sentido da");
        ed.WriteMessage("\n     segunda linha, Enter aceita e gera as barras.");
        ed.WriteMessage("\n  Linhas aceitas: Line, Arc, Polyline, Polyline 2D/3D, Spline,");
        ed.WriteMessage("\n  Feature Line e AutoCorridorFeatureLine.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidLigarEditar);
        ed.WriteMessage("\n  Selecione uma barra e altere N, diametro ou sentido.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidLigarAtualizar);
        ed.WriteMessage("\n  Recria os conjuntos apos alterar as linhas de origem.");
        ed.WriteMessage("\n  Selecione barras ou linhas, ou use a opcao Todos.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\nREGRAS:");
        ed.WriteMessage("\n  Espacamento igual pelo comprimento 3D de cada linha.");
        ed.WriteMessage("\n  Um Solid3d por barra; barras de comprimento nulo sao ignoradas.");
        ed.WriteMessage("\n  Barras apagadas manualmente voltam ao atualizar o conjunto.");
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

    private static int? AskCount(Editor ed, int current)
    {
        current = Math.Max(2, current);

        // Valor padrao exibido manualmente e aplicado no Enter (mesmo padrao do C3DSOLIDARRAY).
        PromptIntegerOptions options = new(
            "\nQuantidade de pontos em cada linha (inclui inicio e fim) <" + current + ">: ")
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

    private static double? AskDiameter(Editor ed, double current)
    {
        if (current <= Epsilon)
            current = 0.10;

        PromptDistanceOptions options = new(
            "\nDiametro do tubo (unidades do desenho) <" + FormatNumber(current) + ">: ")
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
    /// Sugere o sentido que evita barras cruzadas: compara a soma das
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

    // ------------------------------------------------------------------
    // Geracao dos solidos
    // ------------------------------------------------------------------

    private static int GenerateSolids(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        IReadOnlyList<Point3d> pointsA,
        IReadOnlyList<Point3d> pointsB,
        LinkSetData data)
    {
        AcadDb.BlockTableRecord space = (AcadDb.BlockTableRecord)tr.GetObject(
            db.CurrentSpaceId,
            AcadDb.OpenMode.ForWrite);

        double radius = data.Diameter / 2.0;
        int count = Math.Min(pointsA.Count, pointsB.Count);
        int skipped = 0;

        data.GeneratedHandles.Clear();

        for (int i = 0; i < count; i++)
        {
            Point3d a = pointsA[i];
            Point3d b = pointsB[data.Reversed ? count - 1 - i : i];

            AcadDb.Solid3d? solid = CreateTube(db, a, b, radius);
            if (solid is null)
            {
                skipped++;
                continue;
            }

            space.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
            TagSolid(tr, solid, data.GroupId, i);

            data.GeneratedHandles.Add(solid.Handle.ToString());
        }

        return skipped;
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

    private static void EraseGenerated(AcadDb.Transaction tr, AcadDb.Database db, LinkSetData data)
    {
        foreach (string handle in data.GeneratedHandles)
        {
            AcadDb.ObjectId id = ResolveHandle(db, handle);
            if (id.IsNull || id.IsErased)
                continue;

            if (tr.GetObject(id, AcadDb.OpenMode.ForWrite, false) is AcadDb.Entity entity &&
                string.Equals(TryReadTag(tr, id), data.GroupId, StringComparison.Ordinal))
            {
                entity.Erase();
            }
        }

        data.GeneratedHandles.Clear();
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
                ": uma das linhas de origem foi apagada. As barras existentes foram mantidas.");
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

        /// <summary>N pontos igualmente espacados, incluindo inicio e fim.</summary>
        public List<Point3d> Distribute(int count)
        {
            List<Point3d> result = new(count);

            for (int i = 0; i < count; i++)
            {
                double distance = count == 1 ? 0.0 : Length * i / (count - 1);
                result.Add(PointAt(distance, i == 0, i == count - 1));
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

    private static LinkSettings LoadSettings(AcadDb.Transaction tr, AcadDb.Database db)
    {
        LinkSettings settings = new();
        AcadDb.DBDictionary? root = TryGetRootDictionary(tr, db);

        if (root is null || !root.Contains(SettingsKey))
            return settings;

        AcadDb.Xrecord record = (AcadDb.Xrecord)tr.GetObject(root.GetAt(SettingsKey), AcadDb.OpenMode.ForRead);
        AcadDb.TypedValue[]? values = record.Data?.AsArray();

        if (values is { Length: >= 2 })
        {
            if (values[0].Value is int count && count >= 2)
                settings.Count = count;
            if (values[1].Value is double diameter && diameter > Epsilon)
                settings.Diameter = diameter;
        }

        return settings;
    }

    private static void SaveSettings(AcadDb.Transaction tr, AcadDb.Database db, LinkSettings settings)
    {
        AcadDb.ResultBuffer buffer = new(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, settings.Count),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, settings.Diameter));

        WriteRecord(tr, db, SettingsKey, buffer);
    }

    private static void SaveSet(AcadDb.Transaction tr, AcadDb.Database db, LinkSetData data)
    {
        List<AcadDb.TypedValue> values = new()
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, RecordSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.GroupId),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.HandleA),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.HandleB),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, data.Count),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.Diameter),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, data.Reversed ? 1 : 0)
        };

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
        if (values is null || values.Length < 7)
            return null;

        if (!string.Equals(values[0].Value as string, RecordSignature, StringComparison.Ordinal))
            return null;

        LinkSetData data = new()
        {
            GroupId = values[1].Value as string ?? string.Empty,
            HandleA = values[2].Value as string ?? string.Empty,
            HandleB = values[3].Value as string ?? string.Empty,
            Count = values[4].Value is int count ? count : 2,
            Diameter = values[5].Value is double diameter ? diameter : 0.10,
            Reversed = values[6].Value is int reversed && reversed != 0
        };

        for (int i = 7; i < values.Length; i++)
        {
            if (values[i].Value is string handle && !string.IsNullOrWhiteSpace(handle))
                data.GeneratedHandles.Add(handle);
        }

        return string.IsNullOrWhiteSpace(data.GroupId) ? null : data;
    }

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

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id) ? "?" : (id.Length <= 8 ? id : id[..8]);
}
