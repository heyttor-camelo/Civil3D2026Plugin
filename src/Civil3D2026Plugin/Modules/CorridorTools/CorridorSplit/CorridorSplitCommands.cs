using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.CorridorTools.CorridorSplit;

/// <summary>
/// CORRSPLIT v1.0: copia/move regioes entre dois corredores do mesmo DWG.
/// As alteracoes so sao confirmadas apos recriacao, verificacao e rebuild.
/// </summary>
public sealed class CorridorSplitCommands
{
    [CommandMethod(C3DCommands.Corridor.Split, CommandFlags.Modal)]
    public void Split()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var ed = doc.Editor;

        var prompt = new PromptEntityOptions("\nSelecione o Corridor de origem: ");
        prompt.SetRejectMessage("\nSelecione um objeto Corridor do Civil 3D.");
        prompt.AddAllowedClass(typeof(CivilDb.Corridor), true);
        var result = ed.GetEntity(prompt);
        if (result.Status != PromptStatus.OK) return;

        try
        {
            var choices = new List<RegionChoice>();
            string originalName;
            int surfaces;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var source = (CivilDb.Corridor)tr.GetObject(result.ObjectId, AcadDb.OpenMode.ForRead);
                originalName = source.Name;
                surfaces = source.CorridorSurfaces.Count;
                for (int b = 0; b < source.Baselines.Count; b++)
                {
                    var baseline = source.Baselines[b];
                    for (int r = 0; r < baseline.BaselineRegions.Count; r++)
                    {
                        var reg = baseline.BaselineRegions[r];
                        choices.Add(new RegionChoice(b, r,
                            baseline.Name + " [" + (baseline.IsFeatureLineBased() ? "Feature Line" : "Alignment") +
                            "]  /  " + reg.Name + "  (" +
                            reg.StartStation.ToString("F3", CultureInfo.InvariantCulture) + " - " +
                            reg.EndStation.ToString("F3", CultureInfo.InvariantCulture) + ")"));
                    }
                }
                tr.Commit();
            }

            if (choices.Count == 0)
            {
                ed.WriteMessage("\n[CORRSPLIT] O corredor nao possui regioes.");
                return;
            }

            using var form = new CorridorSplitDialog(originalName, choices, surfaces);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK) return;

            int moved;
            int baselineCount;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var source = (CivilDb.Corridor)tr.GetObject(result.ObjectId, AcadDb.OpenMode.ForWrite);
                // Impede executar com indices obsoletos se o corredor tiver mudado.
                foreach (var choice in form.Selected)
                {
                    if (choice.BaselineIndex >= source.Baselines.Count ||
                        choice.RegionIndex >= source.Baselines[choice.BaselineIndex].BaselineRegions.Count ||
                        choice.Label != FormatChoice(source.Baselines[choice.BaselineIndex],
                            source.Baselines[choice.BaselineIndex].BaselineRegions[choice.RegionIndex]))
                        throw new InvalidOperationException("O Corridor foi alterado desde a selecao. Execute o comando novamente.");
                }

                (moved, baselineCount) = CorridorSplitService.Execute(
                    CivilApplication.ActiveDocument, tr, source, form.TargetName,
                    form.Selected, form.RemoveFromOriginal);
                tr.Commit();
            }

            ed.WriteMessage("\n[CORRSPLIT v1.0] " + moved + " regiao(oes) em " +
                baselineCount + " baseline(s) criadas no corredor '" + form.TargetName + "'.");
            ed.WriteMessage(form.RemoveFromOriginal
                ? "\nTransferencia concluida. Regioes removidas da origem."
                : "\nCopia concluida. Corredor original mantido.");
            if (surfaces > 0)
                ed.WriteMessage("\nAVISO: Corridor Surfaces da origem NAO foram copiadas para o destino.");
        }
        catch (Exception ex)
        {
            ed.WriteMessage("\n[CORRSPLIT] Operacao cancelada (rollback): " + ex.Message);
        }
    }

    private static string FormatChoice(CivilDb.Baseline b, CivilDb.BaselineRegion r) =>
        b.Name + " [" + (b.IsFeatureLineBased() ? "Feature Line" : "Alignment") +
        "]  /  " + r.Name + "  (" +
        r.StartStation.ToString("F3", CultureInfo.InvariantCulture) + " - " +
        r.EndStation.ToString("F3", CultureInfo.InvariantCulture) + ")";
}

