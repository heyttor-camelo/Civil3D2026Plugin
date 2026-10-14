using System;
using System.Collections.Generic;
using System.Linq;
using AcColors = Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Sinalizacao horizontal previa num bloco unico (VIARIO-SINALIZACAO) inserido
/// na origem. O conteudo do bloco e refeito quando a rede muda.
/// </summary>
internal static class ViarioSignageSync
{
    public const string BlockName = "VIARIO-SINALIZACAO";
    public const string LayerRef = "VIARIO-SINALIZACAO";
    public const string LayerWhite = "VIARIO-SH-BRANCA";
    public const string LayerYellow = "VIARIO-SH-AMARELA";
    public const string LayerOutline = "VIARIO-SH-CONTORNO";

    public static bool Exists(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        return bt.Has(BlockName);
    }

    /// <summary>Gera/atualiza o bloco. Retorna o numero de marcas (ou -1 se nao existe e create=false).</summary>
    public static int Sync(Document doc, IReadOnlyList<AxisInput> axes, ViarioResult result, bool create)
    {
        AcadDb.Database db = doc.Database;
        SignageOptions options = ViarioStore.LoadSignage(db);
        List<MarkLine> marks = ViarioSignage.Build(axes, result, options);

        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (!bt.Has(BlockName) && !create)
            return -1;

        EnsureLayer(tr, db, LayerRef, 7);
        EnsureLayer(tr, db, LayerWhite, 7);
        EnsureLayer(tr, db, LayerYellow, 2);
        EnsureLayer(tr, db, LayerOutline, 8, transparencyPercent: 50);

        AcadDb.BlockTableRecord block;
        if (bt.Has(BlockName))
        {
            block = (AcadDb.BlockTableRecord)tr.GetObject(bt[BlockName], AcadDb.OpenMode.ForWrite);
            foreach (AcadDb.ObjectId id in block)
                tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
        }
        else
        {
            bt.UpgradeOpen();
            block = new AcadDb.BlockTableRecord { Name = BlockName, Origin = Point3d.Origin };
            bt.Add(block);
            tr.AddNewlyCreatedDBObject(block, true);
        }

        foreach (MarkLine mark in marks)
        {
            AcadDb.Polyline pl;
            if (options.Vazada)
            {
                // So o contorno, cinza e transparente pela layer; nada pintado.
                List<Seg> ring = ViarioSignage.Outline(mark);
                if (ring.Count == 0)
                    continue;
                pl = ViarioStore.ToPolyline(ring, true);
                pl.SetDatabaseDefaults(db);
                pl.Layer = LayerOutline;
            }
            else
            {
                pl = ViarioStore.ToPolyline(mark.Segs, false);
                pl.SetDatabaseDefaults(db);
                pl.Layer = mark.Yellow ? LayerYellow : LayerWhite;
                pl.ConstantWidth = mark.Width;
            }

            pl.ColorIndex = 256;
            block.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        // Uma referencia na origem, no espaco do modelo. A referencia antiga e recriada:
        // so marcar RecordGraphicsModified deixava a tela com o desenho anterior do bloco
        // (visto apos UNDO), embora o conteudo do bloco estivesse certo.
        AcadDb.BlockTableRecord ms = (AcadDb.BlockTableRecord)tr.GetObject(bt[AcadDb.BlockTableRecord.ModelSpace], AcadDb.OpenMode.ForWrite);
        string refLayer = LayerRef;
        foreach (AcadDb.ObjectId id in block.GetBlockReferenceIds(true, false))
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForWrite) is AcadDb.BlockReference old)
            {
                if (old.OwnerId == ms.ObjectId)
                    refLayer = old.Layer;
                old.Erase();
            }
        }

        AcadDb.BlockReference br = new(Point3d.Origin, block.ObjectId);
        br.SetDatabaseDefaults(db);
        br.Layer = refLayer;
        ms.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);
        // Mantem a sinalizacao por baixo das linhas da rede.
        AcadDb.DrawOrderTable dot = (AcadDb.DrawOrderTable)tr.GetObject(ms.DrawOrderTableId, AcadDb.OpenMode.ForWrite);
        dot.MoveToBottom(new AcadDb.ObjectIdCollection { br.ObjectId });

        tr.Commit();
        return marks.Count;
    }

    public static bool Remove(Document doc)
    {
        AcadDb.Database db = doc.Database;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.BlockTable bt = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
        if (!bt.Has(BlockName))
            return false;

        AcadDb.BlockTableRecord block = (AcadDb.BlockTableRecord)tr.GetObject(bt[BlockName], AcadDb.OpenMode.ForWrite);
        foreach (AcadDb.ObjectId id in block.GetBlockReferenceIds(false, true))
            tr.GetObject(id, AcadDb.OpenMode.ForWrite).Erase();
        block.Erase();
        tr.Commit();
        return true;
    }

    /// <summary>Contagem por tipo para o painel/relatorio.</summary>
    public static string Summary(IEnumerable<MarkLine> marks) =>
        string.Join(", ", marks.GroupBy(m => m.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));

    /// <summary>Comando C3DVIARIOSINAL: [Gerar/Remover].</summary>
    public static void Command(Document doc)
    {
        Editor ed = doc.Editor;
        bool exists = Exists(doc.Database);
        PromptKeywordOptions options = new(
            "\nSinalizacao horizontal previa [Gerar/Remover/Painel] <Gerar>: ", "Gerar Remover Painel")
        {
            AllowNone = true
        };

        PromptResult r = ed.GetKeywords(options);
        if (r.Status == PromptStatus.Cancel || r.Status == PromptStatus.Error)
            return;

        string choice = r.Status == PromptStatus.OK ? r.StringResult : "Gerar";
        if (choice == "Remover")
        {
            ed.WriteMessage(Remove(doc) ? "\n  Sinalizacao removida." : "\n  Nao ha sinalizacao no desenho.");
            return;
        }

        if (choice == "Painel")
        {
            ViarioPanel.ShowPanel("Sinalizacao");
            return;
        }

        (List<AxisInput> axes, ViarioResult result, _) = ViarioSync.ComputeCurrent(doc.Database);
        int count = Sync(doc, axes, result, create: true);
        ed.WriteMessage($"\n  Sinalizacao {(exists ? "atualizada" : "criada")}: {count} marca(s) no bloco {BlockName} " +
                        "(acompanha a rede; opcoes no painel C3DVIARIOPAINEL > Sinalizacao).");
    }

    private static void EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db, string name, short color, int transparencyPercent = 0)
    {
        AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        if (table.Has(name))
            return;

        table.UpgradeOpen();
        AcadDb.LayerTableRecord layer = new()
        {
            Name = name,
            Color = AcColors.Color.FromColorIndex(AcColors.ColorMethod.ByAci, color)
        };
        table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);

        // Transparencia so depois de a layer estar no banco (antes da eNoDatabase).
        if (transparencyPercent > 0)
            layer.Transparency = new AcColors.Transparency((byte)Math.Round(255.0 * (100 - transparencyPercent) / 100.0));
    }
}
