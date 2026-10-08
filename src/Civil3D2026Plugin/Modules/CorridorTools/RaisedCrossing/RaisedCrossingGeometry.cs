using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing;

internal static class RaisedCrossingGeometry
{
    private const double Epsilon = 1e-8;

    // Um perfil com espessura exatamente zero nao gera Region/Loft solido de forma confiavel.
    // O pe de cada rampa usa 0,1 mm somente como tolerancia geometrica interna.
    private const double MinimumLoftThickness = 0.0001;

    // Sobreposicao INTERNA para tornar a uniao booleana entre via/conector mais robusta.
    // Nao altera o contorno externo do solido final.
    private const double BooleanOverlap = 0.001;

    public static Point3d ProjectPointToFeatureLine(CivilFeatureLine featureLine, Point3d point)
    {
        return featureLine.GetClosestPointTo(point, Vector3d.ZAxis, false);
    }

    public static double GetDistanceAtPoint(CivilFeatureLine featureLine, Point3d point)
    {
        Point3d projected = ProjectPointToFeatureLine(featureLine, point);
        return featureLine.GetDistAtPoint(projected);
    }

    public static double GetReferenceDistance(
        CivilFeatureLine featureLine,
        RaisedCrossingData data,
        Point3d? preferredPoint = null)
    {
        double total = featureLine.Length3D;
        if (total <= Epsilon)
            throw new InvalidOperationException("A Feature Line 1 possui comprimento nulo.");

        if (preferredPoint.HasValue)
        {
            try
            {
                return Clamp(featureLine.GetDistAtPoint(
                    ProjectPointToFeatureLine(featureLine, preferredPoint.Value)), 0.0, total);
            }
            catch
            {
            }
        }

        return Clamp(data.ReferenceFraction, 0.0, 1.0) * total;
    }

    /// <summary>
    /// Mantido para compatibilidade com codigo antigo/diagnostico.
    /// Resolve o plato usando FL1 como referencia longitudinal.
    /// </summary>
    public static CrossingPlacement GetPlacement(
        CivilFeatureLine featureLine1,
        CivilFeatureLine featureLine2,
        double referenceDistance,
        CrossingAnchorMode anchorMode,
        double length)
    {
        if (length <= Epsilon)
            throw new InvalidOperationException("O comprimento do plato da passagem deve ser maior que zero.");

        double total1 = featureLine1.Length3D;
        ResolveLimits(referenceDistance, length, anchorMode, total1, out double start1, out double end1);

        Point3d p1Start = featureLine1.GetPointAtDist(start1);
        Point3d p1End = featureLine1.GetPointAtDist(end1);

        Point3d p2Start = featureLine2.GetClosestPointTo(p1Start, Vector3d.ZAxis, false);
        Point3d p2End = featureLine2.GetClosestPointTo(p1End, Vector3d.ZAxis, false);
        double start2 = featureLine2.GetDistAtPoint(p2Start);
        double end2 = featureLine2.GetDistAtPoint(p2End);

        if (Math.Abs(end2 - start2) <= Epsilon)
            throw new InvalidOperationException("Nao foi possivel estabelecer o trecho correspondente na Feature Line 2.");

        double cgD1 = (start1 + end1) * 0.5;
        Point3d cg1 = featureLine1.GetPointAtDist(cgD1);
        Point3d cg2 = featureLine2.GetClosestPointTo(cg1, Vector3d.ZAxis, false);

        return new CrossingPlacement(
            featureLine1.GetPointAtDist(referenceDistance),
            MidPoint(cg1, cg2),
            start1,
            end1,
            start2,
            end2);
    }