internal sealed record RegionChoice(int BaselineIndex, int RegionIndex, string Label);

internal sealed class CorridorSplitDialog : Form
{
    private readonly CheckedListBox _items = new();
    private readonly TextBox _name = new();
    private readonly RadioButton _copy = new();
    private readonly RadioButton _move = new();
    private readonly List<RegionChoice> _choices;

    public IReadOnlyList<RegionChoice> Selected { get; private set; } = Array.Empty<RegionChoice>();
    public string TargetName { get; private set; } = "";
    public bool RemoveFromOriginal => _move.Checked;

    public CorridorSplitDialog(string original, List<RegionChoice> choices, int surfaces)
    {
        _choices = choices;
        Text = "CORRSPLIT v1.0 - Separar regioes do Corridor";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new System.Drawing.Size(790, 610);
        MinimumSize = new System.Drawing.Size(660, 530);
        Font = new System.Drawing.Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1,
            RowCount = 9
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, surfaces > 0 ? 62 : 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "Origem: " + original, Dock = DockStyle.Fill,
            Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold),
            AutoEllipsis = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(new Label { Text = "Marque as regioes para o novo Corridor:",
            Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 1);

        _items.Dock = DockStyle.Fill;
        _items.CheckOnClick = true;
        _items.HorizontalScrollbar = true;
        foreach (var item in choices) _items.Items.Add(item.Label, false);
        layout.Controls.Add(_items, 0, 2);

        var bulk = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var all = new Button { Text = "Marcar todas", AutoSize = true };
        var none = new Button { Text = "Limpar selecao", AutoSize = true };
        all.Click += (_, _) => { for (int i = 0; i < _items.Items.Count; i++) _items.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < _items.Items.Count; i++) _items.SetItemChecked(i, false); };
        bulk.Controls.Add(all);
        bulk.Controls.Add(none);
        layout.Controls.Add(bulk, 0, 3);

        layout.Controls.Add(new Label { Text = "Nome do novo Corridor:", Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft }, 0, 4);
        _name.Text = original + "_SPLIT";
        _name.Dock = DockStyle.Fill;
        layout.Controls.Add(_name, 0, 5);

        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill };
        _copy.Text = "Copiar (preservar origem)";
        _copy.Checked = true;
        _copy.AutoSize = true;
        _move.Text = "Transferir (remover da origem)";
        _move.AutoSize = true;
        modes.Controls.Add(_copy);
        modes.Controls.Add(_move);
        layout.Controls.Add(modes, 0, 6);

        layout.Controls.Add(new Label {
            Text = surfaces > 0
                ? "ATENCAO: origem possui " + surfaces + " Corridor Surface(s). As superficies, boundaries, overrides e objetos dependentes nao sao clonados nesta v1.0. Confira o DWG apos a copia."
                : "V1.0: preserva regioes, assemblies, frequencias, estacas extras, targets e transitions compativeis. Overrides e offset baselines nao sao suportados.",
            ForeColor = System.Drawing.Color.DarkGoldenrod,
            Dock = DockStyle.Fill, AutoEllipsis = false
        }, 0, 7);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "Executar", Width = 110, Height = 32 };
        var cancel = new Button { Text = "Cancelar", Width = 100, Height = 32, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
            {
                MessageBox.Show(this, "Informe um nome para o novo corredor.", "CORRSPLIT");
                return;
            }
            if (_name.Text.Trim().Equals(original, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "O corredor novo deve ter nome diferente da origem.", "CORRSPLIT");
                return;
            }
            Selected = _items.CheckedIndices.Cast<int>().Select(i => _choices[i]).ToArray();
            if (Selected.Count == 0)
            {
                MessageBox.Show(this, "Selecione pelo menos uma regiao.", "CORRSPLIT");
                return;
            }
            if (_move.Checked &&
                MessageBox.Show(this,
                    "TRANSFERIR removera as regioes marcadas do corredor original.\n" +
                    (surfaces > 0 ? "Corridor Surfaces NAO serao copiadas para o novo corredor.\n" : "") +
                    "Salve uma copia do DWG antes de executar. Confirma?",
                    "Confirmar transferencia", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            TargetName = _name.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 8);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
