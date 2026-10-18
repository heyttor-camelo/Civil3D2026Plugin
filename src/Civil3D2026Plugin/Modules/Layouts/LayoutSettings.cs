using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Preferencias do explorador de layouts (chave=valor em
/// %APPDATA%\Civil3D2026Plugin\layouts_painel.txt): prancha por formato, modo leve
/// das viewports, modo de ajuste das viewports vinculadas a view frames.
/// </summary>
internal static class LayoutSettings
{
    private static readonly object Lock = new();
    private static Dictionary<string, string>? _valores;

    private static string Caminho => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Civil3D2026Plugin", "layouts_painel.txt");

    private static Dictionary<string, string> Valores
    {
        get
        {
            lock (Lock)
            {
                if (_valores is not null)
                    return _valores;
                _valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(Caminho))
                        foreach (string linha in File.ReadAllLines(Caminho))
                        {
                            int i = linha.IndexOf('=');
                            if (i > 0)
                                _valores[linha[..i].Trim()] = linha[(i + 1)..].Trim();
                        }
                }
                catch (Exception)
                {
                    // Sem preferencias: usa os padroes.
                }
                return _valores;
            }
        }
    }

    public static string Get(string chave, string padrao = "") =>
        Valores.TryGetValue(chave, out string? v) ? v : padrao;

    public static bool GetBool(string chave, bool padrao) =>
        Valores.TryGetValue(chave, out string? v) ? v is "1" or "true" or "sim" : padrao;

    public static int GetInt(string chave, int padrao) =>
        Valores.TryGetValue(chave, out string? v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : padrao;

    public static double GetDouble(string chave, double padrao) =>
        Valores.TryGetValue(chave, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : padrao;

    public static void Set(string chave, string valor)
    {
        lock (Lock)
        {
            Valores[chave] = valor;
            Salvar();
        }
    }

    public static void Set(string chave, bool valor) => Set(chave, valor ? "1" : "0");

    public static void Set(string chave, int valor) => Set(chave, valor.ToString(CultureInfo.InvariantCulture));

    public static void Set(string chave, double valor) => Set(chave, valor.ToString("0.####", CultureInfo.InvariantCulture));

    private static void Salvar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            var linhas = new List<string>();
            foreach ((string k, string v) in Valores)
                linhas.Add($"{k}={v}");
            linhas.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(Caminho, linhas);
        }
        catch (Exception)
        {
            // Preferencias sao melhor esforco.
        }
    }
}
