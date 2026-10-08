using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.CorridorSplit;

/// <summary>
/// CORRSPLIT v1.6: copia/move regioes entre dois corredores do mesmo DWG.
/// As alteracoes so sao confirmadas apos recriacao, verificacao e rebuild.
/// </summary>
public sealed class CorridorSplitCommands
{
    [CommandMethod(C3DCommands.Corridor.Split, CommandFlags.Modal)]
    public void Split()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var ed = doc.Editor;

        var prompt = new PromptEntityOptions("\nSelecione o Corridor de origem: ");
        prompt.SetRejectMessage("\nSelecione um objeto Corridor do Civil 3D.");
        prompt.AddAllowedClass(typeof(CivilDb.Corridor), true);
        var result = ed.GetEntity(prompt);
        if (result.Status != PromptStatus.OK) return;

        try
        {
            var choices = new List<RegionChoice>();
            string originalName;
            int surfaces;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var source = (CivilDb.Corridor)tr.GetObject(result.ObjectId, AcadDb.OpenMode.ForRead);
                originalName = source.Name;
                surfaces = source.CorridorSurfaces.Count;
                for (int b = 0; b < source.Baselines.Count; b++)
                {
                    var baseline = source.Baselines[b];
                    for (int r = 0; r < baseline.BaselineRegions.Count; r++)
                    {
                        var reg = baseline.BaselineRegions[r];
                        var targets = reg.GetTargets();
                        int assigned = 0;
                        foreach (CivilDb.SubassemblyTargetInfo t in targets)
                            if (t.TargetIds.Count > 0) assigned++;

                        choices.Add(new RegionChoice(b, r, FormatChoice(baseline, reg))
                        {
                            BaselineName = baseline.Name,
                            RegionName = reg.Name,
                            Horizontal = baseline.IsFeatureLineBased()
                                ? ReadName(tr, baseline.FeatureLineId)
                                : ReadName(tr, baseline.AlignmentId),
                            Vertical = baseline.IsFeatureLineBased()
                                ? "(Feature Line)"
                                : ReadName(tr, baseline.ProfileId),
                            Assembly = ReadName(tr, reg.AssemblyId),
                            StartStation = reg.StartStation,
                            EndStation = reg.EndStation,
                            TargetSummary = assigned.ToString(CultureInfo.InvariantCulture) + "/" +
                                targets.Count.ToString(CultureInfo.InvariantCulture),
                            HasMissingTargets = targets.Count > assigned
                        });
                    }
                }
                tr.Commit();
            }

            if (choices.Count == 0)
            {
                ed.WriteMessage("\n[CORRSPLIT] O corredor nao possui regioes.");
                return;
            }

            using var form = new CorridorSplitDialog(originalName, choices, surfaces, doc, result.ObjectId);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK) return;

            int moved;
            int baselineCount;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var source = (CivilDb.Corridor)tr.GetObject(result.ObjectId, AcadDb.OpenMode.ForWrite);
                // Impede executar com indices obsoletos se o corredor tiver mudado.
                foreach (var choice in form.Selected)
                {
                    if (choice.BaselineIndex >= source.Baselines.Count ||
                        choice.RegionIndex >= source.Baselines[choice.BaselineIndex].BaselineRegions.Count ||
                        choice.Label != FormatChoice(source.Baselines[choice.BaselineIndex],
                            source.Baselines[choice.BaselineIndex].BaselineRegions[choice.RegionIndex]))
                        throw new InvalidOperationException("O Corridor foi alterado desde a selecao. Execute o comando novamente.");
                }

                (moved, baselineCount) = CorridorSplitService.Execute(
                    CivilApplication.ActiveDocument, tr, source, form.TargetName,
                    form.Selected, form.RemoveFromOriginal);
                tr.Commit();
            }

            ed.WriteMessage("\n[CORRSPLIT v1.6] " + moved + " regiao(oes) em " +
                baselineCount + " baseline(s) criadas no corredor '" + form.TargetName + "'.");
            ed.WriteMessage(form.RemoveFromOriginal
                ? "\nTransferencia concluida. Regioes removidas da origem."
                : "\nCopia concluida. Corredor original mantido.");
            if (surfaces > 0)
                ed.WriteMessage("\nAVISO: Corridor Surfaces da origem NAO foram copiadas para o destino.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[CORRSPLIT v1.6] Operacao cancelada (rollback): " + ex.ToString());
        }
    }

    private static string ReadName(AcadDb.Transaction tr, AcadDb.ObjectId id)
    {
        if (id.IsNull) return "(nenhum)";
        try
        {
            var obj = tr.GetObject(id, AcadDb.OpenMode.ForRead);
            if (obj is CivilDb.Alignment alignment) return alignment.Name;
            if (obj is CivilDb.Profile profile) return profile.Name;
            if (obj is CivilDb.FeatureLine line) return line.Name;
            if (obj is CivilDb.Assembly assembly) return assembly.Name;
            return obj.GetType().Name;
        }
        catch (System.Exception)
        {
            return "(referencia indisponivel)";
        }
    }

    private static string FormatChoice(CivilDb.Baseline b, CivilDb.BaselineRegion r) =>
        b.Name + " [" + (b.IsFeatureLineBased() ? "Feature Line" : "Alignment") +
        "]  /  " + r.Name + "  (" +
        r.StartStation.ToString("F3", CultureInfo.InvariantCulture) + " - " +
        r.EndStation.ToString("F3", CultureInfo.InvariantCulture) + ")";
}

