using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    }

    private static Pending? _pending;

    [CommandMethod(C3DCommands.Ifc.Exportar, CommandFlags.Modal | CommandFlags.Redraw)]
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

            string projectName = Path.GetFileNameWithoutExtension(dwg);
            (IfcCriteria criteria, string criteriaPath, bool created) = IfcCriteria.LoadFor(dwg, projectName);
            if (created)
                ed.WriteMessage($"\nCriterios do projeto criados a partir do padrao: {criteriaPath}");

            while (true)
            {
                var ko = new PromptKeywordOptions(
                    $"\nC3DIFC: projeto \"{criteria.ProjetoNome}\", corredores {(criteria.Corredores ? (criteria.CorredoresNomes.Count == 0 ? "todos" : string.Join(", ", criteria.CorredoresNomes)) : "nao")}, " +
                    $"eixos {(criteria.EixosDosCorredores ? "sim" : "nao")}, lombofaixas {(criteria.Lombofaixas ? criteria.LombofaixasPecas.ToLowerInvariant() : "nao")}, superficies {(criteria.Superficies ? "sim" : "nao")}. " +
                    "Exportar, abrir os Criterios do projeto ou o Padrao [Exportar/Criterios/Padrao] <Exportar>: ")
                { AllowNone = true, AppendKeywordsToMessage = false };
                ko.Keywords.Add("Exportar");
                ko.Keywords.Add("Criterios");
                ko.Keywords.Add("Padrao");
                PromptResult kr = ed.GetKeywords(ko);
                if (kr.Status == PromptStatus.Cancel)
                    return;
                if (kr.Status == PromptStatus.OK && kr.StringResult is "Criterios" or "Padrao")
                {
                    string file = kr.StringResult == "Padrao" ? IfcCriteria.DefaultPath : criteriaPath;
                    if (!File.Exists(file))
                        IfcCriteria.Save(new IfcCriteria(), file);
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + file + "\"") { UseShellExecute = false });
                    ed.WriteMessage($"\nAberto no Bloco de Notas: {file}. Salve e responda Exportar (o arquivo e lido de novo).");
                    continue;
                }
                break;
            }
            criteria = IfcCriteria.Read(criteriaPath);

            // Corredores desatualizados: avisa e pergunta (nunca faz Rebuild sozinho).
            List<(ObjectId Id, string Name, bool OutOfDate)> corridors = Corridors(db, criteria);
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
            string defaultIfc = Path.Combine(Path.GetDirectoryName(dwg)!, projectName + ".ifc");
            var po = new PromptStringOptions($"\nArquivo IFC de saida <{defaultIfc}>: ") { AllowSpaces = true };
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

            // Planilha de mapeamento ao lado do .ifc (a extensao le "<nome do ifc>-IfcInfraExportMapping-IFC4X3_ADD2.xlsx").
            string mapping = Path.Combine(Path.GetDirectoryName(ifc)!, Path.GetFileNameWithoutExtension(ifc) + IfcMappingWriter.MappingSuffix);
            (int changed, int added) = IfcMappingWriter.Write(mapping, criteria.Mapeamento);

            // Selecao.
            List<ObjectId> ids = new();
            var what = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (criteria.Corredores)
                {
                    foreach ((ObjectId id, string name, bool _) in corridors)
                    {
                        ids.Add(id);
                        if (criteria.EixosDosCorredores && tr.GetObject(id, OpenMode.ForRead) is CivilCorridor c)
                        {
                            foreach (Autodesk.Civil.DatabaseServices.Baseline b in c.Baselines)
                            {
                                try
                                {
                                    if (!b.AlignmentId.IsNull && !ids.Contains(b.AlignmentId))
                                        ids.Add(b.AlignmentId);
                                }
                                catch
                                {
                                    // Baseline por feature line: sem alinhamento.
                                }
                            }
                        }
                    }
                    what.Add($"{corridors.Count} corredor(es)" + (criteria.EixosDosCorredores ? " com os eixos" : ""));
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

            _pending = new Pending
            {
                IfcPath = ifc,
                CriteriaPath = criteriaPath,
                Criteria = criteria,
                Selected = ids.Count,
                Selection = string.Join(", ", what),
                Started = DateTime.Now
            };
            ed.WriteMessage($"\nPlanilha de mapeamento: {mapping} ({changed} linha(s) mudada(s), {added} acrescentada(s)).");
            ed.WriteMessage($"\nSelecionado(s) {ids.Count} objeto(s): {string.Join(", ", what)}. Chamando a exportacao IFC 4.3 da Autodesk...");
            ed.SetImpliedSelection(ids.ToArray());
            try
            {
                // Exportacao da Autodesk chamada daqui mesmo (sem janela: ela pede so o caminho do arquivo).
                ed.Command("IFCInfraExportSelected", ifc);
                Relatorio();
            }
            catch (System.Exception ex)
            {
                // Sem chamada direta: a exportacao roda logo depois deste comando, com a selecao pronta.
                PluginLog.Aviso("IFC", "C3DIFC: chamada direta da exportacao falhou (" + ex.Message + "); seguindo pela fila de comandos.");
                doc.SendStringToExecute("IFCInfraExportSelected\n" + Quote(ifc) + "\n" + C3DCommands.Ifc.Relatorio + "\n", true, false, false);
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("IFC", "C3DIFC: " + ex.Message, ex);
            ed.WriteMessage("\nErro: " + ex.Message + "\n");
        }
    }

    private static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    private static List<(ObjectId Id, string Name, bool OutOfDate)> Corridors(Database db, IfcCriteria criteria)
    {
        var list = new List<(ObjectId, string, bool)>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        foreach (ObjectId id in MFRebaixoData.CorridorIds(db, tr))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not CivilCorridor c)
                continue;
            if (criteria.CorredoresNomes.Count > 0 && !criteria.CorredoresNomes.Any(n => n.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
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

    /// <summary>Depois da exportacao: nome do projeto/local no .ifc, conferencia e resumo ao lado do .ifc.</summary>
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
        try
        {
            if (!File.Exists(p.IfcPath) || File.GetLastWriteTime(p.IfcPath) < p.Started.AddSeconds(-2))
            {
                ed.WriteMessage($"\nO arquivo {p.IfcPath} nao foi gerado (a exportacao foi cancelada ou deu erro; veja o log *_log.json da Autodesk).\n");
                return;
            }
            string report = IfcReport.Finish(p.IfcPath, p.Criteria, p.Selected, p.Selection);
            string reportPath = Path.Combine(Path.GetDirectoryName(p.IfcPath)!, Path.GetFileNameWithoutExtension(p.IfcPath) + "-C3DIFC-resumo.txt");
            File.WriteAllText(reportPath, report, new UTF8Encoding(true));
            ed.WriteMessage("\n" + report.Replace("\r\n", "\n") + $"\nResumo gravado em {reportPath}\n");
            PluginLog.Info("IFC", "C3DIFC: " + p.IfcPath);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro("IFC", "C3DIFCRELATORIO: " + ex.Message, ex);
            ed.WriteMessage("\nErro no resumo: " + ex.Message + "\n");
        }
    }
}

