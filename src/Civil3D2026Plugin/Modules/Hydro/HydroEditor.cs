using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Escrita nos objetos nativos da Pipe Network. Cada metodo abre a sua transacao e e chamado
/// dentro de um comando (HYAPLICAR ou um HY*), para virar um unico UNDO. Convencoes:
/// - fundo (invert) = eixo da ponta - InnerHeight/2, como no DRENEXCEL;
/// - declividade positiva descendo no sentido do fluxo; SetSlopeHold* recebe o sinal negativo.
/// </summary>
internal static class HydroEditor
{
    private const string Module = "Hydro";

    private static T? Open<T>(AcadDb.Transaction tr, AcadDb.Database db, string handle, AcadDb.OpenMode mode) where T : AcadDb.DBObject
    {
        AcadDb.ObjectId? id = HydroReader.Id(db, handle);
        return id is null ? null : tr.GetObject(id.Value, mode, false) as T;
    }

    // ------------------------------------------------------------------
    // Estruturas
    // ------------------------------------------------------------------

    /// <summary>
    /// Nome, tampa e fundo (ou profundidade = tampa - fundo, medida na tampa atual do desenho). Ao
    /// mudar o fundo a tampa fica onde esta e a altura da peca (SRS) acompanha; os tubos ligados
    /// acompanham (movePipes): segurando a outra ponta (muda a declividade) ou a declividade (tubo
    /// inteiro sobe/desce).
    /// </summary>
    public static void EditStructure(AcadDb.Database db, string handle, string? name, double? rim, double? sump, HydroStore.HoldMode hold, bool movePipes = true, double? depth = null)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Structure? s = Open<CivilDb.Structure>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (s is null)
            throw new InvalidOperationException("Estrutura nao encontrada (handle " + handle + ").");

        if (name is not null && name.Length > 0 && name != s.Name)
            s.Name = name;

        double rimTarget = rim ?? s.RimElevation;
        if (depth.HasValue && !sump.HasValue)
            sump = rimTarget - depth.Value;

        if (rim.HasValue && Math.Abs(rim.Value - s.RimElevation) > 1e-6)
        {
            // Com o ajuste automatico pela superficie ligado o Civil desfaz a tampa digitada.
            if (s.AutomaticRimSurfaceAdjustment)
            {
                s.AutomaticRimSurfaceAdjustment = false;
                PluginLog.Info(Module, $"{s.Name}: ajuste automatico da tampa pela superficie desligado para aceitar a tampa digitada.");
            }

            s.RimElevation = rim.Value;
        }

        if (sump.HasValue && Math.Abs(sump.Value - s.SumpElevation) > 1e-6)
        {
            double delta = sump.Value - s.SumpElevation;
            if (s.ControlSumpBy != CivilDb.StructureControlSumpType.ByElevation)
                s.ControlSumpBy = CivilDb.StructureControlSumpType.ByElevation;
            s.SumpElevation = sump.Value;

            if (movePipes)
            {
                for (int i = 0; i < s.ConnectedPipesCount; i++)
                {
                    AcadDb.ObjectId pid = s.get_ConnectedPipe(i);
                    if (tr.GetObject(pid, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Pipe p)
                        continue;
                    bool atStart = p.StartStructureId == s.ObjectId;
                    Point3d sp = p.StartPoint, ep = p.EndPoint;
                    if (hold == HydroStore.HoldMode.Declividade)
                    {
                        p.StartPoint = new Point3d(sp.X, sp.Y, sp.Z + delta);
                        p.EndPoint = new Point3d(ep.X, ep.Y, ep.Z + delta);
                    }
                    else if (atStart)
                    {
                        p.StartPoint = new Point3d(sp.X, sp.Y, sp.Z + delta);
                    }
                    else
                    {
                        p.EndPoint = new Point3d(ep.X, ep.Y, ep.Z + delta);
                    }
                }
            }

            // O Civil guarda a altura tampa-fundo no PartData (SRS) e pode refazer a tampa a partir
            // dela: grava a altura nova e repoe tampa/fundo se algum dos dois andou.
            SyncRimToSump(s, rimTarget - sump.Value);
            if (!s.AutomaticRimSurfaceAdjustment && Math.Abs(s.RimElevation - rimTarget) > 1e-4)
                s.RimElevation = rimTarget;
            if (Math.Abs(s.SumpElevation - sump.Value) > 1e-4)
                s.SumpElevation = sump.Value;
            if (Math.Abs(s.RimElevation - rimTarget) > 1e-3)
                PluginLog.Aviso(Module, $"{s.Name}: tampa {s.RimElevation:0.000} depois do fundo (esperada {rimTarget:0.000}).");
        }

        tr.Commit();
    }

