using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.UI.Common;

using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Categoria "Pranchas do eixo" do explorador (C3DPRANCHAS): assistente em quatro passos
/// (layout modelo, eixo, perfil, pranchas), lista das folhas e aba da folha selecionada.
/// </summary>
internal sealed partial class LayoutPanel
{
    private static readonly CultureInfo InvPP = CultureInfo.InvariantCulture;

    private List<SheetSeries> _series = new();
    private List<SheetFrameInfo> _framesPP = new();
    private List<(ObjectId Id, string Nome, double Ini, double Fim)> _eixos = new();
    private List<(ObjectId Id, string Nome)>? _estilosPv;
    private List<(ObjectId Id, string Nome)>? _bandasPv;
    private SheetSeries? _serie;
    private SheetFrameInfo? _frameSel;

    // ------------------------------------------------------------------
    // Dados
    // ------------------------------------------------------------------

    private void LerPranchas(Database db)
    {
        _series = LayoutSheetSeries.LerSeries(db);
        _framesPP = _series.SelectMany(s => LayoutSheetSeries.LerFrames(db, s)).ToList();
        _eixos = LayoutSheetSeries.Eixos(db);
        _estilosPv = null;
        _bandasPv = null;
        SincronizarSerie();
        if (_frameSel is not null)
            _frameSel = _framesPP.FirstOrDefault(f => f.Id == _frameSel.Id);
    }

    /// <summary>Mantem as opcoes que o usuario esta editando, mas traz frames e calibracao gravados.</summary>
    private void SincronizarSerie()
    {
        if (_serie is not null && !_eixos.Any(e => e.Id == _serie.Eixo))
            _serie = null;
        if (_serie is null)
        {
            _serie = _series.FirstOrDefault() ?? (_eixos.Count > 0 ? NovaSerie(_eixos[0].Id, null) : null);
            return;
        }
        SheetSeries? gravada = _series.FirstOrDefault(s => s.Eixo == _serie.Eixo);
        _serie.Frames.Clear();
        _serie.Cortes.Clear();
        if (gravada is null)
        {
            // Serie removida: a calibracao era do estilo antigo.
            _serie.MargemPerfil = double.NaN;
            _serie.SobraVertical = double.NaN;
            _serie.Exagero = double.NaN;
            return;
        }
        _serie.Frames.AddRange(gravada.Frames);
        _serie.Cortes.AddRange(gravada.Cortes);
        _serie.MargemPerfil = gravada.MargemPerfil;
        _serie.SobraVertical = gravada.SobraVertical;
        _serie.Exagero = gravada.Exagero;
        _serie.EixoNome = gravada.EixoNome;
    }

    private SheetSeries NovaSerie(ObjectId eixo, SheetSeries? baseSerie)
    {
        var s = new SheetSeries { Eixo = eixo, EixoNome = _eixos.FirstOrDefault(e => e.Id == eixo).Nome ?? "" };
        if (baseSerie is not null)
        {
            s.Modelo = baseSerie.Modelo;
            s.VpPlanta = baseSerie.VpPlanta;
            s.VpPerfil = baseSerie.VpPerfil;
            s.Arredondar = baseSerie.Arredondar;
            s.Folga = baseSerie.Folga;
            s.Girar = baseSerie.Girar;
            s.Prefixo = baseSerie.Prefixo;
            s.Digitos = baseSerie.Digitos;
            s.EstiloPerfil = baseSerie.EstiloPerfil;
            s.BandasPerfil = baseSerie.BandasPerfil;
            s.AjustarCotas = baseSerie.AjustarCotas;
            s.Automatico = baseSerie.Automatico;
            s.Numerar = baseSerie.Numerar;
            // Continua a numeracao de outra serie com o mesmo prefixo.
            int depois = _series.Where(x => x.Prefixo == s.Prefixo)
                .Select(x => x.Inicio + _framesPP.Count(f => f.Eixo == x.Eixo)).DefaultIfEmpty(1).Max();
            s.Inicio = Math.Max(1, depois);
            return s;
        }

        s.Prefixo = LayoutSettings.Get("pp_prefixo", "PP-");
        s.Digitos = LayoutSettings.GetInt("pp_digitos", 2);
        s.Arredondar = LayoutSettings.GetDouble("pp_arred", 20);
        s.Folga = LayoutSettings.GetDouble("pp_folga", 10);
        s.Girar = LayoutSettings.GetBool("pp_girar", true);
        s.AjustarCotas = LayoutSettings.GetBool("pp_cotas", true);
        s.Automatico = LayoutSettings.GetBool("pp_auto", true);
        s.Numerar = LayoutSettings.GetBool("pp_numerar", true);
        string modelo = LayoutSettings.Get("pp_modelo", "");
        SheetData? f = _folhas.FirstOrDefault(x => x.Nome.Equals(modelo, StringComparison.OrdinalIgnoreCase))
                       ?? _folhas.FirstOrDefault(x => x.Viewports.Count(v => v.Planta) >= 2);
        if (f is not null)
            EscolherModelo(s, f);
        return s;
    }

