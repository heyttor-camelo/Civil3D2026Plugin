using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;

[assembly: CommandClass(
    typeof(
        Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo.MFRebaixoCommands
    )
)]

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

public sealed class MFRebaixoCommands
{
    public const string Versao = "1.3.1";

    private const string MfMacroOrClassName =
        "Subassembly.MF";

    // PKT novo (meio-fio SUDECAP/Kassel + sarjeta + berço).
    private const string MfSarjetaMacroOrClassName =
        "Subassembly.MFSARJETA";

    // Saída do MFSARJETA com a altura exposta real (equivale a AMF_LIVRE_EFETIVA).
    private const string EffectiveOutputAltParameterName =
        "EXPOSTO";

    // Parâmetro numérico do MFSARJETA que escolhe os codes de destaque (hachura em planta).
    // Faixas: [1,2) aba de entrada | [2,3) rebaixo pleno | [3,4) aba de saída | 0 ou >=4 normal.
    // As faixas crescem sempre: na estaca de emenda entre duas Transitions, qualquer uma
    // das duas já dá o code do trecho que começa.
    private const string HighlightParameterName =
        "DESTAQUE";

    // Usado quando a subassembly não tem iRampa (8,33 %).
    private const double DefaultRampSlope =
        0.0833;

    private const string ParameterName =
        "AMF_LIVRE";

    private const string NormalParameterName =
        "AMF_LIVRE_NORMAL";

    private const string EffectiveOutputParameterName =
        "AMF_LIVRE_EFETIVA";

    private const string ExistingVerticalOutputParameterName =
        "DIST_P4_P1_Y";

    private const string GutterSlopeParameterName =
        "iSa";

    private const string GutterWidthParameterName =
        "LS";

    private const string RampSlopeParameterName =
        "iRampa";

    private const string FlowlinePointCode =
        "Flowline_Gutter";

    private const string BackCurbPointCode =
        "Back_Curb";

    private const double StationTolerance =
        1e-6;

    private const double AutoSideOffsetTolerance =
        0.01;

    private enum SideSelectionMode
    {
        Auto,
        Left,
        Right
    }

    private enum ValueInputMode
    {
        Reduction,
        FinalHeight
    }

    [CommandMethod(
        "MFREBAIXO",
        CommandFlags.Modal)]
    public void InserirRebaixo()
    {
        var doc =
            AcApp.DocumentManager.MdiActiveDocument;

        if (doc is null)
            return;

        var ed =
            doc.Editor;

        var db =
            doc.Database;

        var settings =
            MFRebaixoSettingsStore.Load();

        var peo =
            new PromptEntityOptions(
                "\nSelecione o Corridor: ");

        peo.SetRejectMessage(
            "\nSelecione um objeto Corridor do Civil 3D.");

        peo.AddAllowedClass(
            typeof(CivilCorridor),
            exactMatch: true);

        var per =
            ed.GetEntity(peo);

        if (per.Status != PromptStatus.OK)
            return;

        ObjectId corridorId =
            per.ObjectId;

        SideSelectionMode sideMode =
            SideSelectionMode.Auto;

        ValueInputMode valueMode =
            ParseValueInputMode(
                settings.UltimoModoValor);

        ed.WriteMessage(
            "\nMFREBAIXO por dois pontos rebaixados.");

        ed.WriteMessage(
            "\n1º clique = início do trecho totalmente rebaixado; " +
            "2º clique = fim do trecho totalmente rebaixado.");

        ed.WriteMessage(
            "\nAs rampas de entrada/saída têm o comprimento calculado " +
            "pela diferença de altura livre e pelo parâmetro iRampa.");

        ed.WriteMessage(
            "\nA DLL cria Corridor Sections novas nas posições calculadas " +
            "de início/fim das rampas.");

        while (true)
        {
            var firstOptions =
                new PromptPointOptions(
                    $"\nClique no 1º PONTO REBAIXADO " +
                    $"| Lado={GetSideModeLabel(sideMode)} " +
                    "[Auto/Esquerda/Direita] <Encerrar>: ")
                {
                    AllowNone = true
                };

            firstOptions.Keywords.Add(
                "Auto");

            firstOptions.Keywords.Add(
                "Esquerda");

            firstOptions.Keywords.Add(
                "Direita");

            PromptPointResult firstResult =
                ed.GetPoint(
                    firstOptions);

            if (
                firstResult.Status == PromptStatus.None
                ||
                firstResult.Status == PromptStatus.Cancel)
            {
                break;
            }

            if (
                firstResult.Status == PromptStatus.Keyword)
            {
                string keyword =
                    firstResult.StringResult;

                if (
                    keyword.Equals(
                        "Auto",
                        StringComparison.OrdinalIgnoreCase))
                {
                    sideMode =
                        SideSelectionMode.Auto;
                }
                else if (
                    keyword.Equals(
                        "Esquerda",
                        StringComparison.OrdinalIgnoreCase))
                {
                    sideMode =
                        SideSelectionMode.Left;
                }
                else if (
                    keyword.Equals(
                        "Direita",
                        StringComparison.OrdinalIgnoreCase))
                {
                    sideMode =
                        SideSelectionMode.Right;
                }

                ed.WriteMessage(
                    $"\nLado = {GetSideModeLabel(sideMode)}.");

                continue;
            }

            if (
                firstResult.Status != PromptStatus.OK)
            {
                continue;
            }

            Point3d firstPoint =
                firstResult.Value.TransformBy(
                    ed.CurrentUserCoordinateSystem);

            FirstPointInfo firstInfo;

            try
            {
                firstInfo =
                    InspectFirstPoint(
                        db,
                        corridorId,
                        firstPoint,
                        sideMode);
            }
            catch (
                System.Exception ex)
            {
                ed.WriteMessage(
                    $"\n[ERRO MFREBAIXO] {ex.Message}\n");

                continue;
            }

            ed.WriteMessage(
                $"\n1º ponto identificado: " +
                $"Baseline={firstInfo.BaselineName}" +
                $" | Region={firstInfo.RegionName}" +
                $" | Estaca={firstInfo.Station:0.000}" +
                $" | Referência anterior={firstInfo.PreviousStation:0.000}" +
                $" | Lado={GetSideLabel(firstInfo.Side)}" +
                $" | Subassembly={firstInfo.SubassemblyName}" +
                $" | Altura livre efetiva em S0={firstInfo.NormalHeight:0.###} m" +
                $" ({firstInfo.EffectiveHeightSource})" +
                $" | nominal={firstInfo.NominalNormalHeight:0.###} m.");

            if (!AskLaunchValue(
                    ed,
                    settings,
                    firstInfo.NormalHeight,
                    ref valueMode,
                    out double finalHeight))
            {
                ed.WriteMessage(
                    "\nPar de pontos cancelado.");

                continue;
            }

            while (true)
            {
                double reduction =
                    firstInfo.NormalHeight -
                    finalHeight;

                var secondOptions =
                    new PromptPointOptions(
                        $"\n1º={firstInfo.Station:0.000}" +
                        $" | {GetValueModeLabel(valueMode)}" +
                        $" | Abaixa={reduction:0.###} m" +
                        $" | Altura livre final={finalHeight:0.###} m" +
                        "\nClique no 2º PONTO REBAIXADO " +
                        "[Cancelar] <Cancelar par>: ")
                    {
                        AllowNone = true
                    };

                secondOptions.Keywords.Add(
                    "Cancelar");

                PromptPointResult secondResult =
                    ed.GetPoint(
                        secondOptions);

                if (
                    secondResult.Status == PromptStatus.Cancel)
                {
                    MFRebaixoSettingsStore.Save(
                        settings);

                    return;
                }

                if (
                    secondResult.Status == PromptStatus.None
                    ||
                    (
                        secondResult.Status == PromptStatus.Keyword
                        &&
                        secondResult.StringResult.Equals(
                            "Cancelar",
                            StringComparison.OrdinalIgnoreCase)
                    ))
                {
                    ed.WriteMessage(
                        "\nPar de pontos cancelado.");

                    break;
                }

                if (
                    secondResult.Status != PromptStatus.OK)
                {
                    continue;
                }

                Point3d secondPoint =
                    secondResult.Value.TransformBy(
                        ed.CurrentUserCoordinateSystem);

                try
                {
                    ApplyRebaixoPair(
                        db,
                        ed,
                        corridorId,
                        firstPoint,
                        secondPoint,
                        firstInfo,
                        finalHeight,
                        sideMode);

                    break;
                }
                catch (
                    System.Exception ex)
                {
                    ed.WriteMessage(
                        $"\n[ERRO MFREBAIXO] {ex.Message}\n");

                    ed.WriteMessage(
                        "\nInforme novamente o 2º ponto ou ENTER para cancelar o par.");
                }
            }
        }

        MFRebaixoSettingsStore.Save(
            settings);
    }

