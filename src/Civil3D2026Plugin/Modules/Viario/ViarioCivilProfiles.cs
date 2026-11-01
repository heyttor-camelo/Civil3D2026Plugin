using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Civil.ApplicationServices;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Etapa 2 dos objetos do Civil: perfis.
/// - No alinhamento do eixo: perfil do terreno (da superficie escolhida) e um greide de projeto
///   (perfil por layout, reta do inicio ao fim na elevacao do terreno) que o usuario edita.
/// - No bordo do pavimento (encontro com a sarjeta; sem sarjeta, na face do meio-fio): perfil
///   offset dinamico do Civil, ligado ao greide pela declividade transversal. Se a API do Civil
///   instalado nao criar o offset dinamico, o plugin calcula o perfil (greide - i x afastamento)
///   e o refaz a cada atualizacao.
/// Os perfis levam XData do modulo (CIVIL/PERFIL) e sao reconhecidos por ela, nao pelo nome.
/// </summary>
internal static class ViarioCivilProfiles
{
    public const string KindProfile = "PERFIL";
    public const string LayerProfile = "VIARIO-C3D-PERFIL";

    public const string RoleTerrain = "TERRENO";
    public const string RoleGrade = "GREIDE";
    public const string RolePavement = "PAV";

    private const string MethodOffset = "OFFSET";
    private const string MethodCalc = "CALC";

    internal sealed class Report
    {
        public int TerrainCreated, GradeCreated, PavementCreated, PavementKept, PavementCalc, Erased;
        public List<string> Messages { get; } = new();

        public override string ToString() =>
            $"Perfis: terreno +{TerrainCreated} | greide +{GradeCreated} | bordo do pavimento +{PavementCreated} (mantidos {PavementKept}; calculados pelo plugin {PavementCalc}) | removidos {Erased}";
    }

    private sealed class Existing
    {
        public AcadDb.ObjectId Id;
        public string Role = "";
        public int Side;
        public string Geom = "";
        public string Method = "";
    }

