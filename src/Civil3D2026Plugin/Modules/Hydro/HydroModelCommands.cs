using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Comandos de um clique no model (fase H1): editar fundo, tampa, declividade, trocar familia e
/// tamanho, igualar propriedades, conectar, inverter, mover, inserir e remover estrutura, caminho.
/// Todos aceitam a selecao previa (pickfirst) e viram um UNDO cada.
/// </summary>
public sealed class HydroModelCommands
{
    private const string Module = "Hydro";
    private const CommandFlags Flags = CommandFlags.Modal | CommandFlags.UsePickSet;

    // ------------------------------------------------------------------
    // Apoio
    // ------------------------------------------------------------------

    private static Document? Doc => AcApp.DocumentManager.MdiActiveDocument;

    private static string H(AcadDb.ObjectId id) => id.Handle.ToString();

    private static string N(double v, int d = 3) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    /// <summary>Entidade do tipo pedido: da selecao previa (se for uma so) ou por clique.</summary>
    private static AcadDb.ObjectId? Pick<T>(Editor ed, string prompt, out Point3d picked) where T : AcadDb.Entity
    {
        picked = Point3d.Origin;
        PromptSelectionResult pre = ed.SelectImplied();
        if (pre.Status == PromptStatus.OK && pre.Value.Count == 1)
        {
            AcadDb.ObjectId id = pre.Value[0].ObjectId;
            if (id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(T))))
            {
                ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());
                return id;
            }
        }

        PromptEntityOptions o = new("\n" + prompt);
        o.SetRejectMessage("\nNao e " + (typeof(T) == typeof(CivilDb.Pipe) ? "um tubo" : typeof(T) == typeof(CivilDb.Structure) ? "uma estrutura" : "uma peca") + " da Pipe Network.");
        o.AddAllowedClass(typeof(T), false);
        PromptEntityResult r = ed.GetEntity(o);
        if (r.Status != PromptStatus.OK)
            return null;
        picked = r.PickedPoint;
        return r.ObjectId;
    }

    /// <summary>Varias pecas: selecao previa, pre-selecao do painel ou janela.</summary>
    private static List<AcadDb.ObjectId> PickMany(Editor ed, AcadDb.Database db, string prompt, bool pipes, bool structures)
    {
        List<AcadDb.ObjectId> ids = new();
        List<string>? pre = HydroApply.TakePreselect();
        if (pre is not null)
        {
            foreach (string h in pre)
            {
                if (HydroReader.Id(db, h) is AcadDb.ObjectId id && Accept(id, pipes, structures))
                    ids.Add(id);
            }

            if (ids.Count > 0)
                return ids;
        }

        PromptSelectionResult implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK)
        {
            ids.AddRange(implied.Value.GetObjectIds().Where(id => Accept(id, pipes, structures)));
            ed.SetImpliedSelection(Array.Empty<AcadDb.ObjectId>());
            if (ids.Count > 0)
                return ids;
        }

        PromptSelectionOptions o = new() { MessageForAdding = "\n" + prompt };
        PromptSelectionResult r = ed.GetSelection(o);
        if (r.Status == PromptStatus.OK)
            ids.AddRange(r.Value.GetObjectIds().Where(id => Accept(id, pipes, structures)));
        return ids;
    }

    private static bool Accept(AcadDb.ObjectId id, bool pipes, bool structures) =>
        (pipes && id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Pipe)))) ||
        (structures && id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(CivilDb.Structure))));

    private static double? AskDouble(Editor ed, string prompt, double? def, bool allowNegative = true)
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

    private static string? AskKeyword(Editor ed, string prompt, string def, params string[] keywords)
    {
        PromptKeywordOptions o = new("\n" + prompt) { AllowNone = true };
        foreach (string k in keywords)
            o.Keywords.Add(k);
        o.Keywords.Default = def;
        PromptResult r = ed.GetKeywords(o);
        return r.Status == PromptStatus.OK ? r.StringResult : r.Status == PromptStatus.None ? def : null;
    }

    private static void Run(string what, Action<Document, Editor> body)
    {
        Document? doc = Doc;
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

    private static HydroSettings Settings(AcadDb.Database db) => HydroStore.Load(db);

    // ------------------------------------------------------------------
    // HYFUNDO / HYTAMPA / HYDECLIV
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Hydro.Fundo, Flags)]
    public void Fundo() => Run("HYFUNDO", (doc, ed) =>
    {
        AcadDb.ObjectId? id = Pick<CivilDb.Structure>(ed, "Clique a estrutura para mudar o fundo: ", out _);
        if (id is null)
            return;
        double sump, rim;
        string name;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(id.Value, AcadDb.OpenMode.ForRead);
            sump = s.SumpElevation;
            rim = s.RimElevation;
            name = s.Name;
        }

        HydroSettings st = Settings(doc.Database);
        ed.WriteMessage($"\n{name}: tampa {N(rim)}  fundo {N(sump)}  profundidade {N(rim - sump, 2)} m");
        string? mode = AskKeyword(ed, "Fundo por [Cota/Deslocamento/Altura]", "Cota", "Cota", "Deslocamento", "Altura");
        if (mode is null)
            return;
        double? value = mode switch
        {
            "Cota" => AskDouble(ed, "Cota do fundo", sump),
            "Deslocamento" => AskDouble(ed, "Deslocamento do fundo (+ sobe, - desce) [m]", 0),
            _ => AskDouble(ed, "Altura da estrutura (tampa - fundo) [m]", rim - sump, false)
        };
        if (value is null)
            return;
        double target = mode switch
        {
            "Cota" => value.Value,
            "Deslocamento" => sump + value.Value,
            _ => rim - value.Value
        };

        string? follow = AskKeyword(ed, "O tubo acompanha o fundo? [Sim/Nao]", "Sim", "Sim", "Nao");
        if (follow is null)
            return;
        bool movePipes = follow == "Sim";

        // A tampa nao muda: o fundo anda e a altura da peca acompanha.
        HydroEditor.EditStructure(doc.Database, H(id.Value), null, null, target, st.HoldMode, movePipes);
        ed.WriteMessage($"\n{name}: fundo {N(sump)} -> {N(target)}, profundidade {N(rim - target, 2)} m, tampa {N(rim)} mantida; " +
            (movePipes ? "tubos acompanharam." : "tubos ficaram onde estavam."));
    });

    [CommandMethod(C3DCommands.Hydro.Tampa, Flags)]
    public void Tampa() => Run("HYTAMPA", (doc, ed) =>
    {
        List<AcadDb.ObjectId> ids = PickMany(ed, doc.Database, "Selecione as estruturas (tampa): ", false, true);
        if (ids.Count == 0)
            return;
        string? mode = AskKeyword(ed, "Tampa pela [Superficie/Valor/Rebaixar]", "Superficie", "Superficie", "Valor", "Rebaixar");
        if (mode is null)
            return;
        double? value = null;
        if (mode == "Valor")
            value = AskDouble(ed, "Elevacao da tampa", null);
        else if (mode == "Rebaixar")
            value = AskDouble(ed, "Rebaixar quanto (m, positivo abaixa)", 0.05);
        if (mode != "Superficie" && value is null)
            return;

        int n = 0;
        foreach (AcadDb.ObjectId id in ids)
        {
            double rim;
            string name;
            using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(id, AcadDb.OpenMode.ForRead);
                name = s.Name;
                rim = mode switch
                {
                    "Superficie" => s.SurfaceElevationAtInsertionPoint,
                    "Valor" => value!.Value,
                    _ => s.RimElevation - value!.Value
                };
            }

            if (double.IsNaN(rim) || Math.Abs(rim) > 1e6)
            {
                ed.WriteMessage($"\n{name}: sem elevacao de superficie (a estrutura precisa de superficie de referencia).");
                continue;
            }

            HydroEditor.EditStructure(doc.Database, H(id), null, rim, null, HydroStore.HoldMode.OutraPonta);
            n++;
        }

        ed.WriteMessage($"\n{n} tampa(s) ajustada(s) ({mode}).");
    });

    [CommandMethod(C3DCommands.Hydro.Decliv, Flags)]
    public void Decliv() => Run("HYDECLIV", (doc, ed) =>
    {
        AcadDb.ObjectId? id = Pick<CivilDb.Pipe>(ed, "Clique o tubo para mudar a declividade: ", out _);
        if (id is null)
            return;
        double slope;
        string name;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Pipe p = (CivilDb.Pipe)tr.GetObject(id.Value, AcadDb.OpenMode.ForRead);
            name = p.Name;
            bool startUp = p.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
            double up = startUp ? p.StartPoint.Z : p.EndPoint.Z, down = startUp ? p.EndPoint.Z : p.StartPoint.Z;
            slope = p.Length2D > 1e-9 ? (up - down) / p.Length2D : 0;
        }

        ed.WriteMessage($"\n{name}: declividade atual {N(slope * 100, 3)} % (positiva descendo no sentido do fluxo).");
        string? hold = AskKeyword(ed, "Segurar a ponta de [Montante/Jusante]", "Montante", "Montante", "Jusante");
        if (hold is null)
            return;
        double? value = AskDouble(ed, "Nova declividade [%]", slope * 100);
        if (value is null)
            return;
        HydroEditor.SetSlope(doc.Database, H(id.Value), value.Value / 100, hold == "Montante");
        ed.WriteMessage($"\n{name}: declividade {N(slope * 100, 3)} % -> {N(value.Value, 3)} % segurando {hold.ToLowerInvariant()}.");
    });

    // ------------------------------------------------------------------
    // HYTROCAR / HYIGUALAR
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Hydro.Trocar, Flags)]
    public void Trocar() => Run("HYTROCAR", (doc, ed) =>
    {
        List<AcadDb.ObjectId> ids = PickMany(ed, doc.Database, "Selecione tubos e/ou estruturas para trocar familia/tamanho: ", true, true);
        if (ids.Count == 0)
            return;
        List<AcadDb.ObjectId> pipes = ids.Where(i => Accept(i, true, false)).ToList();
        List<AcadDb.ObjectId> structures = ids.Where(i => Accept(i, false, true)).ToList();
        foreach ((List<AcadDb.ObjectId> group, bool isPipe) in new[] { (pipes, true), (structures, false) })
        {
            if (group.Count == 0)
                continue;
            string netHandle;
            using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                netHandle = H(((CivilDb.Part)tr.GetObject(group[0], AcadDb.OpenMode.ForRead)).NetworkId);
            List<HydroEditor.FamilyChoice> families = HydroEditor.Families(doc.Database, netHandle, isPipe);
            if (families.Count == 0)
            {
                ed.WriteMessage("\nA parts list da rede nao tem familias de " + (isPipe ? "tubos." : "estruturas."));
                continue;
            }

            // Peca de familia que saiu do catalogo (ex. _IPE) acusa "Part Family is invalid" ao ler o nome:
            // nao impede a troca, que e justamente o jeito de consertar.
            string current = "";
            using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                try
                {
                    current = ((CivilDb.Part)tr.GetObject(group[0], AcadDb.OpenMode.ForRead)).PartFamilyName;
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n[HYDRO] Familia atual ilegivel ({ex.Message}); escolha a nova familia.");
                }
            }
            int def = Math.Max(0, families.FindIndex(f => string.Equals(f.Family, current, StringComparison.OrdinalIgnoreCase)));
            int fi = HydroPrompt.Choose(ed, $"Familia de {(isPipe ? "tubo" : "estrutura")} ({group.Count} item(ns))", families.Select(f => f.Family).ToList(), def);
            if (fi < 0)
                return;
            HydroEditor.FamilyChoice fam = families[fi];
            (AcadDb.ObjectId sizeId, string sizeLabel)? size = ChooseSize(ed, doc.Database, fam, isPipe);
            if (size is null)
                return;
            int n = HydroEditor.Swap(doc.Database, group.Select(H), fam.FamilyId, size.Value.sizeId, out List<string> skipped);
            // Tamanho novo digitado: o Civil pode montar a peca com as medidas da primeira linha da familia;
            // grava o comprimento e a largura escolhidos direto em cada peca trocada.
            if (!isPipe && _chosenPlan is (double cl, double cw))
            {
                foreach (AcadDb.ObjectId sid in group)
                {
                    string r = HydroEditor.SetPlanSize(doc.Database, H(sid), cl, cw);
                    if (r.Length > 0)
                        ed.WriteMessage("
[HYDRO] " + r);
                }
            }

            ed.WriteMessage($"\n{n} item(ns) trocado(s) para {fam.Family} / {size.Value.sizeLabel} (conexoes mantidas)" + (skipped.Count > 0 ? $"; {skipped.Count} pulado(s):" : "."));
            foreach (string s in skipped.Take(10))
                ed.WriteMessage("\n  " + s);
        }
    });

    private const string NewSize = "Novo tamanho (comprimento x largura)";

    /// <summary>
    /// Tamanho da familia por menu; nas estruturas com comprimento/largura interna ha a opcao de
    /// criar um tamanho novo na parts list (ex. PV 3000 x 2000) a partir do primeiro tamanho.
    /// </summary>
    /// <summary>Comprimento x largura digitados no ultimo ChooseSize (null quando escolheu um tamanho da lista).</summary>
    private static (double, double)? _chosenPlan;

    private static (AcadDb.ObjectId, string)? ChooseSize(Editor ed, AcadDb.Database db, HydroEditor.FamilyChoice fam, bool isPipe, int def = 0)
    {
        _chosenPlan = null;
        List<string> labels = fam.Sizes.Select(s => s.Size).ToList();
        bool canCreate = !isPipe && fam.Sizes.Count > 0 && HydroEditor.HasPlanSize(db, fam.Sizes[0].SizeId);
        if (canCreate)
            labels.Add(NewSize);
        if (labels.Count == 0)
        {
            ed.WriteMessage("\nA familia nao tem tamanhos na parts list.");
            return null;
        }

        int si = HydroPrompt.Choose(ed, $"Tamanho de {fam.Family}", labels, def);
        if (si < 0)
            return null;
        if (si < fam.Sizes.Count)
            return (fam.Sizes[si].SizeId, fam.Sizes[si].Size);

        (double length, double width) = HydroEditor.PlanSize(db, fam.Sizes[0].SizeId);
        double? l = AskDouble(ed, "Comprimento interno [m]", length, false);
        if (l is null)
            return null;
        double? w = AskDouble(ed, "Largura interna [m]", width, false);
        if (w is null)
            return null;
        (AcadDb.ObjectId id, string label) = HydroEditor.AddStructureSize(db, fam.FamilyId, l.Value, w.Value);
        _chosenPlan = (l.Value, w.Value);
        ed.WriteMessage($"\nTamanho {label} criado na parts list ({fam.Family}).");
        return (id, label);
    }

    [CommandMethod(C3DCommands.Hydro.Igualar, Flags)]
    public void Igualar() => Run("HYIGUALAR", (doc, ed) =>
    {
        AcadDb.ObjectId? source = Pick<CivilDb.Part>(ed, "Clique a peca de origem (tubo ou estrutura): ", out _);
        if (source is null)
            return;
        bool isPipe = Accept(source.Value, true, false);
        List<AcadDb.ObjectId> targets = PickMany(ed, doc.Database, "Selecione as pecas de destino: ", isPipe, !isPipe).Where(i => i != source.Value).ToList();
        if (targets.Count == 0)
            return;
        string? what = AskKeyword(ed, isPipe ? "Copiar [Tudo/Declividade/Familia/Regras]" : "Copiar [Tudo/Profundidade/Familia/Regras]", "Tudo", "Tudo", isPipe ? "Declividade" : "Profundidade", "Familia", "Regras");
        if (what is null)
            return;
        int n = HydroEditor.CopyProperties(doc.Database, H(source.Value), targets.Select(H), what);
        ed.WriteMessage($"\n{n} peca(s) igualada(s) ({what}).");
    });

    // ------------------------------------------------------------------
    // HYCONECTAR / HYINVERTER
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Hydro.Conectar, Flags)]
    public void Conectar() => Run("HYCONECTAR", (doc, ed) =>
    {
        AcadDb.ObjectId? pipeId = Pick<CivilDb.Pipe>(ed, "Clique o tubo perto da ponta a ligar/desligar: ", out Point3d picked);
        if (pipeId is null)
            return;
        bool start;
        string name;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Pipe p = (CivilDb.Pipe)tr.GetObject(pipeId.Value, AcadDb.OpenMode.ForRead);
            name = p.Name;
            Point3d sp = p.StartPoint, ep = p.EndPoint;
            if (picked == Point3d.Origin)
            {
                string? which = AskKeyword(ed, "Ponta [Inicio/Fim]", "Fim", "Inicio", "Fim");
                if (which is null)
                    return;
                start = which == "Inicio";
            }
            else
            {
                start = new Point2d(picked.X, picked.Y).GetDistanceTo(new Point2d(sp.X, sp.Y)) < new Point2d(picked.X, picked.Y).GetDistanceTo(new Point2d(ep.X, ep.Y));
            }
        }

        PromptEntityOptions o = new($"\nClique a estrutura para ligar a ponta {(start ? "inicial" : "final")} de {name} (Enter desliga): ") { AllowNone = true };
        o.SetRejectMessage("\nNao e uma estrutura.");
        o.AddAllowedClass(typeof(CivilDb.Structure), false);
        PromptEntityResult r = ed.GetEntity(o);
        if (r.Status == PromptStatus.None)
        {
            HydroEditor.Connect(doc.Database, H(pipeId.Value), start, null);
            ed.WriteMessage($"\n{name}: ponta {(start ? "inicial" : "final")} desligada.");
            return;
        }

        if (r.Status != PromptStatus.OK)
            return;
        HydroEditor.Connect(doc.Database, H(pipeId.Value), start, H(r.ObjectId));
        ed.WriteMessage($"\n{name}: ponta {(start ? "inicial" : "final")} ligada.");
    });

    [CommandMethod(C3DCommands.Hydro.Inverter, Flags)]
    public void Inverter() => Run("HYINVERTER", (doc, ed) =>
    {
        List<AcadDb.ObjectId> ids = PickMany(ed, doc.Database, "Selecione os tubos para inverter o sentido: ", true, false);
        if (ids.Count == 0)
            return;
        HydroEditor.Reverse(doc.Database, ids.Select(H));
        ed.WriteMessage($"\n{ids.Count} tubo(s) com o sentido invertido.");
    });

    // ------------------------------------------------------------------
    // HYMOVER / HYINSERIR / HYREMOVER / HYCAMINHO
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Hydro.Mover, Flags)]
    public void Mover() => Run("HYMOVER", (doc, ed) =>
    {
        AcadDb.ObjectId? id = Pick<CivilDb.Structure>(ed, "Clique a estrutura para mover: ", out _);
        if (id is null)
            return;
        Point3d from;
        string name;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(id.Value, AcadDb.OpenMode.ForRead);
            from = s.Location;
            name = s.Name;
        }

        string? keep = AskKeyword(ed, "Os tubos ligados mantem [Declividade/Fundo]", "Fundo", "Declividade", "Fundo");
        if (keep is null)
            return;
        PromptPointOptions po = new("\nNovo ponto da estrutura: ") { UseBasePoint = true, BasePoint = from, UseDashedLine = true };
        PromptPointResult pr = ed.GetPoint(po);
        if (pr.Status != PromptStatus.OK)
            return;
        HydroEditor.MoveStructure(doc.Database, H(id.Value), pr.Value, keep == "Declividade");
        ed.WriteMessage($"\n{name} movida para E {N(pr.Value.X)} N {N(pr.Value.Y)} ({(keep == "Declividade" ? "declividades mantidas" : "fundos mantidos")}).");
    });

    [CommandMethod(C3DCommands.Hydro.Inserir, Flags)]
    public void Inserir() => Run("HYINSERIR", (doc, ed) =>
    {
        AcadDb.ObjectId? pipeId = Pick<CivilDb.Pipe>(ed, "Clique o tubo onde inserir a estrutura: ", out _);
        if (pipeId is null)
            return;
        PromptPointResult pr = ed.GetPoint(new PromptPointOptions("\nPonto no tubo para a nova estrutura: "));
        if (pr.Status != PromptStatus.OK)
            return;
        string netHandle;
        using (AcadDb.Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            netHandle = H(((CivilDb.Part)tr.GetObject(pipeId.Value, AcadDb.OpenMode.ForRead)).NetworkId);
        (AcadDb.ObjectId familyId, AcadDb.ObjectId sizeId, string label)? choice = ChooseStructure(ed, doc.Database, netHandle, pipeId.Value);
        if (choice is null)
            return;
        (string newStructure, string newPipe) = HydroEditor.InsertStructure(doc.Database, H(pipeId.Value), pr.Value, choice.Value.familyId, choice.Value.sizeId);
        ed.WriteMessage($"\nEstrutura {choice.Value.label} inserida no tubo (handles {newStructure} / tubo novo {newPipe}); declividade mantida.");
    });

    /// <summary>Familia/tamanho de estrutura: por padrao a mesma da estrutura de montante do tubo.</summary>
    private static (AcadDb.ObjectId, AcadDb.ObjectId, string)? ChooseStructure(Editor ed, AcadDb.Database db, string netHandle, AcadDb.ObjectId pipeId)
    {
        List<HydroEditor.FamilyChoice> families = HydroEditor.Families(db, netHandle, false);
        if (families.Count == 0)
        {
            ed.WriteMessage("\nA parts list da rede nao tem familias de estruturas.");
            return null;
        }

        string defaultFamily = "";
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Pipe p = (CivilDb.Pipe)tr.GetObject(pipeId, AcadDb.OpenMode.ForRead);
            AcadDb.ObjectId sid = p.StartStructureId.IsNull ? p.EndStructureId : p.StartStructureId;
            if (!sid.IsNull && tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Structure s)
                defaultFamily = s.PartFamilyName;
        }

        int def = Math.Max(0, families.FindIndex(f => string.Equals(f.Family, defaultFamily, StringComparison.OrdinalIgnoreCase)));
        int fi = HydroPrompt.Choose(ed, "Familia da estrutura", families.Select(f => f.Family).ToList(), def);
        if (fi < 0)
            return null;
        HydroEditor.FamilyChoice fam = families[fi];
        (AcadDb.ObjectId sizeId, string sizeLabel)? size = ChooseSize(ed, db, fam, false);
        if (size is null)
            return null;
        return (fam.FamilyId, size.Value.sizeId, fam.Family + " / " + size.Value.sizeLabel);
    }

    [CommandMethod(C3DCommands.Hydro.Remover, Flags)]
    public void Remover() => Run("HYREMOVER", (doc, ed) =>
    {
        AcadDb.ObjectId? id = Pick<CivilDb.Structure>(ed, "Clique a estrutura a remover (os dois tubos sao emendados): ", out _);
        if (id is null)
            return;
        string result = HydroEditor.RemoveStructure(doc.Database, H(id.Value));
        ed.WriteMessage("\n" + result);
    });

    [CommandMethod(C3DCommands.Hydro.Caminho, Flags)]
    public void Caminho() => Run("HYCAMINHO", (doc, ed) =>
    {
        AcadDb.ObjectId? a = Pick<CivilDb.Structure>(ed, "Clique a primeira estrutura do caminho: ", out _);
        if (a is null)
            return;
        AcadDb.ObjectId? b = Pick<CivilDb.Structure>(ed, "Clique a ultima estrutura do caminho: ", out _);
        if (b is null)
            return;
        (List<AcadDb.ObjectId> ids, double length) = HydroEditor.ShortestPath(doc.Database, a.Value, b.Value);
        if (ids.Count == 0)
        {
            ed.WriteMessage("\nNao ha caminho entre as duas estruturas nesta rede.");
            return;
        }

        ed.SetImpliedSelection(ids.ToArray());
        int pipes = ids.Count(i => Accept(i, true, false));
        ed.WriteMessage($"\nCaminho selecionado: {pipes} tubo(s), {ids.Count - pipes} estrutura(s), {N(length, 2)} m. Use o painel (Estruturas/Tubos > marcar selecionados) ou os comandos com a selecao.");
        HydroApply.Preselect = ids.Select(H).ToList();
    });

    // ------------------------------------------------------------------
    // HYREGRA / HYREGRAS / HYREGRASAUTO (medidas associativas, §9)
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Hydro.Regra, Flags)]
    public void Regra() => Run("HYREGRA", (doc, ed) =>
    {
        AcadDb.ObjectId? id = Pick<CivilDb.Structure>(ed, "Clique a estrutura: ", out _);
        if (id is null)
            return;
        AcadDb.Database db = doc.Database;
        Dictionary<string, HydroRuleTemplate> templates = HydroRules.LoadTemplates(db);
        string name;
        HydroStructureRules own;
        Dictionary<string, double> vars;
        List<PartParam> parameters;
        using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(id.Value, AcadDb.OpenMode.ForRead);
            name = s.Name;
            own = HydroRules.Read(tr, s);
            vars = HydroRules.Variables(tr, s, out parameters);
        }

        Dictionary<string, string> eff = HydroRules.Effective(own, templates);
        ed.WriteMessage($"\n{name}" + (own.Template.Length > 0 ? $" (modelo {own.Template})" : "") + ":");
        foreach (PartParam p in parameters.Where(p => p.Editable && !p.Fixed))
        {
            string rule = eff.TryGetValue(p.Name, out string? r) ? "  = " + r : "  (sem regra)";
            ed.WriteMessage($"\n  {HydroNames.Short(p.Context, p.Name),-13} {N(p.Meters)} m  {p.Description} [{p.Name}]{rule}");
        }

        ed.WriteMessage($"\n  tampa  {N(vars[HydroRules.Rim])} m" + (eff.TryGetValue(HydroRules.Rim, out string? rr) ? "  = " + rr : "  (fixa)"));
        ed.WriteMessage($"\n  fundo  {N(vars[HydroRules.Sump])} m" + (eff.TryGetValue(HydroRules.Sump, out string? rf) ? "  = " + rf : "  (fixa)"));
        HashSet<string> partVars = new(parameters.SelectMany(p => new[] { Calc.HydroExpr.Normalize(p.Name), Calc.HydroExpr.Normalize(HydroNames.Short(p.Context, p.Name)) }));
        ed.WriteMessage("\n  Outras variaveis (m): " + string.Join("  ", vars.Where(kv => !partVars.Contains(kv.Key))
            .Select(kv => $"{kv.Key}={N(kv.Value)}")));
        ed.WriteMessage("\n  " + HydroRules.Help);

        // Lista clicavel (nunca digitar o nome da medida): as editaveis da familia + tampa e fundo.
        List<PartParam> choices = parameters.Where(p => p.Editable && !p.Fixed).ToList();
        List<string> labels = choices.Select(p => p.Description).Concat(new[] { "Elevacao da tampa", "Elevacao do fundo" }).ToList();
        int pick = HydroPrompt.Choose(ed, "Medida para a regra", labels);
        if (pick < 0)
            return;
        string target = pick < choices.Count ? choices[pick].Name : pick == choices.Count ? HydroRules.Rim : HydroRules.Sump;
        PartParam? param = pick < choices.Count ? choices[pick] : null;
        if (param is not null)
            target = param.Name;
        else if (target.Equals(HydroRules.Rim, StringComparison.OrdinalIgnoreCase) || target.Equals(HydroRules.Sump, StringComparison.OrdinalIgnoreCase))
            target = target.ToLowerInvariant();
        else
        {
            ed.WriteMessage($"\n'{target}' nao e medida desta peca.");
            return;
        }

        if (param is not null && !param.Editable)
        {
            ed.WriteMessage($"\n{param.Name} e fixa no catalogo (nem faixa nem lista).");
            return;
        }

        PromptStringOptions eo = new($"\nRegra para {labels[pick].ToLowerInvariant()} (ex. dmax + 2*0,15; vazio = valor fixo; '.' = volta ao modelo): ") { AllowSpaces = true };
        if (eff.TryGetValue(target, out string? current))
        {
            eo.DefaultValue = current;
            eo.UseDefaultValue = true;
        }

        PromptResult er = ed.GetString(eo);
        if (er.Status != PromptStatus.OK)
            return;
        string text = er.StringResult.Trim();
        string? store = text == "." ? null : text;
        if (store is { Length: > 0 })
        {
            Dictionary<string, double> probe = new(vars, StringComparer.Ordinal);
            try
            {
                double v = Calc.HydroExpr.Parse(store).Evaluate(probe);
                ed.WriteMessage($"\n{target} = {N(v)} m agora.");
            }
            catch (Calc.HydroExprException ex)
            {
                ed.WriteMessage($"\nRegra recusada: {ex.Message}");
                return;
            }
        }

        string handle = H(id.Value);
        using (HydroSync.Applying())
        {
            HydroRules.Edit(db, new[] { handle }, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [target] = store }, null);
            HydroRules.Report rep = HydroRules.Apply(db, new[] { id.Value }, Settings(db).HoldMode);
            WriteReport(ed, rep);
        }
    });

    [CommandMethod(C3DCommands.Hydro.Regras, Flags)]
    public void Regras() => Run("HYREGRAS", (doc, ed) =>
    {
        AcadDb.Database db = doc.Database;
        List<AcadDb.ObjectId> ids = PickMany(ed, db, "Selecione as estruturas (Enter = todas do desenho): ", false, true);
        if (ids.Count == 0)
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            foreach (AcadDb.ObjectId netId in Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db).GetPipeNetworkIds())
            {
                if (tr.GetObject(netId, AcadDb.OpenMode.ForRead) is CivilDb.Network net)
                    ids.AddRange(net.GetStructureIds().Cast<AcadDb.ObjectId>());
            }
        }

        using (HydroSync.Applying())
            WriteReport(ed, HydroRules.Apply(db, ids, Settings(db).HoldMode));
    });

    [CommandMethod(C3DCommands.Hydro.RegrasAuto, CommandFlags.Modal)]
    public void RegrasAuto() => Run("HYREGRASAUTO", (doc, ed) =>
    {
        bool on = Settings(doc.Database).RulesAuto;
        string? k = AskKeyword(ed, $"Atualizacao automatica das regras esta {(on ? "LIGADA" : "DESLIGADA")} [Ligar/Desligar]", on ? "Desligar" : "Ligar", "Ligar", "Desligar");
        if (k is null)
            return;
        HydroSync.SetAuto(doc, k == "Ligar");
    });

    [CommandMethod(C3DCommands.Hydro.Dica, CommandFlags.Modal)]
    public void Dica() => Run("HYDICA", (doc, ed) =>
    {
        bool on = HydroTooltip.Enabled;
        string? k = AskKeyword(ed, $"Dica do HYDRO no cursor esta {(on ? "LIGADA" : "DESLIGADA")} [Ligar/Desligar]", on ? "Desligar" : "Ligar", "Ligar", "Desligar");
        if (k is null)
            return;
        HydroTooltip.Enabled = k == "Ligar";
        ed.WriteMessage($"\n[HYDRO] Dica no cursor {(HydroTooltip.Enabled ? "ligada" : "desligada")} (nesta sessao).");
    });

    private static void WriteReport(Editor ed, HydroRules.Report rep)
    {
        ed.WriteMessage("\n[HYDRO] " + rep.Summary + ".");
        foreach (string line in rep.Lines.Take(30))
            ed.WriteMessage("\n  " + line);
        foreach (string err in rep.Errors.Take(30))
            ed.WriteMessage("\n  aviso: " + err);
    }
}