    [CommandMethod(
        "MFREBAIXOCFG",
        CommandFlags.Modal)]
    public void Configurar()
    {
        var doc =
            AcApp.DocumentManager.MdiActiveDocument;

        if (doc is null)
            return;

        var ed =
            doc.Editor;

        var settings =
            MFRebaixoSettingsStore.Load();

        string? presetName =
            AskPreset(
                ed,
                settings,
                allowConfig: false);

        if (presetName is null)
            return;

        ConfigurePreset(
            ed,
            settings,
            presetName);

        MFRebaixoSettingsStore.Save(
            settings);
    }

    [CommandMethod(
        "MFREBAIXOHELP",
        CommandFlags.Modal)]
    public void Help()
    {
        var doc =
            AcApp.DocumentManager.MdiActiveDocument;

        if (doc is null)
            return;

        var ed =
            doc.Editor;

        ed.WriteMessage(
            "\n============================================================");

        ed.WriteMessage(
            $"\n MFREBAIXO v{Versao} - " +
            "Corridor Transition de meio-fio");

        ed.WriteMessage(
            "\n============================================================");

        ed.WriteMessage(
            "\nFluxo principal:");

        ed.WriteMessage(
            "\n  1. Selecione o Corridor.");

        ed.WriteMessage(
            "\n  2. Clique no 1º ponto onde o MF já deve estar rebaixado.");

        ed.WriteMessage(
            "\n  3. A DLL identifica Baseline/Region/lado/Subassembly.MF e lê " +
            "a altura livre EFETIVA na seção anterior ao 1º clique.");

        ed.WriteMessage(
            "\n  4. Informe por Redução ou AlturaFinal. " +
            "O último modo e valor ficam como padrão.");

        ed.WriteMessage(
            "\n  5. Clique no 2º ponto totalmente rebaixado. " +
            "Não é solicitado novamente o valor.");

        ed.WriteMessage(
            "\n  6. A altura efetiva normal é lida nas Corridor Sections de referência vizinhas.");

        ed.WriteMessage(
            "\n  7. Lrampa = |AlturaEfetiva - AlturaFinal| / |iRampa|.");

        ed.WriteMessage(
            "\n  8. S0 = S1 - LrampaEntrada; S3 = S2 + LrampaSaída.");

        ed.WriteMessage(
            "\n  9. A DLL cria Corridor Sections novas em S0, S1, S2 e S3.");

        ed.WriteMessage(
            "\n  10. AMF_LIVRE: S0 efetiva -> S1 final -> S2 final -> S3 efetiva.");

        ed.WriteMessage(
            "\n  11. A altura total AMF permanece constante; somente AMF_LIVRE recebe Transition.");

        ed.WriteMessage(
            "\nIdentificação da subassembly:");

        ed.WriteMessage(
            $"\n  Classe SAC/.NET: {MfMacroOrClassName} ou {MfSarjetaMacroOrClassName}");

        ed.WriteMessage(
            $"\n  Parâmetro controlado por Transition: {ParameterName}");

        ed.WriteMessage(
            $"\n  Altura livre nominal: {NormalParameterName} (sem ele: valor de projeto de {ParameterName})");

        ed.WriteMessage(
            $"\n  Altura livre efetiva preferida: {EffectiveOutputParameterName} ou {EffectiveOutputAltParameterName}");

        ed.WriteMessage(
            $"\n  Rampa do MF: {RampSlopeParameterName} (sem ele: {DefaultRampSlope * 100.0:0.##}%)");

        ed.WriteMessage(
            $"\n  Destaque em planta: se a subassembly tiver {HighlightParameterName}, " +
            "um segundo Transition grava 1->2 (aba), 2->3 (pleno), 3->4 (aba); " +
            "o PKT troca os codes do topo e o Code Set aplica a hachura.");

        ed.WriteMessage(
            $"\n  Fallback geométrico: {FlowlinePointCode} -> {BackCurbPointCode}");

        ed.WriteMessage(
            "\n  O nome da instância (MF_DIR/MF_ESQ/etc.) não é usado como identidade.");

        ed.WriteMessage(
            "\n============================================================");
    }

    private sealed class FirstPointInfo
    {
        public string BaselineKey { get; init; } = "";
        public string BaselineName { get; init; } = "";
        public string RegionName { get; init; } = "";
        public double RegionStart { get; init; }
        public double RegionEnd { get; init; }
        public double Station { get; init; }
        public double Offset { get; init; }
        public double PreviousStation { get; init; }
        public SubassemblySideType Side { get; init; }
        public string SubassemblyName { get; init; } = "";

        // Altura efetiva da seção S0. É esta que serve de base
        // para o modo Redução e para o início da Transition.
        public double NormalHeight { get; init; }

        // Valor nominal AMF_LIVRE_NORMAL da subassembly.
        public double NominalNormalHeight { get; init; }

        public string EffectiveHeightSource { get; init; } = "";
    }

    private static FirstPointInfo
        InspectFirstPoint(
            Database db,
            ObjectId corridorId,
            Point3d point,
            SideSelectionMode sideMode)
    {
        using var tr =
            db.TransactionManager
              .StartTransaction();

        var corridor =
            (CivilCorridor)
            tr.GetObject(
                corridorId,
                OpenMode.ForRead);

        var hit =
            FindBestBaselineAndRegion(
                tr,
                corridor,
                point,
                0.0);

        if (hit is null)
        {
            throw new InvalidOperationException(
                "Não foi encontrada Baseline/Region válida para o 1º ponto.");
        }

        Baseline baseline =
            hit.Value.Baseline;

        BaselineRegion region =
            hit.Value.Region;

        SubassemblySideType desiredSide =
            ResolveSide(
                sideMode,
                hit.Value.Offset);

        string subassemblyInstanceName;

        ObjectId subassemblyId =
            FindMfSubassemblyId(
                tr,
                region,
                desiredSide,
                out subassemblyInstanceName);

        if (subassemblyId.IsNull)
        {
            throw new InvalidOperationException(
                "Não foi encontrada a subassembly MF " +
                $"(classe '{MfMacroOrClassName}') " +
                $"no lado {GetSideLabel(desiredSide)} " +
                $"da Assembly da Region '{region.Name}'.");
        }

        var subassembly =
            (Subassembly)
            tr.GetObject(
                subassemblyId,
                OpenMode.ForRead);

        if (!HasDoubleParameter(
                subassembly,
                ParameterName))
        {
            throw new InvalidOperationException(
                $"A subassembly '{subassemblyInstanceName}' não possui " +
                $"o parâmetro '{ParameterName}'.");
        }

        if (!TryGetNominalFreeHeight(
                subassembly,
                out double normalHeight))
        {
            throw new InvalidOperationException(
                $"A subassembly '{subassemblyInstanceName}' não possui " +
                $"o parâmetro '{NormalParameterName}' nem '{ParameterName}'.");
        }

        double[] stations =
            GetExistingStations(
                region);

        double? previousStation =
            FindPreviousStation(
                stations,
                hit.Value.Station);

        if (!previousStation.HasValue)
        {
            throw new InvalidOperationException(
                "Não existe Corridor Section anterior ao 1º ponto " +
                $"na Region '{region.Name}'.");
        }

        if (!TryGetEffectiveFreeHeightAtStation(
                baseline,
                region,
                previousStation.Value,
                subassemblyId,
                out double startEffectiveHeight,
                out string effectiveHeightSource))
        {
            throw new InvalidOperationException(
                $"Não foi possível obter a altura livre efetiva em " +
                $"S0={previousStation.Value:0.000}. " +
                $"Use o PKT com saída '{EffectiveOutputParameterName}' " +
                $"ou mantenha os point codes '{FlowlinePointCode}' e " +
                $"'{BackCurbPointCode}'.");
        }

        return new FirstPointInfo
        {
            BaselineKey = GetBaselineKey(baseline),
            BaselineName = baseline.Name,
            RegionName = region.Name,
            RegionStart = region.StartStation,
            RegionEnd = region.EndStation,
            Station = hit.Value.Station,
            Offset = hit.Value.Offset,
            PreviousStation = previousStation.Value,
            Side = desiredSide,
            SubassemblyName = subassemblyInstanceName,
            NormalHeight = startEffectiveHeight,
            NominalNormalHeight = normalHeight,
            EffectiveHeightSource = effectiveHeightSource
        };
    }