    /// <summary>
    /// Cria/mantem os perfis dos eixos e bordos do pavimento do plano. Chamado dentro da transacao
    /// do ViarioCivil.Sync, depois dos alinhamentos. edgeIds: alinhamentos dos bordos (face do
    /// meio-fio e pavimento) ja existentes ou criados agora.
    /// </summary>
    public static Report Sync(AcadDb.Transaction tr, AcadDb.Database db, CivilDocument cdoc, CivilPlan plan,
        IReadOnlyDictionary<string, AcadDb.ObjectId> axisIds, IReadOnlyDictionary<CivilEdgePlan, AcadDb.ObjectId> edgeIds,
        ViarioStore.CivilProfiles settings)
    {
        Report report = new();
        AcadDb.ObjectId layerId = EnsureLayer(tr, db);
        AcadDb.ObjectId styleEg = PickStyle(tr, cdoc.Styles.ProfileStyles, "Existing", "Terreno", "Existente", "EG");
        AcadDb.ObjectId styleFg = PickStyle(tr, cdoc.Styles.ProfileStyles, "Design", "Projeto", "Greide", "Layout", "FG");
        AcadDb.ObjectId labelId = PickStyle(tr, cdoc.Styles.LabelSetStyles.ProfileLabelSetStyles, "No Labels", "Sem", "_No");
        AcadDb.ObjectId surfaceId = FindSurface(tr, cdoc, settings.Surface);
        if (settings.Surface.Length > 0 && surfaceId.IsNull)
            report.Messages.Add($"Superficie \"{settings.Surface}\" nao existe no desenho: perfis de terreno nao criados.");

        foreach (AxisInput axis in plan.Axes)
        {
            if (!axisIds.TryGetValue(axis.Key, out AcadDb.ObjectId alId) || alId.IsNull || alId.IsErased)
                continue;

            try
            {
                CivilDb.Alignment al = (CivilDb.Alignment)tr.GetObject(alId, AcadDb.OpenMode.ForRead);
                List<Existing> existing = Read(tr, al);

                // ---------- Terreno ----------
                AcadDb.ObjectId terrainId = AcadDb.ObjectId.Null;
                Existing? terrain = existing.FirstOrDefault(e => e.Role == RoleTerrain);
                if (terrain is not null && (surfaceId.IsNull || !string.Equals(terrain.Geom, settings.Surface, StringComparison.OrdinalIgnoreCase)))
                {
                    Erase(tr, terrain.Id, report);
                    existing.Remove(terrain);
                    terrain = null;
                }

                if (terrain is not null)
                {
                    terrainId = terrain.Id;
                }
                else if (!surfaceId.IsNull)
                {
                    terrainId = CivilDb.Profile.CreateFromSurface(Unique(tr, al, $"{al.Name} - Terreno"), alId, surfaceId, layerId, styleEg, labelId);
                    Tag(tr, terrainId, axis.Key, RoleTerrain, 0, settings.Surface, "SUPERFICIE");
                    report.TerrainCreated++;
                }

                // ---------- Greide ----------
                Existing? grade = existing.FirstOrDefault(e => e.Role == RoleGrade);
                AcadDb.ObjectId gradeId = grade?.Id ?? AcadDb.ObjectId.Null;
                if (grade is null)
                {
                    gradeId = CivilDb.Profile.CreateByLayout(Unique(tr, al, $"{al.Name} - Greide"), alId, layerId, styleFg, labelId);
                    CivilDb.Profile g = (CivilDb.Profile)tr.GetObject(gradeId, AcadDb.OpenMode.ForWrite);
                    double z0 = 0.0, z1 = 0.0;
                    if (!terrainId.IsNull && tr.GetObject(terrainId, AcadDb.OpenMode.ForRead, false) is CivilDb.Profile t)
                    {
                        z0 = ElevationOrDefault(t, al.StartingStation, 0.0);
                        z1 = ElevationOrDefault(t, al.EndingStation, z0);
                    }

                    g.PVIs.AddPVI(al.StartingStation, z0);
                    g.PVIs.AddPVI(al.EndingStation, z1);
                    Tag(tr, gradeId, axis.Key, RoleGrade, 0, terrainId.IsNull ? "" : "TERRENO", "LAYOUT");
                    report.GradeCreated++;
                }

                // ---------- Bordo do pavimento (um por lado) ----------
                foreach (int side in new[] { -1, +1 })
                {
                    CivilEdgePlan? edge = plan.Edges.FirstOrDefault(e => e.AxisKey == axis.Key && e.Side == side && e.IsPavement)
                                          ?? plan.Edges.Where(e => e.AxisKey == axis.Key && e.Side == side && !e.IsPavement).OrderByDescending(e => e.Length).FirstOrDefault();
                    if (edge is null || !edgeIds.TryGetValue(edge, out AcadDb.ObjectId edgeId) || edgeId.IsNull || edgeId.IsErased)
                        continue;

                    SyncPavement(tr, db, axis, side, edge, edgeId, al, gradeId, settings, layerId, styleFg, labelId, report);
                }
            }
            catch (System.Exception ex)
            {
                report.Messages.Add($"Perfis do eixo {axis.Key}: {ex.Message}");
            }
        }

        return report;
    }

