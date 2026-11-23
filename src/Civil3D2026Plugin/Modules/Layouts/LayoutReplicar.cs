using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Replicar objetos nas pranchas (C3DREPLICAR): copia os objetos selecionados no layout atual para
/// os layouts escolhidos, na mesma posicao do papel ou deslocados (ponto base -> ponto de destino).
/// Cada copia recebe um XData com a chave do grupo de origem; assim "Substituir copias anteriores"
/// troca as copias feitas antes com os mesmos objetos, e Remover apaga so o que o plugin inseriu.
/// </summary>
internal static class LayoutReplicar
{
    private const string Mod = "Layouts";
    public const string RegApp = "C3DREPLICAR";

    /// <summary>Layouts marcados no explorador, para a janela ja vir com eles marcados.</summary>
    public static Func<List<string>>? MarcadosNoPainel { get; set; }

    /// <summary>Um layout na janela de escolha.</summary>
    internal sealed record Alvo(string Nome, int Ordem, bool JaTemCopia, bool EhOrigem);

    /// <summary>
    /// Chave do grupo: hash dos handles dos objetos de origem. Se um objeto selecionado for ele mesmo
    /// uma copia do plugin, a chave e a do grupo original (replicar a partir de uma copia continua
    /// o mesmo grupo).
    /// </summary>
    public static string Chave(Database db, IEnumerable<ObjectId> ids)
    {
        var handles = new SortedSet<string>(StringComparer.Ordinal);
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                    continue;
                string? origem = ChaveGravada(e);
                handles.Add(origem is null ? e.Handle.ToString() : "G:" + origem);
            }
            tr.Commit();
        }
        if (handles.Count == 1 && handles.First().StartsWith("G:", StringComparison.Ordinal))
            return handles.First().Substring(2);
        byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("|", handles)));
        return Convert.ToHexString(h, 0, 10);
    }

    /// <summary>Chave gravada no XData de uma copia do plugin (null = objeto comum).</summary>
    public static string? ChaveGravada(Entity e)
    {
        using ResultBuffer? rb = e.GetXDataForApplication(RegApp);
        if (rb is null)
            return null;
        foreach (TypedValue tv in rb)
        {
            if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString && tv.Value is string s && s.Length > 0)
                return s;
        }
        return null;
    }

    /// <summary>Layouts do desenho na ordem das abas, com a informacao de ja terem copia do grupo.</summary>
    public static List<Alvo> Alvos(Database db, string chave, string? layoutOrigem)
    {
        var lista = new List<Alvo>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach ((AcadDb.Layout layout, BlockTableRecord btr) in Layouts(tr, db))
            {
                bool tem = false;
                foreach (ObjectId id in btr)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity e && ChaveGravada(e) == chave)
                    {
                        tem = true;
                        break;
                    }
                }
                lista.Add(new Alvo(layout.LayoutName, layout.TabOrder, tem,
                    layoutOrigem is not null && layout.LayoutName.Equals(layoutOrigem, StringComparison.OrdinalIgnoreCase)));
            }
            tr.Commit();
        }
        return lista.OrderBy(a => a.Ordem).ToList();
    }

    /// <summary>
    /// Copia os objetos para cada layout, aplica o deslocamento e grava o XData. Com
    /// <paramref name="substituir"/>, as copias anteriores do mesmo grupo nesses layouts sao apagadas.
    /// </summary>
    public static OpResult Replicar(Document doc, IList<ObjectId> objetos, IList<string> layouts, Vector3d deslocamento, bool substituir)
    {
        Database db = doc.Database;
        if (objetos.Count == 0)
            return new OpResult(0, 0, "Nenhum objeto para replicar.") { Mudou = false };
        if (layouts.Count == 0)
            return new OpResult(0, 0, "Nenhum layout escolhido.") { Mudou = false };

        string chave = Chave(db, objetos);
        var alvo = new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);
        int ok = 0, falhas = 0, copiados = 0, apagados = 0;
        var erros = new List<string>();

        using (doc.LockDocument())
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            GarantirRegApp(tr, db);
            var ids = new ObjectIdCollection(objetos.ToArray());

            foreach ((AcadDb.Layout layout, BlockTableRecord btr) in Layouts(tr, db))
            {
                if (!alvo.Contains(layout.LayoutName))
                    continue;
                try
                {
                    if (substituir)
                        apagados += ApagarCopias(tr, btr, chave, objetos);

                    var map = new IdMapping();
                    db.DeepCloneObjects(ids, btr.ObjectId, map, false);
                    var mat = Matrix3d.Displacement(deslocamento);
                    foreach (IdPair par in map)
                    {
                        if (!par.IsPrimary || !par.IsCloned)
                            continue;
                        if (tr.GetObject(par.Value, OpenMode.ForWrite) is not Entity e)
                            continue;
                        if (!deslocamento.IsZeroLength())
                            e.TransformBy(mat);
                        e.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, chave));
                        copiados++;
                    }
                    ok++;
                }
                catch (System.Exception ex)
                {
                    falhas++;
                    erros.Add($"{layout.LayoutName}: {ex.Message}");
                    PluginLog.Erro(Mod, $"Replicar em {layout.LayoutName}: {ex.Message}", ex);
                }
            }
            tr.Commit();
        }

        var sb = new StringBuilder();
        sb.Append($"Replicado em {ok} de {layouts.Count} layouts: {copiados} objetos copiados");
        if (apagados > 0)
            sb.Append($", {apagados} copias anteriores substituidas");
        if (!deslocamento.IsZeroLength())
            sb.Append($", deslocamento {deslocamento.X.ToString("0.##", CultureInfo.InvariantCulture)}, {deslocamento.Y.ToString("0.##", CultureInfo.InvariantCulture)} mm");
        sb.Append('.');
        if (erros.Count > 0)
            sb.Append(" Falhas: " + string.Join("; ", erros.Take(5)));
        return new OpResult(ok, falhas, sb.ToString());
    }

    /// <summary>Apaga, nos layouts escolhidos, as copias do grupo dos objetos selecionados.</summary>
    public static OpResult Remover(Document doc, IList<ObjectId> objetos, IList<string> layouts)
    {
        Database db = doc.Database;
        string chave = Chave(db, objetos);
        var alvo = new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);
        int ok = 0, apagados = 0;
        using (doc.LockDocument())
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach ((AcadDb.Layout layout, BlockTableRecord btr) in Layouts(tr, db))
            {
                if (!alvo.Contains(layout.LayoutName))
                    continue;
                int n = ApagarCopias(tr, btr, chave, objetos);
                apagados += n;
                if (n > 0)
                    ok++;
            }
            tr.Commit();
        }
        return new OpResult(ok, 0, $"Removidas {apagados} copias em {ok} layouts. Os objetos selecionados ficaram.") { Mudou = apagados > 0 };
    }

    private static int ApagarCopias(Transaction tr, BlockTableRecord btr, string chave, IList<ObjectId> preservar)
    {
        int n = 0;
        var manter = new HashSet<ObjectId>(preservar);
        foreach (ObjectId id in btr)
        {
            if (manter.Contains(id))
                continue;
            if (tr.GetObject(id, OpenMode.ForRead) is Entity e && ChaveGravada(e) == chave)
            {
                e.UpgradeOpen();
                e.Erase();
                n++;
            }
        }
        return n;
    }

    private static void GarantirRegApp(Transaction tr, Database db)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(RegApp))
            return;
        rat.UpgradeOpen();
        var r = new RegAppTableRecord { Name = RegApp };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }

    private static IEnumerable<(AcadDb.Layout Layout, BlockTableRecord Btr)> Layouts(Transaction tr, Database db)
    {
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var layout = (AcadDb.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (layout.ModelType)
                continue;
            yield return (layout, (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead));
        }
    }

    // ------------------------------------------------------------------
    // Janela de escolha das pranchas
    // ------------------------------------------------------------------

    /// <summary>Escolha feita na janela.</summary>
    internal sealed record Escolha(List<string> Layouts, bool Substituir);

    /// <summary>
    /// Janela com a lista de layouts para marcar. <paramref name="remover"/> muda os textos para o
    /// modo Remover (so layouts que ja tem copia podem ser marcados).
    /// </summary>
    public static Escolha? Escolher(List<Alvo> alvos, int objetos, string posicao, bool remover)
    {
        ToolTheme t = ToolTheme.Current;
        var w = new SW.Window
        {
            Title = remover ? "Remover copias das pranchas" : "Replicar nas pranchas",
            Width = 460,
            Height = 560,
            MinWidth = 380,
            MinHeight = 360,
            ShowInTaskbar = false,
            Background = t.Back,
            Foreground = t.Text,
            WindowStartupLocation = SW.WindowStartupLocation.CenterScreen
        };
        ToolStyles.Aplicar(w.Resources, t);

        var raiz = new WC.DockPanel { Margin = new SW.Thickness(12), LastChildFill = true };

        string resumo = remover
            ? $"Objetos selecionados: {objetos}. As copias deles feitas pelo plugin sao apagadas nas pranchas marcadas; os objetos selecionados ficam."
            : $"Objetos selecionados: {objetos}. Posicao: {posicao}.";
        var cab = new WC.TextBlock { Text = resumo, TextWrapping = SW.TextWrapping.Wrap, Foreground = t.Text, Margin = new SW.Thickness(0, 0, 0, 8) };
        WC.DockPanel.SetDock(cab, WC.Dock.Top);
        raiz.Children.Add(cab);

        var barra = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 0, 0, 6) };
        var bTodos = new WC.Button { Content = "Marcar todas", Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = "Marca todas as pranchas da lista." };
        var bNenhum = new WC.Button { Content = "Desmarcar", Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = "Desmarca todas." };
        var bPainel = new WC.Button { Content = "Marcadas no explorador", Padding = new SW.Thickness(8, 2, 8, 2), ToolTip = "Marca as pranchas que estao marcadas no explorador de layouts (C3DLAYOUT)." };
        barra.Children.Add(bTodos);
        barra.Children.Add(bNenhum);
        barra.Children.Add(bPainel);
        WC.DockPanel.SetDock(barra, WC.Dock.Top);
        raiz.Children.Add(barra);

        var rodape = new WC.StackPanel { Margin = new SW.Thickness(0, 8, 0, 0) };
        var substituir = new WC.CheckBox
        {
            Content = "Substituir copias anteriores destes objetos",
            IsChecked = true,
            Foreground = t.Text,
            Margin = new SW.Thickness(0, 0, 0, 8),
            ToolTip = "Nas pranchas marcadas, as copias feitas antes pelo plugin com estes mesmos objetos sao apagadas antes de copiar de novo. Desmarque para acumular."
        };
        if (!remover)
            rodape.Children.Add(substituir);
        var erro = new WC.TextBlock { Foreground = t.Accent, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(0, 0, 0, 6) };
        rodape.Children.Add(erro);
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right };
        var ok = new WC.Button { Content = remover ? "Remover" : "Replicar", Padding = new SW.Thickness(14, 4, 14, 4), Margin = new SW.Thickness(0, 0, 6, 0), IsDefault = true };
        var cancelar = new WC.Button { Content = "Cancelar", Padding = new SW.Thickness(14, 4, 14, 4), IsCancel = true };
        botoes.Children.Add(ok);
        botoes.Children.Add(cancelar);
        rodape.Children.Add(botoes);
        WC.DockPanel.SetDock(rodape, WC.Dock.Bottom);
        raiz.Children.Add(rodape);

        var lista = new WC.StackPanel();
        var caixas = new List<(WC.CheckBox Caixa, Alvo Alvo)>();
        foreach (Alvo a in alvos)
        {
            var linha = new WC.DockPanel { LastChildFill = true, Margin = new SW.Thickness(0, 1, 0, 1) };
            var cb = new WC.CheckBox { Content = a.Nome, Foreground = t.Text, VerticalContentAlignment = SW.VerticalAlignment.Center };
            string? nota = null;
            if (a.EhOrigem)
            {
                nota = "origem";
                cb.IsEnabled = false;
                cb.ToolTip = "Prancha onde os objetos selecionados estao.";
            }
            else if (a.JaTemCopia)
            {
                nota = "ja tem copia";
                cb.ToolTip = remover ? "Tem copia destes objetos feita pelo plugin." : "Ja recebeu uma copia destes objetos; com Substituir marcado, ela e refeita.";
            }
            else if (remover)
            {
                cb.IsEnabled = false;
                cb.ToolTip = "Sem copia destes objetos nesta prancha.";
            }
            if (nota is not null)
            {
                var tb = new WC.TextBlock { Text = nota, Foreground = t.Muted, Margin = new SW.Thickness(8, 0, 0, 0), VerticalAlignment = SW.VerticalAlignment.Center };
                WC.DockPanel.SetDock(tb, WC.Dock.Right);
                linha.Children.Add(tb);
            }
            linha.Children.Add(cb);
            lista.Children.Add(linha);
            caixas.Add((cb, a));
        }
        var scroll = new WC.ScrollViewer { Content = lista, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, Background = t.Pane, Padding = new SW.Thickness(6) };
        raiz.Children.Add(scroll);
        w.Content = raiz;

        // Pre-marcacao: as do explorador (se aberto); no Remover, as que tem copia.
        List<string> doPainel = MarcadosNoPainel?.Invoke() ?? new List<string>();
        foreach ((WC.CheckBox cb, Alvo a) in caixas)
        {
            if (!cb.IsEnabled)
                continue;
            cb.IsChecked = remover ? a.JaTemCopia : doPainel.Contains(a.Nome, StringComparer.OrdinalIgnoreCase);
        }

        bTodos.Click += (_, _) => { foreach ((WC.CheckBox cb, _) in caixas) if (cb.IsEnabled) cb.IsChecked = true; };
        bNenhum.Click += (_, _) => { foreach ((WC.CheckBox cb, _) in caixas) cb.IsChecked = false; };
        bPainel.Click += (_, _) =>
        {
            List<string> m = MarcadosNoPainel?.Invoke() ?? new List<string>();
            if (m.Count == 0)
            {
                erro.Text = "O explorador de layouts nao esta aberto ou nao tem pranchas marcadas.";
                return;
            }
            erro.Text = "";
            foreach ((WC.CheckBox cb, Alvo a) in caixas)
                if (cb.IsEnabled)
                    cb.IsChecked = m.Contains(a.Nome, StringComparer.OrdinalIgnoreCase);
        };

        Escolha? resultado = null;
        ok.Click += (_, _) =>
        {
            var sel = caixas.Where(c => c.Caixa.IsChecked == true).Select(c => c.Alvo.Nome).ToList();
            if (sel.Count == 0)
            {
                erro.Text = "Marque pelo menos uma prancha.";
                return;
            }
            resultado = new Escolha(sel, substituir.IsChecked == true);
            w.DialogResult = true;
        };

        return AcApp.ShowModalWindow(w) == true ? resultado : null;
    }
}