    /// <summary>
    /// PASSAGEM v1.1.0 - travessia composta sobre duas vias separadas por canteiro.
    ///
    /// Ordem transversal das Feature Lines:
    ///   FL1 externa VIA 1 -> FL2 interna VIA 1 -> FL3 interna VIA 2 -> FL4 externa VIA 2.
    ///
    /// Geometria:
    /// - cada VIA recebe rampa + plato + rampa;
    /// - o canteiro recebe SOMENTE o plato, sem rampa veicular;
    /// - o conector do canteiro inicia depois do overhang interno da VIA 1 e termina
    ///   antes do overhang interno da VIA 2;
    /// - base = cota local de cada Feature Line (com interpolacao/extrapolacao transversal);
    /// - topo = base + H;
    /// - os tres corpos sao unidos por Boolean Unite e o resultado e um unico Solid3d.
    /// </summary>
    public static CrossingGeometryResult BuildSolid(
        CivilFeatureLine featureLine1,
        CivilFeatureLine featureLine2,
        CivilFeatureLine featureLine3,
        CivilFeatureLine featureLine4,
        double referenceDistance,
        CrossingAnchorMode anchorMode,
        double length,
        double rampLength,
        double overhang,
        double height,
        double sampleStep)
    {
        if (length <= Epsilon)
            throw new InvalidOperationException("O comprimento do plato da passagem deve ser maior que zero.");
        if (rampLength <= Epsilon)
            throw new InvalidOperationException("O comprimento da rampa veicular deve ser maior que zero.");
        if (overhang < 0.0)
            throw new InvalidOperationException("A extensao alem das Feature Lines nao pode ser negativa.");
        if (height <= Epsilon)
            throw new InvalidOperationException("A altura da passagem deve ser maior que zero.");

        double total1 = featureLine1.Length3D;
        ResolveLimits(referenceDistance, length, anchorMode, total1, out double plateauStart1, out double plateauEnd1);

        double rampStart1 = plateauStart1 - rampLength;
        double rampEnd1 = plateauEnd1 + rampLength;
        ValidateTotalLimits(rampStart1, rampEnd1, total1, rampLength);

        double safeStep = Math.Max(0.10, sampleStep);
        List<double> fullStations = BuildStationList(
            rampStart1,
            plateauStart1,
            plateauEnd1,
            rampEnd1,
            safeStep);

        List<double> plateauStations = BuildPlateauStationList(
            plateauStart1,
            plateauEnd1,
            safeStep);

        var lane1Sections = new List<CrossingSection>(fullStations.Count);
        var lane2Sections = new List<CrossingSection>(fullStations.Count);

        foreach (double d1 in fullStations)
        {
            FourFeaturePoints points = GetFeaturePointsAtStation(
                featureLine1,
                featureLine2,
                featureLine3,
                featureLine4,
                d1);

            double localRise = GetLocalRise(
                d1,
                rampStart1,
                plateauStart1,
                plateauEnd1,
                rampEnd1,
                height);

            // Somente para viabilizar Region/Loft no pe da rampa.
            double loftRise = Math.Max(localRise, MinimumLoftThickness);

            lane1Sections.Add(BuildLaneSection(points.P1, points.P2, overhang, loftRise));
            lane2Sections.Add(BuildLaneSection(points.P3, points.P4, overhang, loftRise));
        }

        var connectorSections = new List<CrossingSection>(plateauStations.Count);
        foreach (double d1 in plateauStations)
        {
            FourFeaturePoints points = GetFeaturePointsAtStation(
                featureLine1,
                featureLine2,
                featureLine3,
                featureLine4,
                d1);

            connectorSections.Add(BuildMedianConnectorSection(points, overhang, height));
        }

        Solid3dAttempt lane1Attempt = CreateLoft(lane1Sections);
        Solid3dAttempt lane2Attempt = CreateLoft(lane2Sections);
        Solid3dAttempt connectorAttempt = CreateLoft(connectorSections);

        AcadDb.Solid3d finalSolid = lane1Attempt.Solid;
        bool success = false;
        try
        {
            finalSolid.BooleanOperation(AcadDb.BooleanOperationType.BoolUnite, connectorAttempt.Solid);
            finalSolid.BooleanOperation(AcadDb.BooleanOperationType.BoolUnite, lane2Attempt.Solid);
            success = true;
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException(
                "Os tres trechos foram gerados, mas o AutoCAD nao conseguiu uni-los em um unico Solid3d. " +
                "Verifique se as quatro Feature Lines estao na ordem FL1-FL2-FL3-FL4 e se a extensao nao invade todo o canteiro. " +
                "Detalhe: " + ex.Message,
                ex);
        }
        finally
        {
            connectorAttempt.Solid.Dispose();
            lane2Attempt.Solid.Dispose();
            if (!success)
                finalSolid.Dispose();
        }

        double cgD1 = (plateauStart1 + plateauEnd1) * 0.5;
        FourFeaturePoints cgPoints = GetFeaturePointsAtStation(
            featureLine1,
            featureLine2,
            featureLine3,
            featureLine4,
            cgD1);

        ValidateMedianGap(cgPoints, overhang);

        return new CrossingGeometryResult
        {
            Solid = finalSolid,
            ReferencePoint = featureLine1.GetPointAtDist(referenceDistance),
            CgPoint = MidPoint(cgPoints.P1, cgPoints.P4),
            ReferenceDistance = referenceDistance,
            StartDistance = plateauStart1,
            EndDistance = plateauEnd1,
            RampStartDistance = rampStart1,
            RampEndDistance = rampEnd1,
            LoftMode = $"VIA1={lane1Attempt.Mode}; CANTEIRO={connectorAttempt.Mode}; VIA2={lane2Attempt.Mode}; BOOLEAN=UNITE",
            SectionCount = lane1Sections.Count + connectorSections.Count + lane2Sections.Count,
            FeaturePoint1AtCg = cgPoints.P1,
            FeaturePoint2AtCg = cgPoints.P2,
            FeaturePoint3AtCg = cgPoints.P3,
            FeaturePoint4AtCg = cgPoints.P4,
            Lane1WidthAtCg = HorizontalDistance(cgPoints.P1, cgPoints.P2),
            MedianWidthAtCg = HorizontalDistance(cgPoints.P2, cgPoints.P3),
            Lane2WidthAtCg = HorizontalDistance(cgPoints.P3, cgPoints.P4)
        };
    }