    /// <summary>Altura tampa-fundo da peca (contexto StructRimToSumpHeight), se o catalogo aceitar.</summary>
    private static void SyncRimToSump(CivilDb.Structure s, double meters)
    {
        try
        {
            using CivilDb.PartDataRecord rec = s.PartData;
            CivilDb.PartDataField f = rec.GetDataFieldBy(CivilDb.PartContextType.StructRimToSumpHeight);
            if (f.IsReadOnly || meters <= 0)
                return;
            if (!IsTrueRimToSump(rec, f, out string why))
            {
                PluginLog.Aviso(Module, $"{s.Name}: altura tampa-fundo nao gravada no PartData ({why}); confira ids duplicados no catalogo (tools/Fix-DuplicateColumnIds.ps1).");
                return;
            }
            double v = meters / HydroRules.Factor(f.Units);
            if (f.Value is double cur && Math.Abs(cur - v) < 1e-6 * Math.Max(1, Math.Abs(v)))
                return;
            if (f.IsFromRange)
            {
                using CivilDb.PartDataRange r = f.ValueRange;
                if (!r.IsValidValue(v))
                {
                    PluginLog.Aviso(Module, $"{s.Name}: altura {meters:0.000} m fora da faixa do catalogo ({r.RangeMin}..{r.RangeMax} {f.Units}).");
                    return;
                }
            }
            else if (f.IsFromList)
            {
                return;
            }

            f.Value = v;
            s.PartData = rec;
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, $"{s.Name}: SRS nao gravado: {ex.Message}");
        }
    }

    /// <summary>
    /// Trava contra catalogos com id de coluna duplicado (ex. SIL e SRS ambos CCR1): o campo do
    /// contexto StructRimToSumpHeight so e gravado se for mesmo o SRS - nome SRS, ou nome/indice
    /// que nenhum outro campo do registro repete.
    /// </summary>
    private static bool IsTrueRimToSump(CivilDb.PartDataRecord rec, CivilDb.PartDataField f, out string why)
    {
        why = "";
        string name = f.Name ?? "";
        bool conflict = false;
        foreach (CivilDb.PartDataField o in rec.GetAllDataFields())
        {
            if (o.Context == CivilDb.PartContextType.StructRimToSumpHeight)
                continue;
            // Index so conta dentro do mesmo contexto: as tabelas _IPE tem index 0 em cada contexto (SRS, WTh...).
            if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase) || (o.Index == f.Index && o.Context == f.Context))
            {
                conflict = true;
                why = $"campo '{name}' conflita com '{o.Name}' ({o.ContextString})";
                break;
            }
        }

        if (string.Equals(name, "SRS", StringComparison.OrdinalIgnoreCase))
            return !conflict;
        if (conflict)
            return false;
        if (name.Length == 0)
        {
            why = "campo sem nome";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Grava parametros dimensionais da peca (PartData): le o registro, muda os campos pedidos e
    /// devolve o registro a peca (Part.PartData tem setter na API 2026). Valores fora da faixa
    /// Min/Max do catalogo sao recusados com mensagem. Retorna o que foi aplicado.
    /// </summary>
    public static string SetParameters(AcadDb.Database db, string handle, IReadOnlyDictionary<string, double> values)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Part? part = Open<CivilDb.Part>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (part is null)
            throw new InvalidOperationException("Peca nao encontrada (handle " + handle + ").");
        List<string> applied = new(), refused = new();
        using (CivilDb.PartDataRecord rec = part.PartData)
        {
            foreach ((string name, double value) in values)
            {
                CivilDb.PartDataField f;
                try
                {
                    f = rec.GetDataFieldBy(name);
                }
                catch
                {
                    refused.Add($"{name}: a peca nao tem esse parametro");
                    continue;
                }

                if (f.IsReadOnly)
                {
                    refused.Add($"{name}: somente leitura");
                    continue;
                }

                try
                {
                    if (f.IsFromRange)
                    {
                        using CivilDb.PartDataRange r = f.ValueRange;
                        if (!r.IsValidValue(value))
                        {
                            double k = HydroRules.Factor(f.Units);
                            string M(object o) => (Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture) * k).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
                            refused.Add($"{HydroNames.Long(f.ContextString, f.Description, name)} {M(value)} m fora da faixa da familia ({M(r.RangeMin)} a {M(r.RangeMax)} m; para liberar rode C:\\Temp\\Ampliar-Faixas-FDX.ps1)");
                            continue;
                        }
                    }
                    else if (f.IsFromList)
                    {
                        using CivilDb.PartDataList l = f.ValueList;
                        if (!l.IsValidValue(value))
                        {
                            refused.Add($"{name}: {value} nao esta na lista do catalogo");
                            continue;
                        }
                    }
                }
                catch
                {
                    // Sem faixa/lista legivel: tenta gravar assim mesmo.
                }

                f.Value = value;
                applied.Add($"{name}={value}");
            }

            if (applied.Count > 0)
                part.PartData = rec;
        }

        tr.Commit();
        PluginLog.Info(Module, $"{part.Name}: parametros {string.Join(", ", applied)}" + (refused.Count > 0 ? "; recusados: " + string.Join("; ", refused) : ""));
        // Recusa nunca calada: o painel e as regras nao mostram o retorno, entao avisa na linha de comando.
        if (refused.Count > 0)
            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[HYDRO] {part.Name}: nao gravado - {string.Join("; ", refused)}.");
        return (applied.Count > 0 ? $"{applied.Count} parametro(s) gravado(s)" : "nada gravado") + (refused.Count > 0 ? " (" + string.Join("; ", refused) + ")" : "");
    }

    /// <summary>Move a estrutura em planta; os tubos ligados acompanham (o Civil mantem a conexao).</summary>
    public static void MoveStructure(AcadDb.Database db, string handle, Point3d to, bool keepSlopes)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Structure? s = Open<CivilDb.Structure>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (s is null)
            throw new InvalidOperationException("Estrutura nao encontrada.");

        // Guarda declividade e fundo de cada tubo ligado para repor depois do movimento.
        List<(AcadDb.ObjectId Id, bool AtStart, double Slope, double OtherZ)> pipes = new();
        for (int i = 0; i < s.ConnectedPipesCount; i++)
        {
            AcadDb.ObjectId pid = s.get_ConnectedPipe(i);
            if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is CivilDb.Pipe p)
            {
                bool atStart = p.StartStructureId == s.ObjectId;
                pipes.Add((pid, atStart, p.Slope, atStart ? p.EndPoint.Z : p.StartPoint.Z));
            }
        }

        s.Location = new Point3d(to.X, to.Y, s.Location.Z);

        foreach ((AcadDb.ObjectId id, bool atStart, double slope, double otherZ) in pipes)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForWrite, false) is not CivilDb.Pipe p)
                continue;
            Point3d sp = p.StartPoint, ep = p.EndPoint;
            Point3d here = new(to.X, to.Y, atStart ? sp.Z : ep.Z);
            if (atStart)
                p.StartPoint = here;
            else
                p.EndPoint = here;
            if (keepSlopes)
            {
                // Mantem a declividade geometrica a partir da outra ponta.
                if (atStart)
                    p.SetSlopeHoldEnd(slope);
                else
                    p.SetSlopeHoldStart(slope);
            }
        }

        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Tubos
    // ------------------------------------------------------------------

    /// <summary>
    /// Nome, fundo de montante, fundo de jusante e declividade (segurando montante). Com fundo de
    /// montante e declividade, o jusante sai da declividade; com os dois fundos, a declividade sai deles.
    /// </summary>
    public static void EditPipe(AcadDb.Database db, string handle, string? name, double? upInvert, double? downInvert, double? slope)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado (handle " + handle + ").");

        if (name is not null && name.Length > 0 && name != p.Name)
            p.Name = name;

        bool startUp = p.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
        double half = (p.InnerHeight > 0 ? p.InnerHeight : p.InnerDiameterOrWidth) / 2.0;

        if (upInvert.HasValue)
            SetEndZ(p, startUp, upInvert.Value + half);
        if (downInvert.HasValue)
            SetEndZ(p, !startUp, downInvert.Value + half);
        if (slope.HasValue)
        {
            double s = Math.Abs(slope.Value);
            if (startUp)
                p.SetSlopeHoldStart(-s);
            else
                p.SetSlopeHoldEnd(-s);
        }

        tr.Commit();
    }

    /// <summary>Declividade (m/m, positiva descendo) segurando a ponta de montante ou a de jusante.</summary>
    public static void SetSlope(AcadDb.Database db, string handle, double slope, bool holdUpstream)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado.");
        bool startUp = p.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
        double s = Math.Abs(slope);
        bool holdStart = startUp == holdUpstream;
        // Segurando o inicio, a queda e no sentido start->end (negativa) quando o inicio e montante.
        if (holdStart)
            p.SetSlopeHoldStart(startUp ? -s : s);
        else
            p.SetSlopeHoldEnd(startUp ? -s : s);
        tr.Commit();
    }

    /// <summary>"Match properties" de rede: Tudo, Declividade/Profundidade, Familia ou Regras.</summary>
    public static int CopyProperties(AcadDb.Database db, string sourceHandle, IEnumerable<string> targets, string what)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Part? src = Open<CivilDb.Part>(tr, db, sourceHandle, AcadDb.OpenMode.ForRead);
        if (src is null)
            throw new InvalidOperationException("Peca de origem nao encontrada.");
        bool all = what == "Tudo";
        AcadDb.ObjectId sizeId = AcadDb.ObjectId.Null;
        if (all || what == "Familia")
            sizeId = SizeIdOf(tr, src);

        foreach (string h in targets)
        {
            CivilDb.Part? dst = Open<CivilDb.Part>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (dst is null || dst.GetType() != src.GetType())
                continue;
            if ((all || what == "Familia") && !sizeId.IsNull && (dst.PartFamilyId != src.PartFamilyId || SizeIdOf(tr, dst) != sizeId))
            {
                double zUp = 0, zDown = 0;
                if (dst is CivilDb.Pipe pd)
                {
                    zUp = pd.StartPoint.Z - (pd.InnerHeight / 2.0);
                    zDown = pd.EndPoint.Z - (pd.InnerHeight / 2.0);
                }

                dst.SwapPartFamilyAndSize(src.PartFamilyId, sizeId);
                if (dst is CivilDb.Pipe pd2)
                {
                    double half = pd2.InnerHeight / 2.0;
                    pd2.StartPoint = new Point3d(pd2.StartPoint.X, pd2.StartPoint.Y, zUp + half);
                    pd2.EndPoint = new Point3d(pd2.EndPoint.X, pd2.EndPoint.Y, zDown + half);
                }
            }

            if (all || what == "Regras")
            {
                if (!src.RuleSetStyleId.IsNull)
                    dst.RuleSetStyleId = src.RuleSetStyleId;
            }

            if (src is CivilDb.Pipe sp && dst is CivilDb.Pipe dp && (all || what == "Declividade"))
            {
                bool srcStartUp = sp.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
                double srcSlope = sp.Length2D > 1e-9 ? ((srcStartUp ? sp.StartPoint.Z : sp.EndPoint.Z) - (srcStartUp ? sp.EndPoint.Z : sp.StartPoint.Z)) / sp.Length2D : 0;
                bool dstStartUp = dp.FlowDirection != CivilDb.FlowDirectionType.EndToStart;
                if (dstStartUp)
                    dp.SetSlopeHoldStart(-Math.Abs(srcSlope));
                else
                    dp.SetSlopeHoldEnd(-Math.Abs(srcSlope));
            }

            if (src is CivilDb.Structure ss && dst is CivilDb.Structure ds && (all || what == "Profundidade"))
            {
                double depth = ss.RimElevation - ss.SumpElevation;
                if (ds.ControlSumpBy != CivilDb.StructureControlSumpType.ByElevation)
                    ds.ControlSumpBy = CivilDb.StructureControlSumpType.ByElevation;
                ds.SumpElevation = ds.RimElevation - depth;
            }

            n++;
        }

        tr.Commit();
        return n;
    }

    /// <summary>Id do PartSize atual da peca (pelo nome do tamanho dentro da familia).</summary>
    private static AcadDb.ObjectId SizeIdOf(AcadDb.Transaction tr, CivilDb.Part part)
    {
        try
        {
            if (tr.GetObject(part.PartFamilyId, AcadDb.OpenMode.ForRead, false) is not CivilDb.Styles.PartFamily fam)
                return AcadDb.ObjectId.Null;
            string sizeName = part.PartSizeName;
            for (int i = 0; i < fam.PartSizeCount; i++)
            {
                AcadDb.ObjectId sid = fam[i];
                if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartSize size)
                {
                    try
                    {
                        if (string.Equals(size.SizeDataRecord.GetDataFieldBy("SizeName").Value?.ToString(), sizeName, StringComparison.OrdinalIgnoreCase))
                            return sid;
                    }
                    catch
                    {
                        // Sem campo de nome: segue a comparacao pelo diametro.
                    }
                }
            }

            // Fallback: o tamanho com o mesmo diametro/largura interna.
            double inner = part is CivilDb.Pipe pp ? pp.InnerDiameterOrWidth : part is CivilDb.Structure st ? st.InnerDiameterOrWidth : 0;
            CivilDb.PartContextType ctx = part is CivilDb.Pipe ? CivilDb.PartContextType.PipeInnerDiameter : CivilDb.PartContextType.StructInnerDiameter;
            for (int i = 0; i < fam.PartSizeCount; i++)
            {
                AcadDb.ObjectId sid = fam[i];
                if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartSize size)
                {
                    try
                    {
                        if (size.SizeDataRecord.GetDataFieldBy(ctx).Value is double d && Math.Abs(d - inner) < 1e-6)
                            return sid;
                    }
                    catch
                    {
                        // Contexto ausente.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Tamanho atual nao identificado: " + ex.Message);
        }

        return AcadDb.ObjectId.Null;
    }

    /// <summary>Insere uma estrutura no meio do tubo (Network.BreakPipe): divide o tubo, mantem a declividade.</summary>
    public static (string Structure, string Pipe) InsertStructure(AcadDb.Database db, string pipeHandle, Point3d point, AcadDb.ObjectId familyId, AcadDb.ObjectId sizeId)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, pipeHandle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado.");
        Point3d on = ((AcadDb.Curve)p).GetClosestPointTo(new Point3d(point.X, point.Y, p.StartPoint.Z), false);
        CivilDb.Network net = (CivilDb.Network)tr.GetObject(p.NetworkId, AcadDb.OpenMode.ForWrite, false);
        AcadDb.ObjectId newStructure = AcadDb.ObjectId.Null;
        net.AddStructure(familyId, sizeId, new Point3d(on.X, on.Y, 0), 0, ref newStructure, false);
        AcadDb.ObjectId newPipe = AcadDb.ObjectId.Null;
        CivilDb.FlowDirectionMethodType method = p.FlowDirectionMethod;
        net.BreakPipe(p.ObjectId, on, newStructure, ref newPipe);
        if (!newPipe.IsNull && tr.GetObject(newPipe, AcadDb.OpenMode.ForWrite, false) is CivilDb.Pipe np)
            np.FlowDirectionMethod = method;
        if (tr.GetObject(newStructure, AcadDb.OpenMode.ForWrite, false) is CivilDb.Structure s)
        {
            // Fundo no menor fundo dos tubos ligados; tampa pela superficie se houver, senao a do tubo + 1,5 m.
            double lowest = double.MaxValue;
            for (int i = 0; i < s.ConnectedPipesCount; i++)
            {
                if (tr.GetObject(s.get_ConnectedPipe(i), AcadDb.OpenMode.ForRead, false) is CivilDb.Pipe cp)
                    lowest = Math.Min(lowest, Math.Min(cp.StartPoint.Z, cp.EndPoint.Z) - (cp.InnerHeight / 2.0));
            }

            if (lowest < double.MaxValue)
            {
                s.ControlSumpBy = CivilDb.StructureControlSumpType.ByElevation;
                s.SumpElevation = lowest;
                double surf = s.SurfaceElevationAtInsertionPoint;
                if (double.IsNaN(surf) || Math.Abs(surf) > 1e6 || surf < lowest + 0.3)
                    s.RimElevation = lowest + 1.5;
            }
        }

        string sh = newStructure.Handle.ToString(), ph = newPipe.IsNull ? "" : newPipe.Handle.ToString();
        tr.Commit();
        return (sh, ph);
    }

    /// <summary>Remove a estrutura e emenda os dois tubos (o de entrada e esticado ate a estrutura seguinte).</summary>
    public static string RemoveStructure(AcadDb.Database db, string handle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Structure? s = Open<CivilDb.Structure>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (s is null)
            throw new InvalidOperationException("Estrutura nao encontrada.");
        if (s.ConnectedPipesCount != 2)
            return $"{s.Name} tem {s.ConnectedPipesCount} tubo(s) ligado(s): so remove com exatamente 2 (um entrando e um saindo).";

        CivilDb.Pipe a = (CivilDb.Pipe)tr.GetObject(s.get_ConnectedPipe(0), AcadDb.OpenMode.ForWrite, false);
        CivilDb.Pipe b = (CivilDb.Pipe)tr.GetObject(s.get_ConnectedPipe(1), AcadDb.OpenMode.ForWrite, false);
        // Mantem o tubo que entra (jusante dele e a estrutura); o outro e apagado.
        bool aIn = (a.FlowDirection == CivilDb.FlowDirectionType.StartToEnd && a.EndStructureId == s.ObjectId) ||
                   (a.FlowDirection == CivilDb.FlowDirectionType.EndToStart && a.StartStructureId == s.ObjectId);
        CivilDb.Pipe keep = aIn ? a : b, drop = aIn ? b : a;
        bool keepEndHere = keep.EndStructureId == s.ObjectId;
        AcadDb.ObjectId far = drop.StartStructureId == s.ObjectId ? drop.EndStructureId : drop.StartStructureId;
        Point3d farPoint = drop.StartStructureId == s.ObjectId ? drop.EndPoint : drop.StartPoint;
        string name = s.Name, dropName = drop.Name, keepName = keep.Name;

        CivilDb.ConnectorPositionType pos = keepEndHere ? CivilDb.ConnectorPositionType.End : CivilDb.ConnectorPositionType.Start;
        keep.Disconnect(pos);
        drop.Disconnect(CivilDb.ConnectorPositionType.Start);
        drop.Disconnect(CivilDb.ConnectorPositionType.End);
        if (keepEndHere)
            keep.EndPoint = farPoint;
        else
            keep.StartPoint = farPoint;
        if (!far.IsNull)
            keep.ConnectToStructure(pos, far, true);
        drop.Erase();
        s.Erase();
        tr.Commit();
        return $"{name} removida; {dropName} apagado e {keepName} esticado ate a estrutura seguinte.";
    }

    /// <summary>Caminho mais curto entre duas estruturas (Network.FindShortestNetworkPath).</summary>
    public static (List<AcadDb.ObjectId> Ids, double Length) ShortestPath(AcadDb.Database db, AcadDb.ObjectId a, AcadDb.ObjectId b)
    {
        double len = 0;
        List<AcadDb.ObjectId> ids = new();
        try
        {
            AcadDb.ObjectIdCollection col = CivilDb.Network.FindShortestNetworkPath(a, b, ref len);
            foreach (AcadDb.ObjectId id in col)
                ids.Add(id);
        }
        catch (Exception ex)
        {
            PluginLog.Aviso(Module, "FindShortestNetworkPath: " + ex.Message);
        }

        return (ids, len);
    }

    private static void SetEndZ(CivilDb.Pipe p, bool start, double z)
    {
        if (start)
        {
            Point3d sp = p.StartPoint;
            p.StartPoint = new Point3d(sp.X, sp.Y, z);
        }
        else
        {
            Point3d ep = p.EndPoint;
            p.EndPoint = new Point3d(ep.X, ep.Y, z);
        }
    }

    /// <summary>Inverte o sentido do fluxo (bidirecional vira Start > End).</summary>
    public static void Reverse(AcadDb.Database db, IEnumerable<string> handles)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (p is null)
                continue;
            p.FlowDirectionMethod = p.FlowDirection == CivilDb.FlowDirectionType.StartToEnd
                ? CivilDb.FlowDirectionMethodType.EndToStart
                : CivilDb.FlowDirectionMethodType.StartToEnd;
        }

        tr.Commit();
    }

    /// <summary>Aplica o rule set do Civil (Part.ApplyRules) em tubos e estruturas.</summary>
    public static int ApplyRules(AcadDb.Database db, IEnumerable<string> handles)
    {
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            CivilDb.Part? part = Open<CivilDb.Part>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (part is null)
                continue;
            try
            {
                part.ApplyRules();
                n++;
            }
            catch (Exception ex)
            {
                PluginLog.Aviso(Module, $"ApplyRules em {part.Name}: {ex.Message}");
            }
        }

        tr.Commit();
        return n;
    }

    /// <summary>Troca o tamanho do tubo pelo diametro interno (tamanho mais proximo da familia).</summary>
    public static void ResizePipe(AcadDb.Database db, string handle, double innerDiameter)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado.");
        double before = p.InnerDiameterOrWidth;
        double zUp = p.StartPoint.Z - (p.InnerHeight / 2.0), zDown = p.EndPoint.Z - (p.InnerHeight / 2.0);
        p.ResizeByInnerDiameterOrWidth(innerDiameter, true);
        // Mantem os fundos (o Civil pode segurar o eixo ao trocar o tamanho).
        double half = p.InnerHeight / 2.0;
        p.StartPoint = new Point3d(p.StartPoint.X, p.StartPoint.Y, zUp + half);
        p.EndPoint = new Point3d(p.EndPoint.X, p.EndPoint.Y, zDown + half);
        PluginLog.Info(Module, $"{p.Name}: diametro {before * 1000:0} -> {p.InnerDiameterOrWidth * 1000:0} mm");
        tr.Commit();
    }

    /// <summary>Troca familia e tamanho mantendo as conexoes (Part.SwapPartFamilyAndSize).</summary>
    public static int Swap(AcadDb.Database db, IEnumerable<string> handles, AcadDb.ObjectId familyId, AcadDb.ObjectId sizeId) =>
        Swap(db, handles, familyId, sizeId, out _);

    /// <summary>Como Swap, peca a peca: a que o Civil recusar e pulada (motivo em skipped) e as outras seguem.</summary>
    public static int Swap(AcadDb.Database db, IEnumerable<string> handles, AcadDb.ObjectId familyId, AcadDb.ObjectId sizeId, out List<string> skipped)
    {
        int n = 0;
        skipped = new List<string>();
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        foreach (string h in handles)
        {
            CivilDb.Part? part = Open<CivilDb.Part>(tr, db, h, AcadDb.OpenMode.ForWrite);
            if (part is null)
                continue;
            string name = h;
            try
            {
                name = part.Name;
                bool isPipe = part is CivilDb.Pipe;
                double zUp = 0, zDown = 0;
                if (part is CivilDb.Pipe pp)
                {
                    zUp = pp.StartPoint.Z - (pp.InnerHeight / 2.0);
                    zDown = pp.EndPoint.Z - (pp.InnerHeight / 2.0);
                }

                part.SwapPartFamilyAndSize(familyId, sizeId);
                if (isPipe && part is CivilDb.Pipe p2)
                {
                    double half = p2.InnerHeight / 2.0;
                    p2.StartPoint = new Point3d(p2.StartPoint.X, p2.StartPoint.Y, zUp + half);
                    p2.EndPoint = new Point3d(p2.EndPoint.X, p2.EndPoint.Y, zDown + half);
                }

                n++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{name}: {ex.Message}");
                PluginLog.Aviso(Module, $"Troca pulada em {name}: {ex.Message}");
            }
        }

        tr.Commit();
        return n;
    }

    /// <summary>Liga a ponta (Start/End) do tubo a estrutura; estrutura vazia desliga.</summary>
    public static void Connect(AcadDb.Database db, string pipeHandle, bool start, string? structureHandle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Pipe? p = Open<CivilDb.Pipe>(tr, db, pipeHandle, AcadDb.OpenMode.ForWrite);
        if (p is null)
            throw new InvalidOperationException("Tubo nao encontrado.");
        CivilDb.ConnectorPositionType pos = start ? CivilDb.ConnectorPositionType.Start : CivilDb.ConnectorPositionType.End;
        if (string.IsNullOrEmpty(structureHandle))
        {
            p.Disconnect(pos);
        }
        else
        {
            AcadDb.ObjectId? sid = HydroReader.Id(db, structureHandle);
            if (sid is null)
                throw new InvalidOperationException("Estrutura nao encontrada.");
            CivilDb.Structure s = (CivilDb.Structure)tr.GetObject(sid.Value, AcadDb.OpenMode.ForWrite, false);
            p.ConnectToStructure(pos, s.ObjectId, true);
        }

        tr.Commit();
    }

    // ------------------------------------------------------------------
    // Bacias
    // ------------------------------------------------------------------

    public static void EditCatchment(AcadDb.Database db, string handle, double? c, double? tc, string? structureHandle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Catchment? cat = Open<CivilDb.Catchment>(tr, db, handle, AcadDb.OpenMode.ForWrite);
        if (cat is null)
            throw new InvalidOperationException("Bacia nao encontrada.");
        if (c.HasValue)
            cat.RunoffCoefficient = c.Value;
        if (tc.HasValue)
        {
            cat.TimeOfConcentrationCalculationMethod = CivilDb.TimeOfConcentrationCalculationMethod.CalculationMethodUserDefined;
            cat.TimeOfConcentration = tc.Value;
        }

        if (structureHandle is not null)
        {
            if (structureHandle.Length == 0)
            {
                CatchmentApi.SetDischarge(cat, AcadDb.ObjectId.Null);
            }
            else
            {
                AcadDb.ObjectId? sid = HydroReader.Id(db, structureHandle);
                if (sid is null)
                    throw new InvalidOperationException("Estrutura nao encontrada.");
                CatchmentApi.SetDischarge(cat, sid.Value);
            }
        }

        tr.Commit();
    }

    /// <summary>Cria um catchment a partir de uma polilinha fechada (grupo criado se nao existir).</summary>
    public static string CreateCatchment(AcadDb.Database db, AcadDb.ObjectId polylineId, string groupName, string name, double c, double tc, string? structureHandle)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(polylineId, AcadDb.OpenMode.ForRead, false) is not AcadDb.Curve curve)
            throw new InvalidOperationException("Selecione uma polilinha fechada.");
        Point3dCollection pts = new();
        if (curve is AcadDb.Polyline pl)
        {
            for (int i = 0; i < pl.NumberOfVertices; i++)
                pts.Add(pl.GetPoint3dAt(i));
        }
        else
        {
            double len = curve.GetDistanceAtParameter(curve.EndParam);
            int n = Math.Max(12, (int)(len / 2));
            for (int i = 0; i < n; i++)
                pts.Add(curve.GetPointAtDist(len * i / n));
        }

        if (pts.Count < 3)
            throw new InvalidOperationException("A polilinha precisa de ao menos 3 vertices.");

        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
        CivilDb.CatchmentGroupCollection groups = cdoc.GetCatchmentGroups();
        AcadDb.ObjectId groupId = AcadDb.ObjectId.Null;
        foreach (AcadDb.ObjectId gid in groups)
        {
            if (tr.GetObject(gid, AcadDb.OpenMode.ForRead, false) is CivilDb.CatchmentGroup g && string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase))
            {
                groupId = gid;
                break;
            }
        }

        if (groupId.IsNull)
            groupId = CivilDb.CatchmentGroup.Create(db, groupName);

        AcadDb.ObjectId id = CivilDb.Catchment.Create(name, AcadDb.ObjectId.Null, groupId, AcadDb.ObjectId.Null, pts);
        CivilDb.Catchment cat = (CivilDb.Catchment)tr.GetObject(id, AcadDb.OpenMode.ForWrite, false);
        cat.RunoffCoefficient = c;
        cat.TimeOfConcentrationCalculationMethod = CivilDb.TimeOfConcentrationCalculationMethod.CalculationMethodUserDefined;
        cat.TimeOfConcentration = tc;
        if (!string.IsNullOrEmpty(structureHandle) && HydroReader.Id(db, structureHandle) is AcadDb.ObjectId sid)
            CatchmentApi.SetDischarge(cat, sid);
        string handle = cat.Handle.ToString();
        tr.Commit();
        return handle;
    }

    // ------------------------------------------------------------------
    // Redes, parts lists e rotulos
    // ------------------------------------------------------------------

    public static List<string> PartsListNames(AcadDb.Database? db)
    {
        List<string> names = new();
        if (db is null)
            return names;
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
            foreach (AcadDb.ObjectId id in cdoc.Styles.PartsListSet)
            {
                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartsList pl)
                    names.Add(pl.Name);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Diag(Module, "Parts lists nao lidas: " + ex.Message);
        }

        return names;
    }

    /// <summary>Familias (e tamanhos) de tubos ou de estruturas da parts list da rede.</summary>
    public sealed record FamilyChoice(string Family, AcadDb.ObjectId FamilyId, List<(string Size, AcadDb.ObjectId SizeId)> Sizes);

    public static List<FamilyChoice> Families(AcadDb.Database db, string networkHandle, bool pipes)
    {
        List<FamilyChoice> list = new();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        CivilDb.Network? net = Open<CivilDb.Network>(tr, db, networkHandle, AcadDb.OpenMode.ForRead);
        if (net is null || net.PartsListId.IsNull)
            return list;
        CivilDb.Styles.PartsList pl = (CivilDb.Styles.PartsList)tr.GetObject(net.PartsListId, AcadDb.OpenMode.ForRead, false);
        CivilDb.DomainType domain = pipes ? CivilDb.DomainType.Pipe : CivilDb.DomainType.Structure;
        foreach (AcadDb.ObjectId fid in pl.GetPartFamilyIdsByDomain(domain))
        {
            // Familia da parts list que saiu do catalogo (ex. as _IPE removidas): o Civil acusa
            // "Part Family is invalid" ao ler; fica de fora da lista em vez de derrubar o comando.
            try
            {
                if (tr.GetObject(fid, AcadDb.OpenMode.ForRead, false) is not CivilDb.Styles.PartFamily fam)
                    continue;
                List<(string, AcadDb.ObjectId)> sizes = new();
                for (int i = 0; i < fam.PartSizeCount; i++)
                {
                    AcadDb.ObjectId sid = fam[i];
                    string label = "tamanho " + (i + 1);
                    try
                    {
                        if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartSize size)
                            label = SizeLabel(size, pipes);
                    }
                    catch
                    {
                        // Fica o rotulo generico.
                    }

                    sizes.Add((label, sid));
                }

                list.Add(new FamilyChoice(fam.Description, fid, sizes));
            }
            catch (Exception ex)
            {
                PluginLog.Aviso(Module, $"Familia da parts list ignorada (nao esta mais no catalogo?): {ex.Message}");
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[HYDRO] Uma familia da parts list foi ignorada ({ex.Message}); se ela saiu do catalogo, apague-a da parts list.");
            }
        }

        return list.OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>O tamanho tem comprimento e largura interna em faixa (da para criar tamanhos novos).</summary>
    public static bool HasPlanSize(AcadDb.Database db, AcadDb.ObjectId sizeId)
    {
        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(sizeId, AcadDb.OpenMode.ForRead, false) is not CivilDb.Styles.PartSize size)
                return false;
            CivilDb.PartDataRecord rec = size.SizeDataRecord;
            return rec.GetDataFieldBy(CivilDb.PartContextType.StructInnerLength).IsFromRange &&
                   rec.GetDataFieldBy(CivilDb.PartContextType.StructInnerWidth).IsFromRange;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Comprimento e largura interna do tamanho, em metros.</summary>
    public static (double Length, double Width) PlanSize(AcadDb.Database db, AcadDb.ObjectId sizeId)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        CivilDb.Styles.PartSize size = (CivilDb.Styles.PartSize)tr.GetObject(sizeId, AcadDb.OpenMode.ForRead, false);
        CivilDb.PartDataRecord rec = size.SizeDataRecord;
        double M(CivilDb.PartContextType ctx)
        {
            CivilDb.PartDataField f = rec.GetDataFieldBy(ctx);
            return f.Value is double d ? d * HydroRules.Factor(f.Units) : double.NaN;
        }

        return (M(CivilDb.PartContextType.StructInnerLength), M(CivilDb.PartContextType.StructInnerWidth));
    }

    /// <summary>
    /// Cria na parts list um tamanho novo da familia com o comprimento e a largura internos dados
    /// (o resto com os valores padrao do catalogo). Valores fora da faixa do catalogo sao recusados.
    /// </summary>
    public static (AcadDb.ObjectId Id, string Label) AddStructureSize(AcadDb.Database db, AcadDb.ObjectId familyId, double length, double width)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Styles.PartFamily fam = (CivilDb.Styles.PartFamily)tr.GetObject(familyId, AcadDb.OpenMode.ForWrite, false);
        using CivilDb.Styles.SizeFilterRecord rec = new(fam);
        HashSet<AcadDb.ObjectId> before = new();
        for (int i = 0; i < fam.PartSizeCount; i++)
            before.Add(fam[i]);
        foreach ((CivilDb.PartContextType ctx, double meters, string what) in new[]
        {
            (CivilDb.PartContextType.StructInnerLength, length, "comprimento"),
            (CivilDb.PartContextType.StructInnerWidth, width, "largura")
        })
        {
            CivilDb.Styles.SizeFilterField f = rec.GetParamByContextAndIndex(ctx, 0);
            double v = meters / HydroRules.Factor(f.Units);
            using (CivilDb.PartDataRange r = f.ValueRange)
            {
                if (!r.IsValidValue(v))
                    throw new InvalidOperationException($"{what} {meters:0.###} m fora da faixa do catalogo ({r.RangeMin}..{r.RangeMax} {f.Units}).");
            }

            f.Value = v;
        }

        fam.AddPartSize(rec);
        AcadDb.ObjectId id = AcadDb.ObjectId.Null;
        for (int i = 0; i < fam.PartSizeCount; i++)
        {
            if (!before.Contains(fam[i]))
                id = fam[i];
        }

        if (id.IsNull)
            throw new InvalidOperationException("O Civil nao criou o tamanho (ja existe um igual na parts list?).");
        string label = tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartSize created ? SizeLabel(created, false) : "novo";
        tr.Commit();
        PluginLog.Info(Module, $"{fam.Description}: tamanho novo {label}");
        return (id, label);
    }

    private static string SizeLabel(CivilDb.Styles.PartSize size, bool pipes)
    {
        CivilDb.PartDataRecord rec = size.SizeDataRecord;
        CivilDb.PartContextType[] contexts = pipes
            ? new[] { CivilDb.PartContextType.PipeInnerDiameter, CivilDb.PartContextType.PipeInnerWidth, CivilDb.PartContextType.PipeInnerHeight }
            : new[] { CivilDb.PartContextType.StructInnerDiameter, CivilDb.PartContextType.StructInnerLength, CivilDb.PartContextType.StructInnerWidth, CivilDb.PartContextType.StructRimToSumpHeight };
        List<string> parts = new();
        foreach (CivilDb.PartContextType ctx in contexts)
        {
            try
            {
                CivilDb.PartDataField f = rec.GetDataFieldBy(ctx);
                if (f.Value is double d)
                    parts.Add($"{f.Description.Split(' ')[0]} {d:0.###} {f.Units}");
                else if (f.Value is not null)
                    parts.Add(f.Value.ToString() ?? "");
            }
            catch
            {
                // Contexto nao existe neste tamanho.
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "tamanho";
    }

    public static string CreateNetwork(AcadDb.Database db, string partsListName)
    {
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
        string name = "Rede HYDRO";
        AcadDb.ObjectId id = CivilDb.Network.Create(cdoc, ref name);
        CivilDb.Network net = (CivilDb.Network)tr.GetObject(id, AcadDb.OpenMode.ForWrite, false);
        foreach (AcadDb.ObjectId pid in cdoc.Styles.PartsListSet)
        {
            if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.PartsList pl && string.Equals(pl.Name, partsListName, StringComparison.OrdinalIgnoreCase))
            {
                net.PartsListId = pid;
                break;
            }
        }

        string handle = net.Handle.ToString();
        tr.Commit();
        PluginLog.Info(Module, $"Rede {name} criada com parts list {partsListName}.");
        return handle;
    }

    /// <summary>Rotulos de planta nativos (estilo padrao da rede) nos tubos e/ou estruturas.</summary>
    public static int AddLabels(AcadDb.Database db, string? networkHandle, bool pipes, bool structures)
    {
        if (networkHandle is null)
            return 0;
        int n = 0;
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        CivilDb.Network? net = Open<CivilDb.Network>(tr, db, networkHandle, AcadDb.OpenMode.ForRead);
        if (net is null)
            return 0;
        if (pipes)
        {
            AcadDb.ObjectId style = net.PipePlanLabelStyleId;
            foreach (AcadDb.ObjectId pid in net.GetPipeIds())
            {
                try
                {
                    if (tr.GetObject(pid, AcadDb.OpenMode.ForRead, false) is CivilDb.Pipe p && p.GetPipeLabelIds().Count == 0)
                    {
                        if (style.IsNull)
                            CivilDb.PipeLabel.Create(pid, 0.5);
                        else
                            CivilDb.PipeLabel.Create(pid, 0.5, style);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Aviso(Module, "Rotulo de tubo: " + ex.Message);
                }
            }
        }

        if (structures)
        {
            foreach (AcadDb.ObjectId sid in net.GetStructureIds())
            {
                try
                {
                    if (tr.GetObject(sid, AcadDb.OpenMode.ForRead, false) is CivilDb.Structure s && s.GetAvailableStructureLabelIds().Count == 0)
                    {
                        CivilDb.StructureLabel.Create(sid);
                        n++;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Aviso(Module, "Rotulo de estrutura: " + ex.Message);
                }
            }
        }

        tr.Commit();
        return n;
    }
}
