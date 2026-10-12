using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

/// <summary>
/// C3DOCULTARPLANTA: como os solidos/bodies dos layers configurados aparecem
/// nas vistas de planta, mantendo-os normais nas vistas 3D:
/// - Ocultar: nao aparecem em planta;
/// - Exato: aparecem so com as arestas visiveis de cima (linhas ocultas
///   removidas entre todos os solidos), inclusive em 2D Wireframe e na plotagem.
/// C3DPLANTAATUALIZAR: recalcula a representacao Exato do desenho inteiro.
/// </summary>
public sealed class PlanVisibilityCommands
{
    public const string Versao = C3DVersions.PlanVisibility;

    [CommandMethod(C3DCommands.Corridor.OcultarPlanta, CommandFlags.Modal)]
    public void OcultarPlanta()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;

        try
        {
            ed.WriteMessage(
                "\n" + C3DCommands.Corridor.OcultarPlanta + " v" + Versao +
                " - solidos em planta: ocultos ou so com arestas visiveis; normais no 3D.");
            WriteStatus(doc);

            // Primeiro uso: sem prefixos, configura tudo em sequencia.
            if (PlanVisibilitySettings.Prefixes.Count == 0)
            {
                if (!AskPrefixes(ed) || !AskMode(ed))
                    return;

                PlanVisibilitySettings.Enabled = PlanVisibilitySettings.Prefixes.Count > 0;
                Commit(doc, fullRefresh: PlanVisibilitySettings.Mode == PlanDisplayMode.Exato);
                return;
            }

            string toggle = PlanVisibilitySettings.Enabled ? "Desligar" : "Ligar";

            PromptKeywordOptions options = new(
                "\nOpcao [Ligar/Desligar/Prefixos/Modo/Auto/Atualizar] <" + toggle + ">: ",
                "Ligar Desligar Prefixos Modo Auto Atualizar")
            {
                AllowNone = true
            };

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                return;

            string choice = result.Status == PromptStatus.OK ? result.StringResult : toggle;
            bool fullRefresh = false;

            switch (choice)
            {
                case "Prefixos":
                    if (!AskPrefixes(ed))
                        return;
                    PlanVisibilitySettings.Enabled = PlanVisibilitySettings.Prefixes.Count > 0;
                    fullRefresh = PlanVisibilitySettings.Mode == PlanDisplayMode.Exato;
                    break;

                case "Modo":
                    if (!AskMode(ed))
                        return;
                    fullRefresh = PlanVisibilitySettings.Mode == PlanDisplayMode.Exato;
                    break;

                case "Auto":
                    if (!AskAuto(ed))
                        return;
                    break;

                case "Atualizar":
                    fullRefresh = true;
                    break;

                case "Ligar":
                    PlanVisibilitySettings.Enabled = true;
                    fullRefresh = PlanVisibilitySettings.Mode == PlanDisplayMode.Exato;
                    break;

                default:
                    PlanVisibilitySettings.Enabled = false;
                    break;
            }

            Commit(doc, fullRefresh);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Corridor.OcultarPlanta + "] " + ex.Message);
        }
    }

    [CommandMethod(C3DCommands.Corridor.PlantaAtualizar, CommandFlags.Modal)]
    public void PlantaAtualizar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;

        try
        {
            if (!PlanVisibilityManager.IsActive || PlanVisibilitySettings.Mode != PlanDisplayMode.Exato)
            {
                ed.WriteMessage(
                    "\nO modo Exato nao esta ativo. Use " + C3DCommands.Corridor.OcultarPlanta +
                    " > Modo > Exato.");
                return;
            }

            RunFullRefresh(doc);
            ed.Regen();
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Corridor.PlantaAtualizar + "] " + ex.Message);
        }
    }

    private static bool AskPrefixes(Editor ed)
    {
        string current = PlanVisibilitySettings.PrefixesText;

        PromptStringOptions options = new(
            "\nPrefixos de layer separados por ; (ex.: CORR-SOL;C-PAV), * = todos os solidos" +
            (string.IsNullOrEmpty(current) ? ": " : " <" + current + ">: "))
        {
            AllowSpaces = true
        };

        PromptResult result = ed.GetString(options);
        if (result.Status != PromptStatus.OK)
            return false;

        string text = string.IsNullOrWhiteSpace(result.StringResult) ? current : result.StringResult;
        PlanVisibilitySettings.SetPrefixes(text);

        if (PlanVisibilitySettings.Prefixes.Count == 0)
            ed.WriteMessage("\nNenhum prefixo informado. A funcao fica desligada.");

        return true;
    }

    private static bool AskMode(Editor ed)
    {
        string current = PlanVisibilitySettings.Mode.ToString();

        ed.WriteMessage("\n  Ocultar: solidos nao aparecem em planta.");
        ed.WriteMessage("\n  Exato: aparecem so com as arestas visiveis de cima, inclusive em 2D Wireframe e na plotagem.");

        PromptKeywordOptions options = new(
            "\nModo em planta [Ocultar/Exato] <" + current + ">: ",
            "Ocultar Exato")
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return false;

        if (result.Status == PromptStatus.OK)
        {
            PlanVisibilitySettings.Mode = string.Equals(result.StringResult, "Exato", StringComparison.OrdinalIgnoreCase)
                ? PlanDisplayMode.Exato
                : PlanDisplayMode.Ocultar;
        }

        return true;
    }

    private static bool AskAuto(Editor ed)
    {
        string current = PlanVisibilitySettings.AutoRefresh ? "Sim" : "Nao";

        PromptKeywordOptions options = new(
            "\nAtualizar automaticamente apos alteracoes (modo Exato)? [Sim/Nao] <" + current + ">: ",
            "Sim Nao")
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return false;

        if (result.Status == PromptStatus.OK)
            PlanVisibilitySettings.AutoRefresh = string.Equals(result.StringResult, "Sim", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static void Commit(Document doc, bool fullRefresh)
    {
        PlanVisibilitySettings.Save();
        PlanRepresentationStore.ClearAllCaches();
        PlanVisibilityManager.Apply();

        if (fullRefresh && PlanVisibilityManager.IsActive && PlanVisibilitySettings.Mode == PlanDisplayMode.Exato)
            RunFullRefresh(doc);

        // Redesenha todos os viewports para aplicar imediatamente.
        doc.Editor.Regen();

        WriteStatus(doc);
    }

    private static void RunFullRefresh(Document doc)
    {
        doc.Editor.WriteMessage("\nCalculando arestas visiveis em planta (ESC cancela)...");

        try
        {
            PlanRepresentationStore.Refresh(doc, full: true, out string summary);
            doc.Editor.WriteMessage("\n  " + summary);
        }
        catch (OperationCanceledException)
        {
            doc.Editor.WriteMessage(
                "\n  Calculo cancelado. Nada foi gravado; as representacoes anteriores continuam valendo.");
        }
    }

    private static void WriteStatus(Document doc)
    {
        Editor ed = doc.Editor;
        string prefixes = PlanVisibilitySettings.Prefixes.Count == 0
            ? "(nenhum)"
            : PlanVisibilitySettings.PrefixesText;

        ed.WriteMessage(
            "\n  Estado: " + (PlanVisibilityManager.IsActive ? "LIGADO" : "DESLIGADO") +
            " | modo: " + PlanVisibilitySettings.Mode +
            " | auto: " + (PlanVisibilitySettings.AutoRefresh ? "sim" : "nao") +
            " | prefixos: " + prefixes);

        if (PlanVisibilityManager.IsActive && PlanVisibilitySettings.Mode == PlanDisplayMode.Exato)
        {
            int pending = PlanRepresentationStore.PendingCount(doc.Database);
            if (pending > 0)
            {
                ed.WriteMessage(
                    "\n  " + pending + " solido(s) aguardando atualizacao (aparecem completos em planta ate la)." +
                    " Use " + C3DCommands.Corridor.PlantaAtualizar + " ou ligue Auto.");
            }
        }
    }
}
