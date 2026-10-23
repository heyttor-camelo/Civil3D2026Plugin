using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.UI.Common;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Janela do padrao de malha, norte e escala (C3DMNE > Configurar e explorador): liga/desliga cada
/// item, intervalo da malha e total da escala (fixos em metros ou automaticos pelo papel), tamanhos
/// de texto e do norte, canto/deslocamento e layers.
/// </summary>
internal static class LayoutConjuntoJanela
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Mostra a janela; true se o padrao foi alterado e salvo.</summary>
    public static bool Editar(PadraoConjunto p, Database? db, SW.Window? dono)
    {
        ToolTheme t = ToolTheme.Current;
        var w = new SW.Window
        {
            Title = "Malha, norte e escala - padrao",
            Width = 470,
            SizeToContent = SW.SizeToContent.Height,
            ResizeMode = SW.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = t.Back,
            Foreground = t.Text,
            WindowStartupLocation = dono is null ? SW.WindowStartupLocation.CenterScreen : SW.WindowStartupLocation.CenterOwner
        };
        if (dono is not null)
            w.Owner = dono;
        ToolStyles.Aplicar(w.Resources, t);

        List<string> layers = Layers(db);
        var raiz = new WC.StackPanel { Margin = new SW.Thickness(12) };

        WC.Grid Grupo(string titulo, out WC.CheckBox ativo, bool valor)
        {
            ativo = new WC.CheckBox { Content = titulo, IsChecked = valor, FontWeight = SW.FontWeights.Bold, Foreground = t.Text, Margin = new SW.Thickness(0, 8, 0, 4) };
            raiz.Children.Add(ativo);
            var g = new WC.Grid { Margin = new SW.Thickness(18, 0, 0, 0) };
            g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(210) });
            g.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
            WC.CheckBox a = ativo;
            a.Click += (_, _) => g.IsEnabled = a.IsChecked == true;
            g.IsEnabled = valor;
            raiz.Children.Add(g);
            return g;
        }
        void Linha(WC.Grid g, string rotulo, SW.UIElement campo, string? dica = null)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            var tb = new WC.TextBlock { Text = rotulo, Foreground = t.Text, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 2, 6, 2), ToolTip = dica };
            WC.Grid.SetRow(tb, r);
            g.Children.Add(tb);
            if (campo is SW.FrameworkElement fe)
            {
                fe.Margin = new SW.Thickness(0, 2, 0, 2);
                fe.ToolTip ??= dica;
            }
            WC.Grid.SetRow(campo, r);
            WC.Grid.SetColumn(campo, 1);
            g.Children.Add(campo);
        }
        WC.TextBox Num(double v, string fmt = "0.##") => new() { Text = v.ToString(fmt, Br), Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        WC.TextBox Auto(double v) => new() { Text = v > 0 ? v.ToString("0.##", Br) : "", Width = 90, HorizontalAlignment = SW.HorizontalAlignment.Left };
        WC.ComboBox Layer(string atual)
        {
            var c = new WC.ComboBox { IsEditable = true, Text = atual, Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
            foreach (string l in layers)
                c.Items.Add(l);
            c.Text = atual;
            return c;
        }
        WC.ComboBox Canto(CantoVp atual)
        {
            var c = new WC.ComboBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left };
            foreach (CantoVp k in Enum.GetValues(typeof(CantoVp)))
                c.Items.Add(new WC.ComboBoxItem { Content = PadraoConjunto.NomeCanto(k), Tag = k });
            c.SelectedIndex = Array.IndexOf(Enum.GetValues(typeof(CantoVp)), atual);
            return c;
        }
        WC.StackPanel Desloc(Ancora a, out WC.TextBox dx, out WC.TextBox dy)
        {
            var sp = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
            dx = Num(a.Dx);
            dy = Num(a.Dy);
            dx.Width = dy.Width = 60;
            sp.Children.Add(new WC.TextBlock { Text = "X ", Foreground = t.Muted, VerticalAlignment = SW.VerticalAlignment.Center });
            sp.Children.Add(dx);
            sp.Children.Add(new WC.TextBlock { Text = "   Y ", Foreground = t.Muted, VerticalAlignment = SW.VerticalAlignment.Center });
            sp.Children.Add(dy);
            return sp;
        }

        raiz.Children.Add(new WC.TextBlock
        {
            Text = "Tamanhos em mm de papel. Deixe o intervalo/total em metros vazio para o plugin escolher um valor redondo pela escala de cada viewport. " +
                   "Dica: lance os tres numa viewport, ajuste a mao e use \"Salvar o padrao\" (botao direito na viewport) para ler posicao, tamanho e layer.",
            TextWrapping = SW.TextWrapping.Wrap,
            Foreground = t.Muted
        });

        WC.Grid gm = Grupo("Malha de coordenadas", out WC.CheckBox mAtivo, p.Malha);
        WC.TextBox mM = Auto(p.MalhaM);
        WC.TextBox mMm = Num(p.MalhaMm, "0.#");
        WC.TextBox mTxt = Num(p.MalhaTexto);
        var mCruz = new WC.CheckBox { Content = "Cruzes (com tracos na borda) em vez de linhas", IsChecked = p.MalhaCruzes, Foreground = t.Text };
        WC.TextBox mTr = Num(p.MalhaTransp, "0");
        WC.ComboBox mLay = Layer(p.MalhaLayer);
        Linha(gm, "Intervalo fixo (m)", mM, "Ex.: 100 = linhas a cada 100 m em todas as viewports. Vazio = automatico.");
        Linha(gm, "Automatico: distancia no papel (mm)", mMm, "Usado quando o intervalo fixo esta vazio: o intervalo sai redondo (1, 2, 5 x 10^n m) perto dessa distancia no papel.");
        Linha(gm, "Altura do texto E= / N= (mm)", mTxt);
        Linha(gm, "", mCruz);
        Linha(gm, "Transparencia das linhas (0 a 90)", mTr, "Os textos E= / N= ficam sem transparencia.");
        Linha(gm, "Layer", mLay);

        WC.Grid gn = Grupo("Norte", out WC.CheckBox nAtivo, p.Norte);
        WC.TextBox nTam = Num(p.NorteTamanho, "0.#");
        WC.ComboBox nCanto = Canto(p.NortePos.Canto);
        WC.StackPanel nDes = Desloc(p.NortePos, out WC.TextBox nDx, out WC.TextBox nDy);
        WC.ComboBox nLay = Layer(p.NorteLayer);
        Linha(gn, "Altura do norte (mm)", nTam);
        Linha(gn, "Canto da viewport", nCanto);
        Linha(gn, "Deslocamento do canto (mm)", nDes);
        Linha(gn, "Layer", nLay);

        WC.Grid ge = Grupo("Escala grafica", out WC.CheckBox eAtivo, p.Escala);
        WC.TextBox eM = Auto(p.EscalaM);
        WC.TextBox eMm = Num(p.EscalaMm, "0.#");
        WC.TextBox eTxt = Num(p.EscalaTexto);
        WC.ComboBox eCanto = Canto(p.EscalaPos.Canto);
        WC.StackPanel eDes = Desloc(p.EscalaPos, out WC.TextBox eDx, out WC.TextBox eDy);
        WC.ComboBox eLay = Layer(p.EscalaLayer);
        Linha(ge, "Total fixo (m)", eM, "Ex.: 500 = escala grafica de 0 a 500 m em todas as viewports. Vazio = automatico.");
        Linha(ge, "Automatico: comprimento no papel (mm)", eMm, "Usado quando o total fixo esta vazio: o maior total redondo (1; 2; 2,5; 5 x 10^n m) que cabe nesse comprimento.");
        Linha(ge, "Altura do texto (mm)", eTxt);
        Linha(ge, "Canto da viewport", eCanto);
        Linha(ge, "Deslocamento do canto (mm)", eDes);
        Linha(ge, "Layer", eLay);

        var erro = new WC.TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(0, 8, 0, 0) };
        raiz.Children.Add(erro);

        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right, Margin = new SW.Thickness(0, 10, 0, 0) };
        var restaurar = new WC.Button { Content = "Valores iniciais", Padding = new SW.Thickness(8, 2, 8, 2), Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = "Volta os campos aos valores iniciais do plugin (nao salva ate clicar OK)." };
        var ok = new WC.Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new SW.Thickness(0, 0, 6, 0) };
        var cancelar = new WC.Button { Content = "Cancelar", MinWidth = 80, IsCancel = true };
        botoes.Children.Add(restaurar);
        botoes.Children.Add(ok);
        botoes.Children.Add(cancelar);
        raiz.Children.Add(botoes);
        w.Content = new WC.ScrollViewer { Content = raiz, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, MaxHeight = 760 };

        restaurar.Click += (_, _) =>
        {
            var d = new PadraoConjunto();
            mM.Text = "";
            mMm.Text = d.MalhaMm.ToString("0.#", Br);
            mTxt.Text = d.MalhaTexto.ToString("0.##", Br);
            mCruz.IsChecked = d.MalhaCruzes;
            mTr.Text = d.MalhaTransp.ToString("0", Br);
            mLay.Text = d.MalhaLayer;
            nTam.Text = d.NorteTamanho.ToString("0.#", Br);
            nCanto.SelectedIndex = Array.IndexOf(Enum.GetValues(typeof(CantoVp)), d.NortePos.Canto);
            nDx.Text = d.NortePos.Dx.ToString("0.##", Br);
            nDy.Text = d.NortePos.Dy.ToString("0.##", Br);
            nLay.Text = d.NorteLayer;
            eM.Text = "";
            eMm.Text = d.EscalaMm.ToString("0.#", Br);
            eTxt.Text = d.EscalaTexto.ToString("0.##", Br);
            eCanto.SelectedIndex = Array.IndexOf(Enum.GetValues(typeof(CantoVp)), d.EscalaPos.Canto);
            eDx.Text = d.EscalaPos.Dx.ToString("0.##", Br);
            eDy.Text = d.EscalaPos.Dy.ToString("0.##", Br);
            eLay.Text = d.EscalaLayer;
        };

        ok.Click += (_, _) =>
        {
            var falhas = new List<string>();
            double Ler(WC.TextBox tb, string nome, bool vazioZero = false, bool positivo = true, bool permiteNeg = false)
            {
                string s = tb.Text.Trim();
                if (s.Length == 0 && vazioZero)
                    return 0;
                if (double.TryParse(s.Replace('.', ','), NumberStyles.Float, Br, out double v) && (permiteNeg || (positivo ? v > 0 : v >= 0)))
                    return v;
                falhas.Add(nome);
                return 0;
            }
            CantoVp CantoDe(WC.ComboBox c) => c.SelectedItem is WC.ComboBoxItem { Tag: CantoVp k } ? k : CantoVp.IE;

            var n = new PadraoConjunto
            {
                Malha = mAtivo.IsChecked == true,
                MalhaM = Ler(mM, "intervalo da malha", vazioZero: true, positivo: false),
                MalhaMm = Ler(mMm, "distancia da malha no papel"),
                MalhaTexto = Ler(mTxt, "texto da malha"),
                MalhaCruzes = mCruz.IsChecked == true,
                MalhaTransp = Math.Min(90, Ler(mTr, "transparencia da malha", positivo: false)),
                MalhaLayer = NomeLayer(mLay.Text, PadraoConjunto.LayerMalha),
                Norte = nAtivo.IsChecked == true,
                NorteTamanho = Ler(nTam, "altura do norte"),
                NortePos = new Ancora(CantoDe(nCanto), Ler(nDx, "deslocamento X do norte", permiteNeg: true), Ler(nDy, "deslocamento Y do norte", permiteNeg: true)),
                NorteLayer = NomeLayer(nLay.Text, PadraoConjunto.LayerNorte),
                Escala = eAtivo.IsChecked == true,
                EscalaM = Ler(eM, "total da escala grafica", vazioZero: true, positivo: false),
                EscalaMm = Ler(eMm, "comprimento da escala no papel"),
                EscalaTexto = Ler(eTxt, "texto da escala grafica"),
                EscalaPos = new Ancora(CantoDe(eCanto), Ler(eDx, "deslocamento X da escala", permiteNeg: true), Ler(eDy, "deslocamento Y da escala", permiteNeg: true)),
                EscalaLayer = NomeLayer(eLay.Text, PadraoConjunto.LayerEscala)
            };
            if (falhas.Count > 0)
            {
                erro.Text = "Valor invalido: " + string.Join(", ", falhas) + ".";
                return;
            }
            Copiar(n, p);
            p.Salvar();
            w.DialogResult = true;
        };

        bool? r = dono is null ? AcApp.ShowModalWindow(w) : w.ShowDialog();
        return r == true;
    }

    private static string NomeLayer(string texto, string padrao)
    {
        string s = texto.Trim();
        if (s.Length == 0)
            return padrao;
        foreach (char c in "<>/\\\":;?*|,=`")
            s = s.Replace(c, '_');
        return s;
    }

    private static void Copiar(PadraoConjunto de, PadraoConjunto para)
    {
        para.Malha = de.Malha;
        para.MalhaM = de.MalhaM;
        para.MalhaMm = de.MalhaMm;
        para.MalhaTexto = de.MalhaTexto;
        para.MalhaCruzes = de.MalhaCruzes;
        para.MalhaTransp = de.MalhaTransp;
        para.MalhaLayer = de.MalhaLayer;
        para.Norte = de.Norte;
        para.NorteTamanho = de.NorteTamanho;
        para.NortePos = de.NortePos;
        para.NorteLayer = de.NorteLayer;
        para.Escala = de.Escala;
        para.EscalaM = de.EscalaM;
        para.EscalaMm = de.EscalaMm;
        para.EscalaTexto = de.EscalaTexto;
        para.EscalaPos = de.EscalaPos;
        para.EscalaLayer = de.EscalaLayer;
    }

    /// <summary>Layers do desenho (sem as de xref), para a lista.</summary>
    private static List<string> Layers(Database? db)
    {
        var nomes = new List<string> { PadraoConjunto.LayerMalha, PadraoConjunto.LayerNorte, PadraoConjunto.LayerEscala };
        if (db is null)
            return nomes;
        try
        {
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!l.IsDependent)
                    nomes.Add(l.Name);
            }
            tr.Commit();
        }
        catch (System.Exception)
        {
        }
        return nomes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
