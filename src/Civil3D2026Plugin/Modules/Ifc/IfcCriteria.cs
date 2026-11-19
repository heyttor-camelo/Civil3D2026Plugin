using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>Uma linha da planilha de mapeamento da extensao IFC (por layer, estilo de shape, de link...).</summary>
internal sealed class IfcMapRow
{
    /// <summary>LayerName, ShapeStyleName, LinkStyleName, AlignmentStyleName, SurfaceStyleName...</summary>
    public string Mapeamento { get; set; } = "LayerName";

    public string Nome { get; set; } = "";

    public bool Exportar { get; set; } = true;

    /// <summary>Classe IFC 4.3 (ex. IfcKerb, IfcPavement.RIGID); vazio = deixa a extensao decidir.</summary>
    public string ClasseIfc { get; set; } = "";

    public string Observacao { get; set; } = "";
}

/// <summary>
/// Criterios da exportacao IFC (C3DIFC), em JSON legivel: um por projeto, ao lado do DWG
/// ("&lt;desenho&gt;-C3DIFC.json"), criado a partir do padrao em %APPDATA%\Civil3D2026Plugin\Ifc.
/// </summary>
internal sealed class IfcCriteria
{
    public string Leiame { get; set; } =
        "Criterios do C3DIFC (exportacao IFC 4.3). Edite e salve; o comando le de novo a cada exportacao. " +
        "Mapeamento: Mapeamento = LayerName / ShapeStyleName / LinkStyleName / AlignmentStyleName / SurfaceStyleName; " +
        "Nome = nome da layer ou do estilo (maiusculas/minusculas tanto faz); Exportar = false tira do IFC; ClasseIfc = classe IFC 4.3.";

    public string ProjetoNome { get; set; } = "";

    public string ProjetoDescricao { get; set; } = "";

    public string LocalNome { get; set; } = "";

    public bool Corredores { get; set; } = true;

    /// <summary>Nomes dos corredores (vazio = todos).</summary>
    public List<string> CorredoresNomes { get; set; } = new();

    /// <summary>
    /// Eixos dos corredores: "principais" (padrao: so os alinhamentos de baseline do tipo eixo central,
    /// Centerline; ficam de fora bordos/offset, retornos de esquina, ferrovia e diversos), "todos" ou "nenhum".
    /// </summary>
    public string? Eixos { get; set; }

    /// <summary>Versao antiga (2.118.0): verdadeiro/falso. Ainda lido: true = principais, false = nenhum.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EixosDosCorredores { get; set; }

    [JsonIgnore]
    public string EixosModo => (Eixos ?? (EixosDosCorredores == false ? "nenhum" : "principais")).Trim().ToLowerInvariant() switch
    {
        "todos" => "todos",
        "nenhum" => "nenhum",
        _ => "principais"
    };

    /// <summary>
    /// Corredores de referencia (data shortcut): "Nao" (padrao: ficam de fora, com os eixos so deles; sao
    /// exportados no desenho de origem), "Sim" (entram sempre) ou "Perguntar" (a cada exportacao).
    /// O KM_02 do Heyvid e referencia (17/11/2026).
    /// </summary>
    public string CorredoresReferenciados { get; set; } = "Nao";

    public bool Lombofaixas { get; set; } = true;

    /// <summary>Tudo, Solidos, Plato, Grelhas, Vigas, Sinalizacao (o mesmo filtro do MFTRAVESSIASELECIONAR).</summary>
    public string LombofaixasPecas { get; set; } = "Solidos";

    public bool Superficies { get; set; }

    /// <summary>
    /// Grelhas em X exportadas como chapa simples (contorno da grelha, mesma espessura): o IFC fica bem mais
    /// leve. So o .ifc muda; os solidos do desenho continuam com a grelha em X. Desligado por padrao.
    /// </summary>
    public bool GrelhaSimplificada { get; set; }

    /// <summary>
    /// Peças de corredor que a exportação da Autodesk grava incompletas (faixas descartadas onde seções vizinhas se
    /// cruzam, em curvas fechadas) recebem a malha do sólido do corredor. Ligado por padrão. Ver IfcSolidFix.
    /// </summary>
    public bool CorrigirPecasCortadas { get; set; } = true;