    private void EscolherModelo(SheetSeries s, SheetData folha)
    {
        s.Modelo = folha.Nome;
        if (Doc is not Document doc)
            return;
        (ObjectId planta, ObjectId perfil) = LayoutSheetSeries.Palpite(doc.Database, folha);
        s.VpPlanta = planta;
        s.VpPerfil = perfil;
    }

    private void SalvarPreferenciasPP(SheetSeries s)
    {
        LayoutSettings.Set("pp_prefixo", s.Prefixo);
        LayoutSettings.Set("pp_digitos", s.Digitos);
        LayoutSettings.Set("pp_arred", s.Arredondar);
        LayoutSettings.Set("pp_folga", s.Folga);
        LayoutSettings.Set("pp_girar", s.Girar);
        LayoutSettings.Set("pp_cotas", s.AjustarCotas);
        LayoutSettings.Set("pp_auto", s.Automatico);
        LayoutSettings.Set("pp_numerar", s.Numerar);
        LayoutSettings.Set("pp_modelo", s.Modelo);
        LayoutSettings.Set("pp_estilo", _estilosPv?.FirstOrDefault(e => e.Id == s.EstiloPerfil).Nome ?? "");
        LayoutSettings.Set("pp_bandas", _bandasPv?.FirstOrDefault(e => e.Id == s.BandasPerfil).Nome ?? "");
    }

    private static Point3d? BasePerfis()
    {
        string[] p = LayoutSettings.Get("pp_base", "").Split(';');
        if (p.Length == 2 &&
            double.TryParse(p[0], NumberStyles.Float, InvPP, out double x) &&
            double.TryParse(p[1], NumberStyles.Float, InvPP, out double y))
            return new Point3d(x, y, 0);
        return null;
    }

    /// <summary>Rotulo da viewport no modelo ("PLANTA"/"PERFIL") para a vista grafica.</summary>
    private string? PapelNoModelo(SheetData folha, SheetViewport vp)
    {
        if (_serie is null || !folha.Nome.Equals(_serie.Modelo, StringComparison.OrdinalIgnoreCase))
            return null;
        if (vp.Id == _serie.VpPlanta)
            return "PLANTA";
        if (vp.Id == _serie.VpPerfil)
            return "PERFIL";
        return null;
    }

    /// <summary>Botao direito na viewport: usar como planta/perfil do modelo.</summary>
    private void DefinirModelo(SheetData folha, SheetViewport vp, bool planta)
    {
        if (_eixos.Count == 0)
        {
            Say("O desenho nao tem alinhamentos do Civil 3D.");
            return;
        }
        _serie ??= NovaSerie(_eixos[0].Id, null);
        if (!folha.Nome.Equals(_serie.Modelo, StringComparison.OrdinalIgnoreCase))
            EscolherModelo(_serie, folha);
        if (planta)
        {
            if (_serie.VpPerfil == vp.Id)
                _serie.VpPerfil = _serie.VpPlanta;
            _serie.VpPlanta = vp.Id;
        }
        else
        {
            if (_serie.VpPlanta == vp.Id)
                _serie.VpPlanta = _serie.VpPerfil;
            _serie.VpPerfil = vp.Id;
        }
        MostrarFolha(folha);
        RenderGraphic();
        SelectCategory("PRANCHAS", "Gerar");
        Say($"Modelo: {folha.Nome}, viewport #{vp.Indice} como {(planta ? "planta" : "perfil")}.");
    }

    // ------------------------------------------------------------------
    // Aba Gerar (assistente)
    // ------------------------------------------------------------------