    private static FourFeaturePoints GetFeaturePointsAtStation(
        CivilFeatureLine featureLine1,
        CivilFeatureLine featureLine2,
        CivilFeatureLine featureLine3,
        CivilFeatureLine featureLine4,
        double distanceOnFl1)
    {
        Point3d p1 = featureLine1.GetPointAtDist(distanceOnFl1);

        // Encadeamento proposital: cada bordo procura o proximo bordo transversal.
        // Isso reduz o risco de FL3/FL4 "saltarem" para outra secao em curvas.
        Point3d p2 = featureLine2.GetClosestPointTo(p1, Vector3d.ZAxis, false);
        Point3d p3 = featureLine3.GetClosestPointTo(p2, Vector3d.ZAxis, false);
        Point3d p4 = featureLine4.GetClosestPointTo(p3, Vector3d.ZAxis, false);

        if (HorizontalDistance(p1, p2) <= Epsilon)
            throw new InvalidOperationException("FL1 e FL2 coincidem em planta em uma das secoes.");
        if (HorizontalDistance(p2, p3) <= Epsilon)
            throw new InvalidOperationException("FL2 e FL3 coincidem em planta em uma das secoes do canteiro.");
        if (HorizontalDistance(p3, p4) <= Epsilon)
            throw new InvalidOperationException("FL3 e FL4 coincidem em planta em uma das secoes.");

        return new FourFeaturePoints(p1, p2, p3, p4);
    }

    /// <summary>
    /// Perfil de uma das vias. Estende overhang para fora dos dois bordos selecionados.
    /// A base acompanha a inclinacao entre as duas Feature Lines; o topo = base + localRise.
    /// </summary>
    private static CrossingSection BuildLaneSection(
        Point3d featurePoint1,
        Point3d featurePoint2,
        double overhang,
        double localRise)
    {
        Point3d base1Outer = ExtendPoint(featurePoint2, featurePoint1, overhang);
        Point3d base2Outer = ExtendPoint(featurePoint1, featurePoint2, overhang);

        Point3d top1Outer = AddVertical(base1Outer, localRise);
        Point3d top2Outer = AddVertical(base2Outer, localRise);

        return new CrossingSection(base1Outer, base2Outer, top2Outer, top1Outer);
    }

