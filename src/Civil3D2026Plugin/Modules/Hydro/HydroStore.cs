using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Civil3D2026Plugin.Modules.Hydro.Calc;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Parametros de calculo e criterios do HYDRO, gravados no desenho (NOD).</summary>
internal sealed class HydroSettings
{
    public string IdfName { get; set; } = HydroIdf.Library[0].Name;
    public double IdfK { get; set; } = HydroIdf.Library[0].K;
    public double IdfA { get; set; } = HydroIdf.Library[0].A;
    public double IdfB { get; set; } = HydroIdf.Library[0].B;
    public double IdfC { get; set; } = HydroIdf.Library[0].C;
    public double ReturnPeriod { get; set; } = 10;
    public double MinTc { get; set; } = 10;
    public double MaxDepthRatio { get; set; } = 0.85;
    public double MinVelocity { get; set; } = 0.75;
    public double MaxVelocity { get; set; } = 5.0;
    public double MinCover { get; set; } = 1.0;
    public double MinSlope { get; set; } = 0.003;
    public double MaxSlope { get; set; } = 0.15;
    public double MaxDrop { get; set; } = 1.5;
    public double Manning { get; set; } = 0.013;

    /// <summary>Diametros comerciais (m) para dimensionar e para a matriz.</summary>
    public List<double> Diameters { get; set; } = new() { 0.3, 0.4, 0.5, 0.6, 0.8, 1.0, 1.2, 1.5 };

    /// <summary>Ao mudar o fundo de uma estrutura, o que os tubos ligados fazem.</summary>
    public HydroStore.HoldMode HoldMode { get; set; } = HydroStore.HoldMode.OutraPonta;

    /// <summary>Familia e tamanho padrao do lancamento (HYLANCAR), "Familia | Tamanho"; vazio = ultima usada na rede.</summary>
    public string LaunchStructure { get; set; } = "";
    public string LaunchPipe { get; set; } = "";

    /// <summary>Regras das estruturas recalculadas no fim de cada comando (como o "Auto" do Viario).</summary>
    public bool RulesAuto { get; set; } = true;

    public IdfEquation Idf => new(IdfName == "custom" ? "personalizada" : IdfName, IdfK, IdfA, IdfB, IdfC);

    public Criteria Criteria => new(MaxDepthRatio, MinVelocity, MaxVelocity, MinCover, MinSlope, MaxSlope, 0, MaxDrop, MinTc);

    public HydroSettings Clone() => (HydroSettings)MemberwiseClone();
}

/// <summary>Resultado hidraulico gravado por item (handle). Vazao em m3/s.</summary>
internal sealed class HydroResult
{
    public double Flow { get; set; }
    public double AreaHa { get; set; }
    public double C { get; set; }
    public double Tc { get; set; }
    public double Intensity { get; set; }
    public double DepthRatio { get; set; }
    public double Velocity { get; set; }
    public double TravelTime { get; set; }
    public double FullFlow { get; set; }
    public bool Flows { get; set; } = true;
}

/// <summary>
/// Persistencia no desenho: dicionario C3D_HYDRO no NOD, com um Xrecord de parametros (linhas
/// chave=valor) e um de resultados (uma linha por item). Nada depende de arquivo externo.
/// </summary>
internal static class HydroStore
{
    public enum HoldMode
    {
        /// <summary>Segura a outra ponta do tubo: muda a declividade.</summary>
        OutraPonta,

        /// <summary>Segura a declividade: o tubo inteiro sobe/desce.</summary>
        Declividade
    }

    private const string DictName = "C3D_HYDRO";
    private const string SettingsKey = "PARAMETROS";
    private const string ResultsKey = "RESULTADOS";

