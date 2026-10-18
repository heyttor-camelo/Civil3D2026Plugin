using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// C3DVIARIO: rede viaria urbana em planta.
/// - eixos com secao (pista, canteiro, sarjeta, meio-fio, calcada, raio);
/// - linhas por elemento da secao, cada uma no seu layer;
/// - intersecoes automaticas com esquinas concordadas; rotatoria;
/// - preview ao lancar eixo (retas e arcos) e ao mudar raios;
/// - edicao associativa (mexeu no eixo, refaz).
/// C3DVIARIOEIXO: lancar eixos. C3DVIARIORAIO: raio de esquina/ilha/eixo.
/// </summary>
public sealed class ViarioCommands
{
    public const string Versao = C3DVersions.Viario;

    [CommandMethod(C3DCommands.Viario.Rede, CommandFlags.Modal)]
    public void Rede()
    {
        Run(C3DCommands.Viario.Rede, doc =>
        {
            Editor ed = doc.Editor;
            PromptKeywordOptions options = new(
                "\nOpcao [Eixo/MOdelos/ADicionar/Secao/ATribuir/Raio/ROtatoria/ELementos/DEsvincular/REmover/ATUalizar/AUtomatico/CIvil/SInalizacao/COtar/HAchura/PAinel/Config] <Eixo>: ",
                "Eixo MOdelos ADicionar Secao ATribuir Raio ROtatoria ELementos DEsvincular REmover ATUalizar AUtomatico CIvil SInalizacao COtar HAchura PAinel Config")
            {
                AllowNone = true
            };

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                return;

            switch (result.Status == PromptStatus.OK ? result.StringResult : "Eixo")
            {
                case "MOdelos": ViarioTemplateCommands.InsertTemplate(doc); break;
                case "ADicionar": AddAxes(doc); break;
                case "Secao": ManageSections(doc); break;
                case "ATribuir": AssignSection(doc); break;
                case "Raio": EditRadius(doc); break;
                case "ROtatoria": CreateRoundabout(doc); break;
                case "ELementos": ViarioElementCommands.Menu(doc); break;
                case "DEsvincular": Release(doc); break;
                case "REmover": RemoveAxes(doc); break;
                case "ATUalizar": ViarioSync.Forget(doc.Database); ViarioSync.Regenerate(doc, quiet: false, force: true); break;
                case "AUtomatico": ToggleAuto(doc); break;
                case "CIvil": CivilMenu(doc); break;
                case "SInalizacao": ViarioSignageSync.Command(doc); break;
                case "COtar": ViarioDimensions.Command(doc); break;
                case "HAchura": ViarioHatchSync.Command(doc); break;
                case "PAinel": ViarioPanel.Command(doc); break;
                case "Config": Configure(doc); break;
                default: DrawAxes(doc); break;
            }
        });
    }

    /// <summary>Liga/desliga a atualizacao automatica da rede neste desenho.</summary>
    private static void ToggleAuto(Document doc)
    {
        bool on = ViarioSync.IsAuto(doc.Database);
        PromptKeywordOptions options = new(
            $"\nAtualizacao automatica da rede (agora {(on ? "ligada" : "desligada")}) [Ligar/Desligar] <{(on ? "Desligar" : "Ligar")}>: ",
            "Ligar Desligar")
        {
            AllowNone = true
        };
        PromptResult result = doc.Editor.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return;

        bool want = result.Status == PromptStatus.OK ? result.StringResult == "Ligar" : !on;
        if (want != on)
            ViarioSync.SetAuto(doc, want);
        else
            doc.Editor.WriteMessage($"\n  Atualizacao automatica ja esta {(on ? "ligada" : "desligada")}.");
    }

    [CommandMethod(C3DCommands.Viario.Painel, CommandFlags.Modal)]
    public void Painel() => Run(C3DCommands.Viario.Painel, ViarioPanel.Command);

    [CommandMethod(C3DCommands.Viario.Sinal, CommandFlags.Modal)]
    public void Sinal() => Run(C3DCommands.Viario.Sinal, ViarioSignageSync.Command);

    [CommandMethod(C3DCommands.Viario.Cotar, CommandFlags.Modal)]
    public void Cotar() => Run(C3DCommands.Viario.Cotar, ViarioDimensions.Command);

    [CommandMethod(C3DCommands.Viario.Hachura, CommandFlags.Modal)]
    public void Hachura() => Run(C3DCommands.Viario.Hachura, ViarioHatchSync.Command);

    [CommandMethod(C3DCommands.Viario.Eixo, CommandFlags.Modal)]
    public void Eixo() => Run(C3DCommands.Viario.Eixo, DrawAxes);

    [CommandMethod(C3DCommands.Viario.Raio, CommandFlags.Modal)]
    public void Raio() => Run(C3DCommands.Viario.Raio, EditRadius);

    [CommandMethod(C3DCommands.Viario.Modelos, CommandFlags.Modal)]
    public void Modelos() => Run(C3DCommands.Viario.Modelos, ViarioTemplateCommands.InsertTemplate);

    [CommandMethod(C3DCommands.Viario.Alargar, CommandFlags.Modal)]
    public void Alargar() => Run(C3DCommands.Viario.Alargar, ViarioElementCommands.Alargar);

    [CommandMethod(C3DCommands.Viario.Gota, CommandFlags.Modal)]
    public void Gota() => Run(C3DCommands.Viario.Gota, ViarioElementCommands.Gota);

    [CommandMethod(C3DCommands.Viario.Conversao, CommandFlags.Modal)]
    public void Conversao() => Run(C3DCommands.Viario.Conversao, ViarioElementCommands.Conversao);

    [CommandMethod(C3DCommands.Viario.Abertura, CommandFlags.Modal)]
    public void Abertura() => Run(C3DCommands.Viario.Abertura, ViarioElementCommands.Abertura);

    [CommandMethod(C3DCommands.Viario.Livre, CommandFlags.Modal)]
    public void Livre() => Run(C3DCommands.Viario.Livre, ViarioElementCommands.Livre);

    [CommandMethod(C3DCommands.Viario.ElemRemover, CommandFlags.Modal)]
    public void ElemRemover() => Run(C3DCommands.Viario.ElemRemover, ViarioElementCommands.Remove);

    [CommandMethod(C3DCommands.Viario.Ilha, CommandFlags.Modal)]
    public void Ilha() => Run(C3DCommands.Viario.Ilha, ViarioVertexCommands.Islands);

    [CommandMethod(C3DCommands.Viario.Teste, CommandFlags.Modal)]
    public void Teste() => Run(C3DCommands.Viario.Teste, RunSelfTest);

