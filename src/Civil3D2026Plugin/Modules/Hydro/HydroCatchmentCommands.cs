using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Hydro.Calc;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Comandos das bacias (fase H2): criar por polilinha, ligar a estrutura e calcular o Tc.</summary>
public sealed class HydroCatchmentCommands
{
    private const string Module = "Hydro";
    private const CommandFlags Flags = CommandFlags.Modal | CommandFlags.UsePickSet;

    private static string H(AcadDb.ObjectId id) => id.Handle.ToString();

    private static string N(double v, int d = 2) => v.ToString("F" + d, CultureInfo.InvariantCulture);

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

    private static double? AskDouble(Editor ed, string prompt, double? def, bool allowNegative = false)
    {
        PromptDoubleOptions o = new("\n" + prompt) { AllowNegative = allowNegative, AllowNone = def.HasValue };
        if (def.HasValue)
        {
            o.DefaultValue = def.Value;
            o.UseDefaultValue = true;
        }

        PromptDoubleResult r = ed.GetDouble(o);
        return r.Status == PromptStatus.OK ? r.Value : null;
    }

    private static AcadDb.ObjectId? PickEntity<T>(Editor ed, string prompt, string reject, bool allowNone = false)
    {
        PromptEntityOptions o = new("\n" + prompt) { AllowNone = allowNone };
        o.SetRejectMessage("\n" + reject);
        o.AddAllowedClass(typeof(T), false);
        PromptEntityResult r = ed.GetEntity(o);
        return r.Status == PromptStatus.OK ? r.ObjectId : null;
    }

    [CommandMethod(C3DCommands.Hydro.Bacia, Flags)]
    public void Bacia() => Run("HYBACIA", (doc, ed) =>
    {
        List<AcadDb.ObjectId> curves = new();
        PromptSelectionResult implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK)
        {
            curves.AddRange(implied.Value.GetObjectIds().Where(IsClosedCurve));
            ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());
        }

        if (curves.Count == 0)
        {
            PromptSelectionOptions o = new() { MessageForAdding = "\nSelecione as polilinhas fechadas das bacias: " };
            PromptSelectionResult r = ed.GetSelection(o);
            if (r.Status != PromptStatus.OK)
                return;
            curves.AddRange(r.Value.GetObjectIds().Where(IsClosedCurve));
        }

        if (curves.Count == 0)
        {
            ed.WriteMessage("\nNenhuma polilinha fechada na selecao.");
            return;
        }

        PromptStringOptions so = new("\nGrupo das bacias") { DefaultValue = "HYDRO", UseDefaultValue = true, AllowSpaces = true };
        PromptResult sr = ed.GetString(so);
        if (sr.Status != PromptStatus.OK)
            return;
        string group = sr.StringResult.Trim().Length == 0 ? "HYDRO" : sr.StringResult.Trim();
        HydroSettings st = HydroStore.Load(doc.Database);
        double? c = AskDouble(ed, "Coeficiente de escoamento C", 0.80);
        if (c is null)
            return;
        double? tc = AskDouble(ed, "Tempo de concentracao [min]", st.MinTc);
        if (tc is null)
            return;
        AcadDb.ObjectId? structure = PickEntity<CivilDb.Structure>(ed, "Estrutura de engolimento para todas (Enter = nenhuma, ligue depois com HYLIGARBACIA): ", "Nao e uma estrutura.", true);

        int existing = HydroReader.Read(doc.Database).Catchments.Count;
        int n = 0;
        foreach (AcadDb.ObjectId id in curves)
        {
            string name = $"Bacia-{existing + n + 1:00}";
            string handle = HydroEditor.CreateCatchment(doc.Database, id, group, name, c.Value, tc.Value, structure is null ? null : H(structure.Value));
            ed.WriteMessage($"\n{name} criada (handle {handle}).");
            n++;
        }

