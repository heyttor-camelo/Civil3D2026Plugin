using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Civil3D2026Plugin.Infrastructure;

/// <summary>
/// Quais modulos o usuario quer ver: os desmarcados saem da Ribbon e da arvore
/// de ajuda e nao iniciam seus servicos (reatores, autoload de LISPs) na proxima
/// carga. Os comandos continuam registrados (a DLL .NET nao descarrega por
/// partes), entao digitar o nome ainda funciona.
///
/// Gravado em %APPDATA%\Civil3D2026Plugin\modulos.txt, uma linha por modulo
/// desativado: "desativado=Categoria/Nome". O modulo Geral nunca desativa.
/// </summary>
public static class ModuleSettings
{
    private static readonly HashSet<string> Disabled = Load();

    public static string Key(CommandCatalog.ModuleDefinition module) => module.Category + "/" + module.Name;

    public static bool IsEnabled(CommandCatalog.ModuleDefinition module) =>
        string.Equals(module.Category, "Geral", StringComparison.OrdinalIgnoreCase) || !Disabled.Contains(Key(module));

    /// <summary>Se algum modulo da categoria esta ativo (para iniciar o servico da categoria).</summary>
    public static bool IsCategoryEnabled(string category) =>
        CommandCatalog.Modules.Any(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase) && IsEnabled(m));

    /// <summary>Se o modulo que contem o comando esta ativo (comando desconhecido = ativo).</summary>
    public static bool IsCommandEnabled(string commandName)
    {
        CommandCatalog.ModuleDefinition? module = CommandCatalog.Modules
            .FirstOrDefault(m => m.Commands.Any(c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase)));
        return module is null || IsEnabled(module);
    }

    public static void SetEnabled(CommandCatalog.ModuleDefinition module, bool enabled)
    {
        if (enabled)
            Disabled.Remove(Key(module));
        else if (!string.Equals(module.Category, "Geral", StringComparison.OrdinalIgnoreCase))
            Disabled.Add(Key(module));
    }

    public static int DisabledCount => Disabled.Count;

    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Civil3D2026Plugin", "modulos.txt");

    private static HashSet<string> Load()
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Path))
                return set;
            foreach (string line in File.ReadAllLines(Path))
            {
                string t = line.Trim();
                if (t.StartsWith("desativado=", StringComparison.OrdinalIgnoreCase))
                    set.Add(t["desativado=".Length..].Trim());
            }
        }
        catch
        {
            // Preferencia e conveniencia: sem arquivo, tudo ativo.
        }

        return set;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            List<string> lines = new() { "# Modulos desativados do Civil3D2026Plugin (C3DMODULOS). Apague o arquivo para ativar todos." };
            lines.AddRange(Disabled.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => "desativado=" + k));
            File.WriteAllLines(Path, lines);
        }
        catch
        {
            // Sem permissao de gravacao: vale so na sessao.
        }
    }
}
