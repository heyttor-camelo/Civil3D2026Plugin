using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>
/// Monta a ficha do PED no mesmo arranjo da aba "Ficha para desenhos" da planilha
/// (BRT Amazonas - Proposta PEDs Barreiro). As letras abaixo sao as colunas da aba
/// "PEDs revisado V3" usadas pelos PROCV da ficha.
///
/// Se a planilha mudar de layout (coluna inserida/removida), ajuste SO as letras daqui:
/// a importacao confere os cabecalhos em <see cref="ExpectedHeaders"/> e avisa.
/// A aba "Todos os campos" da paleta mostra qualquer coluna, independente deste mapa.
/// </summary>
internal static class PedFicha
{
    public const string ColCodigo = "A";
    public const string ColFoto = "P";   // Imagem local
    public const string ColMapa = "BK";  // Fig com localizacao

    public sealed record Info(string Label, string Value);

    public sealed record WidthLine(string Group, string Item, string Atual, string Proposta);

    public sealed record Distance(string Label, string Ped, string Meters);

    public sealed class Sheet
    {
        public List<Info> Header { get; } = new();
        public List<WidthLine> Widths { get; } = new();
        public string TotalAtual { get; set; } = string.Empty;
        public string TotalProposta { get; set; } = string.Empty;
        public List<Info> Complement { get; } = new();
        public List<Info> Boardings { get; } = new();
        public List<Distance> Distances { get; } = new();
        public string Obs { get; set; } = string.Empty;
        public List<Info> Evaluation { get; } = new();

        /// <summary>"viavel", "inviavel" ou vazio (coluna AP).</summary>
        public string Verdict { get; set; } = string.Empty;
    }