        ed.WriteMessage($"\n{n} bacia(s) no grupo {group}, C {N(c.Value)}, Tc {N(tc.Value, 1)} min.");
    });

    private static bool IsClosedCurve(AcadDb.ObjectId id)
    {
        if (!id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(AcadDb.Curve))))
            return false;
        using AcadDb.Transaction tr = id.Database.TransactionManager.StartOpenCloseTransaction();
        return tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Curve c && c.Closed;
    }

    [CommandMethod(C3DCommands.Hydro.LigarBacia, Flags)]
    public void LigarBacia() => Run("HYLIGARBACIA", (doc, ed) =>
    {
        // Pela selecao previa (menu de contexto numa estrutura): a estrutura ja esta escolhida.
        AcadDb.ObjectId? preStructure = null;
        PromptSelectionResult implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value.Count == 1 && implied.Value[0].ObjectId.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Structure))))
            preStructure = implied.Value[0].ObjectId;
        ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());

        int n = 0;
        while (true)
        {
            AcadDb.ObjectId? cat = PickEntity<CivilDb.Catchment>(ed, n == 0 ? "Clique a bacia: " : "Proxima bacia (Enter termina): ", "Nao e uma bacia (catchment).", n > 0);
            if (cat is null)
                break;
            AcadDb.ObjectId? structure = preStructure ?? PickEntity<CivilDb.Structure>(ed, "Clique a estrutura de engolimento (Enter desliga): ", "Nao e uma estrutura.", true);
            HydroEditor.EditCatchment(doc.Database, H(cat.Value), null, null, structure is null ? "" : H(structure.Value));
            string catName, stName = "(nenhuma)";
            using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                catName = ((CivilDb.Catchment)tr.GetObject(cat.Value, AcadDb.OpenMode.ForRead)).Name;
                if (structure is not null)
                    stName = ((CivilDb.Structure)tr.GetObject(structure.Value, AcadDb.OpenMode.ForRead)).Name;
            }

            ed.WriteMessage($"\n{catName} -> {stName}");
            n++;
        }

        ed.WriteMessage($"\n{n} bacia(s) ligada(s).");
    });

    [CommandMethod(C3DCommands.Hydro.Tc, Flags)]
    public void Tc() => Run("HYTC", (doc, ed) =>
    {
        PromptKeywordOptions ko = new("\nFormula do tempo de concentracao [Kirpich/Ventechow/Dooge/Kerby]") { AllowNone = true };
        ko.Keywords.Add("Kirpich");
        ko.Keywords.Add("Ventechow");
        ko.Keywords.Add("Dooge");
        ko.Keywords.Add("Kerby");
        ko.Keywords.Default = "Kirpich";
        PromptResult kr = ed.GetKeywords(ko);
        if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None)
            return;
        TcMethod method = (kr.Status == PromptStatus.None ? "Kirpich" : kr.StringResult) switch
        {
            "Ventechow" => TcMethod.VenTeChow,
            "Dooge" => TcMethod.Dooge,
            "Kerby" => TcMethod.Kerby,
            _ => TcMethod.Kirpich
        };

        List<AcadDb.ObjectId> ids = new();
        List<string>? pre = HydroApply.TakePreselect();
        if (pre is not null)
            ids.AddRange(pre.Select(h => HydroReader.Id(doc.Database, h)).Where(i => i.HasValue).Select(i => i!.Value));
        if (ids.Count == 0)
        {
            PromptSelectionResult implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK)
                ids.AddRange(implied.Value.GetObjectIds());
            ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());
        }

        ids = ids.Where(i => i.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Catchment)))).ToList();
        if (ids.Count == 0)
        {
            PromptSelectionOptions o = new() { MessageForAdding = "\nSelecione as bacias: " };
            PromptSelectionResult r = ed.GetSelection(o);
            if (r.Status != PromptStatus.OK)
                return;
            ids.AddRange(r.Value.GetObjectIds().Where(i => i.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Catchment)))));
        }

        if (ids.Count == 0)
            return;

        HydroSettings st = HydroStore.Load(doc.Database);
        int n = 0;
        foreach (AcadDb.ObjectId id in ids)
        {
            string name;
            double length, area, current;
            using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                CivilDb.Catchment c = (CivilDb.Catchment)tr.GetObject(id, AcadDb.OpenMode.ForRead);
                name = c.Name;
                length = CatchmentApi.GetFlowPathLength(c);
                area = c.Area2d;
                current = c.TimeOfConcentration;
            }

            ed.WriteMessage($"\n{name}: area {N(area / 10000, 4)} ha, Tc atual {N(current, 1)} min, talvegue {(length > 0 ? N(length, 1) + " m" : "nao definido")}.");
            double? len = AskDouble(ed, $"Comprimento do talvegue de {name} [m]", length > 0 ? length : null);
            if (len is null)
                return;
            double? drop = AskDouble(ed, $"Desnivel do talvegue de {name} [m]", null);
            if (drop is null)
                return;
            double tc = TimeOfConcentration.Compute(method, len.Value, drop.Value, area);
            double final = TimeOfConcentration.WithMinimum(tc, st.MinTc);
            HydroEditor.EditCatchment(doc.Database, H(id), null, final, null);
            ed.WriteMessage($"\n{name}: Tc {TimeOfConcentration.Name(method)} = {N(tc, 1)} min" + (final > tc + 1e-9 ? $" -> minimo de projeto {N(final, 1)} min" : "") + ".");
            n++;
        }

        ed.WriteMessage($"\n{n} bacia(s) com Tc calculado.");
    });
}
