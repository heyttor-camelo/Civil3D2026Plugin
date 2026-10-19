using System;
using System.Collections.Generic;
using System.Linq;
using Civil3D2026Plugin.UI.Common;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Filtro e ordenacao por coluna da lista (todas as categorias): clicar no cabecalho abre a lista
/// de valores daquela coluna com caixas, busca e ordenar A-Z/Z-A, como o autofiltro do Excel.
/// </summary>
internal sealed partial class LayoutPanel
{
    /// <summary>Por categoria: coluna -> valores visiveis (sem entrada = sem filtro).</summary>
    private readonly Dictionary<string, Dictionary<int, HashSet<string>>> _filtrosColuna = new();
    private readonly Dictionary<string, (int Coluna, bool Desc)> _ordemColuna = new();
    /// <summary>Linhas da lista antes dos filtros de coluna (depois do Localizar).</summary>
    private List<Row> _linhasBase = new();

    private Dictionary<int, HashSet<string>> FiltrosDaCategoria()
    {
        if (!_filtrosColuna.TryGetValue(_category, out Dictionary<int, HashSet<string>>? f))
            _filtrosColuna[_category] = f = new Dictionary<int, HashSet<string>>();
        return f;
    }

    private static string Celula(Row r, int coluna) => coluna < r.Cells.Length ? r.Cells[coluna] ?? "" : "";

    /// <summary>Aplica os filtros de coluna (menos o da coluna "exceto") e a ordenacao.</summary>
    private List<Row> AplicarFiltrosColuna(List<Row> linhas, int exceto = -1, bool ordenar = true)
    {
        IEnumerable<Row> r = linhas;
        foreach ((int col, HashSet<string> valores) in FiltrosDaCategoria())
        {
            if (col == exceto)
                continue;
            int c = col;
            r = r.Where(x => valores.Contains(Celula(x, c)));
        }
        if (ordenar && _ordemColuna.TryGetValue(_category, out (int Coluna, bool Desc) o))
        {
            r = o.Desc
                ? r.OrderByDescending(x => Celula(x, o.Coluna), LayoutOps.NaturalComparer.Instance)
                : r.OrderBy(x => Celula(x, o.Coluna), LayoutOps.NaturalComparer.Instance);
        }
        return r.ToList();
    }

    /// <summary>Cabecalho clicavel: nome, seta de ordenacao e destaque quando filtrado.</summary>
    private WC.GridViewColumnHeader CabecalhoFiltravel(string titulo, int coluna)
    {
        bool filtrada = FiltrosDaCategoria().ContainsKey(coluna);
        string ordem = _ordemColuna.TryGetValue(_category, out (int Coluna, bool Desc) o) && o.Coluna == coluna ? (o.Desc ? "  ↓" : "  ↑") : "";
        var texto = new WC.TextBlock { Foreground = filtrada ? T.Accent : T.Text, FontWeight = filtrada ? SW.FontWeights.Bold : SW.FontWeights.Normal };
        texto.Inlines.Add(new SW.Documents.Run(titulo + ordem));
        texto.Inlines.Add(new SW.Documents.Run(filtrada ? "  ▼" : "  ▾") { Foreground = filtrada ? T.Accent : T.Muted });
        var h = new WC.GridViewColumnHeader
        {
            Content = texto,
            ToolTip = filtrada ? $"{titulo}: filtrado ({FiltrosDaCategoria()[coluna].Count} valor(es)). Clique para mudar." : $"Clique para filtrar ou ordenar por {titulo}."
        };
        h.Click += (_, _) => AbrirFiltro(h, titulo, coluna);
        return h;
    }