    /// <summary>
    /// Autoteste: confere os objetos do Civil contra a rede e grava o relatorio em
    /// %TEMP%\C3DVIARIOTESTE.txt (a etapa "INICIO" zera o arquivo).
    /// </summary>
    private static void RunSelfTest(Document doc)
    {
        Editor ed = doc.Editor;
        PromptStringOptions label = new("\nEtapa do teste <manual>: ") { AllowSpaces = false };
        PromptResult r = ed.GetString(label);
        if (r.Status == PromptStatus.Cancel)
            return;

        string step = string.IsNullOrWhiteSpace(r.StringResult) ? "manual" : r.StringResult.Trim();
        List<string> lines;
        int ok, fail;
        try
        {
            lines = ViarioCivil.Check(doc, out ok, out fail);
        }
        catch (System.Exception ex)
        {
            lines = new List<string> { "FALHA excecao: " + ex };
            ok = 0;
            fail = 1;
        }

        string header = $"=== {step} | {DateTime.Now:HH:mm:ss} | OK {ok} | FALHA {fail} ===";
        ed.WriteMessage("\n" + header);
        foreach (string line in lines.Where(l => !l.StartsWith("OK", StringComparison.Ordinal)).Take(25))
            ed.WriteMessage("\n  " + line);

        List<string> all = new() { header };
        all.AddRange(lines.Select(l => "  " + l));
        all.Add(string.Empty);
        bool reset = string.Equals(step, "INICIO", StringComparison.OrdinalIgnoreCase);
        foreach (string path in ReportPaths())
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (reset)
                    System.IO.File.WriteAllLines(path, all);
                else
                    System.IO.File.AppendAllLines(path, all);
                ed.WriteMessage($"\n  Relatorio: {path}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n  Nao gravei o relatorio em " + path + ": " + ex.Message);
            }
        }
    }

    /// <summary>%TEMP% e, se a DLL estiver na pasta NETLOAD do repositorio, tests\viario\out.</summary>
    private static IEnumerable<string> ReportPaths()
    {
        yield return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "C3DVIARIOTESTE.txt");
        string? dir = System.IO.Path.GetDirectoryName(typeof(ViarioCommands).Assembly.Location);
        string? repo = dir is null ? null : System.IO.Directory.GetParent(dir)?.FullName;
        if (repo is not null && System.IO.Directory.Exists(System.IO.Path.Combine(repo, "tests", "viario")))
            yield return System.IO.Path.Combine(repo, "tests", "viario", "out", "C3DVIARIOTESTE.txt");
    }

    [CommandMethod(C3DCommands.Viario.Civil, CommandFlags.Modal)]
    public void Civil() => Run(C3DCommands.Viario.Civil, CivilMenu);

    /// <summary>Gera/atualiza ou remove os alinhamentos do Civil (eixos, LD/LE, curb returns).</summary>
    private static void CivilMenu(Document doc)
    {
        Editor ed = doc.Editor;
        bool active = ViarioCivil.IsActive(doc.Database);
        ed.WriteMessage(active
            ? "\n  Objetos do Civil ativos: acompanham a rede automaticamente."
            : "\n  Ainda nao ha objetos do Civil gerados para esta rede.");

        PromptKeywordOptions options = new("\nObjetos do Civil [Gerar/Remover] <Gerar>: ", "Gerar Remover")
        {
            AllowNone = true
        };
        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        if (r.Status == PromptStatus.OK && r.StringResult == "Remover")
        {
            PromptKeywordOptions confirm = new("\nApagar eixos, bordos e curb returns gerados no Civil (perfis juntos)? [Sim/Nao] <Nao>: ", "Sim Nao")
            {
                AllowNone = true
            };
            PromptResult c = ed.GetKeywords(confirm);
            if (c.Status != PromptStatus.OK || c.StringResult != "Sim")
                return;

            int n = ViarioCivil.RemoveAll(doc);
            ed.WriteMessage($"\n  {n} alinhamento(s) removido(s). A rede em polilinhas continua.");
            return;
        }

        ViarioCivil.Report report = ViarioCivil.Sync(doc, create: true);
        ed.WriteMessage("\n  " + report);
        if (report.Styles.Length > 0)
            ed.WriteMessage("\n  Alinhamentos novos: " + report.Styles);
        foreach (string m in report.Messages.Distinct().Take(12))
            ed.WriteMessage("\n  [CIVIL] " + m);
        ed.WriteMessage($"\n  Site \"{ViarioCivil.SiteName}\", layers {ViarioCivil.LayerAxis}, {ViarioCivil.LayerEdge}, {ViarioCivil.LayerCurb}.");
    }

    private static void Run(string command, Action<Document> action)
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        ViarioSync.Initialize();

        bool hints = command != C3DCommands.Viario.Teste && command != C3DCommands.Viario.Diag && command != C3DCommands.Viario.Painel;
        HashSet<string> before = hints ? HintKeys(doc) : new HashSet<string>();

        try
        {
            ViarioSync.Suspended = true;
            WriteHeader(doc);
            action(doc);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\n[ERRO " + command + "] " + ex.Message);
        }
        finally
        {
            ViarioSync.Suspended = false;
        }

        // Recomendacoes novas (manuais DNIT) que surgiram com este comando.
        if (hints)
        {
            try
            {
                List<Hint> fresh = CurrentHints(doc).Where(h => !before.Contains(h.Key)).ToList();
                foreach (Hint h in fresh.OrderByDescending(h => h.Level).Take(4))
                    doc.Editor.WriteMessage("\n  [DNIT] " + h.Text);
                if (fresh.Count > 4)
                    doc.Editor.WriteMessage($"\n  [DNIT] ... mais {fresh.Count - 4} recomendacao(oes) no painel (C3DVIARIOPAINEL).");
            }
            catch
            {
                // Recomendacao nunca atrapalha o comando.
            }
        }
    }

    internal static List<Hint> CurrentHints(Document doc)
    {
        AcadDb.Database db = doc.Database;
        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(db);
        return ViarioNorms.Check(axes, result, ViarioSync.EngineSettingsFor(db), ViarioStore.LoadSections(db));
    }

    private static HashSet<string> HintKeys(Document doc)
    {
        try
        {
            return CurrentHints(doc).Select(h => h.Key).ToHashSet();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static void WriteHeader(Document doc)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        RoadSection current = CurrentSection(doc.Database, settings);
        doc.Editor.WriteMessage(
            "\n" + C3DCommands.Viario.Rede + " v" + Versao + " - rede viaria (bordos e intersecoes automaticos)." +
            "\n  Secao corrente: " + Describe(current));
    }

    // ------------------------------------------------------------------
    // Eixo (retas e arcos, com preview)
    // ------------------------------------------------------------------

    /// <summary>Modo de lancamento lembrado na sessao: concordancia mantida (Vehicle Tracking) ou livre.</summary>
    private static bool _chainMode = true;

    private static void DrawAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        while (true)
        {
            ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
            RoadSection section = CurrentSection(db, settings);

            PromptPointOptions first = new(
                "\nPrimeiro ponto do eixo [" + section.Name + ", " + (_chainMode ? "concordancia mantida" : "livre") + "] ou [Secao/COncordancia/Inverter] <sair>: ")
            {
                AllowNone = true
            };
            first.Keywords.Add("Secao");
            first.Keywords.Add("COncordancia");
            first.Keywords.Add("Inverter");
            first.AppendKeywordsToMessage = false;

            PromptPointResult start = ed.GetPoint(first);
            if (start.Status == PromptStatus.Keyword)
            {
                switch (start.StringResult)
                {
                    case "Secao": ChooseCurrentSection(doc); break;
                    case "Inverter": ReverseAxesPrompt(doc); break;
                    default: ChainMenu(doc); break;
                }

                continue;
            }

            if (start.Status != PromptStatus.OK)
                return;

            Point3d p0 = start.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            List<V2> points = new() { new V2(p0.X, p0.Y) };
            List<double> bulges = new();
            bool arcMode = false;
            bool chained = _chainMode;

            (List<AxisInput> existing, HashSet<string> existingKeys) = ViarioSync.CurrentAxes(db);
            EngineSettings engine = ViarioSync.EngineSettingsFor(db);

            bool finished = false;
            while (!finished)
            {
                double tol = ClickTolerance(ed);
                AxisJig jig = new(points, bulges, arcMode, chained, tol, section, existing, existingKeys, engine, settings.Capture);
                PromptResult drag = ed.Drag(jig);

                if (jig.Finished || drag.Status == PromptStatus.None)
                {
                    finished = true;
                }
                else if (jig.Keyword is string keyword)
                {
                    switch (keyword)
                    {
                        case "Arc":
                            arcMode = true;
                            break;
                        case "Tangent":
                            arcMode = false;
                            break;
                        case "Free":
                            chained = false;
                            break;
                        case "Chain":
                            chained = true;
                            break;
                        default: // Undo
                            Undo(points, bulges);
                            break;
                    }
                }
                else if (drag.Status == PromptStatus.OK)
                {
                    if (points.Count > 1 && V2.Dist(points[^1], jig.Picked) <= tol)
                    {
                        // Clique em cima do ultimo vertice: desfaz o ultimo trecho (como no Vehicle Tracking).
                        Undo(points, bulges);
                    }
                    else if (V2.Dist(points[^1], jig.Current) > 1e-6)
                    {
                        points.Add(jig.Current);
                        bulges.Add(jig.CurrentBulge);
                    }
                }
                else
                {
                    return; // ESC: descarta o eixo em andamento
                }
            }

            if (points.Count < 2)
                continue;

            _chainMode = chained;
            List<V2> final = chained
                ? AxisJig.CaptureAlong(points, bulges, existing, settings.Capture)
                : AxisJig.Capture(points, existing, settings.Capture);
            if (chained)
            {
                (final, bulges, _) = ViarioChain.Solve(final, bulges);
            }

            CreateAxis(doc, final, bulges, section, chained);
            ViarioResult? result = ViarioSync.Regenerate(doc, quiet: true);
            if (result is not null)
            {
                ed.WriteMessage($"\n  Eixo criado ({points.Count - 1} trecho(s), {(chained ? "concordancia mantida" : "livre")}). " +
                                $"Rede: {result.Nodes.Count} intersecao(oes), {result.Lines.Count} linha(s).");
                ViarioSync.WriteWarnings(doc, result);
            }
        }

        static void Undo(List<V2> points, List<double> bulges)
        {
            if (points.Count > 1)
            {
                points.RemoveAt(points.Count - 1);
                bulges.RemoveAt(bulges.Count - 1);
            }
        }
    }

    // ------------------------------------------------------------------
    // Reconcordar dois trechos de um eixo (num vertice)
    // ------------------------------------------------------------------

    /// <summary>
    /// Clique perto de um vertice interno do eixo: o arco vizinho e refeito para
    /// sair/chegar tangente ao outro trecho (Seguinte: o arco depois do vertice
    /// recebe a direcao do trecho anterior; Anterior: o arco antes do vertice
    /// passa a chegar na direcao do trecho seguinte). Entre duas retas, use
    /// C3DVIARIORAIO no vertice para inserir o arco.
    /// </summary>
    private static void ReconcordVertex(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult pick = ed.GetPoint(new PromptPointOptions("\nClique perto do vertice entre os dois trechos: "));
        if (pick.Status != PromptStatus.OK)
            return;
        Point3d w = pick.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 p = new(w.X, w.Y);
        double tol = Math.Max(ClickTolerance(ed) * 3.0, 1.0);

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.Polyline? target = null;
        int vertex = -1;
        double best = tol;
        foreach (AxisInput a in ViarioSync.CollectAxes(db, tr, ViarioSync.SectionMap(db)))
        {
            AcadDb.ObjectId id = db.GetObjectId(false, new AcadDb.Handle(Convert.ToInt64(a.Key, 16)), 0);
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is not AcadDb.Polyline pl || pl.Closed)
                continue;
            for (int i = 1; i < pl.NumberOfVertices - 1; i++)
            {
                Point2d q = pl.GetPoint2dAt(i);
                double d = V2.Dist(new V2(q.X, q.Y), p);
                if (d < best)
                {
                    best = d;
                    target = pl;
                    vertex = i;
                }
            }
        }

        if (target is null)
        {
            ed.WriteMessage("\n  Nenhum vertice interno de eixo perto desse ponto.");
            return;
        }

        (List<V2> pts, List<double> bulges) = ViarioStore.Vertices(target);
        bool prevArc = Math.Abs(bulges[vertex - 1]) > 1e-9;
        bool nextArc = Math.Abs(bulges[vertex]) > 1e-9;
        if (!prevArc && !nextArc)
        {
            ed.WriteMessage("\n  Os dois trechos sao retas: use C3DVIARIORAIO perto do vertice para inserir o arco de concordancia.");
            return;
        }

        Seg prev = new(pts[vertex - 1], pts[vertex], bulges[vertex - 1]);
        Seg next = new(pts[vertex], pts[vertex + 1], bulges[vertex]);
        double before = Math.Abs(Math.Atan2(V2.Cross(prev.TangentAt(1.0), next.TangentAt(0.0)), V2.Dot(prev.TangentAt(1.0), next.TangentAt(0.0)))) * 180.0 / Math.PI;
        ed.WriteMessage($"\n  Vertice {vertex} de {target.Handle}: {(prevArc ? "arco" : "reta")} -> {(nextArc ? "arco" : "reta")}, quebra de {before:0.#} graus.");

        // O que se ajusta: o arco (refeito tangente a reta) ou a reta (movida ate tangenciar o arco, que fica).
        string what = "Arco";
        if (prevArc != nextArc)
        {
            PromptKeywordOptions wk = new(
                "\nO que ajustar [Arco/Tangente] <Arco> (Arco: refaz o arco pela direcao da reta; Tangente: mantem o arco e move a reta ate tangencia-lo): ",
                "Arco Tangente")
            {
                AllowNone = true
            };
            PromptResult wr = ed.GetKeywords(wk);
            if (wr.Status != PromptStatus.OK && wr.Status != PromptStatus.None)
                return;
            what = wr.Status == PromptStatus.OK ? wr.StringResult : "Arco";
        }

        target.UpgradeOpen();
        string done;
        if (what == "Tangente")
        {
            // Reta vizinha vai ate o ponto de tangencia do circulo do arco, tirado da ponta fixa da reta.
            bool arcIsPrev = prevArc;
            Seg arc = arcIsPrev ? prev : next;
            V2 arcFixedEnd = arcIsPrev ? pts[vertex - 1] : pts[vertex + 1];
            V2 lineFarEnd = arcIsPrev ? pts[vertex + 1] : pts[vertex - 1];
            (V2 Vertex, double Bulge)? sol = ViarioChain.LineToArc(arc, arcFixedEnd, lineFarEnd, pts[vertex], arcEndsAtVertex: arcIsPrev);
            if (sol is null)
            {
                ed.WriteMessage("\n  Nao ha tangente: a outra ponta da reta esta dentro do circulo do arco (ou o arco fecharia a volta). Ajuste pelo Arco ou mova a ponta da reta.");
                return;
            }

            target.SetPointAt(vertex, new Point2d(sol.Value.Vertex.X, sol.Value.Vertex.Y));
            target.SetBulgeAt(arcIsPrev ? vertex - 1 : vertex, sol.Value.Bulge);
            done = $"reta {(arcIsPrev ? "seguinte" : "anterior")} movida ate a tangencia; arco mantido (R {arc.Radius:0.##} m), vertice deslocado {V2.Dist(sol.Value.Vertex, pts[vertex]):0.##} m";
        }
        else
        {
            string which = nextArc ? "Seguinte" : "Anterior";
            if (prevArc && nextArc)
            {
                PromptKeywordOptions opt = new("\nQual arco refazer [Seguinte/Anterior] <Seguinte>: ", "Seguinte Anterior") { AllowNone = true };
                PromptResult r = ed.GetKeywords(opt);
                if (r.Status != PromptStatus.OK && r.Status != PromptStatus.None)
                    return;
                which = r.Status == PromptStatus.OK ? r.StringResult : "Seguinte";
            }

            if (which == "Seguinte")
            {
                // Arco seguinte: sai do vertice na direcao em que o trecho anterior chega.
                target.SetBulgeAt(vertex, ViarioChain.TangentBulge(prev.TangentAt(1.0), pts[vertex], pts[vertex + 1]));
            }
            else
            {
                // Arco anterior: chega ao vertice na direcao em que o trecho seguinte sai
                // (e o arco tangente visto de tras, com o sinal trocado).
                V2 h = next.TangentAt(0.0);
                target.SetBulgeAt(vertex - 1, -ViarioChain.TangentBulge(-h, pts[vertex], pts[vertex - 1]));
            }

            done = $"arco {(which == "Seguinte" ? "seguinte" : "anterior")} refeito tangente";
        }

        tr.Commit();
        ed.WriteMessage($"\n  {done} (quebra de {before:0.#} graus eliminada)." +
                        (ViarioStore.IsChained(target) ? "" : " O eixo continua em edicao livre; Concordancia > Selecionar > Manter para fixar."));
        ViarioSync.Regenerate(doc, quiet: true);
    }

    // ------------------------------------------------------------------
    // Inverter o sentido do eixo
    // ------------------------------------------------------------------

    private static void ReverseAxesPrompt(Document doc)
    {
        Editor ed = doc.Editor;
        PromptSelectionOptions sel = new() { MessageForAdding = "\nEixos para inverter o sentido (estacas, esquerda/direita): " };
        PromptSelectionResult selection = ed.GetSelection(sel, new SelectionFilter(new[]
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, ViarioStore.RegApp)
        }));
        if (selection.Status != PromptStatus.OK)
            return;

        (int count, List<string> notes) = ReverseAxes(doc, selection.Value.GetObjectIds());
        ed.WriteMessage($"\n  {count} eixo(s) invertido(s).");
        foreach (string n in notes)
            ed.WriteMessage("\n  " + n);
        if (count > 0)
            ViarioSync.Regenerate(doc, quiet: false);
    }

    /// <summary>
    /// Inverte o sentido de eixos: a geometria e revertida, os elementos sao
    /// reancorados (estaca L - s, lado do no trocado) e as chaves das esquinas
    /// (raios e faixas livres, que guardam o sentido do ramo) sao reescritas.
    /// Secoes com lados diferentes trocam de lado neste eixo (aviso).
    /// </summary>
    internal static (int Count, List<string> Notes) ReverseAxes(Document doc, IEnumerable<AcadDb.ObjectId> ids)
    {
        AcadDb.Database db = doc.Database;
        List<string> notes = new();
        int count = 0;
        Dictionary<string, RoadSection> sections = ViarioSync.SectionMap(db);

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        Dictionary<string, double> radii = ViarioStore.LoadCornerRadii(db);
        Dictionary<string, SlipLane> slips = ViarioStore.LoadSlipLanes(db);
        bool keysChanged = false;

        foreach (AcadDb.ObjectId id in ids)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForWrite) is not AcadDb.Curve curve ||
                !ViarioStore.ReadAxis(curve, out string sectionId, out double? radius, out List<AxisEvent> events, out bool chained))
            {
                continue;
            }

            if (curve is AcadDb.Circle)
            {
                notes.Add($"{curve.Handle}: anel de rotatoria nao tem sentido a inverter.");
                continue;
            }

            GPath? before = ViarioStore.ToPath(curve);
            if (before is null)
                continue;

            try
            {
                curve.ReverseCurve();
            }
            catch (System.Exception ex)
            {
                notes.Add($"{curve.Handle}: nao foi possivel inverter ({ex.Message}).");
                continue;
            }

            double length = before.Length;
            foreach (AxisEvent ev in events)
            {
                ev.S = Math.Max(0.0, length - ev.S);
                ev.Dir = -ev.Dir;
            }

            ViarioStore.SetAxis(curve, sectionId, radius, events, chained);

            string key = curve.Handle.ToString();
            keysChanged |= FlipCornerKeys(radii, key);
            keysChanged |= FlipCornerKeys(slips, key);

            if (sections.TryGetValue(sectionId, out RoadSection? sec) && !sec.Simetrica)
                notes.Add($"{key}: a secao {sec.Name} tem lados diferentes; esquerda e direita trocam neste eixo.");

            count++;
        }

        if (keysChanged)
        {
            ViarioStore.SaveCornerRadii(tr, db, radii);
            ViarioStore.SaveSlipLanes(tr, db, slips);
        }

        tr.Commit();
        return (count, notes);
    }

    /// <summary>Troca o sinal do sentido nas chaves "A+|B-" (e "A+|B-#2") que citam o eixo.</summary>
    private static bool FlipCornerKeys<T>(Dictionary<string, T> map, string axisKey)
    {
        bool changed = false;
        foreach (string old in map.Keys.ToList())
        {
            string suffix = "";
            string body = old;
            int hash = old.IndexOf('#');
            if (hash >= 0)
            {
                suffix = old[hash..];
                body = old[..hash];
            }

            string[] parts = body.Split('|');
            bool hit = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length < 2)
                    continue;
                string k = parts[i][..^1];
                char sign = parts[i][^1];
                if (k == axisKey && (sign == '+' || sign == '-'))
                {
                    parts[i] = k + (sign == '+' ? "-" : "+");
                    hit = true;
                }
            }

            if (!hit)
                continue;

            T value = map[old];
            map.Remove(old);
            map[string.Join("|", parts) + suffix] = value;
            changed = true;
        }

        return changed;
    }

    /// <summary>Raio, em unidades do desenho, para "clicar em cima" de um vertice (cerca de 1% da altura da tela).</summary>
    private static double ClickTolerance(Editor ed)
    {
        try
        {
            using AcadDb.ViewTableRecord view = ed.GetCurrentView();
            return Math.Max(view.Height / 90.0, 1e-3);
        }
        catch
        {
            return 0.5;
        }
    }

    /// <summary>Concordancia: modo dos proximos eixos, ou Manter/Liberar em eixos existentes.</summary>
    private static void ChainMenu(Document doc)
    {
        Editor ed = doc.Editor;
        PromptKeywordOptions opt = new(
            "\nConcordancia [Manter/Liberar/Selecionar/Vertice/VIncular/DEsvincular] <" + (_chainMode ? "Manter" : "Liberar") + "> (Manter/Liberar: proximos eixos; VIncular/DEsvincular: eixos existentes; Vertice: reconcorda dois trechos): ",
            "Manter Liberar Selecionar Vertice VIncular DEsvincular")
        {
            AllowNone = true
        };
        PromptResult r = ed.GetKeywords(opt);
        if (r.Status == PromptStatus.None)
            return;
        if (r.Status != PromptStatus.OK)
            return;

        switch (r.StringResult)
        {
            case "Manter":
                _chainMode = true;
                ed.WriteMessage("\n  Proximos eixos: retas e arcos concordantes (T reta na direcao, A arco tangente, U desfaz).");
                return;
            case "Liberar":
                _chainMode = false;
                ed.WriteMessage("\n  Proximos eixos: lancamento livre (vertices e arcos independentes).");
                return;
            case "Vertice":
                ReconcordVertex(doc);
                return;
        }

        bool? preset = r.StringResult switch { "VIncular" => true, "DEsvincular" => false, _ => null };
        PromptSelectionOptions sel = new()
        {
            MessageForAdding = preset switch
            {
                true => "\nEixos para vincular a concordancia (retas e arcos tangentes, refeitos ao editar): ",
                false => "\nEixos para desvincular a concordancia (vertices e arcos livres): ",
                _ => "\nEixos para mudar a concordancia: "
            }
        };
        PromptSelectionResult selection = ed.GetSelection(sel, new SelectionFilter(new[]
        {
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Start, "LWPOLYLINE"),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.ExtendedDataRegAppName, ViarioStore.RegApp)
        }));
        if (selection.Status != PromptStatus.OK)
            return;

        bool keep;
        if (preset is bool fixedChoice)
        {
            keep = fixedChoice;
        }
        else
        {
            PromptKeywordOptions what = new("\nNesses eixos [Manter/Liberar] <Manter>: ", "Manter Liberar") { AllowNone = true };
            PromptResult w = ed.GetKeywords(what);
            if (w.Status != PromptStatus.OK && w.Status != PromptStatus.None)
                return;
            keep = w.Status != PromptStatus.OK || w.StringResult == "Manter";
        }

        int count = 0;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (AcadDb.ObjectId id in selection.Value.GetObjectIds())
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForWrite) is not AcadDb.Polyline pl || !ViarioStore.IsAxis(pl, out _))
                    continue;
                ViarioStore.SetChained(pl, keep);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s): concordancia {(keep ? "mantida (refeita na proxima atualizacao)" : "liberada")}.");
        if (keep)
            ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void CreateAxis(Document doc, List<V2> points, List<double> bulges, RoadSection section, bool chained = false)
    {
        AcadDb.Database db = doc.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

        ViarioStore.EnsureRegApp(tr, db);
        ViarioStore.EnsureLayers(tr, db);

        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

        AcadDb.Polyline pl = new();
        for (int i = 0; i < points.Count; i++)
            pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), i < bulges.Count ? bulges[i] : 0.0, 0, 0);

        pl.SetDatabaseDefaults(db);
        pl.Layer = ViarioStore.LayerAxis;
        ms.AppendEntity(pl);
        tr.AddNewlyCreatedDBObject(pl, true);
        ViarioStore.SetAxis(pl, section.Id, null, null, chained && points.Count > 2);
        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Rotatoria
    // ------------------------------------------------------------------

    private static void CreateRoundabout(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult centerResult = ed.GetPoint(new PromptPointOptions("\nCentro da rotatoria: "));
        if (centerResult.Status != PromptStatus.OK)
            return;

        Point3d center = centerResult.Value.TransformBy(ed.CurrentUserCoordinateSystem);

        RoadSection? section = PickSection(doc, "Secao do anel (pista de giro)");
        if (section is null)
            return;

        PromptDistanceOptions radiusOptions = new("\nRaio da ilha central, na face do meio-fio <15>: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
            BasePoint = centerResult.Value,
            UseBasePoint = true
        };

        PromptDoubleResult radiusResult = ed.GetDistance(radiusOptions);
        if (radiusResult.Status != PromptStatus.OK && radiusResult.Status != PromptStatus.None)
            return;

        double island = radiusResult.Status == PromptStatus.OK ? radiusResult.Value : 15.0;
        double axisRadius = island + section.EdgeOffset(EdgeLevel.FaceMF);

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            ViarioStore.EnsureLayers(tr, db);

            AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);

            AcadDb.Circle ring = new(new Point3d(center.X, center.Y, 0), Vector3d.ZAxis, axisRadius);
            ring.SetDatabaseDefaults(db);
            ring.Layer = ViarioStore.LayerAxis;
            ms.AppendEntity(ring);
            tr.AddNewlyCreatedDBObject(ring, true);
            ViarioStore.SetAxis(ring, section.Id, null);
            tr.Commit();
        }

        ed.WriteMessage(
            $"\n  Rotatoria criada: ilha central R={Num(island)} m, eixo do anel R={Num(axisRadius)} m, pista de giro {Num(section.Pista)} m." +
            "\n  Lance os ramos ate o anel: as pontas sao capturadas no eixo do anel e as entradas se formam sozinhas." +
            "\n  Raio de entrada/saida: " + C3DCommands.Viario.Raio + ".");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Raios (esquina, ilha, eixo) com preview
    // ------------------------------------------------------------------

    private static void EditRadius(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointOptions options = new("\nClique perto da esquina (ou de um vertice do eixo) ou [Ilha/Eixo/Limpar]: ");
        options.Keywords.Add("Ilha");
        options.Keywords.Add("Eixo");
        options.Keywords.Add("Limpar");
        options.AppendKeywordsToMessage = false;

        PromptPointResult pick = ed.GetPoint(options);
        if (pick.Status == PromptStatus.Keyword)
        {
            switch (pick.StringResult)
            {
                case "Ilha": EditIslandRadius(doc); break;
                case "Eixo": EditAxisRadius(doc); break;
                default: ClearCornerRadii(doc); break;
            }

            return;
        }

        if (pick.Status != PromptStatus.OK)
            return;

        Point3d wcs = pick.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 p = new(wcs.X, wcs.Y);

        // Clique dentro de uma ilha canalizadora (gota, ilha da faixa livre): raio dos cantos dela.
        if (ViarioVertexCommands.TryEditChannelIsland(doc, p))
            return;

        (List<AxisInput> axes, ViarioResult result, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);
        CornerInfo? corner = result.Corners
            .OrderBy(c => DistanceToCorner(c, p))
            .FirstOrDefault();

        // Vertice do proprio eixo (curva de uma via, canto de quadra) mais perto que a esquina?
        VertexPick? vertex = ViarioVertexCommands.FindVertex(db, axes, p, 30.0);
        if (vertex is not null && (corner is null || vertex.Distance < DistanceToCorner(corner, p)))
        {
            ViarioVertexCommands.EditVertex(doc, vertex, p, axes, ViarioSync.EngineSettingsFor(db), keys);
            return;
        }

        if (corner is null || DistanceToCorner(corner, p) > Math.Max(30.0, corner.Radius * 3.0))
        {
            ed.WriteMessage("\nNenhuma esquina perto do ponto clicado.");
            return;
        }

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        ed.WriteMessage($"\n  Esquina: raio atual {Num(corner.Radius)} m." +
                        (corner.Limit.Length > 0 ? $" Pedido {Num(corner.Requested)} m nao coube: {corner.Limit}." : ""));

        double? value = DragRadius(ed, axes, engine, keys, corner.Corner, corner.Radius,
            r => (axes, With(engine, new[] { corner.Key }, r)), out bool toDefault);

        if (value is null && !toDefault)
            return;

        Dictionary<string, double> radii = ViarioStore.LoadCornerRadii(db);
        if (toDefault)
            radii.Remove(corner.Key);
        else
            radii[corner.Key] = value!.Value;

        SaveRadiiAndRegenerate(doc, radii);

        // Diz na hora se o raio pedido nao coube e por que.
        (_, ViarioResult after, _) = ViarioSync.ComputeCurrent(db);
        CornerInfo? applied = after.Corners.FirstOrDefault(c => c.Key == corner.Key);
        if (applied is not null && applied.Limit.Length > 0)
            ed.WriteMessage($"\n  Esquina: aplicado {Num(applied.Radius)} m em vez de {Num(applied.Requested)} m - {applied.Limit}.");
        else if (applied is not null)
            ed.WriteMessage($"\n  Esquina: raio {Num(applied.Radius)} m.");
    }

    private static void EditIslandRadius(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptPointResult pick = ed.GetPoint(new PromptPointOptions("\nClique dentro da ilha (quadra, gota ou ilha da faixa livre): "));
        if (pick.Status != PromptStatus.OK)
            return;

        Point3d wcs = pick.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 p = new(wcs.X, wcs.Y);

        if (ViarioVertexCommands.TryEditChannelIsland(doc, p))
            return;

        (List<AxisInput> axes, ViarioResult result, HashSet<string> keys) = ViarioSync.ComputeCurrent(db);

        OutputLine? island = result.Lines
            .Where(l => l.Level == EdgeLevel.FaceMF && l.Closed && Contains(l, p))
            .OrderBy(l => Area(l))
            .FirstOrDefault();

        if (island is null)
        {
            ed.WriteMessage("\nO ponto nao esta dentro de uma ilha/quadra fechada pela face do meio-fio.");
            return;
        }

        List<CornerInfo> corners = result.Corners
            .Where(c => c.Arc is not null && DistanceToLine(island, c.Arc.PointAt(0.5)) < 1e-3)
            .ToList();

        // Cantos do proprio eixo (vertices de polilinha) que dobram em volta da ilha.
        List<ViarioVertexCommands.IslandVertices> vertexSets = ViarioVertexCommands.FindIslandVertices(db, axes, island.Segs);
        int vertexCount = vertexSets.Sum(v => v.Corners.Count);

        if (corners.Count == 0 && vertexCount == 0)
        {
            ed.WriteMessage("\nEssa ilha nao tem esquinas nem cantos de eixo para concordar.");
            return;
        }

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        string[] cornerKeys = corners.Select(c => c.Key).ToArray();
        List<double> currents = corners.Select(c => c.Radius)
            .Concat(vertexSets.SelectMany(v => v.Corners).Select(c => c.CurrentFace)).ToList();
        double current = currents.Count > 0 ? currents.Average() : 0.0;
        ed.WriteMessage($"\n  Ilha com {corners.Count} esquina(s) e {vertexCount} canto(s) de eixo, raio medio {Num(current)} m.");

        List<AxisInput> AxesWith(double r)
        {
            if (vertexCount == 0)
                return axes;
            Dictionary<string, GPath> paths = new();
            foreach (ViarioVertexCommands.IslandVertices set in vertexSets)
            {
                List<(V2 P, double Bulge)>? verts = ViarioVertexGeometry.ApplyMany(set.Vertices, set.Closed, set.Face,
                    set.Corners.Select(c => (c.Pi, c.Inner)), r, out _);
                if (verts is not null)
                    paths[set.AxisKey] = ViarioVertexGeometry.PathOf(verts, set.Closed);
            }

            return axes.Select(a => paths.TryGetValue(a.Key, out GPath? g) ? new AxisInput(a.Key, g, a.Section, a.Radius, a.Events) : a).ToList();
        }

        double? value = DragRadius(ed, axes, engine, keys, p, current,
            r => (AxesWith(r), With(engine, cornerKeys, r)), out bool toDefault);

        if (value is null && !toDefault)
            return;

        // Cantos de eixo: Padrao = canto vivo.
        double vertexRadius = toDefault ? 0.0 : value!.Value;
        List<string> problems = new();
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ViarioVertexCommands.IslandVertices set in vertexSets)
            {
                List<(V2 P, double Bulge)>? verts = ViarioVertexGeometry.ApplyMany(set.Vertices, set.Closed, set.Face,
                    set.Corners.Select(c => (c.Pi, c.Inner)), vertexRadius, out string? error);
                if (verts is null)
                {
                    problems.Add(error ?? "canto nao concordado");
                    continue;
                }

                ViarioVertexCommands.WriteVertices((AcadDb.Polyline)tr.GetObject(set.Id, AcadDb.OpenMode.ForWrite), verts);
            }

            tr.Commit();
        }

        foreach (string problem in problems.Distinct())
            ed.WriteMessage("\n  [AVISO] " + problem);

        Dictionary<string, double> radii = ViarioStore.LoadCornerRadii(db);
        foreach (string key in cornerKeys)
        {
            if (toDefault)
                radii.Remove(key);
            else
                radii[key] = value!.Value;
        }

        SaveRadiiAndRegenerate(doc, radii);
    }

    private static void EditAxisRadius(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptEntityOptions options = new("\nSelecione o eixo: ");
        options.SetRejectMessage("\nSelecione um eixo da rede.");
        options.AddAllowedClass(typeof(AcadDb.Curve), false);
        PromptEntityResult pick = ed.GetEntity(options);
        if (pick.Status != PromptStatus.OK)
            return;

        string sectionId;
        double? radius;
        string handle;
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            AcadDb.DBObject obj = tr.GetObject(pick.ObjectId, AcadDb.OpenMode.ForRead);
            if (!ViarioStore.ReadAxis(obj, out sectionId, out radius))
            {
                ed.WriteMessage("\nO objeto nao e um eixo da rede.");
                return;
            }

            handle = obj.Handle.ToString() ?? string.Empty;
        }

        (List<AxisInput> axes, HashSet<string> keys) = ViarioSync.CurrentAxes(db);
        AxisInput? target = axes.FirstOrDefault(a => a.Key == handle);
        if (target is null)
            return;

        EngineSettings engine = ViarioSync.EngineSettingsFor(db);
        double current = radius ?? target.Section.Raio;
        ed.WriteMessage($"\n  Eixo: raio {Num(current)} m" + (radius is null ? " (da secao)." : " (especifico do eixo)."));

        Point3d wcs = pick.PickedPoint.TransformBy(ed.CurrentUserCoordinateSystem);
        V2 basePoint = target.Path.Project(new V2(wcs.X, wcs.Y)).Point;

        // Raio do eixo vale para todas as esquinas dele: sem os especificos dessas esquinas.
        EngineSettings withoutCorners = engine.Clone();
        withoutCorners.CornerRadius = engine.CornerRadius
            .Where(kv => !CornerHasAxis(kv.Key, handle))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        double? value = DragRadius(ed, axes, engine, keys, basePoint, current,
            r => (axes.Select(a => a.Key == handle ? a.With((double?)r) : a).ToList(), withoutCorners),
            out bool toDefault);

        if (value is null && !toDefault)
            return;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            AcadDb.Entity entity = (AcadDb.Entity)tr.GetObject(pick.ObjectId, AcadDb.OpenMode.ForWrite);
            ViarioStore.UpdateAxis(entity, radius: toDefault ? null : value, clearRadius: toDefault);
            ViarioStore.SaveCornerRadii(tr, db, withoutCorners.CornerRadius);
            tr.Commit();
        }

        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void ClearCornerRadii(Document doc)
    {
        PromptKeywordOptions confirm = new(
            "\nApagar todos os raios especificos de esquina/ilha (voltam ao raio da secao ou do eixo)? [Sim/Nao] <Nao>: ",
            "Sim Nao")
        {
            AllowNone = true
        };

        PromptResult answer = doc.Editor.GetKeywords(confirm);
        if (answer.Status == PromptStatus.OK && answer.StringResult == "Sim")
            SaveRadiiAndRegenerate(doc, new Dictionary<string, double>());
    }

    private static void SaveRadiiAndRegenerate(Document doc, Dictionary<string, double> radii)
    {
        AcadDb.Database db = doc.Database;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveCornerRadii(tr, db, radii);
            tr.Commit();
        }

        ViarioSync.Regenerate(doc, quiet: false);
    }

    internal static bool CornerHasAxis(string cornerKey, string axisKey) =>
        cornerKey.Split('#')[0].Split('|').Any(part => part.Length > 1 && part[..^1] == axisKey);

    private static EngineSettings With(EngineSettings engine, IEnumerable<string> keys, double radius)
    {
        EngineSettings result = engine.Clone();
        foreach (string key in keys)
            result.CornerRadius[key] = radius;
        return result;
    }

    /// <summary>Jig de raio: distancia do mouse ao ponto base = raio. Null = cancelado.</summary>
    private static double? DragRadius(
        Editor ed, List<AxisInput> axes, EngineSettings engine, HashSet<string> keys, V2 basePoint, double current,
        Func<double, (List<AxisInput>, EngineSettings)> apply, out bool toDefault)
    {
        toDefault = false;
        ValueJig jig = new("Raio", keys, basePoint, current, apply);
        PromptResult result = ed.Drag(jig);

        if (jig.ToDefault)
        {
            toDefault = true;
            return null;
        }

        if (result.Status == PromptStatus.None)
            return current;

        return result.Status == PromptStatus.OK ? jig.Value : null;
    }

    private static double DistanceToCorner(CornerInfo c, V2 p) =>
        c.Arc is not null ? Math.Min(c.Arc.Closest(p).Dist, V2.Dist(c.Corner, p)) : V2.Dist(c.Corner, p);

    private static double DistanceToLine(OutputLine line, V2 p) =>
        line.Segs.Min(s => s.Closest(p).Dist);

    private static List<V2> Sample(OutputLine line)
    {
        List<V2> pts = new();
        foreach (Seg s in line.Segs)
        {
            int n = s.IsArc ? 16 : 1;
            for (int k = 0; k < n; k++)
                pts.Add(s.PointAt(k / (double)n));
        }

        return pts;
    }

    private static bool Contains(OutputLine line, V2 p)
    {
        List<V2> pts = Sample(line);
        bool inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            if (((pts[i].Y > p.Y) != (pts[j].Y > p.Y)) &&
                (p.X < ((pts[j].X - pts[i].X) * (p.Y - pts[i].Y) / (pts[j].Y - pts[i].Y)) + pts[i].X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double Area(OutputLine line)
    {
        List<V2> pts = Sample(line);
        double area = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            area += V2.Cross(pts[j], pts[i]);
        return Math.Abs(area / 2.0);
    }

    // ------------------------------------------------------------------
    // Adicionar / Atribuir / Remover / Desvincular
    // ------------------------------------------------------------------

    private static AcadDb.ObjectId[]? SelectCurves(Editor ed, string message)
    {
        AcadDb.TypedValue[] filter =
        {
            new((int)AcadDb.DxfCode.Start, "LWPOLYLINE,LINE,ARC,CIRCLE")
        };

        PromptSelectionOptions options = new() { MessageForAdding = message };
        PromptSelectionResult result = ed.GetSelection(options, new SelectionFilter(filter));
        return result.Status == PromptStatus.OK ? result.Value.GetObjectIds() : null;
    }

    private static void AddAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadSection section = CurrentSection(db, settings);

        AcadDb.ObjectId[]? ids = SelectCurves(ed,
            "\nSelecione os eixos existentes (polilinhas, linhas, arcos, circulos) para a secao " + section.Name + ": ");
        if (ids is null)
            return;

        int count = 0, skipped = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (ViarioStore.IsGenerated(curve) || ViarioStore.ToPath(curve) is null)
                {
                    skipped++;
                    continue;
                }

                ViarioStore.SetAxis(curve, section.Id, null);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) adicionado(s) com a secao {section.Name}." +
                        (skipped > 0 ? $" Ignorados: {skipped} (linha gerada ou fora do plano XY)." : ""));
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void AssignSection(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        AcadDb.ObjectId[]? ids = SelectCurves(ed, "\nSelecione os eixos que vao mudar de secao: ");
        if (ids is null)
            return;

        RoadSection? section = PickSection(doc, "Secao para os eixos selecionados");
        if (section is null)
            return;

        int count = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.EnsureRegApp(tr, db);
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (!ViarioStore.IsAxis(curve, out _))
                    continue;

                ViarioStore.UpdateAxis(curve, sectionId: section.Id);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) agora com a secao {section.Name}.");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void RemoveAxes(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        AcadDb.ObjectId[]? ids = SelectCurves(ed,
            "\nSelecione os eixos a tirar da rede (a polilinha do eixo continua no desenho): ");
        if (ids is null)
            return;

        int count = 0;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (AcadDb.ObjectId id in ids)
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (!ViarioStore.IsAxis(curve, out _))
                    continue;

                ViarioStore.ClearTag(curve);
                count++;
            }

            tr.Commit();
        }

        ed.WriteMessage($"\n  {count} eixo(s) removido(s) da rede.");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void Release(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        PromptKeywordOptions options = new(
            "\nDesvincular [Selecionar/Revincular] <Selecionar>: ",
            "Selecionar Revincular")
        {
            AllowNone = true
        };

        PromptResult choice = ed.GetKeywords(options);
        if (choice.Status == PromptStatus.Cancel || choice.Status == PromptStatus.Error)
            return;

        if (choice.Status == PromptStatus.OK && choice.StringResult == "Revincular")
        {
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                ViarioStore.SaveReleased(tr, db, Array.Empty<string>());
                tr.Commit();
            }

            ed.WriteMessage("\n  Linhas revinculadas: o plugin volta a gerar todas. As polilinhas desvinculadas continuam no desenho (apague as que nao quiser).");
            ViarioSync.Regenerate(doc, quiet: false);
            return;
        }

        AcadDb.TypedValue[] filter = { new((int)AcadDb.DxfCode.Start, "LWPOLYLINE") };
        PromptSelectionResult selection = ed.GetSelection(
            new PromptSelectionOptions { MessageForAdding = "\nSelecione as linhas geradas a desvincular (viram polilinhas comuns): " },
            new SelectionFilter(filter));
        if (selection.Status != PromptStatus.OK)
            return;

        HashSet<string> released = ViarioStore.LoadReleased(db);
        int count = 0;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (AcadDb.ObjectId id in selection.Value.GetObjectIds())
            {
                AcadDb.Polyline pl = (AcadDb.Polyline)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                if (ViarioStore.ReadTag(pl, out string levelText) != ViarioStore.TagGenerated)
                    continue;

                released.Add(ViarioStore.GeometryKey(pl, ViarioStore.ParseLevel(levelText)));
                ViarioStore.ClearTag(pl);
                count++;
            }

            ViarioStore.SaveReleased(tr, db, released);
            tr.Commit();
        }

        ed.WriteMessage(
            $"\n  {count} linha(s) desvinculada(s): pode editar a vontade, o plugin nao recria nem apaga." +
            "\n  Se a rede mudar naquele trecho, a linha nova aparece por cima. Desvincular > Revincular desfaz.");
    }

    // ------------------------------------------------------------------
    // Secoes
    // ------------------------------------------------------------------

    private static RoadSection CurrentSection(AcadDb.Database db, ViarioStore.Settings settings)
    {
        List<RoadSection> sections = ViarioStore.LoadSections(db);
        return sections.FirstOrDefault(p => p.Id == settings.CurrentSectionId) ?? sections[0];
    }

    private static string Describe(RoadSection s) =>
        $"{s.Name} [{s.Id}, {s.Tipo}]: pista {Num(s.Pista)} ({s.FaixasPorSentido * 2} faixas)" +
        (s.Canteiro > 0 ? $" | canteiro {Num(s.Canteiro)}" : "") +
        $" | sarjeta {Num(s.Sarjeta)}{LadosSufixo(s.LadosSarjeta)} | meio-fio {Num(s.MeioFio)}{LadosSufixo(s.LadosMeioFio)}" +
        $" | calcada {Num(s.Calcada)}{LadosSufixo(s.LadosCalcada)} | raio {Num(s.Raio)}" +
        $" (total {Num(s.TotalWidth)} m)";

    private static string LadosSufixo(Lados l) => l == Lados.Ambos ? "" : " (" + ViarioStore.LadosNome(l) + ")";

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void ListSections(Document doc)
    {
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        foreach (RoadSection s in ViarioStore.LoadSections(doc.Database))
            doc.Editor.WriteMessage("\n  " + Describe(s) + (s.Id == settings.CurrentSectionId ? "  (corrente)" : ""));
    }

    private static void ManageSections(Document doc)
    {
        Editor ed = doc.Editor;
        ListSections(doc);

        PromptKeywordOptions options = new(
            "\nSecoes [Corrente/Nova/Editar/eXcluir] <Corrente>: ",
            "Corrente Nova Editar eXcluir")
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
            return;

        switch (result.Status == PromptStatus.OK ? result.StringResult : "Corrente")
        {
            case "Nova": NewSection(doc); break;
            case "Editar": EditSection(doc); break;
            case "eXcluir": DeleteSection(doc); break;
            default: ChooseCurrentSection(doc); break;
        }
    }

    private static RoadSection? PickSection(Document doc, string label)
    {
        List<RoadSection> sections = ViarioStore.LoadSections(doc.Database);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(doc.Database);
        RoadSection current = sections.FirstOrDefault(p => p.Id == settings.CurrentSectionId) ?? sections[0];

        ListSections(doc);
        PromptStringOptions options = new("\n" + label + " (codigo ou nome) <" + current.Id + ">: ")
        {
            AllowSpaces = true
        };

        PromptResult result = doc.Editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return null;

        string text = result.StringResult.Trim();
        if (text.Length == 0)
            return current;

        RoadSection? found = sections.FirstOrDefault(p => p.Id.Equals(text, StringComparison.OrdinalIgnoreCase)) ??
                             sections.FirstOrDefault(p => p.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            doc.Editor.WriteMessage("\nSecao nao encontrada.");
        return found;
    }

    private static void ChooseCurrentSection(Document doc)
    {
        RoadSection? section = PickSection(doc, "Secao corrente");
        if (section is null)
            return;

        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        settings.CurrentSectionId = section.Id;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Secao corrente: " + Describe(section));
    }

    /// <summary>Grava no DWG as secoes padrao/antigas na primeira alteracao.</summary>
    private static void EnsureSectionsSaved(AcadDb.Transaction tr, AcadDb.Database db)
    {
        foreach (RoadSection s in ViarioStore.LoadSections(db))
            ViarioStore.SaveSection(tr, db, s);
    }

    private static bool AskSectionValues(Editor ed, RoadSection s)
    {
        PromptStringOptions name = new("\nNome da secao <" + s.Name + ">: ") { AllowSpaces = true };
        PromptResult nameResult = ed.GetString(name);
        if (nameResult.Status != PromptStatus.OK)
            return false;
        if (nameResult.StringResult.Trim().Length > 0)
            s.Name = nameResult.StringResult.Trim();

        double? pista = AskDouble(ed, "Largura total de rolamento, os dois sentidos (m)", s.Pista, allowZero: false);
        if (pista is null) return false;
        double? canteiro = AskDouble(ed, "Canteiro central (m, 0 = sem canteiro)", s.Canteiro, allowZero: true);
        if (canteiro is null) return false;
        double? sarjeta = AskDouble(ed, "Sarjeta de cada lado (m, 0 = sem sarjeta)", s.Sarjeta, allowZero: true);
        if (sarjeta is null) return false;
        double? meioFio = AskDouble(ed, "Meio-fio de cada lado, face as costas (m, 0 = sem)", s.MeioFio, allowZero: true);
        if (meioFio is null) return false;
        double? calcada = AskDouble(ed, "Calcada de cada lado (m, 0 = sem calcada)", s.Calcada, allowZero: true);
        if (calcada is null) return false;
        double? raio = AskDouble(ed, "Raio de esquina na face do meio-fio (m, 0 = canto vivo)", s.Raio, allowZero: true);
        if (raio is null) return false;

        // Lados: sarjeta, meio-fio e calcada podem existir so de um lado (pista de mao unica, bordo
        // junto de um talude...). Esquerda/direita no sentido do eixo.
        string ladosAtual = s.Simetrica ? "Ambos" : "Detalhar";
        PromptKeywordOptions ladosOpt = new(
            "\nSarjeta, meio-fio e calcada em que lados (no sentido do eixo) [Ambos/Esquerda/Direita/Detalhar] <" + ladosAtual + ">: ",
            "Ambos Esquerda Direita Detalhar")
        {
            AllowNone = true
        };
        PromptResult ladosRes = ed.GetKeywords(ladosOpt);
        if (ladosRes.Status != PromptStatus.OK && ladosRes.Status != PromptStatus.None)
            return false;
        string lados = ladosRes.Status == PromptStatus.OK ? ladosRes.StringResult : ladosAtual;
        switch (lados)
        {
            case "Ambos": s.LadosSarjeta = s.LadosMeioFio = s.LadosCalcada = Lados.Ambos; break;
            case "Esquerda": s.LadosSarjeta = s.LadosMeioFio = s.LadosCalcada = Lados.Esquerda; break;
            case "Direita": s.LadosSarjeta = s.LadosMeioFio = s.LadosCalcada = Lados.Direita; break;
            default:
                Lados? ls = AskLados(ed, "Sarjeta", s.LadosSarjeta);
                if (ls is null) return false;
                Lados? lm = AskLados(ed, "Meio-fio", s.LadosMeioFio);
                if (lm is null) return false;
                Lados? lc = AskLados(ed, "Calcada", s.LadosCalcada);
                if (lc is null) return false;
                s.LadosSarjeta = ls.Value;
                s.LadosMeioFio = lm.Value;
                s.LadosCalcada = lc.Value;
                break;
        }

        // Largura de pista mudou pela linha de comando: faixas voltam ao automatico.
        if (Math.Abs(pista.Value - s.Pista) > 1e-6 || Math.Abs(canteiro.Value - s.Canteiro) > 1e-6)
            s.Faixas = 0;
        s.Pista = pista.Value;
        s.Canteiro = canteiro.Value;
        s.Sarjeta = sarjeta.Value;
        s.MeioFio = meioFio.Value;
        s.Calcada = calcada.Value;
        s.Raio = raio.Value;
        return true;
    }

    private static Lados? AskLados(Editor ed, string label, Lados current)
    {
        string atual = current switch { Lados.Esquerda => "Esquerda", Lados.Direita => "Direita", Lados.Nenhum => "Nenhum", _ => "Ambos" };
        PromptKeywordOptions opt = new("\n" + label + " [Ambos/Esquerda/Direita/Nenhum] <" + atual + ">: ", "Ambos Esquerda Direita Nenhum")
        {
            AllowNone = true
        };
        PromptResult r = ed.GetKeywords(opt);
        if (r.Status == PromptStatus.None)
            return current;
        if (r.Status != PromptStatus.OK)
            return null;
        return r.StringResult switch { "Esquerda" => Lados.Esquerda, "Direita" => Lados.Direita, "Nenhum" => Lados.Nenhum, _ => Lados.Ambos };
    }

    private static double? AskDouble(Editor ed, string label, double current, bool allowZero)
    {
        PromptDoubleOptions options = new("\n" + label + " <" + Num(current) + ">: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = allowZero
        };

        PromptDoubleResult result = ed.GetDouble(options);
        if (result.Status == PromptStatus.None)
            return current;
        return result.Status == PromptStatus.OK ? result.Value : null;
    }

    private static void NewSection(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<RoadSection> sections = ViarioStore.LoadSections(db);
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadSection basis = sections.FirstOrDefault(p => p.Id == settings.CurrentSectionId) ?? sections[0];

        RoadSection section = basis.Clone();
        section.Id = ViarioStore.NextSectionId(sections);
        section.Name = "Secao " + section.Id;

        if (!AskSectionValues(doc.Editor, section))
            return;

        settings.CurrentSectionId = section.Id;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.SaveSection(tr, db, section);
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Secao criada e definida como corrente: " + Describe(section));
    }

    private static void EditSection(Document doc)
    {
        RoadSection? section = PickSection(doc, "Secao a editar");
        if (section is null || !AskSectionValues(doc.Editor, section))
            return;

        AcadDb.Database db = doc.Database;
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.SaveSection(tr, db, section);
            tr.Commit();
        }

        doc.Editor.WriteMessage("\n  Secao atualizada: " + Describe(section));
        ViarioSync.Regenerate(doc, quiet: false);
    }

    private static void DeleteSection(Document doc)
    {
        AcadDb.Database db = doc.Database;
        List<RoadSection> sections = ViarioStore.LoadSections(db);
        if (sections.Count <= 1)
        {
            doc.Editor.WriteMessage("\nE preciso manter pelo menos uma secao.");
            return;
        }

        RoadSection? section = PickSection(doc, "Secao a excluir");
        if (section is null)
            return;

        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);
        RoadSection replacement = sections.First(p => p.Id != section.Id);

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            EnsureSectionsSaved(tr, db);
            ViarioStore.DeleteSection(tr, db, section.Id);
            if (settings.CurrentSectionId == section.Id)
            {
                settings.CurrentSectionId = replacement.Id;
                ViarioStore.SaveSettings(tr, db, settings);
            }

            tr.Commit();
        }

        doc.Editor.WriteMessage(
            "\n  Secao excluida. Eixos que usavam " + section.Name + " passam a usar " + replacement.Name + ".");
        ViarioSync.Regenerate(doc, quiet: false);
    }

    // ------------------------------------------------------------------
    // Configuracao
    // ------------------------------------------------------------------

    private static void Configure(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        ViarioStore.Settings settings = ViarioStore.LoadSettings(db);

        double? snap = AskDouble(ed, "Tolerancia de encontro entre eixos (ponta encostada em outro eixo)", settings.SnapTolerance, allowZero: false);
        if (snap is null) return;
        double? capture = AskDouble(ed, "Captura das pontas ao lancar eixos (0 = desligada)", settings.Capture, allowZero: true);
        if (capture is null) return;
        double? setback = AskDouble(ed, "Recuo do nariz do canteiro em relacao a pista transversal", settings.MedianSetback, allowZero: true);
        if (setback is null) return;

        settings.SnapTolerance = snap.Value;
        settings.Capture = capture.Value;
        settings.MedianSetback = setback.Value;

        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            ViarioStore.SaveSettings(tr, db, settings);
            tr.Commit();
        }

        ed.WriteMessage("\n  Configuracao salva no desenho.");
        ViarioSync.Regenerate(doc, quiet: false);
    }
}

