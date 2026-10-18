using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Resultado simples das operacoes em lote.</summary>
internal sealed record OpResult(int Ok, int Falhas, string Mensagem);

/// <summary>Regra de renomeacao em lote.</summary>
internal sealed record RenameRule(string Prefixo, string Base, string Sufixo, int Inicio, int Digitos, bool Numerar)
{
    /// <summary>Nome gerado para a posicao i (0-based) com o nome atual como base quando Base e vazia.</summary>
    public string Nome(int i, string atual)
    {
        string meio = Base.Length > 0 ? Base : atual;
        string numero = Numerar ? (Inicio + i).ToString().PadLeft(Math.Max(1, Digitos), '0') : "";
        return (Prefixo + meio + numero + Sufixo).Trim();
    }
}

/// <summary>
/// Operacoes do modulo Layouts. Todas travam o documento (as janelas sao modeless).
/// </summary>
internal static class LayoutOps
{
    public const string LayerNumero = "PRANCHA_NUMERO";
    public const string LayerTotal = "PRANCHA_TOTAL";
    public const string DispositivoPdf = "DWG To PDF.pc3";

    // ------------------------------------------------------------------
    // Layer state
    // ------------------------------------------------------------------

    /// <summary>Restaura o layer state como override de viewport (flag 4) em cada viewport.</summary>
    public static OpResult AplicarLayerState(Document doc, string layerState, IEnumerable<ObjectId> viewports)
    {
        int ok = 0, falhas = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        LayerStateManager lsm = db.LayerStateManager;
        LayerStateMasks mascara = lsm.GetLayerStateMask(layerState);

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports)
            {
                try
                {
                    lsm.RestoreLayerState(layerState, id, 4, mascara);
                    ok++;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    falhas++;
                }
            }
            tr.Commit();
        }

        return new OpResult(ok, falhas, $"\"{layerState}\" aplicado em {ok} viewport(s)" + (falhas > 0 ? $"; {falhas} falha(s)." : "."));
    }

    // ------------------------------------------------------------------
    // Layouts: renomear, reordenar, copiar, excluir, abrir
    // ------------------------------------------------------------------

    public static OpResult Renomear(Document doc, IReadOnlyList<string> nomes, RenameRule regra)
    {
        int ok = 0, falhas = 0;
        var erros = new List<string>();
        using DocumentLock _ = doc.LockDocument();
        LayoutManager lm = LayoutManager.Current;
        var existentes = new HashSet<string>(TodosOsNomes(doc.Database), StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < nomes.Count; i++)
        {
            string novo = regra.Nome(i, nomes[i]);
            if (novo.Length == 0 || novo.Equals(nomes[i], StringComparison.Ordinal))
                continue;
            if (novo.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' }) >= 0)
            {
                erros.Add($"{novo}: caracteres invalidos");
                falhas++;
                continue;
            }
            if (existentes.Contains(novo) && !novo.Equals(nomes[i], StringComparison.OrdinalIgnoreCase))
            {
                erros.Add($"{novo}: ja existe");
                falhas++;
                continue;
            }

            try
            {
                lm.RenameLayout(nomes[i], novo);
                existentes.Remove(nomes[i]);
                existentes.Add(novo);
                ok++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                erros.Add($"{nomes[i]}: {ex.Message}");
                falhas++;
            }
        }

        string msg = $"{ok} layout(s) renomeado(s)" + (falhas > 0 ? $"; {falhas} falha(s): {string.Join(", ", erros.Take(3))}" : ".");
        return new OpResult(ok, falhas, msg);
    }

    /// <summary>Move os layouts escolhidos uma posicao (delta -1 sobe, +1 desce) mantendo a ordem relativa.</summary>
    public static OpResult Mover(Document doc, IReadOnlyList<string> nomes, int delta)
    {
        using DocumentLock _ = doc.LockDocument();
        List<string> ordem = OrdemAtual(doc.Database);
        var marcados = new HashSet<string>(nomes, StringComparer.OrdinalIgnoreCase);

        if (delta < 0)
        {
            for (int i = 1; i < ordem.Count; i++)
                if (marcados.Contains(ordem[i]) && !marcados.Contains(ordem[i - 1]))
                    (ordem[i], ordem[i - 1]) = (ordem[i - 1], ordem[i]);
        }
        else
        {
            for (int i = ordem.Count - 2; i >= 0; i--)
                if (marcados.Contains(ordem[i]) && !marcados.Contains(ordem[i + 1]))
                    (ordem[i], ordem[i + 1]) = (ordem[i + 1], ordem[i]);
        }

        GravarOrdem(doc.Database, ordem);
        return new OpResult(nomes.Count, 0, $"{nomes.Count} layout(s) movido(s) {(delta < 0 ? "para a esquerda" : "para a direita")}.");
    }

    /// <summary>Ordena todos os layouts pelo nome (ordem natural: PR-2 antes de PR-10).</summary>
    public static OpResult OrdenarPorNome(Document doc)
    {
        using DocumentLock _ = doc.LockDocument();
        List<string> ordem = OrdemAtual(doc.Database).OrderBy(n => n, NaturalComparer.Instance).ToList();
        GravarOrdem(doc.Database, ordem);
        return new OpResult(ordem.Count, 0, "Layouts ordenados pelo nome.");
    }

    public static OpResult Copiar(Document doc, IReadOnlyList<string> nomes)
    {
        int ok = 0, falhas = 0;
        using DocumentLock _ = doc.LockDocument();
        LayoutManager lm = LayoutManager.Current;
        var existentes = new HashSet<string>(TodosOsNomes(doc.Database), StringComparer.OrdinalIgnoreCase);
        List<string> ordem = OrdemAtual(doc.Database);

        foreach (string nome in nomes)
        {
            string novo = NomeLivre(nome, existentes);
            try
            {
                lm.CopyLayout(nome, novo);
                existentes.Add(novo);
                // Fica logo depois do original.
                ordem.Remove(novo);
                ordem.Insert(Math.Min(ordem.Count, ordem.IndexOf(nome) + 1), novo);
                ok++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                falhas++;
            }
        }

        GravarOrdem(doc.Database, ordem);
        return new OpResult(ok, falhas, $"{ok} layout(s) copiado(s)" + (falhas > 0 ? $"; {falhas} falha(s)." : "."));
    }

    public static OpResult Excluir(Document doc, IReadOnlyList<string> nomes)
    {
        int ok = 0, falhas = 0;
        using DocumentLock _ = doc.LockDocument();
        LayoutManager lm = LayoutManager.Current;
        List<string> todos = TodosOsNomes(doc.Database);
        if (nomes.Count >= todos.Count)
            return new OpResult(0, nomes.Count, "O desenho precisa manter pelo menos um layout.");

        // Se o atual esta na lista, muda para outro antes.
        string atual = lm.CurrentLayout;
        if (nomes.Contains(atual, StringComparer.OrdinalIgnoreCase))
        {
            string? outro = todos.FirstOrDefault(n => !nomes.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (outro is not null)
                lm.CurrentLayout = outro;
        }

        foreach (string nome in nomes)
        {
            try
            {
                lm.DeleteLayout(nome);
                ok++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                falhas++;
            }
        }

        return new OpResult(ok, falhas, $"{ok} layout(s) excluido(s)" + (falhas > 0 ? $"; {falhas} falha(s)." : "."));
    }

    public static void Abrir(Document doc, string nome)
    {
        using DocumentLock _ = doc.LockDocument();
        LayoutManager.Current.CurrentLayout = nome;
    }

    // ------------------------------------------------------------------
    // Viewports em lote
    // ------------------------------------------------------------------

    public static OpResult Travar(Document doc, IEnumerable<ObjectId> viewports, bool travar) =>
        PorViewport(doc, viewports, vp => vp.Locked = travar, travar ? "travada(s)" : "destravada(s)");

    public static OpResult Ligar(Document doc, IEnumerable<ObjectId> viewports, bool ligar) =>
        PorViewport(doc, viewports, vp => vp.On = ligar, ligar ? "ligada(s)" : "desligada(s)");

    /// <summary>
    /// Aplica a escala da viewport no formato da propriedade "Custom scale" do AutoCAD
    /// (unidades de papel por unidade de desenho; 2 = 1:500 num desenho em metros).
    /// A escala de anotacao 1:N equivalente (N = fatorUnidade / custom) e aplicada se existir.
    /// </summary>
    public static OpResult Escalar(Document doc, IEnumerable<ObjectId> viewports, double custom, double fatorUnidade)
    {
        if (custom <= 0)
            return new OpResult(0, 0, "Escala invalida.");

        AnnotationScale? anot = EscalaAnotacao(doc.Database, fatorUnidade / custom);
        return PorViewport(doc, viewports, vp =>
        {
            bool travada = vp.Locked;
            vp.Locked = false;
            vp.StandardScale = StandardScaleType.CustomScale;
            vp.CustomScale = custom;
            if (anot is not null)
                vp.AnnotationScale = anot;
            vp.Locked = travada;
        }, $"com escala {EscalaTexto(custom, fatorUnidade)}");
    }

    /// <summary>Texto da escala como o AutoCAD mostra (custom) mais o 1:N equivalente.</summary>
    public static string EscalaTexto(double custom, double fatorUnidade)
    {
        if (custom <= 0)
            return "";
        string c = custom.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        double n = fatorUnidade / custom;
        string eq = n >= 1 ? $"1:{n:0.##}" : $"{1 / n:0.##}:1";
        return $"{c}  ({eq})";
    }

    /// <summary>
    /// Interpreta "2", "0,5", "1:500", "1/500", "4:1" ou "1000/100" -> custom scale.
    /// a:b = a unidades de papel (mm) para b unidades de desenho em mm, logo custom = a/b * fator.
    /// </summary>
    public static bool TentarEscala(string texto, double fatorUnidade, out double custom)
    {
        custom = 0;
        texto = (texto ?? "").Trim().Replace(',', '.');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        int sep = texto.IndexOfAny(new[] { ':', '/' });
        if (sep > 0)
        {
            if (!double.TryParse(texto[..sep].Trim(), ns, ci, out double a) || !double.TryParse(texto[(sep + 1)..].Trim(), ns, ci, out double b) || a <= 0 || b <= 0)
                return false;
            custom = a / b * fatorUnidade;
            return true;
        }
        if (!double.TryParse(texto, ns, ci, out custom) || custom <= 0)
            return false;
        return true;
    }

    private static OpResult PorViewport(Document doc, IEnumerable<ObjectId> viewports, Action<Viewport> acao, string feito)
    {
        int ok = 0, falhas = 0;
        using DocumentLock _ = doc.LockDocument();
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForWrite) is Viewport vp)
                    {
                        acao(vp);
                        ok++;
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    falhas++;
                }
            }
            tr.Commit();
        }
        return new OpResult(ok, falhas, $"{ok} viewport(s) {feito}" + (falhas > 0 ? $"; {falhas} falha(s)." : "."));
    }

    /// <summary>Escala de anotacao 1:N existente no desenho, ou null.</summary>
    private static AnnotationScale? EscalaAnotacao(Database db, double denominador)
    {
        try
        {
            ObjectContextCollection? col = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (col is null)
                return null;
            foreach (ObjectContext ctx in col)
            {
                if (ctx is AnnotationScale sc && Math.Abs(sc.DrawingUnits / sc.PaperUnits - denominador) < 1e-6)
                    return sc;
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        return null;
    }

    /// <summary>Escalas de anotacao do desenho (nome e denominador), para a lista da janela.</summary>
    public static List<(string Nome, double Denominador)> EscalasDoDesenho(Database db)
    {
        var lista = new List<(string, double)>();
        try
        {
            ObjectContextCollection? col = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (col is not null)
                foreach (ObjectContext ctx in col)
                    if (ctx is AnnotationScale sc && sc.PaperUnits > 0)
                        lista.Add((sc.Name, sc.DrawingUnits / sc.PaperUnits));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }
        return lista.OrderBy(e => e.Item2).ToList();
    }

    // ------------------------------------------------------------------
    // Numeracao de pranchas (ex-NP)
    // ------------------------------------------------------------------

    /// <summary>
    /// Escreve o numero (01, 02...) nos textos da layer PRANCHA_NUMERO e o total (/NN) nos
    /// da layer PRANCHA_TOTAL de cada layout, na ordem das abas. Inclui atributos de blocos.
    /// </summary>
    public static OpResult Numerar(Document doc)
    {
        int textos = 0, layoutsComTexto = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            GarantirLayer(tr, db, LayerNumero);
            GarantirLayer(tr, db, LayerTotal);

            List<string> ordem = OrdemAtual(db);
            string total = "/" + Formatar(ordem.Count);
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

            for (int i = 0; i < ordem.Count; i++)
            {
                if (!dic.Contains(ordem[i]))
                    continue;
                var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(ordem[i]), OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                int n = 0;
                string numero = Formatar(i + 1);

                foreach (ObjectId id in btr)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    switch (obj)
                    {
                        case DBText or MText:
                            n += Escrever(tr, (Entity)obj, numero, total);
                            break;
                        case BlockReference br:
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference att)
                                    n += Escrever(tr, att, numero, total);
                            break;
                    }
                }

                textos += n;
                if (n > 0)
                    layoutsComTexto++;
            }
            tr.Commit();
        }

        return new OpResult(textos, 0,
            textos > 0
                ? $"{textos} texto(s) atualizado(s) em {layoutsComTexto} layout(s)."
                : $"Nenhum texto nas layers {LayerNumero}/{LayerTotal}. Use NP > Configurar para escolher os textos.");
    }

    private static int Escrever(Transaction tr, Entity ent, string numero, string total)
    {
        string? valor = ent.Layer.Equals(LayerNumero, StringComparison.OrdinalIgnoreCase) ? numero
                      : ent.Layer.Equals(LayerTotal, StringComparison.OrdinalIgnoreCase) ? total : null;
        if (valor is null)
            return 0;

        ent.UpgradeOpen();
        switch (ent)
        {
            case AttributeReference a: a.TextString = valor; break;
            case DBText t: t.TextString = valor; break;
            case MText m: m.Contents = valor; break;
            default: return 0;
        }
        return 1;
    }

    /// <summary>Poe o texto escolhido na layer do numero ou do total (NP > Configurar).</summary>
    public static void AssociarTexto(Document doc, ObjectId id, bool numero)
    {
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        string layer = numero ? LayerNumero : LayerTotal;
        GarantirLayer(tr, db, layer);
        if (tr.GetObject(id, OpenMode.ForWrite) is Entity ent)
            ent.Layer = layer;
        tr.Commit();
    }

    private static string Formatar(int n) => n < 10 ? "0" + n : n.ToString();

    private static void GarantirLayer(Transaction tr, Database db, string nome)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(nome))
            return;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = nome };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }

    // ------------------------------------------------------------------
    // Plotagem em PDF
    // ------------------------------------------------------------------

    /// <summary>
    /// Plota os layouts (na ordem dada) em PDF com a configuracao de pagina de cada um,
    /// trocando so o dispositivo para DWG To PDF. Um arquivo por layout ou um unico PDF.
    /// Precisa rodar no contexto de um comando (ver C3DLAYOUTPLOT).
    /// </summary>
    public static OpResult PlotarPdf(Document doc, IReadOnlyList<string> nomes, string pasta, bool unico, string? nomeUnico)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            return new OpResult(0, nomes.Count, "Ja existe uma plotagem em andamento. Aguarde e tente de novo.");

        Directory.CreateDirectory(pasta);
        string baseNome = Path.GetFileNameWithoutExtension(doc.Name);
        object bg = AcApp.GetSystemVariable("BACKGROUNDPLOT");
        int ok = 0, falhas = 0;
        var erros = new List<string>();

        using DocumentLock _ = doc.LockDocument();
        try
        {
            AcApp.SetSystemVariable("BACKGROUNDPLOT", 0);
            Database db = doc.Database;
            using Transaction tr = db.TransactionManager.StartTransaction();
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

            var infos = new List<(string Nome, PlotInfo Info)>();
            foreach (string nome in nomes)
            {
                if (!dic.Contains(nome))
                    continue;
                try
                {
                    var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(nome), OpenMode.ForRead);
                    infos.Add((nome, PrepararPlot(layout)));
                }
                catch (System.Exception ex)
                {
                    erros.Add($"{nome}: {ex.Message}");
                    falhas++;
                }
            }

            if (infos.Count == 0)
                return new OpResult(0, falhas, "Nenhum layout pronto para plotar. " + string.Join("; ", erros.Take(3)));

            using PlotEngine pe = PlotFactory.CreatePublishEngine();
            using var progresso = new PlotProgressDialog(false, infos.Count, true);
            progresso.set_PlotMsgString(PlotMessageIndex.DialogTitle, "Civil3D2026Plugin - PDF dos layouts");
            progresso.set_PlotMsgString(PlotMessageIndex.CancelJobButtonMessage, "Cancelar");
            progresso.set_PlotMsgString(PlotMessageIndex.CancelSheetButtonMessage, "Pular folha");
            progresso.set_PlotMsgString(PlotMessageIndex.SheetSetProgressCaption, "Folhas");
            progresso.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, "Folha");
            progresso.LowerPlotProgressRange = 0;
            progresso.UpperPlotProgressRange = 100;
            progresso.PlotProgressPos = 0;
            progresso.OnBeginPlot();
            progresso.IsVisible = true;

            pe.BeginPlot(progresso, null);

            if (unico)
            {
                string arquivo = Path.Combine(pasta, Limpar(nomeUnico ?? baseNome) + ".pdf");
                Apagar(arquivo);
                pe.BeginDocument(infos[0].Info, baseNome, null, 1, true, arquivo);
                for (int i = 0; i < infos.Count; i++)
                {
                    if (progresso.IsPlotCancelled)
                        break;
                    Pagina(pe, progresso, infos[i], i == infos.Count - 1);
                    ok++;
                }
                pe.EndDocument(null);
            }
            else
            {
                for (int i = 0; i < infos.Count; i++)
                {
                    if (progresso.IsPlotCancelled)
                        break;
                    string arquivo = Path.Combine(pasta, $"{Limpar(baseNome)}-{Limpar(infos[i].Nome)}.pdf");
                    Apagar(arquivo);
                    pe.BeginDocument(infos[i].Info, baseNome, null, 1, true, arquivo);
                    Pagina(pe, progresso, infos[i], true);
                    pe.EndDocument(null);
                    ok++;
                }
            }

            progresso.PlotProgressPos = 100;
            progresso.OnEndPlot();
            pe.EndPlot(null);
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            return new OpResult(ok, falhas + 1, $"Plotagem interrompida apos {ok} folha(s): {ex.Message}");
        }
        finally
        {
            AcApp.SetSystemVariable("BACKGROUNDPLOT", bg);
        }

        string msg = unico
            ? $"PDF com {ok} folha(s) gravado em {pasta}."
            : $"{ok} PDF(s) gravado(s) em {pasta}.";
        if (falhas > 0)
            msg += $" {falhas} falha(s): {string.Join("; ", erros.Take(3))}";
        return new OpResult(ok, falhas, msg);
    }

    private static void Pagina(PlotEngine pe, PlotProgressDialog progresso, (string Nome, PlotInfo Info) folha, bool ultima)
    {
        progresso.set_PlotMsgString(PlotMessageIndex.SheetName, folha.Nome);
        progresso.OnBeginSheet();
        progresso.LowerSheetProgressRange = 0;
        progresso.UpperSheetProgressRange = 100;
        progresso.SheetProgressPos = 0;

        using var ppi = new PlotPageInfo();
        pe.BeginPage(ppi, folha.Info, ultima, null);
        pe.BeginGenerateGraphics(null);
        pe.EndGenerateGraphics(null);
        pe.EndPage(null);

        progresso.SheetProgressPos = 100;
        progresso.OnEndSheet();
    }

    /// <summary>PlotInfo com a configuracao de pagina do layout e o dispositivo DWG To PDF.</summary>
    private static PlotInfo PrepararPlot(Autodesk.AutoCAD.DatabaseServices.Layout layout)
    {
        var ps = new PlotSettings(layout.ModelType);
        ps.CopyFrom(layout);

        PlotSettingsValidator psv = PlotSettingsValidator.Current;
        string media = layout.CanonicalMediaName;
        try
        {
            psv.SetPlotConfigurationName(ps, DispositivoPdf, media.Length > 0 ? media : null);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Papel do layout nao existe no DWG To PDF: deixa o validador escolher o mais parecido.
            psv.SetPlotConfigurationName(ps, DispositivoPdf, null);
        }

        var pi = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = ps };
        var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
        piv.Validate(pi);
        return pi;
    }

    private static void Apagar(string arquivo)
    {
        try
        {
            if (File.Exists(arquivo))
                File.Delete(arquivo);
        }
        catch (IOException)
        {
        }
    }

    private static string Limpar(string nome)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            nome = nome.Replace(c, '_');
        return nome.Trim();
    }

    // ------------------------------------------------------------------
    // Ordem das abas
    // ------------------------------------------------------------------

    public static List<string> TodosOsNomes(Database db) => OrdemAtual(db);

    /// <summary>Layouts (sem o Model) na ordem das abas.</summary>
    public static List<string> OrdemAtual(Database db)
    {
        var lista = new List<(int Ordem, string Nome)>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dic)
            {
                var l = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (!l.ModelType)
                    lista.Add((l.TabOrder, l.LayoutName));
            }
            tr.Commit();
        }
        return lista.OrderBy(e => e.Ordem).Select(e => e.Nome).ToList();
    }

    internal static void GravarOrdem(Database db, List<string> ordem)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        for (int i = 0; i < ordem.Count; i++)
        {
            if (!dic.Contains(ordem[i]))
                continue;
            var l = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(ordem[i]), OpenMode.ForWrite);
            if (l.TabOrder != i + 1)
                l.TabOrder = i + 1;
        }
        tr.Commit();
    }

    private static string NomeLivre(string nome, HashSet<string> existentes)
    {
        string candidato = nome + " (2)";
        for (int k = 2; existentes.Contains(candidato); k++)
            candidato = $"{nome} ({k})";
        return candidato;
    }

    /// <summary>Ordem natural: "PR-2" antes de "PR-10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            a ??= "";
            b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int i0 = i, j0 = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    int c = long.Parse(a[i0..i]).CompareTo(long.Parse(b[j0..j]));
                    if (c != 0)
                        return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0)
                        return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
