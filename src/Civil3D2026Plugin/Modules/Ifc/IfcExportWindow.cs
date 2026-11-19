using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using SW = System.Windows;
using WC = System.Windows.Controls;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;
using Civil3D2026Plugin.UI.Common;

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// Janela do C3DIFC: arquivo de saída (lembrado), o que exportar em árvore com caixas de marcar
/// (corredores, eixos, lombofaixas por peça e por travessia, superfícies, redes, objetos escolhidos no desenho),
/// opções e as classes IFC (janela própria, sem arquivo de texto). Tudo fica gravado no JSON do projeto.
/// 19/11/2026: "Selecionar no desenho" marca a caixa do que já está na lista (eixo, corredor, superfície, rede);
/// caixa marcada = sai no IFC (eixo marcado sai mesmo com o corredor desmarcado).
/// </summary>
internal sealed class IfcExportWindow : SW.Window
{
    // Resultado (lido pelo comando depois que a janela fecha com Exportar).
    public string IfcPath { get; private set; } = "";
    public List<ObjectId> Ids { get; } = new();
    public List<ObjectId> Rebuild { get; } = new();
    public string Selection { get; private set; } = "";

    private readonly Document _doc;
    private readonly string _criteriaPath;
    private readonly IfcCriteria _c;
    private readonly ToolTheme T = ToolTheme.Current;
    private readonly HashSet<ObjectId> _manual = new();

    private sealed class Node
    {
        public string Key = "";
        public string Label = "";
        public List<ObjectId> Ids = new();
        public WC.CheckBox Box = null!;
        public List<Node> Children = new();
        public string Kind = "";
        public bool IsReference;
        public bool OutOfDate;
        public string Extra = "";
        public string Detail = "";
    }

    private readonly List<Node> _roots = new();
    private readonly WC.TreeView _tree = new();
    private readonly WC.TextBox _file = new();
    private readonly WC.TextBox _project = new();
    private readonly WC.TextBox _site = new();
    private readonly WC.ComboBox _axes = new();
    private readonly WC.ComboBox _refs = new();
    private readonly WC.CheckBox _simple = new();
    private readonly WC.TextBox _layers = new();
    private readonly WC.TextBlock _status = new();
    private bool _syncing;

    public IfcExportWindow(Document doc, IfcCriteria criteria, string criteriaPath, IEnumerable<ObjectId> pickfirst)
    {
        _doc = doc;
        _c = criteria;
        _criteriaPath = criteriaPath;
        foreach (string h in _c.ManuaisHandles)
        {
            if (TryHandle(h, out ObjectId id))
                _manual.Add(id);
        }
        Title = "Exportar IFC 4.3";
        Width = 800;
        Height = 840;
        MinWidth = 640;
        MinHeight = 580;
        WindowStartupLocation = SW.WindowStartupLocation.CenterOwner;
        Background = T.Back;
        Foreground = T.Text;
        ToolStyles.Aplicar(Resources, T);
        Content = BuildUi();
        _file.Text = _c.SuggestedIfc(doc.Database.Filename);
        Fill();
        List<ObjectId> pick = pickfirst.ToList();
        if (pick.Count > 0)
            ApplyPicked(pick);
    }

    // ------------------------------------------------------------------
    // Interface
    // ------------------------------------------------------------------

