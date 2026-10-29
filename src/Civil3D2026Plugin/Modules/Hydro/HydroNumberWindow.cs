using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.EditorInput;
using Civil3D2026Plugin.Modules.Hydro.Calc;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Numeracao da rede (DRENNUM): janela no estilo do painel HYDRO, com a planta dos tubos e
/// estruturas e os nomes novos na hora. Um contador por prefixo (tipos diferentes podem usar o
/// mesmo prefixo sem colidir), numeros ja usados fora do lote sao pulados, e da para numerar so
/// as estruturas, so os tubos ou so um tipo. Clique na planta tira/poe uma peca do lote.
/// </summary>
internal static class HydroNumbering
{
    /// <summary>Estado que sobrevive entre aberturas (prefixos e opcoes da sessao, por desenho).</summary>
    internal sealed class Memory
    {
        public Dictionary<string, string> Prefix { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string PipePrefix { get; set; } = "";
        public bool PipesEnabled { get; set; } = true;
        public bool BySize { get; set; }
        public int Digits { get; set; } = 2;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AcadDb.Database, Memory> Memories = new();

    public static void Run()
    {
        Autodesk.AutoCAD.ApplicationServices.Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        AcadDb.Database db = doc.Database;
        Editor ed = doc.Editor;
        try
        {
            HashSet<string>? seeds = null;
            PromptSelectionResult implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value is { Count: > 0 })
            {
                seeds = Handles(db, implied.Value.GetObjectIds());
                ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());
            }

            HydroSnapshot snap = HydroReader.Read(db);
            if (snap.Networks.Count == 0)
            {
                ed.WriteMessage("\nDRENNUM: o desenho nao tem Pipe Network.");
                return;
            }

            Memory memory = Memories.GetValue(db, _ => new Memory());
            HydroNumberWindow? w = null;
            while (true)
            {
                w = new HydroNumberWindow(snap, memory, seeds, w);
                AcApp.ShowModalWindow(w);
                if (w.PickRequested)
                {
                    PromptSelectionResult r = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelecione tubos/estruturas: entra a parte da rede ligada a eles (Enter = rede inteira): " });
                    seeds = r.Status == PromptStatus.OK ? Handles(db, r.Value.GetObjectIds()) : null;
                    continue;
                }

                break;
            }

            if (!w.ApplyRequested || w.Result.Count == 0)
            {
                ed.WriteMessage("\nDRENNUM: nada foi alterado.");
                return;
            }

            int n = Apply(db, w.Result);
            ed.WriteMessage($"\nDRENNUM: {w.Result.Count(r => !r.IsPipe)} estrutura(s) e {w.Result.Count(r => r.IsPipe)} tubo(s) renomeados ({n} nome(s) mudaram). U desfaz.");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nDRENNUM: erro - {ex.Message}");
            Infrastructure.PluginLog.Erro("Hydro", "DRENNUM", ex);
        }
    }

    private static HashSet<string> Handles(AcadDb.Database db, IEnumerable<AcadDb.ObjectId> ids)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (AcadDb.ObjectId id in ids)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Part)
                set.Add(id.Handle.ToString());
        }

        return set;
    }

    /// <summary>Grava os nomes numa transacao (um UNDO); passa por nomes temporarios para nao colidir no meio.</summary>
    private static int Apply(AcadDb.Database db, IReadOnlyList<NumResult> plan)
    {
        List<(CivilDb.Part Part, string Name)> parts = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (NumResult r in plan)
        {
            if (HydroReader.Id(db, r.Id) is AcadDb.ObjectId id && tr.GetObject(id, AcadDb.OpenMode.ForWrite, false) is CivilDb.Part part)
                parts.Add((part, r.NewName));
        }

        int changed = parts.Count(p => !string.Equals(((CivilDb.Entity)p.Part).Name, p.Name, StringComparison.Ordinal));
        int i = 0;
        foreach ((CivilDb.Part part, string _) in parts)
            ((CivilDb.Entity)part).Name = $"__NUM_{Guid.NewGuid():N}_{i++}";
        foreach ((CivilDb.Part part, string name) in parts)
            ((CivilDb.Entity)part).Name = name;
        tr.Commit();
        return changed;
    }
}

/// <summary>Janela da numeracao: planta a esquerda, opcoes e previa a direita.</summary>
internal sealed class HydroNumberWindow : SW.Window
{
    private sealed class Group
    {
        public string Key = "";
        public string Label = "";
        public int Count;
        public WM.Color Color;
        public WC.CheckBox Check = null!;
        public WC.TextBox Prefix = null!;
    }

    private readonly HydroSnapshot _snap;
    private readonly HydroNumbering.Memory _memory;
    private HashSet<string>? _seeds;
    private readonly HashSet<string> _netChecked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Group> _groups = new();
    private readonly Dictionary<string, WC.TextBox> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _startText = new(StringComparer.OrdinalIgnoreCase);

    private readonly WC.StackPanel _netPanel = new();
    private readonly WC.StackPanel _groupPanel = new();
    private readonly WC.StackPanel _startPanel = new();
    private readonly WC.TextBlock _scope = new() { TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(6, 2, 6, 4) };
    private readonly WC.TextBlock _warnings = new() { TextWrapping = SW.TextWrapping.Wrap, Margin = new SW.Thickness(6, 2, 6, 2) };
    private readonly WC.ListView _list = new() { BorderThickness = new SW.Thickness(0) };
    private readonly WC.CheckBox _bySize = new() { Content = "Separar os tipos tambem pelo tamanho", Margin = new SW.Thickness(6, 4, 6, 2) };
    private readonly WC.ComboBox _digits = new() { Width = 90, Margin = new SW.Thickness(6, 2, 6, 2) };
    private WC.CheckBox _pipeCheck = null!;
    private WC.TextBox _pipePrefix = null!;
    private readonly NumberPlanView _plan;
    private bool _building;

