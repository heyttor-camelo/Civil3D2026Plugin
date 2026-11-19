using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

// Registro tambem aqui: a DLL avulsa C3DPassagemRampa compila este arquivo sem o CommandRegistration.
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Ifc.IfcExportCommands))]

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// C3DIFC: exportacao IFC 4.3 com criterios nossos, usando o exportador da Autodesk por baixo
/// (extensao IFC do Civil 3D 2026: IFCInfraExportSelected). O comando:
///  1) le os criterios do projeto (JSON ao lado do DWG, criado do padrao);
///  2) avisa corredores desatualizados (Rebuild so se o usuario pedir);
///  3) gera a planilha de mapeamento ao lado do .ifc ("&lt;ifc&gt;-IfcInfraExportMapping-IFC4X3_ADD2.xlsx",
///     que a extensao le sozinha; a configuracao global dela nao muda);
///  4) seleciona o que entra (corredores, eixos, lombofaixas, layers extras) e chama a exportacao;
///  5) depois (C3DIFCRELATORIO) poe o nome do projeto/local no .ifc, confere o resultado e grava um
///     resumo em portugues ao lado do .ifc.
/// </summary>
public sealed class IfcExportCommands
{
    private sealed class Pending
    {
        public string IfcPath = "";
        public string CriteriaPath = "";
        public IfcCriteria Criteria = new();
        public int Selected;
        public string Selection = "";
        public DateTime Started;
        public object? FileDia;
        public List<ObjectId> Corridors = new();
    }

    private static Pending? _pending;

