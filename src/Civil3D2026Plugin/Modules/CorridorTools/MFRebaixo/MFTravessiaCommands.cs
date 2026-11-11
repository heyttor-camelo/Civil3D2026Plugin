using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Travessia elevada / semi-elevada (lombofaixa) entre dois rebaixos de meio-fio frente a frente.
///
/// - Plato = plataforma (faixa) + rampa dos carros de cada lado, de borda a borda da pista, sobre
///   a superficie de topo do corredor; solido 3D com Property Set C3D_TRAVESSIA.
/// - Sarjeta livre com tampa de concreto por cima, no nivel do plato (a agua passa por baixo).
/// - Planta: plato e rampas em cinza, faixa zebrada e triangulos brancos nas rampas.
/// - Semi-elevada: os dois rebaixos vao para o eixo da travessia e a altura final deles passa a
///   deixar o topo do meio-fio no nivel do plato. Elevada: plato no nivel da calcada, rebaixos ficam.
/// </summary>
public sealed class MFTravessiaCommands
{
    /// <summary>Rebaixo escolhido no painel antes de chamar o comando (nome do Transition Set).</summary>
    internal static string? PreSelectedSet;

    private const string Mod = "MFTRAVESSIA";

    [CommandMethod(C3DCommands.Corridor.MfTravessia, CommandFlags.Modal)]
    public void Criar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        string? pre = PreSelectedSet;
        PreSelectedSet = null;