    private SW.UIElement BuildSheetSeries()
    {
        WC.StackPanel p = Form();
        Database? db = Doc?.Database;
        p.Children.Add(Label("Pranchas planta e perfil ao longo do eixo", true));
        if (db is null)
        {
            p.Children.Add(Label("Nenhum desenho aberto."));
            return p;
        }
        if (_eixos.Count == 0)
        {
            p.Children.Add(Label("O desenho nao tem alinhamentos do Civil 3D."));
            return p;
        }
        _serie ??= NovaSerie(_eixos[0].Id, null);
        SheetSeries s = _serie;
        _estilosPv ??= LayoutSheetSeries.EstilosPerfil(db);
        _bandasPv ??= LayoutSheetSeries.BandasPerfil(db);
        if (s.EstiloPerfil.IsNull)
            s.EstiloPerfil = PorNome(_estilosPv, LayoutSettings.Get("pp_estilo", ""));
        if (s.BandasPerfil.IsNull)
            s.BandasPerfil = PorNome(_bandasPv, LayoutSettings.Get("pp_bandas", ""));

        List<SheetFrameInfo> frames = _framesPP.Where(f => f.Eixo == s.Eixo).OrderBy(f => f.E0).ToList();
        bool temSerie = frames.Count > 0;
        int semPrancha = frames.Count(f => !LayoutSheetSeries.Valido(f.Layout) || (s.ComPerfil && !LayoutSheetSeries.Valido(f.Perfil)));

        p.Children.Add(Label("Frames no tamanho da viewport de planta, uma prancha por frame (copia do layout modelo) e um perfil por folha no tamanho da viewport de perfil. " +
                             "Depois, mover, girar ou esticar um frame no Model (ou editar o eixo) atualiza a prancha e o intervalo do perfil."));
        if (temSerie)
        {
            string nomes = frames.Any(f => f.LayoutNome.Length > 0)
                ? $" ({frames.First(f => f.LayoutNome.Length > 0).LayoutNome} a {frames.Last(f => f.LayoutNome.Length > 0).LayoutNome})" : "";
            p.Children.Add(Destaque($"Serie de {s.EixoNome}: {frames.Count} folha(s){nomes}" +
                                    (semPrancha > 0 ? $", {semPrancha} sem prancha ou sem perfil" : "") +
                                    $". {(s.Automatico ? "Atualiza sozinha ao mover os frames." : "Atualizacao manual.")}"));
        }

        // ---- 1. Layout modelo -------------------------------------------------
        SheetTemplate? modelo = null;
        string? erroModelo = null;
        try
        {
            modelo = LayoutSheetSeries.LerModelo(db, s);
        }
        catch (System.Exception ex)
        {
            erroModelo = ex.Message;
        }
        p.Children.Add(Passo(1, "Layout modelo", modelo is not null));
        var cbModelo = new WC.ComboBox { Width = 240, HorizontalAlignment = SW.HorizontalAlignment.Left };
        foreach (SheetData f in _folhas)
            cbModelo.Items.Add(new WC.ComboBoxItem { Content = $"{f.Nome}   ({f.Viewports.Count} viewport(s))", Tag = f });
        cbModelo.SelectedItem = cbModelo.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => ((SheetData)i.Tag!).Nome.Equals(s.Modelo, StringComparison.OrdinalIgnoreCase));
        cbModelo.SelectionChanged += (_, _) =>
        {
            if ((cbModelo.SelectedItem as WC.ComboBoxItem)?.Tag is not SheetData f)
                return;
            EscolherModelo(s, f);
            _todos = false;
            _folhaAtual = null;
            MostrarFolha(f);
            BuildTabs();
        };
        p.Children.Add(Linha(Campo("Layout com carimbo, viewport de planta e (opcional) de perfil", cbModelo)));

        SheetData? folhaModelo = _folhas.FirstOrDefault(f => f.Nome.Equals(s.Modelo, StringComparison.OrdinalIgnoreCase));
        if (modelo is not null)
        {
            p.Children.Add(Prop("Planta", $"viewport #{IndiceVp(folhaModelo, modelo.VpPlanta)}  {modelo.WPlanta:0} x {modelo.HPlanta:0} mm, escala {EscalaTexto(modelo.EscalaPlanta)}  ->  frame {modelo.LarguraFrame:0.#} x {modelo.AlturaFrame:0.#} m"));
            p.Children.Add(Prop("Perfil", modelo.TemPerfil
                ? $"viewport #{IndiceVp(folhaModelo, modelo.VpPerfil)}  {modelo.WPerfil:0} x {modelo.HPerfil:0} mm, escala {EscalaTexto(modelo.EscalaPerfil)}  ->  {modelo.LarguraPerfil:0.#} x {modelo.AlturaPerfil:0.#} m"
                : "sem perfil (so planta)"));
        }
        else if (erroModelo is not null)
        {
            p.Children.Add(Aviso(erroModelo));
        }
        var b1 = new WC.WrapPanel();
        b1.Children.Add(Botao("Planta = viewport marcada", () => UsarMarcada(planta: true)));
        b1.Children.Add(Botao("Perfil = viewport marcada", () => UsarMarcada(planta: false)));
        b1.Children.Add(Botao("Sem perfil", () => { s.VpPerfil = ObjectId.Null; RenderGraphic(); BuildTabs(); }));
        p.Children.Add(b1);
        p.Children.Add(Label("Na vista acima o modelo mostra PLANTA e PERFIL nas viewports. Para trocar: clique numa viewport e use os botoes, ou botao direito nela."));

