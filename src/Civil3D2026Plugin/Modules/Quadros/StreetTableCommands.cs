using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcGi = Autodesk.AutoCAD.GraphicsInterface;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace Civil3D2026Plugin.Modules.Quadros;

/// <summary>
/// C3DQUADRORUAS: monta e exporta o QUADRO DE RUAS (.xlsx no layout da planilha
/// modelo) a partir dos eixos das ruas (polilinhas, alinhamentos, linhas...).
///
/// - as ruas ficam guardadas no DWG, ligadas aos eixos; a exportacao sempre
///   rele a geometria atual (extensao, estacas, coordenadas);
/// - nome: TEXT/MTEXT selecionado (relido na exportacao) ou digitado;
/// - largura da pista: cotas selecionadas (somadas, relidas na exportacao) ou
///   valor/expressao digitada (ex.: 3.2*2), gravada como formula no Excel;
/// - estaqueamento automatico pela extensao em planta (intervalo padrao 20 m);
///   alinhamentos usam as proprias estacas;
/// - coordenadas geograficas em DD°MM'SS.ss" do inicio e do fim: pela
///   geolocalizacao do desenho ou, na falta dela, por UTM (fuso do sistema de
///   coordenadas do Civil 3D ou informado), elipsoide GRS80/SIRGAS 2000.
/// </summary>
public sealed class StreetTableCommands
{
    public const string Versao = C3DVersions.QuadroRuas;

    private const string RootDictionaryName = "C3D_QUADRO_RUAS_V1";
    private const string SettingsKey = "SETTINGS";
    private const string RowPrefix = "ROW_";
    private const string RowSignature = "QRUA1";
    private const string SettingsSignature = "QRUACFG1";

    private sealed class Settings
    {
        public double StakeInterval { get; set; } = 20.0;
        public string ZoneOverride { get; set; } = string.Empty;
        public string Responsible { get; set; } = string.Empty;
        public string Registration { get; set; } = string.Empty;
        public string Place { get; set; } = string.Empty;
        public string LastWidth { get; set; } = "7";
        public bool IncludeTotals { get; set; } = true;
        public bool IncludeSignature { get; set; } = true;
    }

    private sealed class StreetEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public long Order { get; set; } = DateTime.UtcNow.Ticks;
        public string CurveHandle { get; set; } = string.Empty;
        public bool Reversed { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NameTextHandle { get; set; } = string.Empty;
        public string WidthExpression { get; set; } = string.Empty;
        public string DimensionHandles { get; set; } = string.Empty;
        public double StartStation { get; set; }
    }

    /// <summary>Valores calculados de uma rua a partir da geometria atual.</summary>
    private sealed class StreetGeometry
    {
        public string Name { get; init; } = string.Empty;
        public double LengthM { get; init; }
        public double StartStation { get; init; }
        public double EndStation { get; init; }
        public string WidthFormula { get; init; } = string.Empty;
        public double WidthM { get; init; }
        public Point3d StartPoint { get; init; }
        public Point3d EndPoint { get; init; }
    }

    // ------------------------------------------------------------------
    // Comando
    // ------------------------------------------------------------------

