using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// "Modo leve": desliga as viewports dos layouts (marcando-as com XData C3D_VPOFF) para a troca
/// de layout ficar rapida, e religa so as que o plugin desligou. Opcionalmente tambem deixa o
/// Model mais leve: congela layers escolhidas (F-VT-NOTAVEL, por exemplo) e oculta as labels do
/// Civil 3D, marcando o que mudou (C3D_LEVE) para "Religar todas" desfazer exatamente isso.
/// No automatico: ao sair de um layout desliga as viewports dele; ao entrar religa as dele no
/// proximo Idle (o Model nao e mexido no automatico). O que o usuario desligou nao e tocado.
/// </summary>
internal static class LayoutViewportPower
{
    public const string App = "C3D_VPOFF";
    public const string AppLeve = "C3D_LEVE";
    private const string ChaveAuto = "vp_modo_leve";
    private const string Mod = "Layouts";

    private static bool _iniciado;
    private static bool _executando;
    private static readonly HashSet<string> Pendentes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modo leve automatico (preferencia gravada).</summary>
    public static bool Automatico
    {
        get => LayoutSettings.GetBool(ChaveAuto, false);
        set => LayoutSettings.Set(ChaveAuto, value);
    }

    /// <summary>Ao desligar, congelar tambem as layers da lista.</summary>
    public static bool CongelarLayers
    {
        get => LayoutSettings.GetBool("leve_layers", false);
        set => LayoutSettings.Set("leve_layers", value);
    }

    /// <summary>Layers para congelar (separadas por virgula; aceita * e ?).</summary>
    public static string Layers
    {
        get => LayoutSettings.Get("leve_layers_lista", "F-VT-NOTAVEL");
        set => LayoutSettings.Set("leve_layers_lista", value);
    }

    /// <summary>Ao desligar, ocultar tambem as labels do Civil 3D no Model.</summary>
    public static bool OcultarLabels
    {
        get => LayoutSettings.GetBool("leve_labels", false);
        set => LayoutSettings.Set("leve_labels", value);
    }

    public static void Initialize()
    {
        if (_iniciado)
            return;
        _iniciado = true;
        LayoutManager.Current.LayoutSwitched += OnSwitched;
        AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
        AcApp.Idle += OnIdle;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        _anterior = doc is null ? null : LayoutManager.Current.CurrentLayout;
    }

    public static void Terminate()
    {
        if (!_iniciado)
            return;
        _iniciado = false;
        LayoutManager.Current.LayoutSwitched -= OnSwitched;
        AcApp.DocumentManager.DocumentActivated -= OnDocumentActivated;
        AcApp.Idle -= OnIdle;
    }

    // ------------------------------------------------------------------
    // Operacoes
    // ------------------------------------------------------------------

    /// <summary>Desliga as viewports ligadas dos layouts dados (null = todos), marcando-as; com as opcoes, alivia o Model.</summary>
    public static OpResult Desligar(Document doc, IEnumerable<string>? layouts, bool poupaAtual)
    {
        using DocumentLock _ = doc.LockDocument();
        using IDisposable __ = PluginLog.Medir(Mod, "Modo leve: desligar");
        Progresso.Mostrar("Desligando as viewports...", 0.05);
        int n = Executar(doc.Database, layouts, poupaAtual ? LayoutManager.Current.CurrentLayout : null, desligar: true);
        string msg = n > 0 ? $"{n} viewport(s) desligada(s) (marcadas para religar)" : "Nenhuma viewport ligada para desligar";
        if (CongelarLayers || OcultarLabels)
        {
            (int layersN, int labelsN, string nota) = AliviarModel(doc, CongelarLayers, OcultarLabels);
            if (CongelarLayers)
                msg += $"; {layersN} layer(s) congelada(s)";
            if (OcultarLabels)
                msg += $"; {labelsN} label(s) do Civil oculta(s)";
            msg += nota;
        }
        return new OpResult(n, 0, msg + ".");
    }

    /// <summary>
    /// Religa as viewports que o plugin desligou nos layouts dados (null = todos). O Model (layers
    /// congeladas e labels ocultas pelo plugin) volta quando nao sobra viewport desligada pelo plugin.
    /// </summary>
    public static OpResult Religar(Document doc, IEnumerable<string>? layouts)
    {
        using DocumentLock _ = doc.LockDocument();
        using IDisposable __ = PluginLog.Medir(Mod, "Modo leve: religar");
        Progresso.Mostrar("Religando as viewports...", 0.05);
        int n = Executar(doc.Database, layouts, null, desligar: false);
        string msg = n > 0 ? $"{n} viewport(s) religada(s)" : "Nenhuma viewport desligada pelo plugin para religar";
        if (layouts is null || Desligadas(doc.Database) == 0)
        {
            (int layersN, int labelsN) = RestaurarModel(doc);
            if (layersN > 0)
                msg += $"; {layersN} layer(s) descongelada(s)";
            if (labelsN > 0)
                msg += $"; {labelsN} label(s) do Civil de volta";
        }
        return new OpResult(n, 0, msg + ".");
    }

