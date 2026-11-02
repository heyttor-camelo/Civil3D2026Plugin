using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// Retrato da entrada do motor (eixos, secoes, eventos, opcoes) num JSON, para refazer o
/// calculo da rede e da hachura fora do Civil (tests/Viario.Engine.Tests) quando algo sai
/// errado num desenho do Heyvid. Sem referencias ao AutoCAD: entra no harness por link.
/// </summary>
internal static class ViarioDump
{
    public const int Version = 1;

    public sealed class Content
    {
        public string Drawing = "";
        public double TileSize;
        public List<AxisInput> Axes = new();
        public EngineSettings Settings = new();
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string Serialize(string drawing, IReadOnlyList<AxisInput> axes, EngineSettings settings, double tileSize)
    {
        JsonObject root = new()
        {
            ["version"] = Version,
            ["drawing"] = drawing,
            ["tile"] = tileSize,
            ["settings"] = new JsonObject
            {
                ["snap"] = settings.SnapTolerance,
                ["straightDeg"] = settings.StraightToleranceDeg,
                ["medianSetback"] = settings.MedianSetback,
                ["endCapture"] = settings.EndCapture,
                ["noseRadius"] = settings.NoseRadius,
                ["cornerRadius"] = new JsonObject(settings.CornerRadius.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
                ["slipLanes"] = new JsonObject(settings.SlipLanes.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value.Serialize()))),
                ["removedIslands"] = new JsonArray(settings.RemovedIslands.Select(p => (JsonNode)new JsonArray(p.X, p.Y)).ToArray()),
                ["islandRadii"] = new JsonArray(settings.IslandRadii.Select(t => (JsonNode)new JsonArray(t.Seed.X, t.Seed.Y, t.Radius)).ToArray())
            }
        };