    /// <summary>Cabecalhos esperados (normalizados, comeco do texto) para conferir o layout.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "codigo",
        ["B"] = "via",
        ["C"] = "quadra",
        ["D"] = "sentido",
        ["K"] = "lote",
        ["P"] = "imagem local",
        ["R"] = "largura calcada no ped",
        ["T"] = "largura canteiro central",
        ["AA"] = "largura da calcada no ped",
        ["AC"] = "largura da faixa exclusiva",
        ["AG"] = "canteiro central",
        ["AN"] = "largura disponivel do trafego geral",
        ["AP"] = "avaliacao",
        ["AX"] = "tipo ped",
        ["BA"] = "ped anterior",
        ["BB"] = "ped posterior",
        ["BE"] = "ciclovia final",
        ["BG"] = "obs para tabela",
        ["BI"] = "embarques pm",
        ["BJ"] = "embarques pt",
        ["BK"] = "fig com localizacao"
    };

    /// <summary>Colunas cujo cabecalho nao bate com o esperado (layout da planilha mudou).</summary>
    public static List<string> CheckLayout(IEnumerable<PedColumn> columns)
    {
        Dictionary<string, string> byLetter = columns.ToDictionary(c => c.Letter, c => PedFormat.Normalize(c.Label), StringComparer.OrdinalIgnoreCase);
        List<string> wrong = new();
        foreach ((string letter, string expected) in ExpectedHeaders)
        {
            if (!byLetter.TryGetValue(letter, out string? actual) || !actual.StartsWith(expected, StringComparison.Ordinal))
                wrong.Add($"{letter} (esperado \"{expected}\", achado \"{actual ?? "vazio"}\")");
        }
        return wrong;
    }

    public static Sheet Build(Func<string, string> get)
    {
        Sheet f = new();

        f.Header.Add(new Info("PED", get("A")));
        f.Header.Add(new Info("Via", get("B")));
        f.Header.Add(new Info("Referência", get("K")));
        f.Header.Add(new Info("Quadra", get("C")));
        f.Header.Add(new Info("Sentido", get("D")));

        // ----- Larguras dos componentes viarios (m): atual x proposta -----
        bool semCanteiroProposto = IsZero(get("AG"));
        string cicloOposta = BlankZero(get("AL"));
        string pistaOpostaProposta = semCanteiroProposto
            ? string.Empty
            : cicloOposta.Length == 0
                ? BlankZero(get("U"))
                : Minus(get("U"), get("AL"));

        f.Widths.Add(new WidthLine("Pista junto ao PED", "Calçada", BlankZero(get("R")), get("AA")));
        f.Widths.Add(new WidthLine("Pista junto ao PED", "Faixa exclusiva de ônibus", get("O"), get("AC")));
        f.Widths.Add(new WidthLine("Pista junto ao PED", "Faixas do tráfego geral", get("S"), get("AN")));
        f.Widths.Add(new WidthLine("Pista junto ao PED", "Ciclovia/ciclofaixa", string.Empty, semCanteiroProposto ? string.Empty : get("AE")));
        f.Widths.Add(new WidthLine("Canteiro central", string.Empty, BlankZero(get("T")), BlankZero(get("AG"))));
        f.Widths.Add(new WidthLine("Pista oposta ao PED", "Pista", BlankZero(get("U")), pistaOpostaProposta));
        f.Widths.Add(new WidthLine("Pista oposta ao PED", "Calçada", get("V"), get("AK")));
        f.Widths.Add(new WidthLine("Pista oposta ao PED", "Ciclovia/ciclofaixa", BlankZero(get("W")), cicloOposta));

        f.TotalAtual = Sum(f.Widths.Select(w => w.Atual));
        f.TotalProposta = Sum(f.Widths.Select(w => w.Proposta));

        // ----- Informacoes complementares da proposta -----
        string fx = get("AB");
        f.Complement.Add(new Info("Modelo de abrigo proposto", get("AX")));
        f.Complement.Add(new Info("Faixa exclusiva", string.Equals(fx.Trim(), "sim", StringComparison.OrdinalIgnoreCase) ? "Proposto" : fx));
        f.Complement.Add(new Info("Ciclovia ou ciclofaixa", get("BE")));
        f.Complement.Add(new Info("Alteração calçada PED", get("AS")));
        f.Complement.Add(new Info("Alteração do canteiro central", get("AT")));
        f.Complement.Add(new Info("Alteração calçada oposta PED", get("AU")));

        f.Boardings.Add(new Info("Embarques PM (passag.)", BlankZero(get("BI"))));
        f.Boardings.Add(new Info("Embarques PT (passag.)", BlankZero(get("BJ"))));

        f.Distances.Add(new Distance("Distância do PED anterior (m)", get("BA"), get("BC")));
        f.Distances.Add(new Distance("Distância do PED posterior (m)", get("BB"), get("BD")));

        f.Obs = get("BG");

        // ----- Avaliacao (colunas da planilha que nao aparecem na ficha impressa) -----
        f.Verdict = PedFormat.Normalize(get("AP"));
        f.Evaluation.Add(new Info("Avaliação faixa de tráfego", get("AP")));
        f.Evaluation.Add(new Info("Largura faixa tráfego geral (m)", get("AO")));
        f.Evaluation.Add(new Info("Comprimento do abrigo (10 m)", get("AQ")));
        f.Evaluation.Add(new Info("Remanejamento", get("AR")));
        f.Evaluation.Add(new Info("Impacto da faixa exclusiva", get("AV")));
        f.Evaluation.Add(new Info("Trecho", get("AW")));
        f.Evaluation.Add(new Info("Ponto escalonado", get("BF")));
        f.Evaluation.Add(new Info("Uso do solo", get("G")));
        f.Evaluation.Add(new Info("Obs. 3", get("BH")));

        return f;
    }

    private static bool IsZero(string s) => !PedFormat.TryNumber(s, out double v) || Math.Abs(v) < 1e-9;

    private static string BlankZero(string s) =>
        PedFormat.TryNumber(s, out double v) && Math.Abs(v) < 1e-9 ? string.Empty : s;

    private static string Minus(string a, string b) =>
        PedFormat.TryNumber(a, out double x) && PedFormat.TryNumber(b, out double y) ? PedFormat.Number(x - y) : a;

    private static string Sum(IEnumerable<string> values)
    {
        double total = 0;
        bool any = false;
        foreach (string s in values)
        {
            if (PedFormat.TryNumber(s, out double v))
            {
                total += v;
                any = true;
            }
        }
        return any ? PedFormat.Number(total) : string.Empty;
    }
}
