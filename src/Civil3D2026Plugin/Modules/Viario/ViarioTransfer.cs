using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// C3DVIARIOCOPIAR: leva a rede viaria (ou parte dela) para outro desenho aberto.
///
/// Copiar e colar so leva os eixos (polilinhas com XData); o que fica guardado no desenho,
/// fora deles, nao vai: secoes, raios de esquina e faixas livres (gravados pelo handle dos
/// eixos, que muda ao colar), ilhas eliminadas, raios de ilha, linhas soltas e configuracao.
/// Este comando clona os eixos no mesmo lugar e traduz tudo isso para o destino:
/// - secoes: a mesma ja existindo la fica; codigo igual com medidas diferentes ganha codigo
///   novo e os eixos copiados passam a apontar para ele;
/// - raios de esquina e faixas livres: chaves reescritas com os handles novos;
/// - ilhas eliminadas, raios de ilha e linhas soltas: por posicao (copiados como estao);
/// - configuracao e atualizacao automatica: so se o destino ainda nao tem rede viaria.
/// Linhas geradas, hachura, sinalizacao, cotas e objetos do Civil nao sao copiados: a rede e
/// refeita no destino (na hora, com a atualizacao automatica; senao fica pendente).
/// </summary>
internal static class ViarioTransfer
{
    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database src = doc.Database;

        // ---------- Destino ----------
        List<Document> others = AcApp.DocumentManager.Cast<Document>().Where(d => !ReferenceEquals(d, doc) && !d.IsDisposed).ToList();
        if (others.Count == 0)
        {
            ed.WriteMessage("\n  Abra o desenho de destino nesta sessao do Civil 3D e rode o comando de novo (o destino tem que estar aberto).");
            return;
        }

        ed.WriteMessage("\n  Desenhos abertos: " + string.Join(", ", others.Select((d, i) => $"[{i + 1}] {Path.GetFileName(d.Name)}")));
        Document dest;
        if (others.Count == 1)
        {
            dest = others[0];
        }
        else
        {
            PromptIntegerOptions io = new("\nDesenho de destino (numero): ") { LowerLimit = 1, UpperLimit = others.Count, AllowNone = false };
            PromptIntegerResult ir = ed.GetInteger(io);
            if (ir.Status != PromptStatus.OK)
                return;
            dest = others[ir.Value - 1];
        }

        if (dest.IsReadOnly)
        {
            ed.WriteMessage($"\n  {Path.GetFileName(dest.Name)} esta aberto somente para leitura.");
            return;
        }

        // ---------- Eixos ----------
        PromptKeywordOptions which = new("\nCopiar quais eixos [Todos/Selecionar] <Selecionar>: ", "Todos Selecionar") { AllowNone = true };
        PromptResult w = ed.GetKeywords(which);
        if (w.Status == PromptStatus.Cancel || w.Status == PromptStatus.Error)
            return;
        bool all = w.Status == PromptStatus.OK && w.StringResult == "Todos";

