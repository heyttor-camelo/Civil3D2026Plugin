using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

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
        {
            ed.WriteMessage("\n[Mapa-chave] " + r.Mensagem);
            PluginLog.Info("Layouts", "Mapa-chave: " + r.Mensagem);
        }
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
                    string msg = LayoutSheetSeries.AtualizarTodas(doc).Mensagem;
                    ed.WriteMessage("\n" + msg);
                    PluginLog.Info("Layouts", "Pranchas: " + msg);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage("\n[Pranchas] " + ex.Message);
                    PluginLog.Erro("Layouts", "Pranchas: atualizar (comando)", ex);
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
        PluginLog.Info("Layouts", "Inserir prancha: " + r.Mensagem);
    }

    /// <summary>
    /// Seta do norte: clica o ponto no layout; gira pela viewport embaixo (ou a mais perto).
    /// [Tamanho] muda a altura em mm; [Girar] acerta todos os nortes pelas viewports.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.Norte, CommandFlags.Modal)]
    public void Norte()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        bool model = Convert.ToInt32(AcApp.GetSystemVariable("TILEMODE")) == 1;
        if (!model && Convert.ToInt32(AcApp.GetSystemVariable("CVPORT")) != 1)
            ed.SwitchToPaperSpace();
        while (true)
        {
            double tam = LayoutSettings.GetDouble("norte_mm", 20);
            var ppo = new PromptPointOptions($"\nPonto da seta do norte ({LayoutNorte.Mm(tam)} mm) [Tamanho/Girar]: ", "Tamanho Girar");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status == PromptStatus.Keyword && ppr.StringResult == "Tamanho")
            {
                var pdo = new PromptDoubleOptions("\nAltura da seta do norte no papel (mm)") { DefaultValue = tam, UseDefaultValue = true, AllowNegative = false, AllowZero = false };
                PromptDoubleResult pdr = ed.GetDouble(pdo);
                if (pdr.Status == PromptStatus.OK)
                    LayoutSettings.Set("norte_mm", pdr.Value);
                continue;
            }
            if (ppr.Status == PromptStatus.Keyword && ppr.StringResult == "Girar")
            {
                OpResult g = LayoutNorte.Girar(doc);
                ed.WriteMessage("\n" + g.Mensagem);
                PluginLog.Info("Layouts", "Norte: " + g.Mensagem);
                return;
            }
            if (ppr.Status != PromptStatus.OK)
                return;
            Point3d p = ppr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            OpResult r = LayoutNorte.Inserir(doc, doc.Database.CurrentSpaceId, p, tam, ObjectId.Null);
            ed.WriteMessage("\n" + r.Mensagem);
            PluginLog.Info("Layouts", "Norte: " + r.Mensagem);
            return;
        }
    }

    /// <summary>
    /// Malha de coordenadas, norte e escala grafica de uma vez, direto no layout, pelo padrao salvo.
    /// [Inserir] nas viewports clicadas (ou Todas / Layout atual), [Padrao] le o padrao de uma
    /// viewport onde os tres foram lancados e ajustados a mao, [Configurar] muda malha e tamanhos,
    /// [Remover] apaga o conjunto das viewports.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.Conjunto, CommandFlags.Modal)]
    public void Conjunto()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        PadraoConjunto p = PadraoConjunto.Ler();
        ed.WriteMessage("
Padrao atual: " + p.Resumo() + ".");

        var pk = new PromptKeywordOptions("
Malha, norte e escala [Inserir/Padrao/Configurar/Remover] <Inserir>: ", "Inserir Padrao Configurar Remover") { AllowNone = true };
        PromptResult rk = ed.GetKeywords(pk);
        if (rk.Status != PromptStatus.OK && rk.Status != PromptStatus.None)
            return;
        string acao = rk.Status == PromptStatus.None ? "Inserir" : rk.StringResult;

        if (acao == "Configurar")
        {
            ConfigurarConjunto(ed, p);
            return;
        }

        bool emLayout = Convert.ToInt32(AcApp.GetSystemVariable("TILEMODE")) == 0;
        if (acao == "Padrao")
        {
            if (!emLayout)
            {
                ed.WriteMessage("
Abra o layout com a viewport ajustada.");
                return;
            }
            if (Convert.ToInt32(AcApp.GetSystemVariable("CVPORT")) != 1)
                ed.SwitchToPaperSpace();
            var peo = new PromptEntityOptions("
Clique a viewport onde malha, norte e escala foram ajustados: ");
            peo.SetRejectMessage("
O objeto selecionado nao e uma viewport.");
            peo.AddAllowedClass(typeof(Viewport), true);
            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
                return;
            OpResult r = LayoutConjunto.LerPadrao(doc, per.ObjectId, out PadraoConjunto novo);
            ed.WriteMessage("
" + r.Mensagem);
            if (r.Falhas == 0)
                ed.WriteMessage("
Novo padrao: " + novo.Resumo() + ".");
            PluginLog.Info("Layouts", "Malha/norte/escala: " + r.Mensagem);
            return;
        }

        // Viewports: clicadas no layout, todas do layout atual ou todas dos layouts.
        System.Collections.Generic.List<ObjectId> vps;
        var pko = new PromptKeywordOptions("
Viewports [Selecionar/Layout/Todas] <Selecionar>: ", "Selecionar Layout Todas") { AllowNone = true };
        PromptResult rv = ed.GetKeywords(pko);
        if (rv.Status != PromptStatus.OK && rv.Status != PromptStatus.None)
            return;
        string quais = rv.Status == PromptStatus.None ? "Selecionar" : rv.StringResult;
        if (quais == "Todas")
        {
            vps = LayoutConjunto.ViewportsDosLayouts(doc.Database, null);
        }
        else if (!emLayout)
        {
            ed.WriteMessage("
Abra um layout (ou use Todas).");
            return;
        }
        else if (quais == "Layout")
        {
            vps = LayoutConjunto.ViewportsDosLayouts(doc.Database, LayoutManager.Current.CurrentLayout);
        }
        else
        {
            if (Convert.ToInt32(AcApp.GetSystemVariable("CVPORT")) != 1)
                ed.SwitchToPaperSpace();
            var pso = new PromptSelectionOptions { MessageForAdding = "
Selecione as viewports: " };
            PromptSelectionResult psr = ed.GetSelection(pso, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "VIEWPORT") }));
            if (psr.Status != PromptStatus.OK)
                return;
            vps = psr.Value.GetObjectIds().ToList();
        }

        OpResult res = acao == "Remover" ? LayoutConjunto.Remover(doc, vps) : LayoutConjunto.Inserir(doc, vps, p);
        ed.WriteMessage("
" + res.Mensagem);
        PluginLog.Info("Layouts", "Malha/norte/escala: " + res.Mensagem);
    }

    private static void ConfigurarConjunto(Editor ed, PadraoConjunto p)
    {
        bool SimNao(string pergunta, bool atual)
        {
            var o = new PromptKeywordOptions($"
{pergunta} [Sim/Nao] <{(atual ? "Sim" : "Nao")}>: ", "Sim Nao") { AllowNone = true };
            PromptResult r = ed.GetKeywords(o);
            return r.Status == PromptStatus.OK ? r.StringResult == "Sim" : atual;
        }
        double Numero(string pergunta, double atual)
        {
            var o = new PromptDoubleOptions("
" + pergunta) { DefaultValue = atual, UseDefaultValue = true, AllowNegative = false, AllowZero = false };
            PromptDoubleResult r = ed.GetDouble(o);
            return r.Status == PromptStatus.OK ? r.Value : atual;
        }

        p.Malha = SimNao("Inserir a malha de coordenadas?", p.Malha);
        if (p.Malha)
        {
            p.MalhaMm = Numero("Distancia aproximada entre as linhas da malha no papel (mm; o intervalo sai redondo: 50, 100, 200, 500 m...)", p.MalhaMm);
            p.MalhaTexto = Numero("Altura do texto das coordenadas (mm)", p.MalhaTexto);
            var o = new PromptKeywordOptions($"
Malha em [Linhas/Cruzes] <{(p.MalhaCruzes ? "Cruzes" : "Linhas")}>: ", "Linhas Cruzes") { AllowNone = true };
            PromptResult r = ed.GetKeywords(o);
            if (r.Status == PromptStatus.OK)
                p.MalhaCruzes = r.StringResult == "Cruzes";
            var ps = new PromptStringOptions($"
Layer da malha <{p.MalhaLayer}>: ") { AllowSpaces = true };
            PromptResult rs = ed.GetString(ps);
            if (rs.Status == PromptStatus.OK && rs.StringResult.Trim().Length > 0)
                p.MalhaLayer = rs.StringResult.Trim();
        }
        p.Norte = SimNao("Inserir o norte?", p.Norte);
        if (p.Norte)
            p.NorteTamanho = Numero("Altura do norte no papel (mm)", p.NorteTamanho);
        p.Escala = SimNao("Inserir a escala grafica?", p.Escala);
        if (p.Escala)
        {
            p.EscalaMm = Numero("Comprimento aproximado da escala grafica no papel (mm; o total sai redondo)", p.EscalaMm);
            p.EscalaTexto = Numero("Altura do texto da escala grafica (mm)", p.EscalaTexto);
        }
        p.Salvar();
        ed.WriteMessage("
Padrao salvo: " + p.Resumo() + ". A posicao de norte e escala vem do [Padrao] (ajuste numa viewport e leia).");
    }

    /// <summary>
    /// Escala grafica (ex-ESCALAGRAF_V2.LSP, mesmo nome e mesmo fluxo): viewport, ate quantos
    /// metros, altura do texto e o canto inferior esquerdo da barra.
    /// </summary>
    [CommandMethod(C3DCommands.Layouts.EscalaGrafica, CommandFlags.Modal)]
    public void EscalaGrafica()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        if (Convert.ToInt32(AcApp.GetSystemVariable("TILEMODE")) == 1)
        {
            ed.WriteMessage("\nExecute o comando em um Layout (Paper Space).");
            return;
        }
        if (Convert.ToInt32(AcApp.GetSystemVariable("CVPORT")) != 1)
            ed.SwitchToPaperSpace();

        var peo = new PromptEntityOptions("\nSelecione a viewport: ");
        peo.SetRejectMessage("\nO objeto selecionado nao e uma viewport.");
        peo.AddAllowedClass(typeof(Viewport), true);
        PromptEntityResult per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
            return;

        var pmax = new PromptDoubleOptions("\nAte quantos metros deve ir a escala grafica")
        {
            DefaultValue = LayoutEscalaGrafica.UltimoMaximo, UseDefaultValue = true, AllowNegative = false, AllowZero = false
        };
        PromptDoubleResult rmax = ed.GetDouble(pmax);
        if (rmax.Status != PromptStatus.OK)
            return;
        var ptx = new PromptDoubleOptions("\nAltura do texto em mm")
        {
            DefaultValue = LayoutEscalaGrafica.UltimaAltura, UseDefaultValue = true, AllowNegative = false, AllowZero = false
        };
        PromptDoubleResult rtx = ed.GetDouble(ptx);
        if (rtx.Status != PromptStatus.OK)
            return;
        LayoutEscalaGrafica.UltimoMaximo = rmax.Value;
        LayoutEscalaGrafica.UltimaAltura = rtx.Value;

        PromptPointResult ppr = ed.GetPoint("\nClique no ponto inferior esquerdo da barra: ");
        if (ppr.Status != PromptStatus.OK)
            return;
        Point3d p = ppr.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        OpResult r = LayoutEscalaGrafica.Inserir(doc, per.ObjectId, rmax.Value, rtx.Value, p);
        ed.WriteMessage("\n" + r.Mensagem);
        PluginLog.Info("Layouts", "Escala grafica: " + r.Mensagem);
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

        OpResult r = LayoutNumbering.Numerar(doc, LayoutNumbering.FormatoGravado(doc.Database) ?? NumberingRule.Padrao(2));
        ed.WriteMessage("\n" + r.Mensagem);
        PluginLog.Info("Layouts", "NP: " + r.Mensagem);
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