    /// <summary>
    /// Secao do trecho sobre o canteiro. Nao possui rampa veicular.
    /// Comeca APOS a extensao interna da VIA 1 e termina APOS a extensao interna da VIA 2.
    /// Uma sobreposicao interna de 1 mm e usada apenas para robustez do Boolean Unite.
    /// </summary>
    private static CrossingSection BuildMedianConnectorSection(
        FourFeaturePoints points,
        double overhang,
        double height)
    {
        ValidateMedianGap(points, overhang);

        // Valor negativo e permitido aqui: quando overhang=0, o conector entra 1 mm
        // dentro de cada via para garantir intersecao volumetrica no booleano.
        double connectorOffset = overhang - BooleanOverlap;

        Point3d base1 = ExtendPoint(points.P1, points.P2, connectorOffset);
        Point3d base2 = ExtendPoint(points.P4, points.P3, connectorOffset);

        if (HorizontalDistance(base1, base2) <= Epsilon)
            throw new InvalidOperationException("O trecho de ligacao sobre o canteiro ficou com largura nula.");

        return new CrossingSection(
            base1,
            base2,
            AddVertical(base2, height),
            AddVertical(base1, height));
    }

    private static void ValidateMedianGap(FourFeaturePoints points, double overhang)
    {
        Point3d exact1 = ExtendPoint(points.P1, points.P2, overhang);
        Point3d exact2 = ExtendPoint(points.P4, points.P3, overhang);

        Vector3d transverse = new(
            points.P2.X - points.P1.X,
            points.P2.Y - points.P1.Y,
            0.0);
        Vector3d connector = new(
            exact2.X - exact1.X,
            exact2.Y - exact1.Y,
            0.0);

        if (transverse.Length <= Epsilon || connector.Length <= Epsilon)
        {
            throw new InvalidOperationException(
                "A extensao interna consumiu toda a largura disponivel do canteiro. Reduza a Extensao.");
        }

        double dot = transverse.GetNormal().DotProduct(connector.GetNormal());
        if (dot <= 0.0)
        {
            throw new InvalidOperationException(
                "A ordem transversal das Feature Lines parece incorreta ou as extensoes internas se cruzaram. " +
                "Selecione: FL externa VIA 1, FL interna VIA 1, FL interna VIA 2, FL externa VIA 2.");
        }
    }

    /// <summary>
    /// Extrapola a linha from->to por distance alem de 'to'.
    /// O Z e extrapolado usando a inclinacao entre os dois pontos.
    /// distance negativo recua para dentro do segmento.
    /// </summary>
    private static Point3d ExtendPoint(Point3d from, Point3d to, double distance)
    {
        Vector3d horizontal = new(to.X - from.X, to.Y - from.Y, 0.0);
        double width = horizontal.Length;
        if (width <= Epsilon)
            throw new InvalidOperationException("Nao foi possivel extrapolar um trecho transversal de largura nula.");

        Vector3d u = horizontal / width;
        double slopeDzPerM = (to.Z - from.Z) / width;

        return new Point3d(
            to.X + (u.X * distance),
            to.Y + (u.Y * distance),
            to.Z + (slopeDzPerM * distance));
    }

    private static Point3d AddVertical(Point3d point, double dz) =>
        new(point.X, point.Y, point.Z + dz);

    private static double GetLocalRise(
        double station,
        double rampStart,
        double plateauStart,
        double plateauEnd,
        double rampEnd,
        double height)
    {
        if (station <= plateauStart)
        {
            double denominator = plateauStart - rampStart;
            if (denominator <= Epsilon)
                return height;

            double t = Clamp((station - rampStart) / denominator, 0.0, 1.0);
            return height * t;
        }

        if (station >= plateauEnd)
        {
            double denominator = rampEnd - plateauEnd;
            if (denominator <= Epsilon)
                return height;

            double t = Clamp((rampEnd - station) / denominator, 0.0, 1.0);
            return height * t;
        }

        return height;
    }