    // Lote atual (pecas das redes marcadas, ou so a parte ligada a selecao).
    private List<StructureInfo> _structures = new();
    private List<PipeInfo> _pipes = new();
    private Dictionary<string, string> _groupOf = new(StringComparer.OrdinalIgnoreCase);

    public bool PickRequested { get; private set; }
    public bool ApplyRequested { get; private set; }
    public List<NumResult> Result { get; private set; } = new();

    private static readonly WM.Color[] Palette =
    {
        WM.Color.FromRgb(255, 196, 0), WM.Color.FromRgb(90, 200, 120), WM.Color.FromRgb(240, 120, 200), WM.Color.FromRgb(120, 170, 255),
        WM.Color.FromRgb(255, 140, 70), WM.Color.FromRgb(170, 130, 255), WM.Color.FromRgb(80, 220, 220), WM.Color.FromRgb(220, 220, 120)
    };

    internal static readonly WM.Brush Back = Frozen(WM.Color.FromRgb(37, 37, 38));
    internal static readonly WM.Brush Pane = Frozen(WM.Color.FromRgb(45, 45, 48));
    internal static readonly WM.Brush Head = Frozen(WM.Color.FromRgb(62, 62, 66));
    internal static readonly WM.Brush TextBrush = Frozen(WM.Color.FromRgb(225, 225, 225));
    internal static readonly WM.Brush Muted = Frozen(WM.Color.FromRgb(150, 150, 150));
    internal static readonly WM.Brush Warn = Frozen(WM.Color.FromRgb(240, 170, 40));
    internal static readonly WM.Brush FieldBack = Frozen(WM.Color.FromRgb(24, 24, 26));
    internal static readonly WM.Brush FieldBorder = Frozen(WM.Color.FromRgb(105, 105, 112));
    internal static readonly WM.Brush Accent = Frozen(WM.Color.FromRgb(80, 160, 255));

    internal static WM.Brush Frozen(WM.Color c)
    {
        WM.SolidColorBrush b = new(c);
        b.Freeze();
        return b;
    }