    private static void SyncPavement(AcadDb.Transaction tr, AcadDb.Database db, AxisInput axis, int side, CivilEdgePlan edge,
        AcadDb.ObjectId edgeId, CivilDb.Alignment parentAl, AcadDb.ObjectId gradeId, ViarioStore.CivilProfiles settings,
        AcadDb.ObjectId layerId, AcadDb.ObjectId styleId, AcadDb.ObjectId labelId, Report report)
    {
        CivilDb.Alignment edgeAl = (CivilDb.Alignment)tr.GetObject(edgeId, AcadDb.OpenMode.ForRead);
        CivilDb.Profile grade = (CivilDb.Profile)tr.GetObject(gradeId, AcadDb.OpenMode.ForRead);
        string parentKey = grade.Handle.ToString();
        string slopeKey = F(settings.CrossSlope);
        List<Existing> existing = Read(tr, edgeAl).Where(e => e.Role == RolePavement).ToList();

        // Perfil que ja existe: offset dinamico com o mesmo greide e declividade fica; o calculado
        // pelo plugin e refeito quando o greide ou a declividade mudam.
        string calcGeom = parentKey + "|" + slopeKey + "|" + GradeSignature(grade);
        Existing? keep = null;
        foreach (Existing e in existing)
        {
            bool sameParent = e.Geom.StartsWith(parentKey + "|" + slopeKey, StringComparison.Ordinal);
            bool ok = sameParent && (e.Method == MethodOffset || e.Geom == calcGeom);
            if (ok && keep is null)
                keep = e;
            else
                Erase(tr, e.Id, report);
        }

        if (keep is not null)
        {
            report.PavementKept++;
            return;
        }

        string name = Unique(tr, edgeAl, $"{edgeAl.Name} - Perfil");
        double offset = Math.Abs(edge.Offset);

        // 1) Offset dinamico do Civil (segue o greide sozinho).
        AcadDb.ObjectId id = TryCreateOffsetProfile(tr, name, edgeId, gradeId, layerId, styleId, labelId, settings.CrossSlope, offset, parentAl, edgeAl, grade, out string why);
        if (!id.IsNull)
        {
            Tag(tr, id, axis.Key, RolePavement, side, parentKey + "|" + slopeKey, MethodOffset);
            report.PavementCreated++;
            return;
        }

        if (why.Length > 0)
            Civil3D2026Plugin.Infrastructure.PluginLog.Diag("Viario", $"Perfil offset nao criado em {edgeAl.Name}: {why}. Calculado pelo plugin.");

        // 2) Calculado pelo plugin: greide - i x afastamento, nas estacas correspondentes do bordo.
        id = CivilDb.Profile.CreateByLayout(name, edgeId, layerId, styleId, labelId);
        CivilDb.Profile p = (CivilDb.Profile)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
        List<(double S, double Z)> pvis = PavementPvis(parentAl, grade, edgeAl, settings.CrossSlope);
        if (pvis.Count < 2)
        {
            p.Erase();
            report.Messages.Add($"{name}: nao consegui projetar o greide no bordo (estacas fora do alinhamento).");
            return;
        }

        foreach ((double s, double z) in pvis)
            p.PVIs.AddPVI(s, z);
        Tag(tr, id, axis.Key, RolePavement, side, calcGeom, MethodCalc);
        report.PavementCreated++;
        report.PavementCalc++;
    }

    // ------------------------------------------------------------------
    // Offset dinamico: Profile.CreateOffsetProfileBySlope (metodo do perfil pai)
    // ------------------------------------------------------------------

    private static AcadDb.ObjectId TryCreateOffsetProfile(AcadDb.Transaction tr, string name, AcadDb.ObjectId edgeId, AcadDb.ObjectId gradeId,
        AcadDb.ObjectId layerId, AcadDb.ObjectId styleId, AcadDb.ObjectId labelId, double slope, double offset,
        CivilDb.Alignment parentAl, CivilDb.Alignment edgeAl, CivilDb.Profile grade, out string why)
    {
        why = string.Empty;
        List<string> errors = new();

        // O Civil usa declividade negativa para "caindo do eixo para o bordo"; se o resultado subir, inverte.
        foreach (double signed in new[] { -slope, slope })
        {
            AcadDb.ObjectId id = AcadDb.ObjectId.Null;
            try
            {
                id = grade.CreateOffsetProfileBySlope(name, edgeId, styleId, signed);
                if (id.IsNull)
                    continue;

                CivilDb.Profile p = (CivilDb.Profile)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
                try
                {
                    p.LayerId = layerId;
                }
                catch
                {
                    // Layer do perfil presa ao estilo.
                }

                if (VerifyPavement(parentAl, grade, edgeAl, p, slope))
                    return id;

                errors.Add($"i={signed:0.###}: elevacao nao confere");
                p.Erase();
            }
            catch (System.Exception ex)
            {
                errors.Add($"i={signed:0.###}: {(ex.InnerException ?? ex).Message}");
                try
                {
                    if (!id.IsNull && !id.IsErased)
                        tr.GetObject(id, AcadDb.OpenMode.ForWrite, false).Erase();
                }
                catch
                {
                    // Ja apagado ou nunca criado.
                }
            }
        }

        why = string.Join(" | ", errors.Distinct().Take(3));
        return AcadDb.ObjectId.Null;
    }

    // ------------------------------------------------------------------
    // Geometria do perfil do bordo
    // ------------------------------------------------------------------

