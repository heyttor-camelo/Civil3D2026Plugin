using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;
using Civil3D2026Plugin.UI.Common;

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// Janela do C3DIFC (pedido do Heyvid em 17/11/2026): o que exportar, com caixinhas e contagens
/// (corredores, eixos, lombofaixas por peca e por travessia, superficies, redes de tubulacao,
/// selecionados a mao no desenho), nome do projeto/local, corredor de referencia, grelha simplificada,
/// mapeamento e criterios avancados. As marcacoes ficam no JSON do projeto.
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
    private IfcCriteria _c;
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
    }

    private readonly List<Node> _roots = new();
    private readonly WC.TreeView _tree = new();
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
        foreach (ObjectId id in pickfirst)
            _manual.Add(id);
        foreach (string h in _c.ManuaisHandles)
        {
            if (TryHandle(h, out ObjectId id))
                _manual.Add(id);
        }
        Title = "Exportar IFC 4.3 (C3DIFC)";
        Width = 760;
        Height = 820;
        MinWidth = 600;
        MinHeight = 560;
        WindowStartupLocation = SW.WindowStartupLocation.CenterOwner;
        Background = T.Back;
        Foreground = T.Text;
        ToolStyles.Aplicar(Resources, T);
        Content = BuildUi();
        Fill();
    }

    // ------------------------------------------------------------------
    // Interface
    // ------------------------------------------------------------------

    private SW.UIElement BuildUi()
    {
        var root = new WC.DockPanel { Margin = new SW.Thickness(10) };

        var top = new WC.Grid { Margin = new SW.Thickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(150) });
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new SW.GridLength(1, SW.GridUnitType.Star) });
        for (int i = 0; i < 5; i++)
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
        Row(0, "Nome do projeto", _project, "Vai para o IfcProject (a exportacao da Autodesk gravava \"Unknown\").");
        Row(1, "Nome do local", _site, "Vai para o IfcSite.");
        _axes.Items.Add("principais (so eixos centrais)");
        _axes.Items.Add("todos");
        _axes.Items.Add("nenhum");
        _axes.SelectionChanged += (_, _) => { if (!_syncing) ApplyAxisRule(); };
        Row(2, "Eixos dos corredores", _axes, "Principais: so os alinhamentos de baseline do tipo eixo central (fora bordos/offset, retornos de esquina). Pode marcar/desmarcar um a um na lista.");
        _refs.Items.Add("nunca (padrao)");
        _refs.Items.Add("perguntar ao exportar");
        _refs.Items.Add("sempre");
        _refs.SelectionChanged += (_, _) => { if (!_syncing) ApplyReferenceRule(); };
        Row(3, "Corredor de referencia", _refs, "Corredor por data shortcut (de outro desenho): normalmente e exportado no desenho de origem; aqui sairia duplicado.");
        _simple.Content = "Grelha simplificada no IFC (chapa: o arquivo fica bem mais leve; o desenho nao muda)";
        _simple.Foreground = T.Text;
        Row(4, "", _simple, "As grelhas em X tem milhares de vertices; a chapa simplificada tem o mesmo contorno e espessura.");
        WC.DockPanel.SetDock(top, WC.Dock.Top);
        root.Children.Add(top);

        // Rodape: botoes.
        var bottom = new WC.DockPanel { Margin = new SW.Thickness(0, 8, 0, 0) };
        var left = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        left.Children.Add(Button("Selecionar no desenho", PickInDrawing, "Esconde a janela; clique ou faca janela nos objetos (Enter termina). Entram no grupo \"Selecionados a mao\"."));
        left.Children.Add(Button("Limpar selecionados", () => { _manual.Clear(); Fill(); }, "Tira os objetos escolhidos a mao."));
        left.Children.Add(Button("Mapeamento...", EditMapping, "Classe IFC por layer, estilo de shape ou de link, e o que nao sai."));
        left.Children.Add(Button("Criterios avancados...", OpenJson, "Abre o arquivo de criterios do projeto no Bloco de Notas (salve e clique Reler)."));
        left.Children.Add(Button("Reler", () => { _c = IfcCriteria.Read(_criteriaPath); Fill(); }, "Le de novo o arquivo de criterios."));
        WC.DockPanel.SetDock(left, WC.Dock.Left);
        bottom.Children.Add(left);
        var right = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = SW.HorizontalAlignment.Right };
        WC.Button export = Button("Exportar...", Export, "Escolhe o arquivo .ifc e exporta.");
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
        var ll = new WC.TextBlock { Text = "Layers extras (separadas por ;)", Foreground = T.Muted, VerticalAlignment = SW.VerticalAlignment.Center, Margin = new SW.Thickness(0, 0, 8, 0) };
        WC.DockPanel.SetDock(ll, WC.Dock.Left);
        layersRow.Children.Add(ll);
        _layers.ToolTip = "Objetos dessas layers tambem entram (solidos, blocos...).";
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
                _status.Text = "Erro: " + ex.Message;
            }
        };
        return b;
    }

    // ------------------------------------------------------------------
    // Arvore: o que existe no desenho
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
        var axes = new Node { Key = "EIXOS", Label = "Eixos dos corredores", Kind = "grupo" };
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
                    // Sem a informacao.
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
                        // Nome nao lido.
                    }
                    axes.Children.Add(new Node { Key = "A:" + an, Label = $"{an}  ({TypeName(type)}, de {c.Name})", Kind = "eixo", Ids = { aid }, Extra = type + "|" + c.Name });
                }
                n.Label = $"{c.Name}  ({baselines} baseline(s))" + (reference ? "  - referencia por data shortcut" : "") + (stale ? "  - desatualizado" : "");
                corr.Children.Add(n);
            }
            tr.Commit();
        }
        _roots.Add(corr);
        _roots.Add(axes);

        // Lombofaixas: por peca e por travessia.
        var lomb = new Node { Key = "LOMBOFAIXAS", Label = "Lombofaixas (travessias)", Kind = "grupo" };
        var pecas = new Node { Key = "PECAS", Label = "Pecas", Kind = "grupo" };
        foreach ((string key, string label) in new[] { ("Plato", "Plato"), ("Grelhas", "Grelhas / tampas"), ("Vigas", "Vigas baldrame"), ("Sinalizacao", "Sinalizacao 3D (faixa e triangulos)"), ("Planta", "Representacao em planta (hachuras)") })
        {
            List<ObjectId> ids = MFTravessia.Objects(db, null, key);
            pecas.Children.Add(new Node { Key = "P:" + key, Label = label, Kind = "peca", Ids = ids });
        }
        var travs = new Node { Key = "TRAVESSIAS", Label = "Travessias e unioes", Kind = "grupo" };
        List<MFTravessia.Stored> stored = MFTravessia.FindAll(db).Select(x => x.Data).GroupBy(x => x.Id).Select(g => g.First()).ToList();
        foreach (string tid in MFTravessia.AllIds(db))
        {
            MFTravessia.Stored? d = stored.FirstOrDefault(x => x.Id == tid);
            string label = d is null ? $"Uniao de cruzamento {tid}" : $"{MFTravessia.CodeOf(db, d)}  {d.LabelA} - {d.LabelB}";
            travs.Children.Add(new Node { Key = "T:" + tid, Label = label, Kind = "travessia", Ids = MFTravessia.Objects(db, new List<string> { tid }, "Tudo") });
        }
        travs.Children.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        lomb.Children.Add(pecas);
        lomb.Children.Add(travs);
        _roots.Add(lomb);

        // Superficies e redes.
        var surf = new Node { Key = "SUPERFICIES", Label = "Superficies", Kind = "grupo" };
        var nets = new Node { Key = "REDES", Label = "Redes de tubulacao", Kind = "grupo" };
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
            _status.Text = "Superficies/redes nao lidas: " + ex.Message;
        }
        _roots.Add(surf);
        if (nets.Children.Count > 0)
            _roots.Add(nets);

        var manual = new Node { Key = "MANUAIS", Label = "Selecionados a mao no desenho", Kind = "manual", Ids = _manual.Where(x => !x.IsNull && !x.IsErased).ToList() };
        _roots.Add(manual);

        // Marcacoes (criterios gravados ou padrao).
        foreach (Node n in corr.Children)
            n.Extra = n.IsReference ? (_c.CorredoresReferenciaMarcados.Contains(KeyName(n)) || _refs.SelectedIndex == 2 ? "on" : "off") : (_c.CorredoresDesmarcados.Contains(KeyName(n)) ? "off" : "on");
        _tree.Items.Clear();
        foreach (Node r in _roots)
            _tree.Items.Add(Item(r, null));
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
        manual.Box.IsChecked = _c.ManuaisIncluir && manual.Ids.Count > 0;
        foreach (Node r in _roots)
            SyncParent(r);
        _syncing = false;
        UpdateStatus();
    }

    private static string KeyName(Node n) => n.Key.Length > 2 ? n.Key[2..] : n.Key;

    private WC.TreeViewItem Item(Node n, Node? parent)
    {
        int count = Count(n);
        n.Box = new WC.CheckBox
        {
            Content = $"{n.Label}   [{count}]",
            Foreground = n.IsReference ? T.Muted : T.Text,
            IsThreeState = false,
            VerticalAlignment = SW.VerticalAlignment.Center
        };
        n.Box.Click += (_, _) =>
        {
            if (_syncing)
                return;
            _syncing = true;
            bool on = n.Box.IsChecked == true;
            SetAll(n, on);
            foreach (Node r in _roots)
                SyncParent(r);
            _syncing = false;
            UpdateStatus();
        };
        var item = new WC.TreeViewItem { Header = n.Box, IsExpanded = n.Kind == "grupo" && n.Key is not ("EIXOS" or "TRAVESSIAS"), Foreground = T.Text };
        foreach (Node c in n.Children)
            item.Items.Add(Item(c, n));
        return item;
    }

    private static int Count(Node n) => n.Children.Count > 0 ? n.Children.Sum(Count) : n.Ids.Count;

    private static void SetAll(Node n, bool on)
    {
        n.Box.IsChecked = on;
        foreach (Node c in n.Children)
            SetAll(c, on);
    }

    /// <summary>Grupo marcado quando todos os filhos estao; meio-marcado (sem preenchimento) quando parte.</summary>
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

    private void ApplyAxisRule(bool keepManual = false)
    {
        Node? axes = Find("EIXOS");
        if (axes is null)
            return;
        bool wasSyncing = _syncing;
        _syncing = true;
        foreach (Node a in axes.Children)
        {
            string type = a.Extra.Split('|')[0];
            bool on = _axes.SelectedIndex switch { 1 => true, 2 => false, _ => type.Equals("Centerline", StringComparison.OrdinalIgnoreCase) };
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
                parts.Add($"{on.Count} corredor(es)");
            Node? axes = Find("EIXOS");
            // Eixos so dos corredores que entram.
            var names = new HashSet<string>(on.Select(KeyName), StringComparer.OrdinalIgnoreCase);
            var ax = axes?.Children.Where(a => a.Box.IsChecked == true && names.Contains(a.Extra.Split('|')[1])).ToList() ?? new List<Node>();
            ids.AddRange(ax.SelectMany(a => a.Ids));
            if (ax.Count > 0)
                parts.Add($"{ax.Count} eixo(s)");
        }
        Node? pecas = Find("PECAS");
        Node? travs = Find("TRAVESSIAS");
        if (pecas is not null && travs is not null)
        {
            var pieceIds = new HashSet<ObjectId>(pecas.Children.Where(p => p.Box.IsChecked == true).SelectMany(p => p.Ids));
            var travIds = new HashSet<ObjectId>(travs.Children.Where(t => t.Box.IsChecked == true).SelectMany(t => t.Ids));
            var both = pieceIds.Where(travIds.Contains).ToList();
            ids.AddRange(both);
            if (both.Count > 0)
                parts.Add($"{both.Count} peca(s) de lombofaixa");
        }
        foreach (string key in new[] { "SUPERFICIES", "REDES" })
        {
            Node? g = Find(key);
            var on = g?.Children.Where(n => n.Box.IsChecked == true).ToList() ?? new List<Node>();
            ids.AddRange(on.SelectMany(n => n.Ids));
            if (on.Count > 0)
                parts.Add($"{on.Count} {(key == "REDES" ? "rede(s)" : "superficie(s)")}");
        }
        Node? manual = Find("MANUAIS");
        if (manual?.Box.IsChecked == true)
        {
            ids.AddRange(manual.Ids);
            parts.Add($"{manual.Ids.Count} selecionado(s) a mao");
        }
        return ids.Distinct().ToList();
    }

    private void UpdateStatus()
    {
        if (_roots.Count == 0)
            return;
        List<ObjectId> ids = Checked(out List<string> parts);
        _status.Text = ids.Count == 0 ? "Nada marcado." : $"Vai exportar {ids.Count} objeto(s): {string.Join(", ", parts)}.";
    }

    // ------------------------------------------------------------------
    // Botoes
    // ------------------------------------------------------------------

    private void PickInDrawing()
    {
        Editor ed = _doc.Editor;
        using (EditorUserInteraction ui = ed.StartUserInteraction(this))
        {
            var po = new PromptSelectionOptions { MessageForAdding = "\nObjetos para o IFC (Enter termina): " };
            PromptSelectionResult r = ed.GetSelection(po);
            if (r.Status == PromptStatus.OK)
            {
                foreach (ObjectId id in r.Value.GetObjectIds())
                    _manual.Add(id);
            }
            ui.End();
        }
        SaveMarks();
        Fill();
        _status.Text = $"{_manual.Count} objeto(s) escolhido(s) a mao. " + _status.Text;
    }

    private void EditMapping()
    {
        var rows = _c.Mapeamento.Select(r => new IfcMapRow { Mapeamento = r.Mapeamento, Nome = r.Nome, Exportar = r.Exportar, ClasseIfc = r.ClasseIfc, Observacao = r.Observacao }).ToList();
        var grid = new WC.DataGrid
        {
            ItemsSource = rows,
            AutoGenerateColumns = true,
            CanUserAddRows = true,
            CanUserDeleteRows = true,
            Background = T.Pane,
            Foreground = T.Text
        };
        var w = new SW.Window
        {
            Title = "Mapeamento IFC (classe por layer / estilo; Exportar desmarcado = nao sai)",
            Owner = this,
            Width = 900,
            Height = 560,
            Background = T.Back,
            WindowStartupLocation = SW.WindowStartupLocation.CenterOwner
        };
        var panel = new WC.DockPanel { Margin = new SW.Thickness(8) };
        var ok = new WC.Button { Content = "Guardar", Padding = new SW.Thickness(12, 3, 12, 3), Margin = new SW.Thickness(0, 8, 0, 0), HorizontalAlignment = SW.HorizontalAlignment.Right };
        ok.Click += (_, _) => { w.DialogResult = true; };
        var help = new WC.TextBlock
        {
            Text = "Mapeamento: LayerName, ShapeStyleName, LinkStyleName, AlignmentStyleName, SurfaceStyleName.  Nome: nome da layer ou do estilo.  ClasseIfc: ex. IfcKerb, IfcPavement, IfcCourse, IfcPlate.COVER_PLATE.",
            Foreground = T.Muted,
            TextWrapping = SW.TextWrapping.Wrap,
            Margin = new SW.Thickness(0, 0, 0, 6)
        };
        WC.DockPanel.SetDock(help, WC.Dock.Top);
        WC.DockPanel.SetDock(ok, WC.Dock.Bottom);
        panel.Children.Add(help);
        panel.Children.Add(ok);
        panel.Children.Add(grid);
        w.Content = panel;
        if (w.ShowDialog() == true)
        {
            grid.CommitEdit();
            _c.Mapeamento = rows.Where(r => !string.IsNullOrWhiteSpace(r.Nome)).ToList();
            SaveMarks();
            _status.Text = $"Mapeamento guardado ({_c.Mapeamento.Count} linha(s)).";
        }
    }

    private void OpenJson()
    {
        SaveMarks();
        Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _criteriaPath + "\"") { UseShellExecute = false });
        _status.Text = "Aberto no Bloco de Notas. Salve e clique Reler.";
    }

    /// <summary>Grava no JSON do projeto o que esta marcado na janela.</summary>
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
            Node axes = Find("EIXOS")!;
            _c.EixosMarcados = new List<string>();
            _c.EixosDesmarcados = new List<string>();
            foreach (Node a in axes.Children)
            {
                string type = a.Extra.Split('|')[0];
                bool rule = _axes.SelectedIndex switch { 1 => true, 2 => false, _ => type.Equals("Centerline", StringComparison.OrdinalIgnoreCase) };
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
            _c.ManuaisIncluir = Find("MANUAIS")!.Box.IsChecked == true;
        }
        _c.ManuaisHandles = _manual.Where(x => !x.IsNull && !x.IsErased).Select(x => x.Handle.ToString()).ToList();
        IfcCriteria.Save(_c, _criteriaPath);
    }

    private void Export()
    {
        SaveMarks();
        // Corredor de referencia: "perguntar".
        Node corr = Find("CORREDORES")!;
        List<Node> refsOff = corr.Children.Where(n => n.IsReference && n.Box.IsChecked != true).ToList();
        if (_refs.SelectedIndex == 1 && refsOff.Count > 0)
        {
            SW.MessageBoxResult a = SW.MessageBox.Show(this,
                $"Incluir o(s) corredor(es) de referencia (data shortcut) {string.Join(", ", refsOff.Select(KeyName))}?\n\nNormalmente ele e exportado no desenho de origem; aqui sairia duplicado.",
                "Corredor de referencia", SW.MessageBoxButton.YesNoCancel, SW.MessageBoxImage.Question);
            if (a == SW.MessageBoxResult.Cancel)
                return;
            if (a == SW.MessageBoxResult.Yes)
                refsOff.ForEach(n => n.Box.IsChecked = true);
        }
        List<ObjectId> ids = Checked(out List<string> parts);
        if (ids.Count == 0)
        {
            _status.Text = "Nada marcado para exportar.";
            return;
        }

        string dwg = _doc.Database.Filename;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Arquivo IFC 4.3",
            Filter = "IFC (*.ifc)|*.ifc",
            InitialDirectory = Path.GetDirectoryName(dwg),
            FileName = Path.GetFileNameWithoutExtension(dwg) + ".ifc",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != true)
            return;

        // Desatualizados: so os do proprio desenho podem ter Rebuild; referencia se atualiza na origem.
        List<Node> on = corr.Children.Where(n => n.Box.IsChecked == true).ToList();
        List<Node> stale = on.Where(n => n.OutOfDate && !n.IsReference).ToList();
        List<Node> staleRef = on.Where(n => n.OutOfDate && n.IsReference).ToList();
        if (stale.Count > 0)
        {
            SW.MessageBoxResult a = SW.MessageBox.Show(this,
                $"Corredor(es) desatualizado(s): {string.Join(", ", stale.Select(KeyName))}.\n\nFazer Rebuild antes de exportar? (Nao = exporta como esta)" +
                (staleRef.Count > 0 ? $"\n\nReferencia desatualizada ({string.Join(", ", staleRef.Select(KeyName))}): atualize no desenho de origem e sincronize o data shortcut; aqui sai como esta." : ""),
                "Corredor desatualizado", SW.MessageBoxButton.YesNoCancel, SW.MessageBoxImage.Warning);
            if (a == SW.MessageBoxResult.Cancel)
                return;
            if (a == SW.MessageBoxResult.Yes)
                Rebuild.AddRange(stale.SelectMany(n => n.Ids));
        }
        else if (staleRef.Count > 0)
        {
            SW.MessageBox.Show(this, $"Referencia desatualizada ({string.Join(", ", staleRef.Select(KeyName))}): atualize no desenho de origem e sincronize o data shortcut; aqui sai como esta.",
                "Corredor de referencia", SW.MessageBoxButton.OK, SW.MessageBoxImage.Information);
        }

        IfcPath = dlg.FileName;
        Ids.AddRange(ids);
        List<string> refsLeft = corr.Children.Where(n => n.IsReference && n.Box.IsChecked != true).Select(KeyName).ToList();
        Selection = string.Join(", ", parts) + (refsLeft.Count > 0 ? Environment.NewLine + $"Corredor(es) de referencia fora: {string.Join(", ", refsLeft)} (para incluir: Corredor de referencia = sempre, na janela)." : "");
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
        "Centerline" => "eixo central",
        "Offset" => "bordo/offset",
        "CurbReturn" => "retorno de esquina",
        "Rail" => "ferrovia",
        "Miscellaneous" => "diversos",
        _ => type
    };
}
