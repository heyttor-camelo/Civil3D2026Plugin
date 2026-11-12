using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;
using W3 = System.Windows.Media.Media3D;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.UI.Common;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Categoria "Travessias" do painel de rebaixos (pedido do Heyvid, 11/11/2026): lista das
/// travessias do desenho, editar/redesenhar/separar/apagar, e as travessias na planta e no 3D.
/// </summary>
internal sealed partial class MFRebaixoPanel
{
    private List<TravessiaView> _travs = new();
    private readonly Dictionary<W3.GeometryModel3D, (TravessiaView T, string Part)> _travFaces = new();
    private string? _travPart;

    private static string PartName(string part) => part switch
    {
        "PLATO" => "Lombofaixa (plato e rampas)",
        "UNIAO_PLATO" => "Plato do cruzamento",
        "GRELHA_A" or "GRELHA_B" => "Grelha metalica",
        "TAMPA_A" or "TAMPA_B" or "UNIAO_TAMPA" => "Tampa de concreto",
        "VIGA_A" => "Viga baldrame (tras)",
        "VIGA_B" => "Viga baldrame (frente)",
        "FAIXA_3D" => "Faixa (pintura)",
        "TRIANGULO_3D" => "Triangulo (pintura)",
        _ => part
    };

    /// <summary>Clique numa travessia no 3D (das duas categorias): seleciona, mostra a ficha e as Propriedades.</summary>
    private void SelectTravFrom3D(TravessiaView t, string part)
    {
        _currentTrav = t;
        _keepCamera3D = true;
        if (_category != "TRAVESSIAS")
        {
            _category = "TRAVESSIAS";
            _tab = "Propriedades";
            FillCategories();
        }
        FillList();
        foreach (object o in _list.Items)
        {
            if (o is WC.ListViewItem { Tag: Row { Data: TravessiaView x } } item && ReferenceEquals(x, t))
            {
                _list.ScrollIntoView(item);
                break;
            }
        }
        _travPart = part;
        BuildTabs();
        HighlightTrav();
        RenderGraphic();
        _keepCamera3D = false;
        ShowTravCard3D();
    }

    /// <summary>Ficha da travessia (e da peca clicada) no quadro do 3D.</summary>
    private bool ShowTravCard3D()
    {
        TravessiaView? t = _currentTrav;
        if (t is null)
            return false;
        var lines = new List<string> { "LOMBOFAIXA " + TravName(t) };
        if (t.Data is { } d)
        {
            lines.Add($"Tipo: {d.Params.TipoTexto}{(d.Params.Esconsa ? (d.Params.RampasPerpendiculares ? ", esconsa (rampas perpendiculares)" : ", esconsa") : "")}   plataforma {N(d.Params.Plataforma, "0.00")} m   rampas {N(d.Params.Rampa, "0.00")} m");
            lines.Add($"Sarjeta: {d.Params.CoberturaTexto}");
            if (d.Params.Vigas)
                lines.Add($"Vigas baldrame: {Cm(d.Params.VigaLargura)} x {Cm(d.Params.VigaAltura)}, {d.Params.VigaPosicaoTexto}");
        }
        lines.Add($"Volume do plato: {N(t.Volume, "0.000")} m3   elevacao {N(t.MinZ, "0.000")} a {N(t.MaxZ, "0.000")}");
        if (_travPart is string part && t.Solids.Where(s => s.Part == part).ToList() is { Count: > 0 } solids)
        {
            lines.Add("");
            double vol = solids.Sum(s => s.Volume);
            lines.Add($"Peca clicada: {PartName(part)}" + (solids.Count > 1 ? $" ({solids.Count} pecas)" : "") + $", {N(vol, "0.000")} m3");
            Dictionary<string, string> pr = solids[0].Props;
            string Get(string k) => pr.TryGetValue(k, out string? s) ? s : "";
            if (Get("Comprimento").Length > 0)
                lines.Add($"  comprimento {Get("Comprimento")} m, largura {Get("Largura")} m, altura/espessura {Get("Altura_Espessura")} m");
            if (Get("Barras").Length > 0 && Get("Barras") != "0")
                lines.Add($"  {Get("Barras")} barras, area livre {Get("Area_Livre")} m2");
        }
        if (_picked3D is Point3d pk)
        {
            lines.Add("");
            lines.Add($"Ponto clicado: elevacao {pk.Z:0.000}");
        }
        _info3D.Text = string.Join("\n", lines);
        _info3D.Visibility = SW.Visibility.Visible;
        return true;
    }
    private TravessiaView? _currentTrav;
    private string _travSig = "";