    public static HydroSettings Load(AcadDb.Database db)
    {
        HydroSettings s = new();
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            foreach (string line in ReadLines(tr, db, SettingsKey))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                Apply(s, line[..eq].Trim(), line[(eq + 1)..].Trim());
            }
        }
        catch (Exception ex)
        {
            Infrastructure.PluginLog.Diag("Hydro", "Parametros nao lidos: " + ex.Message);
        }

        return s;
    }

    public static void Save(AcadDb.Database db, HydroSettings s)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        WriteLines(tr, db, SettingsKey, ToLines(s));
        tr.Commit();
    }

    public static Dictionary<string, HydroResult> LoadResults(AcadDb.Database db)
    {
        Dictionary<string, HydroResult> map = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            foreach (string line in ReadLines(tr, db, ResultsKey))
            {
                string[] f = line.Split('|');
                if (f.Length < 10)
                    continue;
                map[f[0]] = new HydroResult
                {
                    Flow = D(f[1]), AreaHa = D(f[2]), C = D(f[3]), Tc = D(f[4]), Intensity = D(f[5]),
                    DepthRatio = D(f[6]), Velocity = D(f[7]), TravelTime = D(f[8]), Flows = f[9] == "1", FullFlow = f.Length > 10 ? D(f[10]) : 0
                };
            }
        }
        catch (Exception ex)
        {
            Infrastructure.PluginLog.Diag("Hydro", "Resultados nao lidos: " + ex.Message);
        }

        return map;
    }

    public static void SaveResults(AcadDb.Transaction tr, AcadDb.Database db, Dictionary<string, HydroResult> results)
    {
        List<string> lines = results.Select(kv => string.Join("|", kv.Key, S(kv.Value.Flow, 6), S(kv.Value.AreaHa, 5), S(kv.Value.C, 4), S(kv.Value.Tc, 2),
            S(kv.Value.Intensity, 2), S(kv.Value.DepthRatio, 4), S(kv.Value.Velocity, 3), S(kv.Value.TravelTime, 3), kv.Value.Flows ? "1" : "0", S(kv.Value.FullFlow, 6))).ToList();
        WriteLines(tr, db, ResultsKey, lines);
    }

    // ---------------- campos do painel ----------------

    /// <summary>Linhas (chave, rotulo, valor) para a categoria Calculo.</summary>
    public static IEnumerable<(string Key, string Label, string Value)> Describe(HydroSettings s)
    {
        yield return ("idf.nome", "Equacao IDF (biblioteca)", s.IdfName);
        yield return ("idf.k", "IDF: K", S(s.IdfK, 4));
        yield return ("idf.a", "IDF: expoente a de Tr", S(s.IdfA, 4));
        yield return ("idf.b", "IDF: b (min)", S(s.IdfB, 3));
        yield return ("idf.c", "IDF: expoente c de (t + b)", S(s.IdfC, 4));
        yield return ("tr", "Periodo de retorno Tr [anos]", S(s.ReturnPeriod, 0));
        yield return ("tcmin", "Tempo de concentracao minimo [min]", S(s.MinTc, 1));
        yield return ("manning", "Manning n padrao (quando a peca nao tem)", S(s.Manning, 3));
        yield return ("yd", "Lamina maxima y/D", S(s.MaxDepthRatio, 2));
        yield return ("vmin", "Velocidade minima [m/s]", S(s.MinVelocity, 2));
        yield return ("vmax", "Velocidade maxima [m/s]", S(s.MaxVelocity, 2));
        yield return ("recob", "Recobrimento minimo [m]", S(s.MinCover, 2));
        yield return ("imin", "Declividade minima [%]", S(s.MinSlope * 100, 2));
        yield return ("imax", "Declividade maxima [%]", S(s.MaxSlope * 100, 2));
        yield return ("degrau", "Degrau maximo nas estruturas [m]", S(s.MaxDrop, 2));
        yield return ("diametros", "Diametros comerciais [mm]", string.Join(", ", s.Diameters.Select(d => Math.Round(d * 1000).ToString(CultureInfo.InvariantCulture))));
        yield return ("segurar", "Ao mudar o fundo da estrutura, os tubos seguram", s.HoldMode == HoldMode.OutraPonta ? "a outra ponta (muda declividade)" : "a declividade (tubo inteiro sobe/desce)");
        yield return ("lancar.estrutura", "Lancamento: estrutura padrao (Familia | Tamanho)", s.LaunchStructure);
        yield return ("lancar.tubo", "Lancamento: tubo padrao (Familia | Tamanho)", s.LaunchPipe);
        yield return ("regras.auto", "Regras das estruturas: atualizar no fim de cada comando", s.RulesAuto ? "sim" : "nao");
    }

    /// <summary>Le os campos editados no painel (vazio = mantem).</summary>
    public static HydroSettings FromFields(HydroSettings basis, Func<string, string> field)
    {
        HydroSettings s = basis.Clone();
        s.Diameters = new List<double>(basis.Diameters);
        foreach ((string key, _, _) in Describe(basis))
        {
            string v = field(key);
            if (v.Length > 0)
                Apply(s, key, v);
        }

        // Escolher uma equacao da biblioteca copia os coeficientes dela.
        if (s.IdfName != basis.IdfName && s.IdfName != "custom")
        {
            IdfEquation e = HydroIdf.Find(s.IdfName);
            s.IdfK = e.K;
            s.IdfA = e.A;
            s.IdfB = e.B;
            s.IdfC = e.C;
        }
        else if (s.IdfK != basis.IdfK || s.IdfA != basis.IdfA || s.IdfB != basis.IdfB || s.IdfC != basis.IdfC)
        {
            s.IdfName = "custom";
        }

        return s;
    }

    private static void Apply(HydroSettings s, string key, string value)
    {
        string v = value.Replace(',', '.').Replace("%", "").Trim();
        double num = D(v);
        switch (key)
        {
            case "idf.nome": s.IdfName = value; break;
            case "idf.k": s.IdfK = num; break;
            case "idf.a": s.IdfA = num; break;
            case "idf.b": s.IdfB = num; break;
            case "idf.c": s.IdfC = num; break;
            case "tr": s.ReturnPeriod = Math.Max(1, num); break;
            case "tcmin": s.MinTc = Math.Max(0, num); break;
            case "manning": s.Manning = num > 0 ? num : s.Manning; break;
            case "yd": s.MaxDepthRatio = num > 0 ? Math.Min(num, 1) : s.MaxDepthRatio; break;
            case "vmin": s.MinVelocity = num; break;
            case "vmax": s.MaxVelocity = num; break;
            case "recob": s.MinCover = num; break;
            case "imin": s.MinSlope = num / 100; break;
            case "imax": s.MaxSlope = num / 100; break;
            case "degrau": s.MaxDrop = num; break;
            case "diametros":
                List<double> list = v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(D).Where(d => d > 0).Select(d => d / 1000).OrderBy(d => d).ToList();
                if (list.Count > 0)
                    s.Diameters = list;
                break;
            case "segurar":
                s.HoldMode = value.StartsWith("a decl", StringComparison.OrdinalIgnoreCase) || value.StartsWith("decl", StringComparison.OrdinalIgnoreCase) || value == "1"
                    ? HoldMode.Declividade : HoldMode.OutraPonta;
                break;
            case "lancar.estrutura": s.LaunchStructure = value.Trim(); break;
            case "lancar.tubo": s.LaunchPipe = value.Trim(); break;
            case "regras.auto": s.RulesAuto = !(v.StartsWith("n", StringComparison.OrdinalIgnoreCase) || v == "0" || v.StartsWith("desl", StringComparison.OrdinalIgnoreCase)); break;
        }
    }

    private static List<string> ToLines(HydroSettings s) => new()
    {
        "idf.nome=" + s.IdfName, "idf.k=" + S(s.IdfK, 6), "idf.a=" + S(s.IdfA, 6), "idf.b=" + S(s.IdfB, 4), "idf.c=" + S(s.IdfC, 6),
        "tr=" + S(s.ReturnPeriod, 1), "tcmin=" + S(s.MinTc, 2), "manning=" + S(s.Manning, 4), "yd=" + S(s.MaxDepthRatio, 3),
        "vmin=" + S(s.MinVelocity, 3), "vmax=" + S(s.MaxVelocity, 3), "recob=" + S(s.MinCover, 3), "imin=" + S(s.MinSlope * 100, 4),
        "imax=" + S(s.MaxSlope * 100, 4), "degrau=" + S(s.MaxDrop, 3),
        "diametros=" + string.Join(",", s.Diameters.Select(d => S(d * 1000, 0))),
        "segurar=" + (s.HoldMode == HoldMode.Declividade ? "1" : "0"),
        "lancar.estrutura=" + s.LaunchStructure,
        "lancar.tubo=" + s.LaunchPipe,
        "regras.auto=" + (s.RulesAuto ? "1" : "0")
    };

    // ---------------- NOD ----------------

    private static IEnumerable<string> ReadLines(AcadDb.Transaction tr, AcadDb.Database db, string key)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        if (!nod.Contains(DictName))
            yield break;
        AcadDb.DBDictionary dict = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(DictName), AcadDb.OpenMode.ForRead);
        if (!dict.Contains(key))
            yield break;
        AcadDb.Xrecord rec = (AcadDb.Xrecord)tr.GetObject(dict.GetAt(key), AcadDb.OpenMode.ForRead);
        using AcadDb.ResultBuffer? rb = rec.Data;
        if (rb is null)
            yield break;
        foreach (AcadDb.TypedValue tv in rb)
        {
            if (tv.TypeCode == (int)AcadDb.DxfCode.Text && tv.Value is string s)
                yield return s;
        }
    }

    private static void WriteLines(AcadDb.Transaction tr, AcadDb.Database db, string key, List<string> lines)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForWrite);
        AcadDb.DBDictionary dict;
        if (nod.Contains(DictName))
        {
            dict = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(DictName), AcadDb.OpenMode.ForWrite);
        }
        else
        {
            dict = new AcadDb.DBDictionary();
            nod.SetAt(DictName, dict);
            tr.AddNewlyCreatedDBObject(dict, true);
        }

        AcadDb.Xrecord rec;
        if (dict.Contains(key))
        {
            rec = (AcadDb.Xrecord)tr.GetObject(dict.GetAt(key), AcadDb.OpenMode.ForWrite);
        }
        else
        {
            rec = new AcadDb.Xrecord();
            dict.SetAt(key, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        using AcadDb.ResultBuffer rb = new(lines.Select(l => new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, l.Length > 250 ? l[..250] : l)).ToArray());
        rec.Data = rb;
    }

    private static double D(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static string S(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);
}
