using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.LispTools;

/// <summary>Comandos do modulo LispTools.</summary>
public sealed class LispCommands
{
    /// <summary>Janela com a lista das LISPs, explicacao e botao para cada comando.</summary>
    [CommandMethod(C3DCommands.LispTools.Painel, CommandFlags.Modal)]
    public void Painel()
    {
        LispPanel.ShowPanel();
    }

    /// <summary>Mesma lista, na linha de comando.</summary>
    [CommandMethod(C3DCommands.LispTools.Lista, CommandFlags.Modal)]
    public void Lista()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        var sb = new StringBuilder();
        sb.Append($"\n=== LISPs embutidas - LispTools v{C3DVersions.LispTools} ({LispModule.Catalogo.Count} rotinas) ===");
        sb.Append("\nCada rotina e carregada no primeiro uso do comando.\n");

        foreach (var grupo in LispModule.Catalogo.GroupBy(l => l.Categoria))
        {
            sb.Append($"\n[{grupo.Key}]");
            foreach (LispInfo l in grupo)
            {
                string ver = l.Versao.Length > 0 ? $" v{l.Versao}" : "";
                sb.Append($"\n  {string.Join(" / ", l.Comandos)}  -  {l.Titulo}  ({l.Arquivo}{ver})");
                sb.Append($"\n      {l.Descricao}");
                foreach (string u in l.Uso)
                    sb.Append($"\n        - {u}");
            }
            sb.Append('\n');
        }

        sb.Append($"\n{C3DCommands.LispTools.Painel} abre a mesma lista numa janela. {C3DCommands.LispTools.Carregar} registra as rotinas de novo.\n");
        doc.Editor.WriteMessage(sb.ToString());
    }

    /// <summary>Regrava as LISPs na pasta local e registra de novo no desenho atual.</summary>
    [CommandMethod(C3DCommands.LispTools.Carregar, CommandFlags.Modal)]
    public void Carregar()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        LispModule.ExtrairArquivos();
        LispModule.GarantirPastaConfiavel();
        LispModule.CarregarEm(doc, forcar: true);
        doc.Editor.WriteMessage($"\n[LispTools] {LispModule.Catalogo.Count} rotinas registradas a partir de {LispModule.Pasta}.");
    }
}