    public HydroNumberWindow(HydroSnapshot snap, HydroNumbering.Memory memory, HashSet<string>? seeds, HydroNumberWindow? previous)
    {
        _snap = snap;
        _memory = memory;
        _seeds = seeds is { Count: > 0 } ? seeds : null;
        _plan = new NumberPlanView(this);
        Title = "HYDRO - Numerar rede (DRENNUM)";
        Width = 1280;
        Height = 780;
        MinWidth = 900;
        MinHeight = 520;
        Background = Back;
        Foreground = TextBrush;
        WindowStartupLocation = SW.WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Redes: as da selecao; senao as marcadas antes; senao todas.
        if (_seeds is not null)
        {
            foreach (NetworkInfo n in snap.Networks.Where(n => n.StructureByHandle.Keys.Concat(n.PipeByHandle.Keys).Any(_seeds.Contains)))
                _netChecked.Add(n.Handle);
        }
        else if (previous is not null)
        {
            foreach (string h in previous._netChecked)
                _netChecked.Add(h);
        }

        if (_netChecked.Count == 0)
        {
            foreach (NetworkInfo n in snap.Networks)
                _netChecked.Add(n.Handle);
        }

        if (previous is not null)
        {
            foreach (string h in previous._excluded)
                _excluded.Add(h);
        }

        for (int d = 1; d <= 4; d++)
            _digits.Items.Add(new WC.ComboBoxItem { Content = d == 1 ? "1 (1, 2...)" : d == 2 ? "2 (01, 02...)" : d == 3 ? "3 (001...)" : "4 (0001...)", Tag = d });
        _digits.SelectedIndex = Math.Clamp(memory.Digits, 1, 4) - 1;
        _digits.SelectionChanged += (_, _) => Recalc();
        _bySize.IsChecked = memory.BySize;
        _bySize.Checked += (_, _) => RebuildGroups();
        _bySize.Unchecked += (_, _) => RebuildGroups();

        Content = BuildUi();
        BuildNetworks();
        RebuildGroups();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Escape)
                Close();
        };
    }

    // ------------------------------------------------------------------
    // Interface
    // ------------------------------------------------------------------

    private SW.UIElement BuildUi()
    {
        WC.StackPanel side = new();
        side.Children.Add(Header("1. Onde numerar"));
        side.Children.Add(_netPanel);
        WC.WrapPanel scopeButtons = new() { Margin = new SW.Thickness(6, 2, 6, 2) };
        scopeButtons.Children.Add(Button("Selecionar no desenho...", () =>
        {
            PickRequested = true;
            Close();
        }, "Escolha tubos/estruturas no desenho: entra so a parte da rede ligada a eles."));
        scopeButtons.Children.Add(Button("Rede inteira", () =>
        {
            _seeds = null;
            RebuildGroups();
        }, "Volta a numerar as redes marcadas inteiras."));
        scopeButtons.Children.Add(Button("Repor pecas tiradas", () =>
        {
            _excluded.Clear();
            Recalc();
        }, "Volta para o lote as pecas que voce tirou clicando na planta."));
        side.Children.Add(scopeButtons);
        side.Children.Add(_scope);

        side.Children.Add(Header("2. O que numerar e com que prefixo"));
        side.Children.Add(new WC.TextBlock
        {
            Text = "Desmarque o que deve ficar com o nome atual. Tipos com o MESMO prefixo seguem uma numeracao so (sem repetir numero).",
            Foreground = Muted,
            TextWrapping = SW.TextWrapping.Wrap,
            Margin = new SW.Thickness(6, 2, 6, 4)
        });
        side.Children.Add(_groupPanel);
        side.Children.Add(_bySize);

        side.Children.Add(Header("3. Numero inicial de cada prefixo"));
        side.Children.Add(_startPanel);
        WC.StackPanel digitsRow = new() { Orientation = WC.Orientation.Horizontal };
        digitsRow.Children.Add(new WC.TextBlock { Text = "Digitos:", Foreground = TextBrush, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 2, 0, 2) });
        digitsRow.Children.Add(_digits);
        side.Children.Add(digitsRow);

        side.Children.Add(Header("Avisos"));
        side.Children.Add(_warnings);

        WC.ScrollViewer sideScroll = new() { Content = side, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };

        // Previa em lista.
        WC.GridView gv = new();
        gv.Columns.Add(Column("Ordem", "Order", 50));
        gv.Columns.Add(Column("Atual", "Old", 110));
        gv.Columns.Add(Column("Novo", "New", 110));
        gv.Columns.Add(Column("Tipo", "Kind", 150));
        gv.Columns.Add(Column("Montante > jusante", "Path", 170));
        _list.View = gv;
        _list.Background = Pane;
        _list.Foreground = TextBrush;
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is Row r)
            {
                _plan.ShowItem(r.Id);
            }
        };

        WC.Grid right = new() { Width = 470, Background = Pane };
        right.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(3, SW.GridUnitType.Star) });
        right.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(4) });
        right.RowDefinitions.Add(new WC.RowDefinition { Height = new SW.GridLength(2, SW.GridUnitType.Star) });
        right.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        right.Children.Add(sideScroll);
        WC.GridSplitter split = new() { HorizontalAlignment = SW.HorizontalAlignment.Stretch, Background = Head, ResizeDirection = WC.GridResizeDirection.Rows };
        WC.Grid.SetRow(split, 1);
        right.Children.Add(split);
        WC.DockPanel listHost = new();
        WC.TextBlock listHead = Header("Previa (clique: mostra na planta)");
        WC.DockPanel.SetDock(listHead, WC.Dock.Top);
        listHost.Children.Add(listHead);
        listHost.Children.Add(_list);
        WC.Grid.SetRow(listHost, 2);
        right.Children.Add(listHost);

        WC.WrapPanel buttons = new() { Margin = new SW.Thickness(6), HorizontalAlignment = SW.HorizontalAlignment.Right };
        WC.Button apply = Button("Aplicar no desenho", () =>
        {
            if (Result.Count == 0)
                return;
            SaveMemory();
            ApplyRequested = true;
            Close();
        }, "Renomeia tudo de uma vez (um U desfaz).", true);
        buttons.Children.Add(apply);
        buttons.Children.Add(Button("Fechar", Close, "Fecha sem mudar nada."));
        WC.Grid.SetRow(buttons, 3);
        right.Children.Add(buttons);

        WC.Grid stage = new() { Background = NumberPlanView.Ground };
        stage.Children.Add(_plan);
        stage.Children.Add(new WC.TextBlock
        {
            Text = "clique numa peca: tira/poe do lote   roda: zoom   arrastar: mover   duplo clique: enquadrar",
            Foreground = Muted,
            Margin = new SW.Thickness(8, 6, 8, 6),
            VerticalAlignment = SW.VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });

        WC.DockPanel root = new();
        WC.DockPanel.SetDock(right, WC.Dock.Right);
        root.Children.Add(right);
        root.Children.Add(stage);
        return root;
    }

    private static WC.GridViewColumn Column(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new SW.Data.Binding(path) };

    private static WC.TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = SW.FontWeights.Bold,
        Foreground = SW.Media.Brushes.White,
        Background = Head,
        Padding = new SW.Thickness(6, 3, 6, 3),
        Margin = new SW.Thickness(0, 6, 0, 2)
    };

    private static WC.Button Button(string text, Action action, string tip, bool primary = false)
    {
        WC.Button b = new()
        {
            Content = text,
            Margin = new SW.Thickness(0, 0, 6, 4),
            Padding = new SW.Thickness(10, 3, 10, 3),
            FontWeight = primary ? SW.FontWeights.Bold : SW.FontWeights.Normal,
            ToolTip = tip
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static WC.TextBox Field(string text, double width)
    {
        WC.TextBox box = new()
        {
            Text = text,
            Width = width,
            Background = FieldBack,
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new SW.Thickness(1),
            Padding = new SW.Thickness(3, 1, 3, 1),
            Margin = new SW.Thickness(4, 1, 4, 1),
            VerticalContentAlignment = SW.VerticalAlignment.Center
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = Accent;
            box.SelectAll();
        };
        box.LostKeyboardFocus += (_, _) => box.BorderBrush = FieldBorder;
        return box;
    }

    private void BuildNetworks()
    {
        _netPanel.Children.Clear();
        foreach (NetworkInfo n in _snap.Networks.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            WC.CheckBox cb = new()
            {
                Content = $"{n.Name}  ({n.Structures.Count} estruturas, {n.Pipes.Count} tubos)",
                IsChecked = _netChecked.Contains(n.Handle),
                Foreground = TextBrush,
                Margin = new SW.Thickness(6, 1, 6, 1)
            };
            string h = n.Handle;
            cb.Checked += (_, _) =>
            {
                _netChecked.Add(h);
                RebuildGroups();
            };
            cb.Unchecked += (_, _) =>
            {
                _netChecked.Remove(h);
                RebuildGroups();
            };
            _netPanel.Children.Add(cb);
        }
    }

    // ------------------------------------------------------------------
    // Lote, grupos e inicio
    // ------------------------------------------------------------------

    private string GroupKey(StructureInfo s) =>
        _bySize.IsChecked == true ? $"{s.Family} / {s.Size}" : (s.Family.Length > 0 ? s.Family : "(sem familia)");

    /// <summary>Pecas do lote: redes marcadas, e se houve selecao so a parte ligada a ela.</summary>
    private void BuildBatch()
    {
        List<NetworkInfo> nets = _snap.Networks.Where(n => _netChecked.Contains(n.Handle)).ToList();
        _structures = nets.SelectMany(n => n.Structures).ToList();
        _pipes = nets.SelectMany(n => n.Pipes).ToList();
        if (_seeds is not null)
        {
            HashSet<string> keep = Connected(nets, _seeds);
            _structures = _structures.Where(s => keep.Contains(s.Handle)).ToList();
            _pipes = _pipes.Where(p => keep.Contains(p.Handle)).ToList();
        }

        _groupOf = _structures.ToDictionary(s => s.Handle, GroupKey, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> Connected(List<NetworkInfo> nets, HashSet<string> seeds)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> stack = new(seeds);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!seen.Add(h))
                continue;
            foreach (NetworkInfo n in nets)
            {
                if (n.StructureByHandle.TryGetValue(h, out StructureInfo? s))
                {
                    foreach (string p in s.PipesIn.Concat(s.PipesOut).Concat(s.PipesUndefined))
                        stack.Push(p);
                }
                else if (n.PipeByHandle.TryGetValue(h, out PipeInfo? p))
                {
                    if (p.StartHandle.Length > 0)
                        stack.Push(p.StartHandle);
                    if (p.EndHandle.Length > 0)
                        stack.Push(p.EndHandle);
                }
            }
        }

        return seen;
    }

    private void RebuildGroups()
    {
        if (_building)
            return;
        _building = true;
        try
        {
            // Guarda o que estava na tela para nao perder ao trocar rede/tamanho.
            foreach (Group g in _groups)
            {
                _memory.Prefix[g.Key] = g.Prefix.Text;
                if (g.Check.IsChecked == true)
                    _memory.Disabled.Remove(g.Key);
                else
                    _memory.Disabled.Add(g.Key);
            }

            if (_pipePrefix is not null)
            {
                _memory.PipePrefix = _pipePrefix.Text;
                _memory.PipesEnabled = _pipeCheck.IsChecked == true;
            }

            BuildBatch();
            _groups.Clear();
            _groupPanel.Children.Clear();
            int ci = 0;
            foreach (IGrouping<string, StructureInfo> g in _structures.GroupBy(s => _groupOf[s.Handle], StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string prefix = _memory.Prefix.TryGetValue(g.Key, out string? p) && p.Length > 0 ? p
                    : MostCommonPrefix(g.Select(s => s.Name)) ?? Suggest(g.First().Family);
                Group grp = new()
                {
                    Key = g.Key,
                    Label = g.Key,
                    Count = g.Count(),
                    Color = Palette[ci++ % Palette.Length]
                };
                grp.Check = new WC.CheckBox { IsChecked = !_memory.Disabled.Contains(g.Key), VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 1, 4, 1) };
                grp.Prefix = Field(prefix, 80);
                _groups.Add(grp);
                _groupPanel.Children.Add(GroupRow(grp.Check, grp.Color, $"{grp.Label}  ({grp.Count})", grp.Prefix, false));
            }

            string pipePrefix = _memory.PipePrefix.Length > 0 ? _memory.PipePrefix : MostCommonPrefix(_pipes.Select(x => x.Name)) ?? "T-";
            _pipeCheck = new WC.CheckBox { IsChecked = _memory.PipesEnabled, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 1, 4, 1) };
            _pipePrefix = Field(pipePrefix, 80);
            _groupPanel.Children.Add(GroupRow(_pipeCheck, NumberPlanView.PipeColor, $"Tubos  ({_pipes.Count})", _pipePrefix, true));

            WC.WrapPanel quick = new() { Margin = new SW.Thickness(6, 2, 6, 2) };
            quick.Children.Add(Button("So estruturas", () => SetAll(true, false), "Marca todas as estruturas e desmarca os tubos."));
            quick.Children.Add(Button("So tubos", () => SetAll(false, true), "Marca so os tubos."));
            quick.Children.Add(Button("Tudo", () => SetAll(true, true), "Marca tudo."));
            _groupPanel.Children.Add(quick);

            foreach (Group g in _groups)
            {
                g.Check.Checked += (_, _) => Recalc();
                g.Check.Unchecked += (_, _) => Recalc();
                g.Prefix.TextChanged += (_, _) => Recalc();
            }

            _pipeCheck.Checked += (_, _) => Recalc();
            _pipeCheck.Unchecked += (_, _) => Recalc();
            _pipePrefix.TextChanged += (_, _) => Recalc();
            _plan.Load(_snap.Networks.Where(n => _netChecked.Contains(n.Handle)).ToList());
        }
        finally
        {
            _building = false;
        }

        Recalc();
        _plan.Fit();
    }

    private void SetAll(bool structures, bool pipes)
    {
        _building = true;
        foreach (Group g in _groups)
            g.Check.IsChecked = structures;
        _pipeCheck.IsChecked = pipes;
        _building = false;
        Recalc();
    }

    private WC.Grid GroupRow(WC.CheckBox check, WM.Color color, string label, WC.TextBox prefix, bool pipe)
    {
        WC.Grid row = new() { Margin = new SW.Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        row.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = SW.GridLength.Auto });
        row.Children.Add(check);
        SW.Shapes.Shape swatch = pipe
            ? new SW.Shapes.Rectangle { Width = 14, Height = 4, Fill = new WM.SolidColorBrush(color) }
            : new SW.Shapes.Rectangle { Width = 11, Height = 11, Fill = new WM.SolidColorBrush(color), Stroke = SW.Media.Brushes.Black };
        swatch.VerticalAlignment = SW.VerticalAlignment.Center;
        swatch.Margin = new SW.Thickness(0, 0, 6, 0);
        WC.Grid.SetColumn(swatch, 1);
        row.Children.Add(swatch);
        WC.TextBlock text = new() { Text = label, Foreground = TextBrush, VerticalAlignment = SW.VerticalAlignment.Center, TextTrimming = SW.TextTrimming.CharacterEllipsis, ToolTip = label };
        WC.Grid.SetColumn(text, 2);
        row.Children.Add(text);
        WC.TextBlock pl = new() { Text = "prefixo", Foreground = Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(6, 0, 0, 0) };
        WC.Grid.SetColumn(pl, 3);
        row.Children.Add(pl);
        WC.Grid.SetColumn(prefix, 4);
        row.Children.Add(prefix);
        return row;
    }

    private static string? MostCommonPrefix(IEnumerable<string> names) =>
        names.Select(Numbering.PrefixOf).Where(p => p is not null)
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length)
            .Select(g => g.Key).FirstOrDefault();

    private static string Suggest(string family)
    {
        string letters = new(family.Where(char.IsLetter).Take(3).ToArray());
        return (letters.Length == 0 ? "EST" : letters.ToUpperInvariant()) + "-";
    }

    /// <summary>Nomes que ficam: fora do lote, tirados na planta e de tipos desmarcados.</summary>
    private HashSet<string> KeptNames(HashSet<string> renamed)
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInfo n in _snap.Networks)
        {
            foreach (StructureInfo s in n.Structures)
            {
                if (!renamed.Contains(s.Handle))
                    used.Add(s.Name);
            }

            foreach (PipeInfo p in n.Pipes)
            {
                if (!renamed.Contains(p.Handle))
                    used.Add(p.Name);
            }
        }

        return used;
    }

    private void Recalc()
    {
        if (_building || _pipePrefix is null)
            return;

        NumOptions o = new() { Digits = (_digits.SelectedItem as WC.ComboBoxItem)?.Tag is int d ? d : 2 };
        foreach (Group g in _groups)
            o.Groups[g.Key] = new NumGroupRule(g.Check.IsChecked == true && g.Prefix.Text.Length > 0, g.Prefix.Text);
        o.Pipes = new NumGroupRule(_pipeCheck.IsChecked == true && _pipePrefix.Text.Length > 0, _pipePrefix.Text);
        foreach (string h in _excluded)
            o.Excluded.Add(h);

        // Quem sera renomeado (para saber quais nomes ficam e devem ser pulados).
        HashSet<string> renamed = new(StringComparer.OrdinalIgnoreCase);
        foreach (StructureInfo s in _structures)
        {
            if (!_excluded.Contains(s.Handle) && o.Groups.TryGetValue(_groupOf[s.Handle], out NumGroupRule? r) && r.Enabled)
                renamed.Add(s.Handle);
        }

        if (o.Pipes.Enabled)
        {
            foreach (PipeInfo p in _pipes.Where(p => !_excluded.Contains(p.Handle)))
                renamed.Add(p.Handle);
        }

        foreach (string name in KeptNames(renamed))
            o.UsedOutside.Add(name);

        // Inicio de cada prefixo em uso (um campo por prefixo; o texto digitado fica guardado).
        List<string> prefixes = o.Groups.Values.Where(g => g.Enabled).Select(g => g.Prefix)
            .Concat(o.Pipes.Enabled ? new[] { o.Pipes.Prefix } : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach ((string p, WC.TextBox box) in _starts)
            _startText[p] = box.Text;
        _starts.Clear();
        _startPanel.Children.Clear();
        foreach (string p in prefixes)
        {
            int suggested = Numbering.SuggestStart(p, o.UsedOutside);
            string text = _startText.TryGetValue(p, out string? t) ? t : suggested.ToString(CultureInfo.InvariantCulture);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) && start > 0)
                o.StartByPrefix[p] = start;
            WC.TextBox box = Field(text, 60);
            string key = p;
            box.LostKeyboardFocus += (_, _) =>
            {
                if (box.Text != (_startText.TryGetValue(key, out string? old) ? old : ""))
                {
                    _startText[key] = box.Text;
                    Recalc();
                }
            };
            box.KeyDown += (_, e) =>
            {
                if (e.Key == WI.Key.Enter)
                {
                    _startText[key] = box.Text;
                    Recalc();
                }
            };
            _starts[p] = box;
            WC.StackPanel row = new() { Orientation = WC.Orientation.Horizontal, Margin = new SW.Thickness(6, 1, 6, 1) };
            row.Children.Add(new WC.TextBlock { Text = p, Width = 80, Foreground = TextBrush, FontWeight = SW.FontWeights.SemiBold, VerticalAlignment = SW.VerticalAlignment.Center });
            row.Children.Add(new WC.TextBlock { Text = "comeca em", Foreground = Muted, VerticalAlignment = SW.VerticalAlignment.Center });
            row.Children.Add(box);
            row.Children.Add(new WC.TextBlock { Name = "range", Foreground = Muted, VerticalAlignment = SW.VerticalAlignment.Center });
            _startPanel.Children.Add(row);
        }

        List<NumNode> nodes = _structures.Select(s => new NumNode(s.Handle, _groupOf[s.Handle], s.Name, s.Point.X, s.Point.Y)).ToList();
        HashSet<string> inBatch = nodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? In(string? h) => h is not null && inBatch.Contains(h) ? h : null;
        List<NumEdge> edges = _pipes.Select(p => new NumEdge(p.Handle, p.Name, In(p.StartHandle), In(p.EndHandle), In(p.FromHandle), In(p.ToHandle), p.Length2D)).ToList();
        Result = Numbering.Plan(nodes, edges, o, out List<string> warnings);

        // Faixa de cada prefixo ao lado do inicio.
        foreach (WC.StackPanel row in _startPanel.Children.OfType<WC.StackPanel>())
        {
            if (row.Children[0] is not WC.TextBlock pt || row.Children[3] is not WC.TextBlock range)
                continue;
            List<NumResult> mine = Result.Where(r => Numbering.NumberOf(pt.Text, r.NewName) is not null && r.NewName.StartsWith(pt.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            range.Text = mine.Count == 0 ? "" : $"  {mine.Min(r => Numbering.NumberOf(pt.Text, r.NewName))}..{mine.Max(r => Numbering.NumberOf(pt.Text, r.NewName))}  ({mine.Count} peca(s))";
        }

        int structs = Result.Count(r => !r.IsPipe), pipes = Result.Count(r => r.IsPipe);
        _scope.Text = (_seeds is null ? "Rede(s) inteira(s). " : "So a parte ligada a selecao. ")
            + $"No lote: {_structures.Count} estruturas e {_pipes.Count} tubos. Vao mudar: {structs} estrutura(s), {pipes} tubo(s)."
            + (_excluded.Count > 0 ? $" Tiradas na planta: {_excluded.Count}." : "");
        _warnings.Text = warnings.Count == 0 ? "Nenhum." : string.Join("\n", warnings.Take(30)) + (warnings.Count > 30 ? $"\n... e mais {warnings.Count - 30}." : "");
        _warnings.Foreground = warnings.Count == 0 ? Muted : Warn;

        Dictionary<string, Group> groupByKey = _groups.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
        _list.ItemsSource = Result.OrderBy(r => r.IsPipe).ThenBy(r => r.Order).Select(r => new Row
        {
            Id = r.Id,
            Order = r.Order,
            Old = r.OldName,
            New = r.NewName,
            Kind = r.IsPipe ? "Tubo" : r.Group,
            Path = r.IsPipe ? $"{r.UpName} > {r.DownName}" : ""
        }).ToList();

        Dictionary<string, WM.Color> colors = _structures.ToDictionary(s => s.Handle, s => groupByKey.TryGetValue(_groupOf[s.Handle], out Group? g) ? g.Color : WM.Colors.Gray, StringComparer.OrdinalIgnoreCase);
        _plan.SetPlan(Result.ToDictionary(r => r.Id, r => r.NewName, StringComparer.OrdinalIgnoreCase), colors, inBatch.Concat(_pipes.Select(p => p.Handle)).ToHashSet(StringComparer.OrdinalIgnoreCase), _excluded);
    }

    /// <summary>Clique na planta: tira/poe a peca do lote.</summary>
    internal void Toggle(string handle)
    {
        if (!_excluded.Remove(handle))
            _excluded.Add(handle);
        Recalc();
    }

    private void SaveMemory()
    {
        foreach (Group g in _groups)
        {
            _memory.Prefix[g.Key] = g.Prefix.Text;
            if (g.Check.IsChecked == true)
                _memory.Disabled.Remove(g.Key);
            else
                _memory.Disabled.Add(g.Key);
        }

        _memory.PipePrefix = _pipePrefix.Text;
        _memory.PipesEnabled = _pipeCheck.IsChecked == true;
        _memory.BySize = _bySize.IsChecked == true;
        _memory.Digits = (_digits.SelectedItem as WC.ComboBoxItem)?.Tag is int d ? d : 2;
    }

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public int Order { get; init; }
        public string Old { get; init; } = "";
        public string New { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Path { get; init; } = "";
    }
}

