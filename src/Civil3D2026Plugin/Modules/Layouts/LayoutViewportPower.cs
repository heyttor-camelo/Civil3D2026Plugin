using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// "Modo leve" das viewports: desliga as viewports dos layouts (marcando-as com XData
/// C3D_VPOFF) para a troca de layout ficar rapida, e religa so as que o plugin desligou.
/// Opcionalmente faz isso sozinho: ao sair de um layout desliga as viewports dele; ao
/// entrar num layout religa as dele no proximo Idle. As demais viewports (desligadas
/// pelo usuario) nao sao tocadas.
/// </summary>
internal static class LayoutViewportPower
{
    public const string App = "C3D_VPOFF";
    private const string ChaveAuto = "vp_modo_leve";

    private static bool _iniciado;
    private static bool _executando;
    private static readonly HashSet<string> Pendentes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modo leve automatico (preferencia gravada).</summary>
    public static bool Automatico
    {
        get => LayoutSettings.GetBool(ChaveAuto, false);
        set => LayoutSettings.Set(ChaveAuto, value);
    }

    public static void Initialize()
    {
        if (_iniciado)
            return;
        _iniciado = true;
        LayoutManager.Current.LayoutSwitching += OnSwitching;
        LayoutManager.Current.LayoutSwitched += OnSwitched;
        AcApp.Idle += OnIdle;
    }

    public static void Terminate()
    {
        if (!_iniciado)
            return;
        _iniciado = false;
        LayoutManager.Current.LayoutSwitching -= OnSwitching;
        LayoutManager.Current.LayoutSwitched -= OnSwitched;
        AcApp.Idle -= OnIdle;
    }

    // ------------------------------------------------------------------
    // Operacoes
    // ------------------------------------------------------------------

    /// <summary>Desliga as viewports ligadas dos layouts dados (null = todos), marcando-as.</summary>
    public static OpResult Desligar(Document doc, IEnumerable<string>? layouts, bool poupaAtual)
    {
        using DocumentLock _ = doc.LockDocument();
        int n = Executar(doc.Database, layouts, poupaAtual ? LayoutManager.Current.CurrentLayout : null, desligar: true);
        return new OpResult(n, 0, n > 0
            ? $"{n} viewport(s) desligada(s) (marcadas para religar)."
            : "Nenhuma viewport ligada para desligar.");
    }

    /// <summary>Religa as viewports que o plugin desligou nos layouts dados (null = todos).</summary>
    public static OpResult Religar(Document doc, IEnumerable<string>? layouts)
    {
        using DocumentLock _ = doc.LockDocument();
        int n = Executar(doc.Database, layouts, null, desligar: false);
        return new OpResult(n, 0, n > 0
            ? $"{n} viewport(s) religada(s)."
            : "Nenhuma viewport desligada pelo plugin para religar.");
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
                if (tr.GetObject(id, OpenMode.ForRead) is Viewport vp && Marcada(vp))
                    n++;
        }
        tr.Commit();
        return n;
    }

    private static int Executar(Database db, IEnumerable<string>? layouts, string? poupar, bool desligar)
    {
        HashSet<string>? alvo = layouts is null ? null : new HashSet<string>(layouts, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (desligar)
            GarantirApp(tr, db);

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
                    vp.UpgradeOpen();
                    vp.On = false;
                    vp.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App), new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1));
                    n++;
                }
                else if (Marcada(vp))
                {
                    vp.UpgradeOpen();
                    vp.On = true;
                    vp.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App));
                    n++;
                }
            }
        }
        tr.Commit();
        return n;
    }

    private static bool Marcada(Viewport vp)
    {
        using ResultBuffer? rb = vp.GetXDataForApplication(App);
        return rb is not null && rb.AsArray().Length > 1;
    }

    private static void GarantirApp(Transaction tr, Database db)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(App))
            return;
        rat.UpgradeOpen();
        var r = new RegAppTableRecord { Name = App };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }

    // ------------------------------------------------------------------
    // Automatico
    // ------------------------------------------------------------------

    private static void OnSwitching(object? sender, Autodesk.AutoCAD.DatabaseServices.LayoutEventArgs e)
    {
        if (!Automatico || _executando)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        try
        {
            _executando = true;
            string saindo = LayoutManager.Current.CurrentLayout;
            if (!string.Equals(saindo, "Model", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(saindo, e.Name, StringComparison.OrdinalIgnoreCase))
                Executar(doc.Database, new[] { saindo }, null, desligar: true);
        }
        catch (System.Exception)
        {
            // Melhor esforco: nunca impede a troca de layout.
        }
        finally
        {
            _executando = false;
        }
    }

    private static void OnSwitched(object? sender, Autodesk.AutoCAD.DatabaseServices.LayoutEventArgs e)
    {
        if (!Automatico)
            return;
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
            return;
        }
        Pendentes.Clear();
        try
        {
            _executando = true;
            using DocumentLock _ = doc.LockDocument();
            Executar(doc.Database, new[] { atual }, null, desligar: false);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage($"\n[Layouts] Modo leve: nao foi possivel religar as viewports: {ex.Message}");
        }
        finally
        {
            _executando = false;
        }
    }
}