    private static void ApplyRebaixoPair(
        Database db,
        Editor ed,
        ObjectId corridorId,
        Point3d firstPoint,
        Point3d secondPoint,
        FirstPointInfo firstInfo,
        double finalHeight,
        SideSelectionMode sideMode)
    {
        using var tr =
            db.TransactionManager
              .StartTransaction();

        var corridor =
            (CivilCorridor)
            tr.GetObject(
                corridorId,
                OpenMode.ForWrite);

        var firstHit =
            FindBestBaselineAndRegion(
                tr,
                corridor,
                firstPoint,
                0.0);

        if (firstHit is null)
        {
            throw new InvalidOperationException(
                "O 1º ponto não pertence mais a uma Baseline/Region válida.");
        }

        Baseline baseline =
            firstHit.Value.Baseline;

        BaselineRegion region =
            firstHit.Value.Region;

        if (
            GetBaselineKey(baseline) != firstInfo.BaselineKey
            ||
            !region.Name.Equals(
                firstInfo.RegionName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "O contexto do 1º ponto mudou. Reinicie este par de pontos.");
        }

        var secondHit =
            FindBestBaselineAndRegion(
                tr,
                corridor,
                secondPoint,
                0.0);

        if (secondHit is null)
        {
            throw new InvalidOperationException(
                "Não foi encontrada Baseline/Region válida para o 2º ponto.");
        }

        if (
            GetBaselineKey(secondHit.Value.Baseline) != firstInfo.BaselineKey
            ||
            !secondHit.Value.Region.Name.Equals(
                firstInfo.RegionName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "O 2º ponto deve estar na mesma Baseline e Region do 1º ponto.");
        }

        if (sideMode == SideSelectionMode.Auto)
        {
            SubassemblySideType secondSide =
                ResolveSide(
                    SideSelectionMode.Auto,
                    secondHit.Value.Offset);

            if (secondSide != firstInfo.Side)
            {
                throw new InvalidOperationException(
                    "O 2º ponto foi clicado no lado oposto ao 1º ponto.");
            }
        }

        double s1 =
            firstHit.Value.Station;

        double s2 =
            secondHit.Value.Station;

        if (s2 <= s1 + StationTolerance)
        {
            throw new InvalidOperationException(
                "O 2º ponto deve estar depois do 1º ponto " +
                "no sentido crescente da Baseline.");
        }

        double[] existingStations =
            GetExistingStations(
                region);

        // As seções vizinhas servem como referência para obter a altura
        // livre NORMAL efetiva (inclusive quando um target MF_ELEV atua).
        // Elas NÃO definem mais o comprimento da rampa.
        double? previousStation =
            FindPreviousStation(
                existingStations,
                s1);

        double? nextStation =
            FindNextStation(
                existingStations,
                s2);

        if (!previousStation.HasValue)
        {
            throw new InvalidOperationException(
                "Não existe Corridor Section anterior ao 1º ponto para ler a altura normal efetiva.");
        }

        if (!nextStation.HasValue)
        {
            throw new InvalidOperationException(
                "Não existe Corridor Section posterior ao 2º ponto para ler a altura normal efetiva.");
        }

        double referenceStartStation =
            previousStation.Value;

        double referenceEndStation =
            nextStation.Value;

        string subassemblyInstanceName;

        ObjectId subassemblyId =
            FindMfSubassemblyId(
                tr,
                region,
                firstInfo.Side,
                out subassemblyInstanceName);

        if (subassemblyId.IsNull)
        {
            throw new InvalidOperationException(
                "Não foi encontrada a subassembly MF " +
                $"(classe '{MfMacroOrClassName}') " +
                $"no lado {GetSideLabel(firstInfo.Side)}.");
        }

        var subassembly =
            (Subassembly)
            tr.GetObject(
                subassemblyId,
                OpenMode.ForRead);

        if (!TryGetNominalFreeHeight(
                subassembly,
                out double normalHeight))
        {
            throw new InvalidOperationException(
                $"Não foi possível ler o parâmetro '{NormalParameterName}' nem '{ParameterName}'.");
        }

        if (!HasDoubleParameter(
                subassembly,
                ParameterName))
        {
            throw new InvalidOperationException(
                $"A subassembly selecionada não possui o parâmetro '{ParameterName}'.");
        }

        if (Math.Abs(
                normalHeight - firstInfo.NominalNormalHeight)
            > 1e-4)
        {
            throw new InvalidOperationException(
                "A altura livre nominal da subassembly mudou entre os cliques. " +
                "Reinicie este par.");
        }

        if (!TryGetDoubleParameterValue(
                subassembly,
                RampSlopeParameterName,
                out double rampSlope))
        {
            rampSlope =
                DefaultRampSlope;

            ed.WriteMessage(
                $"\n[AVISO] Subassembly sem '{RampSlopeParameterName}'; " +
                $"usando {DefaultRampSlope * 100.0:0.##}% nas rampas do MF.");
        }

        double rampSlopeAbs =
            Math.Abs(rampSlope);

        if (rampSlopeAbs <= 1e-9)
        {
            throw new InvalidOperationException(
                $"O parâmetro '{RampSlopeParameterName}' deve ser diferente de 0 para calcular a rampa.");
        }

        if (!TryGetEffectiveFreeHeightAtStation(
                baseline,
                region,
                referenceStartStation,
                subassemblyId,
                out double startEffectiveHeight,
                out string startEffectiveSource))
        {
            throw new InvalidOperationException(
                $"Não foi possível ler a altura livre efetiva na seção de referência " +
                $"SrefEntrada={referenceStartStation:0.000}.");
        }

        if (Math.Abs(
                startEffectiveHeight - firstInfo.NormalHeight)
            > 1e-4)
        {
            throw new InvalidOperationException(
                "A altura livre efetiva da seção anterior mudou entre os cliques. " +
                "Reinicie este par para recalcular o rebaixo.");
        }

        if (!TryGetEffectiveFreeHeightAtStation(
                baseline,
                region,
                referenceEndStation,
                subassemblyId,
                out double endEffectiveHeight,
                out string endEffectiveSource))
        {
            throw new InvalidOperationException(
                $"Não foi possível ler a altura livre efetiva na seção de referência " +
                $"SrefSaida={referenceEndStation:0.000}.");
        }

        double maximumFinalHeight =
            Math.Min(
                startEffectiveHeight,
                endEffectiveHeight);

        if (
            finalHeight < -StationTolerance
            ||
            finalHeight > maximumFinalHeight + StationTolerance)
        {
            throw new InvalidOperationException(
                $"Altura livre final inválida ({finalHeight:0.###} m). " +
                $"Ela deve ser <= à menor altura efetiva das extremidades " +
                $"(S0={startEffectiveHeight:0.###} m; " +
                $"S3={endEffectiveHeight:0.###} m).");
        }

        double entryRampLength =
            Math.Abs(startEffectiveHeight - finalHeight) /
            rampSlopeAbs;

        double exitRampLength =
            Math.Abs(endEffectiveHeight - finalHeight) /
            rampSlopeAbs;

        if (entryRampLength <= StationTolerance || exitRampLength <= StationTolerance)
        {
            throw new InvalidOperationException(
                "A diferença entre a altura normal efetiva e a altura final é insuficiente " +
                "para gerar uma rampa de meio-fio.");
        }

        // As extremidades das rampas são calculadas geometricamente.
        // NÃO dependem mais da posição das Corridor Sections existentes.
        double s0 =
            s1 - entryRampLength;

        double s3 =
            s2 + exitRampLength;

        if (s0 < region.StartStation - StationTolerance)
        {
            throw new InvalidOperationException(
                $"A rampa de entrada calculada ({entryRampLength:0.###} m) ultrapassa " +
                $"o início da Region '{region.Name}'.");
        }

        if (s3 > region.EndStation + StationTolerance)
        {
            throw new InvalidOperationException(
                $"A rampa de saída calculada ({exitRampLength:0.###} m) ultrapassa " +
                $"o fim da Region '{region.Name}'.");
        }

        List<CorridorTransitionSet> sets =
            baseline.getTransitions();

        string setName =
            MakeUniquePairSetName(
                sets,
                s1,
                s2,
                firstInfo.Side);

        var set =
            new CorridorTransitionSet(
                setName,
                region,
                subassemblyId,
                CorridorTransitionNameType.SubassemblyName);

        set.Comment =
            "REBAIXO MF";

        CorridorTransition t1 =
            set.AddTransition(
                ParameterName);

        SetTransition(
            t1,
            s0,
            startEffectiveHeight,
            s1,
            finalHeight);

        CorridorTransition t2 =
            set.AddTransition();

        SetTransition(
            t2,
            s1,
            finalHeight,
            s2,
            finalHeight);

        CorridorTransition t3 =
            set.AddTransition();

        SetTransition(
            t3,
            s2,
            finalHeight,
            s3,
            endEffectiveHeight);

        set.StationLocked =
            true;

        sets.Add(
            set);

        // DESTAQUE (MFSARJETA): segundo conjunto de Transitions só para os codes
        // de hachura em planta. Ver faixas em HighlightParameterName.
        bool highlightApplied =
            false;

        if (HasDoubleParameter(
                subassembly,
                HighlightParameterName))
        {
            var highlightSet =
                new CorridorTransitionSet(
                    MakeUniqueName(
                        sets,
                        setName + "_DESTAQUE"),
                    region,
                    subassemblyId,
                    CorridorTransitionNameType.SubassemblyName);

            highlightSet.Comment =
                "REBAIXO MF - DESTAQUE";

            CorridorTransition h1 =
                highlightSet.AddTransition(
                    HighlightParameterName);

            SetTransition(
                h1,
                s0,
                1.0,
                s1,
                2.0);

            CorridorTransition h2 =
                highlightSet.AddTransition();

            SetTransition(
                h2,
                s1,
                2.0,
                s2,
                3.0);

            CorridorTransition h3 =
                highlightSet.AddTransition();

            SetTransition(
                h3,
                s2,
                3.0,
                s3,
                4.0);

            highlightSet.StationLocked =
                true;

            sets.Add(
                highlightSet);

            highlightApplied =
                true;
        }


        baseline.SetTransitions(
            sets);

        int addedSections =
            0;

        addedSections +=
            EnsureStation(
                region,
                s0,
                "REBAIXO MF - INICIO RAMPA ENTRADA");

        addedSections +=
            EnsureStation(
                region,
                s1,
                "REBAIXO MF - INICIO TRECHO REBAIXADO");

        addedSections +=
            EnsureStation(
                region,
                s2,
                "REBAIXO MF - FIM TRECHO REBAIXADO");

        addedSections +=
            EnsureStation(
                region,
                s3,
                "REBAIXO MF - FIM RAMPA SAIDA");

        corridor.Rebuild();

        tr.Commit();

        double reduction =
            startEffectiveHeight -
            finalHeight;

        ed.WriteMessage(
            "\n[OK] REBAIXO MF");

        ed.WriteMessage(
            $"\n     Baseline: {baseline.Name}" +
            $" | Region: {region.Name}");

        ed.WriteMessage(
            $"\n     Lado: {GetSideLabel(firstInfo.Side)} " +
            $"({GetSideOriginLabel(sideMode)})");

        ed.WriteMessage(
            $"\n     Subassembly: {subassemblyInstanceName}");

        ed.WriteMessage(
            $"\n     Altura livre nominal={normalHeight:0.###} m");

        ed.WriteMessage(
            $"\n     Entrada efetiva={startEffectiveHeight:0.###} m " +
            $"({startEffectiveSource}, ref. {referenceStartStation:0.000})" +
            $" | redução={reduction:0.###} m" +
            $" | final={finalHeight:0.###} m");

        ed.WriteMessage(
            $"\n     Saída efetiva={endEffectiveHeight:0.###} m " +
            $"({endEffectiveSource}, ref. {referenceEndStation:0.000})");

        ed.WriteMessage(
            $"\n     iRampa={rampSlopeAbs * 100.0:0.###}%" +
            $" | L entrada={entryRampLength:0.###} m" +
            $" | L saída={exitRampLength:0.###} m");

        ed.WriteMessage(
            $"\n     Rampa entrada: {s0:0.000} -> {s1:0.000}");

        ed.WriteMessage(
            $"\n     Trecho rebaixado: {s1:0.000} -> {s2:0.000}");

        ed.WriteMessage(
            $"\n     Rampa saída: {s2:0.000} -> {s3:0.000}");

        ed.WriteMessage(
            highlightApplied
                ? $"\n     {HighlightParameterName}: aba {s0:0.000}->{s1:0.000} | " +
                  $"pleno {s1:0.000}->{s2:0.000} | aba {s2:0.000}->{s3:0.000}"
                : $"\n     {HighlightParameterName}: subassembly sem o parâmetro (sem codes de destaque)");

        ed.WriteMessage(
            $"\n     Corridor Sections adicionadas: {addedSections}\n");
    }