        JsonArray list = new();
        foreach (AxisInput a in axes)
        {
            RoadSection s = a.Section;
            JsonArray segs = new();
            foreach (Seg sg in a.Path.Segs)
                segs.Add(new JsonArray(sg.A.X, sg.A.Y, sg.B.X, sg.B.Y, sg.Bulge));
            list.Add(new JsonObject
            {
                ["key"] = a.Key,
                ["chained"] = a.Chained,
                ["radius"] = a.Radius is double r ? r : null,
                ["events"] = new JsonArray(a.Events.Select(e => (JsonNode)e.Serialize()).ToArray()),
                ["section"] = new JsonObject
                {
                    ["id"] = s.Id, ["name"] = s.Name, ["pista"] = s.Pista, ["canteiro"] = s.Canteiro, ["sarjeta"] = s.Sarjeta,
                    ["meioFio"] = s.MeioFio, ["calcada"] = s.Calcada, ["raio"] = s.Raio, ["tipo"] = s.Tipo, ["faixas"] = s.Faixas,
                    ["velocidade"] = s.Velocidade, ["ladosSarjeta"] = (int)s.LadosSarjeta, ["ladosMeioFio"] = (int)s.LadosMeioFio,
                    ["ladosCalcada"] = (int)s.LadosCalcada
                },
                ["segs"] = segs
            });
        }
        root["axes"] = list;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public static Content Deserialize(string json)
    {
        JsonObject root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("JSON invalido.");
        Content c = new()
        {
            Drawing = root["drawing"]?.GetValue<string>() ?? "",
            TileSize = root["tile"]?.GetValue<double>() ?? 0.0
        };

        if (root["settings"] is JsonObject st)
        {
            c.Settings.SnapTolerance = st["snap"]?.GetValue<double>() ?? c.Settings.SnapTolerance;
            c.Settings.StraightToleranceDeg = st["straightDeg"]?.GetValue<double>() ?? c.Settings.StraightToleranceDeg;
            c.Settings.MedianSetback = st["medianSetback"]?.GetValue<double>() ?? c.Settings.MedianSetback;
            c.Settings.EndCapture = st["endCapture"]?.GetValue<double>() ?? c.Settings.EndCapture;
            c.Settings.NoseRadius = st["noseRadius"]?.GetValue<double>() ?? c.Settings.NoseRadius;
            if (st["cornerRadius"] is JsonObject cr)
                foreach ((string k, JsonNode? v) in cr)
                    if (v is not null)
                        c.Settings.CornerRadius[k] = v.GetValue<double>();
            if (st["slipLanes"] is JsonObject sl)
                foreach ((string k, JsonNode? v) in sl)
                    if (v is not null && SlipLane.Parse(v.GetValue<string>()) is SlipLane lane)
                        c.Settings.SlipLanes[k] = lane;
            if (st["removedIslands"] is JsonArray ri)
                foreach (JsonNode? p in ri)
                    if (p is JsonArray xy && xy.Count >= 2)
                        c.Settings.RemovedIslands.Add(new V2(xy[0]!.GetValue<double>(), xy[1]!.GetValue<double>()));
            if (st["islandRadii"] is JsonArray ir)
                foreach (JsonNode? p in ir)
                    if (p is JsonArray xyr && xyr.Count >= 3)
                        c.Settings.IslandRadii.Add((new V2(xyr[0]!.GetValue<double>(), xyr[1]!.GetValue<double>()), xyr[2]!.GetValue<double>()));
        }

        if (root["axes"] is JsonArray axes)
        {
            foreach (JsonNode? n in axes)
            {
                if (n is not JsonObject a)
                    continue;
                List<Seg> segs = new();
                if (a["segs"] is JsonArray sa)
                    foreach (JsonNode? sn in sa)
                        if (sn is JsonArray v && v.Count >= 5)
                            segs.Add(new Seg(new V2(v[0]!.GetValue<double>(), v[1]!.GetValue<double>()),
                                             new V2(v[2]!.GetValue<double>(), v[3]!.GetValue<double>()), v[4]!.GetValue<double>()));
                if (segs.Count == 0)
                    continue;

                RoadSection s = new();
                if (a["section"] is JsonObject so)
                {
                    s.Id = so["id"]?.GetValue<string>() ?? s.Id;
                    s.Name = so["name"]?.GetValue<string>() ?? s.Name;
                    s.Pista = so["pista"]?.GetValue<double>() ?? s.Pista;
                    s.Canteiro = so["canteiro"]?.GetValue<double>() ?? s.Canteiro;
                    s.Sarjeta = so["sarjeta"]?.GetValue<double>() ?? s.Sarjeta;
                    s.MeioFio = so["meioFio"]?.GetValue<double>() ?? s.MeioFio;
                    s.Calcada = so["calcada"]?.GetValue<double>() ?? s.Calcada;
                    s.Raio = so["raio"]?.GetValue<double>() ?? s.Raio;
                    s.Tipo = so["tipo"]?.GetValue<string>() ?? s.Tipo;
                    s.Faixas = so["faixas"]?.GetValue<int>() ?? s.Faixas;
                    s.Velocidade = so["velocidade"]?.GetValue<double>() ?? s.Velocidade;
                    s.LadosSarjeta = (Lados)(so["ladosSarjeta"]?.GetValue<int>() ?? (int)s.LadosSarjeta);
                    s.LadosMeioFio = (Lados)(so["ladosMeioFio"]?.GetValue<int>() ?? (int)s.LadosMeioFio);
                    s.LadosCalcada = (Lados)(so["ladosCalcada"]?.GetValue<int>() ?? (int)s.LadosCalcada);
                }

                List<AxisEvent> events = new();
                if (a["events"] is JsonArray ea)
                    foreach (JsonNode? e in ea)
                        if (e is not null && AxisEvent.Parse(e.GetValue<string>()) is AxisEvent ev)
                            events.Add(ev);

                double? radius = a["radius"] is JsonValue rv && rv.TryGetValue(out double rd) ? rd : null;
                c.Axes.Add(new AxisInput(a["key"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), new GPath(segs), s, radius, events,
                                         a["chained"]?.GetValue<bool>() ?? false));
            }
        }

        return c;
    }

    /// <summary>
    /// Grava o retrato em %LOCALAPPDATA%\Civil3D2026Plugin\diag e, se existir a pasta do plugin
    /// no Google Drive, tambem em G:\Meu Drive\Civil3D2026Plugin\diag (acessivel de qualquer PC).
    /// Devolve os caminhos gravados; nunca lanca.
    /// </summary>
    public static List<string> TryWrite(string drawingPath, IReadOnlyList<AxisInput> axes, EngineSettings settings, double tileSize)
    {
        List<string> written = new();
        string json;
        try
        {
            json = Serialize(Path.GetFileName(drawingPath), axes, settings, tileSize);
        }
        catch
        {
            return written;
        }

        string name = "viario-" + Sanitize(Path.GetFileNameWithoutExtension(drawingPath)) + ".json";
        foreach (string dir in Folders())
        {
            try
            {
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, name);
                File.WriteAllText(file, json);
                written.Add(file);
            }
            catch
            {
                // Pasta sem acesso: segue.
            }
        }

        return written;
    }

    private static IEnumerable<string> Folders()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "Civil3D2026Plugin", "diag");
        const string drive = @"G:\Meu Drive\Civil3D2026Plugin";
        if (Directory.Exists(drive))
            yield return Path.Combine(drive, "diag");
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "desenho" : s;
    }
}