/// <summary>Desenho comum dos jigs: linhas da rede que mudam em relacao ao desenho atual.</summary>
internal static class ViarioPreview
{
    /// <summary>
    /// Eixos que entram na previa ao vivo: todos em rede pequena; em rede grande, so os
    /// que passam perto do ponto (a previa de cada movimento do mouse nao refaz a cidade toda).
    /// </summary>
    public static List<AxisInput> Near(List<AxisInput> axes, V2 point, double reach = 300.0)
    {
        if (axes.Count <= 150)
            return axes;
        Box2 box = Box2.Empty.Add(point).Inflate(reach);
        return axes.Where(a => a.Path.Bounds().Intersects(box)).ToList();
    }

    public static void DrawChanges(WorldDraw draw, ViarioResult result, HashSet<string> existingKeys)
    {
        foreach (OutputLine line in result.Lines)
        {
            if (existingKeys.Contains(ViarioStore.GeometryKey(line)))
                continue;

            using AcadDb.Polyline pl = ViarioStore.ToPolyline(line.Segs, line.Closed);
            pl.ColorIndex = line.Level switch
            {
                EdgeLevel.FaceMF => 4,
                EdgeLevel.Canteiro => 30,
                EdgeLevel.Ilha => 6,
                _ => 9
            };
            draw.Geometry.Draw(pl);
        }
    }
}