    [CommandMethod(C3DCommands.Quadros.Ruas, CommandFlags.Modal)]
    public void QuadroRuas()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        try
        {
            int count = LoadEntries(db).Count;

            ed.WriteMessage(
                "\n" + C3DCommands.Quadros.Ruas + " v" + Versao + " - quadro de ruas (" + count + " rua(s) no desenho).");

            string defaultOption = count == 0 ? "Adicionar" : "Exportar";

            PromptKeywordOptions options = new(
                "\nOpcao [Adicionar/Listar/Remover/Exportar/Config] <" + defaultOption + ">: ",
                "Adicionar Listar Remover Exportar Config")
            {
                AllowNone = true
            };

            PromptResult result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                return;

            string choice = result.Status == PromptStatus.OK ? result.StringResult : defaultOption;

            switch (choice)
            {
                case "Adicionar":
                    AddLoop(doc);
                    break;
                case "Listar":
                    ListEntries(doc);
                    break;
                case "Remover":
                    RemoveEntry(doc);
                    break;
                case "Config":
                    Configure(doc);
                    break;
                default:
                    Export(doc);
                    break;
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage("\n[ERRO " + C3DCommands.Quadros.Ruas + "] " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Adicionar
    // ------------------------------------------------------------------

    private static void AddLoop(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;

        while (true)
        {
            PromptEntityOptions curveOptions = new(
                "\nSelecione o EIXO da rua (polilinha, alinhamento, linha, arco) ou [Exportar/Sair]: ");
            curveOptions.SetRejectMessage("\nSelecione uma polilinha, alinhamento, linha, arco ou spline.");
            curveOptions.AddAllowedClass(typeof(AcadDb.Curve), false);
            curveOptions.Keywords.Add("Exportar");
            curveOptions.Keywords.Add("Sair");
            curveOptions.AppendKeywordsToMessage = false;

            PromptEntityResult picked = ed.GetEntity(curveOptions);

            if (picked.Status == PromptStatus.Keyword)
            {
                if (picked.StringResult == "Exportar")
                    Export(doc);
                return;
            }

            if (picked.Status != PromptStatus.OK)
                return;

            Settings settings = LoadSettings(db);
            List<StreetEntry> entries = LoadEntries(db);
            string curveHandle = picked.ObjectId.Handle.ToString();

            StreetEntry entry = entries.FirstOrDefault(e => e.CurveHandle == curveHandle) ?? new StreetEntry();
            bool replacing = entries.Any(e => e.Id == entry.Id);

            if (replacing)
            {
                PromptKeywordOptions replace = new(
                    "\nEste eixo ja esta no quadro (" + entry.Name + "). [Substituir/Cancelar] <Substituir>: ",
                    "Substituir Cancelar")
                {
                    AllowNone = true
                };

                PromptResult replaceResult = ed.GetKeywords(replace);
                if (replaceResult.Status == PromptStatus.Cancel ||
                    (replaceResult.Status == PromptStatus.OK && replaceResult.StringResult == "Cancelar"))
                {
                    continue;
                }
            }

            entry.CurveHandle = curveHandle;

            bool isAlignment;
            using (AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                AcadDb.Curve curve = (AcadDb.Curve)tr.GetObject(picked.ObjectId, AcadDb.OpenMode.ForRead);
                isAlignment = curve is CivilAlignment;

                // Sentido do estaqueamento (alinhamentos usam o proprio sentido).
                if (!isAlignment)
                {
                    bool? reversed = AskDirection(ed, curve, replacing ? entry.Reversed : false);
                    if (reversed is null)
                        return;
                    entry.Reversed = reversed.Value;
                }
                else
                {
                    entry.Reversed = false;
                }
            }

            if (!AskName(ed, entry))
                return;

            if (!AskWidth(ed, db, entry, settings))
                return;

            if (isAlignment)
            {
                entry.StartStation = 0.0; // as estacas vem do proprio alinhamento
            }
            else
            {
                double? station = AskStation(ed, replacing ? entry.StartStation : 0.0, settings.StakeInterval);
                if (station is null)
                    return;
                entry.StartStation = station.Value;
            }

            using (doc.LockDocument())
            using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
            {
                SaveEntry(tr, db, entry);
                settings.LastWidth = entry.WidthExpression.Length > 0 ? entry.WidthExpression : settings.LastWidth;
                SaveSettings(tr, db, settings);
                tr.Commit();
            }

            ReportEntry(doc, entry, settings);
            ed.WriteMessage("\nRua " + (replacing ? "atualizada" : "adicionada") + ". Proxima rua, Exportar ou Sair.");
        }
    }

    private static bool? AskDirection(Editor ed, AcadDb.Curve curve, bool currentReversed)
    {
        bool reversed = currentReversed;
        AcGi.TransientManager manager = AcGi.TransientManager.CurrentTransientManager;
        List<AcadDb.Entity> markers = new();

        try
        {
            while (true)
            {
                ClearMarkers(manager, markers);

                Point3d start = reversed ? curve.EndPoint : curve.StartPoint;
                double size = Math.Max(HorizontalLength(curve) * 0.03, 0.5);

                AcadDb.Circle circle = new(new Point3d(start.X, start.Y, 0), Vector3d.ZAxis, size) { ColorIndex = 1 };
                manager.AddTransient(circle, AcGi.TransientDrawingMode.Highlight, 128, new IntegerCollection());
                markers.Add(circle);
                ed.UpdateScreen();

                PromptKeywordOptions options = new(
                    "\nInicio do estaqueamento marcado em vermelho. [Manter/Inverter] <Manter>: ",
                    "Manter Inverter")
                {
                    AllowNone = true
                };

                PromptResult result = ed.GetKeywords(options);
                if (result.Status == PromptStatus.Cancel || result.Status == PromptStatus.Error)
                    return null;

                if (result.Status == PromptStatus.OK && result.StringResult == "Inverter")
                {
                    reversed = !reversed;
                    continue;
                }

                return reversed;
            }
        }
        finally
        {
            ClearMarkers(manager, markers);
            ed.UpdateScreen();
        }
    }

    private static void ClearMarkers(AcGi.TransientManager manager, List<AcadDb.Entity> markers)
    {
        foreach (AcadDb.Entity marker in markers)
        {
            try
            {
                manager.EraseTransient(marker, new IntegerCollection());
            }
            catch
            {
                // Ja removido.
            }

            marker.Dispose();
        }

        markers.Clear();
    }

    private static bool AskName(Editor ed, StreetEntry entry)
    {
        string current = entry.Name;

        PromptEntityOptions options = new(
            "\nSelecione o TEXT/MTEXT com o NOME da rua ou [Digitar]" +
            (string.IsNullOrEmpty(current) ? "" : " <" + current + ">") + ": ");
        options.SetRejectMessage("\nSelecione um TEXT ou MTEXT.");
        options.AddAllowedClass(typeof(AcadDb.DBText), true);
        options.AddAllowedClass(typeof(AcadDb.MText), true);
        options.Keywords.Add("Digitar");
        options.AppendKeywordsToMessage = false;
        options.AllowNone = true;

        PromptEntityResult result = ed.GetEntity(options);

        if (result.Status == PromptStatus.OK)
        {
            using AcadDb.Transaction tr = result.ObjectId.Database.TransactionManager.StartOpenCloseTransaction();
            string text = ReadText(tr.GetObject(result.ObjectId, AcadDb.OpenMode.ForRead));
            if (text.Length > 0)
            {
                entry.Name = text;
                entry.NameTextHandle = result.ObjectId.Handle.ToString();
                ed.WriteMessage("\n  Nome: " + text);
                return true;
            }
        }

        if (result.Status == PromptStatus.Cancel)
            return false;

        if (result.Status == PromptStatus.None && current.Length > 0)
            return true;

        PromptStringOptions typed = new("\nNome da rua" + (current.Length > 0 ? " <" + current + ">" : "") + ": ")
        {
            AllowSpaces = true
        };

        PromptResult typedResult = ed.GetString(typed);
        if (typedResult.Status != PromptStatus.OK)
            return false;

        string name = typedResult.StringResult.Trim();
        if (name.Length == 0)
            name = current;

        if (name.Length == 0)
        {
            ed.WriteMessage("\nNome obrigatorio.");
            return false;
        }

        entry.Name = name;
        entry.NameTextHandle = string.Empty;
        return true;
    }

    private static bool AskWidth(Editor ed, AcadDb.Database db, StreetEntry entry, Settings settings)
    {
        AcadDb.TypedValue[] filter = { new((int)AcadDb.DxfCode.Start, "DIMENSION") };

        PromptSelectionOptions options = new()
        {
            MessageForAdding = "\nSelecione a(s) COTA(S) da largura da pista (somadas) ou Enter para digitar: "
        };

        PromptSelectionResult result = ed.GetSelection(options, new AcadDb.SelectionFilter(filter));

        if (result.Status == PromptStatus.Cancel)
            return false;

        if (result.Status == PromptStatus.OK && result.Value.Count > 0)
        {
            AcadDb.ObjectId[] ids = result.Value.GetObjectIds();
            entry.DimensionHandles = string.Join(";", ids.Select(id => id.Handle.ToString()));
            entry.WidthExpression = string.Empty;

            (string formula, double value) = WidthFromDimensions(db, entry.DimensionHandles);
            ed.WriteMessage("\n  Largura pelas cotas: " + formula + " = " + Format(value, "0.###") + " m");
            return true;
        }

        string current = entry.WidthExpression.Length > 0 ? entry.WidthExpression : settings.LastWidth;

        while (true)
        {
            PromptStringOptions typed = new(
                "\nLargura da pista em m (ex.: 6.4, 3.2*2 ou 3.5+3.5) <" + current + ">: ")
            {
                AllowSpaces = false
            };

            PromptResult typedResult = ed.GetString(typed);
            if (typedResult.Status != PromptStatus.OK)
                return false;

            string expression = NormalizeExpression(string.IsNullOrWhiteSpace(typedResult.StringResult) ? current : typedResult.StringResult);

            if (TryEvaluate(expression, out double value) && value > 0)
            {
                entry.WidthExpression = expression;
                entry.DimensionHandles = string.Empty;
                ed.WriteMessage("\n  Largura: " + expression + " = " + Format(value, "0.###") + " m");
                return true;
            }

            ed.WriteMessage("\nValor invalido. Use numeros e + - * / ( ).");
        }
    }

    private static double? AskStation(Editor ed, double current, double interval)
    {
        while (true)
        {
            PromptStringOptions options = new(
                "\nEstaca inicial (estaca+metros, ex.: 0+0 ou 12+5.30; numero simples = metros) <" +
                FormatStation(current, interval) + ">: ")
            {
                AllowSpaces = false
            };

            PromptResult result = ed.GetString(options);
            if (result.Status != PromptStatus.OK)
                return null;

            if (string.IsNullOrWhiteSpace(result.StringResult))
                return current;

            if (TryParseStation(result.StringResult, interval, out double station))
                return station;

            ed.WriteMessage("\nEstaca invalida.");
        }
    }

    // ------------------------------------------------------------------
    // Listar / Remover / Config
    // ------------------------------------------------------------------

    private static void ListEntries(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        Settings settings = LoadSettings(db);
        List<StreetEntry> entries = LoadEntries(db);

        if (entries.Count == 0)
        {
            ed.WriteMessage("\nNenhuma rua no quadro. Use a opcao Adicionar.");
            return;
        }

        ed.WriteMessage("\n  #  RUA | EST. INICIAL -> FINAL | EXTENSAO | LARGURA");
        int i = 1;
        foreach (StreetEntry entry in entries)
        {
            StreetGeometry? geometry = ComputeGeometry(db, entry, settings, out string error);
            if (geometry is null)
            {
                ed.WriteMessage($"\n  {i}. {entry.Name} | [AVISO] {error}");
            }
            else
            {
                ed.WriteMessage(
                    $"\n  {i}. {geometry.Name} | {FormatStation(geometry.StartStation, settings.StakeInterval)} -> " +
                    $"{FormatStation(geometry.EndStation, settings.StakeInterval)} | {Format(geometry.LengthM, "0.000")} m | " +
                    $"{Format(geometry.WidthM, "0.00")} m");
            }

            i++;
        }
    }

    private static void RemoveEntry(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        List<StreetEntry> entries = LoadEntries(db);

        if (entries.Count == 0)
        {
            ed.WriteMessage("\nNenhuma rua no quadro.");
            return;
        }

        ListEntries(doc);

        PromptEntityOptions options = new("\nSelecione o eixo da rua a remover ou [Numero]: ");
        options.AddAllowedClass(typeof(AcadDb.Curve), false);
        options.Keywords.Add("Numero");
        options.AppendKeywordsToMessage = false;

        PromptEntityResult result = ed.GetEntity(options);
        StreetEntry? target = null;

        if (result.Status == PromptStatus.OK)
        {
            string handle = result.ObjectId.Handle.ToString();
            target = entries.FirstOrDefault(e => e.CurveHandle == handle);
        }
        else if (result.Status == PromptStatus.Keyword)
        {
            PromptIntegerOptions number = new("\nNumero da rua na lista: ")
            {
                LowerLimit = 1,
                UpperLimit = entries.Count
            };

            PromptIntegerResult numberResult = ed.GetInteger(number);
            if (numberResult.Status == PromptStatus.OK)
                target = entries[numberResult.Value - 1];
        }
        else
        {
            return;
        }

        if (target is null)
        {
            ed.WriteMessage("\nEsse eixo nao esta no quadro.");
            return;
        }

        using (doc.LockDocument())
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            AcadDb.DBDictionary root = GetOrCreateRoot(tr, db);
            string key = RowPrefix + target.Id;
            if (root.Contains(key))
                tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite).Erase();
            tr.Commit();
        }

        ed.WriteMessage("\nRua removida do quadro: " + target.Name);
    }

    private static void Configure(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        Settings settings = LoadSettings(db);

        ed.WriteMessage(
            "\n  Intervalo de estacas: " + Format(settings.StakeInterval, "0.###") + " m" +
            "\n  Fuso UTM: " + (settings.ZoneOverride.Length == 0 ? "automatico (geolocalizacao/sistema do Civil 3D)" : settings.ZoneOverride) +
            "\n  Linha de total: " + (settings.IncludeTotals ? "sim" : "nao") +
            "\n  Bloco de assinatura: " + (settings.IncludeSignature ? "sim" : "nao") +
            (settings.Responsible.Length > 0 ? " (" + settings.Responsible + " - " + settings.Registration + ")" : ""));

        PromptKeywordOptions options = new(
            "\nAlterar [Intervalo/Fuso/Total/Assinatura] <sair>: ",
            "Intervalo Fuso Total Assinatura")
        {
            AllowNone = true
        };

        PromptResult result = ed.GetKeywords(options);
        if (result.Status != PromptStatus.OK)
            return;

        switch (result.StringResult)
        {
            case "Intervalo":
            {
                PromptDoubleOptions interval = new("\nIntervalo entre estacas em m <" + Format(settings.StakeInterval, "0.###") + ">: ")
                {
                    AllowNone = true,
                    AllowNegative = false,
                    AllowZero = false
                };

                PromptDoubleResult r = ed.GetDouble(interval);
                if (r.Status == PromptStatus.OK)
                    settings.StakeInterval = r.Value;
                break;
            }

            case "Fuso":
            {
                PromptStringOptions zone = new(
                    "\nFuso UTM fixo (ex.: 21S) ou Auto <" + (settings.ZoneOverride.Length == 0 ? "Auto" : settings.ZoneOverride) + ">: ");
                PromptResult r = ed.GetString(zone);
                if (r.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(r.StringResult))
                    break;

                if (r.StringResult.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase))
                    settings.ZoneOverride = string.Empty;
                else if (GeoConversion.TryParseZone(r.StringResult, out int z, out bool south))
                    settings.ZoneOverride = z.ToString(CultureInfo.InvariantCulture) + (south ? "S" : "N");
                else
                    ed.WriteMessage("\nFuso invalido.");
                break;
            }

            case "Total":
                settings.IncludeTotals = !settings.IncludeTotals;
                break;

            case "Assinatura":
                settings.IncludeSignature = !settings.IncludeSignature;
                break;
        }

        using (doc.LockDocument())
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            SaveSettings(tr, db, settings);
            tr.Commit();
        }

