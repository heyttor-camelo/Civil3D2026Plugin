using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Comandos do modulo Layouts.</summary>
public sealed class LayoutCommands
{
    /// <summary>Explorador de layouts (janela flutuante).</summary>
    [CommandMethod(C3DCommands.Layouts.Painel, CommandFlags.Modal)]
    public void Painel() => LayoutPanel.ShowPanel();

    /// <summary>Abre o explorador na categoria Viewports.</summary>
    [CommandMethod(C3DCommands.Layouts.Viewports, CommandFlags.Modal)]
    public void Viewports() => LayoutPanel.ShowPanel("VIEWPORTS");

    /// <summary>Abre o explorador na categoria Layer state (ex-LSLAYOUTS).</summary>
    [CommandMethod(C3DCommands.Layouts.LayerState, CommandFlags.Modal)]
    public void LayerState() => LayoutPanel.ShowPanel("LAYERSTATE");

    /// <summary>Abre o explorador na categoria Carimbo.</summary>
    [CommandMethod(C3DCommands.Layouts.Carimbo, CommandFlags.Modal)]
    public void Carimbo() => LayoutPanel.ShowPanel("CARIMBO");

    /// <summary>Abre o explorador na categoria Page setup.</summary>
    [CommandMethod(C3DCommands.Layouts.PageSetup, CommandFlags.Modal)]
    public void PageSetup() => LayoutPanel.ShowPanel("PAGESETUP");

    /// <summary>Abre o explorador na categoria View frames.</summary>
    [CommandMethod(C3DCommands.Layouts.ViewFrames, CommandFlags.Modal)]
    public void ViewFrames() => LayoutPanel.ShowPanel("VIEWFRAMES");

    /// <summary>
    /// Mapa-chave: [Base/Area/Gerar/Atualizar/Remover/Painel]. Base e Area pedem selecao
    /// no desenho (por isso sao comandos); o resto tambem esta no painel.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.MapaChave, CommandFlags.Modal)]
    public void MapaChave()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pko = new PromptKeywordOptions("\nMapa-chave [Base/Area/Inserir/Gerar/Atualizar/Remover/Painel] <Painel>: ",
            "Base Area Inserir Gerar Atualizar Remover Painel") { AllowNone = true };
        PromptResult pr = ed.GetKeywords(pko);
        if (pr.Status == PromptStatus.Cancel || pr.Status == PromptStatus.Error)
            return;
        string op = pr.Status == PromptStatus.OK ? pr.StringResult : "Painel";

        OpResult? r = null;
        switch (op)
        {
            case "Base":
                if (!EscolherBase(doc))
                    return;
                r = LayoutKeyMap.Ler(doc.Database).TemCaixa ? LayoutKeyMap.Gerar(doc, true) : null;
                if (r is null)
                    ed.WriteMessage("\nBase guardada. Agora escolha a area do mapa no layout (C3DMAPACHAVE > Area).");
                break;
            case "Area":
                if (!EscolherArea(doc))
                    return;
                r = LayoutKeyMap.Ler(doc.Database).Base.Count > 0 ? LayoutKeyMap.Gerar(doc, false) : null;
                if (r is null)
                    ed.WriteMessage("\nArea guardada. Agora escolha a base no Model (C3DMAPACHAVE > Base).");
                break;
            case "Inserir":
                // Escolhe a area no layout atual e gera na hora (base ja escolhida).
                if (LayoutKeyMap.Ler(doc.Database).Base.Count == 0 && !EscolherBase(doc))
                    return;
                if (!EscolherArea(doc))
                    return;
                r = LayoutKeyMap.Gerar(doc, true);
                break;
            case "Gerar":
                r = LayoutKeyMap.Gerar(doc, true);
                break;
            case "Atualizar":
                r = LayoutKeyMap.Gerar(doc, false);
                break;
            case "Remover":
                r = LayoutKeyMap.Remover(doc);
                break;
            default:
                LayoutPanel.ShowPanel("MAPACHAVE");
                return;
        }