/// <summary>
/// Jig do eixo: a cada movimento do mouse recalcula a rede com o eixo em
/// andamento (reta ou arco tangente) e desenha o que vai mudar.
/// </summary>
internal sealed class AxisJig : DrawJig
{
    private readonly List<V2> _points;
    private readonly List<double> _bulges;
    private readonly bool _arcMode;
    private readonly bool _chained;
    private readonly double _tol;
    private readonly RoadSection _section;
    private readonly List<AxisInput> _existing;
    private readonly HashSet<string> _existingKeys;
    private readonly EngineSettings _engine;
    private readonly double _capture;

    public AxisJig(List<V2> points, List<double> bulges, bool arcMode, bool chained, double tol, RoadSection section,
        List<AxisInput> existing, HashSet<string> existingKeys, EngineSettings engine, double capture)
    {
        _points = points;
        _bulges = bulges;
        _arcMode = arcMode;
        _chained = chained;
        _tol = tol;
        _section = section;
        _existing = existing;
        _existingKeys = existingKeys;
        _engine = engine;
        _capture = capture;
        Current = points[^1];
        Picked = points[^1];
    }

    /// <summary>Ponto final do trecho em andamento (ja resolvido: projetado na direcao, no modo concordante).</summary>
    public V2 Current { get; private set; }

