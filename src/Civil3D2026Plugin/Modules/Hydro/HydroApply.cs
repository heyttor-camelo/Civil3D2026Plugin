using System;
using System.Collections.Generic;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Fila de edicoes pedidas pelo painel. O painel nunca escreve no desenho direto: enfileira a
/// acao e manda o comando HYAPLICAR, que roda no contexto de comando (documento travado) e vira
/// um unico UNDO por operacao.
/// </summary>
internal static class HydroApply
{
    private static readonly Queue<(string What, Action<AcadDb.Database> Action)> Pending = new();

    /// <summary>Rede ativa no painel (handle) para os comandos que precisam escolher a rede.</summary>
    public static string? ActiveNetwork { get; set; }

    /// <summary>Itens marcados no painel para o proximo comando que aceita pre-selecao (HYTROCAR, HYTC).</summary>
    public static List<string>? Preselect { get; set; }

    public static void Queue(string what, Action<AcadDb.Database> action) => Pending.Enqueue((what, action));

    public static bool TryDequeue(out string what, out Action<AcadDb.Database> action)
    {
        if (Pending.Count == 0)
        {
            what = "";
            action = _ => { };
            return false;
        }

        (what, action) = Pending.Dequeue();
        return true;
    }

    /// <summary>Consome a pre-selecao (uma vez).</summary>
    public static List<string>? TakePreselect()
    {
        List<string>? list = Preselect;
        Preselect = null;
        return list;
    }
}
