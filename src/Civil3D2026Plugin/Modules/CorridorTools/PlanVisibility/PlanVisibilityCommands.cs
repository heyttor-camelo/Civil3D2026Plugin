using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

/// <summary>
/// C3DOCULTARPLANTA: solidos/bodies dos layers configurados ficam ocultos nas
/// vistas de planta e visiveis nas vistas 3D (ex.: solidos extraidos do
/// corredor). Configuracao por usuario, valida para todos os desenhos.
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
                " - solidos dos layers configurados aparecem so nas vistas 3D.");
            WriteStatus(ed);

            // Primeiro uso: sem prefixos, pede antes de qualquer outra opcao.
            if (PlanVisibilitySettings.Prefixes.Count == 0)
            {
                if (!AskPrefixes(ed))
                    return;

                PlanVisibilitySettings.Enabled = true;
                Commit(ed);
                return;
            }

            string toggle = PlanVisibilitySettings.Enabled ? "Desligar" : "Ligar";

            PromptKeywordOptions options = new(
                "\nOpcao [Ligar/Desligar/Prefixos] <" + toggle + ">: ",
                "Ligar Desligar Prefixos")
            {
                AllowNone = true
            };

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                return;

            string choice = result.Status == PromptStatus.OK ? result.StringResult : toggle;

            if (string.Equals(choice, "Prefixos", StringComparison.OrdinalIgnoreCase))
            {
                if (!AskPrefixes(ed))
                    return;

                PlanVisibilitySettings.Enabled = PlanVisibilitySettings.Prefixes.Count > 0;
            }
            else
            {
                PlanVisibilitySettings.Enabled = string.Equals(choice, "Ligar", StringComparison.OrdinalIgnoreCase);
            }

            Commit(ed);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Corridor.OcultarPlanta + "] " + ex.Message);
        }
    }

    private static bool AskPrefixes(Editor ed)
    {
        string current = PlanVisibilitySettings.PrefixesText;

        PromptStringOptions options = new(
            "\nPrefixos de layer separados por ; (ex.: CORR-SOL;C-PAV)" +
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
        {
            ed.WriteMessage("\nNenhum prefixo informado. A ocultacao em planta fica desligada.");
        }

        return true;
    }

    private static void Commit(Editor ed)
    {
        PlanVisibilitySettings.Save();
        PlanVisibilityManager.Apply();

        // Redesenha todos os viewports para aplicar imediatamente.
        ed.Regen();

        WriteStatus(ed);
    }

    private static void WriteStatus(Editor ed)
    {
        string prefixes = PlanVisibilitySettings.Prefixes.Count == 0
            ? "(nenhum)"
            : PlanVisibilitySettings.PrefixesText;

        ed.WriteMessage(
            "\n  Estado: " + (PlanVisibilityManager.IsActive ? "LIGADO" : "DESLIGADO") +
            " | prefixos: " + prefixes);

        if (PlanVisibilityManager.IsActive)
        {
            ed.WriteMessage(
                "\n  Solidos e bodies nesses layers nao sao desenhados em vistas de planta" +
                " (camera olhando para baixo) e aparecem normalmente nas vistas 3D.");
        }
    }
}