    /// <summary>Ponto onde o mouse esta (sem resolver), para o clique em cima do ultimo vertice.</summary>
    public V2 Picked { get; private set; }

    public double CurrentBulge { get; private set; }
    public bool Finished { get; private set; }
    public string? Keyword { get; private set; }

    private bool CanArc => _bulges.Count > 0;

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        V2 last = _points[^1];
        bool arc = _arcMode && CanArc;
        string mode = arc ? "arco tangente" : _chained && CanArc ? "reta na direcao corrente" : "reta";
        string keys = "Tangent/Arc" + (_points.Count > 1 ? "/Undo" : "") + (_chained ? "/Free" : "/Chain");
        JigPromptPointOptions options = new(
            "\nProximo ponto (" + mode + ") ou [" + keys + "] <terminar>: ")
        {
            UserInputControls = UserInputControls.Accept3dCoordinates |
                                UserInputControls.NullResponseAccepted |
                                UserInputControls.NoNegativeResponseAccepted,
            BasePoint = new Point3d(last.X, last.Y, 0),
            UseBasePoint = true,
            Cursor = CursorType.RubberBand
        };

        options.Keywords.Add("Tangent");
        options.Keywords.Add("Arc");
        if (_points.Count > 1)
            options.Keywords.Add("Undo");
        options.Keywords.Add(_chained ? "Free" : "Chain");

