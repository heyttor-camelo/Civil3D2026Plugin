using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Edicao associativa das estruturas (§9), no molde do ViarioSync:
/// - ObjectModified so anota as estruturas mexidas (a propria, ou as duas pontas do tubo mexido);
///   nada e lido nem gravado dentro do evento;
/// - no fim do comando (ou no Idle, para edicoes pela paleta de propriedades) as anotadas que tem
///   regra sao recalculadas (HydroRules.Apply) com o documento travado;
/// - as mudancas feitas pelo proprio recalculo nao anotam de novo (sem laco);
/// - UNDO/REDO nao disparam recalculo (o desfazer ja restaura tudo);
/// - com a atualizacao automatica desligada (Calculo > Regras, ou HYREGRASAUTO) as anotadas ficam
///   pendentes ate o HYREGRAS.
/// </summary>
internal static class HydroSync
{
    private const string Module = "Hydro";

    private static readonly Dictionary<AcadDb.Database, HashSet<AcadDb.ObjectId>> Pending = new();
    private static readonly HashSet<Document> Attached = new();
    private static bool _initialized;
    private static bool _applying;

    private static readonly HashSet<string> UndoCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNDO", "U", "REDO", "MREDO", "OOPS"
    };

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        DocumentCollection documents = AcApp.DocumentManager;
        documents.DocumentCreated += OnDocumentCreated;
        documents.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        foreach (Document doc in documents)
            Attach(doc);
        AcApp.Idle += OnIdle;
    }

    public static void Terminate()
    {
        if (!_initialized)
            return;
        _initialized = false;
        try
        {
            AcApp.Idle -= OnIdle;
            AcApp.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch
        {
            // Encerramento.
        }

        foreach (Document doc in Attached.ToList())
            Detach(doc);
    }

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
    {
        if (e.Document is not null)
            Attach(e.Document);
    }

    private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
    {
        if (e.Document is not null)
            Detach(e.Document);
    }

    private static void Attach(Document doc)
    {
        if (!Attached.Add(doc))
            return;
        doc.Database.ObjectModified += OnObjectModified;
        doc.CommandEnded += OnCommandEnded;
    }

    private static void Detach(Document doc)
    {
        if (!Attached.Remove(doc))
            return;
        try
        {
            doc.Database.ObjectModified -= OnObjectModified;
            doc.CommandEnded -= OnCommandEnded;
            Pending.Remove(doc.Database);
        }
        catch
        {
            // Documento fechando.
        }
    }

    /// <summary>Recalculo em andamento (HYREGRAS ou fim de comando): as mudancas nao anotam de novo.</summary>
    public static IDisposable Applying()
    {
        bool was = _applying;
        _applying = true;
        return new Restore(() => _applying = was);
    }

    private sealed class Restore : IDisposable
    {
        private Action? _undo;

        public Restore(Action undo) => _undo = undo;

        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }

    /// <summary>Estruturas anotadas e ainda nao recalculadas neste desenho.</summary>
    public static int PendingCount(AcadDb.Database db) => Pending.TryGetValue(db, out HashSet<AcadDb.ObjectId>? set) ? set.Count : 0;

    private static void OnObjectModified(object? sender, AcadDb.ObjectEventArgs e)
    {
        if (_applying)
            return;
        try
        {
            AcadDb.DBObject obj = e.DBObject;
            if (obj is CivilDb.Structure)
            {
                Mark(obj.Database, obj.ObjectId);
            }
            else if (obj is CivilDb.Pipe p)
            {
                Mark(obj.Database, p.StartStructureId);
                Mark(obj.Database, p.EndStructureId);
            }
        }
        catch
        {
            // Nunca interromper a edicao do usuario.
        }
    }

    private static void Mark(AcadDb.Database db, AcadDb.ObjectId id)
    {
        if (id.IsNull)
            return;
        if (!Pending.TryGetValue(db, out HashSet<AcadDb.ObjectId>? set))
            Pending[db] = set = new HashSet<AcadDb.ObjectId>();
        set.Add(id);
    }

    private static void OnCommandEnded(object? sender, CommandEventArgs e)
    {
        if (sender is not Document doc)
            return;
        try
        {
            if (UndoCommands.Contains(e.GlobalCommandName))
            {
                Pending.Remove(doc.Database);
                return;
            }

            Process(doc);
        }
        catch (Exception ex)
        {
            PluginLog.Erro(Module, "Regras no fim do comando", ex);
        }
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (_applying)
            return;
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || PendingCount(doc.Database) == 0)
            return;
        try
        {
            if (!doc.Editor.IsQuiescent ||
                !string.IsNullOrEmpty(doc.CommandInProgress) ||
                Convert.ToInt32(AcApp.GetSystemVariable("CMDACTIVE")) != 0 ||
                System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None)
            {
                return;
            }

            Process(doc);
        }
        catch (Exception ex)
        {
            PluginLog.Erro(Module, "Regras no Idle", ex);
        }
    }

    private static void Process(Document doc)
    {
        AcadDb.Database db = doc.Database;
        if (_applying || !Pending.TryGetValue(db, out HashSet<AcadDb.ObjectId>? set) || set.Count == 0)
            return;
        HydroSettings settings = HydroStore.Load(db);
        if (!settings.RulesAuto)
            return;

        List<AcadDb.ObjectId> ids = set.ToList();
        set.Clear();
        HydroRules.Report report;
        using (doc.LockDocument())
        using (Applying())
            report = HydroRules.Apply(db, ids, settings.HoldMode);

        if (report.Changed > 0 || report.Errors.Count > 0)
        {
            doc.Editor.WriteMessage("\n[HYDRO] " + report.Summary + ".");
            foreach (string line in report.Lines.Take(8))
                doc.Editor.WriteMessage("\n  " + line);
            foreach (string err in report.Errors.Take(8))
                doc.Editor.WriteMessage("\n  aviso: " + err);
            HydroPanel.NotifyChanged();
        }
    }

    /// <summary>Liga/desliga a atualizacao automatica no desenho (salvo no NOD). Ao ligar, aplica o pendente.</summary>
    public static void SetAuto(Document doc, bool on)
    {
        AcadDb.Database db = doc.Database;
        HydroSettings s = HydroStore.Load(db);
        s.RulesAuto = on;
        HydroStore.Save(db, s);
        doc.Editor.WriteMessage(on
            ? "\n[HYDRO] Regras das estruturas: atualizacao automatica ligada."
            : "\n[HYDRO] Regras das estruturas: atualizacao automatica desligada; use HYREGRAS para atualizar.");
    }
}
