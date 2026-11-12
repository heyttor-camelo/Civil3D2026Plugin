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

            // O Heyvid quer clicar nos dois rebaixos (11/11/2026): nada de escolha automatica.
            _ = pre;
            // Superficie de topo primeiro (pedido do Heyvid): a escolha fica como padrao.
            MFRebaixoSettings settings = MFRebaixoSettingsStore.Load();
            if (!MFTravessia.Surfaces(db).Any(x => x.Name.Equals(settings.TravessiaSuperficie, StringComparison.OrdinalIgnoreCase)))
            {
                string? chosen = AskSurface(ed, db, settings.TravessiaSuperficie);
                if (chosen is null)
                    return;
                settings.TravessiaSuperficie = chosen;
                MFRebaixoSettingsStore.Save(settings);
            }
            ed.WriteMessage($"\nSuperficie de topo: {(settings.TravessiaSuperficie.Length > 0 ? settings.TravessiaSuperficie : "automatica (a de topo do corredor)")}.");

            RebaixoInfo? a = PickRebaixo(ed, items, "\nClique no primeiro rebaixo: ", null);
            if (a is null)
                return;
            ed.WriteMessage($"\nPrimeiro rebaixo: {Describe(a)}");

            RebaixoInfo? b = PickRebaixo(ed, items.Where(r => r != a).ToList(), "\nClique no rebaixo do outro lado da rua: ", null);
            if (b is null)
                return;
            ed.WriteMessage($"\nSegundo rebaixo: {Describe(b)}");

            var p = new TravessiaParams
            {
                Tipo = settings.TravessiaTipo == 1 ? TravessiaTipo.Elevada : TravessiaTipo.SemiElevada,
                Altura = settings.TravessiaAltura,
                Plataforma = settings.TravessiaPlataforma,
                Rampa = settings.TravessiaRampa,
                Tampa = settings.TravessiaTampa,
                TampaEspessura = settings.TravessiaTampaEspessura,
                Centralizar = settings.TravessiaCentralizar,
                Superficie = settings.TravessiaSuperficie,
                Cobertura = settings.TravessiaCobertura,
                GrelhaEspessura = settings.TravessiaGrelhaEspessura,
                Vigas = settings.TravessiaVigas,
                VigaLargura = settings.TravessiaVigaLargura,
                VigaAltura = settings.TravessiaVigaAltura,
                VigaEnterrada = settings.TravessiaVigaEnterrada,
                VigaRecuo = settings.TravessiaVigaRecuo
            };
            if (!AskParams(ed, db, a, b, p))
                return;

            settings.TravessiaSuperficie = p.Superficie;
            settings.TravessiaTipo = (int)p.Tipo;
            settings.TravessiaAltura = p.Altura;
            settings.TravessiaPlataforma = p.Plataforma;
            settings.TravessiaRampa = p.Rampa;
            settings.TravessiaTampa = p.Tampa;
            settings.TravessiaTampaEspessura = p.TampaEspessura;
            settings.TravessiaCentralizar = p.Centralizar;
            settings.TravessiaCobertura = p.Cobertura;
            settings.TravessiaGrelhaEspessura = p.GrelhaEspessura;
            settings.TravessiaVigas = p.Vigas;
            settings.TravessiaVigaLargura = p.VigaLargura;
            settings.TravessiaVigaAltura = p.VigaAltura;
            settings.TravessiaVigaEnterrada = p.VigaEnterrada;
            settings.TravessiaVigaRecuo = p.VigaRecuo;
            MFRebaixoSettingsStore.Save(settings);

            // Eixo: no meio dos dois rebaixos; estaca dele na baseline de cada um.
            Point3d ca = Center(a), cb = Center(b);
            Point3d mid = new((ca.X + cb.X) / 2.0, (ca.Y + cb.Y) / 2.0, 0);
            double stA = StationOrMiddle(db, a, mid);
            double stB = StationOrMiddle(db, b, mid);

            var (side1, side2, geo, roadInfo) = Compute(db, a, b, stA, stB, p);

            // Mudancas nos rebaixos.
            var plan = new List<(RebaixoInfo R, double S1, double S2, double H, string Text, double St, double Desired)>();
            foreach ((RebaixoInfo r, double st, double target0, double desired) in new (RebaixoInfo, double, double, double)[]
            {
                (a, stA, geo.TargetExposedA, MFTravessia.At(side1.Samples, stA).Edge.Z + geo.HeightA),
                (b, stB, geo.TargetExposedB, MFTravessia.At(side2.Samples, stB).Edge.Z + geo.HeightB)
            })
            {
                double target = target0;
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
                    // Sem recusa (pedido do Heyvid, 11/11/2026): so avisa e segue.
                    double normal = r.NormalHeight ?? double.MaxValue;
                    if (target < 0.0)
                    {
                        geo.Notes.Add($"AVISO {r.Label}: o plato fica {MFTravessia.Cm(-target)} abaixo do pe do meio-fio; o meio-fio vai para altura final 0.");
                        target = 0.0;
                    }
                    else if (target > normal + 0.005)
                    {
                        geo.Notes.Add($"AVISO {r.Label}: o plato ({MFTravessia.Cm(target)} acima do pe do meio-fio) passa da altura normal do meio-fio ({MFTravessia.Cm(normal)}): o meio-fio sobe no trecho.");
                    }
                    if (r.FinalHeight is not double cur || Math.Abs(cur - target) > 0.005)
                        changes.Add($"altura final {(r.FinalHeight is double c ? MFTravessia.Cm(c) : "?")} -> {MFTravessia.Cm(target)}");
                    h = target;
                }
                if (changes.Count > 0)
                    plan.Add((r, s1, s2, h, $"{r.Label}: " + string.Join(", ", changes), st, desired));
            }

            // Confirmacao (regra do Heyvid: acao que mexe em varias pecas explica o que muda e como desfazer).
            ed.WriteMessage($"\n\nTravessia {p.TipoTexto} entre {a.Label} e {b.Label}:");
            ed.WriteMessage($"\n  plataforma {MFTravessia.N(p.Plataforma)} m + rampas de {MFTravessia.N(p.Rampa)} m, altura {MFTravessia.Cm((geo.HeightA + geo.HeightB) / 2.0)} acima da borda da pista, {MFTravessia.N(geo.WidthAtAxis)} m de borda a borda");
            ed.WriteMessage($"\n  {geo.RoadSource}");
            ed.WriteMessage($"\n  sarjeta: {p.CoberturaTexto}{(p.Tampa ? ", nos dois lados" : "")}");
            if (p.Vigas)
                ed.WriteMessage($"\n  vigas baldrame {MFTravessia.Cm(p.VigaLargura)} x {MFTravessia.Cm(p.VigaAltura)} {p.VigaPosicaoTexto}, nas duas pontas, de borda a borda da pista");
            foreach (string note in geo.Notes)
                ed.WriteMessage("\n  " + note);
            if (plan.Count == 0)
                ed.WriteMessage("\n  Rebaixos: nada muda.");
            foreach (var item in plan)
                ed.WriteMessage("\n  Muda no corredor: " + item.Text);
            if (p.Tipo == TravessiaTipo.Elevada)
                ed.WriteMessage("\n  Elevada: o plato fica no nivel da calcada e os rebaixos nao sao mexidos (podem ser removidos no painel).");
            ed.WriteMessage($"\n  Desenha: solido do plato (layer {settings.TravessiaLayer}-3D), grelha/tampa (-GRELHA / -TAMPA), vigas (-VIGA), hachura, faixa e triangulos ({settings.TravessiaLayer}, -SINALIZACAO).");
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

            // 1b) O REB e um so no trecho e o meio-fio muda de altura ao longo dele (R01 da Waldir Soeiro:
            // pedia 6,5 cm e o topo ficou 2,3 cm abaixo do plato). Confere o topo do meio-fio no eixo e,
            // se nao bateu com o plato, reaplica uma vez compensando a diferenca.
            if (p.Tipo == TravessiaTipo.SemiElevada && plan.Count > 0)
            {
                List<RebaixoInfo> now = MFRebaixoData.ReadAll(db);
                foreach (var item in plan)
                {
                    RebaixoInfo? r = RebaixoAt(now, item.R.CorridorId, item.R.BaselineKey, item.R.Left, item.St);
                    if (r is null)
                        continue;
                    TravessiaSide s = MFTravessia.ReadSide(db, r, item.St, item.St - 2.0, item.St + 2.0);
                    if (s.Samples.Count < 2 || MFTravessia.At(s.Samples, item.St).Top is not double top)
                        continue;
                    double diff = item.Desired - top;
                    if (Math.Abs(diff) <= 0.005)
                        continue;
                    double h2 = item.H + diff;
                    string msg = $"{r.Label}: topo do meio-fio no eixo {Num(top)}, plato {Num(item.Desired)}; altura final {MFTravessia.Cm(item.H)} -> {MFTravessia.Cm(h2)}";
                    try
                    {
                        if (h2 <= 0.0)
                            throw new InvalidOperationException("a altura corrigida fica abaixo de zero");
                        Reapply(db, ed, r, item.S1, item.S2, h2);
                        PluginLog.Info(Mod, "Correcao: " + msg);
                        ed.WriteMessage("\n  Corrigido: " + msg);
                    }
                    catch (System.Exception ex)
                    {
                        PluginLog.Aviso(Mod, $"Correcao nao feita ({msg}): {ex.Message}");
                        ed.WriteMessage($"\n  AVISO: o topo do meio-fio de {r.Label} ficou {MFTravessia.Cm(-diff)} do nivel do plato; correcao nao feita: {ex.Message}");
                    }
                }
            }

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
            int count;
            List<string> notes;
            try
            {
                (count, notes) = MFTravessia.Draw(db, geo, data, settings);
            }
            catch (System.Exception ex) when (plan.Count > 0)
            {
                // Os rebaixos ja foram mexidos: dizer isso, senao parece que nada mudou.
                throw new InvalidOperationException($"{ex.Message} Os rebaixos ja tinham sido reaplicados ({string.Join("; ", plan.Select(x => x.R.Label))}): use U para voltar como estavam.", ex);
            }
            string fim = $"Travessia {data.Id} ({p.TipoTexto}, {a.Label}-{b.Label}): {count} objeto(s) desenhado(s); {string.Join("; ", notes)}.";
            string gravado = MFTravessia.Summary(db, data.Id);
            ed.WriteMessage("\n" + fim + "\n" + gravado + "\n");
            PluginLog.Info(Mod, fim + " " + roadInfo + " " + gravado);
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
                    RebaixoInfo? ra = RebaixoAt(rebaixos, ca, d.BaselineA, d.LeftA, stA);
                    RebaixoInfo? rb = RebaixoAt(rebaixos, cb, d.BaselineB, d.LeftB, stB);
                    Func<double, double, double?> road = MFTravessia.RoadSurface(db, new[] { ca, cb }, d.Params.Superficie, out string roadInfo);
                    TravessiaGeometry geo = MFTravessia.Build(s1, s2, d.Params, road, ra?.NormalHeight, rb?.NormalHeight);
                    (int count, List<string> notes) = MFTravessia.Draw(db, geo, d, settings);
                    ok++;
                    string line = $"Travessia {d.Id} ({d.LabelA}-{d.LabelB}) refeita: {count} objeto(s). {roadInfo} {MFTravessia.Summary(db, d.Id)}";
                    if (d.Params.Tipo == TravessiaTipo.SemiElevada)
                    {
                        // Mesma conta do MFTRAVESSIA: topo do meio-fio no eixo x nivel do plato na borda da pista.
                        foreach ((RebaixoInfo? r, TravessiaSide s, double st, double desired, string label) in new (RebaixoInfo?, TravessiaSide, double, double, string)[]
                        {
                            (ra, s1, stA, MFTravessia.At(s1.Samples, stA).Edge.Z + geo.HeightA, d.LabelA),
                            (rb, s2, stB, MFTravessia.At(s2.Samples, stB).Edge.Z + geo.HeightB, d.LabelB)
                        })
                        {
                            if (r is null)
                                line += $" AVISO: nao ha rebaixo no eixo em {label}.";
                            else if (MFTravessia.At(s.Samples, st).Top is double top && Math.Abs(top - desired) > 0.01)
                                line += $" AVISO: o topo do meio-fio de {r.Label} esta {MFTravessia.Cm(top - desired)} do nivel do plato (rode MFTRAVESSIA de novo para acertar).";
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
            // Unioes de cruzamento (MFTRAVESSIAUNIR) das travessias refeitas.
            MFTravessia.RedoUnions(db, ed, all.Select(x => x.Data.UnionId).Where(u => u.Length > 0).Distinct().ToList());
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
        Func<double, double, double?> road = MFTravessia.RoadSurface(db, new[] { a.CorridorId, b.CorridorId }, p.Superficie, out string roadInfo);
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

    private static bool AskParams(Editor ed, AcadDb.Database db, RebaixoInfo a, RebaixoInfo b, TravessiaParams p)
    {
        while (true)
        {
            string alt = p.Tipo == TravessiaTipo.Elevada ? "altura da calcada" : "altura " + MFTravessia.Cm(p.Altura);
            string msg = $"\nTravessia {a.Label}-{b.Label}: {p.TipoTexto}, {alt}, plataforma {MFTravessia.N(p.Plataforma)} m, rampas {MFTravessia.N(p.Rampa)} m, " +
                         $"sarjeta {p.CoberturaTexto}, vigas {(p.Vigas ? $"{MFTravessia.Cm(p.VigaLargura)} x {MFTravessia.Cm(p.VigaAltura)} {p.VigaPosicaoTexto}" : "nao")}, centralizar rebaixos {(p.Centralizar ? "sim" : "nao")}, " +
                         $"superficie {(p.Superficie.Length > 0 ? p.Superficie : "automatica")}." +
                         "\nMudar algo ou Enter para seguir [Tipo/Altura/Plataforma/Rampa/cObertura/Vigas/Centralizar/sUperficie/Seguir]";
            var opt = new PromptKeywordOptions(msg, "Tipo Altura Plataforma Rampa cObertura Vigas Centralizar sUperficie Seguir") { AllowNone = true };
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
                    else if (AskDouble(ed, "\nAltura do plato acima da borda da pista (m)", p.Altura, 0.005, 2.0) is double h)
                        p.Altura = h;
                    break;
                case "Plataforma":
                    if (AskDouble(ed, "\nLargura da plataforma, no sentido dos carros (m; o detalhe pede > 4,0)", p.Plataforma, 0.10, 100.0) is double w)
                        p.Plataforma = w;
                    break;
                case "Rampa":
                    if (AskDouble(ed, "\nComprimento de cada rampa dos carros (m)", p.Rampa, 0.01, 50.0) is double rl)
                        p.Rampa = rl;
                    break;
                case "cObertura":
                {
                    var t = new PromptKeywordOptions("\nCobertura da sarjeta na plataforma [Grelha/Concreto/Nenhuma]", "Grelha Concreto Nenhuma") { AllowNone = true };
                    t.Keywords.Default = !p.Tampa ? "Nenhuma" : p.Cobertura == 1 ? "Concreto" : "Grelha";
                    PromptResult tr = ed.GetKeywords(t);
                    if (tr.Status == PromptStatus.OK)
                    {
                        p.Tampa = tr.StringResult != "Nenhuma";
                        p.Cobertura = tr.StringResult == "Concreto" ? 1 : 0;
                    }
                    if (p.Grelha && AskDouble(ed, "\nEspessura da grelha metalica (m)", p.GrelhaEspessura, 0.005, 0.30) is double eg)
                        p.GrelhaEspessura = eg;
                    else if (p.Tampa && p.Cobertura == 1 && AskDouble(ed, "\nEspessura da tampa de concreto (m)", p.TampaEspessura, 0.01, 0.50) is double e)
                        p.TampaEspessura = e;
                    break;
                }
                case "Vigas":
                {
                    var t = new PromptKeywordOptions("\nVigas baldrame nas duas pontas da lombofaixa [Sim/Nao]", "Sim Nao") { AllowNone = true };
                    t.Keywords.Default = p.Vigas ? "Sim" : "Nao";
                    PromptResult tr = ed.GetKeywords(t);
                    if (tr.Status == PromptStatus.OK)
                        p.Vigas = tr.StringResult == "Sim";
                    if (!p.Vigas)
                        break;
                    if (AskDouble(ed, "\nLargura da viga (m)", p.VigaLargura, 0.05, 2.0) is double vl)
                        p.VigaLargura = vl;
                    if (AskDouble(ed, "\nAltura da viga (m)", p.VigaAltura, 0.05, 3.0) is double va)
                        p.VigaAltura = va;
                    var pos = new PromptKeywordOptions("\nPosicao da viga [Plataforma (comeco da plataforma)/Pe (pe da rampa)/Medida (recuo em m a partir do pe)]", "Plataforma Pe Medida") { AllowNone = true };
                    pos.Keywords.Default = p.VigaNaPlataforma ? "Plataforma" : p.VigaRecuo < 0.005 ? "Pe" : "Medida";
                    PromptResult pr = ed.GetKeywords(pos);
                    if (pr.Status == PromptStatus.OK)
                    {
                        if (pr.StringResult == "Plataforma")
                            p.VigaRecuo = -1;
                        else if (pr.StringResult == "Pe")
                            p.VigaRecuo = 0;
                        else if (AskDouble(ed, "\nRecuo da viga a partir do pe da rampa (m)", Math.Max(0, p.VigaRecuo), 0.0, 50.0) is double vr)
                            p.VigaRecuo = vr;
                    }
                    break;
                }
                case "sUperficie":
                    if (AskSurface(ed, db, p.Superficie) is string sn)
                        p.Superficie = sn;
                    break;
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

    /// <summary>
    /// Lista as superficies do desenho e pede o numero da de topo. Devolve o nome ("" = automatica,
    /// a de topo do corredor) ou null se cancelado.
    /// </summary>
    internal static string? AskSurface(Editor ed, AcadDb.Database db, string current)
    {
        List<(AcadDb.ObjectId Id, string Name)> list = MFTravessia.Surfaces(db);
        ed.WriteMessage("\nSuperficie de topo do pavimento (base do plato):");
        ed.WriteMessage("\n   0  automatica: a superficie de topo do corredor dos rebaixos");
        for (int i = 0; i < list.Count; i++)
            ed.WriteMessage($"\n  {i + 1,2}  {list[i].Name}");
        int def = list.FindIndex(x => x.Name.Equals(current, StringComparison.OrdinalIgnoreCase)) + 1;
        if (def == 0)
            def = list.FindIndex(x => x.Name.Contains("TOP", StringComparison.OrdinalIgnoreCase)) + 1;
        var o = new PromptIntegerOptions("\nNumero da superficie de topo")
        {
            AllowNone = true,
            LowerLimit = 0,
            UpperLimit = list.Count,
            DefaultValue = def,
            UseDefaultValue = true
        };
        PromptIntegerResult r = ed.GetInteger(o);
        if (r.Status == PromptStatus.Cancel)
            return null;
        int n = r.Status == PromptStatus.OK ? r.Value : def;
        return n <= 0 || n > list.Count ? "" : list[n - 1].Name;
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

    // Pelo lado tambem: os dois lados da rua costumam estar na mesma baseline (MFSARJETA_ESQ/DIR).
    private static RebaixoInfo? RebaixoAt(List<RebaixoInfo> items, AcadDb.ObjectId corridorId, string baselineKey, bool left, double station) =>
        items.Where(r => r.CorridorId == corridorId && r.BaselineKey == baselineKey && r.Left == left && station >= r.S0 - 0.5 && station <= r.S3 + 0.5)
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
