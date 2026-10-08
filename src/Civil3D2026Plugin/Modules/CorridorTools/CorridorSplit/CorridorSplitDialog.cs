using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.CorridorTools.CorridorSplit;

/// <summary>
/// Snapshot exibido na interface. O indice de baseline e regiao e verificado
/// novamente no desenho antes de qualquer alteracao.
/// </summary>
internal sealed record RegionChoice(int BaselineIndex, int RegionIndex, string Label)
{
    public string BaselineName { get; init; } = "";
    public string RegionName { get; init; } = "";
    public string Horizontal { get; init; } = "";
    public string Vertical { get; init; } = "";
    public string Assembly { get; init; } = "";
    public double StartStation { get; init; }
    public double EndStation { get; init; }
    public string TargetSummary { get; init; } = "";
    public bool HasMissingTargets { get; init; }
}

/// <summary>
/// Layout semelhante a guia Parameters das propriedades do Corridor:
/// baselines em nos expansivos; regioes filhas marcaveis individualmente;
/// colunas Name, Horizontal/Vertical Baseline, Assembly, estacas e Targets.
/// </summary>
internal sealed class CorridorSplitDialog : Form
{
    private readonly List<RegionChoice> _choices;
    private readonly HashSet<RegionChoice> _checked = new();
    private readonly HashSet<int> _expanded = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _name = new();
    private readonly RadioButton _copy = new();
    private readonly RadioButton _move = new();
    private readonly Label _count = new();
    private readonly int _surfaces;
    private readonly string _origin;
    private readonly Autodesk.AutoCAD.ApplicationServices.Document _document;
    private readonly AcadDb.ObjectId _sourceId;
    private readonly Color _baselineBackground = Color.FromArgb(224, 233, 241);
    private readonly Color _selectedBackground = Color.FromArgb(220, 238, 250);

    public IReadOnlyList<RegionChoice> Selected { get; private set; } = Array.Empty<RegionChoice>();
    public string TargetName { get; private set; } = "";
    public bool RemoveFromOriginal => _move.Checked;

    public CorridorSplitDialog(string origin, List<RegionChoice> choices, int surfaces,
        Autodesk.AutoCAD.ApplicationServices.Document document, AcadDb.ObjectId sourceId)
    {
        _origin = origin;
        _choices = choices;
        _surfaces = surfaces;
        _document = document;
        _sourceId = sourceId;
        foreach (var x in choices) _expanded.Add(x.BaselineIndex);

        Text = "CORRSPLIT v1.4 - Corridor Properties / Dividir corredor";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1170, 735);
        MinimumSize = new Size(940, 550);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(246, 247, 249);
        AutoScaleMode = AutoScaleMode.Font;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 8
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(layout);

