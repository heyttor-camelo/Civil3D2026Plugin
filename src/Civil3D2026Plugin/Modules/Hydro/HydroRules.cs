using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Hydro.Calc;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Modelo de regras de uma familia ("PV DNIT"): alvo = expressao, aplicado a varias pecas.</summary>
internal sealed class HydroRuleTemplate
{
    public string Name { get; init; } = "";
    public string Family { get; set; } = "";
    public Dictionary<string, string> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Regras guardadas numa estrutura: o modelo que ela usa e as regras proprias.</summary>
internal sealed class HydroStructureRules
{
    public string Template { get; set; } = "";

    /// <summary>Regras proprias; expressao vazia = medida fixa (sobrepoe o modelo).</summary>
    public Dictionary<string, string> Own { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Template.Length == 0 && Own.Count == 0;
}

/// <summary>
/// Medidas associativas das estruturas (§9 do plano). Cada medida da peca (PartData), a tampa e o
/// fundo podem ser fixos ou uma regra: expressao sobre as variaveis da peca e dos tubos ligados,
/// guardada na propria estrutura (Extension Dictionary HYDRO_REGRAS). Os modelos por familia ficam
/// no NOD (C3D_HYDRO/MODELOS). O recalculo roda no fim do comando (HydroSync) ou no HYREGRAS.
///
/// Variaveis (todas em metros): as medidas da peca pelo nome do catalogo (sil, siw, ac, srs...),
/// tampa, fundo, prof, sup (superficie no ponto) e, dos tubos ligados, dmax, dmin, dext (maior Ø
/// externo), fundo_min, fundo_max, fundo_saida, d_saida, ntubos.
/// </summary>
internal static class HydroRules
{
    private const string Module = "Hydro";
    private const string ExtKey = "HYDRO_REGRAS";
    private const string DictName = "C3D_HYDRO";
    private const string TemplatesKey = "MODELOS";
    private const string TemplateTag = "@modelo";
    private const string FamilyTag = "@familia";

    public const string Rim = "tampa";
    public const string Sump = "fundo";

    /// <summary>Texto de ajuda das variaveis, para o painel e a linha de comando.</summary>
    public const string Help =
        "Variaveis em m: medidas da peca pelo nome (comprimento, largura, parede, espfundo, laje...; os codigos SIL, SIW tambem valem), tampa, fundo, prof, sup, dmax (maior diametro dos tubos), dmin, dext (maior diametro externo), fundo_min, fundo_max, fundo_saida, d_saida, ntubos. " +
        "Funcoes: max, min, abs, raiz, arred(x;n), teto(x;passo), piso(x;passo), limitar(x;a;b), se(cond;a;b). Decimal com virgula, argumentos com ';'.";

    // ------------------------------------------------------------------
    // Unidades
    // ------------------------------------------------------------------

    /// <summary>Fator das unidades do catalogo para metros (mm na maioria das pecas metricas).</summary>
    public static double Factor(string units) => (units ?? "").Trim().ToLowerInvariant() switch
    {
        "mm" or "millimeter" or "millimeters" => 0.001,
        "cm" or "centimeter" or "centimeters" => 0.01,
        "in" or "inch" or "inches" => 0.0254,
        "ft" or "foot" or "feet" => 0.3048,
        _ => 1.0
    };

    // ------------------------------------------------------------------
    // Leitura e escrita das regras da estrutura
    // ------------------------------------------------------------------

    public static HydroStructureRules Read(AcadDb.Transaction tr, AcadDb.DBObject obj)
    {
        HydroStructureRules r = new();
        if (obj.ExtensionDictionary.IsNull)
            return r;
        if (tr.GetObject(obj.ExtensionDictionary, AcadDb.OpenMode.ForRead, false) is not AcadDb.DBDictionary ext || !ext.Contains(ExtKey))
            return r;
        if (tr.GetObject(ext.GetAt(ExtKey), AcadDb.OpenMode.ForRead, false) is not AcadDb.Xrecord rec)
            return r;
        foreach ((string key, string value) in Lines(rec))
        {
            if (key.Equals(TemplateTag, StringComparison.OrdinalIgnoreCase))
                r.Template = value;
            else
                r.Own[key] = value;
        }

        return r;
    }