/// <summary>
/// Planta da numeracao: tubos com seta do fluxo, estruturas na cor do tipo, nome novo em
/// destaque e o atual pequeno embaixo. Fora do lote fica apagado; tirado do lote, riscado.
/// </summary>
internal sealed class NumberPlanView : SW.FrameworkElement
{
    internal static readonly WM.Brush Ground = HydroNumberWindow.Frozen(WM.Color.FromRgb(30, 31, 34));
    internal static readonly WM.Color PipeColor = WM.Color.FromRgb(70, 170, 235);
    private static readonly WM.Typeface Face = new("Segoe UI");
    private static readonly WM.Typeface Bold = new(new WM.FontFamily("Segoe UI"), SW.FontStyles.Normal, SW.FontWeights.SemiBold, SW.FontStretches.Normal);

    private readonly HydroNumberWindow _owner;
    private List<NetworkInfo> _nets = new();
    private Dictionary<string, string> _newNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, WM.Color> _colors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _batch = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private string? _focus;

    private double _scale = 1, _cx, _cy;
    private SW.Point? _drag;
    private SW.Point _down;

    public NumberPlanView(HydroNumberWindow owner)
    {
        _owner = owner;
        ClipToBounds = true;
        Focusable = true;
        MouseWheel += (_, e) =>
        {
            SW.Point p = e.GetPosition(this);
            (double wx, double wy) = ToWorld(p);
            _scale *= e.Delta > 0 ? 1.2 : 1 / 1.2;
            (double nx, double ny) = ToWorld(p);
            _cx += wx - nx;
            _cy += wy - ny;
            InvalidateVisual();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                Fit();
                return;
            }

            _drag = _down = e.GetPosition(this);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (_drag is not SW.Point from)
                return;
            SW.Point p = e.GetPosition(this);
            _cx -= (p.X - from.X) / _scale;
            _cy += (p.Y - from.Y) / _scale;
            _drag = p;
            InvalidateVisual();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            _drag = null;
            SW.Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _down.X) < 4 && Math.Abs(p.Y - _down.Y) < 4 && Hit(p) is string h && _batch.Contains(h))
                _owner.Toggle(h);
        };
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public void Load(List<NetworkInfo> nets)
    {
        _nets = nets;
        InvalidateVisual();
    }

    public void SetPlan(Dictionary<string, string> newNames, Dictionary<string, WM.Color> colors, HashSet<string> batch, HashSet<string> excluded)
    {
        _newNames = newNames;
        _colors = colors;
        _batch = batch;
        _excluded = excluded;
        InvalidateVisual();
    }

    public void ShowItem(string handle)
    {
        _focus = handle;
        foreach (NetworkInfo n in _nets)
        {
            if (n.StructureByHandle.TryGetValue(handle, out StructureInfo? s))
                (_cx, _cy) = (s.Point.X, s.Point.Y);
            else if (n.PipeByHandle.TryGetValue(handle, out PipeInfo? p))
                (_cx, _cy) = ((p.Start.X + p.End.X) / 2, (p.Start.Y + p.End.Y) / 2);
        }

        InvalidateVisual();
    }

    public void Fit()
    {
        Bounds b = Bounds.Empty;
        foreach (NetworkInfo n in _nets)
            b = b.Union(n.Bounds);
        if (b.IsEmpty)
            return;
        double w = Math.Max(ActualWidth, 400), h = Math.Max(ActualHeight, 300);
        double dx = Math.Max(b.MaxX - b.MinX, 1), dy = Math.Max(b.MaxY - b.MinY, 1);
        _scale = Math.Min(w / dx, h / dy) * 0.88;
        _cx = (b.MinX + b.MaxX) / 2;
        _cy = (b.MinY + b.MaxY) / 2;
        InvalidateVisual();
    }

    private SW.Point ToScreen(P2 p) => new(((p.X - _cx) * _scale) + (ActualWidth / 2), (ActualHeight / 2) - ((p.Y - _cy) * _scale));

    private (double, double) ToWorld(SW.Point p) => (((p.X - (ActualWidth / 2)) / _scale) + _cx, ((ActualHeight / 2) - p.Y) / _scale + _cy);

    private string? Hit(SW.Point p)
    {
        string? best = null;
        double bestD = 10;
        foreach (NetworkInfo n in _nets)
        {
            foreach (StructureInfo s in n.Structures)
            {
                SW.Point q = ToScreen(s.Point);
                double d = (q - p).Length;
                if (d < bestD)
                {
                    bestD = d;
                    best = s.Handle;
                }
            }
        }

        if (best is not null)
            return best;
        bestD = 6;
        foreach (NetworkInfo n in _nets)
        {
            foreach (PipeInfo pi in n.Pipes)
            {
                double d = SegDist(p, ToScreen(pi.Start), ToScreen(pi.End));
                if (d < bestD)
                {
                    bestD = d;
                    best = pi.Handle;
                }
            }
        }

        return best;
    }

    private static double SegDist(SW.Point p, SW.Point a, SW.Point b)
    {
        SW.Vector ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p - a) * ab) / len2, 0, 1);
        return (p - (a + (ab * t))).Length;
    }

    protected override void OnRender(WM.DrawingContext dc)
    {
        dc.DrawRectangle(Ground, null, new SW.Rect(0, 0, ActualWidth, ActualHeight));
        if (_nets.Count == 0)
            return;
        double dip = WM.VisualTreeHelper.GetDpi(this).PixelsPerDip;
        List<SW.Rect> taken = new();
        bool Free(SW.Rect r) => !taken.Any(t => t.IntersectsWith(r));

        WM.Pen dim = new(HydroNumberWindow.Frozen(WM.Color.FromRgb(85, 85, 90)), 1.2);
        WM.Pen pipePen = new(new WM.SolidColorBrush(PipeColor), 2.2);
        WM.Pen pipeOff = new(HydroNumberWindow.Frozen(WM.Color.FromRgb(120, 120, 125)), 1.6) { DashStyle = WM.DashStyles.Dash };
        WM.Pen focusPen = new(HydroNumberWindow.Frozen(WM.Color.FromRgb(255, 255, 255)), 3);

        // Tubos.
        foreach (NetworkInfo n in _nets)
        {
            foreach (PipeInfo p in n.Pipes)
            {
                SW.Point a = ToScreen(p.UpPoint), b = ToScreen(p.DownPoint);
                bool inBatch = _batch.Contains(p.Handle), off = _excluded.Contains(p.Handle);
                WM.Pen pen = !inBatch ? dim : off || !_newNames.ContainsKey(p.Handle) ? pipeOff : pipePen;
                if (p.Handle == _focus)
                    dc.DrawLine(focusPen, a, b);
                dc.DrawLine(pen, a, b);
                SW.Vector v = b - a;
                double len = v.Length;
                if (len > 30 && p.Defined)
                {
                    v /= len;
                    SW.Point m = a + (v * (len / 2));
                    SW.Vector nrm = new(-v.Y, v.X);
                    WM.StreamGeometry g = new();
                    using (WM.StreamGeometryContext c = g.Open())
                    {
                        c.BeginFigure(m + (v * 6), true, true);
                        c.LineTo(m - (v * 5) + (nrm * 4.5), true, false);
                        c.LineTo(m - (v * 5) - (nrm * 4.5), true, false);
                    }

                    g.Freeze();
                    dc.DrawGeometry(pen.Brush, null, g);
                }
            }
        }

        // Estruturas.
        foreach (NetworkInfo n in _nets)
        {
            foreach (StructureInfo s in n.Structures)
            {
                SW.Point c = ToScreen(s.Point);
                bool inBatch = _batch.Contains(s.Handle), off = _excluded.Contains(s.Handle);
                WM.Color col = inBatch && _colors.TryGetValue(s.Handle, out WM.Color cc) ? cc : WM.Color.FromRgb(90, 90, 95);
                bool renamed = _newNames.ContainsKey(s.Handle);
                WM.Brush fill = new WM.SolidColorBrush(renamed ? col : WM.Color.FromArgb(90, col.R, col.G, col.B));
                SW.Rect r = new(c.X - 5, c.Y - 5, 10, 10);
                if (s.Handle == _focus)
                    dc.DrawEllipse(null, focusPen, c, 10, 10);
                dc.DrawRectangle(fill, new WM.Pen(WM.Brushes.Black, 1), r);
                if (off)
                {
                    WM.Pen x = new(WM.Brushes.OrangeRed, 2);
                    dc.DrawLine(x, new SW.Point(c.X - 7, c.Y - 7), new SW.Point(c.X + 7, c.Y + 7));
                    dc.DrawLine(x, new SW.Point(c.X - 7, c.Y + 7), new SW.Point(c.X + 7, c.Y - 7));
                }

                taken.Add(r);
            }
        }

        // Rotulos: nome novo (e o atual pequeno) nas estruturas, depois nos tubos; so onde couber.
        void Label(string handle, SW.Point anchor, string oldName, bool pipe)
        {
            string main = _newNames.TryGetValue(handle, out string? nn) ? nn : oldName;
            bool changed = nn is not null && !string.Equals(nn, oldName, StringComparison.Ordinal);
            WM.Brush brush = !_batch.Contains(handle) ? HydroNumberWindow.Muted : nn is null ? HydroNumberWindow.Muted : pipe ? new WM.SolidColorBrush(PipeColor) : WM.Brushes.White;
            WM.FormattedText t1 = new(main, CultureInfo.InvariantCulture, SW.FlowDirection.LeftToRight, nn is null ? Face : Bold, pipe ? 11 : 12.5, brush, dip);
            WM.FormattedText? t2 = changed ? new WM.FormattedText(oldName, CultureInfo.InvariantCulture, SW.FlowDirection.LeftToRight, Face, 9.5, HydroNumberWindow.Muted, dip) : null;
            double w = Math.Max(t1.Width, t2?.Width ?? 0) + 6, h = t1.Height + (t2?.Height ?? 0) + 2;
            foreach (SW.Point p in new[] { new SW.Point(anchor.X + 8, anchor.Y - h - 2), new SW.Point(anchor.X + 8, anchor.Y + 4), new SW.Point(anchor.X - w - 8, anchor.Y - h - 2), new SW.Point(anchor.X - w - 8, anchor.Y + 4), new SW.Point(anchor.X - (w / 2), anchor.Y - h - 8), new SW.Point(anchor.X - (w / 2), anchor.Y + 8) })
            {
                SW.Rect r = new(p.X, p.Y, w, h);
                if (!Free(r))
                    continue;
                dc.DrawRoundedRectangle(HydroNumberWindow.Frozen(WM.Color.FromArgb(200, 28, 29, 32)), null, r, 2.5, 2.5);
                dc.DrawText(t1, new SW.Point(r.X + 3, r.Y + 1));
                if (t2 is not null)
                    dc.DrawText(t2, new SW.Point(r.X + 3, r.Y + 1 + t1.Height));
                taken.Add(r);
                return;
            }
        }

        foreach (NetworkInfo n in _nets)
        {
            foreach (StructureInfo s in n.Structures.OrderByDescending(s => _newNames.ContainsKey(s.Handle)))
                Label(s.Handle, ToScreen(s.Point), s.Name, false);
        }

        foreach (NetworkInfo n in _nets)
        {
            foreach (PipeInfo p in n.Pipes.OrderByDescending(p => _newNames.ContainsKey(p.Handle)))
            {
                SW.Point a = ToScreen(p.Start), b = ToScreen(p.End);
                if ((b - a).Length < 40)
                    continue;
                Label(p.Handle, new SW.Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), p.Name, true);
            }
        }
    }
}
