using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WCP = System.Windows.Controls.Primitives;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Edicao direto na lista (pedido do Heyvid em 13/11/2026: "nao quero ficar acessando a aba de
/// propriedades toda hora"). Duplo clique na celula ou F2 abre uma caixa em cima dela; Enter aplica
/// (mesmo caminho do Aplicar das propriedades), Esc cancela. Com varias linhas selecionadas, o valor
/// vai para todas, depois de confirmar.
/// Rebaixos: Nome, Comprimento, Altura final, Rampa (declividade). Travessias: Tipo, Altura,
/// Plataforma, Rampas, Sarjeta (cobertura).
/// </summary>
internal sealed partial class MFRebaixoPanel
{
    private WCP.Popup? _cellPopup;
    private int _lastCellColumn = -1;

    private static readonly int[] RebaixoEditable = { 0, 3, 4, 5 };
    private static readonly int[] TravEditable = { 1, 2, 3, 4, 5 };

    private int[] EditableColumns => _category switch
    {
        "REBAIXOS" => RebaixoEditable,
        "TRAVESSIAS" => TravEditable,
        _ => Array.Empty<int>()
    };

    private void AttachListEditing()
    {
        _list.SelectionMode = WC.SelectionMode.Extended;
        _list.ToolTip = "Duplo clique numa celula (ou F2) para editar: Enter aplica, Esc cancela. Ctrl/Shift + clique seleciona varias linhas para mudar todas de uma vez.";
        _list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (CellAt(e) is { } hit)
                _lastCellColumn = hit.Column;
        };
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != WI.Key.F2 || _list.SelectedItem is not WC.ListViewItem item)
                return;
            int[] editable = EditableColumns;
            if (editable.Length == 0)
                return;
            int col = editable.Contains(_lastCellColumn) ? _lastCellColumn : editable[0];
            if (BeginCellEdit(item, col))
                e.Handled = true;
        };
    }

    /// <summary>Duplo clique: numa celula editavel abre a edicao; senao faz o de sempre (zoom).</summary>
    private bool TryEditOnDoubleClick(WI.MouseButtonEventArgs e)
    {
        if (CellAt(e) is not { } hit || !EditableColumns.Contains(hit.Column))
            return false;
        return BeginCellEdit(hit.Item, hit.Column);
    }

    private (WC.ListViewItem Item, int Column)? CellAt(WI.MouseEventArgs e)
    {
        if (_list.View is not WC.GridView view)
            return null;
        SW.DependencyObject? d = e.OriginalSource as SW.DependencyObject;
        WC.ListViewItem? item = null;
        WC.GridViewRowPresenter? row = null;
        while (d is not null)
        {
            if (d is WC.GridViewRowPresenter rp)
                row = rp;
            if (d is WC.ListViewItem li)
            {
                item = li;
                break;
            }
            d = d is SW.Media.Visual or SW.Media.Media3D.Visual3D ? WM.VisualTreeHelper.GetParent(d) : SW.LogicalTreeHelper.GetParent(d);
        }
        if (item is null || row is null)
            return null;
        double x = e.GetPosition(row).X, acc = 0;
        for (int i = 0; i < view.Columns.Count; i++)
        {
            acc += view.Columns[i].ActualWidth;
            if (x < acc)
                return (item, i);
        }
        return null;
    }

    private static WC.GridViewRowPresenter? RowPresenter(SW.DependencyObject root)
    {
        for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            SW.DependencyObject child = WM.VisualTreeHelper.GetChild(root, i);
            if (child is WC.GridViewRowPresenter rp)
                return rp;
            if (RowPresenter(child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Abre a caixa de edicao em cima da celula.</summary>
    private bool BeginCellEdit(WC.ListViewItem item, int column)
    {
        if (_list.View is not WC.GridView view || column < 0 || column >= view.Columns.Count)
            return false;
        if (item.Tag is not Row row || !EditableColumns.Contains(column))
            return false;
        if (row.Data is TravessiaView tv && (tv.IsUnion || tv.Data is null))
        {
            Say("Esta linha nao tem medidas para editar (uniao ou travessia sem o solido do plato).");
            return true;
        }

        CloseCellEdit();
        if (!item.IsSelected)
        {
            _list.SelectedItems.Clear();
            item.IsSelected = true;
        }
        item.UpdateLayout();
        WC.GridViewRowPresenter? presenter = RowPresenter(item);
        SW.UIElement target = presenter is null ? item : presenter;
        double x0 = 0;
        for (int i = 0; i < column; i++)
            x0 += view.Columns[i].ActualWidth;
        double width = Math.Max(80, view.Columns[column].ActualWidth);

        SW.FrameworkElement editor;
        string[]? choices = Choices(row, column);
        if (choices is not null)
        {
            var combo = new WC.ComboBox { Width = Math.Max(width, 140), IsEditable = false };
            foreach (string c in choices)
                combo.Items.Add(c);
            combo.SelectedIndex = Math.Max(0, Array.IndexOf(choices, CurrentChoice(row, column)));
            bool ready = false;
            combo.SelectionChanged += (_, _) =>
            {
                if (!ready)
                    return;
                string chosen = combo.SelectedItem as string ?? "";
                CloseCellEdit();
                CommitCell(row, column, chosen);
            };
            combo.KeyDown += (_, e) =>
            {
                if (e.Key == WI.Key.Escape)
                {
                    CloseCellEdit();
                    Say("Edicao cancelada.");
                    e.Handled = true;
                }
                else if (e.Key == WI.Key.Enter)
                {
                    string chosen = combo.SelectedItem as string ?? "";
                    CloseCellEdit();
                    CommitCell(row, column, chosen);
                    e.Handled = true;
                }
            };
            editor = combo;
            combo.Loaded += (_, _) =>
            {
                combo.Focus();
                combo.IsDropDownOpen = true;
                ready = true;
            };
        }
        else
        {
            var box = new WC.TextBox { Text = InitialText(row, column), Width = width, VerticalContentAlignment = SW.VerticalAlignment.Center, ToolTip = Hint(column) };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == WI.Key.Enter)
                {
                    string text = box.Text;
                    CloseCellEdit();
                    CommitCell(row, column, text);
                    e.Handled = true;
                }
                else if (e.Key == WI.Key.Escape)
                {
                    CloseCellEdit();
                    Say("Edicao cancelada.");
                    e.Handled = true;
                }
            };
            box.Loaded += (_, _) =>
            {
                box.Focus();
                WI.Keyboard.Focus(box);
                box.SelectAll();
            };
            editor = box;
        }

        _cellPopup = new WCP.Popup
        {
            PlacementTarget = target,
            Placement = WCP.PlacementMode.Relative,
            HorizontalOffset = x0,
            VerticalOffset = 0,
            StaysOpen = false,
            Child = editor
        };
        _cellPopup.Closed += (_, _) => _cellPopup = null;
        _cellPopup.IsOpen = true;
        int count = TargetsOf(row).Count;
        Say(count > 1
            ? $"Editando {view.Columns[column].Header} de {count} linhas selecionadas: Enter aplica em todas (pede confirmacao), Esc cancela."
            : $"Editando {view.Columns[column].Header}: Enter aplica, Esc cancela. {Hint(column)}");
        return true;
    }

    private void CloseCellEdit()
    {
        if (_cellPopup is { } p)
        {
            _cellPopup = null;
            p.IsOpen = false;
        }
        _list.Focus();
    }

    private string Hint(int column) => _category == "REBAIXOS"
        ? column switch
        {
            3 => "Comprimento do trecho rebaixado em m (o inicio fica, o fim muda).",
            4 => "Altura livre final em cm (ou em m, ex. 0.12).",
            5 => "Declividade das rampas em % (ex. 8.33, ou 8.33/6 para entrada/saida).",
            _ => "Nome do rebaixo (vazio = sem nome)."
        }
        : column switch
        {
            2 => "Altura livre do meio-fio em cm (ou em m, ex. 0.12).",
            3 => "Plataforma (faixa) em m.",
            4 => "Rampa de cada lado em m.",
            _ => ""
        };

    private static string[]? Choices(Row row, int column) => row.Data is TravessiaView
        ? column switch
        {
            1 => new[] { "semi-elevada", "elevada" },
            5 => new[] { "grelha metalica", "tampa de concreto", "nenhuma" },
            _ => null
        }
        : null;

    private static string CurrentChoice(Row row, int column)
    {
        if (row.Data is not TravessiaView { Data: { } d })
            return "";
        return column == 1
            ? d.Params.Tipo == TravessiaTipo.Elevada ? "elevada" : "semi-elevada"
            : !d.Params.Tampa ? "nenhuma" : d.Params.Cobertura == 1 ? "tampa de concreto" : "grelha metalica";
    }

    private static string InitialText(Row row, int column)
    {
        static string F(double v, string f) => v.ToString(f, CultureInfo.InvariantCulture);
        switch (row.Data)
        {
            case RebaixoInfo r:
                return column switch
                {
                    0 => r.CustomName,
                    3 => F(r.LoweredLength, "0.00"),
                    4 => r.FinalHeight is double h ? F(h * 100, "0.#") : "",
                    5 => SlopeText(r),
                    _ => ""
                };
            case TravessiaView { Data: { } d }:
                return column switch
                {
                    2 => F(d.Params.Altura * 100, "0.#"),
                    3 => F(d.Params.Plataforma, "0.00"),
                    4 => F(d.Params.Rampa, "0.00"),
                    _ => ""
                };
        }
        return "";
    }

    /// <summary>
    /// Altura digitada: sem unidade, valor de 1 para cima e em cm e abaixo de 1 em m (12 = 12 cm, 0.12 = 12 cm);
    /// "12 cm" ou "0.12 m" valem como escrito.
    /// </summary>
    private static bool TryParseHeight(string text, out double meters)
    {
        meters = 0;
        string t = (text ?? "").Trim().ToLowerInvariant().Replace(',', '.');
        bool cm = t.EndsWith("cm", StringComparison.Ordinal);
        bool m = !cm && t.EndsWith("m", StringComparison.Ordinal);
        t = t.Replace("cm", "").Replace("m", "").Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v < 0)
            return false;
        meters = cm ? v / 100.0 : m ? v : v >= 1 ? v / 100.0 : v;
        return meters <= 2.0;
    }

    private static bool TryParseMeters(string text, out double meters)
    {
        string t = (text ?? "").Trim().ToLowerInvariant().Replace(',', '.').Replace("m", "").Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out meters) && double.IsFinite(meters);
    }

    /// <summary>A linha editada e, se ela estiver numa selecao de varias, todas as selecionadas do mesmo tipo.</summary>
    private List<Row> TargetsOf(Row edited)
    {
        List<Row> selected = _list.SelectedItems.OfType<WC.ListViewItem>().Select(i => i.Tag).OfType<Row>().ToList();
        if (selected.Count <= 1 || !selected.Contains(edited))
            return new List<Row> { edited };
        return selected.Where(r => r.Data?.GetType() == edited.Data?.GetType()).ToList();
    }

    private void CommitCell(Row row, int column, string text)
    {
        try
        {
            if (row.Data is RebaixoInfo)
                CommitRebaixo(row, column, text);
            else if (row.Data is TravessiaView)
                CommitTrav(row, column, text);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("MFREBAIXO", "Edicao na lista: " + ex.Message, ex);
            Say("Erro: " + ex.Message);
        }
    }

    private bool ConfirmBatch(int count, string what)
    {
        if (count <= 1)
            return true;
        SW.MessageBoxResult answer = SW.MessageBox.Show(this,
            $"Mudar {what} em {count} itens selecionados?\n\n" +
            "Cada um e removido e aplicado de novo com o valor novo (se um falhar, o antigo dele volta e os outros seguem).\n\n" +
            "Para desfazer: U (Ctrl+Z) no desenho logo em seguida, ou edite de novo.",
            "Editar varios", SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question);
        return answer == SW.MessageBoxResult.Yes;
    }

    private void CommitRebaixo(Row row, int column, string text)
    {
        var edited = (RebaixoInfo)row.Data!;
        if (column == 0)
        {
            // Nome: so a linha editada (nome igual em varios nao faz sentido).
            string name = (text ?? "").Trim().Replace("|", "/");
            if (name == edited.CustomName)
                return;
            _pending = (edited.CorridorId, edited.BaselineKey, edited.S1);
            Edit(name.Length > 0 ? $"{edited.Code} agora se chama \"{name}\"." : $"{edited.Code} ficou sem nome.", (db, _) => MFRebaixoData.Rename(db, edited, name));
            return;
        }

        double? comp = null, height = null, sIn = null, sOut = null;
        string what;
        switch (column)
        {
            case 3:
                if (!TryParseMeters(text, out double c) || c < 0.01 || c > 200)
                {
                    Say($"Comprimento invalido ({text}): use metros, ex. 3.00. Nada mudou.");
                    return;
                }
                comp = c;
                what = $"o comprimento do trecho rebaixado para {N(c, "0.00")} m";
                break;
            case 4:
                if (!TryParseHeight(text, out double h))
                {
                    Say($"Altura invalida ({text}): use cm (ex. 12) ou m (ex. 0.12). Nada mudou.");
                    return;
                }
                height = h;
                what = $"a altura livre final para {Cm(h)}";
                break;
            case 5:
                if (!TryParseSlopes(text, out sIn, out sOut, out string why) || sIn is null)
                {
                    Say(why.Length > 0 ? why + " Nada mudou." : "Informe a declividade em % (ex. 8.33). Nada mudou.");
                    return;
                }
                what = Math.Abs(sIn.Value - sOut!.Value) > 1e-6
                    ? $"a declividade das rampas para {Pct(sIn)} (entrada) / {Pct(sOut)} (saida)"
                    : $"a declividade das rampas para {Pct(sIn)}";
                break;
            default:
                return;
        }

        List<RebaixoInfo> targets = TargetsOf(row).Select(r => r.Data).OfType<RebaixoInfo>().ToList();
        if (targets.Any(r => r.FinalHeight is null))
        {
            Say("Nao deu para ler a altura final de um dos rebaixos (Rebuild no corredor e Reler o painel). Nada mudou.");
            return;
        }
        if (!ConfirmBatch(targets.Count, what))
        {
            Say("Edicao cancelada.");
            return;
        }

        ApplyRebaixoValues(targets, comp, height, sIn, sOut, what);
    }

    /// <summary>
    /// Aplica comprimento, altura final e/ou declividade (null = fica como esta) nos rebaixos: um a um,
    /// remove e lanca de novo (mesmo caminho do Aplicar); se um falhar, o antigo dele volta.
    /// </summary>
    private void ApplyRebaixoValues(List<RebaixoInfo> targets, double? comp, double? height, double? sIn, double? sOut, string what)
    {
        if (targets.Count == 1)
        {
            RebaixoInfo r = targets[0];
            double len = comp ?? r.LoweredLength;
            Reapply(r, r.S1, r.S1 + len, height ?? r.FinalHeight!.Value, sIn, sOut);
            return;
        }

        var errors = new List<string>();
        Edit($"{targets.Count} rebaixos: {what}.", (db, ed) =>
        {
            foreach (RebaixoInfo r in targets)
            {
                double oldH = r.FinalHeight!.Value;
                double len = comp ?? r.LoweredLength;
                double newH = height ?? oldH;
                double? a = sIn ?? r.EntrySlope, b = sOut ?? r.ExitSlope;
                MFRebaixoData.Remove(db, r, rebuild: true);
                try
                {
                    MFRebaixoCommands.ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, r.S1, r.S1 + len, newH, MFRebaixoData.CommentFor(r.CustomName), a, b);
                }
                catch (System.Exception ex)
                {
                    try
                    {
                        MFRebaixoCommands.ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, r.S1, r.S2, oldH, MFRebaixoData.CommentFor(r.CustomName), r.EntrySlope, r.ExitSlope);
                        errors.Add($"{r.Label}: {ex.Message} (ficou como estava)");
                    }
                    catch
                    {
                        errors.Add($"{r.Label}: {ex.Message} (o antigo tambem nao voltou; use U para desfazer)");
                    }
                }
            }
        });
        if (errors.Count > 0)
        {
            PluginLog.Aviso("MFREBAIXO", "Edicao em lote: " + string.Join(" | ", errors));
            Say($"{targets.Count - errors.Count} de {targets.Count} mudados. Nao deu: " + string.Join("; ", errors));
        }
    }

    private void CommitTrav(Row row, int column, string text)
    {
        Action<TravessiaParams> change;
        string what;
        switch (column)
        {
            case 1:
                bool elev = text == "elevada";
                change = p => p.Tipo = elev ? TravessiaTipo.Elevada : TravessiaTipo.SemiElevada;
                what = "o tipo para " + (elev ? "elevada" : "semi-elevada");
                break;
            case 2:
                if (!TryParseHeight(text, out double h))
                {
                    Say($"Altura invalida ({text}): use cm (ex. 12) ou m (ex. 0.12). Nada mudou.");
                    return;
                }
                change = p => p.Altura = h;
                what = $"a altura livre do meio-fio para {Cm(h)}";
                break;
            case 3:
                if (!TryParseMeters(text, out double plat) || plat < 0.5 || plat > 30)
                {
                    Say($"Plataforma invalida ({text}): use metros, ex. 5.00. Nada mudou.");
                    return;
                }
                change = p => p.Plataforma = plat;
                what = $"a plataforma para {N(plat, "0.00")} m";
                break;
            case 4:
                if (!TryParseMeters(text, out double ramp) || ramp < 0.1 || ramp > 20)
                {
                    Say($"Rampa invalida ({text}): use metros, ex. 1.50. Nada mudou.");
                    return;
                }
                change = p => p.Rampa = ramp;
                what = $"a rampa de cada lado para {N(ramp, "0.00")} m";
                break;
            case 5:
                int cob = text.StartsWith("tampa", StringComparison.Ordinal) ? 1 : text == "nenhuma" ? 2 : 0;
                change = p =>
                {
                    p.Tampa = cob != 2;
                    p.Cobertura = cob == 1 ? 1 : 0;
                };
                what = "a cobertura da sarjeta para " + text;
                break;
            default:
                return;
        }

        List<TravessiaView> targets = TargetsOf(row).Select(r => r.Data).OfType<TravessiaView>().Where(t => !t.IsUnion && t.Data is not null).ToList();
        if (targets.Count == 0)
            return;
        if (!ConfirmBatch(targets.Count, what))
        {
            Say("Edicao cancelada.");
            return;
        }

        var errors = new List<string>();
        var notes = new List<string>();
        Edit(targets.Count == 1 ? $"Travessia {TravName(targets[0])}: {what}." : $"{targets.Count} travessias: {what}.", (db, ed) =>
        {
            foreach (TravessiaView t in targets)
            {
                MFTravessia.Stored d = t.Data!;
                TravessiaParams old = d.Params;
                TravessiaParams p = old.Copy();
                change(p);
                d.Params = p;
                try
                {
                    (_, string info) = MFTravessia.Redraw(db, ed, d);
                    if (info.Length > 0)
                        notes.Add(info);
                }
                catch (System.Exception ex)
                {
                    d.Params = old;
                    errors.Add($"{TravName(t)}: {ex.Message} (ficou como estava)");
                }
            }
        });
        if (errors.Count > 0)
            Say($"{targets.Count - errors.Count} de {targets.Count} mudadas. Nao deu: " + string.Join("; ", errors));
        else if (notes.Count > 0 && targets.Count == 1)
            Say("Travessia redesenhada. " + notes[0]);
    }
}
