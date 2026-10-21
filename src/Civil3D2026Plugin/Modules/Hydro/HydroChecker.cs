using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Civil3D2026Plugin.Modules.Hydro.Calc;

namespace Civil3D2026Plugin.Modules.Hydro;

internal enum HydroSeverity
{
    Aviso,
    Erro
}

/// <summary>Uma violacao de criterio ou de regra num item da rede.</summary>
internal sealed record HydroViolation(string Handle, string Item, string Rule, string Value, string Limit, HydroSeverity Severity, string Text, string Fix = "");

/// <summary>Verificacoes do projeto (criterios do painel + leitura das regras do Civil) e correcoes sugeridas.</summary>
internal static class HydroChecker
{
    private static string N(double v, int d = 2) => double.IsNaN(v) ? "-" : v.ToString("F" + d, CultureInfo.InvariantCulture);

    private static string Pct(double v) => (v * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %";

    public static List<HydroViolation> Check(NetworkInfo net, HydroSnapshot snap, HydroSettings s, Dictionary<string, HydroResult> results)
    {
        List<HydroViolation> list = new();
        foreach (PipeInfo p in net.OrderedPipes())
        {
            if (!p.Defined)
            {
                list.Add(new HydroViolation(p.Handle, p.Name, "Sentido do fluxo", p.FlowDirection, "StartToEnd/EndToStart", HydroSeverity.Erro,
                    "Tubo sem sentido de fluxo definido ou com ponta solta: o calculo acumulado o ignora.", "Inverter sentido / Conectar"));
                continue;
            }

            if (p.Slope < -1e-6)
                list.Add(new HydroViolation(p.Handle, p.Name, "Contra-declive", Pct(p.Slope), ">= 0", HydroSeverity.Erro, "Fundo de jusante acima do de montante.", "Declividade minima segurando montante"));
            else if (p.Slope < s.MinSlope - 1e-9)
                list.Add(new HydroViolation(p.Handle, p.Name, "Declividade minima", Pct(p.Slope), Pct(s.MinSlope), HydroSeverity.Aviso, "Declividade abaixo da minima.", "Declividade minima segurando montante"));
            if (p.Slope > s.MaxSlope + 1e-9)
                list.Add(new HydroViolation(p.Handle, p.Name, "Declividade maxima", Pct(p.Slope), Pct(s.MaxSlope), HydroSeverity.Aviso, "Declividade acima da maxima (ver velocidade).", "Declividade maxima segurando montante"));

            double cover = p.MinCoverActual;
            if (!double.IsNaN(cover) && cover < s.MinCover - 1e-6)
                list.Add(new HydroViolation(p.Handle, p.Name, "Recobrimento minimo", N(cover), N(s.MinCover), HydroSeverity.Aviso, "Recobrimento (pela superficie de referencia do Civil) abaixo do minimo.", "Baixar o tubo"));
            if (!double.IsNaN(p.MinimumCover) && !double.IsNaN(cover) && cover < p.MinimumCover - 1e-6)
                list.Add(new HydroViolation(p.Handle, p.Name, "Regra do Civil: recobrimento", N(cover), N(p.MinimumCover), HydroSeverity.Aviso, "Abaixo do recobrimento minimo do rule set do tubo.", "Aplicar regras"));

            if (results.TryGetValue(p.Handle, out HydroResult? r))
            {
                if (!r.Flows)
                    list.Add(new HydroViolation(p.Handle, p.Name, "Capacidade", N(r.Flow, 4) + " m3/s", N(r.FullFlow, 4) + " m3/s", HydroSeverity.Erro, "Vazao de projeto maior que a vazao a secao plena.", "Menor diametro que atende"));
                else if (r.DepthRatio > s.MaxDepthRatio + 1e-9)
                    list.Add(new HydroViolation(p.Handle, p.Name, "Lamina maxima", N(r.DepthRatio * 100, 0) + " %", N(s.MaxDepthRatio * 100, 0) + " %", HydroSeverity.Erro, "Lamina acima da maxima.", "Menor diametro que atende"));
                if (r.Flow > 1e-6 && r.Velocity < s.MinVelocity - 1e-9)
                    list.Add(new HydroViolation(p.Handle, p.Name, "Velocidade minima", N(r.Velocity) + " m/s", N(s.MinVelocity) + " m/s", HydroSeverity.Aviso, "Velocidade abaixo da minima (risco de deposicao).", "Aumentar a declividade"));
                if (r.Velocity > s.MaxVelocity + 1e-9)
                    list.Add(new HydroViolation(p.Handle, p.Name, "Velocidade maxima", N(r.Velocity) + " m/s", N(s.MaxVelocity) + " m/s", HydroSeverity.Aviso, "Velocidade acima da maxima (erosao).", "Reduzir a declividade"));
            }
        }

        foreach (StructureInfo st in net.Ordered())
        {
            List<PipeInfo> ins = st.PipesIn.Select(h => net.PipeByHandle.GetValueOrDefault(h)).Where(p => p is not null).Select(p => p!).ToList();
            List<PipeInfo> outs = st.PipesOut.Select(h => net.PipeByHandle.GetValueOrDefault(h)).Where(p => p is not null).Select(p => p!).ToList();
            if (ins.Count > 0 && outs.Count > 0)
            {
                double inMin = ins.Min(p => p.DownInvert);
                double outMax = outs.Max(p => p.UpInvert);
                double drop = inMin - outMax;
                if (drop < -1e-3)
                    list.Add(new HydroViolation(st.Handle, st.Name, "Degrau negativo", N(drop, 3) + " m", ">= 0", HydroSeverity.Erro, "Tubo de saida com fundo acima de um tubo de entrada.", "Baixar o tubo de saida ate o de entrada"));
                else if (drop > s.MaxDrop + 1e-6)
                    list.Add(new HydroViolation(st.Handle, st.Name, "Degrau maximo", N(drop, 3) + " m", N(s.MaxDrop) + " m", HydroSeverity.Aviso, "Degrau entre entrada e saida acima do maximo (tubo de queda?)."));
            }

            double lowest = ins.Select(p => p.DownInvert).Concat(outs.Select(p => p.UpInvert)).DefaultIfEmpty(double.NaN).Min();
            if (!double.IsNaN(lowest) && st.Sump > lowest + 1e-3)
                list.Add(new HydroViolation(st.Handle, st.Name, "Fundo da estrutura", N(st.Sump, 3), "<= " + N(lowest, 3), HydroSeverity.Aviso, "Fundo da estrutura acima do menor fundo de tubo ligado.", "Fundo = menor fundo de tubo"));
            if (st.Depth < 0.05)
                list.Add(new HydroViolation(st.Handle, st.Name, "Profundidade", N(st.Depth), "> 0", HydroSeverity.Erro, "Tampa abaixo do fundo (ou quase)."));
            if (st.PipesUndefined.Count > 0)
                list.Add(new HydroViolation(st.Handle, st.Name, "Tubo sem sentido", st.PipesUndefined.Count.ToString(CultureInfo.InvariantCulture), "0", HydroSeverity.Aviso, "Tubo ligado sem sentido de fluxo."));
            if (ins.Count == 0 && outs.Count == 0 && st.PipesUndefined.Count == 0)
                list.Add(new HydroViolation(st.Handle, st.Name, "Sem tubo", "0", ">= 1", HydroSeverity.Aviso, "Estrutura isolada."));
        }

        foreach (CatchmentInfo c in snap.Catchments.Where(c => c.NetworkName == net.Name || c.StructureHandle.Length > 0))
        {
            if (c.StructureHandle.Length == 0)
                list.Add(new HydroViolation(c.Handle, c.Name, "Bacia sem destino", "-", "estrutura", HydroSeverity.Aviso, "Bacia sem estrutura de engolimento: nao entra na soma.", "Ligar a estrutura (HYLIGARBACIA)"));
            if (c.C <= 0)
                list.Add(new HydroViolation(c.Handle, c.Name, "Coeficiente C", N(c.C), "> 0", HydroSeverity.Aviso, "Bacia com C zero."));
        }

        return list.OrderByDescending(v => v.Severity).ThenBy(v => v.Item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Correcoes para o menu "Corrigir" (acao nula = so informacao).</summary>
    public static List<(string Title, Action<AcadDb.Database>? Action)> Fixes(HydroViolation v, NetworkInfo? net, HydroSettings s)
    {
        List<(string, Action<AcadDb.Database>?)> list = new();
        if (net is null)
            return list;
        PipeInfo? p = net.PipeByHandle.GetValueOrDefault(v.Handle);
        StructureInfo? st = net.StructureByHandle.GetValueOrDefault(v.Handle);
        if (p is not null)
        {
            double minSlope = s.MinSlope, maxSlope = s.MaxSlope;
            list.Add(($"Declividade = minima ({Pct(minSlope)}) segurando montante", db => HydroEditor.EditPipe(db, p.Handle, null, null, null, minSlope)));
            list.Add(($"Declividade = maxima ({Pct(maxSlope)}) segurando montante", db => HydroEditor.EditPipe(db, p.Handle, null, null, null, maxSlope)));
            if (v.Rule is "Capacidade" or "Lamina maxima" or "Velocidade minima")
            {
                double? d = Manning.SmallestDiameter(ResultFlow(v), Math.Max(p.Slope, 1e-5), double.IsNaN(p.Manning) ? s.Manning : p.Manning, s.Diameters.ToArray(), s.Criteria);
                if (d.HasValue && d.Value > p.InnerDiameter + 1e-6)
                    list.Add(($"Trocar pelo menor diametro que atende ({Math.Round(d.Value * 1000)} mm, tamanho mais proximo da familia)", db => HydroEditor.ResizePipe(db, p.Handle, d.Value)));
                else if (!d.HasValue)
                    list.Add(("Nenhum diametro da lista atende com esta declividade: aumente a declividade", null));
            }

            if (v.Rule.Contains("Recobrimento", StringComparison.OrdinalIgnoreCase) && !double.IsNaN(p.MinCoverActual))
            {
                double delta = p.MinCoverActual - s.MinCover - 0.01;
                list.Add(($"Baixar o tubo inteiro {N(-delta)} m (mantem a declividade)", db => HydroEditor.EditPipe(db, p.Handle, null, p.UpInvert + delta, p.DownInvert + delta, null)));
            }

            if (v.Rule == "Sentido do fluxo")
                list.Add(("Inverter sentido (bidirecional vira Start > End)", db => HydroEditor.Reverse(db, new[] { p.Handle })));
            list.Add(("Aplicar regras do Civil", db => HydroEditor.ApplyRules(db, new[] { p.Handle })));
        }
        else if (st is not null)
        {
            List<PipeInfo> ins = st.PipesIn.Select(h => net.PipeByHandle.GetValueOrDefault(h)).Where(x => x is not null).Select(x => x!).ToList();
            List<PipeInfo> outs = st.PipesOut.Select(h => net.PipeByHandle.GetValueOrDefault(h)).Where(x => x is not null).Select(x => x!).ToList();
            double lowest = ins.Select(x => x.DownInvert).Concat(outs.Select(x => x.UpInvert)).DefaultIfEmpty(double.NaN).Min();
            if (!double.IsNaN(lowest))
                list.Add(($"Fundo da estrutura = menor fundo de tubo ({N(lowest, 3)})", db => HydroEditor.EditStructure(db, st.Handle, null, null, lowest, HydroStore.HoldMode.OutraPonta, movePipes: false)));
            if (ins.Count > 0 && outs.Count > 0)
            {
                double inMin = ins.Min(x => x.DownInvert);
                foreach (PipeInfo o in outs.Where(o => o.UpInvert > inMin + 1e-3))
                {
                    PipeInfo oo = o;
                    list.Add(($"Baixar o fundo de montante de {oo.Name} para {N(inMin, 3)} (segura jusante)", db => HydroEditor.EditPipe(db, oo.Handle, null, inMin, null, null)));
                }
            }

            list.Add(("Aplicar regras do Civil", db => HydroEditor.ApplyRules(db, new[] { st.Handle })));
        }
        else
        {
            list.Add(("Ligar a bacia a uma estrutura: use HYLIGARBACIA ou a aba Propriedades", null));
        }

        return list;
    }

    private static double ResultFlow(HydroViolation v)
    {
        string t = v.Value.Replace("m3/s", "").Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double q) ? q : 0;
    }
}