    public static void Write(AcadDb.Transaction tr, AcadDb.DBObject obj, HydroStructureRules rules)
    {
        if (!obj.IsWriteEnabled)
            obj.UpgradeOpen();
        if (obj.ExtensionDictionary.IsNull)
        {
            if (rules.IsEmpty)
                return;
            obj.CreateExtensionDictionary();
        }

        AcadDb.DBDictionary ext = (AcadDb.DBDictionary)tr.GetObject(obj.ExtensionDictionary, AcadDb.OpenMode.ForWrite);
        if (rules.IsEmpty)
        {
            if (ext.Contains(ExtKey))
                tr.GetObject(ext.GetAt(ExtKey), AcadDb.OpenMode.ForWrite).Erase();
            return;
        }

        List<string> lines = new();
        if (rules.Template.Length > 0)
            lines.Add(TemplateTag + "=" + rules.Template);
        lines.AddRange(rules.Own.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value));
        SetLines(tr, ext, ExtKey, lines);
    }

    /// <summary>
    /// Muda as regras de varias estruturas: own (alvo = expressao; null = tira a regra propria,
    /// "" = fixa a medida) e o modelo (null = mantem, "" = sem modelo).
    /// </summary>
    public static int Edit(AcadDb.Database db, IEnumerable<string> handles, IReadOnlyDictionary<string, string?> own, string? template)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            if (HydroReader.Id(db, h) is not AcadDb.ObjectId id || tr.GetObject(id, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Structure s)
                continue;
            HydroStructureRules r = Read(tr, s);
            foreach ((string target, string? expr) in own)
            {
                if (expr is null)
                    r.Own.Remove(target);
                else
                    r.Own[target] = expr.Trim();
            }

            if (template is not null)
                r.Template = template.Trim();
            Write(tr, s, r);
            n++;
        }

        tr.Commit();
        return n;
    }

    /// <summary>Regras que valem na peca: as do modelo, sobrepostas pelas proprias (vazias = fixas).</summary>
    public static Dictionary<string, string> Effective(HydroStructureRules rules, IReadOnlyDictionary<string, HydroRuleTemplate> templates)
    {
        Dictionary<string, string> eff = new(StringComparer.OrdinalIgnoreCase);
        if (rules.Template.Length > 0 && templates.TryGetValue(rules.Template, out HydroRuleTemplate? t))
        {
            foreach ((string k, string v) in t.Rules)
                eff[k] = v;
        }

        foreach ((string k, string v) in rules.Own)
            eff[k] = v;
        foreach (string k in eff.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList())
            eff.Remove(k);
        return eff;
    }

    // ------------------------------------------------------------------
    // Modelos por familia (NOD)
    // ------------------------------------------------------------------

    public static Dictionary<string, HydroRuleTemplate> LoadTemplates(AcadDb.Transaction tr, AcadDb.Database db)
    {
        Dictionary<string, HydroRuleTemplate> map = new(StringComparer.OrdinalIgnoreCase);
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        if (!nod.Contains(DictName) || tr.GetObject(nod.GetAt(DictName), AcadDb.OpenMode.ForRead) is not AcadDb.DBDictionary dict || !dict.Contains(TemplatesKey))
            return map;
        if (tr.GetObject(dict.GetAt(TemplatesKey), AcadDb.OpenMode.ForRead) is not AcadDb.DBDictionary models)
            return map;
        foreach (AcadDb.DBDictionaryEntry entry in models)
        {
            if (tr.GetObject(entry.Value, AcadDb.OpenMode.ForRead) is not AcadDb.Xrecord rec)
                continue;
            HydroRuleTemplate t = new() { Name = entry.Key };
            foreach ((string key, string value) in Lines(rec))
            {
                if (key.Equals(FamilyTag, StringComparison.OrdinalIgnoreCase))
                    t.Family = value;
                else
                    t.Rules[key] = value;
            }

            map[t.Name] = t;
        }

        return map;
    }

