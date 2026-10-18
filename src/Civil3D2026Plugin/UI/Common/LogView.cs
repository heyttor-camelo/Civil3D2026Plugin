using System;
using System.Collections.Generic;
using System.Linq;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.UI.Common;

/// <summary>
/// Visualizacao do log do plugin (aba escondida dos paineis e janela C3DLOG): filtro por modulo,
/// so avisos/erros, diagnostico detalhado, copiar, salvar, abrir a pasta e limpar.
/// </summary>
internal static class LogView
{
    private const string Todos = "(todos os modulos)";
    private const int MaximoNaCaixa = 3000;
    private static SW.Window? _janela;

    public static SW.FrameworkElement Criar(ToolTheme t, string? modulo)
    {
        var raiz = new WC.DockPanel { Margin = new SW.Thickness(8) };

        // Barra de filtros.
        var topo = new WC.WrapPanel { Margin = new SW.Thickness(0, 0, 0, 6) };
        WC.DockPanel.SetDock(topo, WC.Dock.Top);
        var cbModulo = new WC.ComboBox { Width = 200, Margin = new SW.Thickness(0, 0, 10, 0), ToolTip = "Mostra so as linhas deste plugin/modulo." };
        cbModulo.Items.Add(Todos);
        foreach (string m in PluginLog.Modulos())
            cbModulo.Items.Add(m);
        if (modulo is not null && !cbModulo.Items.Contains(modulo))
            cbModulo.Items.Add(modulo);
        cbModulo.SelectedItem = modulo ?? Todos;
        var chProblemas = new WC.CheckBox { Content = "So avisos e erros", Foreground = t.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 10, 0) };
        var chDetalhado = new WC.CheckBox
        {
            Content = "Diagnostico detalhado",
            IsChecked = PluginLog.Detalhado,
            Foreground = t.Text,
            VerticalAlignment = SW.VerticalAlignment.Center,
            ToolTip = "Registra tambem excecoes tratadas dentro do plugin (falhas silenciosas). Ligue para reproduzir um problema e mandar o log; desligue no uso normal."
        };
        chDetalhado.Click += (_, _) =>
        {
            PluginLog.Detalhado = chDetalhado.IsChecked == true;
            PluginLog.Info("Plugin", PluginLog.Detalhado ? "Diagnostico detalhado ligado." : "Diagnostico detalhado desligado.");
        };
        topo.Children.Add(cbModulo);
        topo.Children.Add(chProblemas);
        topo.Children.Add(chDetalhado);
        raiz.Children.Add(topo);

        // Botoes.
        var baixo = new WC.WrapPanel { Margin = new SW.Thickness(0, 6, 0, 0) };
        WC.DockPanel.SetDock(baixo, WC.Dock.Bottom);
        var info = new WC.TextBlock { Foreground = t.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(8, 0, 0, 0) };