        var title = new Label
        {
            Text = "CORRIDOR: " + origin,
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(35, 57, 75)
        };
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Baselines e regiões existentes — marque apenas as regiões que serão copiadas ou transferidas",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        BuildGrid();
        layout.Controls.Add(_grid, 0, 2);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoScroll = true, Padding = new Padding(0, 5, 0, 0)
        };
        AddButton(toolbar, "Selecionar região no desenho", 217, (_, _) => SelectFromDrawing());
        AddButton(toolbar, "Marcar todas", 110, (_, _) => SetAll(true));
        AddButton(toolbar, "Limpar", 78, (_, _) => SetAll(false));
        AddButton(toolbar, "Inverter", 82, (_, _) => Invert());
        AddButton(toolbar, "Expandir/Recolher", 146, (_, _) => ToggleExpand());
        layout.Controls.Add(toolbar, 0, 3);

        var naming = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(0, 5, 0, 0)
        };
        naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        naming.Controls.Add(new Label
        {
            Text = "Nome do novo corredor:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _name.Text = origin + "_SPLIT";
        _name.Dock = DockStyle.Fill;
        naming.Controls.Add(_name, 1, 0);
        layout.Controls.Add(naming, 0, 4);

        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        _copy.Text = "Copiar regiões (manter corredor original)";
        _copy.AutoSize = true;
        _copy.Checked = true;
        _move.Text = "Transferir regiões (remover do original)";
        _move.AutoSize = true;
        modes.Controls.Add(_copy);
        modes.Controls.Add(_move);
        layout.Controls.Add(modes, 0, 5);

        var warnings = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(255, 246, 224),
            ForeColor = Color.FromArgb(122, 78, 5),
            Padding = new Padding(10, 7, 10, 7),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = _surfaces > 0
                ? "ATENÇÃO: a origem possui " + _surfaces +
                  " Corridor Surface(s). Estas superfícies, boundaries e overrides NÃO são clonados nesta versão. " +
                  "Antes de Transferir, salve uma cópia do DWG."
                : "Cópia transacional de baselines, regiões, assemblies, targets e transitions compatíveis. " +
                  "Overrides e Offset Baselines ainda não são suportados."
        };
        layout.Controls.Add(warnings, 0, 6);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        _count.Dock = DockStyle.Fill;
        _count.TextAlign = ContentAlignment.MiddleLeft;
        _count.ForeColor = Color.FromArgb(43, 74, 100);
        footer.Controls.Add(_count, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var execute = new Button
        {
            Text = "Executar", Width = 112, Height = 32,
            BackColor = Color.FromArgb(42, 114, 167), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
        };
        execute.FlatAppearance.BorderSize = 0;
        execute.Click += (_, _) => Confirm();
        var cancel = new Button { Text = "Cancelar", Width = 112, Height = 32,
            DialogResult = DialogResult.Cancel };
        actions.Controls.Add(execute);
        actions.Controls.Add(cancel);
        footer.Controls.Add(actions, 1, 0);
        layout.Controls.Add(footer, 0, 7);
        AcceptButton = execute;
        CancelButton = cancel;

        DrawRows();
    }

    private static void AddButton(Control parent, string text, int width, EventHandler click)
    {
        var button = new Button
        {
            Text = text, Width = width, Height = 28,
            Margin = new Padding(0, 0, 8, 0), FlatStyle = FlatStyle.System
        };
        button.Click += click;
        parent.Controls.Add(button);
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersHeight = 34;
        _grid.RowTemplate.Height = 27;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 236, 240);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(30, 47, 65);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(190, 221, 245);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Marcada", HeaderText = "", Width = 38,
            ThreeState = true, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        AddTextColumn("Name", "Nome / região", 325);
        AddTextColumn("Horizontal", "Horizontal Baseline", 150);
        AddTextColumn("Vertical", "Vertical Baseline", 150);
        AddTextColumn("Assembly", "Assembly", 137);
        AddTextColumn("Inicio", "Estaca início", 100);
        AddTextColumn("Fim", "Estaca fim", 100);
        AddTextColumn("Targets", "Targets", 90);

        _grid.CellClick += (_, args) =>
        {
            if (args.RowIndex < 0) return;
            var row = _grid.Rows[args.RowIndex];
            if (row.Tag is int baseline)
            {
                if (args.ColumnIndex == 0) ToggleBaseline(baseline);
                else if (args.ColumnIndex == 1)
                {
                    if (!_expanded.Add(baseline)) _expanded.Remove(baseline);
                    DrawRows();
                }
            }
            else if (row.Tag is RegionChoice region && args.ColumnIndex == 0)
                ToggleRegion(region);
        };
        _grid.CellDoubleClick += (_, args) =>
        {
            if (args.RowIndex < 0) return;
            if (_grid.Rows[args.RowIndex].Tag is RegionChoice region && args.ColumnIndex != 0)
                ToggleRegion(region);
        };
    }

    private void AddTextColumn(string name, string header, int width)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private static string Station(double value) =>
        value.ToString("F3", CultureInfo.InvariantCulture);

    private void DrawRows()
    {
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            foreach (var group in _choices.GroupBy(x => x.BaselineIndex).OrderBy(x => x.Key))
            {
                var regions = group.ToList();
                var first = regions[0];
                int count = regions.Count(x => _checked.Contains(x));
                var state = count == 0 ? CheckState.Unchecked :
                    count == regions.Count ? CheckState.Checked : CheckState.Indeterminate;
                bool expanded = _expanded.Contains(group.Key);

                int index = _grid.Rows.Add(state,
                    (expanded ? "▾  " : "▸  ") + first.BaselineName,
                    first.Horizontal, first.Vertical, "", "", "",
                    count + "/" + regions.Count);
                var baseRow = _grid.Rows[index];
                baseRow.Tag = group.Key;
                baseRow.DefaultCellStyle.BackColor = _baselineBackground;
                baseRow.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
                baseRow.DefaultCellStyle.ForeColor = Color.FromArgb(38, 53, 68);

                if (!expanded) continue;
                foreach (var region in regions)
                {
                    bool selected = _checked.Contains(region);
                    int rowIndex = _grid.Rows.Add(
                        selected ? CheckState.Checked : CheckState.Unchecked,
                        "     └  " + region.RegionName, "", "",
                        region.Assembly,
                        Station(region.StartStation), Station(region.EndStation),
                        (region.HasMissingTargets ? "⚠ " : "") + region.TargetSummary);
                    var row = _grid.Rows[rowIndex];
                    row.Tag = region;
                    if (selected) row.DefaultCellStyle.BackColor = _selectedBackground;
                    if (region.HasMissingTargets)
                        row.Cells["Targets"].Style.ForeColor = Color.DarkGoldenrod;
                }
            }
            _grid.ClearSelection();
        }
        finally { _grid.ResumeLayout(); }
        int total = _checked.Count;
        int bases = _checked.Select(x => x.BaselineIndex).Distinct().Count();
        _count.Text = total + " de " + _choices.Count + " regiões selecionadas  |  " +
            bases + " baseline(s) necessárias";
    }

    private void ToggleRegion(RegionChoice choice)
    {
        if (!_checked.Add(choice)) _checked.Remove(choice);
        DrawRows();
    }

    private void ToggleBaseline(int baseline)
    {
        var regions = _choices.Where(x => x.BaselineIndex == baseline).ToList();
        bool mark = regions.Any(x => !_checked.Contains(x));
        foreach (var region in regions)
        {
            if (mark) _checked.Add(region);
            else _checked.Remove(region);
        }
        DrawRows();
    }

    private void SetAll(bool value)
    {
        _checked.Clear();
        if (value) foreach (var region in _choices) _checked.Add(region);
        DrawRows();
    }

    private void Invert()
    {
        foreach (var region in _choices)
            if (!_checked.Add(region)) _checked.Remove(region);
        DrawRows();
    }

    private void ToggleExpand()
    {
        var groups = _choices.Select(x => x.BaselineIndex).Distinct().ToList();
        bool expand = groups.Any(x => !_expanded.Contains(x));
        _expanded.Clear();
        if (expand) foreach (int group in groups) _expanded.Add(group);
        DrawRows();
    }

    /// <summary>
    /// Retorna ao editor durante a janela modal (StartUserInteraction) e
    /// encontra a regiao cuja baseline esta mais perto do ponto escolhido.
    /// O criterio aproxima o eixo por segmentos discretizados em planta.
    /// </summary>
    private void SelectFromDrawing()
    {
        var editor = _document.Editor;
        PromptPointResult picked;
        using (editor.StartUserInteraction(Handle))
            picked = editor.GetPoint(new PromptPointOptions(
                "\nCORRSPLIT - clique proximo a baseline da regiao desejada: "));
        if (picked.Status != PromptStatus.OK) return;

        try
        {
            var candidates = new List<(RegionChoice Region, double Distance)>();
            using (var tr = _document.Database.TransactionManager.StartTransaction())
            {
                var corridor = (CivilDb.Corridor)tr.GetObject(_sourceId, AcadDb.OpenMode.ForRead);
                foreach (var region in _choices)
                {
                    if (region.BaselineIndex >= corridor.Baselines.Count) continue;
                    var baseline = corridor.Baselines[region.BaselineIndex];
                    double distance = DistanceToBaselineRegion(baseline, region, picked.Value);
                    if (!double.IsInfinity(distance))
                        candidates.Add((region, distance));
                }
            }

            var nearest = candidates.OrderBy(x => x.Distance).Take(2).ToList();
            if (nearest.Count == 0 || nearest[0].Distance > 35.0)
            {
                MessageBox.Show(this,
                    "Não foi encontrada uma região a até 35 unidades do ponto. " +
                    "Clique mais próximo ao eixo ou selecione pela tabela.",
                    "CORRSPLIT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (nearest.Count > 1 && nearest[1].Distance - nearest[0].Distance < 0.50)
            {
                MessageBox.Show(this,
                    "Há duas regiões muito próximas do ponto clicado. " +
                    "Para evitar selecionar a região errada, escolha-a diretamente na tabela.",
                    "CORRSPLIT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var found = nearest[0].Region;
            _checked.Add(found);
            _expanded.Add(found.BaselineIndex);
            DrawRows();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is RegionChoice item && item.BaselineIndex == found.BaselineIndex &&
                    item.RegionIndex == found.RegionIndex)
                {
                    row.Selected = true;
                    _grid.FirstDisplayedScrollingRowIndex = row.Index;
                    break;
                }
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, "Falha ao localizar a região: " + ex.Message,
                "CORRSPLIT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static double DistanceToBaselineRegion(CivilDb.Baseline baseline,
        RegionChoice region, Point3d point)
    {
        double length = Math.Abs(region.EndStation - region.StartStation);
        int segments = Math.Clamp((int)Math.Ceiling(length / 5.0), 8, 140);
        double best = double.PositiveInfinity;
        Point3d? last = null;
        for (int i = 0; i <= segments; i++)
        {
            double st = region.StartStation + (region.EndStation - region.StartStation) *
                (i / (double)segments);
            Point3d sample;
            try
            {
                sample = baseline.StationOffsetElevationToXYZ(new Point3d(st, 0, 0));
            }
            catch (System.Exception)
            {
                last = null;
                continue;
            }
            if (sample.DistanceTo(Point3d.Origin) < 1e-7)
            {
                last = null; // retorno da API para coordenada SOE invalida
                continue;
            }
            if (last.HasValue)
                best = Math.Min(best, PointToSegmentDistanceXY(point, last.Value, sample));
            else
                best = Math.Min(best, Math.Sqrt((point.X - sample.X) * (point.X - sample.X) +
                    (point.Y - sample.Y) * (point.Y - sample.Y)));
            last = sample;
        }
        return best;
    }

    private static double PointToSegmentDistanceXY(Point3d p, Point3d a, Point3d b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 1e-12)
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0.0, 1.0);
        double x = p.X - (a.X + t * dx), y = p.Y - (a.Y + t * dy);
        return Math.Sqrt(x * x + y * y);
    }

    private void Confirm()
    {
        string name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals(_origin, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Informe um nome diferente do corredor de origem.",
                "CORRSPLIT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Selected = _choices.Where(x => _checked.Contains(x)).ToArray();
        if (Selected.Count == 0)
        {
            MessageBox.Show(this, "Marque pelo menos uma região na árvore.",
                "CORRSPLIT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_move.Checked &&
            MessageBox.Show(this,
                "As " + Selected.Count + " regiões selecionadas serão removidas do corredor " +
                "original APÓS a criação e validação do destino.\n" +
                (_surfaces > 0
                    ? "ATENÇÃO: as " + _surfaces + " Corridor Surfaces não serão copiadas.\n"
                    : "") +
                "Salve uma cópia do DWG antes do teste. Confirma?",
                "CORRSPLIT - Confirmar transferência",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        TargetName = name;
        DialogResult = DialogResult.OK;
        Close();
    }
}