    private SW.UIElement BuildUi()
    {
        var root = new WC.DockPanel { Margin = new SW.Thickness(10) };

        var top = new WC.Grid { Margin = new SW.Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(160) });
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        for (int i = 0; i < 6; i++)
            top.RowDefinitions.Add(new WC.RowDefinition { Height = SW.GridLength.Auto });
        void Row(int r, string label, SW.UIElement el, string tip)
        {
            var l = new WC.TextBlock { Text = label, Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 3, 6, 3), ToolTip = tip };
            WC.Grid.SetRow(l, r);
            top.Children.Add(l);
            if (el is SW.FrameworkElement fe)
            {
                fe.Margin = new SW.Thickness(0, 3, 0, 3);
                fe.ToolTip = tip;
            }
            WC.Grid.SetRow(el, r);
            WC.Grid.SetColumn(el, 1);
            top.Children.Add(el);
        }

        var fileRow = new WC.DockPanel();
        WC.Button browse = Button("Procurar...", Browse, "Escolher a pasta e o nome do arquivo IFC.");
        browse.Margin = new SW.Thickness(6, 0, 0, 0);
        WC.DockPanel.SetDock(browse, WC.Dock.Right);
        fileRow.Children.Add(browse);
        fileRow.Children.Add(_file);
        Row(0, "Arquivo", fileRow, "Arquivo IFC de saída. O último usado neste desenho é lembrado.");
        Row(1, "Projeto", _project, "Nome do projeto no IFC (IfcProject).");
        Row(2, "Local", _site, "Nome do local da obra no IFC (IfcSite).");
        _axes.Items.Add("Eixos centrais");
        _axes.Items.Add("Todos");
        _axes.Items.Add("Nenhum");
        _axes.SelectionChanged += (_, _) => { if (!_syncing) ApplyAxisRule(); };
        Row(3, "Alinhamentos", _axes, "Alinhamentos dos corredores marcados automaticamente. Eixos centrais: sem bordos, offsets e retornos de esquina. Cada um pode ser alterado na lista.");
        _refs.Items.Add("Excluir");
        _refs.Items.Add("Perguntar");
        _refs.Items.Add("Incluir");
        _refs.SelectionChanged += (_, _) => { if (!_syncing) ApplyReferenceRule(); };
        Row(4, "Referências externas", _refs, "Corredores vindos de outro desenho por data shortcut. Em geral são exportados no desenho de origem.");
        _simple.Content = "Simplificar grelhas";
        _simple.Foreground = T.Text;
        Row(5, "", _simple, "Exporta as grelhas como chapa com o mesmo contorno e espessura. Reduz bastante o tamanho do arquivo; o desenho não é alterado.");
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        root.Children.Add(top);

        // Rodapé: botões.
        var bottom = new WC.DockPanel { Margin = new SW.Thickness(0, 8, 0, 0) };
        var left = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        left.Children.Add(Button("Selecionar no desenho", PickInDrawing,
            "Seleciona objetos no desenho. Os que já estão na lista são marcados; os demais entram em Outros objetos."));
        left.Children.Add(Button("Limpar outros objetos", ClearManual, "Remove da lista os itens de Outros objetos."));
        left.Children.Add(Button("Classes IFC...", EditMapping, "Classe IFC por layer ou estilo, e o que não é exportado."));
        WC.DockPanel.SetDock(left, WC.Dock.Left);
        bottom.Children.Add(left);
        var right = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right };
        WC.Button export = Button("Exportar", Export, "Exporta para o arquivo indicado.");
        export.FontWeight = SW.FontWeights.Bold;
        right.Children.Add(export);
        right.Children.Add(Button("Cancelar", () => { DialogResult = false; }, "Fecha sem exportar."));
        bottom.Children.Add(right);
        WC.DockPanel.SetDock(bottom, WC.Dock.Bottom);
        root.Children.Add(bottom);

        _status.Foreground = T.Muted;
        _status.Margin = new SW.Thickness(0, 6, 0, 0);
        _status.TextWrapping = SW.TextWrapping.Wrap;
        WC.DockPanel.SetDock(_status, WC.Dock.Bottom);
        root.Children.Add(_status);

        var layersRow = new WC.DockPanel { Margin = new SW.Thickness(0, 6, 0, 0) };
        var ll = new WC.TextBlock { Text = "Layers adicionais", Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 8, 0) };
        WC.DockPanel.SetDock(ll, WC.Dock.Left);
        layersRow.Children.Add(ll);
        _layers.ToolTip = "Objetos destas layers também são exportados. Separe os nomes com ponto e vírgula.";
        layersRow.Children.Add(_layers);
        WC.DockPanel.SetDock(layersRow, WC.Dock.Bottom);
        root.Children.Add(layersRow);

        _tree.Background = T.Pane;
        _tree.Foreground = T.Text;
        _tree.BorderBrush = T.Line;
        root.Children.Add(_tree);
        return root;
    }

    private WC.Button Button(string text, Action action, string tip)
    {
        var b = new WC.Button { Content = text, Padding = new SW.Thickness(10, 3, 10, 3), Margin = new SW.Thickness(0, 0, 6, 0), ToolTip = tip };
        b.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                PluginLog.Erro("IFC", "Janela do C3DIFC: " + ex.Message, ex);
                _status.Text = "Erro: " + ex.Message;
            }
        };
        return b;
    }

    // ------------------------------------------------------------------
    // Árvore: o que existe no desenho
    // ------------------------------------------------------------------

    private void Fill()
    {
        _syncing = true;
        _project.Text = _c.ProjetoNome;
        _site.Text = _c.LocalNome;
        _axes.SelectedIndex = _c.EixosModo switch { "todos" => 1, "nenhum" => 2, _ => 0 };
        string rm = (_c.CorredoresReferenciados ?? "Nao").ToLowerInvariant();
        _refs.SelectedIndex = rm.StartsWith("perg") ? 1 : rm.StartsWith("sim") ? 2 : 0;
        _simple.IsChecked = _c.GrelhaSimplificada;
        _layers.Text = string.Join("; ", _c.LayersExtras);
        _roots.Clear();
        Database db = _doc.Database;

        // Corredores e eixos.
        var corr = new Node { Key = "CORREDORES", Label = "Corredores", Kind = "grupo" };
        var axes = new Node { Key = "EIXOS", Label = "Alinhamentos dos corredores", Kind = "grupo" };
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in MFRebaixoData.CorridorIds(db, tr))
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilCorridor c)
                    continue;
                bool reference = IsReference(c);
                bool stale = false;
                try
                {
                    stale = c.IsOutOfDate;
                }
                catch
                {
                    // Sem a informação.
                }
                int baselines = 0;
                var n = new Node { Key = "C:" + c.Name, Kind = "corredor", IsReference = reference, OutOfDate = stale, Ids = { id } };
                foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                {
                    baselines++;
                    ObjectId aid;
                    try
                    {
                        aid = b.AlignmentId;
                    }
                    catch
                    {
                        continue;
                    }
                    if (aid.IsNull || axes.Children.Any(x => x.Ids.Contains(aid)))
                        continue;
                    string an = "?", type = "?";
                    try
                    {
                        if (tr.GetObject(aid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Alignment al)
                        {
                            an = al.Name;
                            type = al.AlignmentType.ToString();
                        }
                    }
                    catch
                    {
                        // Nome não lido.
                    }
                    axes.Children.Add(new Node { Key = "A:" + an, Label = an, Detail = $"{TypeName(type)} · {c.Name}", Kind = "eixo", Ids = { aid }, Extra = type + "|" + c.Name });
                }
                n.Label = c.Name;
                n.Detail = string.Join(" · ", new[] { reference ? "Referência externa" : "", stale ? "Desatualizado" : "" }.Where(x => x.Length > 0));
                corr.Children.Add(n);
            }
            tr.Commit();
        }
        _roots.Add(corr);
        _roots.Add(axes);

        // Lombofaixas: por peça e por travessia.
        var lomb = new Node { Key = "LOMBOFAIXAS", Label = "Lombofaixas", Kind = "grupo" };
        var pecas = new Node { Key = "PECAS", Label = "Componentes", Kind = "grupo" };
        foreach ((string key, string label) in new[] { ("Plato", "Platô"), ("Grelhas", "Grelhas e tampas"), ("Vigas", "Vigas baldrame"), ("Sinalizacao", "Sinalização horizontal"), ("Planta", "Hachuras de planta") })
        {
            List<ObjectId> ids = MFTravessia.Objects(db, null, key);
            pecas.Children.Add(new Node { Key = "P:" + key, Label = label, Kind = "peca", Ids = ids });
        }
        var travs = new Node { Key = "TRAVESSIAS", Label = "Travessias", Kind = "grupo" };
        List<MFTravessia.Stored> stored = MFTravessia.FindAll(db).Select(x => x.Data).GroupBy(x => x.Id).Select(g => g.First()).ToList();
        foreach (string tid in MFTravessia.AllIds(db))
        {
            MFTravessia.Stored? d = stored.FirstOrDefault(x => x.Id == tid);
            string label = d is null ? $"União {tid}" : $"{MFTravessia.CodeOf(db, d)}  {d.LabelA} – {d.LabelB}";
            travs.Children.Add(new Node { Key = "T:" + tid, Label = label, Kind = "travessia", Ids = MFTravessia.Objects(db, new List<string> { tid }, "Tudo") });
        }
        travs.Children.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        lomb.Children.Add(pecas);
        lomb.Children.Add(travs);
        _roots.Add(lomb);

        // Superfícies e redes.
        var surf = new Node { Key = "SUPERFICIES", Label = "Superfícies", Kind = "grupo" };
        var nets = new Node { Key = "REDES", Label = "Redes de tubulação", Kind = "grupo" };
        try
        {
            var civ = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db);
            using Transaction tr = db.TransactionManager.StartTransaction();
            foreach (ObjectId sid in civ.GetSurfaceIds())
            {
                string name = (tr.GetObject(sid, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface)?.Name ?? "?";
                surf.Children.Add(new Node { Key = "S:" + name, Label = name, Kind = "superficie", Ids = { sid } });
            }
            foreach (ObjectId nid in civ.GetPipeNetworkIds())
            {
                string name = (tr.GetObject(nid, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Network)?.Name ?? "?";
                nets.Children.Add(new Node { Key = "R:" + name, Label = name, Kind = "rede", Ids = { nid } });
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            _status.Text = "Falha ao ler superfícies e redes: " + ex.Message;
        }
        _roots.Add(surf);
        if (nets.Children.Count > 0)
            _roots.Add(nets);

        // Outros objetos escolhidos no desenho: um por linha, com o tipo e o nome.
        var manual = new Node { Key = "MANUAIS", Label = "Outros objetos", Kind = "grupo" };
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in _manual.Where(x => !x.IsNull && !x.IsErased))
                {
                    (string label, string layer) = Describe(tr, id);
                    manual.Children.Add(new Node { Key = "M:" + id.Handle, Label = label, Detail = layer, Kind = "manual", Ids = { id } });
                }
            tr.Commit();
        }
        manual.Children.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        _roots.Add(manual);

        // Marcações (critérios gravados ou padrão).
        foreach (Node n in corr.Children)
            n.Extra = n.IsReference ? (_c.CorredoresReferenciaMarcados.Contains(KeyName(n)) || _refs.SelectedIndex == 2 ? "on" : "off") : (_c.CorredoresDesmarcados.Contains(KeyName(n)) ? "off" : "on");
        _tree.Items.Clear();
        foreach (Node r in _roots)
            _tree.Items.Add(Item(r));
        foreach (Node n in corr.Children)
            n.Box.IsChecked = n.Extra == "on" && _c.Corredores;
        ApplyAxisRule(keepManual: true);
        foreach (Node n in pecas.Children)
            n.Box.IsChecked = _c.Lombofaixas && _c.LombofaixasPecasMarcadas.Contains(n.Key[2..], StringComparer.OrdinalIgnoreCase);
        foreach (Node n in travs.Children)
            n.Box.IsChecked = _c.Lombofaixas && !_c.TravessiasDesmarcadas.Contains(n.Key[2..]);
        foreach (Node n in surf.Children)
            n.Box.IsChecked = _c.Superficies || _c.SuperficiesMarcadas.Contains(n.Key[2..], StringComparer.OrdinalIgnoreCase);
        foreach (Node n in nets.Children)
            n.Box.IsChecked = _c.RedesMarcadas.Contains(n.Key[2..], StringComparer.OrdinalIgnoreCase);
        foreach (Node n in manual.Children)
            n.Box.IsChecked = !_c.ManuaisDesmarcados.Contains(n.Key[2..]);
        foreach (Node r in _roots)
            SyncParent(r);
        _syncing = false;
        UpdateStatus();
    }

    private static string KeyName(Node n) => n.Key.Length > 2 ? n.Key[2..] : n.Key;

    private WC.TreeViewItem Item(Node n)
    {
        // Nome na caixa; detalhe (tipo, corredor, quantidade) em cinza ao lado.
        string detail = n.Detail;
        if (n.Children.Count > 0 || n.Kind == "peca")
            detail = Count(n).ToString(System.Globalization.CultureInfo.CurrentCulture);
        n.Box = new WC.CheckBox
        {
            Content = n.Label,
            Foreground = n.IsReference ? T.Muted : T.Text,
            IsThreeState = false,
            VerticalAlignment = SW.VerticalAlignment.Center
        };
        var header = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        header.Children.Add(n.Box);
        if (detail.Length > 0)
            header.Children.Add(new WC.TextBlock { Text = detail, Foreground = T.Muted, Margin = new SW.Thickness(10, 0, 0, 0), VerticalAlignment = SW.VerticalAlignment.Center });
        n.Box.Click += (_, _) =>
        {
            if (_syncing)
                return;
            _syncing = true;
            bool on = n.Box.IsChecked == true;
            SetAll(n, on);
            // Corredor marcado/desmarcado leva junto os eixos dele (pela regra de eixos).
            if (n.Kind == "corredor" || n.Key == "CORREDORES")
            {
                var names = new HashSet<string>((n.Kind == "corredor" ? new[] { n } : n.Children.ToArray()).Select(KeyName), StringComparer.OrdinalIgnoreCase);
                foreach (Node a in Find("EIXOS")?.Children ?? new List<Node>())
                {
                    if (names.Contains(AxisCorridor(a)))
                        a.Box.IsChecked = on && TypeRule(a);
                }
            }
            foreach (Node r in _roots)
                SyncParent(r);
            _syncing = false;
            UpdateStatus();
        };
        var item = new WC.TreeViewItem { Header = header, IsExpanded = n.Kind == "grupo" && n.Key is not ("EIXOS" or "TRAVESSIAS"), Foreground = T.Text };
        foreach (Node c in n.Children)
            item.Items.Add(Item(c));
        return item;
    }

    private static int Count(Node n) => n.Children.Count > 0 ? n.Children.Sum(Count) : n.Ids.Count;

    private static void SetAll(Node n, bool on)
    {
        n.Box.IsChecked = on;
        foreach (Node c in n.Children)
            SetAll(c, on);
    }

    /// <summary>Grupo marcado quando todos os filhos estão; meio-marcado quando parte.</summary>
    private static bool? SyncParent(Node n)
    {
        if (n.Children.Count == 0)
            return n.Box.IsChecked;
        var states = n.Children.Select(SyncParent).ToList();
        bool? s = states.All(x => x == true) ? true : states.All(x => x == false) ? false : null;
        n.Box.IsThreeState = s is null;
        n.Box.IsChecked = s;
        return s;
    }

    private Node? Find(string key) => All().FirstOrDefault(n => n.Key == key);

    private IEnumerable<Node> All()
    {
        var stack = new Stack<Node>(_roots);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            yield return n;
            foreach (Node c in n.Children)
                stack.Push(c);
        }
    }

    private static string AxisCorridor(Node a) => a.Extra.Split('|').ElementAtOrDefault(1) ?? "";

    /// <summary>A regra de eixos (centrais/todos/nenhum) pelo tipo do alinhamento.</summary>
    private bool TypeRule(Node a) =>
        _axes.SelectedIndex switch { 1 => true, 2 => false, _ => a.Extra.Split('|')[0].Equals("Centerline", StringComparison.OrdinalIgnoreCase) };

    /// <summary>Regra completa: o corredor do eixo está marcado e o tipo entra pela regra.</summary>
    private bool AxisRule(Node a)
    {
        Node? c = Find("C:" + AxisCorridor(a));
        return (c is null || c.Box.IsChecked == true) && TypeRule(a);
    }

    private void ApplyAxisRule(bool keepManual = false)
    {
        Node? axes = Find("EIXOS");
        if (axes is null)
            return;
        bool wasSyncing = _syncing;
        _syncing = true;
        foreach (Node a in axes.Children)
        {
            bool on = AxisRule(a);
            if (keepManual)
            {
                if (_c.EixosMarcados.Contains(KeyName(a)))
                    on = true;
                if (_c.EixosDesmarcados.Contains(KeyName(a)))
                    on = false;
            }
            a.Box.IsChecked = on;
        }
        SyncParent(axes);
        _syncing = wasSyncing;
        UpdateStatus();
    }

    private void ApplyReferenceRule()
    {
        Node? corr = Find("CORREDORES");
        if (corr is null)
            return;
        _syncing = true;
        foreach (Node n in corr.Children.Where(x => x.IsReference))
            n.Box.IsChecked = _refs.SelectedIndex == 2;
        ApplyAxisRule();
        SyncParent(corr);
        _syncing = false;
        UpdateStatus();
    }

    private List<ObjectId> Checked(out List<string> parts)
    {
        var ids = new List<ObjectId>();
        parts = new List<string>();
        Node? corr = Find("CORREDORES");
        if (corr is not null)
        {
            var on = corr.Children.Where(n => n.Box.IsChecked == true).ToList();
            ids.AddRange(on.SelectMany(n => n.Ids));
            if (on.Count > 0)
                parts.Add(Plural(on.Count, "corredor", "corredores"));
        }
        // Caixa marcada = sai (eixo marcado sai mesmo com o corredor dele desmarcado).
        var ax = Find("EIXOS")?.Children.Where(a => a.Box.IsChecked == true).ToList() ?? new List<Node>();
        ids.AddRange(ax.SelectMany(a => a.Ids));
        if (ax.Count > 0)
            parts.Add(Plural(ax.Count, "alinhamento", "alinhamentos"));
        Node? pecas = Find("PECAS");
        Node? travs = Find("TRAVESSIAS");
        if (pecas is not null && travs is not null)
        {
            var pieceIds = new HashSet<ObjectId>(pecas.Children.Where(p => p.Box.IsChecked == true).SelectMany(p => p.Ids));
            var travIds = new HashSet<ObjectId>(travs.Children.Where(t => t.Box.IsChecked == true).SelectMany(t => t.Ids));
            var both = pieceIds.Where(travIds.Contains).ToList();
            ids.AddRange(both);
            if (both.Count > 0)
                parts.Add(Plural(both.Count, "componente de lombofaixa", "componentes de lombofaixa"));
        }
        foreach ((string key, string one, string many) in new[] { ("SUPERFICIES", "superfície", "superfícies"), ("REDES", "rede", "redes") })
        {
            var on = Find(key)?.Children.Where(n => n.Box.IsChecked == true).ToList() ?? new List<Node>();
            ids.AddRange(on.SelectMany(n => n.Ids));
            if (on.Count > 0)
                parts.Add(Plural(on.Count, one, many));
        }
        var man = Find("MANUAIS")?.Children.Where(n => n.Box.IsChecked == true).ToList() ?? new List<Node>();
        ids.AddRange(man.SelectMany(n => n.Ids));
        if (man.Count > 0)
            parts.Add(Plural(man.Count, "outro objeto", "outros objetos"));
        return ids.Distinct().ToList();
    }

    private void UpdateStatus()
    {
        if (_roots.Count == 0)
            return;
        List<ObjectId> ids = Checked(out List<string> parts);
        _status.Text = ids.Count == 0
            ? "Nenhum objeto selecionado."
            : $"Objetos a exportar: {ids.Count}  ({string.Join(", ", parts)})";
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    internal static string Join(IList<string> parts) =>
        parts.Count <= 1 ? string.Join("", parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " e " + parts[^1];

    // ------------------------------------------------------------------
    // Botões
    // ------------------------------------------------------------------

    private void Browse()
    {
        string cur = _file.Text.Trim().Trim('"');
        string? dir = null;
        try
        {
            dir = Path.GetDirectoryName(cur);
        }
        catch
        {
            // Caminho inválido: usa a pasta do desenho.
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Arquivo IFC 4.3",
            Filter = "IFC (*.ifc)|*.ifc",
            InitialDirectory = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : Path.GetDirectoryName(_doc.Database.Filename),
            FileName = Path.GetFileName(cur).Length > 0 ? Path.GetFileName(cur) : Path.GetFileNameWithoutExtension(_doc.Database.Filename) + ".ifc",
            OverwritePrompt = false
        };
        if (dlg.ShowDialog(this) == true)
            _file.Text = dlg.FileName;
    }

    private void PickInDrawing()
    {
        Editor ed = _doc.Editor;
        var picked = new List<ObjectId>();
        using (EditorUserInteraction ui = ed.StartUserInteraction(this))
        {
            var po = new PromptSelectionOptions { MessageForAdding = "\nSelecione os objetos para exportar: " };
            PromptSelectionResult r = ed.GetSelection(po);
            if (r.Status == PromptStatus.OK)
                picked.AddRange(r.Value.GetObjectIds());
            ui.End();
        }
        if (picked.Count > 0)
            ApplyPicked(picked);
    }

    /// <summary>
    /// Escolhidos no desenho: o que já está na lista (corredor, eixo, superfície, rede) fica com a caixa marcada;
    /// o resto (alinhamento fora de corredor, sólido, bloco...) entra em "Outros objetos escolhidos no desenho".
    /// </summary>
    private void ApplyPicked(List<ObjectId> picked)
    {
        var leaves = All().Where(n => n.Kind is "corredor" or "eixo" or "superficie" or "rede").ToList();
        int listed = 0, added = 0;
        _syncing = true;
        foreach (ObjectId id in picked.Distinct())
        {
            List<Node> hit = leaves.Where(n => n.Ids.Contains(id)).ToList();
            if (hit.Count > 0)
            {
                foreach (Node n in hit)
                {
                    n.Box.IsChecked = true;
                    if (n.Kind == "corredor")
                    {
                        foreach (Node a in Find("EIXOS")?.Children.Where(a => AxisCorridor(a).Equals(KeyName(n), StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<Node>())
                            a.Box.IsChecked = TypeRule(a);
                    }
                }
                listed++;
                continue;
            }
            if (_manual.Add(id))
                added++;
            _c.ManuaisDesmarcados.Remove(id.Handle.ToString());
        }
        _syncing = false;
        SaveMarks();
        Fill();
        _status.Text = (listed + added) switch
        {
            0 => _status.Text,
            1 => "1 objeto marcado.",
            int k => $"{k} objetos marcados."
        };
    }

    private void ClearManual()
    {
        _manual.Clear();
        _c.ManuaisDesmarcados.Clear();
        SaveMarks();
        Fill();
    }

    private void EditMapping()
    {
        SaveMarks();
        var w = new IfcMappingWindow(_doc.Database, _c.Mapeamento) { Owner = this };
        if (w.ShowDialog() != true)
            return;
        _c.Mapeamento = w.Result;
        SaveMarks();
        if (w.SaveAsDefault)
        {
            IfcCriteria def = File.Exists(IfcCriteria.DefaultPath) ? IfcCriteria.Read(IfcCriteria.DefaultPath) : new IfcCriteria { Eixos = "principais" };
            def.Mapeamento = w.Result;
            IfcCriteria.Save(def, IfcCriteria.DefaultPath);
        }
        _status.Text = "Classes IFC atualizadas.";
    }

    /// <summary>Grava no JSON do projeto o que está marcado na janela.</summary>
    private void SaveMarks()
    {
        _c.ProjetoNome = _project.Text.Trim();
        _c.LocalNome = _site.Text.Trim();
        _c.Eixos = _axes.SelectedIndex switch { 1 => "todos", 2 => "nenhum", _ => "principais" };
        _c.CorredoresReferenciados = _refs.SelectedIndex switch { 1 => "Perguntar", 2 => "Sim", _ => "Nao" };
        _c.GrelhaSimplificada = _simple.IsChecked == true;
        _c.LayersExtras = _layers.Text.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (_roots.Count > 0)
        {
            Node corr = Find("CORREDORES")!;
            _c.Corredores = corr.Children.Count == 0 || corr.Children.Any(n => n.Box.IsChecked == true);
            _c.CorredoresDesmarcados = corr.Children.Where(n => !n.IsReference && n.Box.IsChecked != true).Select(KeyName).ToList();
            _c.CorredoresReferenciaMarcados = corr.Children.Where(n => n.IsReference && n.Box.IsChecked == true).Select(KeyName).ToList();
            _c.EixosMarcados = new List<string>();
            _c.EixosDesmarcados = new List<string>();
            foreach (Node a in Find("EIXOS")!.Children)
            {
                bool rule = AxisRule(a);
                bool on = a.Box.IsChecked == true;
                if (on && !rule)
                    _c.EixosMarcados.Add(KeyName(a));
                if (!on && rule)
                    _c.EixosDesmarcados.Add(KeyName(a));
            }
            Node pecas = Find("PECAS")!;
            _c.LombofaixasPecasMarcadas = pecas.Children.Where(n => n.Box.IsChecked == true).Select(KeyName).ToList();
            Node travs = Find("TRAVESSIAS")!;
            _c.Lombofaixas = _c.LombofaixasPecasMarcadas.Count > 0 && (travs.Children.Count == 0 || travs.Children.Any(n => n.Box.IsChecked == true));
            _c.TravessiasDesmarcadas = travs.Children.Where(n => n.Box.IsChecked != true).Select(KeyName).ToList();
            _c.SuperficiesMarcadas = Find("SUPERFICIES")!.Children.Where(n => n.Box.IsChecked == true).Select(KeyName).ToList();
            _c.Superficies = false;
            _c.RedesMarcadas = Find("REDES")?.Children.Where(n => n.Box.IsChecked == true).Select(KeyName).ToList() ?? new List<string>();
            _c.ManuaisDesmarcados = Find("MANUAIS")!.Children.Where(n => n.Box.IsChecked != true).Select(KeyName).ToList();
            _c.ManuaisIncluir = true;
        }
        _c.ManuaisHandles = _manual.Where(x => !x.IsNull && !x.IsErased).Select(x => x.Handle.ToString()).ToList();
        IfcCriteria.Save(_c, _criteriaPath);
    }

    private void Export()
    {
        // Arquivo: um caminho só, o do alto da janela (lembrado para a próxima vez).
        string ifc = _file.Text.Trim().Trim('"');
        if (ifc.Length == 0)
        {
            _status.Text = "Informe o arquivo de saída.";
            return;
        }
        if (!ifc.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
            ifc += ".ifc";
        string? dir;
        try
        {
            ifc = Path.GetFullPath(ifc);
            dir = Path.GetDirectoryName(ifc);
        }
        catch (System.Exception)
        {
            _status.Text = "Caminho de arquivo inválido.";
            return;
        }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _status.Text = $"Pasta não encontrada: {dir}";
            return;
        }
        if (File.Exists(ifc) && SW.MessageBox.Show(this, $"{Path.GetFileName(ifc)} já existe. Deseja substituí-lo?", "Exportar IFC",
                SW.MessageBoxButton.YesNo, SW.MessageBoxImage.Question) != SW.MessageBoxResult.Yes)
            return;
        _file.Text = ifc;

        // Corredor de referência: "perguntar".
        Node corr = Find("CORREDORES")!;
        List<Node> refsOff = corr.Children.Where(n => n.IsReference && n.Box.IsChecked != true).ToList();
        if (_refs.SelectedIndex == 1 && refsOff.Count > 0)
        {
            SW.MessageBoxResult a = SW.MessageBox.Show(this,
                $"Incluir as referências externas na exportação?\n\n{string.Join("\n", refsOff.Select(KeyName))}",
                "Referências externas", SW.MessageBoxButton.YesNoCancel, SW.MessageBoxImage.Question);
            if (a == SW.MessageBoxResult.Cancel)
                return;
            if (a == SW.MessageBoxResult.Yes)
            {
                _syncing = true;
                refsOff.ForEach(n => n.Box.IsChecked = true);
                _syncing = false;
            }
        }
        List<ObjectId> ids = Checked(out List<string> parts);
        if (ids.Count == 0)
        {
            _status.Text = "Nenhum objeto selecionado.";
            return;
        }

        // Desatualizados: só os do próprio desenho podem ter Rebuild; referência se atualiza na origem.
        List<Node> on = corr.Children.Where(n => n.Box.IsChecked == true).ToList();
        List<Node> stale = on.Where(n => n.OutOfDate && !n.IsReference).ToList();
        List<Node> staleRef = on.Where(n => n.OutOfDate && n.IsReference).ToList();
        string refNote = staleRef.Count > 0
            ? $"\n\nReferências externas desatualizadas (atualize no desenho de origem):\n{string.Join("\n", staleRef.Select(KeyName))}"
            : "";
        if (stale.Count > 0)
        {
            SW.MessageBoxResult a = SW.MessageBox.Show(this,
                $"Os corredores abaixo estão desatualizados. Reconstruir antes de exportar?\n\n{string.Join("\n", stale.Select(KeyName))}" + refNote,
                "Corredores desatualizados", SW.MessageBoxButton.YesNoCancel, SW.MessageBoxImage.Warning);
            if (a == SW.MessageBoxResult.Cancel)
                return;
            if (a == SW.MessageBoxResult.Yes)
                Rebuild.AddRange(stale.SelectMany(n => n.Ids));
        }
        else if (staleRef.Count > 0)
        {
            SW.MessageBox.Show(this, refNote.Trim(), "Referências externas", SW.MessageBoxButton.OK, SW.MessageBoxImage.Information);
        }

        _c.UltimoArquivoIfc = ifc;
        IfcCriteria.LastFolder = dir;
        SaveMarks();
        IfcPath = ifc;
        Ids.AddRange(ids);
        List<string> refsLeft = corr.Children.Where(n => n.IsReference && n.Box.IsChecked != true).Select(KeyName).ToList();
        Selection = Join(parts) + (refsLeft.Count > 0 ? Environment.NewLine + $"Referências externas não exportadas: {string.Join(", ", refsLeft)}." : "");
        DialogResult = true;
    }

    // ------------------------------------------------------------------
    // Auxiliares
    // ------------------------------------------------------------------

    private bool TryHandle(string h, out ObjectId id)
    {
        id = ObjectId.Null;
        try
        {
            return _doc.Database.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out id) && !id.IsErased;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>("Alinhamento EIXO 01", "layer") ...</summary>
    private static (string Label, string Layer) Describe(Transaction tr, ObjectId id)
    {
        try
        {
            DBObject o = tr.GetObject(id, OpenMode.ForRead);
            string? name = o.GetType().GetProperty("Name")?.GetValue(o) as string;
            string layer = o is Entity e ? e.Layer : "";
            string kind = o switch
            {
                Autodesk.Civil.DatabaseServices.Alignment => "Alinhamento",
                CivilCorridor => "Corredor",
                Autodesk.Civil.DatabaseServices.Surface => "Superfície",
                Autodesk.Civil.DatabaseServices.FeatureLine => "Feature line",
                Autodesk.Civil.DatabaseServices.Pipe => "Tubo",
                Autodesk.Civil.DatabaseServices.Structure => "Estrutura",
                Solid3d => "Sólido 3D",
                BlockReference br => "Bloco " + br.Name,
                _ => o.GetRXClass().DxfName is { Length: > 0 } dxf ? dxf : o.GetType().Name
            };
            return (string.IsNullOrWhiteSpace(name) ? kind : $"{kind} {name}", layer);
        }
        catch
        {
            return ("Objeto " + id.Handle, "");
        }
    }

    private static bool IsReference(DBObject o)
    {
        try
        {
            foreach (string name in new[] { "IsReferenceObject", "IsReferenceSubObject", "IsDataReference" })
            {
                if (o.GetType().GetProperty(name)?.GetValue(o) is bool b && b)
                    return true;
            }
        }
        catch
        {
            // Sem a propriedade.
        }
        return false;
    }

    private static string TypeName(string type) => type switch
    {
        "Centerline" => "Eixo central",
        "Offset" => "Offset",
        "CurbReturn" => "Retorno de esquina",
        "Rail" => "Ferrovia",
        "Miscellaneous" => "Diversos",
        _ => type
    };
}