        AcadDb.ObjectIdCollection ids = new();
        Dictionary<AcadDb.ObjectId, string> sectionOf = new();
        using (AcadDb.Transaction tr = src.TransactionManager.StartOpenCloseTransaction())
        {
            IEnumerable<AcadDb.ObjectId> candidates;
            if (all)
            {
                AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(src.BlockTableId, AcadDb.OpenMode.ForRead);
                AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
                candidates = ms.Cast<AcadDb.ObjectId>().ToList();
            }
            else
            {
                AcadDb.TypedValue[] filter = { new((int)AcadDb.DxfCode.Start, "LWPOLYLINE,LINE,ARC,CIRCLE") };
                PromptSelectionResult sr = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelecione os eixos do viario a copiar: " }, new SelectionFilter(filter));
                if (sr.Status != PromptStatus.OK)
                    return;
                candidates = sr.Value.GetObjectIds();
            }

            foreach (AcadDb.ObjectId id in candidates)
            {
                if (id.IsErased)
                    continue;
                string dxf = id.ObjectClass.DxfName;
                if (dxf != "LWPOLYLINE" && dxf != "LINE" && dxf != "ARC" && dxf != "CIRCLE")
                    continue;
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.Entity ent || !ViarioStore.ReadAxis(ent, out string sectionId, out _))
                    continue;
                ids.Add(id);
                sectionOf[id] = sectionId;
            }
        }

        if (ids.Count == 0)
        {
            ed.WriteMessage("\n  Nenhum eixo do viario " + (all ? "neste desenho." : "na selecao."));
            return;
        }

        // Eixo que ja existe no destino com a mesma geometria nao e copiado de novo
        // (copiar duas vezes para o mesmo desenho duplicava os eixos).
        int skippedExisting = 0;
        {
            HashSet<string> existing = new(StringComparer.Ordinal);
            using (AcadDb.Transaction tr = dest.Database.TransactionManager.StartOpenCloseTransaction())
            {
                AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(dest.Database.BlockTableId, AcadDb.OpenMode.ForRead);
                AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForRead);
                foreach (AcadDb.ObjectId id in ms)
                {
                    string dxf = id.ObjectClass.DxfName;
                    if (dxf != "LWPOLYLINE" && dxf != "LINE" && dxf != "ARC" && dxf != "CIRCLE")
                        continue;
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Curve axis && ViarioStore.ReadAxis(axis, out _, out _) &&
                        AxisSignature(axis) is string sig)
                        existing.Add(sig);
                }
            }

            if (existing.Count > 0)
            {
                using AcadDb.Transaction tr = src.TransactionManager.StartOpenCloseTransaction();
                AcadDb.ObjectIdCollection kept = new();
                foreach (AcadDb.ObjectId id in ids)
                {
                    if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Curve axis && AxisSignature(axis) is string sig && existing.Contains(sig))
                    {
                        skippedExisting++;
                        sectionOf.Remove(id);
                        continue;
                    }
                    kept.Add(id);
                }
                ids = kept;
            }

            if (skippedExisting > 0)
                ed.WriteMessage($"\n  {skippedExisting} eixo(s) ja existem em \"{Path.GetFileName(dest.Name)}\" no mesmo lugar: nao serao copiados de novo.");
            if (ids.Count == 0)
            {
                ed.WriteMessage("\n  Nada a copiar: todos os eixos escolhidos ja estao la.");
                return;
            }
        }

        // Confirmacao: mexe em outro desenho (desfazer la com U).
        PromptKeywordOptions confirm = new(
            $"\nCopiar {ids.Count} eixo(s) para \"{Path.GetFileName(dest.Name)}\" (no mesmo lugar), com secoes, raios e faixas livres? [Sim/Nao] <Sim>: ", "Sim Nao")
        {
            AllowNone = true
        };
        PromptResult c = ed.GetKeywords(confirm);
        if (c.Status == PromptStatus.Cancel || c.Status == PromptStatus.Error || (c.Status == PromptStatus.OK && c.StringResult == "Nao"))
            return;

        // ---------- Copia ----------
        List<string> report = new();
        AcadDb.Database dst = dest.Database;
        Dictionary<string, string> handleMap = new(StringComparer.Ordinal);
        bool destHadNetwork = ViarioStore.RecordsSignature(dst).Length > 0;
        bool destAuto;
        bool regenerated = false;

        using (dest.LockDocument())
        {
            AcadDb.ObjectId destMs;
            using (AcadDb.Transaction tr = dst.TransactionManager.StartOpenCloseTransaction())
            {
                AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(dst.BlockTableId, AcadDb.OpenMode.ForRead);
                destMs = bt[AcadDb.BlockTableRecord.ModelSpace];
            }

            AcadDb.IdMapping map = new();
            src.WblockCloneObjects(ids, destMs, map, AcadDb.DuplicateRecordCloning.Ignore, false);

            Dictionary<AcadDb.ObjectId, AcadDb.ObjectId> cloned = new();
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.IdPair pair = map[id];
                if (pair.IsCloned && !pair.Value.IsNull)
                {
                    cloned[id] = pair.Value;
                    handleMap[id.Handle.ToString()] = pair.Value.Handle.ToString();
                }
            }

            using (AcadDb.Transaction tr = dst.TransactionManager.StartTransaction())
            {
                ViarioStore.EnsureRegApp(tr, dst);
                ViarioStore.EnsureRegApp(tr, dst, ViarioStore.RegAppName);
                ViarioStore.EnsureLayers(tr, dst);

                // Secoes: mesma la fica; mesmo codigo com medidas diferentes ganha codigo novo.
                Dictionary<string, RoadSection> srcSections = ViarioStore.LoadSections(src).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
                List<RoadSection> destSections = destHadNetwork ? ViarioStore.LoadSections(dst) : new List<RoadSection>();
                Dictionary<string, string> sectionMap = new(StringComparer.Ordinal);
                int sectionsAdded = 0, sectionsRenamed = 0;
                foreach (string sid in sectionOf.Values.Distinct())
                {
                    if (!srcSections.TryGetValue(sid, out RoadSection? s))
                    {
                        sectionMap[sid] = sid; // secao que nem a origem tem: segue a regra de la (primeira secao)
                        continue;
                    }

                    RoadSection? same = destSections.FirstOrDefault(d => Same(d, s));
                    if (same is not null)
                    {
                        sectionMap[sid] = same.Id;
                        continue;
                    }

                    RoadSection copy = s.Clone();
                    if (destSections.Any(d => d.Id == sid))
                    {
                        copy.Id = ViarioStore.NextSectionId(destSections);
                        copy.Name = s.Name + " (copiada)";
                        sectionsRenamed++;
                        report.Add($"Secao {sid} \"{s.Name}\" ja existia no destino com outras medidas: copiada como {copy.Id}.");
                    }

                    ViarioStore.SaveSection(tr, dst, copy);
                    destSections.Add(copy);
                    sectionMap[sid] = copy.Id;
                    sectionsAdded++;
                }

                int relinked = 0;
                foreach ((AcadDb.ObjectId srcId, AcadDb.ObjectId dstId) in cloned)
                {
                    string sid = sectionOf[srcId];
                    if (!sectionMap.TryGetValue(sid, out string? target) || target == sid)
                        continue;
                    if (tr.GetObject(dstId, AcadDb.OpenMode.ForWrite, false) is AcadDb.Entity ent)
                    {
                        ViarioStore.UpdateAxis(ent, sectionId: target);
                        relinked++;
                    }
                }

                // Raios de esquina e faixas livres: chaves pelos handles novos, somadas as do destino.
                Dictionary<string, double> radii = destHadNetwork ? ViarioStore.LoadCornerRadii(dst) : new Dictionary<string, double>();
                int radiiCopied = 0;
                foreach ((string key, double r) in ViarioStore.LoadCornerRadii(src))
                {
                    if (RemapCornerKey(key, handleMap) is string nk)
                    {
                        radii[nk] = r;
                        radiiCopied++;
                    }
                }

                // Chaves de eixos que nao existem mais no destino (copia desfeita, eixo apagado) saem.
                int orphanRadii = radii.Keys.Where(k => !KeyAlive(dst, k)).ToList().Count(k => radii.Remove(k));
                if (radiiCopied > 0 || orphanRadii > 0)
                    ViarioStore.SaveCornerRadii(tr, dst, radii);

                Dictionary<string, SlipLane> slips = destHadNetwork ? ViarioStore.LoadSlipLanes(dst) : new Dictionary<string, SlipLane>();
                int slipsCopied = 0;
                foreach ((string key, SlipLane slip) in ViarioStore.LoadSlipLanes(src))
                {
                    if (RemapCornerKey(key, handleMap) is string nk)
                    {
                        slips[nk] = slip;
                        slipsCopied++;
                    }
                }

                int orphanSlips = slips.Keys.Where(k => !KeyAlive(dst, k)).ToList().Count(k => slips.Remove(k));
                if (slipsCopied > 0 || orphanSlips > 0)
                    ViarioStore.SaveSlipLanes(tr, dst, slips);

                // Por posicao: ilhas eliminadas, raios de ilha, linhas soltas (copiadas no mesmo lugar).
                List<V2> removed = ViarioStore.LoadRemovedIslands(src);
                if (removed.Count > 0)
                {
                    List<V2> merged = destHadNetwork ? ViarioStore.LoadRemovedIslands(dst) : new List<V2>();
                    merged.AddRange(removed.Where(p => !merged.Any(q => V2.Dist(p, q) < 1e-6)));
                    ViarioStore.SaveRemovedIslands(tr, dst, merged);
                }

                List<(V2 Seed, double Radius)> islandRadii = ViarioStore.LoadIslandRadii(src);
                if (islandRadii.Count > 0)
                {
                    List<(V2 Seed, double Radius)> merged = destHadNetwork ? ViarioStore.LoadIslandRadii(dst) : new List<(V2, double)>();
                    merged.AddRange(islandRadii.Where(p => !merged.Any(q => V2.Dist(p.Seed, q.Seed) < 1e-6)));
                    ViarioStore.SaveIslandRadii(tr, dst, merged);
                }

                HashSet<string> released = ViarioStore.LoadReleased(src);
                if (released.Count > 0)
                {
                    HashSet<string> merged = destHadNetwork ? ViarioStore.LoadReleased(dst) : new HashSet<string>(StringComparer.Ordinal);
                    merged.UnionWith(released);
                    ViarioStore.SaveReleased(tr, dst, merged);
                }

                // Configuracao e atualizacao automatica: so num destino sem rede (nao mexe no que ja esta la).
                if (!destHadNetwork)
                {
                    ViarioStore.SaveSettings(tr, dst, ViarioStore.LoadSettings(src));
                    ViarioStore.SaveAutoUpdate(tr, dst, ViarioStore.LoadAutoUpdate(src));
                }

                tr.Commit();

                report.Insert(0, $"{cloned.Count} eixo(s) copiados para \"{Path.GetFileName(dest.Name)}\" (mesmas coordenadas); " +
                                 $"secoes: {sectionsAdded} nova(s) ({sectionsRenamed} com codigo novo), {relinked} eixo(s) reapontados; " +
                                 $"raios de esquina {radiiCopied}, faixas livres {slipsCopied}, ilhas eliminadas {removed.Count}, raios de ilha {islandRadii.Count}, linhas soltas {released.Count}.");
            }

            // A rede e refeita no destino. Os eventos do banco nao servem aqui: no ObjectAppended
            // do clone a XData ainda nao e legivel, entao os eixos novos sao registrados a mao.
            ViarioSync.NotifyImported(dst, cloned.Values);
            destAuto = ViarioSync.IsAuto(dst);
            if (destAuto)
            {
                try
                {
                    ViarioResult? r = ViarioSync.Regenerate(dest, quiet: true, force: true);
                    regenerated = r is not null;
                }
                catch (System.Exception ex)
                {
                    Civil3D2026Plugin.Infrastructure.PluginLog.Aviso("Viario", "Copiar: rede nao refeita no destino: " + ex.Message);
                }
            }
        }

        foreach (string line in report)
            ed.WriteMessage("\n  " + line);
        foreach (string line in report)
            Civil3D2026Plugin.Infrastructure.PluginLog.Info("Viario", "Copiar: " + line);

        ed.WriteMessage(regenerated
            ? $"\n  Rede refeita em \"{Path.GetFileName(dest.Name)}\" (bordos, meio-fio e esquinas)."
            : destAuto
            ? $"\n  Ao mudar para \"{Path.GetFileName(dest.Name)}\" a rede e refeita sozinha (atualizacao automatica); se nao, use \"Atualizar rede\" (C3DVIARIO > ATUalizar) la."
            : $"\n  Em \"{Path.GetFileName(dest.Name)}\" a atualizacao automatica esta desligada: use \"Atualizar rede\" (C3DVIARIO > ATUalizar) la.");
        ed.WriteMessage("\n  Nao copiados (gere la, se quiser): hachura, sinalizacao, cotas, placas e objetos do Civil. Para desfazer no destino: U naquele desenho.");
    }

    /// <summary>Os dois eixos da chave de esquina "A+|B-" ainda existem no desenho.</summary>
    private static bool KeyAlive(AcadDb.Database db, string key)
    {
        string[] parts = key.Split('|');
        if (parts.Length != 2)
            return false;
        foreach (string p in parts)
        {
            if (p.Length < 2)
                return false;
            string hex = p.Substring(0, p.Length - 1);
            if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h))
                return false;
            if (!db.TryGetObjectId(new AcadDb.Handle(h), out AcadDb.ObjectId id) || id.IsNull || id.IsErased)
                return false;
        }

        return true;
    }

    /// <summary>Assinatura da geometria do eixo (vertices e bulges arredondados ao mm), para reconhecer o mesmo eixo em outro desenho.</summary>
    private static string? AxisSignature(AcadDb.Curve curve)
    {
        GPath? path = ViarioStore.ToPath(curve);
        if (path is null || path.IsEmpty)
            return null;
        System.Text.StringBuilder sb = new();
        foreach (Seg seg in path.Segs)
        {
            sb.Append(Math.Round(seg.A.X, 3).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Round(seg.A.Y, 3).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Round(seg.Bulge, 4).ToString("0.####", CultureInfo.InvariantCulture)).Append(';');
        }
        sb.Append(Math.Round(path.End.X, 3).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
          .Append(Math.Round(path.End.Y, 3).ToString("0.###", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Chave de esquina "A+|B-" (handles e sentidos) reescrita com os handles do destino; null se um dos eixos nao foi copiado.</summary>
    private static string? RemapCornerKey(string key, IReadOnlyDictionary<string, string> handleMap)
    {
        string[] parts = key.Split('|');
        if (parts.Length != 2)
            return null;
        string[] mapped = new string[2];
        for (int i = 0; i < 2; i++)
        {
            string p = parts[i];
            if (p.Length < 2)
                return null;
            char dir = p[^1];
            if (dir != '+' && dir != '-')
                return null;
            if (!handleMap.TryGetValue(p[..^1], out string? nh))
                return null;
            mapped[i] = nh + dir;
        }

        return mapped[0] + "|" + mapped[1];
    }

    /// <summary>Mesma secao (medidas, lados, tipo, faixas e velocidade), independentemente do codigo e do nome.</summary>
    private static bool Same(RoadSection a, RoadSection b)
    {
        static bool Eq(double x, double y) => Math.Abs(x - y) < 1e-6;
        return Eq(a.Pista, b.Pista) && Eq(a.Canteiro, b.Canteiro) && Eq(a.Sarjeta, b.Sarjeta) && Eq(a.MeioFio, b.MeioFio) &&
               Eq(a.Calcada, b.Calcada) && Eq(a.Raio, b.Raio) && Eq(a.Velocidade, b.Velocidade) && a.Faixas == b.Faixas &&
               string.Equals(a.Tipo, b.Tipo, StringComparison.OrdinalIgnoreCase) &&
               a.LadosSarjeta == b.LadosSarjeta && a.LadosMeioFio == b.LadosMeioFio && a.LadosCalcada == b.LadosCalcada;
    }
}
