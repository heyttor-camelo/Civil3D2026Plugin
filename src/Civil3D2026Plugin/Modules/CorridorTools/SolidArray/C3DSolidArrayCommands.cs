using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AutoCorridorFeatureLine = Autodesk.Civil.DatabaseServices.AutoCorridorFeatureLine;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>
/// Array associativo (por atualização explícita) de Solid3d ao longo de uma
/// AutoCorridorFeatureLine.
///
/// Regras principais:
/// - insere um Solid3d em cada ponto retornado por FeatureLine.GetPoints(AllPoints);
/// - o ponto-base do sólido é definido pelo usuário e normalizado em uma cópia-modelo
///   persistente dentro do DWG;
/// - rotação SOMENTE em XY, em torno do eixo Z global; nunca inclina o sólido;
/// - X = deslocamento longitudinal, Y = transversal (+ esquerda), Z = vertical global;
/// - os dados do conjunto ficam persistidos no Named Objects Dictionary;
/// - C3DSOLIDATUALIZAR reconstrói o conjunto após alteração/rebuild da feature line.
/// - C3DSOLIDITEM permite offset individual, desassociação temporária e reassociação.
/// </summary>
public sealed class C3DSolidArrayCommands
{
    public const string Versao = C3DVersions.SolidArray;

    private const string RootDictionaryName = "C3D_SOLID_ARRAY_V1";
    private const string SettingsKey = "SETTINGS";
    private const string SetKeyPrefix = "ARRAY_";
    private const string GeneratedTagKey = "C3D_SOLID_ARRAY_TAG";
    private const string RecordSignature = "C3DSOLIDARRAY_V1";
    private const string TemplateNamePrefix = "C3D_SOLID_TEMPLATE_";
    private const string DetachedMarker = "#DETACHED#";
    private const string OverridesMarker = "#OVERRIDES#";
    private const string SolidStateManaged = "MANAGED";
    private const string SolidStateDetached = "DETACHED";

    private const double Epsilon = 1e-9;

    private sealed class DefaultSettings
    {
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double OffsetZ { get; set; }
    }

    private sealed class ItemOffsetData
    {
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double OffsetZ { get; set; }
    }

    private sealed class SolidTagData
    {
        public string GroupId { get; set; } = string.Empty;
        public int Index { get; set; } = -1;
        public string State { get; set; } = SolidStateManaged;
    }

    private sealed class ArraySetData
    {
        public string GroupId { get; set; } = string.Empty;
        public string FeatureLineHandle { get; set; } = string.Empty;
        public string TemplateBlockHandle { get; set; } = string.Empty;
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public double OffsetZ { get; set; }
        public List<string> GeneratedHandles { get; } = new();
        public HashSet<int> DetachedIndices { get; } = new();
        public Dictionary<int, ItemOffsetData> ItemOffsets { get; } = new();
    }

    [CommandMethod(C3DCommands.Corridor.SolidCfg, CommandFlags.Modal)]
    public void ConfigurarOffsetsPadrao()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();

            DefaultSettings settings = LoadSettings(tr, db);

            ed.WriteMessage(
                "\n" + C3DCommands.Corridor.SolidCfg + " v" + Versao +
                " - deslocamentos locais padrão.");

            ed.WriteMessage(
                "\nX = longitudinal | Y = transversal (+ esquerda) | Z = vertical global.");

            double? x = AskDouble(
                ed,
                "\nDeslocamento X longitudinal",
                settings.OffsetX);
            if (!x.HasValue)
                return;

            double? y = AskDouble(
                ed,
                "\nDeslocamento Y transversal (+ esquerda / - direita)",
                settings.OffsetY);
            if (!y.HasValue)
                return;

            double? z = AskDouble(
                ed,
                "\nDeslocamento Z vertical",
                settings.OffsetZ);
            if (!z.HasValue)
                return;

            settings.OffsetX = x.Value;
            settings.OffsetY = y.Value;
            settings.OffsetZ = z.Value;

            SaveSettings(tr, db, settings);
            tr.Commit();

