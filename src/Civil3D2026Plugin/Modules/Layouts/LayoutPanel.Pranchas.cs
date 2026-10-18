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
    private List<(ObjectId Id, string Nome)>? _labelSetsPv;
    private readonly Dictionary<ObjectId, List<(ObjectId Id, string Nome, string Tipo)>> _perfisCache = new();
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
        _labelSetsPv = null;
        _estacasCache.Clear();
        _perfisCache.Clear();
        SincronizarSerie();
        if (_modeloPendente is not null && _serie is not null &&
            _folhas.FirstOrDefault(f => f.Nome.Equals(_modeloPendente, StringComparison.OrdinalIgnoreCase)) is SheetData importado)
        {
            // Layout importado de DWG/DWT: vira o layout base.
            EscolherModelo(_serie, importado);
            _modeloPendente = null;
            _folhaAtual = importado;
        }
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
        var titulo = Label("Plan & Profile: frames, sheets e profile views", true);
        titulo.ToolTip = Dica("Frames no tamanho da viewport de planta do layout base, uma sheet (copia do layout base) por frame e um profile view por sheet no tamanho da viewport de perfil.",
            "Depois de gerar, mover, girar ou esticar um frame no Model (ou editar o alignment) atualiza a sheet, as estacas e as elevations do profile view e as match lines.");
        p.Children.Add(titulo);
        if (db is null)
        {
            p.Children.Add(Label("Nenhum desenho aberto."));
            return p;
        }
        if (_eixos.Count == 0)
        {
            p.Children.Add(Label("O desenho nao tem alignments do Civil 3D."));
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
        int pendentes = frames.Count(f => !LayoutSheetSeries.Valido(f.Layout) || (s.ComPerfil && !LayoutSheetSeries.Valido(f.Perfil)));
        if (temSerie)
        {
            string nomes = frames.Any(f => f.LayoutNome.Length > 0)
                ? $" ({frames.First(f => f.LayoutNome.Length > 0).LayoutNome} a {frames.Last(f => f.LayoutNome.Length > 0).LayoutNome})" : "";
            p.Children.Add(Destaque($"{s.EixoNome}: {frames.Count} sheet(s){nomes}" +
                                    (pendentes > 0 ? $", {pendentes} pendente(s)" : "") +
                                    $"  |  auto-update {(s.Automatico ? "ligado" : "desligado")}"));
        }

        // ---- 1. Layout base -----------------------------------------------------
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
        p.Children.Add(Passo(1, "Layout base", modelo is not null));
        var cbModelo = new WC.ComboBox { Width = 230, HorizontalAlignment = SW.HorizontalAlignment.Left };
        cbModelo.ToolTip = Dica("Layout com carimbo e as viewports de planta e de perfil ja no tamanho e na escala finais.",
            "Cada sheet e uma copia dele. A vista grafica mostra PLANTA e PERFIL nas viewports escolhidas.");
        foreach (SheetData f in _folhas)
            cbModelo.Items.Add(new WC.ComboBoxItem { Content = $"{f.Nome}   ({f.Viewports.Count} vp)", Tag = f });
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
        var btImportar = Botao("Importar de DWG/DWT...", ImportarModelo);
        btImportar.ToolTip = Dica("Traz um layout de outro desenho ou template, como o \"From Template\" do AutoCAD (LAYOUT > Template).",
            "O layout importado vira o layout base.");
        p.Children.Add(Linha(Campo("Layout base", cbModelo), Embaixo(btImportar)));

        SheetData? folhaModelo = _folhas.FirstOrDefault(f => f.Nome.Equals(s.Modelo, StringComparison.OrdinalIgnoreCase));
        if (modelo is not null)
        {
            const string avisoEscala = "Muda a viewport do layout base. Frames ja gerados nao mudam de tamanho: para usar a escala nova, Remover serie e gerar de novo.";
            var lp = Prop("Viewport planta", $"#{IndiceVp(folhaModelo, modelo.VpPlanta)}  |  {modelo.WPlanta:0} x {modelo.HPlanta:0} mm  |  frame {modelo.LarguraFrame:0.#} x {modelo.AlturaFrame:0.#} m");
            lp.ToolTip = "O frame e o tamanho da viewport dividido pela escala (custom scale).";
            lp.VerticalAlignment = SW.VerticalAlignment.Center;
            lp.Width = 330;
            WC.ComboBox cbEscPl = ComboEscala(modelo.EscalaPlanta, c => EscalarModelo(modelo.VpPlanta, c, "planta", temSerie));
            cbEscPl.ToolTip = Dica("Custom scale da viewport de planta do layout base (1 = 1:1000 num desenho em metros).", avisoEscala);
            p.Children.Add(Linha(lp, cbEscPl));
            var lf = Prop("Viewport perfil", modelo.TemPerfil
                ? $"#{IndiceVp(folhaModelo, modelo.VpPerfil)}  |  {modelo.WPerfil:0} x {modelo.HPerfil:0} mm  |  {modelo.LarguraPerfil:0.#} x {modelo.AlturaPerfil:0.#} m"
                : "nenhuma (so planta)");
            lf.ToolTip = modelo.TemPerfil ? $"A largura (menos {LayoutSheetSeries.MargemPapel:0} mm de cada lado) limita quantos metros de profile view cabem por sheet." : null;
            lf.VerticalAlignment = SW.VerticalAlignment.Center;
            lf.Width = 330;
            if (modelo.TemPerfil)
            {
                WC.ComboBox cbEscPf = ComboEscala(modelo.EscalaPerfil, c => EscalarModelo(modelo.VpPerfil, c, "perfil", temSerie));
                cbEscPf.ToolTip = Dica("Custom scale da viewport de perfil do layout base (escala horizontal do profile view; a vertical vem do exagero do estilo).", avisoEscala);
                p.Children.Add(Linha(lf, cbEscPf));
            }
            else
            {
                p.Children.Add(lf);
            }
        }
        else if (erroModelo is not null)
        {
            p.Children.Add(Aviso(erroModelo));
        }
        var b1 = new WC.WrapPanel();
        var btPl = Botao("Definir planta", () => UsarMarcada(planta: true));
        btPl.ToolTip = "Usa a viewport marcada no layout base (clique nela na vista acima; fica azul). Botao direito na viewport faz o mesmo.";
        var btPf = Botao("Definir perfil", () => UsarMarcada(planta: false));
        btPf.ToolTip = btPl.ToolTip;
        var btSp = Botao("Sem perfil", () => { s.VpPerfil = ObjectId.Null; RenderGraphic(); BuildTabs(); });
        btSp.ToolTip = "Sheets so de planta: nao cria profile views.";
        b1.Children.Add(btPl);
        b1.Children.Add(btPf);
        b1.Children.Add(btSp);
        p.Children.Add(b1);

        const string doBase = "(do layout base)";
        WC.ComboBox ComboNomes(IEnumerable<string> nomes, string atual, Action<string> definir, string dica)
        {
            var cb = new WC.ComboBox { Width = 170, HorizontalAlignment = SW.HorizontalAlignment.Left, ToolTip = dica };
            cb.Items.Add(doBase);
            foreach (string n in nomes)
                cb.Items.Add(n);
            cb.SelectedItem = atual.Length > 0 && cb.Items.Contains(atual) ? atual : doBase;
            cb.SelectionChanged += (_, _) => definir(cb.SelectedItem as string is string x && x != doBase ? x : "");
            return cb;
        }
        var cbLsPl = ComboNomes(_states.Select(x => x.Nome), s.LayerStatePlanta, v => s.LayerStatePlanta = v,
            "Layer state aplicado como override na viewport de planta de cada sheet. \"(do layout base)\" copia os overrides e o VP freeze da viewport do layout base.");
        var cbLsPf = ComboNomes(_states.Select(x => x.Nome), s.LayerStatePerfil, v => s.LayerStatePerfil = v,
            "Layer state aplicado como override na viewport de perfil de cada sheet. \"(do layout base)\" copia os overrides e o VP freeze da viewport do layout base.");
        var cbPs = ComboNomes(_pageSetups.Select(x => x.Nome), s.PageSetup, v => s.PageSetup = v,
            "Page setup das sheets. \"(do layout base)\" copia plotter, papel, CTB e area de plotagem do layout base.");
        cbLsPf.IsEnabled = s.ComPerfil;
        p.Children.Add(Linha(Campo("Layer state planta", cbLsPl), Campo("Layer state perfil", cbLsPf), Campo("Page setup", cbPs)));

        // ---- 2. Alignment e frames ----------------------------------------------
        p.Children.Add(Passo(2, "Alignment e frames", !s.Eixo.IsNull));
        var cbEixo = new WC.ComboBox { Width = 240, HorizontalAlignment = SW.HorizontalAlignment.Left, ToolTip = "Alignment das sheets. Uma serie por alignment; o que ja tem serie aparece marcado." };
        foreach ((ObjectId id, string nome, double ini, double fim) in _eixos)
        {
            bool comSerie = _series.Any(x => x.Eixo == id);
            cbEixo.Items.Add(new WC.ComboBoxItem
            {
                Content = nome + (comSerie ? "   [serie]" : ""),
                ToolTip = $"{LayoutSheetSeries.Estaca(ini)} a {LayoutSheetSeries.Estaca(fim)}",
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
        List<(double Estaca, string Rotulo)> estacas = EstacasCache(db, s.Eixo);
        WC.ComboBox cbIni = ComboEstacas(estacas, s.EstacaInicial, inicio: true);
        WC.ComboBox cbFim = ComboEstacas(estacas, s.EstacaFinal, inicio: false);
        cbIni.ToolTip = Dica("Estaca inicial do trecho. Lista: estacas inteiras e pontos notaveis (PC, PT, TS, SC, CS, ST).", "Tambem aceita digitar: 17+10,00 ou metros.");
        cbFim.ToolTip = Dica("Estaca final do trecho.", "Tambem aceita digitar: 17+10,00 ou metros.");
        p.Children.Add(Linha(Campo("Alignment", cbEixo), Campo("Estaca inicial", cbIni), Campo("Estaca final", cbFim)));

        var tbArred = Caixa(s.Arredondar.ToString("0.##", InvPP), 55);
        tbArred.ToolTip = "As match lines caem em estacas multiplas deste valor (20 = estaca inteira). 0 = sem arredondar.";
        var tbFolga = Caixa(s.Folga.ToString("0.##", InvPP), 55);
        tbFolga.ToolTip = "Overlap: quanto do desenho aparece alem da match line, em cada ponta e nos lados do frame (m).";
        var cbGiro = Opcoes(new[] { "Alinhada ao alignment", "Norte (0 graus)" }, s.Girar ? 0 : 1,
            "Alinhada: o alignment fica na horizontal da sheet (viewport girada). Norte: sem giro.");
        var cbUltima = Opcoes(new[] { "Cheia (overlap com a anterior)", "Curta (so o restante)" }, s.UltimaCheia ? 0 : 1,
            "Cheia: a ultima sheet e contada do fim do alignment para tras e repete o fim da anterior; a match line continua no fim da penultima.");
        p.Children.Add(Linha(Campo("Match lines (m)", tbArred), Campo("Overlap (m)", tbFolga), Campo("Rotacao do frame", cbGiro), Campo("Ultima sheet", cbUltima)));

        // ---- 3. Profile view ------------------------------------------------------
        bool comPerfil = s.ComPerfil;
        p.Children.Add(Passo(3, "Profile view", comPerfil ? !s.EstiloPerfil.IsNull && !s.BandasPerfil.IsNull : true, comPerfil ? null : "sem viewport de perfil"));
        var cbEstilo = ComboEstilos(_estilosPv, s.EstiloPerfil, id => s.EstiloPerfil = id);
        cbEstilo.ToolTip = "Profile view style (define o exagero vertical e a grade).";
        var cbBandas = ComboEstilos(_bandasPv, s.BandasPerfil, id => s.BandasPerfil = id);
        cbBandas.ToolTip = "Band set do profile view (bandas de estaca, cotas, etc.).";
        var cbCotas = Opcoes(new[] { "Ajustar a viewport", "Automaticas (Civil)" }, s.AjustarCotas ? 0 : 1,
            "Ajustar: abre a faixa de elevations ate o profile view encher a altura da viewport de perfil (avisa se nao couber). Automaticas: o Civil decide.");
        var cbDisp = Opcoes(new[] { "Empilhados (vertical)", "Lado a lado (horizontal)" }, s.PerfisHorizontal ? 1 : 0,
            "Como os profile views ficam no Model: um embaixo do outro ou um ao lado do outro, na ordem das sheets.");
        cbEstilo.IsEnabled = cbBandas.IsEnabled = cbCotas.IsEnabled = cbDisp.IsEnabled = comPerfil;
        p.Children.Add(Linha(Campo("Profile view style", cbEstilo), Campo("Band set", cbBandas)));
        p.Children.Add(Linha(Campo("Elevations", cbCotas), Campo("Disposicao no Model", cbDisp)));
        if (comPerfil && (_estilosPv.Count == 0 || _bandasPv.Count == 0))
            p.Children.Add(Aviso("Nao foi possivel ler os estilos de profile view do desenho" + (LayoutSheetSeries.ErroEstilos.Length > 0 ? $": {LayoutSheetSeries.ErroEstilos}" : ".")));
        Point3d? basePt = BasePerfis();
        var lbBase = Prop("Insercao", basePt is Point3d bp ? $"a partir de {bp.X:0.##}, {bp.Y:0.##}" : "automatica (abaixo do desenho)");
        lbBase.ToolTip = "Origem do primeiro profile view no Model. Os profile views podem ser movidos depois; a viewport acompanha ao Atualizar.";
        var b3 = new WC.WrapPanel();
        var btPonto = Botao("Escolher ponto...", () => Enviar(C3DCommands.Layouts.Pranchas + " PontoPerfis"));
        btPonto.ToolTip = "Clica a origem do primeiro profile view no Model (C3DPRANCHAS > PontoPerfis).";
        b3.Children.Add(lbBase);
        b3.Children.Add(btPonto);
        if (basePt is not null)
            b3.Children.Add(Botao("Automatica", () => { LayoutSettings.Set("pp_base", ""); BuildTabs(); }));
        b3.IsEnabled = comPerfil;
        p.Children.Add(b3);
        SW.UIElement opcoesPerfis = OpcoesDosPerfis(db, s, temSerie);
        opcoesPerfis.IsEnabled = comPerfil;
        p.Children.Add(opcoesPerfis);

        // ---- 4. Sheets ----------------------------------------------------------
        p.Children.Add(Passo(4, "Sheets", s.Prefixo.Length > 0 || s.Digitos > 0));
        var tbPref = Caixa(s.Prefixo, 70);
        tbPref.ToolTip = "Prefixo do nome do layout de cada sheet.";
        var tbIniN = Caixa(s.Inicio.ToString(InvPP), 45);
        tbIniN.ToolTip = "Numero da primeira sheet.";
        var tbDig = Caixa(s.Digitos.ToString(InvPP), 35);
        tbDig.ToolTip = "Zeros a esquerda (2 = 01, 02...).";
        var lbNomes = new WC.TextBlock { Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Bottom, Margin = new SW.Thickness(0, 0, 0, 4) };
        p.Children.Add(Linha(Campo("Prefixo", tbPref), Campo("N inicial", tbIniN), Campo("Digitos", tbDig), lbNomes));
        var chNum = new WC.CheckBox { Content = "Numeracao automatica", IsChecked = s.Numerar, Margin = new SW.Thickness(0, 4, 14, 0) };
        chNum.ToolTip = "Escreve numero e total da serie nos textos marcados como NUMERO/TOTAL (categoria Numeracao) do layout base.";
        chNum.Click += (_, _) => s.Numerar = chNum.IsChecked == true;
        var chAuto = new WC.CheckBox { Content = "Auto-update", IsChecked = s.Automatico, Margin = new SW.Thickness(0, 4, 0, 0) };
        chAuto.ToolTip = "Atualiza sozinho ao mover, girar ou esticar um frame, ou ao editar o alignment. Desligado: so pelo botao Atualizar.";
        chAuto.Click += (_, _) =>
        {
            s.Automatico = chAuto.IsChecked == true;
            if (!_series.Any(x => x.Eixo == s.Eixo) || Doc is not Document d)
                return;
            try
            {
                LayoutSheetSeries.Salvar(d, s);
                Say(s.Automatico ? "Auto-update ligado." : "Auto-update desligado.");
            }
            catch (System.Exception ex)
            {
                Say("Nao foi possivel gravar: " + ex.Message);
            }
        };
        p.Children.Add(Linha(chNum, chAuto));

        // ---- Resumo e acoes -----------------------------------------------------
        var resumo = new WC.TextBlock { TextWrapping = SW.TextWrapping.Wrap, Foreground = T.Text };
        p.Children.Add(new WC.Border
        {
            BorderBrush = T.Accent,
            BorderThickness = new SW.Thickness(2, 0, 0, 0),
            Background = T.Hover,
            Padding = new SW.Thickness(10, 6, 10, 6),
            Margin = new SW.Thickness(0, 12, 0, 0),
            Child = resumo
        });

        void Ler()
        {
            var invalidos = new List<string>();
            if (LerEstaca(cbIni.Text, out double a))
                s.EstacaInicial = a;
            else
                invalidos.Add("estaca inicial");
            if (LerEstaca(cbFim.Text, out double b))
                s.EstacaFinal = b;
            else
                invalidos.Add("estaca final");
            s.Arredondar = Num(tbArred.Text, s.Arredondar);
            s.Folga = Num(tbFolga.Text, s.Folga);
            s.Girar = cbGiro.SelectedIndex == 0;
            s.UltimaCheia = cbUltima.SelectedIndex == 0;
            s.AjustarCotas = cbCotas.SelectedIndex == 0;
            s.PerfisHorizontal = cbDisp.SelectedIndex == 1;
            s.Prefixo = tbPref.Text;
            s.Inicio = (int)Num(tbIniN.Text, s.Inicio);
            s.Digitos = Math.Clamp((int)Num(tbDig.Text, s.Digitos), 0, 6);
            lbNomes.Text = $"{s.NomeFolha(0)}, {s.NomeFolha(1)}, ...";
            resumo.Text = (invalidos.Count > 0 ? $"Corrija: {string.Join(" e ", invalidos)} (17+10,00 ou metros). " : "") + Resumo(db, s, temSerie);
        }
        foreach (WC.TextBox tb in new[] { tbArred, tbFolga, tbPref, tbIniN, tbDig })
            tb.TextChanged += (_, _) => Ler();
        foreach (WC.ComboBox cb in new[] { cbGiro, cbUltima, cbCotas, cbDisp, cbIni, cbFim })
            cb.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Ler));
        foreach (WC.ComboBox cb in new[] { cbIni, cbFim })
            cb.AddHandler(WC.Primitives.TextBoxBase.TextChangedEvent, new WC.TextChangedEventHandler((_, _) => Ler()));
        Ler();

        var acoes = new WC.WrapPanel { Margin = new SW.Thickness(0, 4, 0, 0) };
        void Acao(WC.Button b, string dica)
        {
            b.ToolTip = dica;
            acoes.Children.Add(b);
        }
        if (!temSerie)
        {
            Acao(Principal("Gerar sheets", () => GerarSerie(pranchas: true)),
                "Cria frames, sheets (layouts), profile views e match lines; numera e refaz o mapa-chave.");
            Acao(ActionButton("So frames", () => GerarSerie(pranchas: false)),
                "Cria so os frames no Model para conferir/ajustar; depois \"Completar\" cria sheets e profile views.");
        }
        else
        {
            if (pendentes > 0)
                Acao(Principal($"Completar ({pendentes})", () => GerarSerie(pranchas: true)),
                    "Cria as sheets e os profile views que faltam (frames sem layout ou sem profile view).");
            Acao(pendentes > 0 ? ActionButton("Atualizar", AtualizarSerie) : Principal("Atualizar", AtualizarSerie),
                "Realinha tudo pelos frames: viewports de planta, estacas e elevations dos profile views, viewports de perfil e match lines.");
            Acao(ActionButton("Reaplicar layout base", ReaplicarLayoutBase),
                "Copia de novo o page setup e as camadas (layer states / overrides) do layout base para todas as sheets.");
            Acao(ActionButton("Remover serie...", RemoverSerie),
                "Remove frames e match lines; pergunta se remove tambem sheets e profile views.");
        }
        p.Children.Add(acoes);
        if (temSerie)
        {
            var nota = Label("Tamanho do frame, overlap e trecho valem ao gerar. Para mudar: Remover serie e gerar de novo.");
            nota.ToolTip = "Para ajustar uma sheet: mova ou estique o frame no Model, ou mude as estacas na aba Sheet.";
            p.Children.Add(nota);
        }
        return p;
    }

    private string Resumo(Database db, SheetSeries s, bool temSerie)
    {
        SheetCapacity? c = LayoutSheetSeries.Capacidade(db, s);
        if (c is null)
            return "Complete o passo 1 para ver quanto cabe por sheet.";
        string planta = $"Planta: ate {c.Planta:0} m/sheet";
        string perfil = double.IsInfinity(c.Perfil)
            ? ""
            : $"  |  profile view: {(c.PerfilCalibrado ? "" : "~")}{c.Perfil:0} m/sheet{(c.PerfilCalibrado ? "" : " (medido ao gerar)")}";
        string folhas = temSerie ? "" : $"  |  {c.Comprimento:0} m -> ~{c.Folhas} sheet(s): {s.NomeFolha(0)} a {s.NomeFolha(Math.Max(0, c.Folhas - 1))}";
        string limite = !double.IsInfinity(c.Perfil) && c.Perfil < c.Planta ? "  |  limitado pelo profile view" : "";
        return planta + perfil + folhas + limite;
    }

    private readonly Dictionary<ObjectId, List<(double Estaca, string Rotulo)>> _estacasCache = new();

    private List<(double Estaca, string Rotulo)> EstacasCache(Database db, ObjectId eixo)
    {
        if (!_estacasCache.TryGetValue(eixo, out List<(double, string)>? lista))
            _estacasCache[eixo] = lista = LayoutSheetSeries.EstacasDoEixo(db, eixo);
        return lista;
    }

    private static string TextoEstaca(double e, string rotulo) =>
        LayoutSheetSeries.Estaca(e) + (rotulo.Length > 0 ? "   " + rotulo : "");

    /// <summary>Combo editavel de estacas (inteiras e pontos notaveis); NaN mostra o inicio/fim do alignment.</summary>
    private WC.ComboBox ComboEstacas(List<(double Estaca, string Rotulo)> estacas, double atual, bool inicio)
    {
        var cb = new WC.ComboBox { Width = 140, IsEditable = true, HorizontalAlignment = SW.HorizontalAlignment.Left, MaxDropDownHeight = 320 };
        cb.ItemsPanel = new WC.ItemsPanelTemplate(new SW.FrameworkElementFactory(typeof(WC.VirtualizingStackPanel)));
        WC.VirtualizingPanel.SetIsVirtualizing(cb, true);
        foreach ((double e, string r) in estacas)
            cb.Items.Add(TextoEstaca(e, r));
        if (double.IsNaN(atual) && estacas.Count > 0)
        {
            (double e, string r) = inicio ? estacas[0] : estacas[^1];
            cb.Text = TextoEstaca(e, r);
        }
        else if (!double.IsNaN(atual))
        {
            (double Estaca, string Rotulo) achada = estacas.FirstOrDefault(x => Math.Abs(x.Estaca - atual) < 1e-3);
            cb.Text = achada.Rotulo is null ? LayoutSheetSeries.Estaca(atual) : TextoEstaca(achada.Estaca, achada.Rotulo);
        }
        return cb;
    }

    /// <summary>Primeiro trecho do texto ("12+5,30   PC" -> 12+5,30). Vazio = NaN (alignment inteiro).</summary>
    private static bool LerEstaca(string texto, out double estaca)
    {
        estaca = double.NaN;
        string t = texto.Trim();
        if (t.Length == 0)
            return true;
        string primeiro = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return LayoutSheetSeries.TentarEstaca(primeiro, out estaca);
    }

    private WC.ComboBox Opcoes(string[] itens, int selecionado, string dica)
    {
        var cb = new WC.ComboBox { Width = 190, HorizontalAlignment = SW.HorizontalAlignment.Left, ToolTip = dica };
        foreach (string i in itens)
            cb.Items.Add(i);
        cb.SelectedIndex = Math.Clamp(selecionado, 0, itens.Length - 1);
        return cb;
    }

    private static object Dica(string linha1, string linha2)
    {
        var tb = new WC.TextBlock { MaxWidth = 380, TextWrapping = SW.TextWrapping.Wrap };
        tb.Inlines.Add(new SW.Documents.Run(linha1));
        tb.Inlines.Add(new SW.Documents.LineBreak());
        tb.Inlines.Add(new SW.Documents.Run(linha2) { FontStyle = SW.FontStyles.Italic });
        return tb;
    }

    private static SW.UIElement Embaixo(SW.FrameworkElement e)
    {
        e.VerticalAlignment = SW.VerticalAlignment.Bottom;
        e.Margin = new SW.Thickness(0, 0, 6, 0);
        var sp = new WC.StackPanel { Margin = new SW.Thickness(0, 4, 12, 0), VerticalAlignment = SW.VerticalAlignment.Bottom };
        sp.Children.Add(new WC.TextBlock { Text = " ", Margin = new SW.Thickness(0, 0, 0, 2) });
        sp.Children.Add(e);
        return sp;
    }

    private string? _modeloPendente;

    /// <summary>Importa um layout de DWG/DWT (LAYOUT > Template do AutoCAD) e o usa como layout base.</summary>
    private void ImportarModelo()
    {
        if (Doc is not Document doc)
            return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Layout base de outro desenho",
            Filter = "Desenhos e templates (*.dwg;*.dwt)|*.dwg;*.dwt|Todos os arquivos (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        List<string> layouts;
        try
        {
            layouts = LayoutSheetSeries.LayoutsDoArquivo(dlg.FileName);
        }
        catch (System.Exception ex)
        {
            Say("Nao foi possivel ler o arquivo: " + ex.Message);
            return;
        }
        if (layouts.Count == 0)
        {
            Say("O arquivo nao tem layouts de papel.");
            return;
        }
        string? nome = layouts.Count == 1 ? layouts[0] : EscolherDaLista("Layout base", $"Layouts de {System.IO.Path.GetFileName(dlg.FileName)}:", layouts);
        if (nome is null)
            return;
        if (nome.Contains('"'))
        {
            Say("Nome de layout com aspas nao pode ser importado pela linha de comando.");
            return;
        }

        if (_folhas.Any(f => f.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase)))
        {
            string antigo = nome + " (anterior)";
            for (int k = 2; _folhas.Any(f => f.Nome.Equals(antigo, StringComparison.OrdinalIgnoreCase)); k++)
                antigo = $"{nome} (anterior {k})";
            SW.MessageBoxResult r = SW.MessageBox.Show(this,
                $"Ja existe o layout \"{nome}\" neste desenho.\n\nRenomear o existente para \"{antigo}\" e importar?",
                "Importar layout base", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question);
            if (r != SW.MessageBoxResult.Yes)
                return;
            try
            {
                using (doc.LockDocument())
                    LayoutManager.Current.RenameLayout(nome, antigo);
            }
            catch (System.Exception ex)
            {
                Say("Nao foi possivel renomear: " + ex.Message);
                return;
            }
        }

        _modeloPendente = nome;
        string Q(string t) => t.Contains(' ') ? $"\"{t}\"" : t;
        Enviar("(setq c3dfd (getvar \"FILEDIA\")) (setvar \"FILEDIA\" 0) " +
               $"_.-LAYOUT _T \"{dlg.FileName}\" {Q(nome)} " +
               "(setvar \"FILEDIA\" c3dfd)");
        Say($"Importando \"{nome}\"; ele vira o layout base quando terminar.");
    }

    private string? EscolherDaLista(string titulo, string texto, IReadOnlyList<string> itens)
    {
        var lista = new WC.ListBox { Background = T.Pane, Foreground = T.Text, BorderBrush = T.Line, Height = 240 };
        foreach (string i in itens)
            lista.Items.Add(i);
        lista.SelectedIndex = 0;
        var w = new SW.Window
        {
            Title = titulo,
            Width = 380,
            SizeToContent = SW.SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner,
            ResizeMode = SW.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = T.Back,
            Foreground = T.Text
        };
        w.Resources.MergedDictionaries.Add(Resources);
        var sp = new WC.StackPanel { Margin = new SW.Thickness(12) };
        sp.Children.Add(new WC.TextBlock { Text = texto, Foreground = T.Text, Margin = new SW.Thickness(0, 0, 0, 6) });
        sp.Children.Add(lista);
        var botoes = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right };
        var ok = new WC.Button { Content = "OK", MinWidth = 80, Margin = new SW.Thickness(0, 10, 6, 0), IsDefault = true };
        var cancelar = new WC.Button { Content = "Cancelar", MinWidth = 80, Margin = new SW.Thickness(0, 10, 0, 0), IsCancel = true };
        ok.Click += (_, _) => w.DialogResult = true;
        lista.MouseDoubleClick += (_, _) => w.DialogResult = true;
        botoes.Children.Add(ok);
        botoes.Children.Add(cancelar);
        sp.Children.Add(botoes);
        w.Content = sp;
        return w.ShowDialog() == true ? lista.SelectedItem as string : null;
    }

    private void ReaplicarLayoutBase()
    {
        if (_serie is null || Doc is not Document doc)
            return;
        SheetSeries s = _serie;
        ExecutarLongo($"Reaplicando o layout base em {s.EixoNome}", () =>
        {
            LayoutSheetSeries.Salvar(doc, s);
            return LayoutSheetSeries.ReaplicarLayoutBase(doc, s.Eixo);
        });
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
        SheetSeries s = _serie;
        SalvarPreferenciasPP(s);
        MostrarStatus(pranchas ? "Gerando frames, sheets e profile views..." : "Gerando frames...");
        ExecutarLongo(pranchas ? $"Gerando as sheets de {s.EixoNome}" : $"Gerando os frames de {s.EixoNome}",
            () => LayoutSheetSeries.Gerar(doc, s, pranchas, BasePerfis()));
    }

    private void AtualizarSerie()
    {
        if (_serie is null || Doc is not Document doc)
            return;
        SheetSeries s = _serie;
        ExecutarLongo($"Atualizando as sheets de {s.EixoNome}", () =>
        {
            LayoutSheetSeries.Salvar(doc, s);
            return LayoutSheetSeries.Atualizar(doc, s.Eixo, null);
        });
    }

    private void AtualizarTodasSeries()
    {
        if (Doc is not Document doc)
            return;
        ExecutarLongo("Atualizando todas as series", () => LayoutSheetSeries.AtualizarTodas(doc));
    }

    private void AplicarOpcoesPerfis()
    {
        if (_serie is null || Doc is not Document doc)
            return;
        SheetSeries s = _serie;
        ExecutarLongo($"Profiles nos profile views de {s.EixoNome}", () =>
        {
            LayoutSheetSeries.Salvar(doc, s);
            return LayoutSheetSeries.AplicarOpcoesPerfis(doc, s.Eixo);
        });
    }

    /// <summary>Muda a escala de uma viewport do layout base (passo 1).</summary>
    private void EscalarModelo(ObjectId vp, double custom, string qual, bool temSerie)
    {
        if (Doc is not Document doc)
            return;
        Executar(() =>
        {
            OpResult r = LayoutOps.Escalar(doc, new[] { vp }, custom, Fator);
            string extra = temSerie && r.Falhas == 0 ? " Frames ja gerados nao mudam: Remover serie e gerar de novo para usar a escala nova." : "";
            return r with { Mensagem = $"Viewport de {qual} do layout base: {r.Mensagem}{extra}" };
        });
    }

    /// <summary>Combo editavel de custom scale (escalas do desenho e as comuns); aplica ao escolher ou com Enter.</summary>
    private WC.ComboBox ComboEscala(double atual, Action<double> aplicar)
    {
        double f = Fator;
        var cb = new WC.ComboBox { Width = 150, IsEditable = true, HorizontalAlignment = SW.HorizontalAlignment.Left, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 4, 0, 0) };
        var denominadores = new SortedSet<double>(_escalas.Select(e => e.Denominador).Where(d => d > 0));
        foreach (int d in new[] { 100, 200, 250, 500, 1000, 2000, 2500, 5000 })
            denominadores.Add(d);
        foreach (double d in denominadores)
            cb.Items.Add(LayoutOps.EscalaTexto(f / d, f));
        cb.Text = LayoutOps.EscalaTexto(atual, f);
        bool aplicando = false;
        void Aplicar()
        {
            if (aplicando)
                return;
            string t = (cb.Text ?? "").Trim();
            int par = t.IndexOf("  (", StringComparison.Ordinal);
            if (par > 0)
                t = t[..par];
            if (!LayoutOps.TentarEscala(t, f, out double c))
            {
                Say("Escala invalida: use o custom scale (1 = 1:1000 num desenho em metros) ou 1:N.");
                return;
            }
            if (Math.Abs(c - atual) < 1e-9)
                return;
            aplicando = true;
            try
            {
                aplicar(c);
            }
            finally
            {
                aplicando = false;
            }
        }
        cb.DropDownClosed += (_, _) => Dispatcher.BeginInvoke(new Action(Aplicar), SW.Threading.DispatcherPriority.Background);
        cb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != WI.Key.Enter)
                return;
            Aplicar();
            e.Handled = true;
        };
        return cb;
    }

    private List<(ObjectId Id, string Nome, string Tipo)> PerfisCache(Database db, ObjectId eixo)
    {
        if (!_perfisCache.TryGetValue(eixo, out List<(ObjectId, string, string)>? lista))
            _perfisCache[eixo] = lista = LayoutSheetSeries.PerfisDoEixo(db, eixo);
        return lista;
    }

    /// <summary>
    /// Passo 3: quais profiles aparecem nos profile views (Draw), o label set de cada um e quem e
    /// Profile1/Profile2 nas bandas. Vale para os profile views criados pelo plugin.
    /// </summary>
    private SW.UIElement OpcoesDosPerfis(Database db, SheetSeries s, bool temSerie)
    {
        var p = new WC.StackPanel { Margin = new SW.Thickness(0, 6, 0, 0) };
        List<(ObjectId Id, string Nome, string Tipo)> perfis = PerfisCache(db, s.Eixo);
        _labelSetsPv ??= LayoutSheetSeries.LabelSetsPerfil(db);
        var titulo = new WC.TextBlock { Text = "Profiles", FontWeight = SW.FontWeights.Bold, Foreground = T.Text, Margin = new SW.Thickness(0, 4, 0, 2) };
        titulo.ToolTip = Dica("Aba Profiles das propriedades do profile view: Draw liga ou desliga o profile; Labels escolhe o label set.",
            "Vale so para os profile views criados pelo plugin (os da serie), ao gerar e no botao Aplicar.");
        p.Children.Add(titulo);
        if (perfis.Count == 0)
        {
            p.Children.Add(new WC.TextBlock { Text = "O alignment nao tem profiles.", Foreground = T.Muted });
            return p;
        }

        ProfileOption Opcao(ObjectId id)
        {
            ProfileOption? o = s.Opcao(id);
            if (o is null)
            {
                o = new ProfileOption { Perfil = id };
                s.Perfis.Add(o);
            }
            return o;
        }

        var grade = new WC.Grid();
        grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(250) });
        grade.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        int linha = 0;
        foreach ((ObjectId id, string nome, string tipo) in perfis)
        {
            grade.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
            ProfileOption? atual = s.Opcao(id);
            var draw = new WC.CheckBox
            {
                Content = $"{nome}   ({tipo})",
                IsChecked = atual?.Draw ?? true,
                VerticalAlignment = SW.VerticalAlignment.Center,
                Margin = new SW.Thickness(0, 2, 8, 2),
                ToolTip = "Draw: o profile aparece nos profile views da serie."
            };
            ObjectId perfilId = id;
            draw.Click += (_, _) => Opcao(perfilId).Draw = draw.IsChecked == true;
            WC.Grid.SetRow(draw, linha);
            grade.Children.Add(draw);

            var labels = new WC.ComboBox { Width = 230, Margin = new SW.Thickness(0, 2, 0, 2), ToolTip = "Label set do profile nos profile views da serie. (labels do Civil): o plugin nao mexe; (sem labels): apaga os labels do profile." };
            labels.Items.Add(new WC.ComboBoxItem { Content = "(labels do Civil)", Tag = "civil" });
            labels.Items.Add(new WC.ComboBoxItem { Content = "(sem labels)", Tag = "nenhum" });
            foreach ((ObjectId lid, string lnome) in _labelSetsPv)
                labels.Items.Add(new WC.ComboBoxItem { Content = lnome, Tag = lid });
            labels.SelectedItem = labels.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i =>
                                      atual is null ? i.Tag as string == "civil"
                                      : atual.SemLabels ? i.Tag as string == "nenhum"
                                      : atual.LabelSet.IsNull ? i.Tag as string == "civil"
                                      : i.Tag is ObjectId x && x == atual.LabelSet) ?? labels.Items[0];
            labels.SelectionChanged += (_, _) =>
            {
                if (labels.SelectedItem is not WC.ComboBoxItem it)
                    return;
                ProfileOption o = Opcao(perfilId);
                o.SemLabels = it.Tag as string == "nenhum";
                o.LabelSet = it.Tag is ObjectId l ? l : ObjectId.Null;
            };
            WC.Grid.SetRow(labels, linha);
            WC.Grid.SetColumn(labels, 1);
            grade.Children.Add(labels);
            linha++;
        }
        p.Children.Add(grade);
        if (_labelSetsPv.Count == 0)
            p.Children.Add(Aviso("Nenhum profile label set no desenho" + (LayoutSheetSeries.ErroEstilos.Length > 0 ? $" ({LayoutSheetSeries.ErroEstilos})" : "") + "."));

        WC.ComboBox ComboPerfil(ObjectId atual, Action<ObjectId> escolher, string dica)
        {
            var cb = new WC.ComboBox { Width = 200, HorizontalAlignment = SW.HorizontalAlignment.Left, ToolTip = dica };
            cb.Items.Add(new WC.ComboBoxItem { Content = "(do band set)", Tag = ObjectId.Null });
            foreach ((ObjectId id, string nome, string tipo) in perfis)
                cb.Items.Add(new WC.ComboBoxItem { Content = $"{nome}   ({tipo})", Tag = id });
            cb.SelectedItem = cb.Items.Cast<WC.ComboBoxItem>().FirstOrDefault(i => i.Tag is ObjectId x && x == atual && !atual.IsNull) ?? cb.Items[0];
            cb.SelectionChanged += (_, _) =>
            {
                if ((cb.SelectedItem as WC.ComboBoxItem)?.Tag is ObjectId id)
                    escolher(id);
            };
            return cb;
        }
        var cbP1 = ComboPerfil(s.Profile1, id => s.Profile1 = id, "Profile1 das bandas do band set (aba Bands): normalmente o terreno (surface).");
        var cbP2 = ComboPerfil(s.Profile2, id => s.Profile2 = id, "Profile2 das bandas do band set (aba Bands): normalmente o greide (layout).");
        p.Children.Add(Linha(Campo("Band set: Profile1", cbP1), Campo("Profile2", cbP2)));
        if (temSerie)
        {
            WC.Button aplicar = Botao("Aplicar nos profile views", AplicarOpcoesPerfis);
            aplicar.ToolTip = "Aplica Draw, labels e Profile1/Profile2 em todos os profile views da serie (so os criados pelo plugin) e refaz as elevations e a viewport de perfil.";
            p.Children.Add(aplicar);
        }
        return p;
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
        ExecutarLongo($"Removendo a serie de {_serie.EixoNome}", () => LayoutSheetSeries.Remover(doc, eixo, tudo));
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
            var t = Label("Selecione uma sheet na lista", true);
            t.ToolTip = "Aqui da para mudar as estacas de uma sheet, dar zoom no frame e abrir o layout. Duplo clique na lista faz zoom no frame.";
            p.Children.Add(t);
            return p;
        }

        p.Children.Add(Label($"{(f.LayoutNome.Length > 0 ? f.LayoutNome : "(sem layout)")}   |   {f.EixoNome}", true));
        p.Children.Add(Prop("Estacas", $"{LayoutSheetSeries.Estaca(f.E0)} a {LayoutSheetSeries.Estaca(f.E1)}  ({f.Extensao:0.##} m)"));
        p.Children.Add(Prop("Layout", f.LayoutNome.Length > 0 ? f.LayoutNome : "nao criado (Completar)"));
        p.Children.Add(Prop("Profile view", f.PerfilNome.Length > 0 ? f.PerfilNome : "-"));
        p.Children.Add(f.Situacao == "ok" ? Prop("Situacao", "ok") : Aviso(f.Situacao));

        Database? db = Doc?.Database;
        List<(double Estaca, string Rotulo)> estacas = db is null ? new() : EstacasCache(db, f.Eixo);
        WC.ComboBox cb0 = ComboEstacas(estacas, f.E0, inicio: true);
        WC.ComboBox cb1 = ComboEstacas(estacas, f.E1, inicio: false);
        cb0.ToolTip = Dica("Estaca da match line inicial desta sheet.", "As match lines andam dentro do frame (o frame nao se move); planta, profile view e match lines acompanham.");
        cb1.ToolTip = Dica("Estaca da match line final desta sheet.", "Aceita digitar: 17+10,00 ou metros.");
        p.Children.Add(Linha(Campo("Estaca inicial", cb0), Campo("Estaca final", cb1)));
        var b = new WC.WrapPanel();
        WC.Button aplicar = Principal("Aplicar estacas", () =>
        {
            if (!LerEstaca(cb0.Text, out double e0) || !LerEstaca(cb1.Text, out double e1) || double.IsNaN(e0) || double.IsNaN(e1))
            {
                Say("Estacas invalidas: use 17+10,00 ou metros.");
                return;
            }
            Executar(() => LayoutSheetSeries.DefinirEstacas(Doc!, f.Id, e0, e1));
        });
        aplicar.ToolTip = "Move as match lines desta sheet para as estacas escolhidas.";
        b.Children.Add(aplicar);
        WC.Button zoom = ActionButton("Zoom frame", () => ZoomFrame(f));
        zoom.ToolTip = "Mostra o frame no Model.";
        b.Children.Add(zoom);
        if (f.LayoutNome.Length > 0)
        {
            WC.Button abrir = ActionButton("Abrir layout", () => Abrir(f.LayoutNome));
            abrir.ToolTip = "Abre o layout desta sheet.";
            b.Children.Add(abrir);
        }
        WC.Button atualizar = ActionButton("Atualizar sheet", () => Executar(() => LayoutSheetSeries.Atualizar(Doc!, f.Eixo, new[] { f.Id })));
        atualizar.ToolTip = "Realinha so esta sheet pelo frame.";
        b.Children.Add(atualizar);
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