    // Altura livre nominal: AMF_LIVRE_NORMAL (PKT antigo) ou o valor de
    // projeto de AMF_LIVRE (MFSARJETA, que não tem AMF_LIVRE_NORMAL).
    private static bool
        TryGetNominalFreeHeight(
            Subassembly subassembly,
            out double nominalHeight)
    {
        if (TryGetDoubleParameterValue(
                subassembly,
                NormalParameterName,
                out nominalHeight))
        {
            return true;
        }

        return
            TryGetDoubleParameterValue(
                subassembly,
                ParameterName,
                out nominalHeight);
    }

    private static bool
        TryGetEffectiveFreeHeightAtStation(
            Baseline baseline,
            BaselineRegion region,
            double station,
            ObjectId subassemblyId,
            out double effectiveHeight,
            out string source)
    {
        effectiveHeight =
            0.0;

        source =
            string.Empty;

        if (!TryGetAppliedAssemblyAtStation(
                baseline,
                region,
                station,
                out AppliedAssembly appliedAssembly))
        {
            return false;
        }

        foreach (
            AppliedSubassembly appliedSubassembly
            in appliedAssembly.GetAppliedSubassemblies())
        {
            if (
                appliedSubassembly.SubassemblyId !=
                subassemblyId)
            {
                continue;
            }

            double? distP4P1 = null;
            double? gutterSlope = null;
            double? gutterWidth = null;

            // Caminho preferido: output específico da altura livre.
            foreach (
                IAppliedSubassemblyParam parameter
                in appliedSubassembly.Parameters)
            {
                if (!TryConvertToDouble(
                        parameter.ValueAsObject,
                        out double value))
                {
                    continue;
                }

                if (
                    string.Equals(
                        parameter.KeyName,
                        EffectiveOutputParameterName,
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    string.Equals(
                        parameter.KeyName,
                        EffectiveOutputAltParameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    effectiveHeight =
                        Math.Abs(value);

                    source =
                        $"output {parameter.KeyName}";

                    return true;
                }

                if (
                    string.Equals(
                        parameter.KeyName,
                        ExistingVerticalOutputParameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    distP4P1 =
                        Math.Abs(value);
                }
                else if (
                    string.Equals(
                        parameter.KeyName,
                        GutterSlopeParameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    gutterSlope =
                        value;
                }
                else if (
                    string.Equals(
                        parameter.KeyName,
                        GutterWidthParameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    gutterWidth =
                        value;
                }
            }

            // Fallback 1: pontos calculados da própria AppliedSubassembly.
            CalculatedPoint? flowlinePoint =
                null;

            CalculatedPoint? backCurbPoint =
                null;

            foreach (
                CalculatedPoint calculatedPoint
                in appliedSubassembly.Points)
            {
                if (
                    HasCorridorCode(
                        calculatedPoint.CorridorCodes,
                        FlowlinePointCode))
                {
                    flowlinePoint =
                        calculatedPoint;
                }

                if (
                    HasCorridorCode(
                        calculatedPoint.CorridorCodes,
                        BackCurbPointCode))
                {
                    backCurbPoint =
                        calculatedPoint;
                }
            }

            if (
                flowlinePoint is not null
                &&
                backCurbPoint is not null)
            {
                effectiveHeight =
                    Math.Abs(
                        backCurbPoint
                            .StationOffsetElevationToBaseline.Z
                        -
                        flowlinePoint
                            .StationOffsetElevationToBaseline.Z);

                source =
                    $"geometria {FlowlinePointCode}/{BackCurbPointCode}";

                return true;
            }

            // Fallback 2: busca pelos codes no AppliedAssembly inteiro.
            // É mais robusto em algumas versões do Civil, nas quais a
            // coleção Points da AppliedSubassembly não expõe todos os codes.
            CalculatedPoint? assemblyFlowlinePoint =
                null;

            CalculatedPoint? assemblyBackCurbPoint =
                null;

            foreach (
                CalculatedPoint calculatedPoint
                in appliedAssembly.GetPointsByCode(
                    FlowlinePointCode))
            {
                if (
                    calculatedPoint.SubassemblyBelongedTo.SubassemblyId ==
                    subassemblyId)
                {
                    assemblyFlowlinePoint =
                        calculatedPoint;
                    break;
                }
            }

            foreach (
                CalculatedPoint calculatedPoint
                in appliedAssembly.GetPointsByCode(
                    BackCurbPointCode))
            {
                if (
                    calculatedPoint.SubassemblyBelongedTo.SubassemblyId ==
                    subassemblyId)
                {
                    assemblyBackCurbPoint =
                        calculatedPoint;
                    break;
                }
            }

            if (
                assemblyFlowlinePoint is not null
                &&
                assemblyBackCurbPoint is not null)
            {
                effectiveHeight =
                    Math.Abs(
                        assemblyBackCurbPoint
                            .StationOffsetElevationToBaseline.Z
                        -
                        assemblyFlowlinePoint
                            .StationOffsetElevationToBaseline.Z);

                source =
                    $"assembly codes {FlowlinePointCode}/{BackCurbPointCode}";

                return true;
            }

            // Fallback 3: o PKT atual já publica DIST_P4_P1_Y.
            // Como P2 = P1 + (iSa * LS) e P3/P4 têm a mesma cota,
            // altura livre = (P4-P1) - (iSa*LS).
            if (
                distP4P1.HasValue
                &&
                gutterSlope.HasValue
                &&
                gutterWidth.HasValue)
            {
                effectiveHeight =
                    Math.Abs(
                        distP4P1.Value
                        -
                        (gutterSlope.Value * gutterWidth.Value));

                source =
                    $"{ExistingVerticalOutputParameterName} + " +
                    $"{GutterSlopeParameterName}/{GutterWidthParameterName}";

                return true;
            }
        }

        return false;
    }

    private static bool
        TryGetAppliedAssemblyAtStation(
            Baseline baseline,
            BaselineRegion region,
            double station,
            out AppliedAssembly appliedAssembly)
    {
        // Preferir a coleção da própria Region evita ambiguidade em
        // estações que coincidem exatamente com limite entre Regions.
        foreach (
            AppliedAssembly candidate
            in region.AppliedAssemblies)
        {
            bool stationMatches =
                false;

            foreach (
                CalculatedPoint point
                in candidate.Points)
            {
                if (
                    Math.Abs(
                        point.StationOffsetElevationToBaseline.X
                        - station)
                    <= 1e-4)
                {
                    stationMatches =
                        true;
                }

                break;
            }

            if (!stationMatches)
            {
                foreach (
                    AppliedSubassembly subassembly
                    in candidate.GetAppliedSubassemblies())
                {
                    if (
                        Math.Abs(
                            subassembly
                                .OriginStationOffsetElevationToBaseline.X
                            - station)
                        <= 1e-4)
                    {
                        stationMatches =
                            true;
                        break;
                    }
                }
            }

            if (stationMatches)
            {
                appliedAssembly =
                    candidate;
                return true;
            }
        }

        // Fallback para estações internas sem ambiguidade.
        try
        {
            appliedAssembly =
                baseline.GetAppliedAssemblyAtStation(
                    station);

            return true;
        }
        catch
        {
            appliedAssembly =
                null!;

            return false;
        }
    }

    private static bool
        TryConvertToDouble(
            object? rawValue,
            out double value)
    {
        value =
            0.0;

        if (rawValue is double directValue)
        {
            value =
                directValue;
            return true;
        }

        try
        {
            if (rawValue is IConvertible convertible)
            {
                value =
                    convertible.ToDouble(
                        System.Globalization.CultureInfo.InvariantCulture);

                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool
        HasCorridorCode(
            CorridorCodeCollection codes,
            string expectedCode)
    {
        foreach (
            string code
            in codes)
        {
            if (
                string.Equals(
                    code,
                    expectedCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static double[]
        GetExistingStations(
            BaselineRegion region)
    {
        IEnumerable<double> stations;

        try
        {
            stations =
                region.SortedStations();
        }
        catch
        {
            var fallback =
                new List<double>();

            try
            {
                fallback.AddRange(
                    region.AdditionalStations());
            }
            catch
            {
            }

            fallback.Add(
                region.StartStation);

            fallback.Add(
                region.EndStation);

            stations =
                fallback;
        }

        return stations
            .Where(
                x =>
                    x >= region.StartStation - StationTolerance
                    &&
                    x <= region.EndStation + StationTolerance)
            .OrderBy(
                x => x)
            .Distinct(
                new StationComparer())
            .ToArray();
    }

    private sealed class StationComparer : IEqualityComparer<double>
    {
        public bool Equals(
            double x,
            double y) =>
            Math.Abs(x - y) <= StationTolerance;

        public int GetHashCode(
            double obj) =>
            0;
    }

    private static double?
        FindPreviousStation(
            IEnumerable<double> stations,
            double station)
    {
        double? result =
            null;

        foreach (
            double candidate
            in stations)
        {
            if (
                candidate <
                station - StationTolerance)
            {
                result =
                    candidate;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static double?
        FindNextStation(
            IEnumerable<double> stations,
            double station)
    {
        foreach (
            double candidate
            in stations)
        {
            if (
                candidate >
                station + StationTolerance)
            {
                return candidate;
            }
        }

        return null;
    }

    private static (
        Baseline Baseline,
        BaselineRegion Region,
        double Station,
        double Offset)?
        FindBestBaselineAndRegion(
            Transaction tr,
            CivilCorridor corridor,
            Point3d point,
            double requiredLength)
    {
        (
            Baseline Baseline,
            BaselineRegion Region,
            double Station,
            double Offset)?
            best =
                null;

        foreach (
            Baseline baseline
            in corridor.Baselines)
        {
            if (!TryGetBaselineStationOffset(
                    tr,
                    baseline,
                    point,
                    out double station,
                    out double offset))
            {
                continue;
            }

            if (
                station <
                baseline.StartStation -
                StationTolerance
                ||
                station >
                baseline.EndStation +
                StationTolerance)
            {
                continue;
            }

            BaselineRegion? region =
                FindRegion(
                    baseline,
                    station,
                    station +
                    requiredLength);

            if (
                region is null)
            {
                continue;
            }

            if (
                best is null
                ||
                Math.Abs(offset) <
                Math.Abs(
                    best.Value.Offset))
            {
                best =
                    (
                        baseline,
                        region,
                        station,
                        offset
                    );
            }
        }

        return best;
    }

    // Estaca/offset de um ponto em planta em qualquer Baseline.
    // Alinhamento: Alignment.StationOffset. Feature line: inversão numérica de
    // Baseline.StationOffsetElevationToXYZ (varredura + refino), a mesma função
    // que o CorridorSplit já usa em baselines de feature line.
    private static bool
        TryGetBaselineStationOffset(
            Transaction tr,
            Baseline baseline,
            Point3d point,
            out double station,
            out double offset)
    {
        station =
            0.0;

        offset =
            0.0;

        bool featureLineBased;

        try
        {
            featureLineBased =
                baseline.IsFeatureLineBased();
        }
        catch
        {
            return false;
        }

        if (!featureLineBased)
        {
            try
            {
                ObjectId alignmentId =
                    baseline.AlignmentId;

                if (alignmentId.IsNull)
                    return false;

                if (tr.GetObject(
                        alignmentId,
                        OpenMode.ForRead)
                    is not Alignment alignment)
                {
                    return false;
                }

                alignment.StationOffset(
                    point.X,
                    point.Y,
                    ref station,
                    ref offset);

                return true;
            }
            catch
            {
                return false;
            }
        }

        double start;
        double end;

        try
        {
            start =
                Math.Min(
                    baseline.StartStation,
                    baseline.EndStation);

            end =
                Math.Max(
                    baseline.StartStation,
                    baseline.EndStation);
        }
        catch
        {
            return false;
        }

        if (end - start <= StationTolerance)
            return false;

        var target =
            new Point2d(
                point.X,
                point.Y);

        double DistanceAt(double s)
        {
            Point3d xyz =
                baseline.StationOffsetElevationToXYZ(
                    new Point3d(
                        s,
                        0.0,
                        0.0));

            return
                new Point2d(
                    xyz.X,
                    xyz.Y)
                .GetDistanceTo(
                    target);
        }

        // 1) varredura grossa (passo de ~0,5 m, no máximo 4000 amostras)
        int samples =
            (int)Math.Clamp(
                Math.Ceiling(
                    (end - start) / 0.5),
                50,
                4000);

        double step =
            (end - start) / samples;

        double bestS =
            start;

        double bestD =
            double.MaxValue;

        try
        {
            for (int i = 0; i <= samples; i++)
            {
                double s =
                    start + i * step;

                double d =
                    DistanceAt(s);

                if (d < bestD)
                {
                    bestD =
                        d;

                    bestS =
                        s;
                }
            }

            // 2) refino por seção áurea em torno da melhor amostra
            double a =
                Math.Max(
                    start,
                    bestS - step);

            double b =
                Math.Min(
                    end,
                    bestS + step);

            const double phi =
                0.6180339887498949;

            double c =
                b - phi * (b - a);

            double e =
                a + phi * (b - a);

            double fc =
                DistanceAt(c);

            double fe =
                DistanceAt(e);

            for (int it = 0; it < 60 && b - a > 1e-5; it++)
            {
                if (fc < fe)
                {
                    b = e;
                    e = c;
                    fe = fc;
                    c = b - phi * (b - a);
                    fc = DistanceAt(c);
                }
                else
                {
                    a = c;
                    c = e;
                    fc = fe;
                    e = a + phi * (b - a);
                    fe = DistanceAt(e);
                }
            }

            station =
                0.5 * (a + b);

            // 3) offset com sinal: positivo à direita do sentido da baseline
            double ds =
                Math.Min(
                    0.05,
                    0.25 * (end - start));

            double s0 =
                Math.Max(
                    start,
                    station - ds);

            double s1 =
                Math.Min(
                    end,
                    station + ds);

            Point3d p0 =
                baseline.StationOffsetElevationToXYZ(
                    new Point3d(s0, 0.0, 0.0));

            Point3d p1 =
                baseline.StationOffsetElevationToXYZ(
                    new Point3d(s1, 0.0, 0.0));

            Point3d pc =
                baseline.StationOffsetElevationToXYZ(
                    new Point3d(station, 0.0, 0.0));

            var tangent =
                new Vector2d(
                    p1.X - p0.X,
                    p1.Y - p0.Y);

            if (tangent.Length <= 1e-9)
                return false;

            tangent =
                tangent.GetNormal();

            var rightNormal =
                new Vector2d(
                    tangent.Y,
                    -tangent.X);

            var toPoint =
                new Vector2d(
                    point.X - pc.X,
                    point.Y - pc.Y);

            offset =
                toPoint.DotProduct(
                    rightNormal);

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Identidade da Baseline que não depende de alinhamento
    // (baselines de feature line não têm AlignmentId).
    private static string
        GetBaselineKey(
            Baseline baseline)
    {
        try
        {
            if (baseline.IsFeatureLineBased())
                return "FL:" + baseline.FeatureLineId.Handle;

            return "AL:" + baseline.AlignmentId.Handle;
        }
        catch
        {
            return "NM:" + baseline.Name;
        }
    }

    private static BaselineRegion?
        FindRegion(
            Baseline baseline,
            double start,
            double end)
    {
        foreach (
            BaselineRegion region
            in baseline.BaselineRegions)
        {
            if (
                start >=
                region.StartStation -
                StationTolerance
                &&
                end <=
                region.EndStation +
                StationTolerance)
            {
                return region;
            }
        }

        return null;
    }

    private static ObjectId
        FindMfSubassemblyId(
            Transaction tr,
            BaselineRegion region,
            SubassemblySideType desiredSide,
            out string instanceName)
    {
        instanceName =
            string.Empty;

        var assembly =
            tr.GetObject(
                region.AssemblyId,
                OpenMode.ForRead)
            as CivilAssembly;

        if (
            assembly is null)
        {
            return ObjectId.Null;
        }

        foreach (
            AssemblyGroup group
            in assembly.Groups)
        {
            foreach (
                ObjectId id
                in group.GetSubassemblyIds())
            {
                var sa =
                    tr.GetObject(
                        id,
                        OpenMode.ForRead)
                    as Subassembly;

                if (
                    sa is null)
                {
                    continue;
                }

                if (
                    !IsMfSubassembly(
                        sa))
                {
                    continue;
                }

                if (
                    !IsSubassemblyOnSide(
                        sa,
                        desiredSide))
                {
                    continue;
                }

                instanceName =
                    sa.Name;

                return id;
            }
        }

        return ObjectId.Null;
    }

    private static bool
        IsMfSubassembly(
            Subassembly sa)
    {
        try
        {
            var generator =
                sa.GeometryGenerator;

            if (
                generator is not null
                &&
                (
                    string.Equals(
                        generator.MacroOrClassName,
                        MfMacroOrClassName,
                        StringComparison.OrdinalIgnoreCase)
                    ||
                    string.Equals(
                        generator.MacroOrClassName,
                        MfSarjetaMacroOrClassName,
                        StringComparison.OrdinalIgnoreCase)
                ))
            {
                return true;
            }
        }
        catch
        {
        }

        // Assinatura do MFSARJETA (quando a classe não é legível).
        if (
            HasDoubleParameter(
                sa,
                "AMF_LIVRE")
            &&
            HasDoubleParameter(
                sa,
                "LS")
            &&
            HasDoubleParameter(
                sa,
                "ESar")
            &&
            HasDoubleParameter(
                sa,
                "EMB"))
        {
            return true;
        }

        return
            HasDoubleParameter(
                sa,
                "AMF")
            &&
            HasDoubleParameter(
                sa,
                "AMF_LIVRE")
            &&
            HasDoubleParameter(
                sa,
                "LS")
            &&
            HasDoubleParameter(
                sa,
                "LMF")
            &&
            HasDoubleParameter(
                sa,
                "OP")
            &&
            HasDoubleParameter(
                sa,
                "T");
    }

    private static bool
        HasDoubleParameter(
            Subassembly sa,
            string key)
    {
        try
        {
            foreach (
                var parameter
                in sa.ParamsDouble)
            {
                if (
                    string.Equals(
                        parameter.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool
        TryGetDoubleParameterValue(
            Subassembly sa,
            string key,
            out double value)
    {
        value =
            0.0;

        try
        {
            foreach (
                var parameter
                in sa.ParamsDouble)
            {
                if (
                    string.Equals(
                        parameter.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value =
                        parameter.Value;

                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool
        IsSubassemblyOnSide(
            Subassembly sa,
            SubassemblySideType desiredSide)
    {
        try
        {
            foreach (
                var parameter
                in sa.ParamsLong)
            {
                if (
                    !string.Equals(
                        parameter.Key,
                        "Side",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int sideValue =
                    parameter.Value;

                if (
                    desiredSide ==
                    SubassemblySideType.Right)
                {
                    return
                        sideValue == 0;
                }

                if (
                    desiredSide ==
                    SubassemblySideType.Left)
                {
                    return
                        sideValue == 1;
                }
            }
        }
        catch
        {
        }

        try
        {
            return
                sa.HasSide
                &&
                sa.Side ==
                desiredSide;
        }
        catch
        {
            return false;
        }
    }

    private static SubassemblySideType
        ResolveSide(
            SideSelectionMode mode,
            double signedOffset)
    {
        if (
            mode ==
            SideSelectionMode.Left)
        {
            return
                SubassemblySideType.Left;
        }

        if (
            mode ==
            SideSelectionMode.Right)
        {
            return
                SubassemblySideType.Right;
        }

        if (
            Math.Abs(
                signedOffset)
            <=
            AutoSideOffsetTolerance)
        {
            throw new InvalidOperationException(
                "O clique ficou praticamente " +
                "sobre a Baseline " +
                $"(offset={signedOffset:0.###} m). " +
                "Clique claramente no lado desejado " +
                "ou force [Esquerda/Direita].");
        }

        return
            signedOffset > 0.0
                ? SubassemblySideType.Right
                : SubassemblySideType.Left;
    }

    private static string
        GetSideModeLabel(
            SideSelectionMode mode)
    {
        return mode switch
        {
            SideSelectionMode.Left =>
                "ESQUERDA",

            SideSelectionMode.Right =>
                "DIREITA",

            _ =>
                "AUTO"
        };
    }

    private static string
        GetSideLabel(
            SubassemblySideType side)
    {
        return
            side ==
            SubassemblySideType.Left
                ? "ESQUERDO"
                : "DIREITO";
    }

    private static string
        GetSideOriginLabel(
            SideSelectionMode mode)
    {
        return
            mode ==
            SideSelectionMode.Auto
                ? "pelo clique"
                : "forçado";
    }

    private static void
        SetTransition(
            CorridorTransition transition,
            double startStation,
            double startValue,
            double endStation,
            double endValue)
    {
        transition.StartStation =
            startStation;

        transition.StartValue =
            startValue;

        transition.EndStation =
            endStation;

        transition.EndValue =
            endValue;

        transition.TransitionType =
            CorridorTransitionType.Linear;
    }

    private static int
        EnsureStation(
            BaselineRegion region,
            double station,
            string description)
    {
        double[] existing;

        try
        {
            existing =
                region.SortedStations();
        }
        catch
        {
            existing =
                region.AdditionalStations();
        }

        if (
            existing.Any(
                x =>
                    Math.Abs(
                        x - station)
                    <=
                    StationTolerance))
        {
            return 0;
        }

        region.AddStation(
            station,
            description);

        return 1;
    }

    private static string
        MakeUniquePairSetName(
            List<CorridorTransitionSet> sets,
            double firstLoweredStation,
            double secondLoweredStation,
            SubassemblySideType side)
    {
        string sideCode =
            side == SubassemblySideType.Left
                ? "ESQ"
                : "DIR";

        string baseName =
            $"MF_REBAIXO_{sideCode}_" +
            $"{firstLoweredStation:0.000}_" +
            $"{secondLoweredStation:0.000}";

        return
            MakeUniqueName(
                sets,
                baseName);
    }

    private static string
        MakeUniqueSetName(
            List<CorridorTransitionSet> sets,
            MFRebaixoPreset preset,
            double station,
            SubassemblySideType side)
    {
        string sideCode =
            side ==
            SubassemblySideType.Left
                ? "ESQ"
                : "DIR";

        string baseName =
            $"MF_" +
            $"{preset.Nome.ToUpperInvariant()}" +
            $"_{sideCode}" +
            $"_{station:0.000}";

        return
            MakeUniqueName(
                sets,
                baseName);
    }

    private static string
        MakeUniqueName(
            List<CorridorTransitionSet> sets,
            string baseName)
    {
        var names =
            new HashSet<string>(
                sets.Select(
                    x => x.Name),
                StringComparer.OrdinalIgnoreCase);

        if (
            !names.Contains(
                baseName))
        {
            return baseName;
        }

        int n =
            2;

        while (
            names.Contains(
                $"{baseName}_{n}"))
        {
            n++;
        }

        return
            $"{baseName}_{n}";
    }

    private static string?
        AskPreset(
            Editor ed,
            MFRebaixoSettings settings,
            bool allowConfig)
    {
        while (true)
        {
            string current =
                settings.UltimoPreset.Equals(
                    "Pedestre",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Pedestre"
                    : "Garagem";

            var pko =
                new PromptKeywordOptions(
                    allowConfig
                        ? "\nTipo de rebaixo " +
                          "[Garagem/Pedestre/Configurar] " +
                          $"<{current}>: "
                        : "\nPreset para configurar " +
                          "[Garagem/Pedestre] " +
                          $"<{current}>: ")
                {
                    AllowNone =
                        true
                };

            pko.Keywords.Add(
                "Garagem");

            pko.Keywords.Add(
                "Pedestre");

            if (
                allowConfig)
            {
                pko.Keywords.Add(
                    "Configurar");
            }

            pko.Keywords.Default =
                current;

            PromptResult pkr =
                ed.GetKeywords(
                    pko);

            if (
                pkr.Status ==
                PromptStatus.Cancel)
            {
                return null;
            }

            string chosen =
                pkr.Status ==
                PromptStatus.None
                    ? current
                    : pkr.StringResult;

            if (
                allowConfig
                &&
                chosen.Equals(
                    "Configurar",
                    StringComparison.OrdinalIgnoreCase))
            {
                string? toConfigure =
                    AskPreset(
                        ed,
                        settings,
                        allowConfig: false);

                if (
                    toConfigure is null)
                {
                    return null;
                }

                ConfigurePreset(
                    ed,
                    settings,
                    toConfigure);

                MFRebaixoSettingsStore.Save(
                    settings);

                continue;
            }

            settings.UltimoPreset =
                chosen;

            MFRebaixoSettingsStore.Save(
                settings);

            return chosen;
        }
    }

    private static void
        ConfigurePreset(
            Editor ed,
            MFRebaixoSettings settings,
            string presetName)
    {
        MFRebaixoPreset p =
            settings.GetPreset(
                presetName);

        ed.WriteMessage(
            $"\n--- Configurando " +
            $"{p.Comment} ---");

        p.AlturaNormal =
            AskDouble(
                ed,
                "Altura livre normal",
                p.AlturaNormal,
                min: 0.0);

        ValueInputMode valueMode =
            ParseValueInputMode(
                settings.UltimoModoValor);

        // Configuração legada dos presets. O MFREBAIXO v1.2.0 lê
        // a altura normal diretamente da subassembly e usa o último
        // modo/valor como padrão de lançamento.
        settings.UltimaAlturaFinal =
            p.AlturaRebaixada;

        settings.UltimaReducao =
            Math.Max(
                0.0,
                p.AlturaNormal - p.AlturaRebaixada);

        if (AskLaunchValue(
                ed,
                settings,
                p.AlturaNormal,
                ref valueMode,
                out double configuredFinalHeight))
        {
            p.AlturaRebaixada =
                configuredFinalHeight;
        }

        p.ExtensaoTransicao =
            AskDouble(
                ed,
                "Extensão de CADA transição",
                p.ExtensaoTransicao,
                min: 0.000001);

        p.ExtensaoRebaixo =
            AskDouble(
                ed,
                "Extensão efetiva rebaixada",
                p.ExtensaoRebaixo,
                min: 0.000001);

        settings.UltimoPreset =
            p.Nome;

        ed.WriteMessage(
            $"\nConfiguração salva. " +
            "As extensões do preset são mantidas apenas por compatibilidade; " +
            "o MFREBAIXO v1.2.2 usa as Corridor Sections vizinhas aos cliques.");

        ed.WriteMessage(
            $"\nPreset legado salvo: altura livre final = " +
            $"{p.AlturaRebaixada:0.###} m.");
    }

    private static bool
        AskLaunchValue(
            Editor ed,
            MFRebaixoSettings settings,
            double normalHeight,
            ref ValueInputMode mode,
            out double finalHeight)
    {
        finalHeight =
            normalHeight;

        string defaultKeyword =
            mode == ValueInputMode.FinalHeight
                ? "AlturaFinal"
                : "Reducao";

        var pko =
            new PromptKeywordOptions(
                "\nComo deseja informar o rebaixo " +
                $"[Reducao/AlturaFinal] <{defaultKeyword}>: ")
            {
                AllowNone = true
            };

        pko.Keywords.Add(
            "Reducao");

        pko.Keywords.Add(
            "AlturaFinal");

        pko.Keywords.Default =
            defaultKeyword;

        PromptResult pkr =
            ed.GetKeywords(
                pko);

        if (pkr.Status == PromptStatus.Cancel)
            return false;

        string chosen =
            pkr.Status == PromptStatus.None
                ? defaultKeyword
                : pkr.StringResult;

        mode =
            chosen.Equals(
                "AlturaFinal",
                StringComparison.OrdinalIgnoreCase)
                ? ValueInputMode.FinalHeight
                : ValueInputMode.Reduction;

        settings.UltimoModoValor =
            mode == ValueInputMode.FinalHeight
                ? "AlturaFinal"
                : "Reducao";

        if (mode == ValueInputMode.Reduction)
        {
            double currentReduction =
                settings.UltimaReducao;

            if (
                currentReduction < 0.0
                ||
                currentReduction > normalHeight)
            {
                currentReduction =
                    Math.Max(
                        0.0,
                        normalHeight -
                        Math.Min(
                            normalHeight,
                            settings.UltimaAlturaFinal));
            }

            while (true)
            {
                var pdo =
                    new PromptDoubleOptions(
                        "\nQuanto deseja ABAIXAR da altura livre efetiva do meio-fio " +
                        $"<{currentReduction:0.###}>: ")
                    {
                        AllowNone = true,
                        AllowNegative = false,
                        AllowZero = true,
                        DefaultValue = currentReduction,
                        UseDefaultValue = true
                    };

                PromptDoubleResult pdr =
                    ed.GetDouble(
                        pdo);

                if (pdr.Status == PromptStatus.Cancel)
                    return false;

                double reduction =
                    pdr.Status == PromptStatus.None
                        ? currentReduction
                        : pdr.Value;

                if (reduction <= normalHeight)
                {
                    finalHeight =
                        normalHeight -
                        reduction;

                    settings.UltimaReducao =
                        reduction;

                    settings.UltimaAlturaFinal =
                        finalHeight;

                    MFRebaixoSettingsStore.Save(
                        settings);

                    ed.WriteMessage(
                        $"\nAltura livre de referência={normalHeight:0.###} m" +
                        $" | redução={reduction:0.###} m" +
                        $" | Altura livre final={finalHeight:0.###} m.");

                    return true;
                }

                ed.WriteMessage(
                    $"\nA redução não pode ser maior que a altura livre de referência " +
                    $"({normalHeight:0.###} m).");
            }
        }

        double currentFinalHeight =
            settings.UltimaAlturaFinal;

        if (
            currentFinalHeight < 0.0
            ||
            currentFinalHeight > normalHeight)
        {
            currentFinalHeight =
                Math.Max(
                    0.0,
                    normalHeight -
                    Math.Min(
                        normalHeight,
                        settings.UltimaReducao));
        }

        while (true)
        {
            var pdo =
                new PromptDoubleOptions(
                    "\nAltura livre FINAL do meio-fio " +
                    $"<{currentFinalHeight:0.###}>: ")
                {
                    AllowNone = true,
                    AllowNegative = false,
                    AllowZero = true,
                    DefaultValue = currentFinalHeight,
                    UseDefaultValue = true
                };

            PromptDoubleResult pdr =
                ed.GetDouble(
                    pdo);

            if (pdr.Status == PromptStatus.Cancel)
                return false;

            double requestedFinalHeight =
                pdr.Status == PromptStatus.None
                    ? currentFinalHeight
                    : pdr.Value;

            if (requestedFinalHeight <= normalHeight)
            {
                finalHeight =
                    requestedFinalHeight;

                double reduction =
                    normalHeight -
                    finalHeight;

                settings.UltimaAlturaFinal =
                    finalHeight;

                settings.UltimaReducao =
                    reduction;

                MFRebaixoSettingsStore.Save(
                    settings);

                ed.WriteMessage(
                    $"\nAltura livre normal={normalHeight:0.###} m" +
                    $" | redução={reduction:0.###} m" +
                    $" | Altura livre final={finalHeight:0.###} m.");

                return true;
            }

            ed.WriteMessage(
                $"\nA altura final não pode ser maior que a altura livre de referência " +
                $"({normalHeight:0.###} m).");
        }
    }

    private static ValueInputMode
        ParseValueInputMode(
            string? value)
    {
        return
            value is not null
            && value.Equals(
                "AlturaFinal",
                StringComparison.OrdinalIgnoreCase)
                ? ValueInputMode.FinalHeight
                : ValueInputMode.Reduction;
    }

    private static string
        GetValueModeLabel(
            ValueInputMode mode)
    {
        return
            mode == ValueInputMode.FinalHeight
                ? "ALTURA FINAL"
                : "REDUÇÃO";
    }

    private static double
        AskDouble(
            Editor ed,
            string label,
            double current,
            double min)
    {
        var pdo =
            new PromptDoubleOptions(
                $"\n{label} " +
                $"<{current:0.###}>: ")
            {
                AllowNone =
                    true,

                AllowNegative =
                    false,

                AllowZero =
                    min <= 0.0,

                DefaultValue =
                    current,

                UseDefaultValue =
                    true
            };

        while (true)
        {
            PromptDoubleResult pdr =
                ed.GetDouble(
                    pdo);

            if (
                pdr.Status ==
                PromptStatus.None
                ||
                pdr.Status ==
                PromptStatus.Cancel)
            {
                return current;
            }

            if (
                pdr.Status ==
                PromptStatus.OK
                &&
                pdr.Value >= min)
            {
                return pdr.Value;
            }

            ed.WriteMessage(
                $"\nValor deve ser >= " +
                $"{min:0.######}.");
        }
    }

    private static void
        PrintPreset(
            Editor ed,
            MFRebaixoPreset p)
    {
        ed.WriteMessage(
            $"\n{p.Comment}:");

        ed.WriteMessage(
            $"\n  Altura livre normal " +
            $"{p.AlturaNormal:0.###} m");

        ed.WriteMessage(
            $"\n  Redução padrão..... " +
            $"{(p.AlturaNormal - p.AlturaRebaixada):0.###} m");

        ed.WriteMessage(
            $"\n  Altura livre final. " +
            $"{p.AlturaRebaixada:0.###} m");

        ed.WriteMessage(
            $"\n  Preset legado...... " +
            $"{p.AlturaRebaixada:0.###} m");

        ed.WriteMessage(
            $"\n  Transição........... " +
            $"{p.ExtensaoTransicao:0.###} m por lado");

        ed.WriteMessage(
            $"\n  Rebaixo efetivo..... " +
            $"{p.ExtensaoRebaixo:0.###} m");

        ed.WriteMessage(
            $"\n  Extensão total...... " +
            $"{p.ExtensaoTotal:0.###} m\n");
    }
}