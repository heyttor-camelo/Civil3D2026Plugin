using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using SW = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;
using Civil3D2026Plugin.UI.Common;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

/// <summary>
/// Posicao das etiquetas (R01, T01...) arrastadas no painel, guardada NO DESENHO (dicionario
/// C3D_MFREBAIXO_ETIQUETAS: chave do rebaixo/travessia -> deslocamento em metros a partir do ponto dele).
/// </summary>
internal static class MFRebaixoLabels
{
    private const string DictName = "C3D_MFREBAIXO_ETIQUETAS";

    public static Dictionary<string, Vector2d> Load(AcadDb.Database db)
    {
        var map = new Dictionary<string, Vector2d>(StringComparer.OrdinalIgnoreCase);
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        if (nod.Contains(DictName) && tr.GetObject(nod.GetAt(DictName), AcadDb.OpenMode.ForRead) is AcadDb.DBDictionary dict)
        {
            foreach (AcadDb.DBDictionaryEntry e in dict)
            {
                if (tr.GetObject(e.Value, AcadDb.OpenMode.ForRead) is AcadDb.Xrecord xr && xr.Data?.AsArray() is { Length: >= 2 } v)
                    map[e.Key] = new Vector2d(Convert.ToDouble(v[0].Value, CultureInfo.InvariantCulture), Convert.ToDouble(v[1].Value, CultureInfo.InvariantCulture));
            }
        }
        tr.Commit();
        return map;
    }

    /// <summary>Grava (ou, com null, apaga) o deslocamento de uma etiqueta.</summary>
    public static void Save(AcadDb.Database db, string key, Vector2d? offset)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        AcadDb.DBDictionary dict;
        if (nod.Contains(DictName))
        {
            dict = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(DictName), AcadDb.OpenMode.ForWrite);
        }
        else
        {
            if (offset is null)
            {
                tr.Commit();
                return;
            }
            nod.UpgradeOpen();
            dict = new AcadDb.DBDictionary();
            nod.SetAt(DictName, dict);
            tr.AddNewlyCreatedDBObject(dict, true);
        }

        string k = Clean(key);
        if (dict.Contains(k))
            tr.GetObject(dict.GetAt(k), AcadDb.OpenMode.ForWrite).Erase();
        if (offset is Vector2d o)
        {
            var xr = new AcadDb.Xrecord
            {
                Data = new AcadDb.ResultBuffer(new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, o.X), new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, o.Y))
            };
            dict.SetAt(k, xr);
            tr.AddNewlyCreatedDBObject(xr, true);
        }
        tr.Commit();
    }

    /// <summary>Apaga todas (Textos > Repor etiquetas).</summary>
    public static int Clear(AcadDb.Database db)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        var nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        int n = 0;
        if (nod.Contains(DictName))
        {
            var dict = (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(DictName), AcadDb.OpenMode.ForWrite);
            foreach (AcadDb.DBDictionaryEntry e in dict)
            {
                tr.GetObject(e.Value, AcadDb.OpenMode.ForWrite).Erase();
                n++;
            }
        }
        tr.Commit();
        return n;
    }

    public static string Clean(string key) => new(key.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
}

internal sealed partial class MFRebaixoPanel
{
    private Dictionary<string, Vector2d>? _tagOffsets;
    private Document? _tagOffsetsDoc;

    private Dictionary<string, Vector2d> TagOffsets()
    {
        if (Doc is not Document doc)
            return new Dictionary<string, Vector2d>();
        if (_tagOffsets is null || !ReferenceEquals(_tagOffsetsDoc, doc))
        {
            try
            {
                using (doc.LockDocument())
                    _tagOffsets = MFRebaixoLabels.Load(doc.Database);
            }
            catch
            {
                _tagOffsets = new Dictionary<string, Vector2d>(StringComparer.OrdinalIgnoreCase);
            }
            _tagOffsetsDoc = doc;
        }
        return _tagOffsets;
    }

    private static string TagKey(RebaixoInfo r) => "R_" + MFRebaixoLabels.Clean(r.SetName);

    private static string TagKey(TravessiaView t) => "T_" + MFRebaixoLabels.Clean(t.Id);

