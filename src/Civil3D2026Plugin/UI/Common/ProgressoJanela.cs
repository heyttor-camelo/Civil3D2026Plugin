using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Civil3D2026Plugin.Infrastructure;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

namespace Civil3D2026Plugin.UI.Common;

/// <summary>
/// Janela de progresso numa thread propria: continua desenhando (etapa, barra e tempo) enquanto
/// a operacao ocupa a thread do AutoCAD. Cancelar so marca o pedido; a operacao le o pedido
/// entre uma etapa e outra e o que ja foi feito fica. Aparece sobre a janela do plugin que
/// abriu a operacao, e so se ela passar de meio segundo (operacoes rapidas nao piscam).
/// </summary>
internal sealed class ProgressoJanela : IProgressoVisor
{
    private readonly ManualResetEventSlim _pronta = new(false);
    private readonly Stopwatch _relogio = Stopwatch.StartNew();
    private WT.Dispatcher? _disp;
    private SW.Window? _janela;
    private volatile string _etapa = "";
    private long _fracao = BitConverter.DoubleToInt64Bits(0);
    private volatile bool _cancelado;
    private volatile bool _fechado;

    private ProgressoJanela()
    {
    }

    /// <summary>Liga o progresso (com a janela) ate o Dispose. Usar com "using" em volta da operacao.</summary>
    public static IDisposable Abrir(string titulo, SW.Window? sobre, bool cancelavel = true)
    {
        if (Progresso.Ativo)
            return Progresso.Iniciar(new ProgressoJanela()); // ja ha uma janela: fica a de fora
        SW.Rect? area = null;
        try
        {
            area = AreaNaTela(sobre);
        }
        catch (Exception)
        {
            // Sem posicao da janela: centraliza na tela.
        }
        var j = new ProgressoJanela();
        j.Iniciar(titulo, area, ToolTheme.Current, cancelavel);
        return Progresso.Iniciar(j);
    }

    public void Mostrar(string etapa, double fracao)
    {
        _etapa = etapa;
        if (!double.IsNaN(fracao))
            Interlocked.Exchange(ref _fracao, BitConverter.DoubleToInt64Bits(Math.Clamp(fracao, 0, 1)));
    }

    public bool Cancelado => _cancelado;