        PromptPointResult result = prompts.AcquirePoint(options);

        switch (result.Status)
        {
            case PromptStatus.Keyword:
                Keyword = result.StringResult;
                return SamplerStatus.OK;
            case PromptStatus.None:
                Finished = true;
                return SamplerStatus.Cancel;
            case PromptStatus.OK:
                break;
            default:
                return SamplerStatus.Cancel;
        }

        V2 p = new(result.Value.X, result.Value.Y);
        if (V2.Dist(p, Picked) < 1e-9)
            return SamplerStatus.NoChange;

        Picked = p;
        (Current, CurrentBulge) = Resolve(p);
        return SamplerStatus.OK;
    }

    /// <summary>Fim e bulge do trecho em andamento para o ponto p do mouse.</summary>
    private (V2 End, double Bulge) Resolve(V2 p)
    {
        V2 last = _points[^1];
        bool arc = _arcMode && CanArc;

        if (_chained && CanArc)
        {
            // Concordancia: resolve a cadeia inteira com o trecho novo no fim.
            List<V2> pts = new(_points) { p };
            List<double> bulges = new(_bulges) { arc ? 1e-3 : 0.0 };
            (List<V2> np, List<double> nb, _) = ViarioChain.Solve(pts, bulges);
            return (np[^1], nb[^1]);
        }

        return (p, arc ? TangentBulge(EndTangent(), last, p) : 0.0);
    }

    private V2 EndTangent() =>
        new Seg(_points[^2], _points[^1], _bulges[^1]).TangentAt(1.0);

    /// <summary>Bulge do arco tangente a t que sai de a e chega em b.</summary>
    public static double TangentBulge(V2 tangent, V2 a, V2 b) => ViarioChain.TangentBulge(tangent, a, b);

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            List<V2> pts = new(_points);
            List<double> bulges = new(_bulges);
            if (V2.Dist(pts[^1], Current) > 1e-6)
            {
                pts.Add(Current);
                bulges.Add(CurrentBulge);
            }

            if (pts.Count < 2)
                return true;

            pts = Capture(pts, _existing, _capture);
            GPath path = PathOf(pts, bulges);
            if (path.IsEmpty)
                return true;

            using (AcadDb.Polyline axis = ViarioStore.ToPolyline(path.Segs, closed: false))
            {
                axis.ColorIndex = 1;
                draw.Geometry.Draw(axis);
            }

            if (_points.Count > 1)
            {
                // Alvo do "clique em cima do ultimo vertice desfaz".
                bool over = V2.Dist(Picked, _points[^1]) <= _tol;
                using AcadDb.Circle dot = new(new Point3d(_points[^1].X, _points[^1].Y, 0), Vector3d.ZAxis, Math.Max(_tol * 0.6, 1e-3));
                dot.ColorIndex = over ? 2 : 8;
                draw.Geometry.Draw(dot);
            }

            List<AxisInput> axes = new(PreviewSubset(path));
            axes.Add(new AxisInput("__NOVO__", path, _section));
            ViarioResult result = ViarioEngine.Compute(axes, _engine);
            ViarioPreview.DrawChanges(draw, result, _existingKeys);

            double size = Math.Max(_section.Pista * 0.25, 0.5);
            foreach (NodeInfo node in result.Nodes.Where(n => n.AxisKeys.Contains("__NOVO__")))
            {
                using AcadDb.Circle marker = new(new Point3d(node.Point.X, node.Point.Y, 0), Vector3d.ZAxis, size);
                marker.ColorIndex = 3;
                draw.Geometry.Draw(marker);
            }
        }
        catch
        {
            // Preview nunca derruba o comando.
        }

        return true;
    }

    private IEnumerable<AxisInput> PreviewSubset(GPath path)
    {
        if (_existing.Count <= 150)
            return _existing;

        Box2 box = path.Bounds().Inflate(200.0);
        return _existing.Where(a => a.Path.Bounds().Intersects(box));
    }

    public static GPath PathOf(List<V2> pts, List<double> bulges)
    {
        List<Seg> segs = new();
        for (int i = 1; i < pts.Count; i++)
            segs.Add(new Seg(pts[i - 1], pts[i], i - 1 < bulges.Count ? bulges[i - 1] : 0.0));
        return new GPath(segs);
    }

    /// <summary>
    /// Captura para eixo concordante: a ultima ponta e levada ate o eixo vizinho ao
    /// longo da propria direcao do ultimo trecho (prolonga/encurta a reta), para
    /// nao quebrar a concordancia; a primeira ponta vai pela perpendicular.
    /// </summary>
    public static List<V2> CaptureAlong(List<V2> pts, List<double> bulges, List<AxisInput> existing, double capture)
    {
        List<V2> result = Capture(pts, existing, capture);
        if (capture <= 0 || existing.Count == 0 || pts.Count < 2)
            return result;

        int n = pts.Count;
        bool lastIsArc = bulges.Count >= n - 1 && Math.Abs(bulges[n - 2]) > 1e-9;
        if (lastIsArc || V2.Dist(result[n - 1], pts[n - 1]) < 1e-9)
            return result; // arco no fim (fica a perpendicular) ou nada capturado

        V2 a = pts[n - 2], b = pts[n - 1];
        V2 dir = (b - a).Unit();
        if (dir.Length < 1e-9)
            return result;

        // Reta prolongada alguns comprimentos de captura para os dois lados da ponta.
        double reach = Math.Max(capture * 4.0, 1.0);
        GPath probe = new(new List<Seg> { Seg.Line(b - (dir * reach), b + (dir * reach)) });
        V2? best = null;
        double bestDist = capture * 1.5;
        foreach (AxisInput axis in existing)
        {
            if (!axis.Path.Bounds().Inflate(reach).Intersects(Box2.Empty.Add(b)))
                continue;
            foreach ((_, _, V2 x) in probe.Intersections(axis.Path))
            {
                double d = V2.Dist(x, b);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = x;
                }
            }
        }

        if (best is V2 along)
            result[n - 1] = along;
        return result;
    }

    /// <summary>Puxa a primeira e a ultima ponta para o eixo vizinho mais proximo (dentro da captura).</summary>
    public static List<V2> Capture(List<V2> pts, List<AxisInput> existing, double capture)
    {
        List<V2> result = new(pts);
        if (capture <= 0 || existing.Count == 0 || result.Count < 2)
            return result;

        foreach (int index in new[] { 0, result.Count - 1 })
        {
            V2 p = result[index];
            double best = capture;
            V2? target = null;

            foreach (AxisInput axis in existing)
            {
                if (!axis.Path.Bounds().Inflate(capture).Intersects(Box2.Empty.Add(p)))
                    continue;

                (_, V2 q, double dist) = axis.Path.Project(p);
                if (dist <= best)
                {
                    best = dist;
                    target = q;
                }
            }

            if (target is V2 t)
                result[index] = t;
        }

        return result;
    }
}