    private void AbrirFiltro(WC.GridViewColumnHeader cabecalho, string titulo, int coluna)
    {
        // Valores com os outros filtros aplicados (como no Excel), com contagem.
        List<Row> linhas = AplicarFiltrosColuna(_linhasBase, exceto: coluna, ordenar: false);
        List<(string Valor, int N)> valores = linhas.GroupBy(x => Celula(x, coluna))
            .Select(g => (g.Key, g.Count()))
            .OrderBy(v => v.Key, LayoutOps.NaturalComparer.Instance).ToList();
        Dictionary<int, HashSet<string>> filtros = FiltrosDaCategoria();
        HashSet<string>? atual = filtros.GetValueOrDefault(coluna);

        var janela = new SW.Window
        {
            WindowStyle = SW.WindowStyle.None,
            ResizeMode = SW.ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SW.SizeToContent.WidthAndHeight,
            Owner = this,
            Background = T.Pane,
            BorderBrush = T.Accent,
            BorderThickness = new SW.Thickness(1)
        };
        janela.Resources.MergedDictionaries.Add(Resources);
        SW.Point tela = cabecalho.PointToScreen(new SW.Point(0, cabecalho.ActualHeight));
        SW.PresentationSource? fonte = SW.PresentationSource.FromVisual(this);
        if (fonte?.CompositionTarget is not null)
            tela = fonte.CompositionTarget.TransformFromDevice.Transform(tela);
        janela.WindowStartupLocation = SW.WindowStartupLocation.Manual;
        janela.Left = tela.X;
        janela.Top = tela.Y;

        var sp = new WC.StackPanel { Margin = new SW.Thickness(8), Width = 260 };
        sp.Children.Add(new WC.TextBlock { Text = titulo, FontWeight = SW.FontWeights.Bold, Foreground = T.Text, Margin = new SW.Thickness(0, 0, 0, 6) });

        int? ordenarPor = null;
        bool desc = false;
        var ordem = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 0, 0, 6) };
        WC.Button BotaoOrdem(string t, bool d)
        {
            var b = new WC.Button { Content = t, Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 6, 0) };
            b.Click += (_, _) =>
            {
                ordenarPor = coluna;
                desc = d;
                janela.DialogResult = true;
            };
            return b;
        }
        ordem.Children.Add(BotaoOrdem("Ordenar A → Z", false));
        ordem.Children.Add(BotaoOrdem("Z → A", true));
        sp.Children.Add(ordem);

        var busca = new WC.TextBox { Margin = new SW.Thickness(0, 0, 0, 4), ToolTip = "Filtra a lista de valores abaixo." };
        sp.Children.Add(busca);

        var caixas = new List<(WC.CheckBox Caixa, string Valor)>();
        var lista = new WC.StackPanel();
        var todos = new WC.CheckBox { Content = "(Selecionar tudo)", FontWeight = SW.FontWeights.Bold, Margin = new SW.Thickness(0, 1, 0, 3) };
        lista.Children.Add(todos);
        foreach ((string valor, int n) in valores)
        {
            var cb = new WC.CheckBox
            {
                Content = $"{(valor.Length == 0 ? "(vazio)" : valor)}   ({n})",
                IsChecked = atual is null || atual.Contains(valor),
                Margin = new SW.Thickness(0, 1, 0, 1)
            };
            caixas.Add((cb, valor));
            lista.Children.Add(cb);
        }
        bool Visivel(WC.CheckBox c) => c.Visibility == SW.Visibility.Visible;
        void AtualizarTodos() => todos.IsChecked = caixas.Where(c => Visivel(c.Caixa)).All(c => c.Caixa.IsChecked == true)
            ? true : caixas.Where(c => Visivel(c.Caixa)).Any(c => c.Caixa.IsChecked == true) ? null : false;
        todos.Click += (_, _) =>
        {
            bool marcar = todos.IsChecked == true;
            foreach ((WC.CheckBox c, _) in caixas.Where(c => Visivel(c.Caixa)))
                c.IsChecked = marcar;
        };
        foreach ((WC.CheckBox c, _) in caixas)
            c.Click += (_, _) => AtualizarTodos();
        busca.TextChanged += (_, _) =>
        {
            string t = busca.Text.Trim();
            foreach ((WC.CheckBox c, string v) in caixas)
                c.Visibility = t.Length == 0 || v.Contains(t, StringComparison.OrdinalIgnoreCase) ? SW.Visibility.Visible : SW.Visibility.Collapsed;
            AtualizarTodos();
        };
        AtualizarTodos();
        sp.Children.Add(new WC.Border
        {
            BorderBrush = T.Line,
            BorderThickness = new SW.Thickness(1),
            Child = new WC.ScrollViewer { Content = lista, MaxHeight = 300, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, Padding = new SW.Thickness(4) }
        });

        bool limpar = false;
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right, Margin = new SW.Thickness(0, 8, 0, 0) };
        var btLimpar = new WC.Button { Content = "Limpar filtro", Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 6, 0) };
        btLimpar.Click += (_, _) =>
        {
            limpar = true;
            janela.DialogResult = true;
        };
        var ok = new WC.Button { Content = "OK", MinWidth = 60, IsDefault = true, Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => janela.DialogResult = true;
        var cancelar = new WC.Button { Content = "Cancelar", Padding = new SW.Thickness(8, 2, 8, 2) };
        botoes.Children.Add(btLimpar);
        botoes.Children.Add(ok);
        botoes.Children.Add(cancelar);
        sp.Children.Add(botoes);
        janela.Content = sp;
        janela.Loaded += (_, _) => busca.Focus();

        if (janela.ShowDialog() != true)
            return;

        if (ordenarPor is int col)
        {
            _ordemColuna[_category] = (col, desc);
        }
        else if (limpar)
        {
            filtros.Remove(coluna);
            if (_ordemColuna.TryGetValue(_category, out (int Coluna, bool Desc) o) && o.Coluna == coluna)
                _ordemColuna.Remove(_category);
        }
        else
        {
            var marcados = caixas.Where(c => c.Caixa.IsChecked == true).Select(c => c.Valor).ToHashSet();
            if (marcados.Count == caixas.Count)
                filtros.Remove(coluna);
            else
                filtros[coluna] = marcados;
        }
        FillList();
        Status();
    }

    /// <summary>Quantos filtros de coluna estao ativos na categoria (para o rodape).</summary>
    private int FiltrosAtivos() => FiltrosDaCategoria().Count;
}
