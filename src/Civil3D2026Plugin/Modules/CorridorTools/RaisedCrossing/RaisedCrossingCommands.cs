using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing;

/// <summary>
/// Criação e manutenção de passagem semi-elevada parametrizada por QUATRO Feature Lines.
///
/// Ordem transversal:
/// - FL1 externa VIA 1;
/// - FL2 interna VIA 1 / bordo do canteiro;
/// - FL3 interna VIA 2 / outro bordo do canteiro;
/// - FL4 externa VIA 2.
///
/// Geometria:
/// - cada via recebe rampa + plato + rampa;
/// - o canteiro recebe somente o plato, sem rampa veicular;
/// - o conector central começa depois das extensoes internas;
/// - base = cotas locais das Feature Lines;
/// - topo = base + H informado pelo usuario;
/// - resultado final = UM unico Solid3d.
///
/// O Solid3d recebe, quando disponivel, o Property Set AEC C3D_PASSAGEM.
/// </summary>
public sealed class RaisedCrossingCommands
{
    public const string Versao = C3DVersions.RaisedCrossing;
    private const double Epsilon = 1e-8;

    [CommandMethod(C3DCommands.Corridor.Passagem, CommandFlags.Modal)]
    public void Create()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            RaisedCrossingSettings settings = RaisedCrossingPersistence.LoadSettings(tr, db);

            if (!TrySelectFeatureLine(
                    ed, tr,
                    "\nSelecione a FL EXTERNA da VIA 1 (FL1): ",
                    out CivilFeatureLine? fl1,
                    out AcadDb.ObjectId fl1Id))
                return;

            if (!TrySelectFeatureLine(
                    ed, tr,
                    "\nSelecione a FL INTERNA da VIA 1 / bordo do canteiro (FL2): ",
                    out CivilFeatureLine? fl2,
                    out AcadDb.ObjectId fl2Id))
                return;

            if (!TrySelectFeatureLine(
                    ed, tr,
                    "\nSelecione a FL INTERNA da VIA 2 / outro bordo do canteiro (FL3): ",
                    out CivilFeatureLine? fl3,
                    out AcadDb.ObjectId fl3Id))
                return;

            if (!TrySelectFeatureLine(
                    ed, tr,
                    "\nSelecione a FL EXTERNA da VIA 2 (FL4): ",
                    out CivilFeatureLine? fl4,
                    out AcadDb.ObjectId fl4Id))
                return;

            AcadDb.ObjectId[] selectedIds = { fl1Id, fl2Id, fl3Id, fl4Id };
            if (selectedIds.Distinct().Count() != 4)
            {
                ed.WriteMessage("\n[ERRO] As quatro Feature Lines precisam ser objetos diferentes.");
                return;
            }

            if (!TryAskAnchorMode(ed, settings.LastAnchorMode, out CrossingAnchorMode anchorMode))
                return;

            PromptPointResult referencePrompt = ed.GetPoint(
                new PromptPointOptions("\nInforme o ponto de referência da passagem sobre a FL1: "));
            if (referencePrompt.Status != PromptStatus.OK)
                return;

            Point3d clickedReference = ToWcs(ed, referencePrompt.Value);
            Point3d projectedReference = RaisedCrossingGeometry.ProjectPointToFeatureLine(fl1, clickedReference);
            double referenceDistance = fl1.GetDistAtPoint(projectedReference);

            if (!TryAskLength(
                    ed,
                    fl1,
                    referenceDistance,
                    settings.LastLengthInputMode,
                    out double length,
                    out CrossingLengthInputMode lengthMode))
            {
                return;
            }

            double? overhang = AskDouble(
                ed,
                "\nExtensão transversal além de CADA Feature Line",
                settings.DefaultOverhang,
                allowZero: true);
            if (!overhang.HasValue)
                return;

            double? manualHeight = AskDouble(
                ed,
                "\nInforme a altura vertical H da passagem acima das cotas das Feature Lines",
                settings.DefaultHeight,
                allowZero: false);
            if (!manualHeight.HasValue)
                return;

            double height = manualHeight.Value;

            CrossingGeometryResult geometry = RaisedCrossingGeometry.BuildSolid(
                fl1,
                fl2,
                fl3,
                fl4,
                referenceDistance,
                anchorMode,
                length,
                settings.DefaultRampLength,
                overhang.Value,
                height,
                settings.SampleStep);