    private void LoadTravessias(AcadDb.Database db)
    {
        try
        {
            _travs = MFTravessia.ReadViews(db);
        }
        catch (System.Exception ex)
        {
            _travs = new List<TravessiaView>();
            PluginLog.Aviso("MFREBAIXO", "Painel: travessias nao lidas: " + ex.Message);
        }
        _travSig = string.Join(",", _travs.Select(t => $"{t.Id}:{t.Objects}:{t.Tris.Count}:{t.Volume:0.###}"));
        if (_currentTrav is not null)
            _currentTrav = _travs.FirstOrDefault(t => t.Id == _currentTrav.Id);
        _currentTrav ??= _travs.FirstOrDefault(t => !t.IsUnion);
    }

    private static string TravName(TravessiaView t) => t.IsUnion
        ? $"Uniao {t.Id}"
        : t.Data is { } d ? $"{d.LabelA} - {d.LabelB}" : $"Travessia {t.Id}";

    private List<Row> TravRows(Func<string[], bool> match)
    {
        var rows = new List<Row>();
        foreach (TravessiaView t in _travs)
        {
            MFTravessia.Stored? d = t.Data;
            string[] cells = t.IsUnion
                ? new[] { TravName(t), "cruzamento", "", "", "", "", N(t.Volume, "0.00") + " m3", t.Id }
                : d is null
                    ? new[] { TravName(t), "(sem o solido do plato)", "", "", "", "", "", "" }
                    : new[]
                    {
                        TravName(t),
                        d.Params.Tipo == TravessiaTipo.Elevada ? "elevada" : "semi-elevada",
                        d.Params.Tipo == TravessiaTipo.Elevada ? "calcada" : Cm(d.Params.Altura),
                        N(d.Params.Plataforma, "0.00") + " m",
                        N(d.Params.Rampa, "0.00") + " m",
                        !d.Params.Tampa ? "nenhuma" : d.Params.Grelha ? "grelha " + Cm(d.Params.GrelhaEspessura) : "concreto " + Cm(d.Params.TampaEspessura),
                        N(t.Volume, "0.00") + " m3",
                        d.UnionId
                    };
            if (!match(cells.Append(t.Id).ToArray()))
                continue;
            rows.Add(new Row { Icon = t.IsUnion ? "solido3d" : "passagem", Data = t, Cells = cells });
        }
        return rows;
    }

    // ------------------------------------------------------------------
    // Aba Propriedades
    // ------------------------------------------------------------------

