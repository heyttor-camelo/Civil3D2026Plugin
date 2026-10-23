using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.UI.Common;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Seguranca do painel: confirmacao explicando o que cada acao em lote vai mudar, pontos de
/// restauracao (Desfazer) e marcacao de varios itens com Shift/Ctrl na lista, nas viewports e nos
/// cartoes dos layouts.
/// </summary>
internal sealed partial class LayoutPanel
{
    /// <summary>Nome da acao que vai rodar (botao, item de menu): vai para o ponto de restauracao.</summary>
    private string? _acaoAtual;

    /// <summary>Acoes cuja confirmacao o usuario dispensou ate fechar o Civil.</summary>
    private static readonly HashSet<string> SemPerguntar = new(StringComparer.OrdinalIgnoreCase);

    private SheetViewport? _ultimoVp;
    private SheetData? _ultimaFolha;
    /// <summary>Caixa de marcar de cada cartao da vista Todos.</summary>
    private readonly Dictionary<SheetData, WC.CheckBox> _marcasCartao = new();

    // ------------------------------------------------------------------
    // Confirmacao
    // ------------------------------------------------------------------

    /// <summary>Janela "o que vai acontecer"; true = executar.</summary>
    private bool Confirmar(string texto)
    {
        string acao = _acaoAtual ?? "Confirmar";
        if (SemPerguntar.Contains(acao))
            return true;

        var w = new SW.Window
        {
            Title = acao,
            Width = 520,
            SizeToContent = SW.SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner,
            ResizeMode = SW.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = T.Back,
            Foreground = T.Text
        };
        w.Resources.MergedDictionaries.Add(Resources);
        var sp = new WC.StackPanel { Margin = new SW.Thickness(14) };
        sp.Children.Add(new WC.TextBlock { Text = "O que vai acontecer", FontWeight = SW.FontWeights.Bold, FontSize = 13, Foreground = T.Text, Margin = new SW.Thickness(0, 0, 0, 6) });
        sp.Children.Add(new WC.ScrollViewer
        {
            MaxHeight = 380,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            Content = new WC.TextBlock { Text = texto, TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text }
        });
        sp.Children.Add(new WC.TextBlock
        {
            Text = "Antes de executar, o plugin guarda um ponto de restauracao com a vista de todas as viewports e a ordem e o nome das abas. " +
                   "Para voltar: botao Desfazer (seta para tras) na barra do painel. Ctrl+Z no Civil tambem desfaz, enquanto o desenho estiver aberto.",
            TextWrapping = SW.TextWrapping.Wrap,
            Foreground = T.Muted,
            Margin = new SW.Thickness(0, 10, 0, 0)
        });
        var naoPerguntar = new WC.CheckBox { Content = $"Nao perguntar de novo para \"{acao}\" ate fechar o Civil", Foreground = T.Text, Margin = new SW.Thickness(0, 10, 0, 0) };
        sp.Children.Add(naoPerguntar);
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right, Margin = new SW.Thickness(0, 12, 0, 0) };
        var executar = new WC.Button { Content = "Executar", MinWidth = 90, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0) };
        var cancelar = new WC.Button { Content = "Cancelar", MinWidth = 90, Padding = new SW.Thickness(10, 3, 10, 3), IsCancel = true, IsDefault = true };
        executar.Click += (_, _) => w.DialogResult = true;
        botoes.Children.Add(executar);
        botoes.Children.Add(cancelar);
        sp.Children.Add(botoes);
        w.Content = sp;

        bool ok = w.ShowDialog() == true;
        if (ok && naoPerguntar.IsChecked == true)
            SemPerguntar.Add(acao);
        PluginLog.Info("Layouts", $"Confirmacao de \"{acao}\": {(ok ? "executar" : "cancelado")}");
        return ok;
    }

    /// <summary>Texto da confirmacao para as viewports marcadas (ou as dadas); null (sem perguntar) abaixo de <paramref name="minimo"/>.</summary>
    private string? ConfVps(string oQue, int minimo, IReadOnlyCollection<ObjectId>? ids = null)
    {
        List<ObjectId> lista = (ids ?? (IReadOnlyCollection<ObjectId>)_vpsMarcadas).ToList();
        if (lista.Count == 0 || lista.Count < minimo)
            return null;
        List<string> layouts = _folhas.Where(f => f.Viewports.Any(v => lista.Contains(v.Id))).Select(f => f.Nome).ToList();
        return $"{oQue}\n\nViewports: {lista.Count}, em {layouts.Count} layout(s):\n{ListaNomes(layouts)}";
    }

    /// <summary>Texto da confirmacao para os layouts marcados; null se nenhum.</summary>
    private string? ConfLayouts(string oQue)
    {
        List<string> nomes = Marcados();
        return nomes.Count == 0 ? null : $"{oQue}\n\nLayouts marcados: {nomes.Count}\n{ListaNomes(nomes)}";
    }

    private static string ListaNomes(List<string> nomes) =>
        "   " + string.Join(", ", nomes.Take(20)) + (nomes.Count > 20 ? $", ... e mais {nomes.Count - 20}" : "");

    // ------------------------------------------------------------------
    // Desfazer (pontos de restauracao)
    // ------------------------------------------------------------------

    private void Restaurar()
    {
        if (Doc is not Document doc)
        {
            Say("Nenhum desenho aberto.");
            return;
        }
        List<LayoutRestauracao.Ponto> pontos = LayoutRestauracao.Listar(doc.Database);
        if (pontos.Count == 0)
        {
            Say("Nenhum ponto de restauracao para este desenho ainda (eles sao criados antes de cada acao do painel que mexe no desenho).");
            return;
        }

        var w = new SW.Window
        {
            Title = "Desfazer: pontos de restauracao",
            Width = 640,
            SizeToContent = SW.SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner,
            ResizeMode = SW.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = T.Back,
            Foreground = T.Text
        };
        w.Resources.MergedDictionaries.Add(Resources);
        var sp = new WC.StackPanel { Margin = new SW.Thickness(14) };
        sp.Children.Add(new WC.TextBlock
        {
            Text = "Antes de cada acao do painel que mexe no desenho (e antes da atualizacao automatica das pranchas do eixo) o plugin guarda a vista de " +
                   "todas as viewports (centro, escala, giro, posicao e tamanho no papel, travada, ligada) e o nome e a ordem das abas. " +
                   "Restaurar devolve so isso; textos, blocos e camadas nao voltam (para eles use Ctrl+Z do Civil enquanto o desenho estiver aberto). " +
                   "Ficam os ultimos 30 pontos de cada arquivo, mesmo depois de fechar o Civil.",
            TextWrapping = SW.TextWrapping.Wrap,
            Foreground = T.Muted,
            Margin = new SW.Thickness(0, 0, 0, 8)
        });
        var lista = new WC.ListBox { Height = 220, Background = T.Pane, Foreground = T.Text, BorderBrush = T.Line };
        foreach (LayoutRestauracao.Ponto p in pontos)
            lista.Items.Add(p.Rotulo);
        sp.Children.Add(lista);
        var resumo = new WC.TextBlock { TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text, Margin = new SW.Thickness(0, 8, 0, 0) };
        sp.Children.Add(resumo);
        var excluirNovos = new WC.CheckBox { Foreground = T.Text, Margin = new SW.Thickness(0, 8, 0, 0), Visibility = SW.Visibility.Collapsed };
        sp.Children.Add(excluirNovos);
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right, Margin = new SW.Thickness(0, 12, 0, 0) };
        var restaurar = new WC.Button { Content = "Restaurar este ponto", MinWidth = 140, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0), IsEnabled = false };
        var fechar = new WC.Button { Content = "Fechar", MinWidth = 90, Padding = new SW.Thickness(10, 3, 10, 3), IsCancel = true };
        botoes.Children.Add(restaurar);
        botoes.Children.Add(fechar);
        sp.Children.Add(botoes);
        w.Content = sp;

        lista.SelectionChanged += (_, _) =>
        {
            if (lista.SelectedIndex < 0)
                return;
            LayoutRestauracao.Ponto p = pontos[lista.SelectedIndex];
            try
            {
                var d = LayoutRestauracao.Diferencas(doc.Database, p);
                var linhas = new List<string>
                {
                    d.Vistas == 0 ? "Vistas das viewports: iguais as de agora." : $"{d.Vistas} viewport(s) voltam para a vista que tinham.",
                    d.Ordem ? "A ordem das abas volta para a daquele momento." : "Ordem das abas: igual a de agora.",
                };
                if (d.Renomeados > 0)
                    linhas.Add($"{d.Renomeados} layout(s) voltam ao nome antigo.");
                if (d.Sumiram.Count > 0)
                    linhas.Add($"{d.Sumiram.Count} layout(s) que existiam foram excluidos depois e NAO voltam por aqui (Ctrl+Z do Civil): {string.Join(", ", d.Sumiram.Take(8))}{(d.Sumiram.Count > 8 ? ", ..." : "")}");
                resumo.Text = "Ao restaurar:\n- " + string.Join("\n- ", linhas);
                excluirNovos.Visibility = d.Novos.Count > 0 ? SW.Visibility.Visible : SW.Visibility.Collapsed;
                excluirNovos.IsChecked = false;
                excluirNovos.Content = $"Excluir tambem os {d.Novos.Count} layout(s) criados depois deste ponto: {string.Join(", ", d.Novos.Take(6))}{(d.Novos.Count > 6 ? ", ..." : "")}";
                restaurar.IsEnabled = true;
            }
            catch (System.Exception ex)
            {
                resumo.Text = "Nao foi possivel comparar: " + ex.Message;
                restaurar.IsEnabled = false;
            }
        };
        LayoutRestauracao.Ponto? escolhido = null;
        bool apagar = false;
        restaurar.Click += (_, _) =>
        {
            escolhido = pontos[lista.SelectedIndex];
            apagar = excluirNovos.Visibility == SW.Visibility.Visible && excluirNovos.IsChecked == true;
            w.DialogResult = true;
        };
        lista.SelectedIndex = 0;

        if (w.ShowDialog() != true || escolhido is null)
            return;
        LayoutRestauracao.Ponto alvo = escolhido;
        _acaoAtual = $"restaurar o ponto de {alvo.Quando:dd/MM HH:mm:ss}";
        ExecutarLongo("Restaurando vistas e abas", () => LayoutRestauracao.Restaurar(doc, alvo, apagar));
    }

    // ------------------------------------------------------------------
    // Marcar varios: Shift e Ctrl
    // ------------------------------------------------------------------

    private static bool Shift => (WI.Keyboard.Modifiers & WI.ModifierKeys.Shift) != 0;
    private static bool Ctrl => (WI.Keyboard.Modifiers & WI.ModifierKeys.Control) != 0;

    /// <summary>
    /// Clique numa viewport: marca/desmarca (com ou sem Ctrl); Shift marca todas entre a ultima
    /// clicada e esta, na ordem das abas e dos numeros das viewports.
    /// </summary>
    private void ClicarViewport(SheetViewport v)
    {
        if (Shift && _ultimoVp is not null && _vpPorId.ContainsKey(_ultimoVp.Id))
        {
            List<SheetViewport> ordem = _folhas.SelectMany(f => f.Viewports).ToList();
            int a = ordem.FindIndex(x => x.Id == _ultimoVp.Id), b = ordem.FindIndex(x => x.Id == v.Id);
            if (a >= 0 && b >= 0)
            {
                int n = 0;
                foreach (SheetViewport x in ordem.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                    if (_vpsMarcadas.Add(x.Id))
                        n++;
                MostrarStatus($"Shift: {n} viewport(s) marcada(s) entre a ultima clicada e esta.");
            }
        }
        else if (!_vpsMarcadas.Remove(v.Id))
        {
            _vpsMarcadas.Add(v.Id);
        }
        _ultimoVp = v;
        AtualizarViewports();
        if (_category == "VIEWPORTS")
            FillList();
        Status();
    }

    /// <summary>Ctrl+clique no cartao marca/desmarca o layout; Shift+clique marca do ultimo clicado ate este.</summary>
    private bool ClicarCartao(SheetData f)
    {
        if (!Shift && !Ctrl)
        {
            _ultimaFolha = f;
            return false;
        }
        if (Shift && _ultimaFolha is not null && _folhas.Contains(_ultimaFolha))
        {
            int a = _folhas.IndexOf(_ultimaFolha), b = _folhas.IndexOf(f);
            foreach (SheetData x in _folhas.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                _layoutsMarcados.Add(x.Nome);
        }
        else if (!_layoutsMarcados.Remove(f.Nome))
        {
            _layoutsMarcados.Add(f.Nome);
        }
        _ultimaFolha = f;
        foreach ((SheetData x, WC.CheckBox cb) in _marcasCartao)
            cb.IsChecked = _layoutsMarcados.Contains(x.Nome);
        if (_category is "LAYOUTS" or "NUMERACAO" or "PAGESETUP" or "CARIMBO")
            FillList();
        Status();
        return true;
    }

    /// <summary>
    /// Na lista, selecionar com Ctrl ou Shift tambem marca: as linhas que entram na selecao ficam
    /// marcadas e as que saem (Ctrl+clique de novo) ficam desmarcadas. Clique simples so mostra.
    /// </summary>
    private void SelecaoDaLista(WC.SelectionChangedEventArgs e)
    {
        if (!_listaComCheck || (!Shift && !Ctrl))
            return;
        bool texto = false, mudou = false;
        foreach ((System.Collections.IList itens, bool marcar) in new[] { (e.AddedItems, true), (e.RemovedItems, false) })
        {
            foreach (WC.ListViewItem it in itens.OfType<WC.ListViewItem>())
            {
                if (it.Tag is not Row r || r.Data is not (SheetData or SheetViewport or SheetText2))
                    continue;
                Marcar(r.Data, marcar);
                if (it.Content is RowView rv)
                    rv.Checked = marcar;
                texto |= r.Data is SheetText2;
                mudou = true;
            }
        }
        if (!mudou)
            return;
        if (texto)
            EscolhidosMudaram();
        Status();
    }
}