        if (r is not null)
            ed.WriteMessage("\n[Mapa-chave] " + r.Mensagem);
        LayoutKeyMap.AvisarMudanca();
    }

    private static bool EscolherBase(Document doc)
    {
        Editor ed = doc.Editor;
        string atual = LayoutManager.Current.CurrentLayout;
        bool trocou = Convert.ToInt16(AcApp.GetSystemVariable("TILEMODE")) == 0;
        if (trocou)
            AcApp.SetSystemVariable("TILEMODE", 1);

        PromptSelectionResult psr;
        try
        {
            psr = ed.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelecione no Model os objetos do mapa-chave (eixos, quadras, limites...): "
            });
        }
        finally
        {
            if (trocou)
                LayoutManager.Current.CurrentLayout = atual;
        }
        if (psr.Status != PromptStatus.OK)
            return false;

        KeyMapConfig cfg = LayoutKeyMap.Ler(doc.Database);
        cfg.Base.Clear();
        cfg.Base.AddRange(psr.Value.GetObjectIds());
        LayoutKeyMap.Gravar(doc, cfg);
        ed.WriteMessage($"\n{cfg.Base.Count} objeto(s) na base do mapa-chave.");
        return true;
    }

    private static bool EscolherArea(Document doc)
    {
        Editor ed = doc.Editor;
        if (Convert.ToInt16(AcApp.GetSystemVariable("TILEMODE")) == 1)
        {
            string? primeiro = LayoutOps.OrdemAtual(doc.Database).FirstOrDefault();
            if (primeiro is null)
            {
                ed.WriteMessage("\nO desenho nao tem layouts.");
                return false;
            }
            LayoutManager.Current.CurrentLayout = primeiro;
        }
        if (Convert.ToInt16(AcApp.GetSystemVariable("CVPORT")) != 1)
            ed.SwitchToPaperSpace();

        PromptPointResult p1 = ed.GetPoint("\nPrimeiro canto da area do mapa-chave (no papel): ");
        if (p1.Status != PromptStatus.OK)
            return false;
        PromptPointResult p2 = ed.GetCorner("\nCanto oposto: ", p1.Value);
        if (p2.Status != PromptStatus.OK)
            return false;

        Matrix3d ucs = ed.CurrentUserCoordinateSystem;
        Point3d a = p1.Value.TransformBy(ucs), b = p2.Value.TransformBy(ucs);
        KeyMapConfig cfg = LayoutKeyMap.Ler(doc.Database);
        cfg.CaixaMin = new Point2d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        cfg.CaixaMax = new Point2d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        LayoutKeyMap.Gravar(doc, cfg);
        ed.WriteMessage("\nArea do mapa-chave guardada (vale para todos os layouts).");
        return true;
    }

    /// <summary>
    /// Pranchas do eixo: [Painel/Atualizar/PontoPerfis]. O assistente fica no explorador; aqui
    /// ficam o que pede contexto de comando (clicar o ponto dos perfis) e a atualizacao.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.Pranchas, CommandFlags.Modal)]
    public void Pranchas()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        var pko = new PromptKeywordOptions("\nPranchas do eixo [Painel/Atualizar/PontoPerfis] <Painel>: ", "Painel Atualizar PontoPerfis") { AllowNone = true };
        PromptResult pr = ed.GetKeywords(pko);
        if (pr.Status == PromptStatus.Cancel || pr.Status == PromptStatus.Error)
            return;
        string op = pr.Status == PromptStatus.OK ? pr.StringResult : "Painel";

        switch (op)
        {
            case "Atualizar":
                try
                {
                    ed.WriteMessage("\n" + LayoutSheetSeries.AtualizarTodas(doc).Mensagem);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage("\n[Pranchas] " + ex.Message);
                }
                break;
            case "PontoPerfis":
            {
                var ppo = new PromptPointOptions("\nPonto do primeiro perfil no Model (origem do grafico; os demais vao para baixo): ");
                PromptPointResult ppr = ed.GetPoint(ppo);
                if (ppr.Status != PromptStatus.OK)
                    return;
                Point3d p = ppr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                LayoutSettings.Set("pp_base", p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";" +
                                              p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                ed.WriteMessage($"\nOs perfis das proximas pranchas comecam em {p.X:0.##}, {p.Y:0.##}.");
                LayoutPanel.ShowPanel("PRANCHAS");
                break;
            }
            default:
                LayoutPanel.ShowPanel("PRANCHAS");
                break;
        }
    }

    /// <summary>Insere o bloco de prancha configurado no painel (ou A1) no ponto clicado.</summary>
    [CommandMethod(C3DCommands.Layouts.Prancha, CommandFlags.Modal)]
    public void Prancha()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        SheetBlockOptions o = LayoutPanel.PranchaAtual ?? new SheetBlockOptions();
        var ppo = new PromptPointOptions($"\nPonto de insercao da prancha {o.NomeBloco()} [Origem] <0,0>: ", "Origem") { AllowNone = true };
        PromptPointResult ppr = doc.Editor.GetPoint(ppo);
        Point3d p = Point3d.Origin;
        if (ppr.Status == PromptStatus.OK)
            p = ppr.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
        else if (ppr.Status != PromptStatus.None && ppr.Status != PromptStatus.Keyword)
            return;
        OpResult r = LayoutSheetBlock.Inserir(doc, o, p);
        doc.Editor.WriteMessage("\n" + r.Mensagem);
    }

    /// <summary>
    /// Numeracao de pranchas direto na linha de comando (ex-NP):
    /// [Configurar/Atualizar] como na LISP original.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.Numerar, CommandFlags.Modal)]
    public void Numerar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;

        var pko = new PromptKeywordOptions("\nNumeracao de pranchas [Configurar/Atualizar] <Atualizar>: ", "Configurar Atualizar") { AllowNone = true };
        PromptResult pr = ed.GetKeywords(pko);
        if (pr.Status == PromptStatus.Cancel || pr.Status == PromptStatus.Error)
            return;

        if (pr.Status == PromptStatus.OK && pr.StringResult == "Configurar")
        {
            if (!Escolher(doc, true) || !Escolher(doc, false))
                return;
        }

        OpResult r = LayoutNumbering.Numerar(doc, new NumberingRule());
        ed.WriteMessage("\n" + r.Mensagem);
        ed.Regen();
    }

    private static bool Escolher(Document doc, bool numero)
    {
        var opt = new PromptNestedEntityOptions(numero
            ? "\nClique no texto/atributo do NUMERO da prancha: "
            : "\nClique no texto/atributo do TOTAL de pranchas: ");
        PromptNestedEntityResult res = doc.Editor.GetNestedEntity(opt);
        if (res.Status != PromptStatus.OK)
            return false;

        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(res.ObjectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead)
                is not (Autodesk.AutoCAD.DatabaseServices.DBText or Autodesk.AutoCAD.DatabaseServices.MText or Autodesk.AutoCAD.DatabaseServices.AttributeReference))
            {
                doc.Editor.WriteMessage("\nSelecione um texto, MTEXT ou atributo de bloco.");
                return false;
            }
            tr.Commit();
        }

        LayoutNumbering.Marcar(doc, new[] { res.ObjectId }, numero ? "Numero" : "Total", moverLayer: true);
        doc.Editor.WriteMessage($"\nTexto marcado como {(numero ? "numero" : "total")} (layer {(numero ? LayoutOps.LayerNumero : LayoutOps.LayerTotal)}).");
        return true;
    }
}
