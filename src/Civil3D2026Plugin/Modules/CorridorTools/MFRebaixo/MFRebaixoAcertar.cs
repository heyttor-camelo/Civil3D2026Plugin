using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// "Acertar altura final" (17/11/2026, pedido do Heyvid: altura 0 pedida e o meio-fio ficava com 1,9 / 7,7 cm):
/// refaz os rebaixos cuja altura obtida no trecho rebaixado difere da pedida. Cada um e removido e lancado de
/// novo com a altura pedida, as mesmas estacas e declividades; o lancamento escolhe sozinho o caminho: altura
/// final direta (MFSARJETA 1.27+, REB_K + ALT_FINAL) ou REB acertado secao por secao (peca antiga).
/// </summary>
public sealed partial class MFRebaixoCommands
{
    [CommandMethod(
        C3DCommands.Corridor.MfRebaixoAcertar,
        CommandFlags.Modal)]
    public void AcertarAlturaFinal()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        try
        {
            List<RebaixoInfo> all = MFRebaixoData.ReadAll(doc.Database);
            var mode = new PromptKeywordOptions("\nAcertar a altura final de quais rebaixos? [Desviados/Todos] <Desviados>: ", "Desviados Todos")
            {
                AllowNone = true
            };
            mode.Keywords.Default = "Desviados";
            PromptResult m = ed.GetKeywords(mode);
            if (m.Status == PromptStatus.Cancel)
                return;
            bool everyone = m.Status == PromptStatus.OK && m.StringResult == "Todos";
            List<RebaixoInfo> targets = all.Where(r => r.TargetFinal is not null && (everyone || r.FinalOff)).ToList();
            if (targets.Count == 0)
            {
                ed.WriteMessage(everyone
                    ? "\nNenhum rebaixo com altura final legivel no desenho.\n"
                    : $"\nNenhum rebaixo com a altura obtida diferente da pedida (mais de {FinalHeightTolerance * 1000:0} mm). Nada mudou.\n");
                return;
            }

            ed.WriteMessage($"\n{targets.Count} rebaixo(s) a refazer com a altura final pedida:");
            foreach (RebaixoInfo r in targets)
                ed.WriteMessage("\n  " + DescribeFinalFix(r));
            ed.WriteMessage("\nCada um e removido e lancado de novo (mesmas estacas e declividades; se um falhar, ele fica como estava). U desfaz tudo.");
            var ok = new PromptKeywordOptions($"\nAcertar estes {targets.Count} rebaixo(s)? [Acertar/Cancelar] <Cancelar>: ", "Acertar Cancelar")
            {
                AllowNone = true
            };
            ok.Keywords.Default = "Cancelar";
            PromptResult a = ed.GetKeywords(ok);
            if (a.Status != PromptStatus.OK || a.StringResult != "Acertar")
            {
                ed.WriteMessage("\nCancelado. Nada mudou.\n");
                return;
            }

            (int done, List<string> errors) = FixFinalHeights(doc.Database, ed, targets);
            string fim = $"{done} de {targets.Count} rebaixo(s) acertado(s)." + (errors.Count > 0 ? " Nao deu: " + string.Join("; ", errors) : "") + " U desfaz.";
            ed.WriteMessage("\n" + fim + "\n");
            PluginLog.Info("MFREBAIXO", "MFREBAIXOACERTAR: " + fim);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFREBAIXO", "MFREBAIXOACERTAR falhou: " + ex.Message, ex);
            ed.WriteMessage("\nErro ao acertar a altura final: " + ex.Message + "\n");
        }
    }

    private static string Cm(double? v) => v is double d ? (d * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + " cm" : "?";

    /// <summary>"R03 (Direito, 99.60 - 101.60): hoje 1.9 a 7.7 cm -> 0.0 cm".</summary>
    internal static string DescribeFinalFix(RebaixoInfo r)
    {
        string now = r.ExposedMin is double lo && r.ExposedMax is double hi
            ? Math.Abs(hi - lo) < 0.0005 ? Cm(lo) : $"{Cm(lo)} a {Cm(hi)}"
            : Cm(r.FinalHeight);
        return $"{r.Label} ({r.SideLabel}, {r.Stations}): hoje {now} -> {Cm(r.TargetFinal)}";
    }

    /// <summary>
    /// Remove e lanca de novo cada rebaixo com a altura final pedida (TargetFinal), as mesmas estacas S1/S2 e as
    /// declividades dele. Se o lancamento falhar, tenta pôr o rebaixo de volta como estava.
    /// </summary>
    internal static (int Done, List<string> Errors) FixFinalHeights(Database db, Editor ed, List<RebaixoInfo> targets)
    {
        int done = 0;
        var errors = new List<string>();
        foreach (RebaixoInfo r in targets)
        {
            if (r.TargetFinal is not double h)
            {
                errors.Add($"{r.Label}: sem altura pedida legivel");
                continue;
            }
            double? oldH = r.FinalHeight;
            MFRebaixoData.Remove(db, r, rebuild: true);
            try
            {
                ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, r.S1, r.S2, h, MFRebaixoData.CommentFor(r.CustomName), r.EntrySlope, r.ExitSlope);
                done++;
            }
            catch (System.Exception ex)
            {
                try
                {
                    ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, r.S1, r.S2, oldH ?? h, MFRebaixoData.CommentFor(r.CustomName), r.EntrySlope, r.ExitSlope);
                    errors.Add($"{r.Label}: {ex.Message} (ficou como estava)");
                }
                catch
                {
                    errors.Add($"{r.Label}: {ex.Message} (o antigo tambem nao voltou; use U para desfazer)");
                }
            }
        }
        return (done, errors);
    }
}
