using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// HYCATALOGO (fase H5): mostra os catalogos de pecas da pasta e os ativos no desenho, abre o
/// SETPIPENETWORKCATALOG nativo para apontar para os CSBIM, reindexa (PARTCATALOGREGEN) e
/// importa familias/tamanhos para uma parts list, por capitulo.
/// </summary>
public sealed class HydroCatalogCommands
{
    private const string Module = "Hydro";

    [CommandMethod(C3DCommands.Hydro.Catalogo, CommandFlags.Modal)]
    public void Catalogo()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        try
        {
            using (PluginLog.Medir(Module, "HYCATALOGO"))
                Run(doc, ed);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, "HYCATALOGO falhou", ex);
            ed.WriteMessage("\n[HYDRO] HYCATALOGO falhou: " + ex.Message);
        }

        HydroPanel.NotifyChanged();
    }

    private static void Run(Document doc, Editor ed)
    {
        List<HydroCatalog.CatalogInfo> catalogs = HydroCatalog.Scan();
        ed.WriteMessage($"\nPasta dos catalogos: {HydroCatalog.Root}");
        if (catalogs.Count == 0)
        {
            ed.WriteMessage("\nNenhum catalogo (.apc) encontrado na pasta.");
            return;
        }

        HydroCatalog.CatalogInfo? activeS = HydroCatalog.Active(CivilDb.DomainType.Structure, catalogs);
        HydroCatalog.CatalogInfo? activeP = HydroCatalog.Active(CivilDb.DomainType.Pipe, catalogs);
        ed.WriteMessage("\nCatalogos instalados:");
        for (int i = 0; i < catalogs.Count; i++)
        {
            HydroCatalog.CatalogInfo c = catalogs[i];
            bool active = ReferenceEquals(c, activeS) || ReferenceEquals(c, activeP);
            ed.WriteMessage($"\n  {i + 1}: [{c.Domain,-9}] {System.IO.Path.GetFileName(c.Folder)}  \"{c.Name}\"  {c.Parts.Count} peca(s) em {c.Chapters.Count()} capitulo(s)" +
                            (c.IsCsbim ? (c.Sanitized ? "  CSBIM saneado" : "  CSBIM (NAO saneado: tools/Sanitize-CsbimCatalog.ps1 -Apply)") : "") + (active ? "  <- ATIVO no desenho" : ""));
        }

        ed.WriteMessage($"\nAtivo para estruturas: {(activeS is null ? "nenhum reconhecido (catalogo vazio ou fora da pasta)" : activeS.Name)}; para tubos: {(activeP is null ? "nenhum reconhecido" : activeP.Name)}.");
        if (activeS?.IsCsbim != true || activeP?.IsCsbim != true)
            ed.WriteMessage("\nO desenho NAO esta apontando para os dois catalogos CSBIM. Use [Definir] (dialogo nativo) e depois [Regenerar].");

        PromptKeywordOptions ko = new("\nOpcao [Definir/Regenerar/Importar/Sair]") { AllowNone = true };
        ko.Keywords.Add("Definir");
        ko.Keywords.Add("Regenerar");
        ko.Keywords.Add("Importar");
        ko.Keywords.Add("Sair");
        ko.Keywords.Default = "Importar";
        PromptResult kr = ed.GetKeywords(ko);
        string op = kr.Status == PromptStatus.None ? "Importar" : kr.Status == PromptStatus.OK ? kr.StringResult : "Sair";
        switch (op)
        {
            case "Definir":
                // A API 2026 nao expoe o caminho do catalogo ativo: so o comando nativo (dialogo).
                ed.WriteMessage("\nAbrindo o dialogo nativo: escolha a pasta e os catalogos CSBIM (estruturas U3929348-..., tubos 93920319-...). Depois rode HYCATALOGO > Regenerar.");
                doc.SendStringToExecute("SETPIPENETWORKCATALOG ", true, false, false);
                return;
            case "Regenerar":
                ed.WriteMessage("\nReindexando os catalogos (PARTCATALOGREGEN)...");
                doc.SendStringToExecute("PARTCATALOGREGEN ", true, false, false);
                return;
            case "Importar":
                Import(doc, ed, catalogs, activeS, activeP);
                return;
        }
    }

    private static void Import(Document doc, Editor ed, List<HydroCatalog.CatalogInfo> catalogs, HydroCatalog.CatalogInfo? activeS, HydroCatalog.CatalogInfo? activeP)
    {
        List<HydroCatalog.CatalogInfo> targets = new();
        PromptKeywordOptions wo = new("\nImportar de [Estruturas/Tubos/Ambos]") { AllowNone = true };
        wo.Keywords.Add("Estruturas");
        wo.Keywords.Add("Tubos");
        wo.Keywords.Add("Ambos");
        wo.Keywords.Default = "Ambos";
        PromptResult wr = ed.GetKeywords(wo);
        string which = wr.Status == PromptStatus.None ? "Ambos" : wr.Status == PromptStatus.OK ? wr.StringResult : "";
        if (which.Length == 0)
            return;
        if (which != "Tubos")
            targets.Add(activeS ?? catalogs.First(c => c.Domain == "Structure" && c.IsCsbim));
        if (which != "Estruturas")
            targets.Add(activeP ?? catalogs.First(c => c.Domain == "Pipe" && c.IsCsbim));

        List<string> names = HydroEditor.PartsListNames(doc.Database);
        ed.WriteMessage("\nParts lists do desenho: " + (names.Count == 0 ? "(nenhuma)" : string.Join(", ", names)));
        PromptStringOptions so = new("\nParts list de destino (nova se nao existir)") { DefaultValue = names.Contains("CSBIM") || names.Count == 0 ? "CSBIM" : "CSBIM", UseDefaultValue = true, AllowSpaces = true };
        PromptResult sr = ed.GetString(so);
        if (sr.Status != PromptStatus.OK)
            return;
        string plName = sr.StringResult.Trim().Length == 0 ? "CSBIM" : sr.StringResult.Trim();

        foreach (HydroCatalog.CatalogInfo cat in targets)
        {
            if (!ReferenceEquals(cat, activeS) && !ReferenceEquals(cat, activeP))
            {
                ed.WriteMessage($"\n{cat.Name} ({cat.Domain}) nao e o catalogo ativo do desenho: defina com SETPIPENETWORKCATALOG antes; pulando.");
                continue;
            }

            List<string> chapters = cat.Chapters.ToList();
            ed.WriteMessage($"\nCapitulos de {cat.Name}: {string.Join(", ", chapters.Select((c, i) => $"{i + 1}={c}"))}");
            PromptStringOptions co = new("\nCapitulos a importar (numeros separados por virgula, ou Enter = todos)") { DefaultValue = "", UseDefaultValue = true, AllowSpaces = true };
            PromptResult cr = ed.GetString(co);
            if (cr.Status != PromptStatus.OK)
                return;
            List<string>? chosen = null;
            string txt = cr.StringResult.Trim();
            if (txt.Length > 0)
            {
                chosen = txt.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => int.TryParse(t, out int n) && n >= 1 && n <= chapters.Count ? chapters[n - 1] : t).ToList();
            }

            HydroCatalog.ImportReport r = HydroCatalog.Import(doc.Database, plName, cat, chosen);
            ed.WriteMessage($"\n{cat.Name}: {r.Families} familia(s) e {r.Sizes} tamanho(s) importados para \"{plName}\"; {r.Skipped} pulado(s) (ja existiam ou fora do catalogo ativo); {r.Errors.Count} falha(s).");
            foreach (string e in r.Errors.Take(15))
                ed.WriteMessage("\n  - " + e);
            if (r.Errors.Count > 15)
                ed.WriteMessage($"\n  ... e mais {r.Errors.Count - 15} (ver C3DLOG)");
            foreach (string e in r.Errors)
                PluginLog.Aviso(Module, "Importacao: " + e);
        }

        ed.WriteMessage($"\nPara usar: nas propriedades da rede, troque a parts list para \"{plName}\" (ou crie a rede nova com ela no painel HYDRO > Redes > Nova rede).");
    }
}