/// <summary>Pos-processamento do .ifc (texto STEP) e o resumo.</summary>
internal static class IfcReport
{
    public static string Finish(string ifcPath, IfcCriteria c, int selected, string selection)
    {
        string text = File.ReadAllText(ifcPath, Encoding.ASCII);
        var notes = new List<string>();

        // Nome do projeto e do local (a extensao grava "Unknown"/"UnknownName"): so essas duas entidades mudam.
        if (!string.IsNullOrWhiteSpace(c.ProjetoNome))
            text = RenameEntity(text, "IFCPROJECT", c.ProjetoNome, c.ProjetoDescricao, notes, "projeto");
        if (!string.IsNullOrWhiteSpace(c.LocalNome))
            text = RenameEntity(text, "IFCSITE", c.LocalNome, null, notes, "local");
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
        sb.AppendLine($"C3DIFC - resumo da exportacao IFC 4.3 ({DateTime.Now:dd/MM/yyyy HH:mm})");
        sb.AppendLine($"Arquivo: {ifcPath} ({new FileInfo(ifcPath).Length / 1048576.0:0.0} MB)");
        sb.AppendLine($"Selecionado: {selected} objeto(s) - {selection}");
        foreach (string n in notes)
            sb.AppendLine(n);
        sb.AppendLine("Pecas por classe IFC:");
        foreach (var g in classes)
            sb.AppendLine($"  {g.Count(),5}  {Friendly(g.Key)}");
        sb.AppendLine($"Eixos (IfcAlignment): {alignments}");
        sb.AppendLine($"Pecas da lombofaixa com os dados do plugin (Property Set C3D_TRAVESSIA): {psets}");
        if (generic.Count == 0)
        {
            sb.AppendLine("Nenhuma peca generica (IfcBuiltElement): tudo saiu com classe.");
        }
        else
        {
            sb.AppendLine($"Ainda sairam {generic.Sum(g => g.Count())} peca(s) generica(s) (IfcBuiltElement). Para dar classe, mapeie o estilo/layer delas nos criterios:");
            foreach (var g in generic.Take(12))
                sb.AppendLine($"  {g.Count(),5}  {g.Key}");
        }
        sb.AppendLine("Criterios usados: arquivo -C3DIFC.json ao lado do DWG; planilha de mapeamento ao lado do .ifc.");
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
        "IFCKERB" => "IfcKerb (meio-fio)",
        "IFCPAVEMENT" => "IfcPavement (pavimento / plato da lombofaixa)",
        "IFCCOURSE" => "IfcCourse (camadas: base, sub-base)",
        "IFCPLATE" => "IfcPlate (grelha / tampa)",
        "IFCFOOTING" => "IfcFooting (viga baldrame)",
        "IFCSURFACEFEATURE" => "IfcSurfaceFeature (faixa e triangulos)",
        "IFCEARTHWORKSFILL" => "IfcEarthworksFill (subleito)",
        "IFCBUILTELEMENT" => "IfcBuiltElement (generico, sem classe)",
        "IFCROAD" => "IfcRoad (corredor)",
        "IFCROADPART" => "IfcRoadPart (baselines, regions, pecas)",
        "IFCANNOTATION" => "IfcAnnotation (anotacoes/levantamento)",
        _ => k
    };

    /// <summary>Troca o nome (3o atributo) e, se dada, a descricao (4o) da primeira entidade do tipo.</summary>
    private static string RenameEntity(string text, string type, string name, string? description, List<string> notes, string what)
    {
        var rx = new Regex(@"(#\d+=\s*" + type + @"\('[^']*',(?:#\d+|\$),)('(?:[^']|'')*'|\$),('(?:[^']|'')*'|\$)");
        Match m = rx.Match(text);
        if (!m.Success)
        {
            notes.Add($"Nome do {what}: {type} nao encontrado no arquivo (nada mudou).");
            return text;
        }
        string old = Decode(m.Groups[2].Value.Trim('\''));
        string desc = string.IsNullOrWhiteSpace(description) ? m.Groups[3].Value : "'" + Encode(description!) + "'";
        string repl = m.Groups[1].Value + "'" + Encode(name) + "'," + desc;
        notes.Add($"Nome do {what}: \"{old}\" -> \"{name}\" (gravado no .ifc).");
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