    public void Fechar()
    {
        if (_fechado)
            return;
        _fechado = true;
        WT.Dispatcher? disp = _disp;
        if (disp is null)
            return;
        try
        {
            disp.BeginInvoke(new Action(() =>
            {
                if (_janela is { IsVisible: true } janela)
                    janela.Close();
                else
                    disp.BeginInvokeShutdown(WT.DispatcherPriority.Background);
            }));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Retangulo da janela em unidades WPF da tela (vale maximizada e com DPI).</summary>
    private static SW.Rect? AreaNaTela(SW.Window? w)
    {
        if (w is null || !w.IsVisible || w.WindowState == SW.WindowState.Minimized)
            return null;
        SW.PresentationSource? fonte = SW.PresentationSource.FromVisual(w);
        if (fonte?.CompositionTarget is null)
            return null;
        WM.Matrix m = fonte.CompositionTarget.TransformFromDevice;
        SW.Point a = m.Transform(w.PointToScreen(new SW.Point(0, 0)));
        SW.Point b = m.Transform(w.PointToScreen(new SW.Point(w.ActualWidth, w.ActualHeight)));
        return new SW.Rect(a, b);
    }

    private void Iniciar(string titulo, SW.Rect? area, ToolTheme t, bool cancelavel)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Rodar(titulo, area, t, cancelavel);
            }
            catch (Exception ex)
            {
                PluginLog.Erro("Plugin", "Janela de progresso", ex);
            }
            finally
            {
                _pronta.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Civil3D2026Plugin progresso"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _pronta.Wait(3000);
    }

    private void Rodar(string titulo, SW.Rect? area, ToolTheme t, bool cancelavel)
    {
        WT.Dispatcher disp = WT.Dispatcher.CurrentDispatcher;
        _disp = disp;
        // A thread do AutoCAD ja pode seguir: um Fechar daqui em diante entra na fila deste
        // dispatcher e roda quando ele comecar.
        _pronta.Set();
        const double largura = 460;

        var tTitulo = new WC.TextBlock
        {
            Text = titulo,
            FontWeight = SW.FontWeights.Bold,
            FontSize = 13,
            Foreground = t.Text,
            TextTrimming = SW.TextTrimming.CharacterEllipsis
        };
        var tEtapa = new WC.TextBlock
        {
            Foreground = t.Text,
            Margin = new SW.Thickness(0, 8, 0, 6),
            TextTrimming = SW.TextTrimming.CharacterEllipsis
        };
        var barra = new WC.ProgressBar
        {
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 1000,
            Height = 14,
            Foreground = t.Accent,
            Background = t.Input,
            BorderBrush = t.Line
        };
        var tTempo = new WC.TextBlock { Foreground = t.Muted, VerticalAlignment = SW.VerticalAlignment.Center };
        var cancelar = new WC.Button
        {
            Content = "Cancelar",
            Padding = new SW.Thickness(12, 3, 12, 3),
            Visibility = cancelavel ? SW.Visibility.Visible : SW.Visibility.Collapsed,
            ToolTip = "Para depois da etapa atual. O que ja foi feito fica no desenho."
        };
        cancelar.Click += (_, _) =>
        {
            _cancelado = true;
            cancelar.IsEnabled = false;
            cancelar.Content = "Cancelando...";
        };

        var rodape = new WC.DockPanel { Margin = new SW.Thickness(0, 10, 0, 0) };
        WC.DockPanel.SetDock(cancelar, WC.Dock.Right);
        rodape.Children.Add(cancelar);
        rodape.Children.Add(tTempo);

        var conteudo = new WC.StackPanel { Margin = new SW.Thickness(16, 12, 16, 12) };
        conteudo.Children.Add(tTitulo);
        conteudo.Children.Add(tEtapa);
        conteudo.Children.Add(barra);
        conteudo.Children.Add(rodape);

        var janela = new SW.Window
        {
            Title = titulo,
            WindowStyle = SW.WindowStyle.None,
            ResizeMode = SW.ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = largura,
            SizeToContent = SW.SizeToContent.Height,
            Background = t.Back,
            Content = new WC.Border { BorderBrush = t.Accent, BorderThickness = new SW.Thickness(1), Child = conteudo }
        };
        _janela = janela;
        ToolStyles.Aplicar(janela.Resources, t);
        if (area is SW.Rect r)
        {
            janela.WindowStartupLocation = SW.WindowStartupLocation.Manual;
            janela.Left = r.Left + (r.Width - largura) / 2;
            janela.Top = r.Top + r.Height * 0.55 - 60;
        }
        else
        {
            janela.WindowStartupLocation = SW.WindowStartupLocation.CenterScreen;
        }

        var relogio = new WT.DispatcherTimer(WT.DispatcherPriority.Normal, disp) { Interval = TimeSpan.FromMilliseconds(150) };
        relogio.Tick += (_, _) =>
        {
            if (_fechado)
            {
                // Garantia (o pedido de Fechar pode ter chegado antes do dispatcher existir).
                relogio.Stop();
                if (janela.IsVisible)
                    janela.Close();
                else
                    disp.BeginInvokeShutdown(WT.DispatcherPriority.Background);
                return;
            }
            double f = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _fracao));
            tEtapa.Text = _etapa;
            // Sem fracao ainda (operacao sem etapas contadas): barra correndo.
            barra.IsIndeterminate = f <= 0;
            if (f > 0)
                barra.Value = f * 1000;
            TimeSpan dec = _relogio.Elapsed;
            string texto = "Tempo: " + Tempo(dec);
            if (f > 0.08 && f < 0.995 && dec.TotalSeconds > 5)
                texto += "   faltam ~" + Tempo(TimeSpan.FromSeconds(dec.TotalSeconds * (1 - f) / f));
            tTempo.Text = texto;
            // Aparece so se a operacao passar de meio segundo.
            if (!janela.IsVisible && dec.TotalMilliseconds > 500)
                janela.Show();
        };
        janela.Closed += (_, _) =>
        {
            relogio.Stop();
            disp.BeginInvokeShutdown(WT.DispatcherPriority.Background);
        };
        relogio.Start();
        WT.Dispatcher.Run();
    }

    private static string Tempo(TimeSpan t) =>
        ((int)t.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
}