    private static List<double> BuildStationList(
        double rampStart,
        double plateauStart,
        double plateauEnd,
        double rampEnd,
        double maxStep)
    {
        var result = new List<double>();

        AppendSegmentStations(result, rampStart, plateauStart, maxStep, includeStart: true);
        AppendSegmentStations(result, plateauStart, plateauEnd, maxStep, includeStart: false);
        AppendSegmentStations(result, plateauEnd, rampEnd, maxStep, includeStart: false);

        // Limite de seguranca para lofts muito extensos.
        if (result.Count > 240)
        {
            var reduced = new List<double>(240);
            for (int i = 0; i < 240; i++)
            {
                double t = (double)i / 239.0;
                reduced.Add(rampStart + ((rampEnd - rampStart) * t));
            }

            AddUniqueSorted(reduced, plateauStart);
            AddUniqueSorted(reduced, plateauEnd);
            return reduced;
        }

        return result;
    }

    private static List<double> BuildPlateauStationList(
        double plateauStart,
        double plateauEnd,
        double maxStep)
    {
        var result = new List<double>();
        AppendSegmentStations(result, plateauStart, plateauEnd, maxStep, includeStart: true);

        if (result.Count > 160)
        {
            var reduced = new List<double>(160);
            for (int i = 0; i < 160; i++)
            {
                double t = (double)i / 159.0;
                reduced.Add(plateauStart + ((plateauEnd - plateauStart) * t));
            }
            return reduced;
        }

        return result;
    }

    private static void AppendSegmentStations(
        List<double> target,
        double start,
        double end,
        double maxStep,
        bool includeStart)
    {
        double span = end - start;
        if (span < -Epsilon)
            throw new InvalidOperationException("Intervalo longitudinal invalido na geometria da passagem.");

        if (span <= Epsilon)
        {
            if (includeStart)
                AddUniqueSorted(target, start);
            AddUniqueSorted(target, end);
            return;
        }

        int intervals = Math.Max(1, (int)Math.Ceiling(span / maxStep));
        for (int i = includeStart ? 0 : 1; i <= intervals; i++)
        {
            double t = (double)i / intervals;
            AddUniqueSorted(target, start + (span * t));
        }
    }

    private static void AddUniqueSorted(List<double> target, double value)
    {
        if (target.Any(existing => Math.Abs(existing - value) <= 1e-7))
            return;

        target.Add(value);
        target.Sort();
    }

