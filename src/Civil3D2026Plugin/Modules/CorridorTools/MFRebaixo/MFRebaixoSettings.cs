using System;
using System.Collections.Generic;
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

    // Painel (MFREBAIXOPAINEL).
    public double PainelMargem { get; set; } = 6.0;
    // "GERAL" (todos os rebaixos) ou "PLANTA" (trecho do rebaixo).
    public string PainelModo { get; set; } = "GERAL";
    // Textos da vista grafica que o usuario desligou (chaves de MFRebaixoPanel.TextOptions).
    public List<string> PainelTextosOcultos { get; set; } = new() { "ESTACAS", "BASELINE" };

    // Hachura do rebaixo em planta, desenhada pelo plugin (v1.8). JSON antigo: ficam os padroes.
    public bool HachuraAtiva { get; set; } = true;
    public string HachuraLayer { get; set; } = MFRebaixoHatch.DefaultLayer;
    public short HachuraCor { get; set; } = 1; // vermelho
    public string HachuraPadraoRampa { get; set; } = "SOLID";
    public string HachuraPadraoRebaixo { get; set; } = "SOLID";
    public double HachuraEscala { get; set; } = 0.5;
    public int HachuraTransparencia { get; set; } = 20;
    // true: da borda da pista ate a borda externa da calcada (se houver); false: so sarjeta + meio-fio.
    public bool HachuraAteCalcada { get; set; }
    // true: a hachura vai para a frente na ordem de desenho (por cima do corredor).
    public bool HachuraNaFrente { get; set; } = true;
    // true: a layer da hachura fica trancada (ninguem move/apaga a hachura sem querer). Pedido do Heyvid em 10/11/2026.
    public bool HachuraTrancada { get; set; } = true;
    // Avisos (NBR 9050 etc.) no painel. Desligados por padrao a pedido do Heyvid em 10/11/2026.
    public bool AvisosAtivos { get; set; }
    // Versao dos padroes da hachura. 2 (MFREBAIXO 1.9, pedido do Heyvid em 10/11/2026): vermelho solido,
    // 20% de transparencia, so sarjeta + meio-fio. config.json mais antigo e trazido para esses padroes uma vez.
    public int HachuraVersao { get; set; }

    public const int HachuraVersaoAtual = 2;

    // Travessia entre rebaixos (MFTRAVESSIA, MFREBAIXO 1.14). Padroes do detalhe de lombofaixa e da
    // planta GPAA que o Heyvid mandou em 11/11/2026: plataforma 5 m, rampas 1,5 m, semi-elevada a 12 cm,
    // sarjeta livre com tampa de concreto por cima.
    public int TravessiaTipo { get; set; }
    public double TravessiaAltura { get; set; } = 0.12;
    public double TravessiaPlataforma { get; set; } = 5.00;
    public double TravessiaRampa { get; set; } = 1.50;
    public bool TravessiaTampa { get; set; } = true;
    public double TravessiaTampaEspessura { get; set; } = 0.10;
    public bool TravessiaCentralizar { get; set; } = true;
    public string TravessiaLayer { get; set; } = "MF-TRAVESSIA";
    public double TravessiaListra { get; set; } = 0.40;
    public double TravessiaListraEspaco { get; set; } = 0.40;

    public void MigrarHachura()
    {
        if (HachuraVersao >= HachuraVersaoAtual)
            return;
        HachuraCor = 1;
        HachuraPadraoRampa = "SOLID";
        HachuraPadraoRebaixo = "SOLID";
        HachuraTransparencia = 20;
        HachuraAteCalcada = false;
        HachuraVersao = HachuraVersaoAtual;
    }

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
                return Novo();

            string json = File.ReadAllText(FilePath);

            MFRebaixoSettings lido = JsonSerializer.Deserialize<MFRebaixoSettings>(json)
                ?? new MFRebaixoSettings();

            lido.MigrarHachura();

            return lido;
        }
        catch
        {
            return Novo();
        }
    }

    private static MFRebaixoSettings Novo() =>
        new() { HachuraVersao = MFRebaixoSettings.HachuraVersaoAtual };

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