    /// <summary>PVIs do bordo: cada PVI do greide (e as pontas do bordo) projetados no bordo, elevacao = greide - i x afastamento.</summary>
    private static List<(double S, double Z)> PavementPvis(CivilDb.Alignment parentAl, CivilDb.Profile grade, CivilDb.Alignment edgeAl, double slope)
    {
        List<double> parentStations = new();
        try
        {
            foreach (CivilDb.ProfilePVI pvi in grade.PVIs)
                parentStations.Add(pvi.RawStation);
        }
        catch
        {
            // Sem PVIs legiveis: pontas.
        }

        parentStations.Add(grade.StartingStation);
        parentStations.Add(grade.EndingStation);

        List<(double S, double Z)> list = new();
        foreach (double sp in parentStations.Distinct().OrderBy(s => s))
        {
            if (!Project(parentAl, sp, edgeAl, out double se, out double off))
                continue;
            if (se < edgeAl.StartingStation - 1e-6 || se > edgeAl.EndingStation + 1e-6)
                continue;
            double z = ElevationOrDefault(grade, sp, double.NaN);
            if (double.IsNaN(z))
                continue;
            list.Add((se, z - (slope * Math.Abs(off))));
        }

        // Pontas do bordo (o bordo pode comecar/terminar fora dos PVIs do greide).
        foreach (double se in new[] { edgeAl.StartingStation, edgeAl.EndingStation })
        {
            if (list.Any(p => Math.Abs(p.S - se) < 0.01))
                continue;
            if (!Project(edgeAl, se, parentAl, out double sp, out double off))
                continue;
            double z = ElevationOrDefault(grade, Math.Max(grade.StartingStation, Math.Min(grade.EndingStation, sp)), double.NaN);
            if (!double.IsNaN(z))
                list.Add((se, z - (slope * Math.Abs(off))));
        }

        List<(double S, double Z)> result = new();
        foreach ((double s, double z) in list.OrderBy(p => p.S))
        {
            if (result.Count > 0 && s - result[^1].S < 0.01)
                continue;
            result.Add((s, z));
        }

        return result;
    }