        var texto = new WC.TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new WM.FontFamily("Consolas"),
            FontSize = 11.5,
            TextWrapping = SW.TextWrapping.NoWrap,
            VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
            Background = t.Input,
            Foreground = t.Text,
            BorderBrush = t.Line,
            MinHeight = 160
        };

        bool Passa(LogEntrada e)
        {
            string? m = cbModulo.SelectedItem as string;
            if (m is not null && m != Todos && !e.Modulo.Equals(m, StringComparison.OrdinalIgnoreCase))
                return false;
            return chProblemas.IsChecked != true || e.Nivel is LogNivel.Aviso or LogNivel.Erro;
        }
        List<LogEntrada> Filtradas() => PluginLog.Copia().Where(Passa).ToList();
        // Linhas mostradas (sem ler texto.Text a cada linha nova: isso remonta o texto inteiro).
        int mostradas = 0;
        void Encher()
        {
            List<LogEntrada> l = Filtradas();
            // So as ultimas na caixa (Copiar e Salvar levam todas as filtradas).
            texto.Text = string.Join(Environment.NewLine, l.Skip(Math.Max(0, l.Count - MaximoNaCaixa)).Select(e => e.Linha));
            mostradas = Math.Min(l.Count, MaximoNaCaixa);
            texto.ScrollToEnd();
            info.Text = $"{l.Count} linha(s). Arquivo do dia em {PluginLog.Pasta}";
        }

        WC.Button Botao(string rotulo, string dica, Action acao)
        {
            var b = new WC.Button { Content = rotulo, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = dica };
            b.Click += (_, _) =>
            {
                try
                {
                    acao();
                }
                catch (Exception ex)
                {
                    info.Text = "Erro: " + ex.Message;
                }
            };
            return b;
        }
        baixo.Children.Add(Botao("Copiar", "Copia o log filtrado (com o cabecalho de versoes) para colar na conversa.", () =>
        {
            string conteudo = PluginLog.Texto(Filtradas());
            for (int tentativa = 0; ; tentativa++)
            {
                try
                {
                    SW.Clipboard.SetText(conteudo);
                    info.Text = "Copiado para a area de transferencia.";
                    return;
                }
                catch (System.Runtime.InteropServices.COMException) when (tentativa < 3)
                {
                    System.Threading.Thread.Sleep(60);
                }
            }
        }));
        baixo.Children.Add(Botao("Salvar...", "Salva o log filtrado num arquivo .txt.", () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Texto (*.txt)|*.txt", FileName = $"civil3d2026plugin-log-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
            if (dlg.ShowDialog() == true)
            {
                System.IO.File.WriteAllText(dlg.FileName, PluginLog.Texto(Filtradas()));
                info.Text = "Salvo em " + dlg.FileName;
            }
        }));
        baixo.Children.Add(Botao("Abrir pasta", "Abre a pasta com os arquivos diarios de log (guardados por 14 dias).", () =>
        {
            System.IO.Directory.CreateDirectory(PluginLog.Pasta);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + PluginLog.Pasta + "\"") { UseShellExecute = true });
        }));
        baixo.Children.Add(Botao("Limpar", "Limpa as linhas da memoria (o arquivo do dia continua).", () =>
        {
            PluginLog.Limpar();
            Encher();
        }));
        baixo.Children.Add(info);
        raiz.Children.Add(baixo);
        raiz.Children.Add(texto);

        cbModulo.SelectionChanged += (_, _) => Encher();
        chProblemas.Click += (_, _) => Encher();

        // Ao vivo: linhas novas entram no fim.
        void Nova(LogEntrada e)
        {
            texto.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Passa(e))
                    return;
                if (cbModulo.SelectedItem is string m && m == Todos && !cbModulo.Items.Contains(e.Modulo))
                    cbModulo.Items.Add(e.Modulo);
                if (mostradas >= MaximoNaCaixa * 2)
                {
                    // Caixa cheia: refaz com as ultimas (barato de vez em quando, caro a cada linha).
                    Encher();
                    return;
                }
                texto.AppendText((mostradas > 0 ? Environment.NewLine : "") + e.Linha);
                mostradas++;
                texto.ScrollToEnd();
            }));
        }
        raiz.Loaded += (_, _) =>
        {
            PluginLog.Novo -= Nova;
            PluginLog.Novo += Nova;
            Encher();
        };
        raiz.Unloaded += (_, _) => PluginLog.Novo -= Nova;
        return raiz;
    }

    /// <summary>Janela de log (C3DLOG ou Ctrl+Shift+L numa janela do plugin).</summary>
    public static void AbrirJanela(string? modulo = null)
    {
        ToolTheme t = ToolTheme.Current;
        if (_janela is not null)
        {
            _janela.Content = Criar(t, modulo);
            _janela.Show();
            _janela.Activate();
            return;
        }
        _janela = new SW.Window
        {
            Title = "Log do plugin (Civil3D2026Plugin)",
            Width = 980,
            Height = 520,
            Background = t.Back,
            Foreground = t.Text,
            ShowInTaskbar = false
        };
        ToolStyles.Aplicar(_janela.Resources, t);
        _janela.Content = Criar(t, modulo);
        _janela.Closed += (_, _) => _janela = null;
        try
        {
            AcApp.ShowModelessWindow(_janela);
        }
        catch (Exception)
        {
            _janela.Show();
        }
    }

    /// <summary>Ctrl+Shift+L numa janela WPF do plugin abre o log daquele modulo.</summary>
    public static void Atalho(SW.Window janela, string modulo)
    {
        janela.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.L && WI.Keyboard.Modifiers == (WI.ModifierKeys.Control | WI.ModifierKeys.Shift))
            {
                AbrirJanela(modulo);
                e.Handled = true;
            }
        };
    }

    /// <summary>Ctrl+Shift+L numa janela Windows Forms do plugin.</summary>
    public static void Atalho(System.Windows.Forms.Form janela, string modulo)
    {
        janela.KeyPreview = true;
        janela.KeyDown += (_, e) =>
        {
            if (e.KeyCode == System.Windows.Forms.Keys.L && e.Control && e.Shift)
            {
                AbrirJanela(modulo);
                e.Handled = true;
            }
        };
    }
}