    private SW.UIElement BuildTravProperties()
    {
        WC.StackPanel p = Form();
        TravessiaView? t = _currentTrav;
        if (t is null)
        {
            p.Children.Add(Label(_travs.Count == 0
                ? "Nenhuma travessia no desenho. Use Nova travessia: escolha a superficie de topo, clique nos dois rebaixos (um de cada lado da rua) e confirme."
                : "Selecione uma travessia na lista.", true));
            WC.WrapPanel first = new();
            first.Children.Add(ActionButton("Nova travessia", NewTravessia, true));
            p.Children.Add(first);
            return p;
        }

        p.Children.Add(Label(TravName(t), true));
        p.Children.Add(Prop("Id", t.Id));
        p.Children.Add(Prop("Objetos no desenho", t.Objects.ToString(CultureInfo.InvariantCulture)));
        if (t.HasBox)
            p.Children.Add(Prop("Elevacao (base a topo)", $"{N(t.MinZ)} a {N(t.MaxZ)}"));
        AddQuantities(p, t);

        WC.WrapPanel acts = new();
        if (t.IsUnion || t.Data is null)
        {
            if (t.IsUnion)
            {
                List<TravessiaView> members = _travs.Where(x => x.Data?.UnionId == t.Id).ToList();
                p.Children.Add(Prop("Travessias unidas", members.Count == 0 ? "-" : string.Join("; ", members.Select(TravName))));
                acts.Children.Add(ActionButton("Separar a uniao", () => SeparateUnion(t.Id), true));
            }
            acts.Children.Add(ActionButton("Zoom no desenho", ZoomTrav));
            acts.Children.Add(ActionButton("Apagar", DeleteTrav));
            p.Children.Add(acts);
            return p;
        }

        MFTravessia.Stored d = t.Data;
        p.Children.Add(Prop("Rebaixos", $"{d.LabelA} ({(d.LeftA ? "esquerdo" : "direito")})  e  {d.LabelB} ({(d.LeftB ? "esquerdo" : "direito")})"));
        p.Children.Add(Prop("Superficie de topo", d.Params.Superficie.Length > 0 ? d.Params.Superficie : "automatica (a de topo do corredor)"));
        string skew = t.Solids.FirstOrDefault(s => s.Part == "PLATO").Props?.TryGetValue("Esconsidade", out string? sk) == true ? sk : "";
        p.Children.Add(Prop("Eixo da travessia", d.Params.Esconsa ? $"esconso (de um rebaixo ao outro){(skew.Length > 0 ? $", {skew} graus com a perpendicular a pista" : "")}" : "perpendicular a pista"));
        if (d.UnionId.Length > 0)
            p.Children.Add(Prop("Uniao do cruzamento", $"{d.UnionId} (so a rampa de fora fica nesta travessia)"));

        p.Children.Add(Label("Medidas (Aplicar redesenha a travessia)", true));
        var tipo = new WC.ComboBox { Width = 140, HorizontalAlignment = SW.HorizontalAlignment.Left, Margin = new SW.Thickness(0, 3, 0, 3) };
        tipo.Items.Add("semi-elevada");
        tipo.Items.Add("elevada");
        tipo.SelectedIndex = d.Params.Tipo == TravessiaTipo.Elevada ? 1 : 0;
        WC.Grid tipoRow = new() { Margin = new SW.Thickness(0, 3, 0, 3) };
        tipoRow.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(190) });
        tipoRow.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        tipoRow.Children.Add(new WC.TextBlock { Text = "Tipo", Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center });
        WC.Grid.SetColumn(tipo, 1);
        tipoRow.Children.Add(tipo);
        p.Children.Add(tipoRow);
        p.Children.Add(EditRow("t_altura", "Altura livre do meio-fio (semi)", N(d.Params.Altura, "0.000"), "m"));
        p.Children.Add(EditRow("t_plat", "Plataforma (faixa)", N(d.Params.Plataforma, "0.00"), "m"));
        p.Children.Add(EditRow("t_rampa", "Rampa de cada lado", N(d.Params.Rampa, "0.00"), "m"));
        var cob = new WC.ComboBox { Width = 160, HorizontalAlignment = SW.HorizontalAlignment.Left, Margin = new SW.Thickness(0, 3, 0, 3) };
        cob.Items.Add("grelha metalica");
        cob.Items.Add("tampa de concreto");
        cob.Items.Add("nenhuma");
        cob.SelectedIndex = !d.Params.Tampa ? 2 : d.Params.Cobertura == 1 ? 1 : 0;
        WC.Grid cobRow = new() { Margin = new SW.Thickness(0, 3, 0, 3) };
        cobRow.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(190) });
        cobRow.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        cobRow.Children.Add(new WC.TextBlock { Text = "Cobertura da sarjeta", Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center });
        WC.Grid.SetColumn(cob, 1);
        cobRow.Children.Add(cob);
        p.Children.Add(cobRow);
        p.Children.Add(EditRow("t_grelha", "Espessura da grelha", N(d.Params.GrelhaEspessura, "0.000"), "m"));
        p.Children.Add(EditRow("t_gb", "Largura das barras da grelha", N(d.Params.GrelhaBarra, "0.000"), "m"));
        p.Children.Add(EditRow("t_gv", "Vao entre as barras", N(d.Params.GrelhaVao, "0.000"), "m"));
        p.Children.Add(EditRow("t_ge", "Grelha alem da plataforma (cada ponta)", N(d.Params.GrelhaExtensao, "0.00"), "m"));
        p.Children.Add(EditRow("t_gf", "Grelha alem do pe do meio-fio", N(d.Params.GrelhaFolgaMF, "0.000"), "m"));
        p.Children.Add(EditRow("t_tampa", "Espessura da tampa de concreto", N(d.Params.TampaEspessura, "0.000"), "m"));
        var rperp = new WC.CheckBox { Content = "Esconsa: rampas perpendiculares a pista (so a faixa inclinada)", IsChecked = d.Params.RampasPerpendiculares, IsEnabled = d.Params.Esconsa, Foreground = T.Text, Margin = new SW.Thickness(0, 6, 0, 3) };
        p.Children.Add(rperp);
        var vigas = new WC.CheckBox { Content = "Vigas baldrame nas duas pontas", IsChecked = d.Params.Vigas, Foreground = T.Text, Margin = new SW.Thickness(0, 6, 0, 3) };
        p.Children.Add(vigas);
        p.Children.Add(EditRow("t_vl", "Largura da viga", N(d.Params.VigaLargura, "0.000"), "m"));
        p.Children.Add(EditRow("t_va", "Altura da viga", N(d.Params.VigaAltura, "0.000"), "m"));
        p.Children.Add(EditRow("t_ve", "Parte enterrada da viga", N(d.Params.VigaAltura * d.Params.VigaEnterrada, "0.000"), "m"));
        var naPlat = new WC.CheckBox { Content = "Viga no comeco da plataforma (acompanha a rampa)", IsChecked = d.Params.VigaNaPlataforma, Foreground = T.Text, Margin = new SW.Thickness(0, 4, 0, 3) };
        p.Children.Add(naPlat);
        p.Children.Add(EditRow("t_vr", "Ou recuo a partir do pe da rampa", N(Math.Max(0, d.Params.VigaRecuo), "0.000"), "m"));
        p.Children.Add(Label("A parte de cima da viga vai ate onde a lombofaixa deixa (nunca passa do topo); a altura real fica nas quantidades acima. Aplicar edita as pecas existentes (mesmos objetos no desenho)."));
        p.Children.Add(Label("A lombofaixa chega no topo do meio-fio (fundo da sarjeta + altura livre final de cada rebaixo). A altura livre aqui so vale onde o rebaixo nao for lido; para mudar a altura dos rebaixos, edite o rebaixo ou lance de novo com Nova travessia."));

        if (_travCheck is { } chk && chk.Id == t.Id)
        {
            p.Children.Add(Label("Conferencia do meio-fio x lombofaixa", true));
            foreach (string line in chk.Lines)
                p.Children.Add(Label(line));
        }
        acts.Children.Add(ActionButton("Conferir meio-fio x lombofaixa", CheckTrav));
        acts.Children.Add(ActionButton("Aplicar e redesenhar", () => ApplyTrav(tipo.SelectedIndex == 1, cob.SelectedIndex, vigas.IsChecked == true, naPlat.IsChecked == true, rperp.IsChecked == true), true));
        acts.Children.Add(ActionButton("Redesenhar como esta", () => RedrawTrav(null)));
        acts.Children.Add(ActionButton("Zoom no desenho", ZoomTrav));
        if (d.UnionId.Length > 0)
            acts.Children.Add(ActionButton("Separar a uniao", () => SeparateUnion(d.UnionId)));
        acts.Children.Add(ActionButton("Apagar", DeleteTrav));
        p.Children.Add(acts);
        return p;
    }

    // ------------------------------------------------------------------
    // Acoes
    // ------------------------------------------------------------------

    private void NewTravessia() => Send(C3DCommands.Corridor.MfTravessia + " ", "Nova travessia: escolha a superficie de topo (so na primeira vez) e clique nos dois rebaixos.");

    private void EditTrav()
    {
        if (_category != "TRAVESSIAS")
            SelectCategory("TRAVESSIAS");
        _tab = "Propriedades";
        BuildTabs();
        Say(_currentTrav is null ? "Selecione uma travessia na lista." : "Mude as medidas e clique em Aplicar e redesenhar.");
    }

    /// <summary>Quantidades: plato, grelha/tampa e vigas (comprimento, secao, volume) e total de concreto.</summary>
    private void AddQuantities(WC.StackPanel p, TravessiaView t)
    {
        static double Num(Dictionary<string, string> m, string k) =>
            m.TryGetValue(k, out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        p.Children.Add(Label("Quantidades (lidas do desenho)", true));
        double concreto = 0;
        foreach ((string part, string handle, double vol, Dictionary<string, string> props) in t.Solids.OrderBy(x => x.Part))
        {
            string name = part switch
            {
                "PLATO" => "Lombofaixa (plato)",
                "UNIAO_PLATO" => "Plato do cruzamento",
                "GRELHA_A" => "Grelha metalica A",
                "GRELHA_B" => "Grelha metalica B",
                "TAMPA_A" => "Tampa de concreto A",
                "TAMPA_B" => "Tampa de concreto B",
                "UNIAO_TAMPA" => "Cobertura de canto",
                "VIGA_A" => "Viga baldrame (tras)",
                "VIGA_B" => "Viga baldrame (frente)",
                _ => ""
            };
            if (name.Length == 0)
                continue; // pintura: nao entra nas quantidades
            string text = $"{N(vol, "0.000")} m3";
            if (part.StartsWith("GRELHA", StringComparison.Ordinal) || part.StartsWith("TAMPA", StringComparison.Ordinal))
                text = $"{N(Num(props, "Comprimento"), "0.00")} x {N(Num(props, "Largura"), "0.00")} m, espessura {Cm(Num(props, "Altura_Espessura"))}, area {N(Num(props, "Area"), "0.00")} m2" +
                       (Num(props, "Barras") > 0 ? $", {Num(props, "Barras"):0} barras, area livre {N(Num(props, "Area_Livre"), "0.00")} m2" : "") + $", {text}";
            else if (part.StartsWith("VIGA", StringComparison.Ordinal))
            {
                double buried = (t.Data?.Params.VigaAltura ?? 0) * (t.Data?.Params.VigaEnterrada ?? 0.5);
                double h = Num(props, "Altura_Espessura");
                text = $"{N(Num(props, "Comprimento"), "0.00")} m, secao {Cm(Num(props, "Largura"))} x {Cm(h)} ({Cm(buried)} enterrada, {Cm(Math.Max(0, h - buried))} dentro da lombofaixa), {text}";
            }
            p.Children.Add(Prop(name + $" [{handle}]", text));
            if (part is "PLATO" or "UNIAO_PLATO" || part.StartsWith("TAMPA", StringComparison.Ordinal))
                concreto += vol;
            else if (part.StartsWith("VIGA", StringComparison.Ordinal))
            {
                // A parte da viga dentro da lombofaixa ja esta no volume do plato: soma so a enterrada.
                double buried = (t.Data?.Params.VigaAltura ?? 0) * (t.Data?.Params.VigaEnterrada ?? 0.5);
                double h = Num(props, "Altura_Espessura");
                concreto += h > 1e-6 ? vol * Math.Min(1.0, buried / h) : vol;
            }
        }
        p.Children.Add(Prop("Total de concreto", N(concreto, "0.000") + " m3 (plato + tampas + parte enterrada das vigas)"));
    }

    private void ApplyTrav(bool elevada, int cobertura, bool vigas, bool vigaNaPlataforma = true, bool rampasPerp = false)
    {
        if (_currentTrav?.Data is not { } d)
        {
            Say("Selecione uma travessia.");
            return;
        }
        double? h = Number("t_altura"), plat = Number("t_plat"), ramp = Number("t_rampa"), lid = Number("t_tampa"), grid = Number("t_grelha");
        double? vl = Number("t_vl"), va = Number("t_va"), ve = Number("t_ve"), vr = Number("t_vr");
        double gb = Number("t_gb") ?? d.Params.GrelhaBarra, gv = Number("t_gv") ?? d.Params.GrelhaVao, ge = Number("t_ge") ?? d.Params.GrelhaExtensao, gf = Number("t_gf") ?? d.Params.GrelhaFolgaMF;
        if (h is null || plat is null || ramp is null || lid is null || grid is null || vl is null || va is null || ve is null || vr is null)
        {
            Say("Valores invalidos (use ponto ou virgula).");
            return;
        }
        // Sem recusas (pedido do Heyvid): o calculo avisa e usa o minimo que fecha o solido.
        var p = new TravessiaParams
        {
            Tipo = elevada ? TravessiaTipo.Elevada : TravessiaTipo.SemiElevada,
            Altura = h.Value,
            Plataforma = plat.Value,
            Rampa = ramp.Value,
            Tampa = cobertura != 2,
            Cobertura = cobertura == 1 ? 1 : 0,
            TampaEspessura = Math.Max(0.005, lid.Value),
            GrelhaEspessura = Math.Max(0.002, grid.Value),
            GrelhaBarra = Math.Max(0.003, gb),
            GrelhaVao = Math.Max(0.003, gv),
            GrelhaExtensao = Math.Max(0, ge),
            GrelhaFolgaMF = Math.Max(0, gf),
            Vigas = vigas,
            VigaLargura = Math.Max(0.01, vl.Value),
            VigaAltura = Math.Max(0.01, va.Value),
            VigaEnterrada = Math.Clamp(ve.Value / Math.Max(0.01, va.Value), 0, 1),
            VigaRecuo = vigaNaPlataforma ? -1 : Math.Max(0, vr.Value),
            Centralizar = d.Params.Centralizar,
            Superficie = d.Params.Superficie,
            Esconsa = d.Params.Esconsa,
            RampasPerpendiculares = d.Params.Esconsa && rampasPerp
        };
        TravessiaParams old = d.Params;
        d.Params = p;
        string info = "";
        Edit($"Travessia {TravName(_currentTrav)} redesenhada.", (db, ed) =>
        {
            try
            {
                (_, info) = MFTravessia.Redraw(db, ed, d);
            }
            catch
            {
                d.Params = old; // a travessia antiga fica como estava (o Draw so apaga depois de calcular)
                throw;
            }
        });
        if (info.Length > 0)
            Say($"Travessia redesenhada. {info}");
    }

    private (string Id, List<string> Lines)? _travCheck;

    /// <summary>So le e calcula (nao muda o desenho): encontro do meio-fio com a lombofaixa/grelha e avisos.</summary>
    private void CheckTrav()
    {
        if (_currentTrav?.Data is not { } d || Doc is not Document doc)
        {
            Say("Selecione uma travessia.");
            return;
        }
        try
        {
            TravessiaGeometry g;
            using (doc.LockDocument())
                g = MFTravessia.GeometryFor(doc.Database, d, MFRebaixoData.ReadAll(doc.Database), out _);
            var lines = new List<string>
            {
                $"Topo do meio-fio no eixo: elevacao {N(g.CurbTopA, "0.000")} ({d.LabelA}) e {N(g.CurbTopB, "0.000")} ({d.LabelB}); plato sobre a pista {Cm(g.HeightA)} e {Cm(g.HeightB)}."
            };
            lines.AddRange(g.Notes.Where(x => x.Contains("meio-fio", StringComparison.OrdinalIgnoreCase) || x.Contains("grelha", StringComparison.OrdinalIgnoreCase) || x.StartsWith("AVISO", StringComparison.Ordinal)));
            _travCheck = (_currentTrav.Id, lines);
            BuildTabs();
            Say("Conferencia feita (nada foi mudado no desenho).");
        }
        catch (System.Exception ex)
        {
            Say("Conferencia falhou: " + ex.Message);
        }
    }

    private void RedrawTrav(TravessiaView? which)
    {
        TravessiaView? t = which ?? _currentTrav;
        if (t?.Data is not { } d)
        {
            Say(t?.IsUnion == true ? "Uniao: use Redesenhar todas (ela e refeita com as travessias)." : "Selecione uma travessia.");
            return;
        }
        string info = "";
        Edit($"Travessia {TravName(t)} redesenhada.", (db, ed) => (_, info) = MFTravessia.Redraw(db, ed, d));
        if (info.Length > 0)
            Say($"Travessia redesenhada. {info}");
    }

    private void SeparateUnion(string unionId)
    {
        SW.MessageBoxResult answer = SW.MessageBox.Show(this,
            $"Separar a uniao {unionId}?\n\nO plato do cruzamento e as tampas dos cantos saem; cada travessia volta a ser inteira (plato e as duas rampas). Para desfazer: U no desenho logo em seguida.",
            "Separar a uniao", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question);
        if (answer != SW.MessageBoxResult.Yes)
            return;
        Edit($"Uniao {unionId} separada.", (db, edUnused) =>
        {
            MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
            List<RebaixoInfo> rebaixos = MFRebaixoData.ReadAll(db);
            MFTravessia.EraseUnion(db, unionId);
            foreach (MFTravessia.Stored d in MFTravessia.FindAll(db).Select(x => x.Data).Where(x => x.UnionId == unionId))
            {
                TravessiaGeometry g = MFTravessia.GeometryFor(db, d, rebaixos, out _);
                d.UnionId = "";
                d.OuterSign = 0;
                MFTravessia.Draw(db, g, d, settings);
            }
        });
    }

    private void DeleteTrav()
    {
        TravessiaView? t = _currentTrav;
        if (t is null)
        {
            Say("Selecione uma travessia.");
            return;
        }
        string what = t.IsUnion
            ? "o plato do cruzamento e as tampas dos cantos (as travessias continuam, so com a rampa de fora; use Separar para volta-las inteiras)"
            : $"o solido do plato, as tampas, as hachuras, a faixa e os triangulos ({t.Objects} objeto(s))" +
              (t.Data?.UnionId.Length > 0 ? $"; a uniao {t.Data.UnionId} e refeita sem ela" : "");
        SW.MessageBoxResult answer = SW.MessageBox.Show(this,
            $"Apagar {TravName(t)}?\n\nSai {what}.\nOs rebaixos e o corredor NAO mudam.\n\nPara desfazer: U no desenho logo em seguida.",
            "Apagar travessia", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Warning);
        if (answer != SW.MessageBoxResult.Yes)
            return;
        int n = 0;
        Edit($"{TravName(t)} apagada.", (db, ed) => n = t.IsUnion ? MFTravessia.EraseUnion(db, t.Id) : MFTravessia.EraseTravessia(db, ed, t.Id));
        Say($"{TravName(t)} apagada ({n} objeto(s)). U no desenho desfaz.");
        _currentTrav = null;
    }

    private void ZoomTrav()
    {
        TravessiaView? t = _currentTrav;
        if (t is null || !t.HasBox || Doc is not Document doc)
        {
            Say("Selecione uma travessia.");
            return;
        }
        try
        {
            double pad = Math.Max(4, Math.Max(t.MaxX - t.MinX, t.MaxY - t.MinY) * 0.4);
            using (doc.LockDocument())
            {
                using AcadDb.ViewTableRecord view = doc.Editor.GetCurrentView();
                Matrix3d dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                    Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                    Matrix3d.PlaneToWorld(view.ViewDirection);
                Matrix3d wcsToDcs = dcsToWcs.Inverse();
                Point3d a = new Point3d(t.MinX - pad, t.MinY - pad, 0).TransformBy(wcsToDcs);
                Point3d b = new Point3d(t.MaxX + pad, t.MaxY + pad, 0).TransformBy(wcsToDcs);
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                double ratio = view.Width / Math.Max(view.Height, 1e-9);
                double width = Math.Max(x1 - x0, 5.0), height = Math.Max(y1 - y0, 5.0);
                if (width / height > ratio)
                    height = width / ratio;
                else
                    width = height * ratio;
                view.CenterPoint = new Point2d((x0 + x1) / 2.0, (y0 + y1) / 2.0);
                view.Width = width;
                view.Height = height;
                doc.Editor.SetCurrentView(view);
                AcApp.UpdateScreen();
            }
            Say($"Zoom em {TravName(t)}.");
        }
        catch (System.Exception ex)
        {
            Say("Zoom falhou: " + ex.Message);
        }
    }

    /// <summary>Contorno do plato da travessia selecionada em amarelo no desenho (temporario).</summary>
    private void HighlightTrav()
    {
        try
        {
            TravessiaView? t = _currentTrav;
            List<Point2d>? loop = t?.Plan.Where(x => x.Part is "PLANTA_PLATO" or "UNIAO_PLANTA").OrderByDescending(x => x.Loop.Count).Select(x => x.Loop).FirstOrDefault();
            if (loop is null || Doc is null)
            {
                MFRebaixoHighlight.Clear();
                return;
            }
            MFRebaixoHighlight.ShowLine(loop.Select(q => new Point3d(q.X, q.Y, 0)).ToList(), true);
            AcApp.UpdateScreen();
        }
        catch
        {
            // Destaque e so visual.
        }
    }

    /// <summary>Na lista de travessias, mostra a planta/3D do rebaixo de um lado (a travessia aparece junto).</summary>
    private void FollowTrav(TravessiaView t)
    {
        _currentTrav = t;
        _travPart = null;
        MFTravessia.Stored? d = t.Data ?? _travs.FirstOrDefault(x => x.Data?.UnionId == t.Id)?.Data;
        if (d is null)
            return;
        RebaixoInfo? r = _items.Where(x => x.Label == d.LabelA && x.Left == d.LeftA).FirstOrDefault()
                         ?? _items.Where(x => x.Left == d.LeftA).OrderBy(x => x.Path.Count == 0 ? double.MaxValue : x.Path.Min(q => q.DistanceTo(new Point3d(d.Origin.X, d.Origin.Y, q.Z)))).FirstOrDefault();
        if (r is not null && !ReferenceEquals(r, _current))
        {
            _current = r;
            _secMark = null;
        }
    }

    // ------------------------------------------------------------------
    // Planta e 3D
    // ------------------------------------------------------------------

    private static readonly WM.Brush TravPlato = ToolTheme.Brush(165, 165, 170, 210);
    private static readonly WM.Brush TravRampa = ToolTheme.Brush(110, 110, 115, 210);
    private static readonly WM.Brush TravTampa = ToolTheme.Brush(210, 180, 90, 220);
    private static readonly WM.Brush TravPaint = ToolTheme.Brush(250, 250, 250);

    /// <summary>Travessias na planta (geral e do rebaixo): plato, rampas, tampas, faixa e triangulos; a selecionada com contorno.</summary>
    private void DrawTravessiasPlan()
    {
        if (_scale <= 0 || _travs.Count == 0)
            return;
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        foreach (TravessiaView t in _travs.OrderBy(x => ReferenceEquals(x, _currentTrav)))
        {
            bool selected = ReferenceEquals(t, _currentTrav) && _category == "TRAVESSIAS";
            foreach ((string part, List<Point2d> loop) in t.Plan.OrderBy(x => x.Part is "FAIXA" or "TRIANGULO" ? 2 : x.Part.Contains("TAMPA") ? 1 : 0))
            {
                WM.Brush fill = part switch
                {
                    "PLANTA_PLATO" or "UNIAO_PLANTA" => TravPlato,
                    "PLANTA_RAMPA" => TravRampa,
                    "FAIXA" or "TRIANGULO" => TravPaint,
                    _ => TravTampa
                };
                var pg = new WS.Polygon { Fill = fill, IsHitTestVisible = false };
                foreach (Point2d q in loop)
                    pg.Points.Add(ToScreen(q.X, q.Y));
                if (pg.Points.All(s => s.X < 0 || s.Y < 0 || s.X > w || s.Y > h))
                    continue;
                if (selected && part is "PLANTA_PLATO" or "UNIAO_PLANTA" or "PLANTA_RAMPA")
                {
                    pg.Stroke = SelectedBrush;
                    pg.StrokeThickness = 2;
                }
                _plan.Children.Add(pg);
            }
        }
    }

    /// <summary>Triangulos das travessias perto do trecho do 3D, uma malha por (travessia, parte).</summary>
    private void AddTravessias3D(W3.Model3DGroup group, double minX, double minY, double maxX, double maxY)
    {
        _travFaces.Clear();
        const double margin = 15.0;
        foreach (TravessiaView t in _travs)
        {
            if (!t.HasBox || t.MaxX < minX - margin || t.MinX > maxX + margin || t.MaxY < minY - margin || t.MinY > maxY + margin)
                continue;
            bool selected = ReferenceEquals(t, _currentTrav) && _category == "TRAVESSIAS";
            foreach (IGrouping<string, (string Part, Point3d A, Point3d B, Point3d C)> g in t.Tris.GroupBy(x => x.Part))
            {
                var mesh = new W3.MeshGeometry3D();
                foreach ((_, Point3d a, Point3d b, Point3d c) in g)
                {
                    int k = mesh.Positions.Count;
                    mesh.Positions.Add(Loc(a));
                    mesh.Positions.Add(Loc(b));
                    mesh.Positions.Add(Loc(c));
                    mesh.TriangleIndices.Add(k);
                    mesh.TriangleIndices.Add(k + 1);
                    mesh.TriangleIndices.Add(k + 2);
                }
                WM.Color color = g.Key.EndsWith("_3D", StringComparison.Ordinal) ? WM.Color.FromRgb(248, 248, 248)
                    : g.Key.Contains("TAMPA") ? WM.Color.FromRgb(205, 178, 105)
                    : selected ? WM.Color.FromRgb(200, 196, 150)
                    : WM.Color.FromRgb(168, 168, 174);
                W3.Material material = Material3D(color, false);
                var gm = new W3.GeometryModel3D(mesh, material) { BackMaterial = material };
                group.Children.Add(gm);
                _faces[gm] = new Face3D(-1, 0, 0, -1, color);
                _travFaces[gm] = (t, g.Key);
            }
        }
    }
}