    /// <summary>Quantas viewports estao desligadas pelo plugin (para o painel).</summary>
    public static int Desligadas(Database db)
    {
        int n = 0;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (layout.ModelType)
                continue;
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
                if (tr.GetObject(id, OpenMode.ForRead) is Viewport vp && Marcada(vp, App))
                    n++;
        }
        tr.Commit();
        return n;
    }

    /// <summary>Layers congeladas e labels ocultas pelo plugin (para o painel).</summary>
    public static (int Layers, int Labels) Aliviados(Database db)
    {
        int layers = 0, labels = 0;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in lt)
            if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord l && Marcada(l, AppLeve))
                layers++;
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        foreach (ObjectId id in ms)
            if (EhLabelCivil(id) && tr.GetObject(id, OpenMode.ForRead) is Entity e && Marcada(e, AppLeve))
                labels++;
        tr.Commit();
        return (layers, labels);
    }

    private static int Executar(Database db, IEnumerable<string>? layouts, string? poupar, bool desligar)
    {
        HashSet<string>? alvo = layouts is null ? null : new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (desligar)
            GarantirApp(tr, db, App);

        foreach (DBDictionaryEntry e in dic)
        {
            var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (layout.ModelType || (alvo is not null && !alvo.Contains(layout.LayoutName)))
                continue;
            if (poupar is not null && string.Equals(poupar, layout.LayoutName, StringComparison.OrdinalIgnoreCase))
                continue;

            ObjectIdCollection papel = layout.GetViewports();
            ObjectId papelId = papel.Count > 0 ? papel[0] : ObjectId.Null;
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (id == papelId || tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                if (desligar)
                {
                    if (!vp.On || vp.Number == 1)
                        continue;
                    // Mesmo em layer travada (comum na layer das viewports).
                    var w = (Viewport)tr.GetObject(id, OpenMode.ForWrite, false, true);
                    w.On = false;
                    w.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App), new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1));
                    n++;
                }
                else if (Marcada(vp, App))
                {
                    var w = (Viewport)tr.GetObject(id, OpenMode.ForWrite, false, true);
                    w.On = true;
                    w.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App));
                    n++;
                }
            }
        }
        tr.Commit();
        return n;
    }

    // ------------------------------------------------------------------
    // Model mais leve: layers congeladas e labels do Civil ocultas
    // ------------------------------------------------------------------

    /// <summary>Congela as layers da lista e/ou oculta as labels do Civil no Model, marcando o que mudou.</summary>
    private static (int Layers, int Labels, string Nota) AliviarModel(Document doc, bool layers, bool labels)
    {
        Database db = doc.Database;
        int nLayers = 0, nLabels = 0;
        var notas = new List<string>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            GarantirApp(tr, db, AppLeve);
            if (layers)
            {
                Regex? filtro = Filtro(Layers);
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var achadas = new List<string>();
                foreach (ObjectId id in lt)
                {
                    if (filtro is null || tr.GetObject(id, OpenMode.ForRead) is not LayerTableRecord l || !filtro.IsMatch(l.Name))
                        continue;
                    achadas.Add(l.Name);
                    if (l.IsFrozen)
                        continue;
                    if (id == db.Clayer)
                    {
                        notas.Add($"a layer atual {l.Name} nao pode ser congelada");
                        continue;
                    }
                    var w = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                    w.IsFrozen = true;
                    w.XData = Marca();
                    nLayers++;
                }
                if (achadas.Count == 0)
                    notas.Add($"nenhuma layer com o nome {Layers}");
                PluginLog.Info(Mod, $"Modo leve: layers {Layers}: {achadas.Count} encontrada(s), {nLayers} congelada(s).");
            }
            if (labels)
            {
                Progresso.Mostrar("Ocultando as labels do Civil 3D no Model...", 0.4);
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                var tipos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ms)
                {
                    if (!EhLabelCivil(id) || tr.GetObject(id, OpenMode.ForRead) is not Entity e || !e.Visible)
                        continue;
                    try
                    {
                        var w = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                        w.Visible = false;
                        w.XData = Marca();
                        nLabels++;
                        string dxf = id.ObjectClass?.DxfName ?? "?";
                        tipos[dxf] = tipos.GetValueOrDefault(dxf) + 1;
                    }
                    catch (System.Exception ex)
                    {
                        PluginLog.Diag(Mod, $"Modo leve: label {id.Handle} nao ocultada: {ex.Message}");
                    }
                }
                PluginLog.Info(Mod, $"Modo leve: {nLabels} label(s) do Civil oculta(s): " + string.Join(", ", tipos.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}")));
            }
            tr.Commit();
        }
        if (nLayers > 0)
            Regenerar(doc);
        return (nLayers, nLabels, notas.Count > 0 ? " (" + string.Join("; ", notas) + ")" : "");
    }

    /// <summary>Descongela as layers e mostra as labels que o plugin mexeu (as do usuario ficam).</summary>
    private static (int Layers, int Labels) RestaurarModel(Document doc)
    {
        Database db = doc.Database;
        int nLayers = 0, nLabels = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not LayerTableRecord l || !Marcada(l, AppLeve))
                    continue;
                var w = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                w.IsFrozen = false;
                w.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppLeve));
                nLayers++;
            }
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Progresso.Mostrar("Mostrando de novo as labels do Civil 3D...", 0.5);
            foreach (ObjectId id in ms)
            {
                if (!EhLabelCivil(id) || tr.GetObject(id, OpenMode.ForRead) is not Entity e || !Marcada(e, AppLeve))
                    continue;
                try
                {
                    var w = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                    w.Visible = true;
                    w.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppLeve));
                    nLabels++;
                }
                catch (System.Exception ex)
                {
                    PluginLog.Diag(Mod, $"Modo leve: label {id.Handle} nao restaurada: {ex.Message}");
                }
            }
            tr.Commit();
        }
        if (nLayers + nLabels > 0)
            PluginLog.Info(Mod, $"Modo leve: Model restaurado ({nLayers} layer(s) descongelada(s), {nLabels} label(s) de volta).");
        if (nLayers > 0)
            Regenerar(doc);
        return (nLayers, nLabels);
    }

    /// <summary>Label do Civil 3D pelo nome DXF da classe (AECC_*LABEL*), sem abrir o objeto.</summary>
    private static bool EhLabelCivil(ObjectId id)
    {
        try
        {
            string dxf = id.ObjectClass?.DxfName ?? "";
            return dxf.StartsWith("AECC", StringComparison.OrdinalIgnoreCase) && dxf.Contains("LABEL", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    /// <summary>"F-VT-NOTAVEL, C-*" -> regex sem diferenciar maiusculas (null se a lista esta vazia).</summary>
    internal static Regex? Filtro(string lista)
    {
        string[] partes = lista.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (partes.Length == 0)
            return null;
        string padrao = "^(" + string.Join("|", partes.Select(p => Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", "."))) + ")$";
        return new Regex(padrao, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static ResultBuffer Marca() =>
        new(new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppLeve), new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1));

    private static void Regenerar(Document doc)
    {
        try
        {
            Progresso.Mostrar("Regenerando o desenho...", 0.9);
            doc.Editor.Regen();
        }
        catch (System.Exception ex)
        {
            PluginLog.Diag(Mod, "Modo leve: REGEN falhou: " + ex.Message);
        }
    }

    private static bool Marcada(DBObject o, string app)
    {
        using ResultBuffer? rb = o.GetXDataForApplication(app);
        return rb is not null && rb.AsArray().Length > 1;
    }

    private static void GarantirApp(Transaction tr, Database db, string nome)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(nome))
            return;
        rat.UpgradeOpen();
        var r = new RegAppTableRecord { Name = nome };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }

    // ------------------------------------------------------------------
    // Automatico (a API so avisa depois da troca: LayoutSwitched; o layout anterior e lembrado aqui)
    // ------------------------------------------------------------------

    private static string? _anterior;
    private static string? _saindo;

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        _anterior = e.Document is null ? null : LayoutManager.Current.CurrentLayout;
        Pendentes.Clear();
    }

    private static void OnSwitched(object? sender, Autodesk.AutoCAD.DatabaseServices.LayoutEventArgs e)
    {
        string? saindo = _anterior;
        _anterior = e.Name;
        if (!Automatico)
            return;
        if (saindo is not null && !string.Equals(saindo, e.Name, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(saindo, "Model", StringComparison.OrdinalIgnoreCase))
            _saindo = saindo;
        Pendentes.Add(e.Name);
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (Pendentes.Count == 0 || _executando)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || !doc.Editor.IsQuiescent)
            return;

        string atual = LayoutManager.Current.CurrentLayout;
        if (!Pendentes.Contains(atual))
        {
            Pendentes.Clear();
            _saindo = null;
            return;
        }
        Pendentes.Clear();
        string? saindo = _saindo;
        _saindo = null;
        try
        {
            _executando = true;
            using DocumentLock _ = doc.LockDocument();
            if (saindo is not null)
                Executar(doc.Database, new[] { saindo }, null, desligar: true);
            Executar(doc.Database, new[] { atual }, null, desligar: false);
        }
        catch (System.Exception ex)
        {
            PluginLog.Erro(Mod, "Modo leve automatico: viewports nao ajustadas", ex);
            doc.Editor.WriteMessage($"\n[Layouts] Modo leve: nao foi possivel ajustar as viewports: {ex.Message}");
        }
        finally
        {
            _executando = false;
        }
    }
}
