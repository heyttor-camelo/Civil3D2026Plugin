using System;
using System.IO;
using System.Text.Json;

namespace Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo;

internal sealed class MFRebaixoSettings
{
    public string UltimoPreset { get; set; } = "Garagem";

    // "Reducao" ou "AlturaFinal".
    // Mantém compatibilidade com config.json antigos: se a propriedade
    // não existir no JSON, o padrão abaixo permanece ativo.
    public string UltimoModoValor { get; set; } = "Reducao";

    // Último valor efetivamente usado no lançamento.
    // Mantém o default entre pares e entre execuções.
    // JSON antigo continua compatível porque os defaults abaixo são usados
    // quando essas propriedades ainda não existem no arquivo.
    public double UltimaReducao { get; set; } = 0.18;
    public double UltimaAlturaFinal { get; set; } = 0.12;

    public MFRebaixoPreset Garagem { get; set; } = new()
    {
        Nome = "Garagem",
        Comment = "REBAIXO GARAGEM",
        AlturaNormal = 0.30,
        AlturaRebaixada = 0.15,
        ExtensaoTransicao = 0.01,
        ExtensaoRebaixo = 3.00
    };

    public MFRebaixoPreset Pedestre { get; set; } = new()
    {
        Nome = "Pedestre",
        Comment = "REBAIXO PEDESTRE",
        AlturaNormal = 0.30,
        AlturaRebaixada = 0.12,
        ExtensaoTransicao = 1.80,
        ExtensaoRebaixo = 1.50
    };

    public MFRebaixoPreset GetPreset(string nome) =>
        nome.Equals("Pedestre", StringComparison.OrdinalIgnoreCase)
            ? Pedestre
            : Garagem;
}

internal sealed class MFRebaixoPreset
{
    public string Nome { get; set; } = "";
    public string Comment { get; set; } = "";
    public double AlturaNormal { get; set; }
    public double AlturaRebaixada { get; set; }
    public double ExtensaoTransicao { get; set; }
    public double ExtensaoRebaixo { get; set; }

    public double ExtensaoTotal =>
        (2.0 * ExtensaoTransicao) + ExtensaoRebaixo;
}

internal static class MFRebaixoSettingsStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Civil3D2026Plugin",
        "MFRebaixo");

    private static readonly string FilePath =
        Path.Combine(Folder, "config.json");

    public static MFRebaixoSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new MFRebaixoSettings();

            string json = File.ReadAllText(FilePath);

            return JsonSerializer.Deserialize<MFRebaixoSettings>(json)
                ?? new MFRebaixoSettings();
        }
        catch
        {
            return new MFRebaixoSettings();
        }
    }

    public static void Save(MFRebaixoSettings settings)
    {
        Directory.CreateDirectory(Folder);

        string json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(FilePath, json);
    }
}