        try
        {
            List<RebaixoInfo> items = MFRebaixoData.ReadAll(db);
            if (items.Count < 2)
            {
                ed.WriteMessage("\nA travessia liga dois rebaixos: lance os rebaixos dos dois lados da rua primeiro (MFREBAIXO).\n");
                return;
            }

            RebaixoInfo? a = pre is null ? null : items.FirstOrDefault(r => r.SetName == pre);
            if (a is null)
            {
                a = PickRebaixo(ed, items, "\nClique perto do primeiro rebaixo: ", null);
                if (a is null)
                    return;
            }
            ed.WriteMessage($"\nPrimeiro rebaixo: {Describe(a)}");

            RebaixoInfo? auto = Facing(a, items);
            RebaixoInfo? b = PickRebaixo(ed, items.Where(r => r != a).ToList(),
                auto is null ? "\nClique perto do rebaixo do outro lado da rua: " : $"\nClique perto do rebaixo do outro lado da rua ou Enter para {auto.Label}: ",
                auto);
            if (b is null)
                return;
            ed.WriteMessage($"\nSegundo rebaixo: {Describe(b)}");

            MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
            var p = new TravessiaParams
            {
                Tipo = settings.TravessiaTipo == 1 ? TravessiaTipo.Elevada : TravessiaTipo.SemiElevada,
                Altura = settings.TravessiaAltura,
                Plataforma = settings.TravessiaPlataforma,
                Rampa = settings.TravessiaRampa,
                Tampa = settings.TravessiaTampa,
                TampaEspessura = settings.TravessiaTampaEspessura,
                Centralizar = settings.TravessiaCentralizar
            };
            if (!AskParams(ed, a, b, p))
                return;

            settings.TravessiaTipo = (int)p.Tipo;
            settings.TravessiaAltura = p.Altura;
            settings.TravessiaPlataforma = p.Plataforma;
            settings.TravessiaRampa = p.Rampa;
            settings.TravessiaTampa = p.Tampa;
            settings.TravessiaTampaEspessura = p.TampaEspessura;
            settings.TravessiaCentralizar = p.Centralizar;
            MFRebaixoSettingsStore.Save(settings);

            // Eixo: no meio dos dois rebaixos; estaca dele na baseline de cada um.
            Point3d ca = Center(a), cb = Center(b);
            Point3d mid = new((ca.X + cb.X) / 2.0, (ca.Y + cb.Y) / 2.0, 0);
            double stA = StationOrMiddle(db, a, mid);
            double stB = StationOrMiddle(db, b, mid);

            var (side1, side2, geo, roadInfo) = Compute(db, a, b, stA, stB, p);

            // Mudancas nos rebaixos.
            var plan = new List<(RebaixoInfo R, double S1, double S2, double H, string Text)>();
            foreach ((RebaixoInfo r, double st, double target) in new (RebaixoInfo, double, double)[] { (a, stA, geo.TargetExposedA), (b, stB, geo.TargetExposedB) })
            {
                double s1 = r.S1, s2 = r.S2;
                if (p.Centralizar && p.Tipo == TravessiaTipo.SemiElevada)
                {
                    s1 = st - (r.LoweredLength / 2.0);
                    s2 = st + (r.LoweredLength / 2.0);
                }
                double h = r.FinalHeight ?? target;
                var changes = new List<string>();
                if (Math.Abs(s1 - r.S1) > 0.02)
                    changes.Add($"trecho rebaixado {Num(r.S1)}-{Num(r.S2)} -> {Num(s1)}-{Num(s2)}");
                if (p.Tipo == TravessiaTipo.SemiElevada)
                {
                    double normal = r.NormalHeight ?? double.MaxValue;
                    if (target < 0.0)
                        throw new InvalidOperationException($"{r.Label}: o plato ficaria abaixo do pe do meio-fio ({MFTravessia.Cm(target)}). Aumente a altura.");
                    if (target > normal - 0.005)
                        throw new InvalidOperationException($"{r.Label}: o plato ({MFTravessia.Cm(target)} acima do pe do meio-fio) passa da altura normal do meio-fio ({MFTravessia.Cm(normal)}). Use a travessia elevada ou diminua a altura.");
                    if (r.FinalHeight is not double cur || Math.Abs(cur - target) > 0.005)
                        changes.Add($"altura final {(r.FinalHeight is double c ? MFTravessia.Cm(c) : "?")} -> {MFTravessia.Cm(target)}");
                    h = target;
                }
                if (changes.Count > 0)
                    plan.Add((r, s1, s2, h, $"{r.Label}: " + string.Join(", ", changes)));
            }

            // Confirmacao (regra do Heyvid: acao que mexe em varias pecas explica o que muda e como desfazer).
            ed.WriteMessage($"\n\nTravessia {p.TipoTexto} entre {a.Label} e {b.Label}:");
            ed.WriteMessage($"\n  plataforma {MFTravessia.N(p.Plataforma)} m + rampas de {MFTravessia.N(p.Rampa)} m, altura {MFTravessia.Cm((geo.HeightA + geo.HeightB) / 2.0)} acima da borda da pista, {MFTravessia.N(geo.WidthAtAxis)} m de borda a borda");
            ed.WriteMessage($"\n  {geo.RoadSource}");
            if (p.Tampa)
                ed.WriteMessage($"\n  tampa de concreto de {MFTravessia.Cm(p.TampaEspessura)} sobre a sarjeta, nos dois lados");
            foreach (string note in geo.Notes)
                ed.WriteMessage("\n  " + note);
            if (plan.Count == 0)
                ed.WriteMessage("\n  Rebaixos: nada muda.");
            foreach (var item in plan)
                ed.WriteMessage("\n  Muda no corredor: " + item.Text);
            if (p.Tipo == TravessiaTipo.Elevada)
                ed.WriteMessage("\n  Elevada: o plato fica no nivel da calcada e os rebaixos nao sao mexidos (podem ser removidos no painel).");
            ed.WriteMessage($"\n  Desenha: solido do plato e tampas (layer {settings.TravessiaLayer}-3D / -TAMPA), hachura, faixa e triangulos ({settings.TravessiaLayer}, -SINALIZACAO).");
            ed.WriteMessage("\n  Para desfazer: U (ou Ctrl+Z) logo em seguida.");

            var confirm = new PromptKeywordOptions("\nCriar a travessia? [Sim/Nao]", "Sim Nao") { AllowNone = true };
            confirm.Keywords.Default = "Sim";
            PromptResult ok = ed.GetKeywords(confirm);
            if (ok.Status == PromptStatus.Cancel || (ok.Status == PromptStatus.OK && ok.StringResult == "Nao"))
            {
                ed.WriteMessage("\nNada foi feito.\n");
                return;
            }

            // 1) Rebaixos.
            foreach (var item in plan)
                Reapply(db, ed, item.R, item.S1, item.S2, item.H);

            // 2) Desenho (o meio-fio e a pista nao dependem do REB; relido so se os rebaixos mudaram).
            if (plan.Count > 0)
                (side1, side2, geo, roadInfo) = Compute(db, a, b, stA, stB, p);
            var data = new MFTravessia.Stored
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant(),
                Params = p,
                Origin = mid,
                CorridorA = side1.CorridorHandle,
                BaselineA = side1.BaselineKey,
                SubassemblyA = side1.SubassemblyName,
                LeftA = side1.Left,
                LabelA = a.Label,
                CorridorB = side2.CorridorHandle,
                BaselineB = side2.BaselineKey,
                SubassemblyB = side2.SubassemblyName,
                LeftB = side2.Left,
                LabelB = b.Label
            };
            (int count, List<string> notes) = MFTravessia.Draw(db, geo, data, settings);
            string fim = $"Travessia {data.Id} ({p.TipoTexto}, {a.Label}-{b.Label}): {count} objeto(s) desenhado(s); {string.Join("; ", notes)}.";
            ed.WriteMessage("\n" + fim + "\n");
            PluginLog.Info(Mod, fim + " " + roadInfo);
            ed.Regen();
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "MFTRAVESSIA: " + ex.Message, ex);
            ed.WriteMessage("\nTravessia nao criada: " + ex.Message + "\n");
        }
    }

    [CommandMethod(C3DCommands.Corridor.MfTravessiaRefazer, CommandFlags.Modal)]
    public void Refazer()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        try
        {
            List<(AcadDb.ObjectId Id, MFTravessia.Stored Data)> all = MFTravessia.FindAll(db);
            if (all.Count == 0)
            {
                ed.WriteMessage("\nNenhuma travessia (MFTRAVESSIA) no desenho.\n");
                return;
            }

            var mode = new PromptKeywordOptions($"\n{all.Count} travessia(s) no desenho. Refazer [Todas/Selecionar]", "Todas Selecionar") { AllowNone = true };
            mode.Keywords.Default = "Todas";
            PromptResult mr = ed.GetKeywords(mode);
            if (mr.Status == PromptStatus.Cancel)
                return;
            if (mr.Status == PromptStatus.OK && mr.StringResult == "Selecionar")
            {
                PromptSelectionResult sel = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelecione partes das travessias: " });
                if (sel.Status != PromptStatus.OK)
                    return;
                var ids = new HashSet<string>();
                using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (AcadDb.ObjectId oid in sel.Value.GetObjectIds())
                    {
                        if (tr.GetObject(oid, AcadDb.OpenMode.ForRead) is not AcadDb.Entity e)
                            continue;
                        (string Id, string Part)? part = MFTravessia.PartOf(e);
                        if (part is not null)
                            ids.Add(part.Value.Id);
                    }
                    tr.Commit();
                }
                all = all.Where(x => ids.Contains(x.Data.Id)).ToList();
            }

            MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
            List<RebaixoInfo> rebaixos = MFRebaixoData.ReadAll(db);
            int ok = 0, fail = 0;
            foreach ((_, MFTravessia.Stored d) in all)
            {
                try
                {
                    AcadDb.ObjectId ca = CorridorId(db, d.CorridorA), cb = CorridorId(db, d.CorridorB);
                    if (ca.IsNull || cb.IsNull)
                        throw new InvalidOperationException("corredor nao existe mais");
                    double stA = Station(db, ca, d.BaselineA, d.Origin) ?? throw new InvalidOperationException($"eixo fora da baseline de {d.LabelA}");
                    double stB = Station(db, cb, d.BaselineB, d.Origin) ?? throw new InvalidOperationException($"eixo fora da baseline de {d.LabelB}");
                    double span = (d.Params.Plataforma / 2.0) + d.Params.Rampa + 4.0;
                    TravessiaSide s1 = MFTravessia.ReadSide(db, ca, d.BaselineA, d.SubassemblyA, d.LeftA, d.LabelA, d.Origin, stA, stA - span, stA + span);
                    TravessiaSide s2 = MFTravessia.ReadSide(db, cb, d.BaselineB, d.SubassemblyB, d.LeftB, d.LabelB, d.Origin, stB, stB - span, stB + span);
                    RebaixoInfo? ra = RebaixoAt(rebaixos, ca, d.BaselineA, stA);
                    RebaixoInfo? rb = RebaixoAt(rebaixos, cb, d.BaselineB, stB);
                    Func<double, double, double?> road = MFTravessia.RoadSurface(db, new[] { ca, cb }, out string roadInfo);
                    TravessiaGeometry geo = MFTravessia.Build(s1, s2, d.Params, road, ra?.NormalHeight, rb?.NormalHeight);
                    (int count, List<string> notes) = MFTravessia.Draw(db, geo, d, settings);
                    ok++;
                    string line = $"Travessia {d.Id} ({d.LabelA}-{d.LabelB}) refeita: {count} objeto(s). {roadInfo}";
                    if (d.Params.Tipo == TravessiaTipo.SemiElevada)
                    {
                        foreach ((RebaixoInfo? r, double target, string label) in new (RebaixoInfo?, double, string)[] { (ra, geo.TargetExposedA, d.LabelA), (rb, geo.TargetExposedB, d.LabelB) })
                        {
                            if (r is null)
                                line += $" AVISO: nao ha rebaixo no eixo em {label}.";
                            else if (r.FinalHeight is double h && Math.Abs(h - target) > 0.01)
                                line += $" AVISO: {r.Label} esta com altura final {MFTravessia.Cm(h)}; o plato pede {MFTravessia.Cm(target)} (rode MFTRAVESSIA de novo para acertar).";
                        }
                    }
                    ed.WriteMessage("\n" + line);
                    PluginLog.Info(Mod, line + " " + string.Join("; ", notes));
                }
                catch (System.Exception ex)
                {
                    fail++;
                    ed.WriteMessage($"\nTravessia {d.Id} ({d.LabelA}-{d.LabelB}) nao refeita: {ex.Message}");
                    PluginLog.Aviso(Mod, $"Travessia {d.Id} nao refeita: {ex.Message}");
                }
            }
            ed.WriteMessage($"\n{ok} travessia(s) refeita(s){(fail > 0 ? $", {fail} com erro" : "")}.\n");
            ed.Regen();
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "MFTRAVESSIAREFAZER: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    // ------------------------------------------------------------------

    private static (TravessiaSide A, TravessiaSide B, TravessiaGeometry Geo, string RoadInfo) Compute(
        AcadDb.Database db, RebaixoInfo a, RebaixoInfo b, double stA, double stB, TravessiaParams p)
    {
        double span = (p.Plataforma / 2.0) + p.Rampa + 4.0;
        TravessiaSide s1 = MFTravessia.ReadSide(db, a, stA, stA - span, stA + span);
        TravessiaSide s2 = MFTravessia.ReadSide(db, b, stB, stB - span, stB + span);
        PluginLog.Diag(Mod, $"{a.Label}: {s1.Samples.Count} secoes do meio-fio em {Num(stA - span)}-{Num(stA + span)}; {b.Label}: {s2.Samples.Count} secoes em {Num(stB - span)}-{Num(stB + span)}");
        Func<double, double, double?> road = MFTravessia.RoadSurface(db, new[] { a.CorridorId, b.CorridorId }, out string roadInfo);
        TravessiaGeometry geo = MFTravessia.Build(s1, s2, p, road, a.NormalHeight, b.NormalHeight);
        return (s1, s2, geo, roadInfo);
    }

    /// <summary>Remove e lanca de novo o rebaixo (mesmo caminho do painel); se falhar, volta o antigo.</summary>
    private static void Reapply(AcadDb.Database db, Editor ed, RebaixoInfo r, double s1, double s2, double h)
    {
        double oldH = r.FinalHeight ?? h;
        MFRebaixoData.Remove(db, r, rebuild: true);
        try
        {
            MFRebaixoCommands.ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, s1, s2, h, MFRebaixoData.CommentFor(r.CustomName));
            PluginLog.Info(Mod, $"{r.Label} reaplicado: {Num(s1)}-{Num(s2)}, altura final {MFTravessia.Cm(h)}.");
        }
        catch (System.Exception ex)
        {
            try
            {
                MFRebaixoCommands.ApplyFromPanel(db, ed, r.CorridorId, r.BaselineKey, r.RegionName, r.Left, r.S1, r.S2, oldH, MFRebaixoData.CommentFor(r.CustomName));
            }
            catch
            {
                throw new InvalidOperationException($"{r.Label}: {ex.Message} (o rebaixo antigo tambem nao voltou; use U para desfazer)");
            }
            throw new InvalidOperationException($"{r.Label}: {ex.Message} (o rebaixo antigo foi mantido)");
        }
    }

    private static bool AskParams(Editor ed, RebaixoInfo a, RebaixoInfo b, TravessiaParams p)
    {
        while (true)
        {
            string alt = p.Tipo == TravessiaTipo.Elevada ? "altura da calcada" : "altura " + MFTravessia.Cm(p.Altura);
            string msg = $"\nTravessia {a.Label}-{b.Label}: {p.TipoTexto}, {alt}, plataforma {MFTravessia.N(p.Plataforma)} m, rampas {MFTravessia.N(p.Rampa)} m, " +
                         $"tampa {(p.Tampa ? MFTravessia.Cm(p.TampaEspessura) : "nao")}, centralizar rebaixos {(p.Centralizar ? "sim" : "nao")}." +
                         "\nMudar algo ou Enter para seguir [Tipo/Altura/Plataforma/Rampa/tAmpa/Centralizar/Seguir]";
            var opt = new PromptKeywordOptions(msg, "Tipo Altura Plataforma Rampa tAmpa Centralizar Seguir") { AllowNone = true };
            opt.Keywords.Default = "Seguir";
            PromptResult r = ed.GetKeywords(opt);
            if (r.Status == PromptStatus.Cancel)
                return false;
            string k = r.Status == PromptStatus.OK ? r.StringResult : "Seguir";
            switch (k)
            {
                case "Seguir":
                    return true;
                case "Tipo":
                {
                    var t = new PromptKeywordOptions("\nTipo [Semi/Elevada]", "Semi Elevada") { AllowNone = true };
                    t.Keywords.Default = p.Tipo == TravessiaTipo.Elevada ? "Elevada" : "Semi";
                    PromptResult tr = ed.GetKeywords(t);
                    if (tr.Status == PromptStatus.OK)
                        p.Tipo = tr.StringResult == "Elevada" ? TravessiaTipo.Elevada : TravessiaTipo.SemiElevada;
                    break;
                }
                case "Altura":
                    if (p.Tipo == TravessiaTipo.Elevada)
                        ed.WriteMessage("\nNa elevada a altura e a do meio-fio (nivel da calcada), lida do corredor.");
                    else if (AskDouble(ed, "\nAltura do plato acima da borda da pista (m)", p.Altura, 0.03, 0.30) is double h)
                        p.Altura = h;
                    break;
                case "Plataforma":
                    if (AskDouble(ed, "\nLargura da plataforma, no sentido dos carros (m; o detalhe pede > 4,0)", p.Plataforma, 1.0, 20.0) is double w)
                        p.Plataforma = w;
                    break;
                case "Rampa":
                    if (AskDouble(ed, "\nComprimento de cada rampa dos carros (m)", p.Rampa, 0.10, 10.0) is double rl)
                        p.Rampa = rl;
                    break;
                case "tAmpa":
                {
                    var t = new PromptKeywordOptions("\nTampa de concreto sobre a sarjeta [Sim/Nao]", "Sim Nao") { AllowNone = true };
                    t.Keywords.Default = p.Tampa ? "Sim" : "Nao";
                    PromptResult tr = ed.GetKeywords(t);
                    if (tr.Status == PromptStatus.OK)
                        p.Tampa = tr.StringResult == "Sim";
                    if (p.Tampa && AskDouble(ed, "\nEspessura da tampa (m)", p.TampaEspessura, 0.03, 0.30) is double e)
                        p.TampaEspessura = e;
                    break;
                }
                case "Centralizar":
                {
                    var t = new PromptKeywordOptions("\nMover os rebaixos para o eixo da travessia [Sim/Nao]", "Sim Nao") { AllowNone = true };
                    t.Keywords.Default = p.Centralizar ? "Sim" : "Nao";
                    PromptResult tr = ed.GetKeywords(t);
                    if (tr.Status == PromptStatus.OK)
                        p.Centralizar = tr.StringResult == "Sim";
                    break;
                }
            }
        }
    }

    private static double? AskDouble(Editor ed, string message, double current, double min, double max)
    {
        var o = new PromptDoubleOptions($"{message} <{current.ToString("0.00", CultureInfo.InvariantCulture)}>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false
        };
        PromptDoubleResult r = ed.GetDouble(o);
        if (r.Status != PromptStatus.OK)
            return null;
        if (r.Value < min || r.Value > max)
        {
            ed.WriteMessage($"\nValor fora do intervalo ({min.ToString("0.00", CultureInfo.InvariantCulture)} a {max.ToString("0.00", CultureInfo.InvariantCulture)}); mantido o anterior.");
            return null;
        }
        return r.Value;
    }

    private static RebaixoInfo? PickRebaixo(Editor ed, List<RebaixoInfo> items, string message, RebaixoInfo? auto)
    {
        while (true)
        {
            var o = new PromptPointOptions(message) { AllowNone = auto is not null };
            PromptPointResult r = ed.GetPoint(o);
            if (r.Status == PromptStatus.None && auto is not null)
                return auto;
            if (r.Status != PromptStatus.OK)
                return null;
            Point3d pt = r.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            RebaixoInfo? best = null;
            double bestD = double.MaxValue;
            foreach (RebaixoInfo item in items)
            {
                double d = DistanceTo(item, pt);
                if (d < bestD)
                {
                    bestD = d;
                    best = item;
                }
            }
            if (best is not null && bestD < 15.0)
                return best;
            ed.WriteMessage("\nNenhum rebaixo perto do clique (ate 15 m). Clique mais perto do meio-fio rebaixado.");
        }
    }

    /// <summary>O rebaixo do outro lado da rua, de frente para <paramref name="a"/> (ate 40 m).</summary>
    private static RebaixoInfo? Facing(RebaixoInfo a, List<RebaixoInfo> items)
    {
        Point3d ca = Center(a);
        Vector3d dir = a.Path.Count >= 2 ? a.Path[^1] - a.Path[0] : a.P3 - a.P0;
        dir = new Vector3d(dir.X, dir.Y, 0);
        if (dir.Length < 1e-6)
            return null;
        dir = dir.GetNormal();
        RebaixoInfo? best = null;
        double bestScore = double.MaxValue;
        foreach (RebaixoInfo r in items)
        {
            if (r == a)
                continue;
            Vector3d d = Center(r) - ca;
            d = new Vector3d(d.X, d.Y, 0);
            double along = Math.Abs(d.DotProduct(dir));
            double across = Math.Abs((d.X * dir.Y) - (d.Y * dir.X));
            if (across < 2.0 || d.Length > 40.0 || along > 15.0)
                continue;
            double score = along + (0.1 * across);
            if (score < bestScore)
            {
                bestScore = score;
                best = r;
            }
        }
        return best;
    }

    private static Point3d Center(RebaixoInfo r)
    {
        double mid = (r.S1 + r.S2) / 2.0;
        if (r.Band.Count > 0)
        {
            var e = r.Band.OrderBy(x => Math.Abs(x.Station - mid)).First();
            return new Point3d((e.Inner.X + e.Outer.X) / 2.0, (e.Inner.Y + e.Outer.Y) / 2.0, 0);
        }
        return new Point3d((r.P1.X + r.P2.X) / 2.0, (r.P1.Y + r.P2.Y) / 2.0, 0);
    }

    private static double DistanceTo(RebaixoInfo r, Point3d p)
    {
        IEnumerable<Point3d> pts = r.Path.Count > 0 ? r.Path : new[] { r.P0, r.P1, r.P2, r.P3 };
        return pts.Min(q => Math.Sqrt(((q.X - p.X) * (q.X - p.X)) + ((q.Y - p.Y) * (q.Y - p.Y))));
    }

    private static double StationOrMiddle(AcadDb.Database db, RebaixoInfo r, Point3d p) =>
        MFRebaixoCommands.TryStationAt(db, r.CorridorId, r.BaselineKey, p, out double st) ? st : (r.S1 + r.S2) / 2.0;

    private static double? Station(AcadDb.Database db, AcadDb.ObjectId corridorId, string baselineKey, Point3d p) =>
        MFRebaixoCommands.TryStationAt(db, corridorId, baselineKey, p, out double st) ? st : null;

    private static RebaixoInfo? RebaixoAt(List<RebaixoInfo> items, AcadDb.ObjectId corridorId, string baselineKey, double station) =>
        items.Where(r => r.CorridorId == corridorId && r.BaselineKey == baselineKey && station >= r.S0 - 0.5 && station <= r.S3 + 0.5)
             .OrderBy(r => Math.Abs(((r.S1 + r.S2) / 2.0) - station))
             .FirstOrDefault();

    private static AcadDb.ObjectId CorridorId(AcadDb.Database db, string handle)
    {
        try
        {
            AcadDb.ObjectId id = db.GetObjectId(false, new AcadDb.Handle(Convert.ToInt64(handle, 16)), 0);
            return id.IsErased ? AcadDb.ObjectId.Null : id;
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    private static string Describe(RebaixoInfo r) =>
        $"{r.Label} ({r.SideLabel}, {r.CorridorName}, altura final {(r.FinalHeight is double h ? MFTravessia.Cm(h) : "?")})";

    private static string Num(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