    /// <summary>Etiquetas ja reservadas: as travessias (o plato inteiro) para nenhuma etiqueta cair em cima.</summary>
    private void ReserveTravAreas()
    {
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        foreach (TravessiaView t in _travs)
        {
            foreach ((string part, List<Point2d> loop) in t.Plan)
            {
                if (part is not ("PLANTA_PLATO" or "UNIAO_PLANTA" or "PLANTA_RAMPA") || loop.Count < 3)
                    continue;
                List<SW.Point> pts = loop.Select(q => ToScreen(q.X, q.Y)).ToList();
                var box = new SW.Rect(new SW.Point(pts.Min(p => p.X), pts.Min(p => p.Y)), new SW.Point(pts.Max(p => p.X), pts.Max(p => p.Y)));
                if (box.Right < 0 || box.Bottom < 0 || box.Left > w || box.Top > h)
                    continue;
                _labelRects.Add(box);
            }
        }
    }

    /// <summary>
    /// Poe a etiqueta: na posicao arrastada pelo usuario (guardada no desenho) ou na primeira livre,
    /// preferindo o lado de fora (calcada, <paramref name="outward"/> em pixels) e longe das travessias.
    /// Arrastar com o botao esquerdo muda a posicao; clique sem arrastar chama <paramref name="click"/>.
    /// </summary>
    private void PlaceTag(WC.Border tag, SW.Point anchor, string key, bool force, SW.Vector? outward, Action<WI.MouseButtonEventArgs> click)
    {
        tag.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = tag.DesiredSize.Width, th = tag.DesiredSize.Height;
        SW.Rect? box = null;
        if (TagOffsets().TryGetValue(key, out Vector2d off) && _scale > 0)
        {
            // Deslocamento em metros (Y do desenho para cima; tela para baixo).
            box = Put(tag, new SW.Rect(anchor.X + (off.X * _scale), anchor.Y - (off.Y * _scale), tw, th));
        }
        else
        {
            if (outward is SW.Vector o && o.Length > 1e-6)
            {
                o.Normalize();
                foreach (double dist in new[] { 22.0, 36.0, 52.0 })
                {
                    var c = new SW.Point(anchor.X + (o.X * (dist + (tw / 2))), anchor.Y + (o.Y * (dist + (th / 2))));
                    var r = new SW.Rect(c.X - (tw / 2), c.Y - (th / 2), tw, th);
                    if (Inside(r) && !_labelRects.Any(x => x.IntersectsWith(r)))
                    {
                        box = Put(tag, r);
                        break;
                    }
                }
            }
            box ??= Place(tag, anchor, force);
        }
        if (box is not SW.Rect placed)
            return;

        WS.Line? leader = null;
        void UpdateLeader(SW.Rect r)
        {
            SW.Point end = Nearest(r, anchor);
            bool show = Show("CHAMADA") || (end - anchor).Length > 25;
            if (!show)
            {
                if (leader is not null)
                    leader.Visibility = SW.Visibility.Collapsed;
                return;
            }
            if (leader is null)
            {
                leader = new WS.Line { Stroke = tag.Background, StrokeThickness = 1.0, IsHitTestVisible = false };
                _plan.Children.Insert(Math.Max(0, _plan.Children.IndexOf(tag)), leader);
            }
            leader.Visibility = SW.Visibility.Visible;
            (leader.X1, leader.Y1, leader.X2, leader.Y2) = (anchor.X, anchor.Y, end.X, end.Y);
        }
        UpdateLeader(placed);

        SW.Point? grab = null;
        SW.Point start = default;
        bool moved = false;
        tag.Cursor = WI.Cursors.SizeAll;
        tag.ToolTip = (tag.ToolTip as string ?? "") + "\narrastar: mudar a etiqueta de lugar (fica guardado no desenho)";
        tag.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                click(e);
                return;
            }
            grab = e.GetPosition(_plan);
            start = new SW.Point(WC.Canvas.GetLeft(tag), WC.Canvas.GetTop(tag));
            moved = false;
            tag.CaptureMouse();
        };
        tag.MouseMove += (_, e) =>
        {
            if (grab is not SW.Point g0 || e.LeftButton != WI.MouseButtonState.Pressed)
                return;
            SW.Vector d = e.GetPosition(_plan) - g0;
            if (!moved && d.Length < 4)
                return;
            moved = true;
            WC.Canvas.SetLeft(tag, start.X + d.X);
            WC.Canvas.SetTop(tag, start.Y + d.Y);
            UpdateLeader(new SW.Rect(start.X + d.X, start.Y + d.Y, tw, th));
            e.Handled = true;
        };
        tag.MouseLeftButtonUp += (_, e) =>
        {
            if (grab is null)
                return;
            grab = null;
            tag.ReleaseMouseCapture();
            e.Handled = true;
            if (!moved)
            {
                click(e);
                return;
            }
            if (_scale <= 0 || Doc is not Document doc)
                return;
            var offset = new Vector2d((WC.Canvas.GetLeft(tag) - anchor.X) / _scale, -(WC.Canvas.GetTop(tag) - anchor.Y) / _scale);
            try
            {
                using (doc.LockDocument())
                    MFRebaixoLabels.Save(doc.Database, key, offset);
                TagOffsets()[key] = offset;
                Say("Etiqueta mudada de lugar (guardada no desenho; Textos > Repor etiquetas volta ao padrao).");
            }
            catch (System.Exception ex)
            {
                Say("Etiqueta nao guardada: " + ex.Message);
            }
        };
    }

    /// <summary>Textos > Repor etiquetas: todas voltam para a posicao automatica.</summary>
    private void ResetTags()
    {
        if (Doc is not Document doc)
            return;
        int n = 0;
        try
        {
            using (doc.LockDocument())
                n = MFRebaixoLabels.Clear(doc.Database);
        }
        catch (System.Exception ex)
        {
            Say("Erro: " + ex.Message);
            return;
        }
        _tagOffsets = null;
        RenderGraphic();
        Say($"{n} etiqueta(s) de volta para a posicao automatica.");
    }

    /// <summary>
    /// Seleciona no desenho (pickfirst, com grips) o que esta selecionado no painel: hachuras dos rebaixos
    /// ou pecas das travessias (com filtro). Serve para qualquer comando do Civil (IFC, layer, isolar).
    /// </summary>
    private void SelectInDrawing(string filter = "Tudo")
    {
        if (Doc is not Document doc)
            return;
        try
        {
            List<AcadDb.ObjectId> ids;
            string what;
            using (doc.LockDocument())
            {
                if (_category == "TRAVESSIAS")
                {
                    var travs = _list.SelectedItems.OfType<WC.ListViewItem>().Select(i => (i.Tag as Row)?.Data).OfType<TravessiaView>().ToList();
                    if (_currentTrav is { } cur && !travs.Contains(cur))
                        travs.Add(cur);
                    if (travs.Count == 0)
                    {
                        Say("Selecione uma ou mais travessias (Ctrl/Shift na lista ou no monitor).");
                        return;
                    }
                    ids = MFTravessia.Objects(doc.Database, travs.Select(t => t.Id).ToList(), filter);
                    what = $"{travs.Count} travessia(s), pecas: {filter.ToLowerInvariant()}";
                }
                else
                {
                    List<RebaixoInfo> rebs = SelectedRebaixos();
                    if (_current is { } cur && !rebs.Contains(cur))
                        rebs.Add(cur);
                    if (rebs.Count == 0)
                    {
                        Say("Selecione um ou mais rebaixos (Ctrl/Shift na lista ou no monitor).");
                        return;
                    }
                    ids = MFRebaixoHatch.HatchIds(doc.Database, rebs.Select(r => r.SetName));
                    what = $"{rebs.Count} rebaixo(s), hachuras";
                }
            }
            if (ids.Count == 0)
            {
                Say("Nada para selecionar no desenho com esse filtro.");
                return;
            }
            // A selecao e feita pelo comando MFREBAIXOSELECIONARPAINEL (dentro de comando e seguro; direto da
            // janela do painel o AutoCAD pode cair).
            MFRebaixoCommands.PendingSelection = ids.ToArray();
            Send(C3DCommands.Corridor.MfRebaixoSelecionarPainel + " ", "Selecionando no desenho...");
            string locked = _category == "TRAVESSIAS" ? "" : (_settings.HachuraTrancada ? $" A layer {_settings.HachuraLayer} esta trancada: para editar as hachuras, destranque (LAYUNLOCK)." : "");
            Say($"{ids.Count} objeto(s) selecionado(s) no desenho ({what}). Use o comando que quiser (ex. IFCInfraExportSelected para IFC 4.3).{locked}");
        }
        catch (System.Exception ex)
        {
            Say("Selecao no desenho falhou: " + ex.Message);
        }
    }

    /// <summary>Menu do filtro de pecas das travessias para "Selecionar no desenho".</summary>
    private void SelectTravInDrawingMenu(SW.FrameworkElement anchor)
    {
        var menu = new WC.ContextMenu { PlacementTarget = anchor };
        foreach ((string key, string text) in new[] { ("Tudo", "Tudo (3D e planta)"), ("Solidos", "So os solidos 3D"), ("Plato", "Plato"), ("Grelhas", "Grelhas / tampas"), ("Vigas", "Vigas baldrame"), ("Sinalizacao", "Sinalizacao 3D (faixa e triangulos)"), ("Planta", "Representacao em planta (hachuras)") })
        {
            var mi = new WC.MenuItem { Header = text };
            string k = key;
            mi.Click += (_, _) => SelectInDrawing(k);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>Codigo curto da travessia (T01, T02...): na ordem dos rebaixos dela (R01, R02...).</summary>
    private string TravCode(TravessiaView t)
    {
        List<TravessiaView> ordered = _travs.OrderBy(x => x.IsUnion ? 1 : 0)
            .ThenBy(x => x.Data is { } d ? Math.Min(CodeNumber(d.LabelA), CodeNumber(d.LabelB)) : int.MaxValue)
            .ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        int i = ordered.IndexOf(t);
        return i < 0 ? "T?" : (t.IsUnion ? "U" : "T") + (i + 1).ToString("00", CultureInfo.InvariantCulture);
    }

    private static int CodeNumber(string label)
    {
        string digits = new(label.Skip(1).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : int.MaxValue;
    }

    /// <summary>Etiquetas das travessias na planta (T01 R19-R20), arrastaveis como as dos rebaixos.</summary>
    private void DrawTravLabels()
    {
        if (_scale <= 0 || _travs.Count == 0 || !Show("TRAVESSIAS"))
            return;
        double w = _plan.ActualWidth, h = _plan.ActualHeight;
        foreach (TravessiaView t in _travs.OrderBy(x => ReferenceEquals(x, _currentTrav) ? 0 : 1))
        {
            List<Point2d>? loop = t.Plan.Where(x => x.Part is "PLANTA_PLATO" or "UNIAO_PLANTA").OrderByDescending(x => x.Loop.Count).Select(x => x.Loop).FirstOrDefault();
            if (loop is null || loop.Count < 3)
                continue;
            // Ancora na borda de cima do plato (na tela), saindo para cima: nao cobre a travessia.
            List<SW.Point> sp = loop.Select(q => ToScreen(q.X, q.Y)).ToList();
            SW.Point center = new(sp.Average(q => q.X), sp.Average(q => q.Y));
            if (center.X < 0 || center.Y < 0 || center.X > w || center.Y > h)
                continue;
            SW.Point anchor = new(center.X, sp.Min(q => q.Y));
            bool selected = ReferenceEquals(t, _currentTrav) && _category == "TRAVESSIAS";
            string text = TravCode(t) + (t.Data is { } d && !t.IsUnion ? $"  {d.LabelA}-{d.LabelB}" : "");
            var tag = new WC.Border
            {
                Child = new WC.TextBlock { Text = text, FontWeight = SW.FontWeights.Bold, FontSize = selected ? 12 : 11, Foreground = DarkText },
                Background = selected ? SelectedBrush : ToolTheme.Brush(90, 170, 230),
                BorderBrush = DarkText,
                BorderThickness = new SW.Thickness(selected ? 1.5 : 0.8),
                CornerRadius = new SW.CornerRadius(3),
                Padding = new SW.Thickness(4, 1, 4, 1),
                ToolTip = $"Travessia {TravName(t)}\nclique: selecionar   duplo clique: 3D"
            };
            TravessiaView item = t;
            PlaceTag(tag, anchor, TagKey(t), force: true, new SW.Vector(0, -1), e =>
            {
                SelectTravFrom3D(item, "PLATO");
                if (e.ClickCount == 2)
                    SetMode("3D");
            });
        }
    }
}