/// <summary>
/// Jig de valor (raio, comprimento...): a distancia do mouse ao ponto base e o
/// valor; a rede e recalculada a cada movimento e o que muda aparece na hora.
/// </summary>
internal sealed class ValueJig : DrawJig
{
    private readonly string _label;
    private readonly HashSet<string> _existingKeys;
    private readonly V2 _base;
    private readonly Func<double, (List<AxisInput> Axes, EngineSettings Settings)> _apply;
    private readonly double _initial;

    public ValueJig(string label, HashSet<string> existingKeys, V2 basePoint, double current,
        Func<double, (List<AxisInput>, EngineSettings)> apply)
    {
        _label = label;
        _existingKeys = existingKeys;
        _base = basePoint;
        _apply = apply;
        _initial = current;
        Value = current;
    }

    public double Value { get; private set; }
    public bool ToDefault { get; private set; }

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        JigPromptDistanceOptions options = new(
            "\n" + _label + " <" + _initial.ToString("0.##", CultureInfo.InvariantCulture) + "> (arraste ou digite) ou [Padrao]: ")
        {
            BasePoint = new Point3d(_base.X, _base.Y, 0),
            UseBasePoint = true,
            UserInputControls = UserInputControls.Accept3dCoordinates |
                                UserInputControls.NullResponseAccepted |
                                UserInputControls.NoNegativeResponseAccepted,
            Cursor = CursorType.RubberBand
        };
        options.Keywords.Add("Padrao");

        PromptDoubleResult result = prompts.AcquireDistance(options);

        switch (result.Status)
        {
            case PromptStatus.Keyword:
                ToDefault = true;
                return SamplerStatus.Cancel;
            case PromptStatus.None:
                return SamplerStatus.Cancel;
            case PromptStatus.OK:
                break;
            default:
                return SamplerStatus.Cancel;
        }

        if (Math.Abs(result.Value - Value) < 1e-6)
            return SamplerStatus.NoChange;

        Value = result.Value;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        try
        {
            (List<AxisInput> axes, EngineSettings settings) = _apply(Value);
            ViarioResult result = ViarioEngine.Compute(ViarioPreview.Near(axes, _base), settings);
            ViarioPreview.DrawChanges(draw, result, _existingKeys);
        }
        catch
        {
            // Preview nunca derruba o comando.
        }

        return true;
    }
}