        ed.WriteMessage("\nConfiguracao salva no desenho.");
    }

    // ------------------------------------------------------------------
    // Exportar
    // ------------------------------------------------------------------

    private static void Export(Document doc)
    {
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        Settings settings = LoadSettings(db);
        List<StreetEntry> entries = LoadEntries(db);

        if (entries.Count == 0)
        {
            ed.WriteMessage("\nNenhuma rua no quadro. Use a opcao Adicionar.");
            return;
        }

        GeoResolver? geo = GeoResolver.Create(doc, settings.ZoneOverride, ed);
        if (geo is null)
            return;

        ed.WriteMessage("\nCoordenadas geograficas: " + geo.Description);

        List<StreetRow> rows = new();
        int skipped = 0;

        foreach (StreetEntry entry in entries)
        {
            StreetGeometry? g = ComputeGeometry(db, entry, settings, out string error);
            if (g is null)
            {
                skipped++;
                ed.WriteMessage("\n[AVISO] " + entry.Name + ": " + error + " Rua ignorada.");
                continue;
            }

            (double latS, double lonS) = geo.ToGeographic(g.StartPoint);
            (double latE, double lonE) = geo.ToGeographic(g.EndPoint);

            (int startStake, double startOffset) = SplitStation(g.StartStation, settings.StakeInterval);
            (int endStake, double endOffset) = SplitStation(g.EndStation, settings.StakeInterval);

            rows.Add(new StreetRow(
                g.Name,
                startStake, startOffset,
                endStake, endOffset,
                g.WidthFormula, g.WidthM,
                GeoConversion.ToDms(latS), latS < 0 ? "S" : "N",
                GeoConversion.ToDms(lonS), lonS < 0 ? "W" : "E",
                GeoConversion.ToDms(latE), latE < 0 ? "S" : "N",
                GeoConversion.ToDms(lonE), lonE < 0 ? "W" : "E"));
        }

        if (rows.Count == 0)
            return;

        StreetTableSignature? signature = null;
        if (settings.IncludeSignature)
        {
            if (!AskSignature(ed, settings))
                return;

            signature = new StreetTableSignature(settings.Responsible, settings.Registration, settings.Place, DateTime.Today);
        }

        string defaultPath = DefaultExportPath(db);
        PromptSaveFileOptions save = new("\nSalvar quadro de ruas")
        {
            Filter = "Planilha Excel (*.xlsx)|*.xlsx",
            InitialFileName = Path.GetFileName(defaultPath),
            InitialDirectory = Path.GetDirectoryName(defaultPath) ?? string.Empty,
            DialogCaption = "Salvar quadro de ruas"
        };

        PromptFileNameResult file = ed.GetFileNameForSave(save);
        if (file.Status != PromptStatus.OK)
            return;

        string path = file.StringResult;
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            path += ".xlsx";

        StreetTableXlsx.Write(path, rows, settings.StakeInterval, settings.IncludeTotals, signature);

        using (doc.LockDocument())
        using (AcadDb.Transaction tr = db.TransactionManager.StartTransaction())
        {
            SaveSettings(tr, db, settings);
            tr.Commit();
        }

        ed.WriteMessage(
            "\nQuadro exportado: " + rows.Count + " rua(s)" + (skipped > 0 ? " | ignoradas: " + skipped : "") +
            "\n  " + path);

        PromptKeywordOptions open = new("\nAbrir a planilha? [Sim/Nao] <Sim>: ", "Sim Nao") { AllowNone = true };
        PromptResult openResult = ed.GetKeywords(open);
        if (openResult.Status == PromptStatus.None ||
            (openResult.Status == PromptStatus.OK && openResult.StringResult == "Sim"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nNao foi possivel abrir automaticamente: " + ex.Message);
            }
        }
    }

    private static bool AskSignature(Editor ed, Settings settings)
    {
        string? responsible = AskText(ed, "Responsavel tecnico (nome)", settings.Responsible);
        if (responsible is null)
            return false;

        string? registration = AskText(ed, "Registro (ex.: CREA-MT 000000)", settings.Registration);
        if (registration is null)
            return false;

        string? place = AskText(ed, "Local (ex.: Cuiaba-MT)", settings.Place);
        if (place is null)
            return false;

        settings.Responsible = responsible;
        settings.Registration = registration;
        settings.Place = place;
        return true;
    }

    private static string? AskText(Editor ed, string label, string current)
    {
        PromptStringOptions options = new("\n" + label + (current.Length > 0 ? " <" + current + ">" : "") + ": ")
        {
            AllowSpaces = true
        };

        PromptResult result = ed.GetString(options);
        if (result.Status != PromptStatus.OK)
            return null;

        return string.IsNullOrWhiteSpace(result.StringResult) ? current : result.StringResult.Trim();
    }

    private static string DefaultExportPath(AcadDb.Database db)
    {
        try
        {
            string dwg = db.Filename;
            if (!string.IsNullOrWhiteSpace(dwg) && File.Exists(dwg))
            {
                return Path.Combine(
                    Path.GetDirectoryName(dwg) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.GetFileNameWithoutExtension(dwg) + "_QUADRO_RUAS.xlsx");
            }
        }
        catch
        {
            // Usa Documentos.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QUADRO_RUAS.xlsx");
    }

    private static void ReportEntry(Document doc, StreetEntry entry, Settings settings)
    {
        StreetGeometry? g = ComputeGeometry(doc.Database, entry, settings, out string error);
        if (g is null)
        {
            doc.Editor.WriteMessage("\n[AVISO] " + error);
            return;
        }

        doc.Editor.WriteMessage(
            "\n  " + g.Name + ": " + FormatStation(g.StartStation, settings.StakeInterval) + " -> " +
            FormatStation(g.EndStation, settings.StakeInterval) + " | extensao " + Format(g.LengthM, "0.000") +
            " m | largura " + Format(g.WidthM, "0.00") + " m | area " + Format(g.LengthM * g.WidthM, "0.00") + " m2");
    }

    // ------------------------------------------------------------------
    // Geometria
    // ------------------------------------------------------------------

    private static StreetGeometry? ComputeGeometry(AcadDb.Database db, StreetEntry entry, Settings settings, out string error)
    {
        error = string.Empty;
        double f = MetersPerUnit(db);

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

            AcadDb.ObjectId curveId = ResolveHandle(db, entry.CurveHandle);
            if (curveId.IsNull || curveId.IsErased || tr.GetObject(curveId, AcadDb.OpenMode.ForRead, false) is not AcadDb.Curve curve)
            {
                error = "o eixo da rua foi apagado.";
                return null;
            }

            double lengthM;
            double startStation;
            Point3d start;
            Point3d end;

            if (curve is CivilAlignment alignment)
            {
                startStation = alignment.StartingStation * f;
                lengthM = (alignment.EndingStation - alignment.StartingStation) * f;
                start = alignment.StartPoint;
                end = alignment.EndPoint;
            }
            else
            {
                lengthM = HorizontalLength(curve) * f;
                startStation = entry.StartStation;
                start = entry.Reversed ? curve.EndPoint : curve.StartPoint;
                end = entry.Reversed ? curve.StartPoint : curve.EndPoint;
            }

            string name = entry.Name;
            AcadDb.ObjectId textId = ResolveHandle(db, entry.NameTextHandle);
            if (!textId.IsNull && !textId.IsErased)
            {
                string current = ReadText(tr.GetObject(textId, AcadDb.OpenMode.ForRead, false));
                if (current.Length > 0)
                    name = current;
            }

            string formula;
            double width;

            if (entry.DimensionHandles.Length > 0)
            {
                (formula, width) = WidthFromDimensions(db, entry.DimensionHandles);
                if (width <= 0 && entry.WidthExpression.Length > 0)
                    (formula, width) = WidthFromExpression(entry.WidthExpression);
            }
            else
            {
                (formula, width) = WidthFromExpression(entry.WidthExpression);
            }

            return new StreetGeometry
            {
                Name = name,
                LengthM = lengthM,
                StartStation = startStation,
                EndStation = startStation + lengthM,
                WidthFormula = formula,
                WidthM = width,
                StartPoint = start,
                EndPoint = end
            };
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Comprimento em planta (projecao no plano XY).</summary>
    private static double HorizontalLength(AcadDb.Curve curve)
    {
        try
        {
            if (curve is AcadDb.Polyline polyline && polyline.Normal.IsParallelTo(Vector3d.ZAxis))
                return polyline.Length;

            using AcadDb.Curve projected = curve.GetOrthoProjectedCurve(new Plane(Point3d.Origin, Vector3d.ZAxis));
            return projected.GetDistanceAtParameter(projected.EndParam) - projected.GetDistanceAtParameter(projected.StartParam);
        }
        catch
        {
            // Segue para a amostragem.
        }

        if (curve is CivilFeatureLine featureLine)
        {
            Point3dCollection points = featureLine.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints);
            double sum = 0;
            for (int i = 1; i < points.Count; i++)
                sum += Distance2d(points[i - 1], points[i]);
            return sum;
        }

        const int samples = 2000;
        double total = 0;
        Point3d previous = curve.StartPoint;
        for (int i = 1; i <= samples; i++)
        {
            double parameter = curve.StartParam + ((curve.EndParam - curve.StartParam) * i / samples);
            Point3d current = curve.GetPointAtParameter(parameter);
            total += Distance2d(previous, current);
            previous = current;
        }

        return total;
    }

    private static double Distance2d(Point3d a, Point3d b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static (string Formula, double Value) WidthFromDimensions(AcadDb.Database db, string handles)
    {
        double f = MetersPerUnit(db);
        List<double> values = new();

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (string handle in handles.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            AcadDb.ObjectId id = ResolveHandle(db, handle);
            if (!id.IsNull && !id.IsErased && tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.Dimension dimension)
                values.Add(Math.Round(dimension.Measurement * f, 3));
        }

        if (values.Count == 0)
            return (string.Empty, 0.0);

        if (values.Count == 1)
            return (string.Empty, values[0]);

        return ("=" + string.Join("+", values.Select(v => Format(v, "0.###"))), values.Sum());
    }

    private static (string Formula, double Value) WidthFromExpression(string expression)
    {
        if (!TryEvaluate(expression, out double value))
            return (string.Empty, 0.0);

        bool isPlainNumber = Regex.IsMatch(expression, @"^\d+(\.\d+)?$");
        return (isPlainNumber ? string.Empty : "=" + expression, value);
    }

    private static string NormalizeExpression(string text) =>
        text.Trim().TrimStart('=').Replace(',', '.').Replace(" ", string.Empty);

    private static bool TryEvaluate(string expression, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression) || !Regex.IsMatch(expression, @"^[0-9\.\+\-\*/\(\)]+$"))
            return false;

        try
        {
            using DataTable table = new();
            object result = table.Compute(expression, string.Empty);
            value = Convert.ToDouble(result, CultureInfo.InvariantCulture);
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadText(AcadDb.DBObject obj) => obj switch
    {
        AcadDb.MText mtext => CleanText(mtext.Text),
        AcadDb.DBText text => CleanText(text.TextString),
        _ => string.Empty
    };

    private static string CleanText(string text) =>
        Regex.Replace(text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " "), @"\s{2,}", " ").Trim();

    // ------------------------------------------------------------------
    // Estacas
    // ------------------------------------------------------------------

    private static (int Stake, double Offset) SplitStation(double station, double interval)
    {
        double rounded = Math.Round(station, 3);
        int stake = (int)Math.Floor((rounded / interval) + 1e-9);
        double offset = Math.Round(rounded - (stake * interval), 3);

        if (offset >= interval - 1e-9)
        {
            stake++;
            offset = 0;
        }

        return (stake, Math.Max(0.0, offset));
    }

    private static string FormatStation(double station, double interval)
    {
        (int stake, double offset) = SplitStation(station, interval);
        return stake.ToString(CultureInfo.InvariantCulture) + "+" + Format(offset, "0.000");
    }

    private static bool TryParseStation(string text, double interval, out double station)
    {
        station = 0;
        string t = text.Trim().Replace(',', '.');

        Match match = Regex.Match(t, @"^(\d+)\s*\+\s*(\d+(\.\d+)?)$");
        if (match.Success)
        {
            station = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * interval) +
                      double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out station) && station >= 0;
    }

    // ------------------------------------------------------------------
    // Coordenadas geograficas
    // ------------------------------------------------------------------

    private sealed class GeoResolver
    {
        private readonly Func<Point3d, (double Lat, double Lon)> _convert;

        private GeoResolver(string description, Func<Point3d, (double Lat, double Lon)> convert)
        {
            Description = description;
            _convert = convert;
        }

        public string Description { get; }

        public (double Lat, double Lon) ToGeographic(Point3d point) => _convert(point);

        public static GeoResolver? Create(Document doc, string zoneOverride, Editor ed)
        {
            AcadDb.Database db = doc.Database;
            double f = MetersPerUnit(db);

            // 1) Fuso fixo configurado.
            if (GeoConversion.TryParseZone(zoneOverride, out int zone, out bool south))
                return Utm(zone, south, f, "UTM fuso " + zone + (south ? "S" : "N") + " (configurado), SIRGAS 2000");

            // 2) Geolocalizacao do desenho.
            GeoResolver? fromGeoLocation = TryGeoLocation(db);
            if (fromGeoLocation is not null)
                return fromGeoLocation;

            // 3) Sistema de coordenadas do Civil 3D.
            string code = string.Empty;
            try
            {
                code = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument
                    .Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? string.Empty;
            }
            catch
            {
                // Sem sistema configurado.
            }

            if (GeoConversion.TryParseZone(code, out zone, out south))
                return Utm(zone, south, f, "UTM fuso " + zone + (south ? "S" : "N") + " (sistema do Civil 3D: " + code + "), GRS80");

            // 4) Perguntar.
            ed.WriteMessage(
                "\nO desenho nao tem geolocalizacao nem sistema de coordenadas UTM definido." +
                "\nAs coordenadas do desenho serao tratadas como UTM SIRGAS 2000 (E, N em metros).");

            PromptStringOptions options = new("\nFuso UTM (ex.: 21S para Cuiaba) <21S>: ") { AllowSpaces = false };
            PromptResult result = ed.GetString(options);
            if (result.Status != PromptStatus.OK)
                return null;

            string text = string.IsNullOrWhiteSpace(result.StringResult) ? "21S" : result.StringResult;
            if (!GeoConversion.TryParseZone(text, out zone, out south))
            {
                ed.WriteMessage("\nFuso invalido.");
                return null;
            }

            return Utm(zone, south, f, "UTM fuso " + zone + (south ? "S" : "N") + " (informado), SIRGAS 2000");
        }

        private static GeoResolver Utm(int zone, bool south, double metersPerUnit, string description) =>
            new(description, p => GeoConversion.UtmToGeographic(p.X * metersPerUnit, p.Y * metersPerUnit, zone, south));

        private static GeoResolver? TryGeoLocation(AcadDb.Database db)
        {
            try
            {
                AcadDb.ObjectId geoId = db.GeoDataObject;
                if (geoId.IsNull)
                    return null;

                using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
                if (tr.GetObject(geoId, AcadDb.OpenMode.ForRead, false) is not AcadDb.GeoLocationData geo)
                    return null;

                string system = geo.CoordinateSystem;
                if (string.IsNullOrWhiteSpace(system))
                    return null;

                // Teste rapido: a conversao precisa funcionar.
                geo.TransformToLonLatAlt(geo.DesignPoint);

                AcadDb.ObjectId id = geoId;
                return new GeoResolver(
                    "geolocalizacao do desenho (" + ShortSystem(system) + ")",
                    p =>
                    {
                        using AcadDb.Transaction t = db.TransactionManager.StartOpenCloseTransaction();
                        AcadDb.GeoLocationData g = (AcadDb.GeoLocationData)t.GetObject(id, AcadDb.OpenMode.ForRead, false);
                        Point3d lla = g.TransformToLonLatAlt(p);
                        return (lla.Y, lla.X);
                    });
            }
            catch
            {
                return null;
            }
        }

        private static string ShortSystem(string system)
        {
            Match match = Regex.Match(system, "\\b(?:CSCode|Name)\\s*=\\s*\"?([^\"\\s,]+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;

            return system.Length > 40 ? system[..40] + "..." : system;
        }
    }

    private static double MetersPerUnit(AcadDb.Database db) => db.Insunits switch
    {
        AcadDb.UnitsValue.Millimeters => 0.001,
        AcadDb.UnitsValue.Centimeters => 0.01,
        AcadDb.UnitsValue.Kilometers => 1000.0,
        AcadDb.UnitsValue.Inches => 0.0254,
        AcadDb.UnitsValue.Feet => 0.3048,
        _ => 1.0
    };

    // ------------------------------------------------------------------
    // Persistencia (NOD)
    // ------------------------------------------------------------------

    private static Settings LoadSettings(AcadDb.Database db)
    {
        Settings settings = new();

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryGetRoot(tr, db);
        if (root is null || !root.Contains(SettingsKey))
            return settings;

        AcadDb.TypedValue[]? v = ((AcadDb.Xrecord)tr.GetObject(root.GetAt(SettingsKey), AcadDb.OpenMode.ForRead)).Data?.AsArray();
        if (v is null || v.Length < 9 || v[0].Value as string != SettingsSignature)
            return settings;

        settings.StakeInterval = v[1].Value is double interval && interval > 0 ? interval : 20.0;
        settings.ZoneOverride = v[2].Value as string ?? string.Empty;
        settings.Responsible = v[3].Value as string ?? string.Empty;
        settings.Registration = v[4].Value as string ?? string.Empty;
        settings.Place = v[5].Value as string ?? string.Empty;
        settings.LastWidth = v[6].Value as string ?? "7";
        settings.IncludeTotals = v[7].Value is int totals && totals != 0;
        settings.IncludeSignature = v[8].Value is int signature && signature != 0;
        return settings;
    }

    private static void SaveSettings(AcadDb.Transaction tr, AcadDb.Database db, Settings s)
    {
        AcadDb.ResultBuffer buffer = new(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, SettingsSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, s.StakeInterval),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.ZoneOverride),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.Responsible),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.Registration),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.Place),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, s.LastWidth),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, s.IncludeTotals ? 1 : 0),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, s.IncludeSignature ? 1 : 0));

        WriteRecord(tr, db, SettingsKey, buffer);
    }

    private static List<StreetEntry> LoadEntries(AcadDb.Database db)
    {
        List<StreetEntry> entries = new();

        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        AcadDb.DBDictionary? root = TryGetRoot(tr, db);
        if (root is null)
            return entries;

        foreach (AcadDb.DBDictionaryEntry item in root)
        {
            if (!item.Key.StartsWith(RowPrefix, StringComparison.Ordinal))
                continue;

            AcadDb.TypedValue[]? v = (tr.GetObject(item.Value, AcadDb.OpenMode.ForRead) as AcadDb.Xrecord)?.Data?.AsArray();
            if (v is null || v.Length < 10 || v[0].Value as string != RowSignature)
                continue;

            entries.Add(new StreetEntry
            {
                Id = v[1].Value as string ?? string.Empty,
                Order = long.TryParse(v[2].Value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out long order) ? order : 0,
                CurveHandle = v[3].Value as string ?? string.Empty,
                Reversed = v[4].Value is int reversed && reversed != 0,
                Name = v[5].Value as string ?? string.Empty,
                NameTextHandle = v[6].Value as string ?? string.Empty,
                WidthExpression = v[7].Value as string ?? string.Empty,
                DimensionHandles = v[8].Value as string ?? string.Empty,
                StartStation = v[9].Value is double station ? station : 0.0
            });
        }

        return entries.OrderBy(e => e.Order).ToList();
    }

    private static void SaveEntry(AcadDb.Transaction tr, AcadDb.Database db, StreetEntry e)
    {
        AcadDb.ResultBuffer buffer = new(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, RowSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.Id),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.Order.ToString(CultureInfo.InvariantCulture)),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.CurveHandle),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, e.Reversed ? 1 : 0),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.Name),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.NameTextHandle),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.WidthExpression),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, e.DimensionHandles),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, e.StartStation));

        WriteRecord(tr, db, RowPrefix + e.Id, buffer);
    }

    private static void WriteRecord(AcadDb.Transaction tr, AcadDb.Database db, string key, AcadDb.ResultBuffer buffer)
    {
        AcadDb.DBDictionary root = GetOrCreateRoot(tr, db);

        if (root.Contains(key))
        {
            ((AcadDb.Xrecord)tr.GetObject(root.GetAt(key), AcadDb.OpenMode.ForWrite)).Data = buffer;
            return;
        }

        AcadDb.Xrecord record = new() { Data = buffer };
        root.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static AcadDb.DBDictionary? TryGetRoot(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        return nod.Contains(RootDictionaryName)
            ? (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForRead)
            : null;
    }

    private static AcadDb.DBDictionary GetOrCreateRoot(AcadDb.Transaction tr, AcadDb.Database db)
    {
        AcadDb.DBDictionary nod = (AcadDb.DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, AcadDb.OpenMode.ForRead);
        if (nod.Contains(RootDictionaryName))
            return (AcadDb.DBDictionary)tr.GetObject(nod.GetAt(RootDictionaryName), AcadDb.OpenMode.ForWrite);

        nod.UpgradeOpen();
        AcadDb.DBDictionary root = new();
        nod.SetAt(RootDictionaryName, root);
        tr.AddNewlyCreatedDBObject(root, true);
        return root;
    }

    private static AcadDb.ObjectId ResolveHandle(AcadDb.Database db, string handleText)
    {
        if (string.IsNullOrWhiteSpace(handleText))
            return AcadDb.ObjectId.Null;

        try
        {
            return db.GetObjectId(false, new AcadDb.Handle(Convert.ToInt64(handleText, 16)), 0);
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    private static string Format(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
