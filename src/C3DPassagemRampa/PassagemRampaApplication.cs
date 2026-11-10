using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Ponto de entrada e comandos da DLL avulsa C3DPassagemRampa.dll.
// O MFRebaixoCommands ja se registra no proprio arquivo; aqui entram as demais classes.
[assembly: ExtensionApplication(typeof(Civil3D2026Plugin.PassagemRampa.PassagemRampaApplication))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.PassagemRampa.PassagemRampaApplication))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing.RaisedCrossingCommands))]

namespace Civil3D2026Plugin.PassagemRampa;

/// <summary>
/// DLL avulsa so com a passagem semi-elevada (PASSAGEM*) e o rebaixo/rampa de meio-fio por
/// Corridor Transition (MFREBAIXO*). O codigo dos dois modulos e o mesmo do Civil3D2026Plugin
/// (o projeto aponta para os arquivos de la), sem Ribbon e sem os outros modulos.
/// </summary>
public sealed class PassagemRampaApplication : IExtensionApplication
{
    private static readonly (string Nome, string Descricao)[] Comandos =
    {
        (C3DCommands.Corridor.Passagem, "Criar passagem semi-elevada (4 Feature Lines, rampas nas vias, conector central)."),
        (C3DCommands.Corridor.PassagemEditar, "Editar plato, extensao, rampas, altura H, posicao ou ancoragem."),
        (C3DCommands.Corridor.PassagemGrupo, "Aplicar parametros comuns a varias passagens."),
        (C3DCommands.Corridor.PassagemAtualizar, "Reconstruir passagens depois de mexer nas Feature Lines."),
        (C3DCommands.Corridor.PassagemCfg, "Configurar extensao transversal, rampas padrao e amostragem."),
        (C3DCommands.Corridor.PassagemHelp, "Ajuda da passagem semi-elevada."),
        (C3DCommands.Corridor.MfRebaixo, "Rebaixo/rampa de meio-fio: cria as Corridor Transitions no corredor."),
        (C3DCommands.Corridor.MfRebaixoPainel, "Painel dos rebaixos (planta, lista, edicao, avisos NBR 9050)."),
        (C3DCommands.Corridor.MfRebaixoHachura, "Redesenhar a hachura em planta de todos os rebaixos."),
        (C3DCommands.Corridor.MfRebaixoLimpar, "Apagar de vez os Transition Sets de rebaixos removidos."),
        (C3DCommands.Corridor.MfRebaixoCfg, "Configurar presets do MFREBAIXO."),
        (C3DCommands.Corridor.MfRebaixoHelp, "Ajuda do MFREBAIXO."),
        (C3DCommands.PassagemRampa.Help, "Mostra esta lista de novo."),
    };

    public void Initialize()
    {
        PluginLog.Iniciar();
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is not null)
            WriteBanner(doc.Editor);
    }

    public void Terminate()
    {
        PluginLog.Info("Plugin", "Fim da sessao (C3DPassagemRampa).");
        PluginLog.Encerrar();
    }

    [CommandMethod(C3DCommands.PassagemRampa.Help, CommandFlags.Modal)]
    public void ShowHelp()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is not null)
            WriteBanner(doc.Editor);
    }

    private static void WriteBanner(Editor ed)
    {
        ed.WriteMessage("\n");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n C3DPassagemRampa v{C3DVersions.PassagemRampa}  (passagem semi-elevada + rampa de meio-fio)");
        ed.WriteMessage($"\n Passagem v{C3DVersions.RaisedCrossing} | MFREBAIXO v{C3DVersions.MfRebaixo}");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n DLL carregada: {AssemblyPath()}");
        foreach ((string nome, string descricao) in Comandos)
            ed.WriteMessage($"\n  {nome,-20} {descricao}");
        ed.WriteMessage("\n============================================================");

        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, "Civil3D2026Plugin", StringComparison.OrdinalIgnoreCase)))
        {
            ed.WriteMessage("\n AVISO: o Civil3D2026Plugin.dll (plugin completo) tambem esta carregado e ja tem estes");
            ed.WriteMessage("\n comandos. Feche o Civil 3D e carregue so uma das duas DLLs.");
            ed.WriteMessage("\n============================================================");
            PluginLog.Aviso("Plugin", "C3DPassagemRampa.dll carregada junto com o Civil3D2026Plugin.dll (comandos repetidos).");
        }
        ed.WriteMessage("\n");
    }

    private static string AssemblyPath()
    {
        try
        {
            string location = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrWhiteSpace(location) ? "(caminho nao informado pelo runtime)" : Path.GetFullPath(location);
        }
        catch (System.Exception)
        {
            return "(caminho indisponivel)";
        }
    }
}
