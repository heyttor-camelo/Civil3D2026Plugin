using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Resultado simples das operacoes em lote.</summary>
internal sealed record OpResult(int Ok, int Falhas, string Mensagem)
{
    /// <summary>A operacao alterou o desenho (false = estava tudo certo; a atualizacao automatica fica quieta).</summary>
    public bool Mudou { get; init; } = true;
}

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
        var aplicadas = new List<ObjectId>();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports)
            {
                try
                {
                    lsm.RestoreLayerState(layerState, id, 4, mascara);
                    aplicadas.Add(id);
                    ok++;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    falhas++;
                }
            }
            tr.Commit();
        }

        RegistrarLayerState(db, aplicadas, layerState);
        return new OpResult(ok, falhas, $"\"{layerState}\" aplicado em {ok} viewport(s)" + (falhas > 0 ? $"; {falhas} falha(s)." : "."));
    }

    /// <summary>XData da viewport com o ultimo layer state aplicado pelo plugin (nome e data).</summary>
    public const string AppLayerState = "C3D_LAYERSTATE";

    /// <summary>Grava na viewport o layer state aplicado (para as propriedades mostrarem depois).</summary>
    public static void RegistrarLayerState(Database db, IEnumerable<ObjectId> viewports, string texto)
    {
        List<ObjectId> ids = viewports.Where(id => !id.IsNull && !id.IsErased).ToList();
        if (ids.Count == 0)
            return;
        if (texto.Length > 250)
            texto = texto.Substring(0, 250);
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(AppLayerState))
            {
                rat.UpgradeOpen();
                var r = new RegAppTableRecord { Name = AppLayerState };
                rat.Add(r);
                tr.AddNewlyCreatedDBObject(r, true);
            }
            string quando = DateTime.Now.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            foreach (ObjectId id in ids)
            {
                // Mesmo em layer travada (comum na layer das viewports).
                if (tr.GetObject(id, OpenMode.ForWrite, false, true) is Viewport vp)
                    vp.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppLayerState),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, texto),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, quando));
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("Layouts", $"Registro do layer state nas viewports: {ex.Message}");
        }
    }

    /// <summary>Layer state registrado na viewport ("nome  (data)"), ou null.</summary>
    public static string? LayerStateRegistrado(Viewport vp)
    {
        using ResultBuffer? rb = vp.GetXDataForApplication(AppLayerState);
        if (rb is null)
            return null;
        TypedValue[] v = rb.AsArray();
        string nome = v.Length > 1 ? v[1].Value as string ?? "" : "";
        string quando = v.Length > 2 ? v[2].Value as string ?? "" : "";
        return nome.Length == 0 ? null : quando.Length > 0 ? $"{nome}  ({quando})" : nome;
    }

    /// <summary>So o nome do layer state registrado na viewport (sem a data), ou null.</summary>
    public static string? NomeRegistrado(Transaction tr, ObjectId vpId)
    {
        if (vpId.IsNull || vpId.IsErased || tr.GetObject(vpId, OpenMode.ForRead) is not Viewport vp)
            return null;
        using ResultBuffer? rb = vp.GetXDataForApplication(AppLayerState);
        TypedValue[] v = rb?.AsArray() ?? Array.Empty<TypedValue>();
        string nome = v.Length > 1 ? v[1].Value as string ?? "" : "";
        return nome.Length == 0 ? null : nome;
    }

    /// <summary>
    /// Texto gravado ao colar/importar: o nome do layer state original (o registrado na viewport de
    /// origem) e de onde veio, ex. "WALDYR (copiado de 04 #1)"; sem registro, "copia de 04 #1".
    /// </summary>
    public static string TextoHerdado(string? original, string origem)
    {
        if (string.IsNullOrEmpty(original))
            return "copia de " + origem;
        int i = original.IndexOf(" (copiado de ", StringComparison.Ordinal);
        string nome = i > 0 ? original.Substring(0, i) : original;
        return nome.StartsWith("copia de ", StringComparison.Ordinal) ? "copia de " + origem : $"{nome} (copiado de {origem})";
    }

    /// <summary>Quantas layers estao congeladas nesta viewport (VP freeze) e quantas tem override nela.</summary>
    public static (int Congeladas, int ComOverride) ResumoCamadas(Database db, ObjectId vpId)
    {
        int congeladas = 0, overrides = 0;
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(vpId, OpenMode.ForRead) is Viewport vp)
        {
            congeladas = vp.GetFrozenLayers()?.Count ?? 0;
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                try
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!ltr.HasOverrides)
                        continue;
                    LayerViewportProperties o = ltr.GetViewportOverrides(vpId);
                    if (o.IsColorOverridden || o.IsLinetypeOverridden || o.IsLineWeightOverridden ||
                        o.IsPlotStyleOverridden || o.IsTransparencyOverridden)
                        overrides++;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                }
            }
        }
        tr.Commit();
        return (congeladas, overrides);
    }

    // ------------------------------------------------------------------
    // Layouts: renomear, reordenar, copiar, excluir, abrir
    // ------------------------------------------------------------------

    /// <summary>Renomeacoes feitas na ultima chamada de <see cref="Renomear"/> (antigo -> novo).</summary>
    public static Dictionary<string, string> UltimasRenomeacoes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static OpResult Renomear(Document doc, IReadOnlyList<string> nomesMarcados, RenameRule regra)
    {
        int falhas = 0;
        UltimasRenomeacoes.Clear();
        // Numera na ordem das abas, qualquer que seja a ordenacao da lista do painel.
        var marcadosSet = new HashSet<string>(nomesMarcados, StringComparer.OrdinalIgnoreCase);
        List<string> nomes = OrdemAtual(doc.Database).Where(marcadosSet.Contains).ToList();
        nomes.AddRange(nomesMarcados.Where(n => !nomes.Contains(n, StringComparer.OrdinalIgnoreCase)));
        var erros = new List<string>();
        using DocumentLock _ = doc.LockDocument();
        LayoutManager lm = LayoutManager.Current;
        var existentes = new HashSet<string>(TodosOsNomes(doc.Database), StringComparer.OrdinalIgnoreCase);
        var antigos = new HashSet<string>(nomes, StringComparer.OrdinalIgnoreCase);
        char[] invalidos = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

        // 1) Plano completo antes de mexer: valida tudo e acha conflitos dentro do proprio lote.
        var plano = new List<(string Antigo, string Novo)>();
        var novosNoLote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < nomes.Count; i++)
        {
            string antigo = nomes[i];
            if (antigo.Equals("Model", StringComparison.OrdinalIgnoreCase))
                continue;
            string novo = regra.Nome(i, antigo);
            if (novo.Length == 0 || novo.Equals(antigo, StringComparison.Ordinal))
            {
                novosNoLote.Add(antigo); // fica com o nome; ninguem do lote pode pega-lo
                continue;
            }
            string? erro = novo.IndexOfAny(invalidos) >= 0 ? "caracteres invalidos"
                : novo.Length > 255 ? "nome com mais de 255 caracteres"
                : novo.Equals("Model", StringComparison.OrdinalIgnoreCase) ? "nome reservado"
                : !novosNoLote.Add(novo) ? "repetido no lote"
                // ja existe fora do lote (um layout do lote vai liberar o nome dele, entao nao conta)
                : existentes.Contains(novo) && !antigos.Contains(novo) ? "ja existe"
                : null;
            if (erro is not null)
            {
                erros.Add($"{antigo} -> {novo}: {erro}");
                falhas++;
                novosNoLote.Add(antigo);
                continue;
            }

            plano.Add((antigo, novo));
        }

        // Um layout do lote que falhou continua com o nome antigo: quem queria esse nome nao pode.
        var presos = new HashSet<string>(nomes.Where(n => !plano.Any(p => p.Antigo.Equals(n, StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);
        foreach ((string a, string n) in plano.ToList())
        {
            if (presos.Contains(n) && !a.Equals(n, StringComparison.OrdinalIgnoreCase))
            {
                erros.Add($"{a} -> {n}: o layout {n} nao foi renomeado");
                falhas++;
                plano.Remove((a, n));
            }
        }

        // 2) Duas passadas: todos para um nome temporario e depois para o final. Assim trocas e
        // deslocamentos (PR-02 -> PR-01 com PR-01 -> PR-00) e mudancas so de maiusculas funcionam.
        string marca = "~ren" + Guid.NewGuid().ToString("N").Substring(0, 6) + "_";
        var temporarios = new List<(string Antigo, string Temp, string Novo)>();
        for (int i = 0; i < plano.Count; i++)
        {
            string temp = marca + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                lm.RenameLayout(plano[i].Antigo, temp);
                temporarios.Add((plano[i].Antigo, temp, plano[i].Novo));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                erros.Add($"{plano[i].Antigo}: {ex.Message}");
                falhas++;
            }
        }

        int ok = 0;
        foreach ((string antigo, string temp, string novo) in temporarios)
        {
            try
            {
                lm.RenameLayout(temp, novo);
                UltimasRenomeacoes[antigo] = novo;
                ok++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                erros.Add($"{antigo} -> {novo}: {ex.Message}");
                falhas++;
                try
                {
                    lm.RenameLayout(temp, antigo); // volta ao nome de antes
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex2)
                {
                    PluginLog.Aviso("Layouts", $"Renomear: {temp} nao voltou para {antigo}: {ex2.Message}");
                }
            }
        }

        if (erros.Count > 0)
            PluginLog.Aviso("Layouts", "Renomear: " + string.Join("; ", erros));
        AtualizarAbas();
        string msg = $"{ok} layout(s) renomeado(s)" + (falhas > 0 ? $"; {falhas} falha(s): {string.Join(", ", erros.Take(3))}" + (erros.Count > 3 ? " (lista completa no log: Ctrl+Shift+L)" : "") : ".");
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
        List<string> atual = OrdemAtual(doc.Database);
        List<string> ordem = atual.OrderBy(n => n, NaturalComparer.Instance).ToList();
        if (atual.SequenceEqual(ordem, StringComparer.OrdinalIgnoreCase))
        {
            // O banco ja esta em ordem; se as abas da tela ficaram velhas, o redesenho resolve.
            AtualizarAbas();
            PluginLog.Diag("Layouts", "Ordenar pelo nome: o banco ja estava em ordem; abas redesenhadas.");
            return new OpResult(ordem.Count, 0, $"Os {ordem.Count} layouts ja estao em ordem de nome (abas redesenhadas).");
        }
        bool ok = GravarOrdem(doc.Database, ordem);
        return ok
            ? new OpResult(ordem.Count, 0, $"{ordem.Count} layouts ordenados pelo nome ({ordem.First()} a {ordem.Last()}).")
            : new OpResult(0, 1, "A ordem das abas nao mudou (detalhes no log: Ctrl+Shift+L).");
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
            try
            {
                vp.StandardScale = StandardScaleType.CustomScale;
                vp.CustomScale = custom;
                if (anot is not null)
                    vp.AnnotationScale = anot;
            }
            finally
            {
                vp.Locked = travada;
            }
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
        var erros = new List<string>();
        using DocumentLock _ = doc.LockDocument();
        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in viewports)
            {
                try
                {
                    // Uma transacao por viewport: se uma propriedade for recusada, a viewport volta
                    // inteira como estava. Abre mesmo em layer travada (comum nas pranchas).
                    using Transaction t1 = doc.Database.TransactionManager.StartTransaction();
                    if (t1.GetObject(id, OpenMode.ForWrite, false, true) is Viewport vp)
                    {
                        acao(vp);
                        t1.Commit();
                        ok++;
                    }
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    erros.Add(ex.Message);
                    PluginLog.Aviso("Layouts", $"Viewport {id.Handle}: nao {feito} ({ex.Message})");
                }
            }
            tr.Commit();
        }
        return new OpResult(ok, falhas, $"{ok} viewport(s) {feito}" + (falhas > 0 ? $"; {falhas} falha(s): {string.Join(", ", erros.Distinct().Take(2))}." : "."));
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

        switch (tr.GetObject(ent.ObjectId, OpenMode.ForWrite, false, true))
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
        if (tr.GetObject(id, OpenMode.ForWrite, false, true) is Entity ent)
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
        string leve = LayoutViewportPower.AvisoPlot(doc.Database);
        if (leve.Length > 0)
            return new OpResult(0, nomes.Count, leve);

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

    /// <summary>
    /// Grava a ordem das abas e confere. Primeiro pelo ActiveX (TabOrder do COM, que redesenha as
    /// abas na hora, como as LISPs de ordenar); sem COM, direto no banco. Retorna se a ordem ficou
    /// a pedida; o que aconteceu vai para o log.
    /// </summary>
    internal static bool GravarOrdem(Database db, List<string> ordem)
    {
        List<string> antes = OrdemAtual(db);
        List<string> pedida = ordem.Where(n => antes.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        pedida.AddRange(antes.Where(n => !pedida.Contains(n, StringComparer.OrdinalIgnoreCase)));
        if (antes.SequenceEqual(pedida, StringComparer.OrdinalIgnoreCase))
            return true;

        string como = "COM";
        try
        {
            if (!GravarOrdemCom(db, pedida))
                como = "";
        }
        catch (System.Exception ex)
        {
            como = "";
            PluginLog.Diag("Layouts", "Ordem das abas pelo COM falhou: " + (ex.InnerException?.Message ?? ex.Message));
        }
        List<string> depois = OrdemAtual(db);
        if (como.Length == 0 || !depois.SequenceEqual(pedida, StringComparer.OrdinalIgnoreCase))
        {
            como = como.Length == 0 ? "banco" : "COM + banco";
            GravarOrdemBanco(db, pedida);
            depois = OrdemAtual(db);
        }
        AtualizarAbas();

        bool ok = depois.SequenceEqual(pedida, StringComparer.OrdinalIgnoreCase);
        string Lista(List<string> l) => string.Join(", ", l.Take(12)) + (l.Count > 12 ? $", ... ({l.Count})" : "");
        if (ok)
            PluginLog.Diag("Layouts", $"Ordem das abas gravada ({como}): {Lista(depois)}");
        else
            PluginLog.Aviso("Layouts", $"Ordem das abas nao ficou a pedida ({como}). Pedida: {Lista(pedida)}. Ficou: {Lista(depois)}.");
        return ok;
    }

    private static void GravarOrdemBanco(Database db, List<string> ordem)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        for (int i = 0; i < ordem.Count; i++)
        {
            if (!dic.Contains(ordem[i]))
                continue;
            var l = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(ordem[i]), OpenMode.ForRead);
            if (l.TabOrder == i + 1)
                continue;
            l.UpgradeOpen();
            l.TabOrder = i + 1;
        }
        tr.Commit();
    }

    /// <summary>TabOrder pelo ActiveX (AcadDocument.Layouts.Item(nome).TabOrder).</summary>
    private static bool GravarOrdemCom(Database db, List<string> ordem)
    {
        Document? doc = null;
        foreach (Document d in AcApp.DocumentManager)
            if (d.Database == db)
                doc = d;
        object? acad = doc is null ? null : AcadDocument(doc);
        if (acad is null || Com(acad, "Layouts", BindingFlags.GetProperty) is not object layouts)
            return false;
        for (int i = 0; i < ordem.Count; i++)
        {
            if (Com(layouts, "Item", BindingFlags.InvokeMethod, ordem[i]) is not object lay)
                return false;
            if (Convert.ToInt32(Com(lay, "TabOrder", BindingFlags.GetProperty), System.Globalization.CultureInfo.InvariantCulture) != i + 1)
                Com(lay, "TabOrder", BindingFlags.SetProperty, i + 1);
        }
        return true;
    }

    private static object? Com(object alvo, string membro, BindingFlags tipo, params object[] args) =>
        alvo.GetType().InvokeMember(membro, tipo, null, alvo, args, System.Globalization.CultureInfo.InvariantCulture);

    private static MethodInfo? _getAcadDocument;
    private static bool _procurouGetAcadDocument;

    /// <summary>Documento ActiveX (DocumentExtension.GetAcadDocument; antes, a propriedade AcadDocument).</summary>
    private static object? AcadDocument(Document doc)
    {
        try
        {
            if (!_procurouGetAcadDocument)
            {
                _procurouGetAcadDocument = true;
                const string nome = "Autodesk.AutoCAD.ApplicationServices.DocumentExtension";
                Type? t = typeof(Document).Assembly.GetType(nome, false)
                          ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(nome, false)).FirstOrDefault(x => x is not null);
                _getAcadDocument = t?.GetMethod("GetAcadDocument", new[] { typeof(Document) });
            }
            if (_getAcadDocument is not null)
                return _getAcadDocument.Invoke(null, new object[] { doc });
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag("Layouts", "GetAcadDocument: " + (ex.InnerException?.Message ?? ex.Message));
        }
        try
        {
            return doc.GetType().GetProperty("AcadDocument")?.GetValue(doc);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>Redesenha as abas de layout (quando a API expoe) e a tela.</summary>
    internal static void AtualizarAbas()
    {
        try
        {
            LayoutManager lm = LayoutManager.Current;
            lm.GetType().GetMethod("UpdateLayoutTabs", Type.EmptyTypes)?.Invoke(lm, null);
        }
        catch (System.Exception)
        {
        }
        try
        {
            AcApp.UpdateScreen();
        }
        catch (System.Exception)
        {
        }
    }

    private static string NomeLivre(string nome, HashSet<string> existentes)
    {
        string candidato = nome + " (2)";
        for (int k = 2; existentes.Contains(candidato); k++)
            candidato = $"{nome} ({k})";
        return candidato;
    }

    /// <summary>Ordem natural: "PR-2" antes de "PR-10".</summary>
    internal sealed class NaturalComparer : IComparer<string>
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
                    // Compara pelo valor sem converter (numeros longos nao estouram).
                    string na = a[i0..i].TrimStart('0'), nb = b[j0..j].TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
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