            AcadDb.BlockTableRecord modelSpace = GetModelSpace(tr, db);
            geometry.Solid.SetDatabaseDefaults(db);
            AcadDb.ObjectId solidId = modelSpace.AppendEntity(geometry.Solid);
            tr.AddNewlyCreatedDBObject(geometry.Solid, true);

            var data = new RaisedCrossingData
            {
                GroupId = Guid.NewGuid().ToString("N"),
                FeatureLine1Handle = fl1Id.Handle.ToString(),
                FeatureLine2Handle = fl2Id.Handle.ToString(),
                FeatureLine3Handle = fl3Id.Handle.ToString(),
                FeatureLine4Handle = fl4Id.Handle.ToString(),
                SolidHandle = solidId.Handle.ToString(),
                AnchorMode = anchorMode,
                ReferenceFraction = SafeFraction(referenceDistance, fl1.Length3D),
                Length = length,
                Overhang = overhang.Value,
                RampLength = settings.DefaultRampLength,
                SampleStep = settings.SampleStep,
                HeightMode = CrossingHeightMode.Manual,
                Height = height,
                LastReferencePoint = geometry.ReferencePoint,
                HasReferenceStation = false
            };

            RaisedCrossingPersistence.TagSolid(tr, geometry.Solid, data.GroupId);
            RaisedCrossingPersistence.SavePassage(tr, db, data);

            bool propertySetOk = RaisedCrossingPropertySet.TryApplyOrUpdate(
                tr,
                db,
                geometry.Solid,
                data,
                geometry,
                out string propertySetMessage);

            settings.DefaultOverhang = overhang.Value;
            settings.DefaultHeight = height;
            settings.LastAnchorMode = anchorMode;
            settings.LastLengthInputMode = lengthMode;
            RaisedCrossingPersistence.SaveSettings(tr, db, settings);

            tr.Commit();

            ed.WriteMessage("\nPASSAGEM criada com sucesso como UM ÚNICO Solid3d.");
            ed.WriteMessage(
                $"\n  Platô={length:0.###} m | rampas={settings.DefaultRampLength:0.###} m/cada via | " +
                $"extensão={overhang.Value:0.###} m | H={height:0.###} m");
            ed.WriteMessage(
                $"\n  VIA1={geometry.Lane1WidthAtCg:0.###} m | canteiro={geometry.MedianWidthAtCg:0.###} m | " +
                $"VIA2={geometry.Lane2WidthAtCg:0.###} m | ID={ShortId(data.GroupId)}");
            ed.WriteMessage(
                $"\n  Loft={geometry.LoftMode} | seções={geometry.SectionCount}");

