using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Hydro.Calc;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Comandos de calculo (fase H3): calcular, verificar, dimensionar e tabela no desenho.</summary>
public sealed class HydroCalcCommands
{
    private const string Module = "Hydro";

    private static string N(double v, int d = 3) => double.IsNaN(v) ? "-" : v.ToString("F" + d, CultureInfo.InvariantCulture);

    private static void Run(string what, Action<Document, Editor> body)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        try
        {
            using (PluginLog.Medir(Module, what))
                body(doc, ed);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Module, what + " falhou", ex);
            ed.WriteMessage($"\n[HYDRO] {what} falhou: {ex.Message}");
        }

        HydroPanel.NotifyChanged();
    }

    /// <summary>Rede a calcular: a ativa no painel, a unica do desenho, ou a da peca clicada.</summary>
    private static NetworkInfo? ChooseNetwork(Editor ed, HydroSnapshot snap)
    {
        if (snap.Networks.Count == 0)
        {
            ed.WriteMessage("\nNenhuma Pipe Network no desenho.");
            return null;
        }

        NetworkInfo? active = snap.Find(HydroApply.ActiveNetwork ?? HydroPanel.ActiveNetworkHandle);
        if (active is not null)
            return active;
        if (snap.Networks.Count == 1)
            return snap.Networks[0];

        PromptEntityOptions o = new("\nClique um tubo ou estrutura da rede: ");
        o.SetRejectMessage("\nNao e uma peca de Pipe Network.");
        o.AddAllowedClass(typeof(CivilDb.Part), false);
        PromptEntityResult r = ed.GetEntity(o);
        if (r.Status != PromptStatus.OK)
            return null;
        string h = r.ObjectId.Handle.ToString();
        return snap.Networks.FirstOrDefault(n => n.PipeByHandle.ContainsKey(h) || n.StructureByHandle.ContainsKey(h));
    }

    [CommandMethod(C3DCommands.Hydro.Calcular, CommandFlags.Modal)]
    public void Calcular() => Run("HYCALCULAR", (doc, ed) =>
    {
        HydroSnapshot snap = HydroReader.Read(doc.Database);
        NetworkInfo? net = ChooseNetwork(ed, snap);
        if (net is null)
            return;
        HydroSettings s = HydroStore.Load(doc.Database);
        int linked = snap.Catchments.Count(c => net.StructureByHandle.ContainsKey(c.StructureHandle));
        ed.WriteMessage($"\nRede {net.Name}: {net.Pipes.Count} tubo(s), {net.Structures.Count} estrutura(s), {linked} bacia(s) ligada(s). IDF {s.Idf.Name}, Tr {s.ReturnPeriod:0} anos, Tc min {s.MinTc:0.#} min.");
        if (linked == 0)
            ed.WriteMessage("\nAviso: nenhuma bacia ligada a esta rede; as vazoes sairao das vazoes conhecidas (KnownFlow) ou zero.");
        HydroCalc.Outcome o = HydroCalc.Run(doc.Database, net, snap, s);
        ed.WriteMessage("\n" + HydroCalc.Summary(o, net, o.Results));
        List<HydroViolation> v = HydroChecker.Check(net, snap, s, o.Results);
        int errs = v.Count(x => x.Severity == HydroSeverity.Erro);
        ed.WriteMessage($"\nVerificacao: {errs} erro(s), {v.Count - errs} aviso(s). Detalhes em C3DHYDROVERIFICAR ou HYVERIFICAR.");
    });

    [CommandMethod(C3DCommands.Hydro.Verificar, CommandFlags.Modal)]
    public void Verificar() => Run("HYVERIFICAR", (doc, ed) =>
    {
        HydroSnapshot snap = HydroReader.Read(doc.Database);
        NetworkInfo? net = ChooseNetwork(ed, snap);
        if (net is null)
            return;
        HydroSettings s = HydroStore.Load(doc.Database);
        Dictionary<string, HydroResult> results = HydroStore.LoadResults(doc.Database);
        List<HydroViolation> list = HydroChecker.Check(net, snap, s, results);
        if (results.Count == 0)
            ed.WriteMessage("\n(rede ainda nao calculada: so as verificacoes geometricas; rode HYCALCULAR para lamina e velocidade)");
        if (list.Count == 0)
        {
            ed.WriteMessage($"\nRede {net.Name}: sem violacoes.");
        }
        else
        {
            ed.WriteMessage($"\nRede {net.Name}: {list.Count} violacao(oes):");
            foreach (HydroViolation v in list)
                ed.WriteMessage($"\n  [{(v.Severity == HydroSeverity.Erro ? "ERRO" : "aviso")}] {v.Item}: {v.Rule} = {v.Value} (limite {v.Limit}). {v.Text}" + (v.Fix.Length > 0 ? $" -> {v.Fix}" : ""));
        }

        HydroPanel.ShowPanel("VERIFICACAO");
    });

    [CommandMethod(C3DCommands.Hydro.Dimensionar, CommandFlags.Modal)]
    public void Dimensionar() => Run("HYDIMENSIONAR", (doc, ed) =>
    {
        HydroSnapshot snap = HydroReader.Read(doc.Database);
        NetworkInfo? net = ChooseNetwork(ed, snap);
        if (net is null)
            return;
        HydroSettings s = HydroStore.Load(doc.Database);
        Dictionary<string, HydroResult> results = HydroStore.LoadResults(doc.Database);
        if (!net.Pipes.Any(p => results.ContainsKey(p.Handle)))
        {
            ed.WriteMessage("\nCalcule a rede antes (HYCALCULAR).");
            return;
        }

        List<HydroSizingRow> rows = HydroSizing.Rows(net, snap, results, s);
        List<HydroSizingRow> changes = rows.Where(r => !double.IsNaN(r.MinDiameter) && Math.Abs(r.MinDiameter - r.Diameter) > 1e-6).ToList();
        List<HydroSizingRow> none = rows.Where(r => double.IsNaN(r.MinDiameter) && !double.IsNaN(r.Flow) && r.Flow > 0).ToList();
        ed.WriteMessage($"\nDiametros comerciais: {string.Join(", ", s.Diameters.Select(d => (d * 1000).ToString("0", CultureInfo.InvariantCulture)))} mm. Criterios: y/D <= {s.MaxDepthRatio}, V {s.MinVelocity}-{s.MaxVelocity} m/s.");
        foreach (HydroSizingRow r in rows)
        {
            string tag = double.IsNaN(r.MinDiameter) ? (r.Flow > 0 ? "NENHUM atende (aumente a declividade)" : "sem vazao")
                : Math.Abs(r.MinDiameter - r.Diameter) < 1e-6 ? "ok" : r.MinDiameter > r.Diameter ? "AUMENTAR" : "pode reduzir";
            ed.WriteMessage($"\n  {r.Trecho}: Q {N(r.Flow, 4)} m3/s, i {N(r.Slope * 100, 2)} %, D {r.Diameter * 1000:0} mm -> menor que atende {(double.IsNaN(r.MinDiameter) ? "-" : (r.MinDiameter * 1000).ToString("0", CultureInfo.InvariantCulture))} mm  [{tag}]");
        }

        if (none.Count > 0)
            ed.WriteMessage($"\n{none.Count} trecho(s) sem diametro da lista que atenda: aumente a declividade ou os diametros em Calculo.");
        if (changes.Count == 0)
        {
            ed.WriteMessage("\nNada a trocar.");
            return;
        }

        PromptKeywordOptions ko = new($"\nAplicar o menor diametro que atende em {changes.Count} trecho(s)? [Sim/Nao/Aumentar]") { AllowNone = true };
        ko.Keywords.Add("Sim");
        ko.Keywords.Add("Nao");
        ko.Keywords.Add("Aumentar");
        ko.Keywords.Default = "Aumentar";
        PromptResult kr = ed.GetKeywords(ko);
        string answer = kr.Status == PromptStatus.None ? "Aumentar" : kr.Status == PromptStatus.OK ? kr.StringResult : "Nao";
        if (answer == "Nao")
            return;
        int n = 0;
        foreach (HydroSizingRow r in changes.Where(r => answer == "Sim" || r.MinDiameter > r.Diameter))
        {
            HydroEditor.ResizePipe(doc.Database, r.PipeHandle, r.MinDiameter);
            n++;
        }

        ed.WriteMessage($"\n{n} tubo(s) trocado(s) pelo tamanho mais proximo da familia. Recalculando...");
        HydroSnapshot snap2 = HydroReader.Read(doc.Database);
        NetworkInfo? net2 = snap2.Find(net.Handle);
        if (net2 is not null)
        {
            HydroCalc.Outcome o = HydroCalc.Run(doc.Database, net2, snap2, s);
            ed.WriteMessage("\n" + HydroCalc.Summary(o, net2, o.Results));
        }
    });

    [CommandMethod(C3DCommands.Hydro.Tabela, CommandFlags.Modal)]
    public void Tabela() => Run("HYTABELA", (doc, ed) =>
    {
        HydroSnapshot snap = HydroReader.Read(doc.Database);
        NetworkInfo? net = ChooseNetwork(ed, snap);
        if (net is null)
            return;
        HydroSettings s = HydroStore.Load(doc.Database);
        Dictionary<string, HydroResult> results = HydroStore.LoadResults(doc.Database);
        List<HydroSizingRow> rows = HydroSizing.Rows(net, snap, results, s);
        if (rows.Count == 0)
        {
            ed.WriteMessage("\nA rede nao tem tubos.");
            return;
        }

        PromptDoubleOptions ho = new("\nAltura do texto") { DefaultValue = 2.5, UseDefaultValue = true, AllowNegative = false, AllowZero = false };
        PromptDoubleResult hr = ed.GetDouble(ho);
        if (hr.Status != PromptStatus.OK)
            return;
        PromptPointResult pr = ed.GetPoint(new PromptPointOptions("\nPonto de insercao (canto superior esquerdo): "));
        if (pr.Status != PromptStatus.OK)
            return;

        string[] headers = { "Trecho", "A acum. (ha)", "C", "Tc (min)", "i (mm/h)", "Q (m3/s)", "L (m)", "Decl. (%)", "D (mm)", "y/D (%)", "V (m/s)", "Fundo mont.", "Fundo jus." };
        double[] widths = { 16, 7, 4, 6, 7, 7, 6, 6, 6, 6, 6, 8, 8 };
        double h = hr.Value;
        using AcadDb.Transaction tr = doc.Database.TransactionManager.StartTransaction();
        AcadDb.BlockTableRecord space = (AcadDb.BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, AcadDb.OpenMode.ForWrite);
        AcadDb.Table table = new();
        table.TableStyle = doc.Database.Tablestyle;
        table.SetSize(rows.Count + 2, headers.Length);
        table.SetRowHeight(h * 1.8);
        table.Position = pr.Value;
        table.Cells[0, 0].TextString = $"HYDRO - Rede {net.Name} - Tr {s.ReturnPeriod:0} anos - IDF {s.Idf.Name}";
        table.Cells[0, 0].TextHeight = h * 1.2;
        for (int c = 0; c < headers.Length; c++)
        {
            table.Columns[c].Width = widths[c] * h;
            table.Cells[1, c].TextString = headers[c];
            table.Cells[1, c].TextHeight = h;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            HydroSizingRow r = rows[i];
            string[] values =
            {
                r.Trecho, N(r.AreaAcumHa, 3), N(r.C, 2), N(r.Tc, 1), N(r.Intensity, 1), N(r.Flow, 4), N(r.Length, 2), N(r.Slope * 100, 2),
                (r.Diameter * 1000).ToString("0", CultureInfo.InvariantCulture), N(r.DepthRatio * 100, 0), N(r.Velocity, 2), N(r.UpInvert, 3), N(r.DownInvert, 3)
            };
            for (int c = 0; c < values.Length; c++)
            {
                table.Cells[i + 2, c].TextString = values[c];
                table.Cells[i + 2, c].TextHeight = h;
                table.Cells[i + 2, c].Alignment = c == 0 ? AcadDb.CellAlignment.MiddleLeft : AcadDb.CellAlignment.MiddleRight;
            }
        }

        table.GenerateLayout();
        space.AppendEntity(table);
        tr.AddNewlyCreatedDBObject(table, true);
        tr.Commit();
        ed.WriteMessage($"\nTabela com {rows.Count} trecho(s) inserida. Recalcule e insira de novo quando a rede mudar (a tabela nao e associativa).");
    });
}
