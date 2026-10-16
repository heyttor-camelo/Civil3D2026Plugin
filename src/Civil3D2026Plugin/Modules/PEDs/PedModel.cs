using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.PEDs;

/// <summary>Uma coluna da aba de dados: letra (chave estavel) e rotulo do cabecalho.</summary>
internal sealed record PedColumn(string Letter, string Label);

/// <summary>
/// Um PED lido da planilha: valores por letra de coluna (ja formatados para exibicao)
/// e as imagens "dentro da celula" (letra da coluna -> entrada do zip do xlsx).
/// </summary>
internal sealed class PedRecord
{
    public PedRecord(string code) => Code = code;

    public string Code { get; }

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Imagens da linha: letra da coluna -> caminho dentro do xlsx (ex.: xl/media/image1.png).</summary>
    public Dictionary<string, string> ImageEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string letter) => Values.TryGetValue(letter, out string? v) ? v : string.Empty;
}

/// <summary>Resultado da leitura da planilha.</summary>
internal sealed class PedSheet
{
    public string SheetName { get; set; } = string.Empty;

    /// <summary>Texto da linha 2 da aba (descricao do cenario, ex.: "Faixa exclusiva com 3,2 m ...").</summary>
    public string Title { get; set; } = string.Empty;

    public List<PedColumn> Columns { get; } = new();

    public List<PedRecord> Records { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>Ponto do KMZ/KML.</summary>
internal sealed record PedPoint(string Name, double Longitude, double Latitude, string Style);

/// <summary>Formatacao comum dos valores.</summary>
internal static class PedFormat
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Numero com ate 2 casas, virgula decimal (6 / 3,2 / 5,11).</summary>
    public static string Number(double value)
    {
        double r = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(r) < 0.005)
            r = 0;
        return r.ToString("0.##", PtBr);
    }

    public static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, PtBr, out value);
    }

    /// <summary>Letra da coluna (1 = A, 27 = AA).</summary>
    public static string ColumnLetter(int index)
    {
        string s = string.Empty;
        while (index > 0)
        {
            int m = (index - 1) % 26;
            s = (char)('A' + m) + s;
            index = (index - 1) / 26;
        }
        return s;
    }

    /// <summary>Indice da coluna a partir da letra (A = 1).</summary>
    public static int ColumnIndex(string letters)
    {
        int n = 0;
        foreach (char c in letters.ToUpperInvariant())
        {
            if (c < 'A' || c > 'Z')
                break;
            n = (n * 26) + (c - 'A' + 1);
        }
        return n;
    }

    /// <summary>"BK12" -> ("BK", 12).</summary>
    public static (string Letters, int Row) SplitRef(string cellRef)
    {
        int i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
            i++;
        int row = int.TryParse(cellRef.AsSpan(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 0;
        return (cellRef[..i].ToUpperInvariant(), row);
    }

    /// <summary>Para comparar cabecalhos: minusculo, sem acento e sem espacos repetidos.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        string d = text.Normalize(System.Text.NormalizationForm.FormD);
        var chars = d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        string s = new string(chars.ToArray()).ToLowerInvariant();
        return string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