    /// <summary>Estaca "from" projetada no alinhamento "to" (estaca e afastamento com sinal).</summary>
    private static bool Project(CivilDb.Alignment from, double station, CivilDb.Alignment to, out double toStation, out double offset)
    {
        toStation = 0;
        offset = 0;
        try
        {
            double e = 0, n = 0;
            from.PointLocation(station, 0.0, ref e, ref n);
            bool outOfRange = false;
            to.StationOffsetAcceptOutOfRange(e, n, ref toStation, ref offset, ref outOfRange);
            return !outOfRange;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Confere o perfil do bordo no meio: greide - i x afastamento (2 cm).</summary>
    private static bool VerifyPavement(CivilDb.Alignment parentAl, CivilDb.Profile grade, CivilDb.Alignment edgeAl, CivilDb.Profile p, double slope)
    {
        double se = (edgeAl.StartingStation + edgeAl.EndingStation) / 2.0;
        if (!Project(edgeAl, se, parentAl, out double sp, out double off))
            return false;
        double zg = ElevationOrDefault(grade, Math.Max(grade.StartingStation, Math.Min(grade.EndingStation, sp)), double.NaN);
        double zp = ElevationOrDefault(p, se, double.NaN);
        if (double.IsNaN(zg) || double.IsNaN(zp))
            return false;
        return Math.Abs(zp - (zg - (slope * Math.Abs(off)))) <= 0.02;
    }

    private static double ElevationOrDefault(CivilDb.Profile p, double station, double fallback)
    {
        try
        {
            double s = Math.Max(p.StartingStation, Math.Min(p.EndingStation, station));
            return p.ElevationAt(s);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Muda quando o greide e editado (PVIs): o perfil calculado pelo plugin e refeito.</summary>
    private static string GradeSignature(CivilDb.Profile grade)
    {
        try
        {
            return string.Join(";", grade.PVIs.Cast<CivilDb.ProfilePVI>().Select(v => $"{v.RawStation:0.###},{v.Elevation:0.###}"));
        }
        catch
        {
            return $"{grade.StartingStation:0.###},{grade.EndingStation:0.###}";
        }
    }

    // ------------------------------------------------------------------
    // Leitura, marcacao e remocao
    // ------------------------------------------------------------------

    private static List<Existing> Read(AcadDb.Transaction tr, CivilDb.Alignment al)
    {
        List<Existing> list = new();
        foreach (AcadDb.ObjectId id in al.GetProfileIds())
        {
            if (id.IsNull || id.IsErased || tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not CivilDb.Profile p)
                continue;
            List<string>? v = ViarioStore.ReadValues(p);
            if (v is null || v.Count < 4 || v[0] != ViarioCivil.Tag || v[1] != KindProfile)
                continue;
            list.Add(new Existing
            {
                Id = id,
                Role = v[3],
                Side = v.Count > 4 && int.TryParse(v[4], out int side) ? side : 0,
                Geom = v.Count > 7 ? v[7] : "",
                Method = v.Count > 8 ? v[8] : ""
            });
        }

        return list;
    }

    /// <summary>Perfis do modulo em todos os alinhamentos do Civil gerados (para o autoteste e o Remover).</summary>
    public static List<(AcadDb.ObjectId Id, string AxisKey, string Role, string Method)> All(AcadDb.Transaction tr, IEnumerable<AcadDb.ObjectId> alignmentIds)
    {
        List<(AcadDb.ObjectId, string, string, string)> list = new();
        foreach (AcadDb.ObjectId alId in alignmentIds)
        {
            if (alId.IsNull || alId.IsErased || tr.GetObject(alId, AcadDb.OpenMode.ForRead, false) is not CivilDb.Alignment al)
                continue;
            foreach (AcadDb.ObjectId id in al.GetProfileIds())
            {
                if (id.IsNull || id.IsErased || tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not CivilDb.Profile p)
                    continue;
                List<string>? v = ViarioStore.ReadValues(p);
                if (v is not null && v.Count >= 4 && v[0] == ViarioCivil.Tag && v[1] == KindProfile)
                    list.Add((id, v[2], v[3], v.Count > 8 ? v[8] : ""));
            }
        }

        return list;
    }

    private static void Tag(AcadDb.Transaction tr, AcadDb.ObjectId id, string axisKey, string role, int side, string geom, string method)
    {
        CivilDb.Profile p = (CivilDb.Profile)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
        ViarioStore.SetTag(p, ViarioCivil.Tag, KindProfile, axisKey, role, side.ToString(CultureInfo.InvariantCulture), "0", "0", geom, method);
    }

    private static void Erase(AcadDb.Transaction tr, AcadDb.ObjectId id, Report report)
    {
        try
        {
            if (id.IsNull || id.IsErased)
                return;
            tr.GetObject(id, AcadDb.OpenMode.ForWrite, false).Erase();
            report.Erased++;
        }
        catch
        {
            // Perfil preso a outro objeto.
        }
    }

    private static string Unique(AcadDb.Transaction tr, CivilDb.Alignment al, string name)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (AcadDb.ObjectId id in al.GetProfileIds())
        {
            if (!id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Profile p)
                names.Add(p.Name);
        }

        string candidate = ViarioCivil.SafeName(name);
        int i = 2;
        while (names.Contains(candidate))
            candidate = $"{ViarioCivil.SafeName(name)} ({i++})";
        return candidate;
    }

    private static AcadDb.ObjectId FindSurface(AcadDb.Transaction tr, CivilDocument cdoc, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return AcadDb.ObjectId.Null;
        foreach (AcadDb.ObjectId id in cdoc.GetSurfaceIds())
        {
            if (!id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Surface s &&
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return AcadDb.ObjectId.Null;
    }

    /// <summary>Nomes das superficies do desenho.</summary>
    public static List<string> SurfaceNames(AcadDb.Database db)
    {
        List<string> names = new();
        try
        {
            CivilDocument cdoc = CivilDocument.GetCivilDocument(db);
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            foreach (AcadDb.ObjectId id in cdoc.GetSurfaceIds())
            {
                if (!id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Surface s)
                    names.Add(s.Name);
            }
        }
        catch
        {
            // Sem Civil document.
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AcadDb.ObjectId EnsureLayer(AcadDb.Transaction tr, AcadDb.Database db)
    {
        ViarioStore.EnsureLayer(tr, db, LayerProfile, 3);
        AcadDb.LayerTable lt = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
        return lt.Has(LayerProfile) ? lt[LayerProfile] : db.Clayer;
    }

    private static AcadDb.ObjectId PickStyle(AcadDb.Transaction tr, System.Collections.IEnumerable styles, params string[] preferred)
    {
        List<(AcadDb.ObjectId Id, string Name)> all = new();
        foreach (object item in styles)
        {
            if (item is AcadDb.ObjectId id && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is CivilDb.Styles.StyleBase style)
                all.Add((id, style.Name));
        }

        foreach (string p in preferred)
        {
            foreach ((AcadDb.ObjectId id, string name) in all)
            {
                if (name.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }

        return all.Count > 0 ? all[0].Id : AcadDb.ObjectId.Null;
    }

    private static string F(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);
}