    // Marcacoes da janela do C3DIFC (gravadas para a proxima vez). Nomes/ids; vazio = padrao.
    public List<string> CorredoresDesmarcados { get; set; } = new();

    public List<string> CorredoresReferenciaMarcados { get; set; } = new();

    /// <summary>Eixos marcados/desmarcados a mao, por cima da regra de Eixos.</summary>
    public List<string> EixosMarcados { get; set; } = new();

    public List<string> EixosDesmarcados { get; set; } = new();

    /// <summary>Pecas da lombofaixa que entram: Plato, Grelhas, Vigas, Sinalizacao, Planta.</summary>
    public List<string> LombofaixasPecasMarcadas { get; set; } = new() { "Plato", "Grelhas", "Vigas", "Sinalizacao" };

    /// <summary>Ids das travessias/unioes que ficam de fora.</summary>
    public List<string> TravessiasDesmarcadas { get; set; } = new();

    public List<string> SuperficiesMarcadas { get; set; } = new();

    public List<string> RedesMarcadas { get; set; } = new();

    /// <summary>Objetos escolhidos a mao no desenho (botao Selecionar no desenho), por handle.</summary>
    public List<string> ManuaisHandles { get; set; } = new();

    public bool ManuaisIncluir { get; set; } = true;

    /// <summary>Selecionados a mao que ficaram desmarcados na janela (por handle).</summary>
    public List<string> ManuaisDesmarcados { get; set; } = new();

    /// <summary>Ultimo arquivo .ifc exportado neste projeto (a janela abre com ele).</summary>
    public string UltimoArquivoIfc { get; set; } = "";

    /// <summary>Layers extras com objetos que tambem entram (solidos, blocos...).</summary>
    public List<string> LayersExtras { get; set; } = new();

    public List<IfcMapRow> Mapeamento { get; set; } = DefaultRows();

    public static List<IfcMapRow> DefaultRows() => new()
    {
        new() { Mapeamento = "LayerName", Nome = "MF-TRAVESSIA-3D", ClasseIfc = "IfcPavement.RIGID", Observacao = "Plato da lombofaixa" },
        new() { Mapeamento = "LayerName", Nome = "MF-TRAVESSIA-GRELHA", ClasseIfc = "IfcPlate.COVER_PLATE", Observacao = "Grelha sobre a sarjeta" },
        new() { Mapeamento = "LayerName", Nome = "MF-TRAVESSIA-TAMPA", ClasseIfc = "IfcPlate.COVER_PLATE", Observacao = "Tampa de concreto sobre a sarjeta" },
        new() { Mapeamento = "LayerName", Nome = "MF-TRAVESSIA-VIGA", ClasseIfc = "IfcFooting.FOOTING_BEAM", Observacao = "Viga baldrame" },
        new() { Mapeamento = "LayerName", Nome = "MF-TRAVESSIA-SINALIZACAO", ClasseIfc = "IfcSurfaceFeature.PAVEMENTSURFACEMARKING", Observacao = "Faixa e triangulos (pintura)" },
        new() { Mapeamento = "LayerName", Nome = "MF-TRAVESSIA", Exportar = false, Observacao = "Hachuras de planta" },
        new() { Mapeamento = "LayerName", Nome = "MF-REBAIXO", Exportar = false, Observacao = "Hachuras dos rebaixos" },
        new() { Mapeamento = "ShapeStyleName", Nome = "GUIA", ClasseIfc = "IfcKerb", Observacao = "Meio-fio (code Curb/GUIA)" },
        new() { Mapeamento = "ShapeStyleName", Nome = "SARJETA", ClasseIfc = "IfcKerb", Observacao = "Sarjeta: so vale se o code Sarjeta tiver o estilo de shape SARJETA no Code Set" },
        new() { Mapeamento = "ShapeStyleName", Nome = "PAVIMENTO 1", ClasseIfc = "IfcPavement", Observacao = "Revestimento" },
        new() { Mapeamento = "ShapeStyleName", Nome = "PAVIMENTO 2", ClasseIfc = "IfcPavement", Observacao = "Revestimento" },
        new() { Mapeamento = "ShapeStyleName", Nome = "Ref_SubLeito", ClasseIfc = "IfcEarthworksFill.SUBGRADE", Observacao = "Subleito" },
        new() { Mapeamento = "ShapeStyleName", Nome = "Basic", Exportar = false, Observacao = "Shapes sem estilo (aparecem como '0')" },
        new() { Mapeamento = "LinkStyleName", Nome = "BASICO", Exportar = false, Observacao = "Cascas dos links (laterais, regularizacao, GUIA)" },
        new() { Mapeamento = "LinkStyleName", Nome = "PISTA", Exportar = false, Observacao = "Casca do topo da pista" },
        new() { Mapeamento = "LinkStyleName", Nome = "SECAO DATUM", Exportar = false, Observacao = "Cascas de datum" },
        new() { Mapeamento = "LinkStyleName", Nome = "Uncoded", Exportar = false, Observacao = "Cascas sem code (Top, TOPO, Datum)" }
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "Ifc", "criterios-padrao.json");