            if (propertySetOk)
                ed.WriteMessage("\n  " + propertySetMessage);
            else
                ed.WriteMessage("\n  [AVISO Property Set] " + propertySetMessage);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO PASSAGEM] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.PassagemCfg, CommandFlags.Modal)]
    public void Configure()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            RaisedCrossingSettings settings = RaisedCrossingPersistence.LoadSettings(tr, db);

            double? overhang = AskDouble(
                ed,
                "\nExtensão transversal padrão além de CADA Feature Line",
                settings.DefaultOverhang,
                allowZero: true);
            if (!overhang.HasValue)
                return;

            double? rampLength = AskDouble(
                ed,
                "\nComprimento padrão de CADA rampa veicular",
                settings.DefaultRampLength,
                allowZero: false);
            if (!rampLength.HasValue)
                return;

            double? step = AskDouble(
                ed,
                "\nPasso máximo de amostragem longitudinal do loft",
                settings.SampleStep,
                allowZero: false);
            if (!step.HasValue)
                return;

            settings.DefaultOverhang = overhang.Value;
            settings.DefaultRampLength = rampLength.Value;
            settings.SampleStep = Math.Max(0.10, step.Value);
            RaisedCrossingPersistence.SaveSettings(tr, db, settings);
            tr.Commit();

            ed.WriteMessage(
                $"\nConfiguração salva: extensão transversal={settings.DefaultOverhang:0.###} m/lado | " +
                $"rampa={settings.DefaultRampLength:0.###} m/cada | amostragem={settings.SampleStep:0.###} m.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO PASSAGEMCFG] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.PassagemEditar, CommandFlags.Modal)]
    public void EditSingle()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
            if (!TrySelectManagedPassage(ed, tr, db, out AcadDb.Solid3d selectedSolid, out RaisedCrossingData data))
                return;

            var actionOptions = new PromptKeywordOptions(
                "\nEditar [Comprimento/Extensao/Rampa/Altura/Posicao/Ancoragem/Atualizar] <Atualizar>: ")
            {
                AllowNone = true
            };
            actionOptions.Keywords.Add("Comprimento");
            actionOptions.Keywords.Add("Extensao");
            actionOptions.Keywords.Add("Rampa");
            actionOptions.Keywords.Add("Altura");
            actionOptions.Keywords.Add("Posicao");
            actionOptions.Keywords.Add("Ancoragem");
            actionOptions.Keywords.Add("Atualizar");
            actionOptions.Keywords.Default = "Atualizar";

            PromptResult actionResult = ed.GetKeywords(actionOptions);
            if (actionResult.Status == PromptStatus.Cancel)
                return;

            string action = actionResult.Status == PromptStatus.None
                ? "Atualizar"
                : actionResult.StringResult;

            if (action.Equals("Comprimento", StringComparison.OrdinalIgnoreCase))
            {
                double? value = AskDouble(ed, "\nNovo comprimento do platô (sem rampas)", data.Length, allowZero: false);
                if (!value.HasValue)
                    return;
                data.Length = value.Value;
            }
            else if (action.Equals("Extensao", StringComparison.OrdinalIgnoreCase))
            {
                double? value = AskDouble(ed, "\nNova extensão transversal além de CADA Feature Line", data.Overhang, allowZero: true);
                if (!value.HasValue)
                    return;
                data.Overhang = value.Value;
            }
            else if (action.Equals("Rampa", StringComparison.OrdinalIgnoreCase))
            {
                double? value = AskDouble(ed, "\nNovo comprimento de CADA rampa veicular", data.RampLength, allowZero: false);
                if (!value.HasValue)
                    return;
                data.RampLength = value.Value;
            }
            else if (action.Equals("Altura", StringComparison.OrdinalIgnoreCase))
            {
                double? value = AskDouble(
                    ed,
                    "\nNova altura vertical H acima das cotas das Feature Lines",
                    data.Height,
                    allowZero: false);
                if (!value.HasValue)
                    return;

                data.HeightMode = CrossingHeightMode.Manual;
                data.Height = value.Value;
                data.HasReferenceStation = false;
            }
            else if (action.Equals("Posicao", StringComparison.OrdinalIgnoreCase))
            {
                AcadDb.ObjectId fl1Id = RaisedCrossingPersistence.ResolveHandle(db, data.FeatureLine1Handle);
                CivilFeatureLine? fl1 = OpenFeatureLine(tr, fl1Id);
                if (fl1 is null)
                    throw new InvalidOperationException("A Feature Line 1 associada não foi encontrada.");

                PromptPointResult ppr = ed.GetPoint(
                    new PromptPointOptions("\nInforme a nova posição de referência sobre a Feature Line 1: "));
                if (ppr.Status != PromptStatus.OK)
                    return;

                Point3d point = ToWcs(ed, ppr.Value);
                Point3d projected = RaisedCrossingGeometry.ProjectPointToFeatureLine(fl1, point);
                double distance = fl1.GetDistAtPoint(projected);
                data.ReferenceFraction = SafeFraction(distance, fl1.Length3D);
                data.LastReferencePoint = projected;
                data.HasReferenceStation = false;
            }
            else if (action.Equals("Ancoragem", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryAskAnchorMode(ed, data.AnchorMode, out CrossingAnchorMode mode))
                    return;
                data.AnchorMode = mode;
            }

            if (!TryRebuildPassage(tr, db, ed, data, selectedSolid.ObjectId, allowBaselinePrompt: true, out string message))
            {
                ed.WriteMessage("\n[ERRO] " + message);
                return;
            }

            tr.Commit();
            ed.WriteMessage("\nPassagem atualizada. " + message);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO PASSAGEMEDITAR] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.PassagemGrupo, CommandFlags.Modal)]
    public void EditGroup()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            List<string> groupIds;
            RaisedCrossingData first;

            using (AcadDb.Transaction readTr = db.TransactionManager.StartTransaction())
            {
                groupIds = GetSelectedPassageIds(ed, readTr);
                if (groupIds.Count == 0)
                {
                    ed.WriteMessage("\nNenhuma passagem parametrizada selecionada.");
                    return;
                }

                first = RaisedCrossingPersistence.LoadPassage(readTr, db, groupIds[0])
                    ?? throw new InvalidOperationException("Dados da primeira passagem não encontrados.");
            }

            var options = new PromptKeywordOptions(
                $"\nEditar {groupIds.Count} passagem(ns) [Comprimento/Extensao/Rampa/Altura/Atualizar] <Atualizar>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Comprimento");
            options.Keywords.Add("Extensao");
            options.Keywords.Add("Rampa");
            options.Keywords.Add("Altura");
            options.Keywords.Add("Atualizar");
            options.Keywords.Default = "Atualizar";

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string action = result.Status == PromptStatus.None ? "Atualizar" : result.StringResult;
            double? commonValue = null;

            if (action.Equals("Comprimento", StringComparison.OrdinalIgnoreCase))
            {
                commonValue = AskDouble(ed, "\nComprimento comum do platô (sem rampas)", first.Length, allowZero: false);
                if (!commonValue.HasValue)
                    return;
            }
            else if (action.Equals("Extensao", StringComparison.OrdinalIgnoreCase))
            {
                commonValue = AskDouble(ed, "\nExtensão transversal comum além de CADA Feature Line", first.Overhang, allowZero: true);
                if (!commonValue.HasValue)
                    return;
            }
            else if (action.Equals("Rampa", StringComparison.OrdinalIgnoreCase))
            {
                commonValue = AskDouble(ed, "\nComprimento comum de CADA rampa veicular", first.RampLength, allowZero: false);
                if (!commonValue.HasValue)
                    return;
            }
            else if (action.Equals("Altura", StringComparison.OrdinalIgnoreCase))
            {
                commonValue = AskDouble(
                    ed,
                    "\nAltura H manual comum acima das cotas das Feature Lines",
                    first.Height,
                    allowZero: false);
                if (!commonValue.HasValue)
                    return;
            }

            int ok = 0;
            var failures = new List<string>();

            foreach (string groupId in groupIds)
            {
                using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                RaisedCrossingData? data = RaisedCrossingPersistence.LoadPassage(tr, db, groupId);
                if (data is null)
                {
                    failures.Add(ShortId(groupId) + ": dados persistidos não encontrados.");
                    continue;
                }

                if (action.Equals("Comprimento", StringComparison.OrdinalIgnoreCase) && commonValue.HasValue)
                    data.Length = commonValue.Value;
                else if (action.Equals("Extensao", StringComparison.OrdinalIgnoreCase) && commonValue.HasValue)
                    data.Overhang = commonValue.Value;
                else if (action.Equals("Rampa", StringComparison.OrdinalIgnoreCase) && commonValue.HasValue)
                    data.RampLength = commonValue.Value;
                else if (action.Equals("Altura", StringComparison.OrdinalIgnoreCase) && commonValue.HasValue)
                {
                    data.HeightMode = CrossingHeightMode.Manual;
                    data.Height = commonValue.Value;
                    data.HasReferenceStation = false;
                }

                AcadDb.ObjectId solidId = RaisedCrossingPersistence.ResolveHandle(db, data.SolidHandle);
                if (TryRebuildPassage(tr, db, ed, data, solidId, allowBaselinePrompt: true, out string message))
                {
                    tr.Commit();
                    ok++;
                }
                else
                {
                    failures.Add(ShortId(groupId) + ": " + message);
                }
            }

            ed.WriteMessage($"\nPASSAGEMGRUPO: {ok}/{groupIds.Count} atualizada(s).");
            foreach (string failure in failures.Take(10))
                ed.WriteMessage("\n  [FALHA] " + failure);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO PASSAGEMGRUPO] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.PassagemAtualizar, CommandFlags.Modal)]
    public void Update()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            var options = new PromptKeywordOptions(
                "\nAtualizar passagens [Selecionar/Todas] <Selecionar>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Selecionar");
            options.Keywords.Add("Todas");
            options.Keywords.Default = "Selecionar";

            PromptResult modeResult = ed.GetKeywords(options);
            if (modeResult.Status == PromptStatus.Cancel)
                return;

            string mode = modeResult.Status == PromptStatus.None ? "Selecionar" : modeResult.StringResult;
            List<string> groupIds;

            using (AcadDb.Transaction readTr = db.TransactionManager.StartTransaction())
            {
                groupIds = mode.Equals("Todas", StringComparison.OrdinalIgnoreCase)
                    ? RaisedCrossingPersistence.LoadAllPassages(readTr, db)
                        .Select(data => data.GroupId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : GetSelectedPassageIds(ed, readTr);
            }

            if (groupIds.Count == 0)
            {
                ed.WriteMessage("\nNenhuma passagem parametrizada encontrada para atualizar.");
                return;
            }

            int ok = 0;
            var failures = new List<string>();

            foreach (string groupId in groupIds)
            {
                using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
                RaisedCrossingData? data = RaisedCrossingPersistence.LoadPassage(tr, db, groupId);
                if (data is null)
                {
                    failures.Add(ShortId(groupId) + ": dados persistidos não encontrados.");
                    continue;
                }

                AcadDb.ObjectId solidId = RaisedCrossingPersistence.ResolveHandle(db, data.SolidHandle);
                if (TryRebuildPassage(tr, db, ed, data, solidId, allowBaselinePrompt: true, out string message))
                {
                    tr.Commit();
                    ok++;
                }
                else
                {
                    failures.Add(ShortId(groupId) + ": " + message);
                }
            }

            ed.WriteMessage($"\nPASSAGEMATUALIZAR: {ok}/{groupIds.Count} atualizada(s).");
            foreach (string failure in failures.Take(10))
                ed.WriteMessage("\n  [FALHA] " + failure);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO PASSAGEMATUALIZAR] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.PassagemHelp, CommandFlags.Modal)]
    public void Help()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        ed.WriteMessage("\n");
        ed.WriteMessage($"\nPASSAGEM semi-elevada v{Versao}");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n{C3DCommands.Corridor.Passagem}");
        ed.WriteMessage("\n  Cria UM Solid3d parametrizado para duas vias separadas por canteiro central.");
        ed.WriteMessage("\n  Ordem de selecao:");
        ed.WriteMessage("\n    FL1 = externa VIA 1");
        ed.WriteMessage("\n    FL2 = interna VIA 1 / bordo do canteiro");
        ed.WriteMessage("\n    FL3 = interna VIA 2 / outro bordo do canteiro");
        ed.WriteMessage("\n    FL4 = externa VIA 2");
        ed.WriteMessage("\n  VIA 1 e VIA 2 recebem rampa + plato + rampa.");
        ed.WriteMessage("\n  O canteiro recebe somente o plato, SEM rampa veicular.");
        ed.WriteMessage("\n  O conector central inicia apos a extensao interna da VIA 1 e termina");
        ed.WriteMessage("\n  antes da extensao interna da VIA 2.");
        ed.WriteMessage("\n  Base = cotas locais das quatro Feature Lines.");
        ed.WriteMessage("\n  Topo = base + H informado pelo usuario.");
        ed.WriteMessage("\n  H e constante; as cotas absolutas podem variar entre FL1/FL2/FL3/FL4.");
        ed.WriteMessage("\n  Comprimento = somente o plato; rampas nao entram nesse valor.");
        ed.WriteMessage("\n  Rampa padrao = 1,00 m/cada extremidade de cada via.");
        ed.WriteMessage("\n  O loft tenta acompanhar a curvatura das Feature Lines.");
        ed.WriteMessage("\n");
        ed.WriteMessage("\nPROPERTY SET:");
        ed.WriteMessage("\n  O Solid3d final recebe o Property Set C3D_PASSAGEM quando a API AEC estiver disponivel.");
        ed.WriteMessage("\n  Sao gravados ID, versao, H, plato, rampa, extensao, handles FL1-FL4,");
        ed.WriteMessage("\n  cotas das 4 Feature Lines no CG e larguras VIA1/CANTEIRO/VIA2 no CG.");
        ed.WriteMessage("\n  Falha no Property Set nao cancela a criacao geometrica da passagem.");
        ed.WriteMessage("\n");
        ed.WriteMessage($"\n{C3DCommands.Corridor.PassagemEditar}");
        ed.WriteMessage("\n  Edita plato, extensao, rampas, H, posicao ou ancoragem.");
        ed.WriteMessage($"\n{C3DCommands.Corridor.PassagemGrupo}");
        ed.WriteMessage("\n  Aplica parametros comuns e atualiza varias passagens.");
        ed.WriteMessage($"\n{C3DCommands.Corridor.PassagemAtualizar}");
        ed.WriteMessage("\n  Reconstrui as passagens apos alteracao das Feature Lines.");
        ed.WriteMessage($"\n{C3DCommands.Corridor.PassagemCfg}");
        ed.WriteMessage("\n  Configura extensao, comprimento padrao das rampas e amostragem.");
        ed.WriteMessage("\n============================================================");
    }

    private static bool TryRebuildPassage(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        Editor ed,
        RaisedCrossingData data,
        AcadDb.ObjectId oldSolidId,
        bool allowBaselinePrompt,
        out string message)
    {
        _ = allowBaselinePrompt; // Mantido na assinatura por compatibilidade interna.
        message = string.Empty;
        AcadDb.Solid3d? newSolid = null;

        try
        {
            if (!data.IsDualRoad)
            {
                message =
                    "Esta passagem foi criada por uma versao antiga com apenas duas Feature Lines. " +
                    "Recrie-a com PASSAGEM para informar FL1, FL2, FL3 e FL4.";
                return false;
            }

            AcadDb.ObjectId fl1Id = RaisedCrossingPersistence.ResolveHandle(db, data.FeatureLine1Handle);
            AcadDb.ObjectId fl2Id = RaisedCrossingPersistence.ResolveHandle(db, data.FeatureLine2Handle);
            AcadDb.ObjectId fl3Id = RaisedCrossingPersistence.ResolveHandle(db, data.FeatureLine3Handle);
            AcadDb.ObjectId fl4Id = RaisedCrossingPersistence.ResolveHandle(db, data.FeatureLine4Handle);

            CivilFeatureLine? fl1 = OpenFeatureLine(tr, fl1Id);
            CivilFeatureLine? fl2 = OpenFeatureLine(tr, fl2Id);
            CivilFeatureLine? fl3 = OpenFeatureLine(tr, fl3Id);
            CivilFeatureLine? fl4 = OpenFeatureLine(tr, fl4Id);

            if (fl1 is null || fl2 is null || fl3 is null || fl4 is null)
            {
                message = "Uma das quatro Feature Lines associadas nao foi encontrada.";
                return false;
            }

            double referenceDistance = RaisedCrossingGeometry.GetReferenceDistance(
                fl1,
                data,
                data.LastReferencePoint);

            // PASSAGEM v1.1.0: H e manual e constante.
            data.HeightMode = CrossingHeightMode.Manual;
            data.HasReferenceStation = false;

            CrossingGeometryResult geometry = RaisedCrossingGeometry.BuildSolid(
                fl1,
                fl2,
                fl3,
                fl4,
                referenceDistance,
                data.AnchorMode,
                data.Length,
                data.RampLength,
                data.Overhang,
                data.Height,
                data.SampleStep);

            newSolid = geometry.Solid;

            string oldLayer = string.Empty;
            AcadDb.Solid3d? oldSolid = null;
            if (!oldSolidId.IsNull && oldSolidId.IsValid && !oldSolidId.IsErased)
            {
                oldSolid = tr.GetObject(oldSolidId, AcadDb.OpenMode.ForWrite, false) as AcadDb.Solid3d;
                if (oldSolid is null)
                {
                    message = "O objeto persistido como solido da passagem nao e mais um Solid3d.";
                    newSolid.Dispose();
                    return false;
                }

                var layer = tr.GetObject(oldSolid.LayerId, AcadDb.OpenMode.ForRead, false) as AcadDb.LayerTableRecord;
                if (layer is not null && layer.IsLocked)
                {
                    message = $"A passagem esta no layer bloqueado '{layer.Name}'. Desbloqueie-o para atualizar.";
                    newSolid.Dispose();
                    return false;
                }

                oldLayer = oldSolid.Layer;
            }

            AcadDb.BlockTableRecord modelSpace = GetModelSpace(tr, db);
            newSolid.SetDatabaseDefaults(db);
            if (!string.IsNullOrWhiteSpace(oldLayer))
                newSolid.Layer = oldLayer;

            AcadDb.ObjectId newId = modelSpace.AppendEntity(newSolid);
            tr.AddNewlyCreatedDBObject(newSolid, true);

            RaisedCrossingPersistence.TagSolid(tr, newSolid, data.GroupId);

            data.SolidHandle = newId.Handle.ToString();
            data.ReferenceFraction = SafeFraction(referenceDistance, fl1.Length3D);
            data.LastReferencePoint = geometry.ReferencePoint;

            bool propertySetOk = RaisedCrossingPropertySet.TryApplyOrUpdate(
                tr,
                db,
                newSolid,
                data,
                geometry,
                out string propertySetMessage);

            RaisedCrossingPersistence.SavePassage(tr, db, data);

            if (oldSolid is not null && !oldSolid.IsErased)
                oldSolid.Erase();

            newSolid = null;

            message =
                $"ID={ShortId(data.GroupId)} | loft={geometry.LoftMode} | secoes={geometry.SectionCount} | " +
                $"H={data.Height:0.###} m";

            if (!propertySetOk)
                message += " | [AVISO Property Set] " + propertySetMessage;

            return true;
        }
        catch (System.Exception ex)
        {
            newSolid?.Dispose();
            message = ex.Message;
            return false;
        }
    }

    private static bool TrySelectFeatureLine(
        Editor ed,
        AcadDb.Transaction tr,
        string message,
        out CivilFeatureLine? featureLine,
        out AcadDb.ObjectId objectId)
    {
        featureLine = null;
        objectId = AcadDb.ObjectId.Null;

        var options = new PromptEntityOptions(message);
        options.SetRejectMessage("\nO objeto precisa ser uma Feature Line do Civil 3D.");
        options.AddAllowedClass(typeof(CivilFeatureLine), false);

        PromptEntityResult result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
            return false;

        featureLine = tr.GetObject(
            result.ObjectId,
            AcadDb.OpenMode.ForRead,
            false) as CivilFeatureLine;
        if (featureLine is null)
            return false;

        objectId = result.ObjectId;
        return true;
    }

    private static bool TrySelectManagedPassage(
        Editor ed,
        AcadDb.Transaction tr,
        AcadDb.Database db,
        out AcadDb.Solid3d solid,
        out RaisedCrossingData data)
    {
        solid = null!;
        data = null!;

        var options = new PromptEntityOptions("\nSelecione um sólido de PASSAGEM parametrizada: ");
        options.SetRejectMessage("\nSelecione um 3D Solid.");
        options.AddAllowedClass(typeof(AcadDb.Solid3d), true);

        PromptEntityResult result = ed.GetEntity(options);
        if (result.Status != PromptStatus.OK)
            return false;

        solid = tr.GetObject(result.ObjectId, AcadDb.OpenMode.ForRead, false) as AcadDb.Solid3d;
        if (solid is null)
            return false;

        string? groupId = RaisedCrossingPersistence.TryReadSolidGroupId(tr, solid);
        if (string.IsNullOrWhiteSpace(groupId))
        {
            ed.WriteMessage("\nEste Solid3d não pertence ao módulo PASSAGEM.");
            return false;
        }

        data = RaisedCrossingPersistence.LoadPassage(tr, db, groupId);
        if (data is null)
        {
            ed.WriteMessage("\nOs dados persistidos desta passagem não foram encontrados.");
            return false;
        }

        return true;
    }

    private static List<string> GetSelectedPassageIds(
        Editor ed,
        AcadDb.Transaction tr)
    {
        var filter = new SelectionFilter(new[]
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Start, "3DSOLID")
        });

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nSelecione os sólidos de PASSAGEM: "
        };

        PromptSelectionResult selection = ed.GetSelection(options, filter);
        if (selection.Status != PromptStatus.OK)
            return new List<string>();

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AcadDb.ObjectId objectId in selection.Value.GetObjectIds())
        {
            try
            {
                AcadDb.DBObject obj = tr.GetObject(objectId, AcadDb.OpenMode.ForRead, false);
                string? groupId = RaisedCrossingPersistence.TryReadSolidGroupId(tr, obj);
                if (!string.IsNullOrWhiteSpace(groupId))
                    ids.Add(groupId);
            }
            catch
            {
            }
        }

        return ids.ToList();
    }

    private static CivilFeatureLine? OpenFeatureLine(AcadDb.Transaction tr, AcadDb.ObjectId id)
    {
        if (id.IsNull)
            return null;

        try
        {
            return tr.GetObject(id, AcadDb.OpenMode.ForRead, false) as CivilFeatureLine;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryAskAnchorMode(
        Editor ed,
        CrossingAnchorMode current,
        out CrossingAnchorMode mode)
    {
        mode = current;
        string defaultKeyword = current switch
        {
            CrossingAnchorMode.Start => "Inicio",
            CrossingAnchorMode.End => "Fim",
            _ => "Meio"
        };

        var options = new PromptKeywordOptions(
            $"\nPosição do ponto de referência [Meio/Inicio/Fim] <{defaultKeyword}>: ")
        {
            AllowNone = true
        };
        options.Keywords.Add("Meio");
        options.Keywords.Add("Inicio");
        options.Keywords.Add("Fim");
        options.Keywords.Default = defaultKeyword;

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel)
            return false;

        string value = result.Status == PromptStatus.None ? defaultKeyword : result.StringResult;
        mode = value switch
        {
            "Inicio" => CrossingAnchorMode.Start,
            "Fim" => CrossingAnchorMode.End,
            _ => CrossingAnchorMode.Middle
        };
        return true;
    }

    private static bool TryAskLength(
        Editor ed,
        CivilFeatureLine featureLine,
        double referenceDistance,
        CrossingLengthInputMode currentMode,
        out double length,
        out CrossingLengthInputMode selectedMode)
    {
        length = 0.0;
        selectedMode = currentMode;
        string defaultKeyword = currentMode == CrossingLengthInputMode.Length ? "Extensao" : "Clique";

        var options = new PromptKeywordOptions(
            $"\nDefinir comprimento por [Clique/Extensao] <{defaultKeyword}>: ")
        {
            AllowNone = true
        };
        options.Keywords.Add("Clique");
        options.Keywords.Add("Extensao");
        options.Keywords.Default = defaultKeyword;

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel)
            return false;

        string mode = result.Status == PromptStatus.None ? defaultKeyword : result.StringResult;
        if (mode.Equals("Extensao", StringComparison.OrdinalIgnoreCase))
        {
            selectedMode = CrossingLengthInputMode.Length;
            double? value = AskDouble(ed, "\nComprimento do PLATÔ elevado (sem as rampas)", 4.00, allowZero: false);
            if (!value.HasValue)
                return false;
            length = value.Value;
            return true;
        }

        selectedMode = CrossingLengthInputMode.Click;
        PromptPointResult second = ed.GetPoint(
            new PromptPointOptions("\nClique a segunda posição sobre a Feature Line 1 para definir o comprimento do PLATÔ: "));
        if (second.Status != PromptStatus.OK)
            return false;

        Point3d secondWcs = ToWcs(ed, second.Value);
        Point3d projected = RaisedCrossingGeometry.ProjectPointToFeatureLine(featureLine, secondWcs);
        double secondDistance = featureLine.GetDistAtPoint(projected);
        length = Math.Abs(secondDistance - referenceDistance);

        if (length <= Epsilon)
        {
            ed.WriteMessage("\nA segunda posição resultou em comprimento nulo.");
            return false;
        }

        ed.WriteMessage($"\nComprimento do platô medido na Feature Line 1: {length:0.###} m.");
        return true;
    }


    private static double? AskDouble(
        Editor ed,
        string message,
        double defaultValue,
        bool allowZero)
    {
        var options = new PromptDoubleOptions(
            message + " <" + defaultValue.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
        {
            AllowNone = true,
            UseDefaultValue = true,
            DefaultValue = defaultValue,
            AllowNegative = false,
            AllowZero = allowZero
        };

        PromptDoubleResult result = ed.GetDouble(options);
        if (result.Status == PromptStatus.Cancel)
            return null;
        if (result.Status == PromptStatus.None)
            return defaultValue;
        if (result.Status != PromptStatus.OK)
            return null;
        return result.Value;
    }

    private static AcadDb.BlockTableRecord GetModelSpace(
        AcadDb.Transaction tr,
        AcadDb.Database db) =>
        (AcadDb.BlockTableRecord)tr.GetObject(
            AcadDb.SymbolUtilityServices.GetBlockModelSpaceId(db),
            AcadDb.OpenMode.ForWrite);

    private static Point3d ToWcs(Editor ed, Point3d pointInUcs) =>
        pointInUcs.TransformBy(ed.CurrentUserCoordinateSystem);

    private static double SafeFraction(double distance, double total) =>
        total <= Epsilon ? 0.0 : Math.Max(0.0, Math.Min(1.0, distance / total));

    private static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id) ? "?" : id.Length <= 8 ? id : id[..8];


}