        // ---- 2. Eixo e cortes -------------------------------------------------
        p.Children.Add(Passo(2, "Eixo e cortes", !s.Eixo.IsNull));
        var cbEixo = new WC.ComboBox { Width = 300, HorizontalAlignment = SW.HorizontalAlignment.Left };
        foreach ((ObjectId id, string nome, double ini, double fim) in _eixos)
        {
            bool comSerie = _series.Any(x => x.Eixo == id);
            cbEixo.Items.Add(new WC.ComboBoxItem
            {
                Content = $"{nome}   ({LayoutViewFrames.Estaca(ini)} a {LayoutViewFrames.Estaca(fim)})" + (comSerie ? "   - tem serie" : ""),
                Tag = id
            });
        }
        cbEixo.SelectedItem = cbEixo.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => (ObjectId)i.Tag! == s.Eixo);
        cbEixo.SelectionChanged += (_, _) =>
        {
            if ((cbEixo.SelectedItem as WC.ComboBoxItem)?.Tag is not ObjectId id || id == s.Eixo)
                return;
            SheetSeries? gravada = _series.FirstOrDefault(x => x.Eixo == id);
            _serie = gravada ?? NovaSerie(id, s);
            RenderGraphic();
            BuildTabs();
        };
        var eixoAtual = _eixos.FirstOrDefault(e => e.Id == s.Eixo);
        var tbIni = Caixa(double.IsNaN(s.EstacaInicial) ? "" : LayoutViewFrames.Estaca(s.EstacaInicial), 90);
        var tbFim = Caixa(double.IsNaN(s.EstacaFinal) ? "" : LayoutViewFrames.Estaca(s.EstacaFinal), 90);
        tbIni.ToolTip = $"Vazio = inicio do eixo ({LayoutViewFrames.Estaca(eixoAtual.Ini)}). Formato 17+10,00 ou metros.";
        tbFim.ToolTip = $"Vazio = fim do eixo ({LayoutViewFrames.Estaca(eixoAtual.Fim)}).";
        var tbArred = Caixa(s.Arredondar.ToString("0.##", InvPP), 60);
        tbArred.ToolTip = "Cada corte cai numa estaca multipla deste valor (20 = estaca inteira). 0 = sem arredondar.";
        var tbFolga = Caixa(s.Folga.ToString("0.##", InvPP), 60);
        tbFolga.ToolTip = "Quanto do desenho aparece alem da linha de corte, em cada ponta e dos lados.";
        p.Children.Add(Linha(Campo("Eixo", cbEixo)));
        p.Children.Add(Linha(Campo("Estaca inicial", tbIni), Campo("Estaca final", tbFim), Campo("Cortes em multiplos de (m)", tbArred), Campo("Folga alem do corte (m)", tbFolga)));
        var rbGirar = new WC.RadioButton { Content = "Girar a planta pelo eixo (eixo na horizontal da prancha)", IsChecked = s.Girar, Margin = new SW.Thickness(0, 4, 0, 0) };
        var rbNorte = new WC.RadioButton { Content = "Norte para cima (sem giro)", IsChecked = !s.Girar, Margin = new SW.Thickness(0, 2, 0, 0) };
        p.Children.Add(rbGirar);
        p.Children.Add(rbNorte);

        // ---- 3. Perfil ----------------------------------------------------------
        bool comPerfil = s.ComPerfil;
        p.Children.Add(Passo(3, "Perfil longitudinal", comPerfil ? !s.EstiloPerfil.IsNull : true, comPerfil ? null : "sem viewport de perfil no modelo"));
        var cbEstilo = ComboEstilos(_estilosPv, s.EstiloPerfil, id => s.EstiloPerfil = id);
        var cbBandas = ComboEstilos(_bandasPv, s.BandasPerfil, id => s.BandasPerfil = id);
        var chCotas = new WC.CheckBox { Content = "Ajustar as cotas para o perfil encher a altura da viewport", IsChecked = s.AjustarCotas, Margin = new SW.Thickness(0, 6, 0, 0) };
        chCotas.Click += (_, _) => s.AjustarCotas = chCotas.IsChecked == true;
        cbEstilo.IsEnabled = cbBandas.IsEnabled = chCotas.IsEnabled = comPerfil;
        p.Children.Add(Linha(Campo("Estilo do profile view", cbEstilo), Campo("Band set", cbBandas)));
        p.Children.Add(chCotas);
        Point3d? basePt = BasePerfis();
        var lbBase = new WC.TextBlock
        {
            Foreground = T.Muted,
            Margin = new SW.Thickness(0, 6, 0, 0),
            TextWrapping = SW.TextWrapping.Wrap,
            Text = basePt is Point3d bp
                ? $"Perfis no Model a partir de {bp.X:0.##}, {bp.Y:0.##} (empilhados para baixo)."
                : "Perfis no Model: abaixo de tudo o que ja esta desenhado (empilhados). Pode move-los depois; a prancha acompanha."
        };
        p.Children.Add(lbBase);
        var b3 = new WC.WrapPanel();
        b3.Children.Add(Botao("Escolher ponto dos perfis...", () => Enviar(C3DCommands.Layouts.Pranchas + " PontoPerfis")));
        if (basePt is not null)
            b3.Children.Add(Botao("Posicao automatica", () => { LayoutSettings.Set("pp_base", ""); BuildTabs(); }));
        b3.IsEnabled = comPerfil;
        p.Children.Add(b3);

        // ---- 4. Pranchas --------------------------------------------------------
        p.Children.Add(Passo(4, "Pranchas", s.Prefixo.Length > 0 || s.Digitos > 0));
        var tbPref = Caixa(s.Prefixo, 80);
        var tbIniN = Caixa(s.Inicio.ToString(InvPP), 50);
        var tbDig = Caixa(s.Digitos.ToString(InvPP), 40);
        var lbNomes = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Bottom, Margin = new SW.Thickness(0, 0, 0, 4) };
        p.Children.Add(Linha(Campo("Nome do layout", tbPref), Campo("Primeiro numero", tbIniN), Campo("Digitos", tbDig), lbNomes));
        var chNum = new WC.CheckBox { Content = "Numerar pelos textos marcados (categoria Numeracao), numero e total da serie", IsChecked = s.Numerar, Margin = new SW.Thickness(0, 4, 0, 0) };
        chNum.Click += (_, _) => s.Numerar = chNum.IsChecked == true;
        var chAuto = new WC.CheckBox { Content = "Atualizar sozinho ao mover, girar ou esticar um frame, ou ao editar o eixo", IsChecked = s.Automatico, Margin = new SW.Thickness(0, 2, 0, 0) };
        chAuto.Click += (_, _) =>
        {
            s.Automatico = chAuto.IsChecked == true;
            if (!_series.Any(x => x.Eixo == s.Eixo) || Doc is not Document d)
                return;
            try
            {
                LayoutSheetSeries.Salvar(d, s);
                Say(s.Automatico ? "Atualizacao automatica ligada." : "Atualizacao automatica desligada.");
            }
            catch (System.Exception ex)
            {
                Say("Nao foi possivel gravar: " + ex.Message);
            }
        };
        p.Children.Add(chNum);
        p.Children.Add(chAuto);

        // ---- Resumo e acoes -----------------------------------------------------
        var resumo = new WC.TextBlock { TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text };
        var caixaResumo = new WC.Border
        {
            BorderBrush = T.Accent,
            BorderThickness = new SW.Thickness(2, 0, 0, 0),
            Background = T.Hover,
            Padding = new SW.Thickness(10, 6, 10, 6),
            Margin = new SW.Thickness(0, 12, 0, 0),
            Child = resumo
        };
        p.Children.Add(caixaResumo);

        void Ler()
        {
            var invalidos = new List<string>();
            if (tbIni.Text.Trim().Length == 0)
                s.EstacaInicial = double.NaN;
            else if (LayoutSheetSeries.TentarEstaca(tbIni.Text, out double a))
                s.EstacaInicial = a;
            else
                invalidos.Add("estaca inicial");
            if (tbFim.Text.Trim().Length == 0)
                s.EstacaFinal = double.NaN;
            else if (LayoutSheetSeries.TentarEstaca(tbFim.Text, out double b))
                s.EstacaFinal = b;
            else
                invalidos.Add("estaca final");
            s.Arredondar = Num(tbArred.Text, s.Arredondar);
            s.Folga = Num(tbFolga.Text, s.Folga);
            s.Girar = rbGirar.IsChecked == true;
            s.Prefixo = tbPref.Text;
            s.Inicio = (int)Num(tbIniN.Text, s.Inicio);
            s.Digitos = Math.Clamp((int)Num(tbDig.Text, s.Digitos), 0, 6);
            lbNomes.Text = $"{s.NomeFolha(0)}, {s.NomeFolha(1)}, ...";
            resumo.Text = (invalidos.Count > 0 ? $"Corrija: {string.Join(" e ", invalidos)} (use 17+10,00 ou metros). " : "") + Resumo(db, s, temSerie);
        }
        foreach (WC.TextBox tb in new[] { tbIni, tbFim, tbArred, tbFolga, tbPref, tbIniN, tbDig })
            tb.TextChanged += (_, _) => Ler();
        rbGirar.Click += (_, _) => Ler();
        rbNorte.Click += (_, _) => Ler();
        Ler();

        var acoes = new WC.WrapPanel { Margin = new SW.Thickness(0, 4, 0, 0) };
        if (!temSerie)
        {
            acoes.Children.Add(Principal("Gerar pranchas", () => GerarSerie(pranchas: true)));
            acoes.Children.Add(ActionButton("So os frames (ajustar antes)", () => GerarSerie(pranchas: false)));
        }
        else
        {
            if (semPrancha > 0)
                acoes.Children.Add(Principal($"Completar {semPrancha} folha(s) (prancha/perfil)", () => GerarSerie(pranchas: true)));
            acoes.Children.Add(semPrancha > 0
                ? ActionButton("Atualizar", AtualizarSerie)
                : Principal("Atualizar", AtualizarSerie));
            acoes.Children.Add(ActionButton("Remover serie...", RemoverSerie));
        }
        p.Children.Add(acoes);
        if (temSerie)
            p.Children.Add(Label("Para mudar o tamanho dos frames, a folga ou o trecho: Remover serie e gerar de novo. Para ajustar uma folha: mova/estique o frame no Model ou mude as estacas na aba Folha."));
        return p;
    }

    private string Resumo(Database db, SheetSeries s, bool temSerie)
    {
        SheetCapacity? c = LayoutSheetSeries.Capacidade(db, s);
        if (c is null)
            return "Complete o passo 1 para ver quanto cabe por folha.";
        string planta = $"Planta: ate {c.Planta:0} m por folha";
        string perfil = double.IsInfinity(c.Perfil)
            ? ""
            : $"; perfil: {(c.PerfilCalibrado ? "" : "~")}{c.Perfil:0} m{(c.PerfilCalibrado ? "" : " (medido ao gerar)")}";
        string folhas = temSerie ? "" : $". Trecho de {c.Comprimento:0} m -> cerca de {c.Folhas} folha(s), {s.NomeFolha(0)} a {s.NomeFolha(Math.Max(0, c.Folhas - 1))}";
        string limite = !double.IsInfinity(c.Perfil) && c.Perfil < c.Planta ? ". O perfil limita o comprimento das folhas" : "";
        return planta + perfil + folhas + limite + ".";
    }

    private void UsarMarcada(bool planta)
    {
        if (_serie is null)
            return;
        SheetData? folha = _folhas.FirstOrDefault(f => f.Nome.Equals(_serie.Modelo, StringComparison.OrdinalIgnoreCase)) ?? _folhaAtual;
        List<SheetViewport> marcadas = folha?.Viewports.Where(v => _vpsMarcadas.Contains(v.Id)).ToList() ?? new();
        if (folha is null || marcadas.Count != 1)
        {
            Say("Clique em UMA viewport do layout modelo na vista acima (fica azul) e use o botao de novo.");
            return;
        }
        _vpsMarcadas.Remove(marcadas[0].Id);
        DefinirModelo(folha, marcadas[0], planta);
    }

    private void GerarSerie(bool pranchas)
    {
        if (_serie is null || Doc is not Document doc)
            return;
        SalvarPreferenciasPP(_serie);
        WI.Mouse.OverrideCursor = WI.Cursors.Wait;
        try
        {
            Say(pranchas ? "Gerando frames, pranchas e perfis..." : "Gerando frames...");
            Executar(() => LayoutSheetSeries.Gerar(doc, _serie, pranchas, BasePerfis()));
        }
        finally
        {
            WI.Mouse.OverrideCursor = null;
        }
    }

    private void AtualizarSerie()
    {
        if (_serie is null || Doc is not Document doc)
            return;
        WI.Mouse.OverrideCursor = WI.Cursors.Wait;
        try
        {
            Executar(() =>
            {
                LayoutSheetSeries.Salvar(doc, _serie);
                return LayoutSheetSeries.Atualizar(doc, _serie.Eixo, null);
            });
        }
        finally
        {
            WI.Mouse.OverrideCursor = null;
        }
    }

    private void AtualizarTodasSeries()
    {
        if (Doc is not Document doc)
            return;
        WI.Mouse.OverrideCursor = WI.Cursors.Wait;
        try
        {
            Executar(() => LayoutSheetSeries.AtualizarTodas(doc));
        }
        finally
        {
            WI.Mouse.OverrideCursor = null;
        }
    }

    private void RemoverSerie()
    {
        if (_serie is null || Doc is not Document doc)
            return;
        int n = _framesPP.Count(f => f.Eixo == _serie.Eixo);
        SW.MessageBoxResult r = SW.MessageBox.Show(
            $"Remover a serie de {_serie.EixoNome} ({n} folha(s))?\n\n" +
            "Sim: remove frames, linhas de corte, perfis e as pranchas (layouts) gerados.\n" +
            "Nao: remove so frames e linhas de corte; pranchas e perfis ficam.\n" +
            "Cancelar: nao remove nada.",
            "Remover serie de pranchas", SW.MessageBoxButton.YesNoCancel, SW.MessageBoxImage.Warning);
        if (r == SW.MessageBoxResult.Cancel || r == SW.MessageBoxResult.None)
            return;
        bool tudo = r == SW.MessageBoxResult.Yes;
        ObjectId eixo = _serie.Eixo;
        Executar(() => LayoutSheetSeries.Remover(doc, eixo, tudo));
    }

    // ------------------------------------------------------------------
    // Aba Folha
    // ------------------------------------------------------------------

    private SW.UIElement BuildSheetFrame()
    {
        WC.StackPanel p = Form();
        SheetFrameInfo? f = _frameSel;
        if (f is null)
        {
            p.Children.Add(Label("Selecione uma folha na lista", true));
            p.Children.Add(Label("Aqui da para mudar as estacas de uma folha, dar zoom no frame e abrir a prancha. Duplo clique na lista faz zoom no frame no Model."));
            return p;
        }

        p.Children.Add(Label($"{(f.LayoutNome.Length > 0 ? f.LayoutNome : "(sem prancha)")}   -   {f.EixoNome}", true));
        p.Children.Add(Prop("Estacas", $"{LayoutViewFrames.Estaca(f.E0)} a {LayoutViewFrames.Estaca(f.E1)}  ({f.Extensao:0.##} m)"));
        p.Children.Add(Prop("Prancha", f.LayoutNome.Length > 0 ? f.LayoutNome : "nao criada (Gerar pranchas)"));
        p.Children.Add(Prop("Perfil", f.PerfilNome.Length > 0 ? f.PerfilNome : "-"));
        p.Children.Add(f.Situacao == "ok" ? Prop("Situacao", "ok") : Aviso(f.Situacao));

        p.Children.Add(Label("Mudar as estacas desta folha", true));
        p.Children.Add(Label("Os cortes andam dentro do frame (o frame nao se move); a planta, o perfil e as linhas de corte acompanham. Formato 17+10,00 ou metros."));
        var tb0 = Caixa(LayoutViewFrames.Estaca(f.E0), 100);
        var tb1 = Caixa(LayoutViewFrames.Estaca(f.E1), 100);
        p.Children.Add(Linha(Campo("Estaca inicial", tb0), Campo("Estaca final", tb1)));
        var b = new WC.WrapPanel();
        b.Children.Add(Principal("Aplicar estacas", () =>
        {
            if (!LayoutSheetSeries.TentarEstaca(tb0.Text, out double e0) || !LayoutSheetSeries.TentarEstaca(tb1.Text, out double e1))
            {
                Say("Estacas invalidas: use 17+10,00 ou metros.");
                return;
            }
            Executar(() => LayoutSheetSeries.DefinirEstacas(Doc!, f.Id, e0, e1));
        }));
        b.Children.Add(ActionButton("Zoom no frame", () => ZoomFrame(f)));
        if (f.LayoutNome.Length > 0)
            b.Children.Add(ActionButton("Abrir prancha", () => Abrir(f.LayoutNome)));
        b.Children.Add(ActionButton("Atualizar esta folha", () => Executar(() => LayoutSheetSeries.Atualizar(Doc!, f.Eixo, new[] { f.Id }))));
        p.Children.Add(b);
        return p;
    }

    private void ZoomFrame(SheetFrameInfo? f)
    {
        f ??= _frameSel;
        if (f is null || Doc is not Document doc)
        {
            Say("Selecione uma folha na categoria Pranchas do eixo.");
            return;
        }
        Extents3d e;
        try
        {
            using Transaction tr = doc.Database.TransactionManager.StartTransaction();
            e = ((Entity)tr.GetObject(f.Id, OpenMode.ForRead)).GeometricExtents;
            tr.Commit();
        }
        catch (System.Exception)
        {
            Say("Frame nao encontrado (apagado?). Clique em Atualizar.");
            return;
        }
        // ZOOM le as coordenadas no UCS atual: o centro vai convertido e a janela vira C(entro)+altura.
        double mx = (e.MaxPoint.X - e.MinPoint.X) * 1.2, my = (e.MaxPoint.Y - e.MinPoint.Y) * 1.2;
        var centro = new Point3d((e.MinPoint.X + e.MaxPoint.X) / 2, (e.MinPoint.Y + e.MaxPoint.Y) / 2, 0);
        Point3d c = centro;
        try
        {
            c = centro.TransformBy(doc.Editor.CurrentUserCoordinateSystem.Inverse());
        }
        catch (System.Exception)
        {
            // Sem acesso ao UCS: usa WCS.
        }
        double altura = Math.Max(my, mx * 0.6);
        Enviar($"_.TILEMODE 1 _.ZOOM _C {c.X.ToString("0.###", InvPP)},{c.Y.ToString("0.###", InvPP)} {altura.ToString("0.###", InvPP)}");
    }

    // ------------------------------------------------------------------
    // Pecas de interface
    // ------------------------------------------------------------------

    private SW.UIElement Passo(int n, string titulo, bool ok, string? nota = null)
    {
        var sp = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(0, 14, 0, 2) };
        var bola = new WC.Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new SW.CornerRadius(10),
            Background = ok ? ToolTheme.Brush(60, 150, 90) : T.Accent,
            Child = new WC.TextBlock
            {
                Text = n.ToString(InvPP),
                Foreground = WM.Brushes.White,
                FontWeight = SW.FontWeights.Bold,
                HorizontalAlignment = SW.HorizontalAlignment.Center,
                VerticalAlignment = SW.VerticalAlignment.Center
            },
            Margin = new SW.Thickness(0, 0, 8, 0)
        };
        sp.Children.Add(bola);
        sp.Children.Add(new WC.TextBlock { Text = titulo, FontWeight = SW.FontWeights.Bold, Foreground = T.Text, VerticalAlignment = SW.VerticalAlignment.Center });
        if (nota is not null)
            sp.Children.Add(new WC.TextBlock { Text = "   " + nota, Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center });
        return sp;
    }

    private SW.UIElement Destaque(string texto) => new WC.Border
    {
        Background = T.Selected,
        Padding = new SW.Thickness(8, 4, 8, 4),
        Margin = new SW.Thickness(0, 8, 0, 0),
        Child = new WC.TextBlock { Text = texto, Foreground = T.Text, TextWrapping = SW.TextWrapping.Wrap }
    };

    private SW.UIElement Aviso(string texto) => new WC.TextBlock
    {
        Text = texto,
        Foreground = ToolTheme.Brush(230, 150, 30),
        TextWrapping = SW.TextWrapping.Wrap,
        Margin = new SW.Thickness(0, 4, 0, 2)
    };

    private WC.Button Principal(string texto, Action acao)
    {
        WC.Button b = ActionButton(texto, acao);
        b.FontWeight = SW.FontWeights.Bold;
        b.Background = T.Selected;
        b.BorderBrush = T.Accent;
        return b;
    }

    private WC.Button Botao(string texto, Action acao)
    {
        WC.Button b = ActionButton(texto, acao);
        b.Margin = new SW.Thickness(0, 4, 6, 0);
        b.Padding = new SW.Thickness(8, 2, 8, 2);
        b.MinWidth = 0;
        return b;
    }

    private WC.TextBox Caixa(string texto, double largura) => new() { Text = texto, Width = largura };

    private static SW.UIElement Campo(string rotulo, SW.UIElement controle)
    {
        var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 4, 12, 0) };
        sp.Children.Add(new WC.TextBlock { Text = rotulo, Foreground = ToolTheme.Current.Muted, Margin = new SW.Thickness(0, 0, 0, 2) });
        sp.Children.Add(controle);
        return sp;
    }

    private static SW.UIElement Linha(params SW.UIElement[] itens)
    {
        var wp = new WC.WrapPanel();
        foreach (SW.UIElement i in itens)
            wp.Children.Add(i);
        return wp;
    }

    private WC.ComboBox ComboEstilos(List<(ObjectId Id, string Nome)>? lista, ObjectId atual, Action<ObjectId> escolher)
    {
        var cb = new WC.ComboBox { Width = 220, HorizontalAlignment = SW.HorizontalAlignment.Left };
        foreach ((ObjectId id, string nome) in lista ?? new())
            cb.Items.Add(new WC.ComboBoxItem { Content = nome, Tag = id });
        cb.SelectedItem = cb.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => (ObjectId)i.Tag! == atual) ?? (cb.Items.Count > 0 ? cb.Items[0] : null);
        if (cb.SelectedItem is WC.ComboBoxItem sel)
            escolher((ObjectId)sel.Tag!);
        cb.SelectionChanged += (_, _) =>
        {
            if ((cb.SelectedItem as WC.ComboBoxItem)?.Tag is ObjectId id)
                escolher(id);
        };
        return cb;
    }

    private static ObjectId PorNome(List<(ObjectId Id, string Nome)>? lista, string nome)
    {
        if (lista is null || lista.Count == 0)
            return ObjectId.Null;
        return lista.FirstOrDefault(e => e.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)).Id is ObjectId id && !id.IsNull ? id : lista[0].Id;
    }

    private static int IndiceVp(SheetData? folha, ObjectId id) => folha?.Viewports.FirstOrDefault(v => v.Id == id)?.Indice ?? 0;

    private static double Num(string texto, double padrao) =>
        double.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.Float, InvPP, out double v) ? v : padrao;
}