    private static Solid3dAttempt CreateLoft(IReadOnlyList<CrossingSection> sections)
    {
        if (sections.Count < 2)
            throw new InvalidOperationException("Sao necessarias pelo menos duas secoes para gerar o loft.");

        Exception? firstError = null;

        try
        {
            return new Solid3dAttempt(CreateLoftFromSections(sections), "CURVO/AMOSTRADO");
        }
        catch (Exception ex)
        {
            firstError = ex;
        }

        if (sections.Count > 5)
        {
            try
            {
                var reduced = new List<CrossingSection>
                {
                    sections[0],
                    sections[Math.Max(1, sections.Count / 4)],
                    sections[sections.Count / 2],
                    sections[Math.Min(sections.Count - 2, (sections.Count * 3) / 4)],
                    sections[^1]
                };

                return new Solid3dAttempt(CreateLoftFromSections(reduced), "CURVO/5 SECOES (FALLBACK)");
            }
            catch
            {
            }
        }

        try
        {
            var straight = new[] { sections[0], sections[sections.Count / 2], sections[^1] };
            return new Solid3dAttempt(CreateLoftFromSections(straight), "SIMPLIFICADO/3 SECOES (FALLBACK)");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "O AutoCAD nao conseguiu gerar o Solid3d por loft. " +
                $"Primeira falha: {firstError?.Message ?? "indisponivel"}. " +
                $"Fallback: {ex.Message}", ex);
        }
    }

    private static AcadDb.Solid3d CreateLoftFromSections(IReadOnlyList<CrossingSection> sections)
    {
        var regions = new List<AcadDb.Region>();
        try
        {
            foreach (CrossingSection section in sections)
                regions.Add(CreateRegion(section));

            var builder = new AcadDb.LoftOptionsBuilder
            {
                Ruled = true,
                NoTwist = true
            };

            using AcadDb.LoftOptions options = builder.ToLoftOptions();
            var solid = new AcadDb.Solid3d();
            solid.SetDatabaseDefaults();
            solid.CreateLoftedSolid(
                regions.Cast<AcadDb.Entity>().ToArray(),
                Array.Empty<AcadDb.Entity>(),
                null,
                options);
            return solid;
        }
        finally
        {
            foreach (AcadDb.Region region in regions)
                region.Dispose();
        }
    }

    private static AcadDb.Region CreateRegion(CrossingSection section)
    {
        Point3d[] points =
        {
            section.Base1Outer,
            section.Base2Outer,
            section.Top2Outer,
            section.Top1Outer
        };

        var curves = new AcadDb.DBObjectCollection();
        var lines = new List<AcadDb.Line>();
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var line = new AcadDb.Line(points[i], points[(i + 1) % points.Length]);
                lines.Add(line);
                curves.Add(line);
            }

            AcadDb.DBObjectCollection created = AcadDb.Region.CreateFromCurves(curves);
            if (created.Count == 0)
                throw new InvalidOperationException("Nao foi possivel criar uma regiao transversal planar.");

            AcadDb.Region? first = null;
            foreach (AcadDb.DBObject obj in created)
            {
                if (first is null && obj is AcadDb.Region region)
                    first = region;
                else
                    obj.Dispose();
            }

            return first ?? throw new InvalidOperationException("A secao transversal nao gerou Region.");
        }
        finally
        {
            foreach (AcadDb.Line line in lines)
                line.Dispose();
        }
    }

    private static void ResolveLimits(
        double referenceDistance,
        double length,
        CrossingAnchorMode mode,
        double total,
        out double start,
        out double end)
    {
        switch (mode)
        {
            case CrossingAnchorMode.Start:
                start = referenceDistance;
                end = referenceDistance + length;
                break;
            case CrossingAnchorMode.End:
                start = referenceDistance - length;
                end = referenceDistance;
                break;
            default:
                start = referenceDistance - (length * 0.5);
                end = referenceDistance + (length * 0.5);
                break;
        }

        if (start < -1e-6 || end > total + 1e-6)
        {
            throw new InvalidOperationException(
                $"O plato da passagem ({start:0.###} a {end:0.###} m) " +
                $"ultrapassa os limites da Feature Line 1 (0 a {total:0.###} m). " +
                "Escolha outro ponto, reduza o comprimento ou altere Meio/Inicio/Fim.");
        }

        start = Clamp(start, 0.0, total);
        end = Clamp(end, 0.0, total);
    }

    private static void ValidateTotalLimits(
        double rampStart,
        double rampEnd,
        double total,
        double rampLength)
    {
        if (rampStart < -1e-6 || rampEnd > total + 1e-6)
        {
            throw new InvalidOperationException(
                $"As rampas de {rampLength:0.###} m fazem a passagem ultrapassar os limites " +
                $"da Feature Line 1 (0 a {total:0.###} m). " +
                "Escolha outro ponto, reduza o plato ou ajuste o comprimento da rampa em PASSAGEMCFG/PASSAGEMEDITAR.");
        }
    }

    private static Point3d MidPoint(Point3d a, Point3d b) =>
        new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);

    private static double HorizontalDistance(Point3d a, Point3d b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(max, value));

    private readonly record struct FourFeaturePoints(Point3d P1, Point3d P2, Point3d P3, Point3d P4);
    private readonly record struct Solid3dAttempt(AcadDb.Solid3d Solid, string Mode);
}