            ed.WriteMessage(
                "\nConfiguração salva no DWG: " +
                FormatOffsets(settings.OffsetX, settings.OffsetY, settings.OffsetZ));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO C3DSOLIDCFG] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidArray, CommandFlags.Modal)]
    public void CriarArray()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        try
        {
            DefaultSettings settings;
            using (Transaction trSettings = db.TransactionManager.StartTransaction())
            {
                settings = LoadSettings(trSettings, db);
                trSettings.Commit();
            }

            ed.WriteMessage(
                "\n" + C3DCommands.Corridor.SolidArray + " v" + Versao +
                " - sólidos nos vértices de AutoCorridorFeatureLine.");

            ed.WriteMessage(
                "\nOffsets atuais: " +
                FormatOffsets(settings.OffsetX, settings.OffsetY, settings.OffsetZ));

            PromptEntityOptions solidOptions = new(
                "\nSelecione o 3D SOLID modelo: ");
            solidOptions.SetRejectMessage(
                "\nSelecione um objeto 3D Solid.");
            solidOptions.AddAllowedClass(typeof(Solid3d), true);

            PromptEntityResult solidResult = ed.GetEntity(solidOptions);
            if (solidResult.Status != PromptStatus.OK)
                return;

            PromptPointOptions baseOptions = new(
                "\nInforme o PONTO-BASE de ancoragem do sólido: ");

            PromptPointResult baseResult = ed.GetPoint(baseOptions);
            if (baseResult.Status != PromptStatus.OK)
                return;

            Point3d basePointWcs = ToWcs(ed, baseResult.Value);

            PromptPointOptions directionOptions = new(
                "\nIndique um segundo ponto na DIREÇÃO LONGITUDINAL do sólido: ")
            {
                UseBasePoint = true,
                BasePoint = baseResult.Value
            };

            PromptPointResult directionResult = ed.GetPoint(directionOptions);
            if (directionResult.Status != PromptStatus.OK)
                return;

            Point3d directionPointWcs = ToWcs(ed, directionResult.Value);

            Vector3d sourceDirection = directionPointWcs - basePointWcs;
            Vector3d sourceDirectionXY = new(
                sourceDirection.X,
                sourceDirection.Y,
                0.0);

            if (sourceDirectionXY.Length <= Epsilon)
            {
                ed.WriteMessage(
                    "\n[ERRO] A direção longitudinal precisa possuir componente XY.");
                return;
            }

            double sourceAngle = Math.Atan2(
                sourceDirectionXY.Y,
                sourceDirectionXY.X);

            PromptEntityOptions flOptions = new(
                "\nSelecione a AutoCorridorFeatureLine: ");
            flOptions.SetRejectMessage(
                "\nSelecione uma AutoCorridorFeatureLine dinâmica extraída de Corridor.");
            flOptions.AddAllowedClass(
                typeof(AutoCorridorFeatureLine),
                false);

            PromptEntityResult flResult = ed.GetEntity(flOptions);
            if (flResult.Status != PromptStatus.OK)
                return;

            using Transaction tr = db.TransactionManager.StartTransaction();

            Solid3d? sourceSolid = tr.GetObject(
                solidResult.ObjectId,
                OpenMode.ForRead,
                false) as Solid3d;

            AutoCorridorFeatureLine? featureLine = tr.GetObject(
                flResult.ObjectId,
                OpenMode.ForRead,
                false) as AutoCorridorFeatureLine;

            if (sourceSolid is null)
            {
                ed.WriteMessage("\n[ERRO] O sólido modelo não pôde ser aberto.");
                return;
            }

            if (featureLine is null)
            {
                ed.WriteMessage(
                    "\n[ERRO] O objeto selecionado não é AutoCorridorFeatureLine.");
                return;
            }

            List<Point3d> points = GetCleanPoints(featureLine);
            if (points.Count == 0)
            {
                ed.WriteMessage("\n[ERRO] A feature line não possui pontos válidos.");
                return;
            }

            string groupId = Guid.NewGuid().ToString("N");

            ObjectId templateBlockId = CreateNormalizedTemplate(
                tr,
                db,
                sourceSolid,
                basePointWcs,
                sourceAngle,
                groupId);

            ArraySetData data = new()
            {
                GroupId = groupId,
                FeatureLineHandle = flResult.ObjectId.Handle.ToString(),
                TemplateBlockHandle = templateBlockId.Handle.ToString(),
                OffsetX = settings.OffsetX,
                OffsetY = settings.OffsetY,
                OffsetZ = settings.OffsetZ
            };

            int created = GenerateSolids(
                tr,
                db,
                featureLine,
                templateBlockId,
                data);

            SaveSet(tr, db, data);
            tr.Commit();

            ed.WriteMessage(
                "\nConjunto criado: " + created + " sólido(s)." +
                " ID=" + ShortId(groupId));

            ed.WriteMessage(
                "\nApós rebuild/alteração da feature line, use " + C3DCommands.Corridor.SolidAtualizar + ".");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO C3DSOLIDARRAY] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidAtualizar, CommandFlags.Modal)]
    public void AtualizarArray()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        try
        {
            PromptEntityOptions peo = new(
                "\nSelecione um sólido gerado OU a AutoCorridorFeatureLine vinculada: ");

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;

            using Transaction tr = db.TransactionManager.StartTransaction();

            DBObject selected = tr.GetObject(
                per.ObjectId,
                OpenMode.ForRead,
                false);

            List<ArraySetData> sets = ResolveSetsFromSelection(
                tr,
                db,
                selected,
                per.ObjectId);

            if (sets.Count == 0)
            {
                ed.WriteMessage(
                    "\nNenhum conjunto C3DSOLIDARRAY associado ao objeto selecionado.");
                return;
            }

            int totalCreated = 0;
            int updated = 0;
            int failed = 0;

            foreach (ArraySetData data in sets)
            {
                if (TryRegenerateSet(
                    tr,
                    db,
                    data,
                    out int created,
                    out string error))
                {
                    totalCreated += created;
                    updated++;
                }
                else
                {
                    failed++;
                    ed.WriteMessage(
                        "\n[AVISO] Conjunto " + ShortId(data.GroupId) +
                        " não atualizado: " + error);
                }
            }

            tr.Commit();

            ed.WriteMessage(
                "\nAtualização concluída: " + updated +
                " conjunto(s), " + totalCreated + " sólido(s) recriado(s)." +
                (failed > 0 ? " Falhas=" + failed + "." : string.Empty));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO C3DSOLIDATUALIZAR] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidEditar, CommandFlags.Modal)]
    public void EditarArray()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        try
        {
            PromptEntityOptions peo = new(
                "\nSelecione um sólido gerado do conjunto a editar: ");

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;

            using Transaction tr = db.TransactionManager.StartTransaction();

            DBObject selected = tr.GetObject(
                per.ObjectId,
                OpenMode.ForRead,
                false);

            string? groupId = TryReadGeneratedGroupId(tr, selected);
            if (string.IsNullOrWhiteSpace(groupId))
            {
                ed.WriteMessage(
                    "\nO objeto selecionado não pertence a um C3DSOLIDARRAY.");
                return;
            }

            ArraySetData? data = LoadSet(tr, db, groupId);
            if (data is null)
            {
                ed.WriteMessage("\nDados persistidos do conjunto não encontrados.");
                return;
            }

            ed.WriteMessage(
                "\nEditando conjunto " + ShortId(groupId) + ".");

            double? x = AskDouble(
                ed,
                "\nDeslocamento X longitudinal",
                data.OffsetX);
            if (!x.HasValue)
                return;

            double? y = AskDouble(
                ed,
                "\nDeslocamento Y transversal (+ esquerda / - direita)",
                data.OffsetY);
            if (!y.HasValue)
                return;

            double? z = AskDouble(
                ed,
                "\nDeslocamento Z vertical",
                data.OffsetZ);
            if (!z.HasValue)
                return;

            data.OffsetX = x.Value;
            data.OffsetY = y.Value;
            data.OffsetZ = z.Value;

            if (!TryRegenerateSet(
                tr,
                db,
                data,
                out int created,
                out string error))
            {
                ed.WriteMessage("\n[ERRO] " + error);
                return;
            }

            tr.Commit();

            ed.WriteMessage(
                "\nConjunto atualizado: " + created + " sólido(s) | " +
                FormatOffsets(data.OffsetX, data.OffsetY, data.OffsetZ));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO C3DSOLIDEDITAR] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidItem, CommandFlags.Modal)]
    public void EditarItemIndividual()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        Database db = doc.Database;

        try
        {
            PromptEntityOptions peo = new(
                "\nSelecione um sólido do C3DSOLIDARRAY: ");
            peo.SetRejectMessage("\nSelecione um 3D Solid pertencente ao C3DSOLIDARRAY.");
            peo.AddAllowedClass(typeof(Solid3d), true);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;

            using Transaction tr = db.TransactionManager.StartTransaction();

            Solid3d? selected = tr.GetObject(
                per.ObjectId,
                OpenMode.ForRead,
                false) as Solid3d;

            if (selected is null)
            {
                ed.WriteMessage("\nO objeto selecionado não é um 3D Solid válido.");
                return;
            }

            SolidTagData? tag = TryReadSolidTag(tr, selected);
            if (tag is null || string.IsNullOrWhiteSpace(tag.GroupId))
            {
                ed.WriteMessage("\nO sólido selecionado não pertence a um C3DSOLIDARRAY.");
                return;
            }

            ArraySetData? data = LoadSet(tr, db, tag.GroupId);
            if (data is null)
            {
                ed.WriteMessage("\nDados persistidos do conjunto não encontrados.");
                return;
            }

            EnsureLegacyItemTags(tr, db, data);

            string selectedHandle = per.ObjectId.Handle.ToString();
            int index = ResolveItemIndex(data, selectedHandle, tag.Index);

            if (index < 0)
            {
                ed.WriteMessage("\nNão foi possível identificar o vértice associado a este sólido.");
                return;
            }

            bool detached =
                string.Equals(tag.State, SolidStateDetached, StringComparison.OrdinalIgnoreCase) ||
                data.DetachedIndices.Contains(index);

            if (detached)
            {
                PromptKeywordOptions detachedOptions = new(
                    "\nEste sólido está DESASSOCIADO. Ação [Reassociar/Cancelar] <Reassociar>: ")
                {
                    AllowNone = true
                };
                detachedOptions.Keywords.Add("Reassociar");
                detachedOptions.Keywords.Add("Cancelar");

                PromptResult detachedResult = ed.GetKeywords(detachedOptions);
                if (detachedResult.Status == PromptStatus.Cancel ||
                    (detachedResult.Status == PromptStatus.OK &&
                     string.Equals(detachedResult.StringResult, "Cancelar", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                data.DetachedIndices.Remove(index);

                if (!TryRegenerateSingleItem(
                    tr,
                    db,
                    data,
                    index,
                    per.ObjectId,
                    selectedHandle,
                    out string reassociateError))
                {
                    ed.WriteMessage("\n[ERRO] " + reassociateError);
                    return;
                }

                tr.Commit();

                ed.WriteMessage(
                    "\nSólido reassociado ao vértice " + index +
                    " do conjunto " + ShortId(data.GroupId) + ".");
                return;
            }

            PromptKeywordOptions actionOptions = new(
                "\nAção [Editar/Desassociar/Resetar] <Editar>: ")
            {
                AllowNone = true
            };
            actionOptions.Keywords.Add("Editar");
            actionOptions.Keywords.Add("Desassociar");
            actionOptions.Keywords.Add("Resetar");

            PromptResult actionResult = ed.GetKeywords(actionOptions);
            if (actionResult.Status == PromptStatus.Cancel)
                return;

            string action = actionResult.Status == PromptStatus.None
                ? "Editar"
                : actionResult.StringResult;

            if (string.Equals(action, "Desassociar", StringComparison.OrdinalIgnoreCase))
            {
                data.DetachedIndices.Add(index);
                data.GeneratedHandles.RemoveAll(
                    h => string.Equals(h, selectedHandle, StringComparison.OrdinalIgnoreCase));

                if (!selected.IsWriteEnabled)
                    selected.UpgradeOpen();

                TagSolid(
                    tr,
                    selected,
                    data.GroupId,
                    index,
                    SolidStateDetached);

                SaveSet(tr, db, data);
                tr.Commit();

                ed.WriteMessage(
                    "\nSólido desassociado temporariamente do vértice " + index + "." +
                    " Ele não será apagado nem recriado por C3DSOLIDATUALIZAR.");
                ed.WriteMessage(
                    "\nPara voltar ao array, execute C3DSOLIDITEM e selecione este mesmo sólido.");
                return;
            }

            if (string.Equals(action, "Resetar", StringComparison.OrdinalIgnoreCase))
            {
                data.ItemOffsets.Remove(index);

                if (!TryRegenerateSingleItem(
                    tr,
                    db,
                    data,
                    index,
                    per.ObjectId,
                    selectedHandle,
                    out string resetError))
                {
                    ed.WriteMessage("\n[ERRO] " + resetError);
                    return;
                }

                tr.Commit();
                ed.WriteMessage(
                    "\nDeslocamento individual removido. O sólido voltou aos offsets do conjunto.");
                return;
            }

            ItemOffsetData current = data.ItemOffsets.TryGetValue(index, out ItemOffsetData? stored)
                ? stored
                : new ItemOffsetData();

            ed.WriteMessage(
                "\nOffsets individuais são ADICIONAIS aos offsets gerais do conjunto.");
            ed.WriteMessage(
                "\nX = longitudinal | Y = transversal (+ esquerda) | Z = vertical global.");

            double? x = AskDouble(
                ed,
                "\nOffset individual X longitudinal",
                current.OffsetX);
            if (!x.HasValue)
                return;

            double? y = AskDouble(
                ed,
                "\nOffset individual Y transversal (+ esquerda / - direita)",
                current.OffsetY);
            if (!y.HasValue)
                return;

            double? z = AskDouble(
                ed,
                "\nOffset individual Z vertical",
                current.OffsetZ);
            if (!z.HasValue)
                return;

            if (Math.Abs(x.Value) <= Epsilon &&
                Math.Abs(y.Value) <= Epsilon &&
                Math.Abs(z.Value) <= Epsilon)
            {
                data.ItemOffsets.Remove(index);
            }
            else
            {
                data.ItemOffsets[index] = new ItemOffsetData
                {
                    OffsetX = x.Value,
                    OffsetY = y.Value,
                    OffsetZ = z.Value
                };
            }

            if (!TryRegenerateSingleItem(
                tr,
                db,
                data,
                index,
                per.ObjectId,
                selectedHandle,
                out string editError))
            {
                ed.WriteMessage("\n[ERRO] " + editError);
                return;
            }

            tr.Commit();

            ed.WriteMessage(
                "\nSólido individual atualizado no vértice " + index +
                " | adicional: " + FormatOffsets(x.Value, y.Value, z.Value));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO C3DSOLIDITEM] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.SolidHelp, CommandFlags.Modal)]
    public void Help()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;

        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidArray + " v" + Versao);
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidCfg);
        ed.WriteMessage("\n  Define os offsets padrão do desenho:");
        ed.WriteMessage("\n  X = longitudinal à feature line;");
        ed.WriteMessage("\n  Y = transversal (+ esquerda / - direita);");
        ed.WriteMessage("\n  Z = vertical global.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidArray);
        ed.WriteMessage("\n  1) selecione o 3D Solid modelo;");
        ed.WriteMessage("\n  2) informe seu ponto-base de ancoragem;");
        ed.WriteMessage("\n  3) informe a direção longitudinal do sólido em planta;");
        ed.WriteMessage("\n  4) selecione a AutoCorridorFeatureLine;");
        ed.WriteMessage("\n  5) a rotina cria um Solid3d em cada ponto da feature line.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidAtualizar);
        ed.WriteMessage("\n  Após rebuild/alteração do Corridor, selecione qualquer sólido");
        ed.WriteMessage("\n  gerado ou a própria AutoCorridorFeatureLine para reconstruir.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidEditar);
        ed.WriteMessage("\n  Selecione um sólido gerado, altere X/Y/Z e o conjunto é recriado.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\n" + C3DCommands.Corridor.SolidItem);
        ed.WriteMessage("\n  Atua somente sobre UM sólido do conjunto.");
        ed.WriteMessage("\n  Editar: aplica X/Y/Z individuais adicionais aos offsets gerais.");
        ed.WriteMessage("\n  Desassociar: o sólido fica livre e não é afetado por ATUALIZAR.");
        ed.WriteMessage("\n  Reassociar: selecione novamente o sólido desassociado para voltar ao array.");
        ed.WriteMessage("\n  Resetar: remove o offset individual e volta ao padrão do conjunto.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\nREGRA DE ORIENTAÇÃO:");
        ed.WriteMessage("\n  O sólido gira SOMENTE em XY, em torno do Z global.");
        ed.WriteMessage("\n  A inclinação/declividade da feature line nunca tomba o sólido.");
        ed.WriteMessage("\n  Em vértices internos é usada a bissetriz XY dos trechos vizinhos.");
        ed.WriteMessage("\n============================================================");
    }

    private static double? AskDouble(
        Editor ed,
        string message,
        double currentValue)
    {
        PromptDoubleOptions pdo = new(
            message + " <" +
            currentValue.ToString("0.###", CultureInfo.InvariantCulture) +
            ">: ")
        {
            AllowNone = true,
            UseDefaultValue = true,
            DefaultValue = currentValue,
            AllowNegative = true,
            AllowZero = true
        };

        PromptDoubleResult pdr = ed.GetDouble(pdo);

        if (pdr.Status == PromptStatus.Cancel)
            return null;

        if (pdr.Status == PromptStatus.None)
            return currentValue;

        if (pdr.Status != PromptStatus.OK)
            return null;

        return pdr.Value;
    }

    private static Point3d ToWcs(Editor ed, Point3d pointInUcs)
    {
        return pointInUcs.TransformBy(ed.CurrentUserCoordinateSystem);
    }

    private static string FormatOffsets(double x, double y, double z)
    {
        return "X=" + x.ToString("0.###", CultureInfo.InvariantCulture) +
               " | Y=" + y.ToString("0.###", CultureInfo.InvariantCulture) +
               " | Z=" + z.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string ShortId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "?";

        return id.Length <= 8 ? id : id[..8];
    }

    private static List<Point3d> GetCleanPoints(AutoCorridorFeatureLine featureLine)
    {
        Point3dCollection collection = featureLine.GetPoints(
            FeatureLinePointType.AllPoints);

        List<Point3d> points = new(collection.Count);

        foreach (Point3d point in collection)
        {
            if (points.Count == 0 ||
                points[^1].DistanceTo(point) > Epsilon)
            {
                points.Add(point);
            }
        }

        return points;
    }

    private static bool TryGetTangentXY(
        IReadOnlyList<Point3d> points,
        int index,
        out Vector3d tangent)
    {
        tangent = Vector3d.XAxis;

        if (points.Count < 2 || index < 0 || index >= points.Count)
            return false;

        Vector3d? incoming = null;
        Vector3d? outgoing = null;

        Point3d current = points[index];

        for (int i = index - 1; i >= 0; i--)
        {
            Vector3d v = current - points[i];
            Vector3d xy = new(v.X, v.Y, 0.0);
            if (xy.Length > Epsilon)
            {
                incoming = xy.GetNormal();
                break;
            }
        }

        for (int i = index + 1; i < points.Count; i++)
        {
            Vector3d v = points[i] - current;
            Vector3d xy = new(v.X, v.Y, 0.0);
            if (xy.Length > Epsilon)
            {
                outgoing = xy.GetNormal();
                break;
            }
        }

        if (incoming.HasValue && outgoing.HasValue)
        {
            Vector3d sum = incoming.Value + outgoing.Value;

            if (sum.Length > Epsilon)
            {
                tangent = sum.GetNormal();
                return true;
            }

            tangent = outgoing.Value;
            return true;
        }

        if (outgoing.HasValue)
        {
            tangent = outgoing.Value;
            return true;
        }

        if (incoming.HasValue)
        {
            tangent = incoming.Value;
            return true;
        }

        return false;
    }

    private static ObjectId CreateNormalizedTemplate(
        Transaction tr,
        Database db,
        Solid3d sourceSolid,
        Point3d sourceBasePoint,
        double sourceAngle,
        string groupId)
    {
        BlockTable blockTable = (BlockTable)tr.GetObject(
            db.BlockTableId,
            OpenMode.ForRead);

        blockTable.UpgradeOpen();

        string blockName = TemplateNamePrefix + groupId.ToUpperInvariant();

        BlockTableRecord templateBtr = new()
        {
            Name = blockName,
            Origin = Point3d.Origin
        };

        ObjectId templateId = blockTable.Add(templateBtr);
        tr.AddNewlyCreatedDBObject(templateBtr, true);

        Solid3d templateSolid = (Solid3d)sourceSolid.Clone();

        templateSolid.TransformBy(
            Matrix3d.Displacement(Point3d.Origin - sourceBasePoint));

        templateSolid.TransformBy(
            Matrix3d.Rotation(
                -sourceAngle,
                Vector3d.ZAxis,
                Point3d.Origin));

        templateBtr.AppendEntity(templateSolid);
        tr.AddNewlyCreatedDBObject(templateSolid, true);

        return templateId;
    }

    private static int GenerateSolids(
        Transaction tr,
        Database db,
        AutoCorridorFeatureLine featureLine,
        ObjectId templateBlockId,
        ArraySetData data)
    {
        List<Point3d> points = GetCleanPoints(featureLine);
        if (points.Count == 0)
            return 0;

        BlockTableRecord templateBtr = (BlockTableRecord)tr.GetObject(
            templateBlockId,
            OpenMode.ForRead);

        Solid3d? templateSolid = null;

        foreach (ObjectId id in templateBtr)
        {
            templateSolid = tr.GetObject(
                id,
                OpenMode.ForRead,
                false) as Solid3d;

            if (templateSolid is not null)
                break;
        }

        if (templateSolid is null)
            throw new InvalidOperationException(
                "O modelo interno do sólido não foi encontrado.");

        BlockTable blockTable = (BlockTable)tr.GetObject(
            db.BlockTableId,
            OpenMode.ForRead);

        BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForWrite);

        data.GeneratedHandles.Clear();

        int created = 0;

        for (int i = 0; i < points.Count; i++)
        {
            if (data.DetachedIndices.Contains(i))
                continue;

            if (!TryGetTangentXY(points, i, out Vector3d tangent))
                continue;

            ItemOffsetData itemOffset = data.ItemOffsets.TryGetValue(i, out ItemOffsetData? specific)
                ? specific
                : new ItemOffsetData();

            double offsetX = data.OffsetX + itemOffset.OffsetX;
            double offsetY = data.OffsetY + itemOffset.OffsetY;
            double offsetZ = data.OffsetZ + itemOffset.OffsetZ;

            Vector3d left = new(-tangent.Y, tangent.X, 0.0);

            Point3d target = points[i] +
                (tangent * offsetX) +
                (left * offsetY) +
                (Vector3d.ZAxis * offsetZ);

            double angle = Math.Atan2(tangent.Y, tangent.X);

            Solid3d solid = (Solid3d)templateSolid.Clone();

            solid.TransformBy(
                Matrix3d.Rotation(
                    angle,
                    Vector3d.ZAxis,
                    Point3d.Origin));

            solid.TransformBy(
                Matrix3d.Displacement(target - Point3d.Origin));

            modelSpace.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);

            TagSolid(
                tr,
                solid,
                data.GroupId,
                i,
                SolidStateManaged);

            data.GeneratedHandles.Add(solid.ObjectId.Handle.ToString());
            created++;
        }

        return created;
    }

    private static bool TryRegenerateSet(
        Transaction tr,
        Database db,
        ArraySetData data,
        out int created,
        out string error)
    {
        created = 0;
        error = string.Empty;

        ObjectId featureLineId = ResolveHandle(db, data.FeatureLineHandle);
        if (featureLineId.IsNull)
        {
            error = "AutoCorridorFeatureLine original não encontrada.";
            return false;
        }

        ObjectId templateBlockId = ResolveHandle(db, data.TemplateBlockHandle);
        if (templateBlockId.IsNull)
        {
            error = "modelo interno do sólido não encontrado.";
            return false;
        }

        AutoCorridorFeatureLine? featureLine = tr.GetObject(
            featureLineId,
            OpenMode.ForRead,
            false) as AutoCorridorFeatureLine;

        if (featureLine is null)
        {
            error = "objeto vinculado deixou de ser AutoCorridorFeatureLine.";
            return false;
        }

        EraseGeneratedSolids(tr, db, data.GeneratedHandles);

        created = GenerateSolids(
            tr,
            db,
            featureLine,
            templateBlockId,
            data);

        SaveSet(tr, db, data);
        return true;
    }

    private static bool TryRegenerateSingleItem(
        Transaction tr,
        Database db,
        ArraySetData data,
        int index,
        ObjectId oldSolidId,
        string oldHandle,
        out string error)
    {
        error = string.Empty;

        ObjectId featureLineId = ResolveHandle(db, data.FeatureLineHandle);
        if (featureLineId.IsNull)
        {
            error = "AutoCorridorFeatureLine original não encontrada.";
            return false;
        }

        ObjectId templateBlockId = ResolveHandle(db, data.TemplateBlockHandle);
        if (templateBlockId.IsNull)
        {
            error = "modelo interno do sólido não encontrado.";
            return false;
        }

        AutoCorridorFeatureLine? featureLine = tr.GetObject(
            featureLineId,
            OpenMode.ForRead,
            false) as AutoCorridorFeatureLine;

        if (featureLine is null)
        {
            error = "objeto vinculado deixou de ser AutoCorridorFeatureLine.";
            return false;
        }

        List<Point3d> points = GetCleanPoints(featureLine);
        if (index < 0 || index >= points.Count)
        {
            error = "o vértice associado não existe mais na feature line atual.";
            return false;
        }

        if (data.DetachedIndices.Contains(index))
        {
            error = "o item permanece marcado como desassociado.";
            return false;
        }

        if (!TryGetTangentXY(points, index, out Vector3d tangent))
        {
            error = "não foi possível calcular a direção XY no vértice selecionado.";
            return false;
        }

        BlockTableRecord templateBtr = (BlockTableRecord)tr.GetObject(
            templateBlockId,
            OpenMode.ForRead);

        Solid3d? templateSolid = null;
        foreach (ObjectId id in templateBtr)
        {
            templateSolid = tr.GetObject(
                id,
                OpenMode.ForRead,
                false) as Solid3d;

            if (templateSolid is not null)
                break;
        }

        if (templateSolid is null)
        {
            error = "o modelo interno do sólido não foi encontrado.";
            return false;
        }

        BlockTable blockTable = (BlockTable)tr.GetObject(
            db.BlockTableId,
            OpenMode.ForRead);

        BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForWrite);

        try
        {
            Entity? oldEntity = tr.GetObject(
                oldSolidId,
                OpenMode.ForWrite,
                false) as Entity;

            if (oldEntity is not null && !oldEntity.IsErased)
                oldEntity.Erase();
        }
        catch
        {
            // Se o objeto antigo já não existir, ainda é possível recriar o slot.
        }

        data.GeneratedHandles.RemoveAll(
            h => string.Equals(h, oldHandle, StringComparison.OrdinalIgnoreCase));

        ItemOffsetData itemOffset = data.ItemOffsets.TryGetValue(index, out ItemOffsetData? specific)
            ? specific
            : new ItemOffsetData();

        double offsetX = data.OffsetX + itemOffset.OffsetX;
        double offsetY = data.OffsetY + itemOffset.OffsetY;
        double offsetZ = data.OffsetZ + itemOffset.OffsetZ;

        Vector3d left = new(-tangent.Y, tangent.X, 0.0);

        Point3d target = points[index] +
            (tangent * offsetX) +
            (left * offsetY) +
            (Vector3d.ZAxis * offsetZ);

        double angle = Math.Atan2(tangent.Y, tangent.X);

        Solid3d solid = (Solid3d)templateSolid.Clone();

        solid.TransformBy(
            Matrix3d.Rotation(
                angle,
                Vector3d.ZAxis,
                Point3d.Origin));

        solid.TransformBy(
            Matrix3d.Displacement(target - Point3d.Origin));

        modelSpace.AppendEntity(solid);
        tr.AddNewlyCreatedDBObject(solid, true);

        TagSolid(
            tr,
            solid,
            data.GroupId,
            index,
            SolidStateManaged);

        data.GeneratedHandles.Add(solid.ObjectId.Handle.ToString());
        SaveSet(tr, db, data);
        return true;
    }

    private static void EraseGeneratedSolids(
        Transaction tr,
        Database db,
        IEnumerable<string> handles)
    {
        foreach (string handleText in handles.ToList())
        {
            ObjectId id = ResolveHandle(db, handleText);
            if (id.IsNull)
                continue;

            try
            {
                Entity? entity = tr.GetObject(
                    id,
                    OpenMode.ForWrite,
                    false) as Entity;

                if (entity is not null && !entity.IsErased)
                    entity.Erase();
            }
            catch
            {
                // Se o usuário já apagou manualmente algum sólido, a atualização
                // continua normalmente e recria apenas o conjunto atual.
            }
        }
    }

    private static void TagSolid(
        Transaction tr,
        Entity entity,
        string groupId,
        int index,
        string state)
    {
        if (entity.ExtensionDictionary.IsNull)
            entity.CreateExtensionDictionary();

        DBDictionary dictionary = (DBDictionary)tr.GetObject(
            entity.ExtensionDictionary,
            OpenMode.ForWrite);

        ResultBuffer buffer = new(
            new TypedValue((int)DxfCode.Text, groupId),
            new TypedValue((int)DxfCode.Int32, index),
            new TypedValue((int)DxfCode.Text, state));

        if (dictionary.Contains(GeneratedTagKey))
        {
            Xrecord existing = (Xrecord)tr.GetObject(
                dictionary.GetAt(GeneratedTagKey),
                OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        Xrecord record = new()
        {
            Data = buffer
        };

        dictionary.SetAt(GeneratedTagKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static SolidTagData? TryReadSolidTag(
        Transaction tr,
        DBObject entity)
    {
        if (entity.ExtensionDictionary.IsNull)
            return null;

        DBDictionary dictionary = (DBDictionary)tr.GetObject(
            entity.ExtensionDictionary,
            OpenMode.ForRead);

        if (!dictionary.Contains(GeneratedTagKey))
            return null;

        Xrecord record = (Xrecord)tr.GetObject(
            dictionary.GetAt(GeneratedTagKey),
            OpenMode.ForRead);

        TypedValue[] values = record.Data?.AsArray() ?? Array.Empty<TypedValue>();
        if (values.Length == 0)
            return null;

        SolidTagData tag = new()
        {
            GroupId = Convert.ToString(
                values[0].Value,
                CultureInfo.InvariantCulture) ?? string.Empty,
            Index = -1,
            State = SolidStateManaged
        };

        if (values.Length >= 2)
        {
            try
            {
                tag.Index = Convert.ToInt32(
                    values[1].Value,
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                tag.Index = -1;
            }
        }

        if (values.Length >= 3)
        {
            string? state = Convert.ToString(
                values[2].Value,
                CultureInfo.InvariantCulture);

            if (!string.IsNullOrWhiteSpace(state))
                tag.State = state;
        }

        return tag;
    }

    private static string? TryReadGeneratedGroupId(
        Transaction tr,
        DBObject entity)
    {
        return TryReadSolidTag(tr, entity)?.GroupId;
    }

    private static void EnsureLegacyItemTags(
        Transaction tr,
        Database db,
        ArraySetData data)
    {
        // A v1.0 gravava somente o GroupId em cada sólido. Antes de qualquer
        // edição individual, converte todos os itens ainda existentes para a
        // tag v1.1 com índice explícito, preservando compatibilidade.
        for (int i = 0; i < data.GeneratedHandles.Count; i++)
        {
            ObjectId id = ResolveHandle(db, data.GeneratedHandles[i]);
            if (id.IsNull)
                continue;

            Entity? entity = tr.GetObject(
                id,
                OpenMode.ForRead,
                false) as Entity;

            if (entity is null || entity.IsErased)
                continue;

            SolidTagData? existing = TryReadSolidTag(tr, entity);
            if (existing is null || existing.Index >= 0)
                continue;

            if (!entity.IsWriteEnabled)
                entity.UpgradeOpen();

            TagSolid(
                tr,
                entity,
                data.GroupId,
                i,
                SolidStateManaged);
        }
    }

    private static int ResolveItemIndex(
        ArraySetData data,
        string selectedHandle,
        int taggedIndex)
    {
        if (taggedIndex >= 0)
            return taggedIndex;

        // Compatibilidade com sólidos criados pela v1.0, cuja tag possuía
        // apenas o GroupId. Na v1.0 a lista era gravada na ordem dos pontos.
        for (int i = 0; i < data.GeneratedHandles.Count; i++)
        {
            if (string.Equals(
                data.GeneratedHandles[i],
                selectedHandle,
                StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<ArraySetData> ResolveSetsFromSelection(
        Transaction tr,
        Database db,
        DBObject selected,
        ObjectId selectedId)
    {
        string? groupId = TryReadGeneratedGroupId(tr, selected);

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            ArraySetData? one = LoadSet(tr, db, groupId);
            return one is null ? new List<ArraySetData>() : new List<ArraySetData> { one };
        }

        if (selected is AutoCorridorFeatureLine)
        {
            string handle = selectedId.Handle.ToString();
            return LoadAllSets(tr, db)
                .Where(s => string.Equals(
                    s.FeatureLineHandle,
                    handle,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new List<ArraySetData>();
    }

    private static DefaultSettings LoadSettings(
        Transaction tr,
        Database db)
    {
        DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            OpenMode.ForRead);

        if (!root.Contains(SettingsKey))
            return new DefaultSettings();

        Xrecord record = (Xrecord)tr.GetObject(
            root.GetAt(SettingsKey),
            OpenMode.ForRead);

        TypedValue[] values = record.Data?.AsArray() ?? Array.Empty<TypedValue>();

        DefaultSettings settings = new();

        if (values.Length >= 3)
        {
            settings.OffsetX = Convert.ToDouble(values[0].Value, CultureInfo.InvariantCulture);
            settings.OffsetY = Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture);
            settings.OffsetZ = Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture);
        }

        return settings;
    }

    private static void SaveSettings(
        Transaction tr,
        Database db,
        DefaultSettings settings)
    {
        DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            OpenMode.ForWrite);

        ResultBuffer buffer = new(
            new TypedValue((int)DxfCode.Real, settings.OffsetX),
            new TypedValue((int)DxfCode.Real, settings.OffsetY),
            new TypedValue((int)DxfCode.Real, settings.OffsetZ));

        if (root.Contains(SettingsKey))
        {
            Xrecord existing = (Xrecord)tr.GetObject(
                root.GetAt(SettingsKey),
                OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        Xrecord record = new()
        {
            Data = buffer
        };

        root.SetAt(SettingsKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static void SaveSet(
        Transaction tr,
        Database db,
        ArraySetData data)
    {
        DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            OpenMode.ForWrite);

        string key = SetKeyPrefix + data.GroupId;
        ResultBuffer buffer = BuildSetBuffer(data);

        if (root.Contains(key))
        {
            Xrecord existing = (Xrecord)tr.GetObject(
                root.GetAt(key),
                OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        Xrecord record = new()
        {
            Data = buffer
        };

        root.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static ResultBuffer BuildSetBuffer(ArraySetData data)
    {
        List<TypedValue> values = new()
        {
            new TypedValue((int)DxfCode.Text, RecordSignature),
            new TypedValue((int)DxfCode.Text, data.GroupId),
            new TypedValue((int)DxfCode.Text, data.FeatureLineHandle),
            new TypedValue((int)DxfCode.Text, data.TemplateBlockHandle),
            new TypedValue((int)DxfCode.Real, data.OffsetX),
            new TypedValue((int)DxfCode.Real, data.OffsetY),
            new TypedValue((int)DxfCode.Real, data.OffsetZ)
        };

        foreach (string handle in data.GeneratedHandles)
        {
            values.Add(new TypedValue((int)DxfCode.Text, handle));
        }

        values.Add(new TypedValue((int)DxfCode.Text, DetachedMarker));
        values.Add(new TypedValue(
            (int)DxfCode.Text,
            string.Join(",", data.DetachedIndices.OrderBy(i => i))));

        values.Add(new TypedValue((int)DxfCode.Text, OverridesMarker));
        foreach (KeyValuePair<int, ItemOffsetData> pair in data.ItemOffsets.OrderBy(p => p.Key))
        {
            ItemOffsetData item = pair.Value;
            string encoded = string.Join(
                "|",
                pair.Key.ToString(CultureInfo.InvariantCulture),
                item.OffsetX.ToString("R", CultureInfo.InvariantCulture),
                item.OffsetY.ToString("R", CultureInfo.InvariantCulture),
                item.OffsetZ.ToString("R", CultureInfo.InvariantCulture));

            values.Add(new TypedValue((int)DxfCode.Text, encoded));
        }

        return new ResultBuffer(values.ToArray());
    }

    private static ArraySetData? LoadSet(
        Transaction tr,
        Database db,
        string groupId)
    {
        DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            OpenMode.ForRead);

        string key = SetKeyPrefix + groupId;
        if (!root.Contains(key))
            return null;

        Xrecord record = (Xrecord)tr.GetObject(
            root.GetAt(key),
            OpenMode.ForRead);

        return ParseSet(record);
    }

    private static List<ArraySetData> LoadAllSets(
        Transaction tr,
        Database db)
    {
        DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            OpenMode.ForRead);

        List<ArraySetData> result = new();

        foreach (DBDictionaryEntry entry in root)
        {
            if (!entry.Key.StartsWith(SetKeyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            Xrecord? record = tr.GetObject(
                entry.Value,
                OpenMode.ForRead,
                false) as Xrecord;

            if (record is null)
                continue;

            ArraySetData? data = ParseSet(record);
            if (data is not null)
                result.Add(data);
        }

        return result;
    }

    private static ArraySetData? ParseSet(Xrecord record)
    {
        TypedValue[] values = record.Data?.AsArray() ?? Array.Empty<TypedValue>();

        if (values.Length < 7)
            return null;

        string signature = Convert.ToString(
            values[0].Value,
            CultureInfo.InvariantCulture) ?? string.Empty;

        if (!string.Equals(
            signature,
            RecordSignature,
            StringComparison.Ordinal))
        {
            return null;
        }

        ArraySetData data = new()
        {
            GroupId = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture) ?? string.Empty,
            FeatureLineHandle = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture) ?? string.Empty,
            TemplateBlockHandle = Convert.ToString(values[3].Value, CultureInfo.InvariantCulture) ?? string.Empty,
            OffsetX = Convert.ToDouble(values[4].Value, CultureInfo.InvariantCulture),
            OffsetY = Convert.ToDouble(values[5].Value, CultureInfo.InvariantCulture),
            OffsetZ = Convert.ToDouble(values[6].Value, CultureInfo.InvariantCulture)
        };

        int pos = 7;

        // Compatibilidade com v1.0: todos os textos após o cabeçalho eram handles.
        // Na v1.1 os marcadores abaixo delimitam estado individual persistente.
        while (pos < values.Length)
        {
            string text = Convert.ToString(
                values[pos].Value,
                CultureInfo.InvariantCulture) ?? string.Empty;

            if (string.Equals(text, DetachedMarker, StringComparison.Ordinal))
            {
                pos++;
                if (pos < values.Length)
                {
                    string detachedText = Convert.ToString(
                        values[pos].Value,
                        CultureInfo.InvariantCulture) ?? string.Empty;

                    foreach (string piece in detachedText.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (int.TryParse(
                            piece,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int detachedIndex))
                        {
                            data.DetachedIndices.Add(detachedIndex);
                        }
                    }
                }

                pos++;
                continue;
            }

            if (string.Equals(text, OverridesMarker, StringComparison.Ordinal))
            {
                pos++;

                while (pos < values.Length)
                {
                    string encoded = Convert.ToString(
                        values[pos].Value,
                        CultureInfo.InvariantCulture) ?? string.Empty;

                    string[] parts = encoded.Split('|');
                    if (parts.Length == 4 &&
                        int.TryParse(
                            parts[0],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int itemIndex) &&
                        double.TryParse(
                            parts[1],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double x) &&
                        double.TryParse(
                            parts[2],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double y) &&
                        double.TryParse(
                            parts[3],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double z))
                    {
                        data.ItemOffsets[itemIndex] = new ItemOffsetData
                        {
                            OffsetX = x,
                            OffsetY = y,
                            OffsetZ = z
                        };
                    }

                    pos++;
                }

                break;
            }

            if (!string.IsNullOrWhiteSpace(text))
                data.GeneratedHandles.Add(text);

            pos++;
        }

        return data;
    }

    private static DBDictionary GetOrCreateRootDictionary(
        Transaction tr,
        Database db,
        OpenMode requestedMode)
    {
        DBDictionary nod = (DBDictionary)tr.GetObject(
            db.NamedObjectsDictionaryId,
            OpenMode.ForRead);

        if (nod.Contains(RootDictionaryName))
        {
            return (DBDictionary)tr.GetObject(
                nod.GetAt(RootDictionaryName),
                requestedMode);
        }

        nod.UpgradeOpen();

        DBDictionary root = new();
        nod.SetAt(RootDictionaryName, root);
        tr.AddNewlyCreatedDBObject(root, true);

        return root;
    }

    private static ObjectId ResolveHandle(Database db, string handleText)
    {
        if (string.IsNullOrWhiteSpace(handleText))
            return ObjectId.Null;

        try
        {
            long value = Convert.ToInt64(handleText, 16);
            Handle handle = new(value);
            return db.GetObjectId(
                false,
                handle,
                0);
        }
        catch
        {
            return ObjectId.Null;
        }
    }
}