    private static string LastFolderFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "Ifc", "ultima-pasta.txt");

    /// <summary>Ultima pasta usada em qualquer projeto (para desenho novo, que ainda nao exportou).</summary>
    public static string LastFolder
    {
        get
        {
            try
            {
                string f = File.Exists(LastFolderFile) ? File.ReadAllText(LastFolderFile).Trim() : "";
                return f.Length > 0 && Directory.Exists(f) ? f : "";
            }
            catch
            {
                return "";
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastFolderFile)!);
                File.WriteAllText(LastFolderFile, value ?? "");
            }
            catch
            {
                // Sem gravar: a janela so nao lembra a pasta em outro projeto.
            }
        }
    }

    /// <summary>Arquivo .ifc proposto: o ultimo do projeto; senao, nome do DWG na ultima pasta usada; senao, ao lado do DWG.</summary>
    public string SuggestedIfc(string dwgPath)
    {
        if (!string.IsNullOrWhiteSpace(UltimoArquivoIfc) && Directory.Exists(System.IO.Path.GetDirectoryName(UltimoArquivoIfc) ?? ""))
            return UltimoArquivoIfc;
        string name = System.IO.Path.GetFileNameWithoutExtension(dwgPath) + ".ifc";
        string folder = LastFolder;
        return System.IO.Path.Combine(folder.Length > 0 ? folder : System.IO.Path.GetDirectoryName(dwgPath) ?? "", name);
    }

    public static string ProjectPath(string dwgPath) =>
        Path.Combine(Path.GetDirectoryName(dwgPath) ?? "", Path.GetFileNameWithoutExtension(dwgPath) + "-C3DIFC.json");

    /// <summary>Le os criterios do projeto; sem eles, copia o padrao (criando o padrao se faltar).</summary>
    public static (IfcCriteria Criteria, string Path, bool Created) LoadFor(string dwgPath, string projectName)
    {
        string path = ProjectPath(dwgPath);
        if (File.Exists(path))
            return (Read(path), path, false);
        IfcCriteria c = File.Exists(DefaultPath) ? Read(DefaultPath) : new IfcCriteria { Eixos = "principais" };
        if (!File.Exists(DefaultPath))
            Save(new IfcCriteria { Eixos = "principais" }, DefaultPath);
        if (string.IsNullOrWhiteSpace(c.ProjetoNome))
            c.ProjetoNome = projectName;
        if (string.IsNullOrWhiteSpace(c.LocalNome))
            c.LocalNome = projectName;
        Save(c, path);
        return (c, path, true);
    }

    public static IfcCriteria Read(string path)
    {
        IfcCriteria c = JsonSerializer.Deserialize<IfcCriteria>(File.ReadAllText(path), Json) ?? new IfcCriteria();
        // Criterio antigo (verdadeiro/falso) vira o novo ao ler; gravado do jeito novo na proxima vez.
        c.Eixos ??= c.EixosModo;
        c.EixosDosCorredores = null;
        return c;
    }

    public static void Save(IfcCriteria c, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(c, Json));
    }
}