    public static Dictionary<string, HydroRuleTemplate> LoadTemplates(AcadDb.Database db)
    {
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            return LoadTemplates(tr, db);
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Modelos de regra nao lidos: " + ex.Message);
            return new Dictionary<string, HydroRuleTemplate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Grava (ou troca) o modelo; rules vazio apaga. Nome vira chave do dicionario.</summary>
    public static void SaveTemplate(AcadDb.Database db, string name, string family, IReadOnlyDictionary<string, string> rules)
    {
        name = CleanName(name);
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForWrite);
        AcadDb.DBDictionary dict = Child(tr, nod, DictName);
        AcadDb.DBDictionary models = Child(tr, dict, TemplatesKey);
        List<string> rows = rules.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key + "=" + kv.Value.Trim()).ToList();
        if (rows.Count == 0)
        {
            if (models.Contains(name))
                tr.GetObject(models.GetAt(name), AcadDb.OpenMode.ForWrite).Erase();
        }
        else
        {
            rows.Insert(0, FamilyTag + "=" + family);
            SetLines(tr, models, name, rows);
        }

        tr.Commit();
    }

    /// <summary>Nome valido para chave de dicionario (sem caracteres que o AutoCAD recusa).</summary>
    public static string CleanName(string name)
    {
        string n = new((name ?? "").Trim().Select(c => "<>/\\\":;?*|,=`".Contains(c) ? '-' : c).ToArray());
        return n.Length == 0 ? "Modelo" : n;
    }

    // ------------------------------------------------------------------
    // Variaveis da peca
    // ------------------------------------------------------------------

    /// <summary>Variaveis (minusculas, em m) da estrutura e dos tubos ligados.</summary>
    public static Dictionary<string, double> Variables(AcadDb.Transaction tr, CivilDb.Structure s, out List<PartParam> parameters)
    {
        Dictionary<string, double> v = new(StringComparer.Ordinal);
        parameters = HydroReader.ReadParameters(s);
        foreach (PartParam p in parameters)
            v[HydroExpr.Normalize(p.Name)] = p.Value * p.Factor;
        // Os mesmos valores pelo nome em portugues (comprimento, largura, parede...), sem tirar o codigo.
        foreach (PartParam p in parameters)
        {
            string pt = HydroExpr.Normalize(HydroNames.Short(p.Context, p.Name));
            if (!v.ContainsKey(pt))
                v[pt] = p.Value * p.Factor;
        }

        double rim = s.RimElevation, sump = s.SumpElevation;
        v[Rim] = rim;
        v[Sump] = sump;
        v["prof"] = rim - sump;
        try
        {
            double sup = s.SurfaceElevationAtInsertionPoint;
            if (!double.IsNaN(sup) && Math.Abs(sup) < 1e7)
                v["sup"] = sup;
        }
        catch
        {
            // Sem superficie de referencia.
        }

        List<(double Inner, double Outer, double Invert, bool Out)> pipes = new();
        for (int i = 0; i < s.ConnectedPipesCount; i++)
        {
            if (tr.GetObject(s.get_ConnectedPipe(i), AcadDb.OpenMode.ForRead, false) is not CivilDb.Pipe p)
                continue;
            bool atStart = p.StartStructureId == s.ObjectId;
            double inner = p.InnerDiameterOrWidth;
            double height = p.InnerHeight > 0 ? p.InnerHeight : inner;
            double z = atStart ? p.StartPoint.Z : p.EndPoint.Z;
            string dir = "";
            try
            {
                dir = p.FlowDirection.ToString();
            }
            catch
            {
                // Sem sentido.
            }

            bool outgoing = (atStart && dir == "StartToEnd") || (!atStart && dir == "EndToStart");
            pipes.Add((inner, p.OuterDiameterOrWidth, z - (height / 2.0), outgoing));
        }

        v["ntubos"] = pipes.Count;
        if (pipes.Count > 0)
        {
            v["dmax"] = pipes.Max(p => p.Inner);
            v["dmin"] = pipes.Min(p => p.Inner);
            v["dext"] = pipes.Max(p => p.Outer);
            v["fundo_min"] = pipes.Min(p => p.Invert);
            v["fundo_max"] = pipes.Max(p => p.Invert);
        }

        if (pipes.Any(p => p.Out))
        {
            v["fundo_saida"] = pipes.Where(p => p.Out).Min(p => p.Invert);
            v["d_saida"] = pipes.Where(p => p.Out).Max(p => p.Inner);
        }

        return v;
    }

    // ------------------------------------------------------------------
    // Recalculo
    // ------------------------------------------------------------------

    /// <summary>O que o recalculo fez.</summary>
    internal sealed class Report
    {
        public int Structures;
        public int Changed;
        public List<string> Lines { get; } = new();
        public List<string> Errors { get; } = new();

        public string Summary => Changed == 0 && Errors.Count == 0
            ? $"regras conferidas em {Structures} estrutura(s): nada mudou"
            : $"regras: {Changed} estrutura(s) refeita(s)" + (Errors.Count > 0 ? $", {Errors.Count} aviso(s)" : "");
    }

    /// <summary>
    /// Recalcula as regras das estruturas e refaz as que mudaram. Quando o fundo/tampa de uma muda e
    /// os tubos acompanham, as vizinhas com regra sao recalculadas tambem (ate 4 voltas).
    /// Deve rodar com o documento travado (comando ou CommandEnded com LockDocument).
    /// </summary>
    public static Report Apply(AcadDb.Database db, IEnumerable<AcadDb.ObjectId> ids, HydroStore.HoldMode hold)
    {
        Report report = new();
        HashSet<AcadDb.ObjectId> round = new(ids.Where(id => !id.IsNull));
        HashSet<AcadDb.ObjectId> seen = new();
        Dictionary<string, HydroRuleTemplate> templates = LoadTemplates(db);

        for (int pass = 0; pass < 4 && round.Count > 0; pass++)
        {
            HashSet<AcadDb.ObjectId> next = new();
            foreach (AcadDb.ObjectId id in round)
            {
                if (id.IsErased || !id.IsValid)
                    continue;
                try
                {
                    if (ApplyOne(db, id, hold, templates, report, out List<AcadDb.ObjectId> neighbours))
                    {
                        foreach (AcadDb.ObjectId n in neighbours)
                            next.Add(n);
                    }

                    seen.Add(id);
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"{id.Handle}: {ex.Message}");
                    PluginLog.Erro(Module, "Regras da estrutura " + id.Handle + " falharam", ex);
                }
            }

            next.ExceptWith(round);
            round = next;
        }

        report.Structures = seen.Count;
        return report;
    }

    /// <summary>Uma estrutura: le regras e variaveis, avalia e grava o que mudou.</summary>
    private static bool ApplyOne(AcadDb.Database db, AcadDb.ObjectId id, HydroStore.HoldMode hold, IReadOnlyDictionary<string, HydroRuleTemplate> templates,
        Report report, out List<AcadDb.ObjectId> neighbours)
    {
        neighbours = new List<AcadDb.ObjectId>();
        string name, handle = id.Handle.ToString();
        Dictionary<string, string> rules;
        Dictionary<string, double> vars;
        List<PartParam> parameters;
        double rim, sump;
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not CivilDb.Structure s)
                return false;
            HydroStructureRules own = Read(tr, s);
            if (own.IsEmpty)
                return false;
            rules = Effective(own, templates);
            if (rules.Count == 0)
                return false;
            name = s.Name;
            rim = s.RimElevation;
            sump = s.SumpElevation;
            vars = Variables(tr, s, out parameters);
            if (own.Template.Length > 0 && !templates.ContainsKey(own.Template))
                report.Errors.Add($"{name}: modelo '{own.Template}' nao existe no desenho");

            for (int i = 0; i < s.ConnectedPipesCount; i++)
            {
                if (tr.GetObject(s.get_ConnectedPipe(i), AcadDb.OpenMode.ForRead, false) is CivilDb.Pipe p)
                {
                    AcadDb.ObjectId other = p.StartStructureId == id ? p.EndStructureId : p.StartStructureId;
                    if (!other.IsNull)
                        neighbours.Add(other);
                }
            }
        }

        HydroRuleResult result = HydroRuleEngine.Evaluate(rules, vars);
        foreach ((string target, string error) in result.Errors)
            report.Errors.Add($"{name}: {target}: {error}");

        // Medidas da peca: converte para a unidade do catalogo e ajusta as listas fechadas.
        Dictionary<string, double> partValues = new(StringComparer.OrdinalIgnoreCase);
        List<string> changes = new();
        foreach ((string target, double value) in result.Values)
        {
            if (target == Rim || target == Sump)
                continue;
            PartParam? p = parameters.FirstOrDefault(x => HydroExpr.Normalize(x.Name) == target);
            if (p is null)
            {
                if (rules.Keys.Any(k => HydroExpr.Normalize(k) == target))
                    report.Errors.Add($"{name}: '{target}' nao e medida editavel desta peca");
                continue;
            }

            if (!p.Editable)
            {
                report.Errors.Add($"{name}: {p.Name} e fixa no catalogo");
                continue;
            }

            double catalog = value / p.Factor;
            if (!p.IsRange && p.List.Count > 0)
            {
                double snapped = HydroRuleEngine.SnapUp(catalog, p.List, out bool overflow);
                if (overflow)
                    report.Errors.Add($"{name}: {p.Name} = {Fmt(value)} m passa do maior valor da lista ({Fmt(snapped * p.Factor)} m)");
                catalog = snapped;
            }
            else if (!double.IsNaN(p.Min) && !double.IsNaN(p.Max) && (catalog < p.Min - 1e-9 || catalog > p.Max + 1e-9))
            {
                report.Errors.Add($"{name}: {p.Name} = {Fmt(value)} m fora da faixa {Fmt(p.Min * p.Factor)}..{Fmt(p.Max * p.Factor)} m do catalogo");
                catalog = Math.Min(Math.Max(catalog, p.Min), p.Max);
            }

            if (Math.Abs(catalog - p.Value) > 1e-6 * Math.Max(1, Math.Abs(p.Value)))
            {
                partValues[p.Name] = catalog;
                changes.Add($"{p.Name} {Fmt(p.Value * p.Factor)} > {Fmt(catalog * p.Factor)}");
            }
        }

        double? newRim = result.Values.TryGetValue(Rim, out double r) && Math.Abs(r - rim) > 5e-4 ? r : null;
        double? newSump = result.Values.TryGetValue(Sump, out double f) && Math.Abs(f - sump) > 5e-4 ? f : null;
        if (newRim.HasValue)
            changes.Add($"tampa {Fmt(rim)} > {Fmt(newRim.Value)}");
        if (newSump.HasValue)
            changes.Add($"fundo {Fmt(sump)} > {Fmt(newSump.Value)}");

        if (changes.Count == 0)
            return false;

        if (partValues.Count > 0)
            HydroEditor.SetParameters(db, handle, partValues);
        if (newRim.HasValue || newSump.HasValue)
            HydroEditor.EditStructure(db, handle, null, newRim, newSump, hold);

        report.Changed++;
        report.Lines.Add($"{name}: {string.Join(", ", changes)}");
        PluginLog.Info(Module, $"Regras {name}: {string.Join(", ", changes)}");

        // So o fundo/tampa mexe nos tubos e, portanto, nas vizinhas.
        if (!newSump.HasValue && !newRim.HasValue)
            neighbours.Clear();
        return true;
    }

    // ------------------------------------------------------------------
    // Xrecord de linhas "chave=valor"
    // ------------------------------------------------------------------

    private static IEnumerable<(string Key, string Value)> Lines(AcadDb.Xrecord rec)
    {
        using AcadDb.ResultBuffer? rb = rec.Data;
        if (rb is null)
            yield break;
        foreach (AcadDb.TypedValue tv in rb)
        {
            if (tv.TypeCode != (int)AcadDb.DxfCode.Text || tv.Value is not string line)
                continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            yield return (line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
    }

    private static void SetLines(AcadDb.Transaction tr, AcadDb.DBDictionary dict, string key, List<string> lines)
    {
        AcadDb.Xrecord rec;
        if (dict.Contains(key))
        {
            rec = (AcadDb.Xrecord)tr.GetObject(dict.GetAt(key), AcadDb.OpenMode.ForWrite);
        }
        else
        {
            if (!dict.IsWriteEnabled)
                dict.UpgradeOpen();
            rec = new AcadDb.Xrecord();
            dict.SetAt(key, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        using AcadDb.ResultBuffer rb = new(lines.Select(l => new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, l.Length > 250 ? l[..250] : l)).ToArray());
        rec.Data = rb;
    }

    private static AcadDb.DBDictionary Child(AcadDb.Transaction tr, AcadDb.DBDictionary parent, string key)
    {
        if (parent.Contains(key))
            return (AcadDb.DBDictionary)tr.GetObject(parent.GetAt(key), AcadDb.OpenMode.ForWrite);
        AcadDb.DBDictionary d = new();
        parent.SetAt(key, d);
        tr.AddNewlyCreatedDBObject(d, true);
        return d;
    }

    private static string Fmt(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