    /// <summary>C3DIFC: janela com o que exportar (pedido do Heyvid em 17/11/2026).</summary>
    [CommandMethod(C3DCommands.Ifc.Exportar, CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
    public void Exportar()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;
        try
        {
            if (!IfcMappingWriter.ExtensionInstalled)
            {
                ed.WriteMessage("\nA extensao IFC do Civil 3D 2026 (Autodesk Civil 3D IFC) nao esta instalada nesta maquina. Instale pelo Autodesk Access e rode de novo.\n");
                return;
            }
            string dwg = db.Filename;
            if (string.IsNullOrWhiteSpace(dwg) || !Path.IsPathRooted(dwg) || dwg.EndsWith(".dwt", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage("\nSalve o desenho primeiro: os criterios ficam num arquivo ao lado do DWG.\n");
                return;
            }
            (IfcCriteria criteria, string criteriaPath, bool created) = IfcCriteria.LoadFor(dwg, Path.GetFileNameWithoutExtension(dwg));
            if (created)
                ed.WriteMessage($"\nCriterios do projeto criados a partir do padrao: {criteriaPath}");

            // Objetos ja selecionados antes do comando entram como "selecionados a mao".
            var pick = new List<ObjectId>();
            PromptSelectionResult implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value is not null)
                pick.AddRange(implied.Value.GetObjectIds());

            var w = new IfcExportWindow(doc, criteria, criteriaPath, pick);
            if (AcApp.ShowModalWindow(w) != true)
                return;
            criteria = IfcCriteria.Read(criteriaPath);
            if (w.Rebuild.Count > 0)
            {
                using Transaction tr = db.TransactionManager.StartTransaction();
                foreach (ObjectId id in w.Rebuild)
                    ((CivilCorridor)tr.GetObject(id, OpenMode.ForWrite)).Rebuild();
                tr.Commit();
                ed.WriteMessage($"\nRebuild feito em {w.Rebuild.Count} corredor(es).");
            }
            string report = RunExport(doc, criteria, criteriaPath, w.IfcPath, w.Ids, w.Selection);
            if (report.Length > 0)
                System.Windows.MessageBox.Show(report, "Exportar IFC 4.3", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("IFC", "C3DIFC: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    /// <summary>-C3DIFC: o mesmo pela linha de comando, sem janela (scripts, teste no console).</summary>
    [CommandMethod(C3DCommands.Ifc.ExportarLinha, CommandFlags.Modal | CommandFlags.Redraw)]
    public void ExportarLinha()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Database db = doc.Database;
        try
        {
            if (!IfcMappingWriter.ExtensionInstalled)
            {
                ed.WriteMessage("\nA extensao IFC do Civil 3D 2026 (Autodesk Civil 3D IFC) nao esta instalada nesta maquina. Instale pelo Autodesk Access e rode de novo.\n");
                return;
            }
            string dwg = db.Filename;
            if (string.IsNullOrWhiteSpace(dwg) || !Path.IsPathRooted(dwg) || dwg.EndsWith(".dwt", StringComparison.OrdinalIgnoreCase))
            {
                ed.WriteMessage("\nSalve o desenho primeiro: os criterios ficam num arquivo ao lado do DWG.\n");
                return;
            }

            string projectName = Path.GetFileNameWithoutExtension(dwg);
            (IfcCriteria criteria, string criteriaPath, bool created) = IfcCriteria.LoadFor(dwg, projectName);
            if (created)
                ed.WriteMessage($"\nCriterios do projeto criados a partir do padrao: {criteriaPath}");

            // Criterios: so pela janela do C3DIFC (sem arquivo de texto, pedido do Heyvid em 19/11/2026).
            ed.WriteMessage($"\nExportar IFC 4.3 com os critérios gravados pela janela do {C3DCommands.Ifc.Exportar}.");
            criteria = IfcCriteria.Read(criteriaPath);

            // Corredores desatualizados: avisa e pergunta (nunca faz Rebuild sozinho).
            // Corredores de referencia (data shortcut): Nao / Sim / Perguntar.
            string refMode = (criteria.CorredoresReferenciados ?? "Nao").Trim();
            bool includeRefs = refMode.Equals("Sim", StringComparison.OrdinalIgnoreCase);
            if (refMode.Equals("Perguntar", StringComparison.OrdinalIgnoreCase))
            {
                List<string> refs = Corridors(db, criteria, true).Select(c => c.Name).ToList();
                refs = includedReferences.ToList();
                if (refs.Count > 0)
                {
                    var ro = new PromptKeywordOptions($"\nCorredor(es) de referencia (data shortcut): {string.Join(", ", refs)}. Incluir na exportacao [Sim/Nao] <Nao>: ") { AllowNone = true, AppendKeywordsToMessage = false };
                    ro.Keywords.Add("Sim");
                    ro.Keywords.Add("Nao");
                    PromptResult rr = ed.GetKeywords(ro);
                    if (rr.Status == PromptStatus.Cancel)
                        return;
                    includeRefs = rr.Status == PromptStatus.OK && rr.StringResult == "Sim";
                }
            }
            List<(ObjectId Id, string Name, bool OutOfDate)> corridors = Corridors(db, criteria, includeRefs);
            var refNotes = new List<string>();
            if (criteria.Corredores && skippedReferences.Count > 0)
            {
                string n = $"Referências externas não exportadas: {string.Join(", ", skippedReferences)}.";
                refNotes.Add(n);
                ed.WriteMessage("\n" + n);
            }
            if (criteria.Corredores && includedReferences.Count > 0)
                refNotes.Add($"Corredor(es) de referencia incluido(s): {string.Join(", ", includedReferences)}.");
            // Referencia nunca entra no Rebuild: ela se atualiza no desenho de origem (sincronizando o atalho).
            List<string> staleRefs = corridors.Where(c => c.OutOfDate && includedReferences.Contains(c.Name)).Select(c => c.Name).ToList();
            if (staleRefs.Count > 0)
                ed.WriteMessage($"\nCorredor(es) de referencia desatualizado(s): {string.Join(", ", staleRefs)}. Referencia se atualiza no desenho de origem (Rebuild la e sincronize o data shortcut aqui); sai como esta.");
            corridors = corridors.Select(c => includedReferences.Contains(c.Name) ? (c.Id, c.Name, false) : c).ToList();
            List<string> stale = corridors.Where(c => c.OutOfDate).Select(c => c.Name).ToList();
            if (criteria.Corredores && stale.Count > 0)
            {
                var so = new PromptKeywordOptions($"\nCorredor(es) desatualizado(s): {string.Join(", ", stale)}. Desatualizado sai com a geometria antiga (ou nao sai). Fazer Rebuild antes [Rebuild/Seguir/Cancelar] <Rebuild>: ")
                { AllowNone = true, AppendKeywordsToMessage = false };
                so.Keywords.Add("Rebuild");
                so.Keywords.Add("Seguir");
                so.Keywords.Add("Cancelar");
                PromptResult sr = ed.GetKeywords(so);
                if (sr.Status == PromptStatus.Cancel || sr.StringResult == "Cancelar")
                    return;
                if (sr.Status != PromptStatus.OK || sr.StringResult == "Rebuild")
                {
                    using Transaction tr = db.TransactionManager.StartTransaction();
                    foreach ((ObjectId id, string _, bool outOfDate) in corridors.Where(c => c.OutOfDate))
                        ((CivilCorridor)tr.GetObject(id, OpenMode.ForWrite)).Rebuild();
                    tr.Commit();
                    ed.WriteMessage($"\nRebuild feito em {stale.Count} corredor(es).");
                }
            }

            // Arquivo de saida.
            string defaultIfc = criteria.SuggestedIfc(dwg);
            var po = new PromptStringOptions($"\nArquivo IFC <{defaultIfc}>: ") { AllowSpaces = true };
            PromptResult pr = ed.GetString(po);
            if (pr.Status == PromptStatus.Cancel)
                return;
            string ifc = string.IsNullOrWhiteSpace(pr.StringResult) ? defaultIfc : pr.StringResult.Trim().Trim('"');
            if (!ifc.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                ifc += ".ifc";
            if (File.Exists(ifc))
            {
                var oo = new PromptKeywordOptions($"\nJa existe {Path.GetFileName(ifc)}. Substituir [Sim/Nao] <Nao>: ") { AllowNone = true, AppendKeywordsToMessage = false };
                oo.Keywords.Add("Sim");
                oo.Keywords.Add("Nao");
                PromptResult or = ed.GetKeywords(oo);
                if (or.Status != PromptStatus.OK || or.StringResult != "Sim")
                {
                    ed.WriteMessage("\nNada exportado.\n");
                    return;
                }
            }


            // Selecao.
            List<ObjectId> ids = new();
            string axisNote = "";
            var what = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (criteria.Corredores)
                {
                    string mode = criteria.EixosModo;
                    int axesIn = 0, axesOut = 0;
                    var outTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach ((ObjectId id, string name, bool _) in corridors)
                    {
                        ids.Add(id);
                        if (tr.GetObject(id, OpenMode.ForRead) is not CivilCorridor c)
                            continue;
                        foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                        {
                            ObjectId aid;
                            try
                            {
                                aid = b.AlignmentId;
                            }
                            catch
                            {
                                continue; // baseline por feature line: sem alinhamento
                            }
                            if (aid.IsNull || ids.Contains(aid))
                                continue;
                            string type = "?";
                            try
                            {
                                if (tr.GetObject(aid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Alignment al)
                                    type = al.AlignmentType.ToString();
                            }
                            catch
                            {
                                // Tipo nao lido.
                            }
                            bool take = mode == "todos" || (mode == "principais" && type.Equals("Centerline", StringComparison.OrdinalIgnoreCase));
                            if (take)
                            {
                                ids.Add(aid);
                                axesIn++;
                            }
                            else
                            {
                                axesOut++;
                                outTypes[type] = outTypes.TryGetValue(type, out int k) ? k + 1 : 1;
                            }
                        }
                    }
                    what.Add($"{corridors.Count} corredor(es)");
                    what.Add($"{axesIn} eixo(s) ({mode})");
                    axisNote = axesOut == 0
                        ? $"Eixos: {axesIn} entraram ({mode})."
                        : $"Alinhamentos: {axesIn} exportados, {axesOut} não exportados ({string.Join(", ", outTypes.Select(kv => $"{kv.Value} {TypeName(kv.Key)}"))}).";
                    ed.WriteMessage("\n" + axisNote);
                }
                if (criteria.Superficies)
                {
                    int n = 0;
                    foreach (ObjectId sid in Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument.GetSurfaceIds())
                    {
                        ids.Add(sid);
                        n++;
                    }
                    what.Add($"{n} superficie(s)");
                }
                if (criteria.LayersExtras.Count > 0)
                {
                    var layers = new HashSet<string>(criteria.LayersExtras, StringComparer.OrdinalIgnoreCase);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    int n = 0;
                    foreach (ObjectId oid in ms)
                    {
                        if (tr.GetObject(oid, OpenMode.ForRead) is Entity e && layers.Contains(e.Layer) && !ids.Contains(oid))
                        {
                            ids.Add(oid);
                            n++;
                        }
                    }
                    what.Add($"{n} objeto(s) das layers extras");
                }
                tr.Commit();
            }
            if (criteria.Lombofaixas)
            {
                List<ObjectId> travs = MFTravessia.Objects(db, null, string.IsNullOrWhiteSpace(criteria.LombofaixasPecas) ? "Solidos" : criteria.LombofaixasPecas);
                ids.AddRange(travs.Where(x => !ids.Contains(x)));
                what.Add($"{travs.Count} peca(s) de lombofaixa");
            }
            if (ids.Count == 0)
            {
                ed.WriteMessage("\nNada para exportar com esses criterios.\n");
                return;
            }

            criteria.UltimoArquivoIfc = ifc;
            IfcCriteria.LastFolder = Path.GetDirectoryName(ifc) ?? "";
            IfcCriteria.Save(criteria, criteriaPath);
            RunExport(doc, criteria, criteriaPath, ifc, ids,
                string.Join(", ", what) + (axisNote.Length > 0 ? Environment.NewLine + axisNote : "") + (refNotes.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, refNotes) : ""));
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("IFC", "C3DIFC: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    /// <summary>
    /// Parte comum (linha de comando e janela): planilha de mapeamento ao lado do .ifc, selecao pronta,
    /// exportacao da Autodesk (sem janela) e o resumo. Devolve o texto do resumo (vazio se nao exportou).
    /// </summary>
    internal static string RunExport(Autodesk.AutoCAD.ApplicationServices.Document doc, IfcCriteria criteria, string criteriaPath, string ifc, List<ObjectId> ids, string selection)
    {
        Editor ed = doc.Editor;
        // A extensao le sozinha "<nome do ifc>-IfcInfraExportMapping-IFC4X3_ADD2.xlsx" ao lado do .ifc.
        // A planilha e temporaria: apagada depois da exportacao (pedido do Heyvid em 19/11/2026: so o .ifc na pasta).
        string mapping = MappingPath(ifc);
        IfcMappingWriter.Write(mapping, criteria.Mapeamento);
        _pending = new Pending
        {
            IfcPath = ifc,
            CriteriaPath = criteriaPath,
            Criteria = criteria,
            Selected = ids.Count,
            Selection = selection,
            Started = DateTime.Now,
            Corridors = CorridorIdsOf(ids)
        };
        _lastReport = "";
        ed.WriteMessage($"\nExportando {ids.Count} objetos para {ifc}...");
        ed.SetImpliedSelection(ids.Distinct().ToArray());

        // 1) Direto na classe da extensao (aceita o caminho e nao abre janela nenhuma).
        if (TryExportDirect(ifc, out string why))
        {
            new IfcExportCommands().Relatorio();
            return _lastReport;
        }
        PluginLog.Aviso("IFC", "C3DIFC: exportacao direta indisponivel (" + why + "); usando o comando IFCInfraExportSelected com FILEDIA=0.");

        // 2) Pelo comando. Com FILEDIA=1 o comando da Autodesk ignora o caminho e abre o "Salvar como" dele
        //    (era o segundo caminho pedido a cada exportacao); com FILEDIA=0 ele le o caminho na linha de comando.
        object fileDia = AcApp.GetSystemVariable("FILEDIA");
        AcApp.SetSystemVariable("FILEDIA", 0);
        bool queued = false;
        try
        {
            ed.Command("IFCInfraExportSelected", ifc);
            new IfcExportCommands().Relatorio();
        }
        catch (System.Exception ex)
        {
            // Sem chamada direta: a exportacao roda logo depois deste comando, com a selecao pronta.
            PluginLog.Aviso("IFC", "C3DIFC: chamada do comando falhou (" + ex.Message + "); seguindo pela fila de comandos.");
            if (_pending is not null)
                _pending.FileDia = fileDia;
            queued = true;
            doc.SendStringToExecute("IFCInfraExportSelected\n" + Quote(ifc) + "\n" + C3DCommands.Ifc.Relatorio + "\n", true, false, false);
        }
        finally
        {
            if (!queued)
                AcApp.SetSystemVariable("FILEDIA", fileDia);
        }
        return _lastReport;
    }

    /// <summary>Corredores entre os objetos exportados (para a correção das peças pelo sólido).</summary>
    private static List<ObjectId> CorridorIdsOf(IEnumerable<ObjectId> ids)
    {
        RXClass corridor = RXObject.GetClass(typeof(CivilCorridor));
        return ids.Where(id => !id.IsNull && !id.IsErased && id.ObjectClass.IsDerivedFrom(corridor)).Distinct().ToList();
    }

    private static string MappingPath(string ifc) =>
        Path.Combine(Path.GetDirectoryName(ifc)!, Path.GetFileNameWithoutExtension(ifc) + IfcMappingWriter.MappingSuffix);

    /// <summary>Pasta dos arquivos auxiliares (log da Autodesk, resumo): fora da pasta do .ifc.</summary>
    internal static string AuxFolder
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3D2026Plugin", "logs", "ifc");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>
    /// Exportacao pela classe ExportSelectedIfcCommand da extensao (metodo Export(string)), por reflexao:
    /// usa a selecao implicita e grava no caminho dado, sem janela. Falso se a extensao nao expoe isso.
    /// </summary>
    private static bool TryExportDirect(string ifc, out string why)
    {
        why = "";
        try
        {
            System.Reflection.Assembly? asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Civil3D_IFC", StringComparison.OrdinalIgnoreCase));
            if (asm is null)
            {
                string dll = Path.Combine(IfcMappingWriter.BundleFolder, "Contents", "Civil3D_IFC.dll");
                asm = System.Reflection.Assembly.LoadFrom(dll);
            }
            Type? type = asm.GetTypes().FirstOrDefault(t => t.Name == "ExportSelectedIfcCommand");
            if (type is null)
            {
                why = "classe ExportSelectedIfcCommand nao encontrada";
                return false;
            }
            System.Reflection.MethodInfo? export = type.GetMethod("Export", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (export is null)
            {
                why = "metodo Export(string) nao encontrado";
                return false;
            }
            object? cmd = Activator.CreateInstance(type, true);
            export.Invoke(cmd, new object[] { ifc });
            return true;
        }
        catch (System.Exception ex)
        {
            why = (ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex).Message;
            return false;
        }
    }

    private static string _lastReport = "";

    private static string TypeName(string type) => type switch
    {
        "Centerline" => "eixo central",
        "Offset" => "de bordo/offset",
        "CurbReturn" => "de retorno de esquina",
        "Rail" => "de ferrovia",
        "Miscellaneous" => "diversos",
        _ => type
    };

    private static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    private static readonly List<string> skippedReferences = new();
    private static readonly List<string> includedReferences = new();

    /// <summary>Corredor de referencia (data shortcut)? Por reflexao: a propriedade varia entre builds.</summary>
    private static bool IsReference(Autodesk.AutoCAD.DatabaseServices.DBObject o)
    {
        try
        {
            foreach (string name in new[] { "IsReferenceObject", "IsReferenceSubObject", "IsDataReference" })
            {
                if (o.GetType().GetProperty(name)?.GetValue(o) is bool b && b)
                    return true;
            }
        }
        catch
        {
            // Sem a propriedade: trata como do proprio desenho.
        }
        return false;
    }

    private static List<(ObjectId Id, string Name, bool OutOfDate)> Corridors(Database db, IfcCriteria criteria, bool includeReferences)
    {
        var list = new List<(ObjectId, string, bool)>();
        skippedReferences.Clear();
        includedReferences.Clear();
        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId id in MFRebaixoData.CorridorIds(db, tr))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not CivilCorridor c)
                continue;
            if (criteria.CorredoresNomes.Count > 0 && !criteria.CorredoresNomes.Any(n => n.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            bool reference = IsReference(c);
            if (reference && !includeReferences)
            {
                skippedReferences.Add(c.Name);
                continue;
            }
            if (reference)
                includedReferences.Add(c.Name);
            bool stale;
            try
            {
                stale = c.IsOutOfDate;
            }
            catch
            {
                stale = false;
            }
            list.Add((id, c.Name, stale));
        }
        tr.Commit();
        return list;
    }

    /// <summary>
    /// So o .ifc fica na pasta: a planilha de mapeamento (temporaria) e apagada e o log da Autodesk
    /// ("&lt;ifc&gt;_log.json") vai para %LOCALAPPDATA%\Civil3D2026Plugin\logs\ifc.
    /// </summary>
    private static void CleanUp(string ifc)
    {
        try
        {
            string map = MappingPath(ifc);
            if (File.Exists(map))
                File.Delete(map);
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("IFC", "Planilha de mapeamento nao apagada: " + ex.Message);
        }
        try
        {
            string log = Path.Combine(Path.GetDirectoryName(ifc)!, Path.GetFileNameWithoutExtension(ifc) + "_log.json");
            if (File.Exists(log))
                File.Move(log, Path.Combine(AuxFolder, Path.GetFileName(log)), true);
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("IFC", "Log da exportacao nao movido: " + ex.Message);
        }
    }

    /// <summary>Depois da exportacao: nome do projeto/local no .ifc, conferencia e resumo (na pasta de logs).</summary>
    [CommandMethod(C3DCommands.Ifc.Relatorio, CommandFlags.Modal)]
    public void Relatorio()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        Pending? p = _pending;
        _pending = null;
        if (p is null)
        {
            ed.WriteMessage("\nC3DIFCRELATORIO roda sozinho depois do C3DIFC.\n");
            return;
        }
        if (p.FileDia is not null)
        {
            try
            {
                AcApp.SetSystemVariable("FILEDIA", p.FileDia);
            }
            catch
            {
                // FILEDIA fica 0; o usuario pode voltar com FILEDIA 1.
            }
        }
        try
        {
            if (!File.Exists(p.IfcPath) || File.GetLastWriteTime(p.IfcPath) < p.Started.AddSeconds(-2))
            {
                CleanUp(p.IfcPath);
                ed.WriteMessage($"\nExportação não concluída: {p.IfcPath} não foi gerado. Detalhes no log da Autodesk em {AuxFolder}.\n");
                return;
            }
            var extra = new List<string>();
            var options = new IfcSolidFix.Options { FixCut = p.Criteria.CorrigirPecasCortadas };
            if (p.Criteria.SepararRebaixos)
            {
                try
                {
                    options.Rebaixos = MFRebaixoData.ReadAll(doc.Database)
                        .Where(x => p.Corridors.Contains(x.CorridorId))
                        .Select(x => new IfcSolidFix.RebaixoCut
                        {
                            Corridor = x.CorridorName,
                            Baseline = x.BaselineName,
                            Subassembly = x.SubassemblyName,
                            Code = x.Code,
                            Label = x.Label,
                            Side = x.SideLabel,
                            S0 = x.S0,
                            S1 = x.S1,
                            S2 = x.S2,
                            S3 = x.S3,
                            FinalHeight = x.TargetFinal,
                            EntrySlope = x.EntrySlope,
                            ExitSlope = x.ExitSlope
                        }).ToList();
                }
                catch (System.Exception ex)
                {
                    PluginLog.Aviso("IFC", "Rebaixos nao lidos: " + ex.Message);
                }
            }
            if ((options.FixCut || options.Rebaixos.Count > 0) && p.Corridors.Count > 0)
            {
                ed.WriteMessage("\nConferindo as peças do corredor com os sólidos...");
                IfcSolidFix.Result fix = IfcSolidFix.ApplyToFile(p.IfcPath, doc.Database, p.Corridors, options, s => PluginLog.Info("IFC", s));
                PluginLog.Info("IFC", $"Pecas do corredor: {fix.Solids} solidos, {fix.Products} pecas, {fix.Matched} casadas, {fix.Replaced} corrigidas, {fix.AreaRecovered:F2} m2 recuperados, " +
                    $"{options.Rebaixos.Count} rebaixos lidos, {fix.RebaixoCodes.Count} separados em {fix.RebaixoElements} elementos, {fix.Milliseconds} ms" + (fix.Error.Length > 0 ? " - erro: " + fix.Error : ""));
                if (fix.Error.Length > 0)
                {
                    extra.Add("Peças do corredor: não verificadas (" + fix.Error + ")");
                }
                else
                {
                    if (options.FixCut)
                    {
                        extra.Add(fix.Replaced > 0
                            ? $"Peças corrigidas: {fix.Replaced} ({fix.AreaRecovered:0.0} m² em planta recuperados do sólido do corredor)"
                            : "Peças corrigidas: 0");
                    }
                    if (p.Criteria.SepararRebaixos)
                    {
                        extra.Add(fix.RebaixoCodes.Count > 0
                            ? $"Rebaixos separados: {fix.RebaixoCodes.Count} ({fix.RebaixoElements} elementos, property set C3D_REBAIXO)" + (fix.SplitFailed > 0 ? $"; {fix.SplitFailed} sólidos mantidos inteiros (corte inválido, ver log)" : "")
                            : "Rebaixos separados: 0");
                    }
                }
            }
            string report = IfcReport.Finish(p.IfcPath, p.Criteria, p.Selected, p.Selection, extra);
            _lastReport = report;
            CleanUp(p.IfcPath);
            string reportPath = Path.Combine(AuxFolder, Path.GetFileNameWithoutExtension(p.IfcPath) + "-resumo.txt");
            File.WriteAllText(reportPath, report, new UTF8Encoding(true));
            ed.WriteMessage("\n" + report.Replace("\r\n", "\n") + "\n");
            PluginLog.Info("IFC", "C3DIFC: " + p.IfcPath + " (resumo em " + reportPath + ")");
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("IFC", "C3DIFCRELATORIO: " + ex.Message, ex);
            ed.WriteMessage("\nErro no resumo: " + ex.Message + "\n");
        }
    }
}

/// <summary>
/// Grelha simplificada no .ifc: a malha da grelha em X (milhares de vertices) vira um prisma com o contorno
/// externo da grelha (casco convexo em planta) e as alturas de cima/baixo em cada vertice do contorno.
/// Mexe so nas entidades da propria grelha (malha, lista de pontos, estilos dela); o resto do arquivo fica.
/// </summary>
internal static class IfcGratings
{
    private static readonly Regex Line = new(@"^#(\d+)=\s*([A-Z0-9]+)\((.*)\);\s*$", RegexOptions.Compiled);

    public static (string Text, int Plates, int Before, int After) Simplify(string text)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        List<string> lines = text.Split(new[] { nl }, StringSplitOptions.None).ToList();
        var index = new Dictionary<int, int>();
        for (int i = 0; i < lines.Count; i++)
        {
            Match m = Line.Match(lines[i]);
            if (m.Success)
                index[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = i;
        }
        string? Get(int id) => index.TryGetValue(id, out int i) ? lines[i] : null;
        static List<int> Refs(string s) => Regex.Matches(s, @"#(\d+)").Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

        var remove = new HashSet<int>();
        int plates = 0, before = 0, after = 0;
        foreach (int elem in index.Keys.ToList())
        {
            string l = lines[index[elem]];
            if (!l.Contains("=IFCPLATE(") && !l.Contains("= IFCPLATE("))
                continue;
            Match nm = Regex.Match(l, @"IFCPLATE\('[^']*',(?:#\d+|\$),'([^']*)',(\$|'[^']*')");
            if (!nm.Success || !nm.Groups[1].Value.Contains("GRELHA", StringComparison.OrdinalIgnoreCase))
                continue;
            // Produto -> IfcProductDefinitionShape -> IfcShapeRepresentation -> malhas.
            List<int> refs = Refs(l.Substring(l.IndexOf('(')));
            int? pds = refs.FirstOrDefault(r => Get(r)?.Contains("IFCPRODUCTDEFINITIONSHAPE(") == true);
            if (pds is null or 0)
                continue;
            int? rep = Refs(Get(pds.Value)!).Skip(1).FirstOrDefault(r => Get(r)?.Contains("IFCSHAPEREPRESENTATION(") == true);
            if (rep is null or 0)
                continue;
            string repLine = Get(rep.Value)!;
            List<int> items = Refs(repLine.Substring(repLine.LastIndexOf('('))).Where(r => Get(r)?.Contains("IFCTRIANGULATEDFACESET(") == true).ToList();
            if (items.Count == 0)
                continue;

            // Pontos de todas as malhas da grelha.
            var pts = new List<(double X, double Y, double Z)>();
            foreach (int fs in items)
            {
                int pl = Refs(Get(fs)!).Skip(1).First();
                string plLine = Get(pl)!;
                foreach (Match pm in Regex.Matches(plLine, @"\(([-0-9.E+]+),([-0-9.E+]+),([-0-9.E+]+)\)"))
                    pts.Add((D(pm.Groups[1].Value), D(pm.Groups[2].Value), D(pm.Groups[3].Value)));
            }
            if (pts.Count < 6)
                continue;
            List<(double X, double Y)> hull = Hull(pts.Select(p => (p.X, p.Y)).Distinct().ToList());
            if (hull.Count < 3)
                continue;
            var top = new List<double>();
            var bot = new List<double>();
            foreach ((double hx, double hy) in hull)
            {
                var near = pts.Where(p => Math.Abs(p.X - hx) < 0.002 && Math.Abs(p.Y - hy) < 0.002).Select(p => p.Z).ToList();
                top.Add(near.Max());
                bot.Add(near.Min());
            }
            int n = hull.Count;
            var sbP = new StringBuilder();
            for (int i = 0; i < n; i++)
                sbP.Append(i == 0 ? "" : ",").Append($"({F(hull[i].X)},{F(hull[i].Y)},{F(top[i])})");
            for (int i = 0; i < n; i++)
                sbP.Append($",({F(hull[i].X)},{F(hull[i].Y)},{F(bot[i])})");
            var tris = new List<string>();
            for (int i = 1; i + 1 < n; i++)
            {
                tris.Add($"({1},{i + 1},{i + 2})");
                tris.Add($"({n + 1},{n + i + 2},{n + i + 1})");
            }
            for (int i = 0; i < n; i++)
            {
                int a = i + 1, b = ((i + 1) % n) + 1;
                tris.Add($"({a},{n + a},{n + b})");
                tris.Add($"({a},{n + b},{b})");
            }

            int keep = items[0];
            int keepList = Refs(Get(keep)!).Skip(1).First();
            before += pts.Count;
            after += 2 * n;
            lines[index[keepList]] = $"#{keepList}= IFCCARTESIANPOINTLIST3D(({sbP}),$);";
            lines[index[keep]] = $"#{keep}= IFCTRIANGULATEDFACESET(#{keepList},$,.T.,({string.Join(",", tris)}),$);";
            foreach (int fs in items.Skip(1))
            {
                remove.Add(fs);
                remove.Add(Refs(Get(fs)!).Skip(1).First());
            }
            // Representacao so com a malha que ficou.
            int open = repLine.LastIndexOf('(');
            lines[index[rep.Value]] = repLine.Substring(0, open) + $"(#{keep}));";
            // Descricao do produto.
            string desc = "'Grelha metalica em X - representacao simplificada (chapa); barras e vaos nos dados C3D_TRAVESSIA'";
            lines[index[elem]] = l.Substring(0, nm.Groups[2].Index) + desc + l.Substring(nm.Groups[2].Index + nm.Groups[2].Length);
            plates++;
        }

        if (remove.Count > 0)
        {
            // Estilos que apontavam para as malhas tiradas saem junto (senao a referencia fica quebrada).
            foreach (int id in index.Keys.ToList())
            {
                string l = lines[index[id]];
                if (l.Contains("IFCSTYLEDITEM(") && Refs(l.Substring(l.IndexOf('('))).Take(1).Any(remove.Contains))
                    remove.Add(id);
            }
        }
        var dropped = new HashSet<int>(remove.Where(index.ContainsKey).Select(r => index[r]));
        return (string.Join(nl, lines.Where((x, i) => !dropped.Contains(i))), plates, before, after);
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Casco convexo (monotone chain), sentido anti-horario.</summary>
    private static List<(double X, double Y)> Hull(List<(double X, double Y)> p)
    {
        p = p.OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3)
            return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        var h = new List<(double X, double Y)>();
        foreach (var q in p)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0)
                h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0)
                h.RemoveAt(h.Count - 1);
            h.Add(p[i]);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }
}

/// <summary>Pos-processamento do .ifc (texto STEP) e o resumo.</summary>
internal static class IfcReport
{
    public static string Finish(string ifcPath, IfcCriteria c, int selected, string selection, IEnumerable<string>? extra = null)
    {
        string text = File.ReadAllText(ifcPath, Encoding.ASCII);
        var notes = new List<string>(extra ?? Enumerable.Empty<string>());

        // Nome do projeto e do local (a extensao grava "Unknown"/"UnknownName"): so essas duas entidades mudam.
        if (!string.IsNullOrWhiteSpace(c.ProjetoNome))
            text = RenameEntity(text, "IFCPROJECT", c.ProjetoNome, c.ProjetoDescricao, notes, "projeto");
        if (!string.IsNullOrWhiteSpace(c.LocalNome))
            text = RenameEntity(text, "IFCSITE", c.LocalNome, null, notes, "local");
        if (c.GrelhaSimplificada)
        {
            (string simplified, int plates, int before, int after) = IfcGratings.Simplify(text);
            text = simplified;
            notes.Add(plates > 0
                ? $"Grelhas simplificadas: {plates}"
                : "Grelhas simplificadas: nenhuma grelha encontrada");
        }
        File.WriteAllText(ifcPath, text, Encoding.ASCII);

        // Conferencia.
        var classes = Regex.Matches(text, @"=\s*(IFC[A-Z0-9]+)\(")
            .Select(m => m.Groups[1].Value)
            .Where(k => ProductClasses.Contains(k))
            .GroupBy(k => k).OrderByDescending(g => g.Count()).ToList();
        var generic = Regex.Matches(text, @"IFCBUILTELEMENT\('[^']*',(?:#\d+|\$),'([^']*)'")
            .Select(m => Decode(m.Groups[1].Value)).GroupBy(n => n).OrderByDescending(g => g.Count()).ToList();
        int psets = Regex.Matches(text, @"IFCPROPERTYSET\('[^']*',(?:#\d+|\$),'C3D_TRAVESSIA'").Count;
        int alignments = Regex.Matches(text, @"=\s*IFCALIGNMENT\(").Count;

        var sb = new StringBuilder();
        sb.AppendLine("Exportação concluída.");
        sb.AppendLine();
        sb.AppendLine($"Arquivo:  {ifcPath}");
        sb.AppendLine($"Tamanho:  {new FileInfo(ifcPath).Length / 1048576.0:0.0} MB");
        sb.AppendLine($"Objetos:  {selected}  ({selection})");
        foreach (string n in notes)
            sb.AppendLine(n);
        sb.AppendLine();
        sb.AppendLine("Elementos por classe IFC");
        foreach (var g in classes)
            sb.AppendLine($"  {g.Count(),6}  {Friendly(g.Key)}");
        sb.AppendLine($"  {alignments,6}  IfcAlignment  ·  Alinhamento");
        if (psets > 0)
            sb.AppendLine($"Componentes de lombofaixa com propriedades C3D_TRAVESSIA: {psets}");
        if (generic.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Elementos sem classe (IfcBuiltElement): {generic.Sum(g => g.Count())}. Defina a classe em Classes IFC.");
            foreach (var g in generic.Take(12))
                sb.AppendLine($"  {g.Count(),6}  {g.Key}");
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> ProductClasses = new(StringComparer.Ordinal)
    {
        "IFCBUILTELEMENT", "IFCKERB", "IFCPAVEMENT", "IFCCOURSE", "IFCPLATE", "IFCFOOTING", "IFCSURFACEFEATURE", "IFCEARTHWORKSFILL",
        "IFCEARTHWORKSCUT", "IFCBEAM", "IFCSLAB", "IFCRAILING", "IFCGEOGRAPHICELEMENT", "IFCANNOTATION", "IFCDISCRETEACCESSORY",
        "IFCDISTRIBUTIONCHAMBERELEMENT", "IFCPIPESEGMENT", "IFCROAD", "IFCROADPART", "IFCSIGN", "IFCSIGNAL"
    };

    private static string Friendly(string k) => k switch
    {
        "IFCKERB" => "IfcKerb  ·  Meio-fio",
        "IFCPAVEMENT" => "IfcPavement  ·  Pavimento",
        "IFCCOURSE" => "IfcCourse  ·  Camada",
        "IFCPLATE" => "IfcPlate  ·  Grelha / tampa",
        "IFCFOOTING" => "IfcFooting  ·  Viga baldrame",
        "IFCSURFACEFEATURE" => "IfcSurfaceFeature  ·  Sinalização horizontal",
        "IFCEARTHWORKSFILL" => "IfcEarthworksFill  ·  Aterro / subleito",
        "IFCBUILTELEMENT" => "IfcBuiltElement  ·  Sem classe",
        "IFCROAD" => "IfcRoad  ·  Via (corredor)",
        "IFCROADPART" => "IfcRoadPart  ·  Trecho de via",
        "IFCANNOTATION" => "IfcAnnotation  ·  Anotação",
        _ => k
    };

    /// <summary>Troca o nome (3o atributo) e, se dada, a descricao (4o) da primeira entidade do tipo.</summary>
    private static string RenameEntity(string text, string type, string name, string? description, List<string> notes, string what)
    {
        var rx = new Regex(@"(#\d+=\s*" + type + @"\('[^']*',(?:#\d+|\$),)('(?:[^']|'')*'|\$),('(?:[^']|'')*'|\$)");
        Match m = rx.Match(text);
        if (!m.Success)
        {
            notes.Add($"Nome do {what} não gravado: {type} não encontrado.");
            return text;
        }
        string desc = string.IsNullOrWhiteSpace(description) ? m.Groups[3].Value : "'" + Encode(description!) + "'";
        string repl = m.Groups[1].Value + "'" + Encode(name) + "'," + desc;

        return text.Substring(0, m.Index) + repl + text.Substring(m.Index + m.Length);
    }

    /// <summary>Texto STEP: aspas dobradas e caracteres fora do ASCII como \X2\hhhh\X0\.</summary>
    public static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            if (ch == '\'')
                sb.Append("''");
            else if (ch == '\\')
                sb.Append("\\\\");
            else if (ch < 32 || ch > 126)
                sb.Append("\\X2\\").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append("\\X0\\");
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string Decode(string s)
    {
        string t = Regex.Replace(s, @"\\X2\\([0-9A-F]+)\\X0\\", m =>
        {
            var sb = new StringBuilder();
            string hex = m.Groups[1].Value;
            for (int i = 0; i + 3 < hex.Length; i += 4)
                sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
            return sb.ToString();
        });
        return t.Replace("''", "'");
    }
}
