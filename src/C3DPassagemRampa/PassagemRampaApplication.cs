using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

// Ponto de entrada e comandos da DLL avulsa C3DPassagemRampa.dll (ferramentas do rebaixo de meio-fio).
// MFRebaixoCommands, IfcExportCommands e IfcCodeSetCommands ja se registram nos proprios arquivos;
// aqui entram as demais classes (com algum CommandClass na assembly, o AutoCAD so le as listadas).
[assembly: ExtensionApplication(typeof(Civil3D2026Plugin.PassagemRampa.PassagemRampaApplication))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.PassagemRampa.PassagemRampaApplication))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo.MFTravessiaCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo.MFTravessiaUniaoCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.FeatureLines.FeatureLineGeometryCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.FeatureLines.FeatureLineOriginCommands))]

namespace Civil3D2026Plugin.PassagemRampa;

/// <summary>
/// DLL avulsa com tudo do fluxo do rebaixo de meio-fio: MFREBAIXO* (Corridor Transitions, painel,
/// hachura), MFTRAVESSIA* (lombofaixas entre rebaixos), exportacao IFC (C3DIFC, C3DCODESETIFC) e as
/// feature lines de meio-fio/ilha usadas como baseline (FLCOPIARGEOMETRIA, FLORIGEM, FLFECHADAS).
/// A passagem semi-elevada antiga (PASSAGEM*) saiu na 1.28.0 e fica so no plugin completo.
/// O codigo e o mesmo do Civil3D2026Plugin (o projeto aponta para os arquivos de la), sem Ribbon.
/// </summary>
public sealed class PassagemRampaApplication : IExtensionApplication
{
    private static readonly (string Grupo, string Nome, string Descricao)[] Comandos =
    {
        ("Rebaixo", C3DCommands.Corridor.MfRebaixo, "Rebaixo/rampa de meio-fio: cria as Corridor Transitions no corredor."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoPainel, "Painel dos rebaixos e travessias (planta, lista, vistas, edicao)."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoHachura, "Redesenhar a hachura em planta de todos os rebaixos."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoAcertar, "Refazer os rebaixos cuja altura final saiu diferente da pedida."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoModoRebuild, "Rebuild do corredor automatico ou manual."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoReconstruir, "Reconstruir os rebaixos pendentes (modo manual)."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoApagar, "Remover rebaixos clicando em cima deles no desenho."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoLimpar, "Apagar de vez os Transition Sets de rebaixos removidos."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoSelecionarPainel, "Selecionar no desenho o que esta marcado no painel (botao do painel)."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoCfg, "Configurar presets do MFREBAIXO."),
        ("Rebaixo", C3DCommands.Corridor.MfRebaixoHelp, "Ajuda do MFREBAIXO."),
        ("Travessia", C3DCommands.Corridor.MfTravessia, "Travessia elevada/semi-elevada (lombofaixa) entre dois rebaixos."),
        ("Travessia", C3DCommands.Corridor.MfTravessiaUnir, "Unir (ou separar) as travessias de um cruzamento num plato so."),
        ("Travessia", C3DCommands.Corridor.MfTravessiaRefazer, "Redesenhar as travessias depois de mexer no corredor."),
        ("Travessia", C3DCommands.Corridor.MfTravessiaApagar, "Apagar travessias clicando nelas no desenho."),
        ("Travessia", C3DCommands.Corridor.MfTravessiaPlanta, "Ocultar ou mostrar as lombofaixas em planta (o 3D fica)."),
        ("Travessia", C3DCommands.Corridor.MfTravessiaSelecionar, "Selecionar no desenho as pecas das lombofaixas (para IFC, layer, isolar)."),
        ("IFC", C3DCommands.Ifc.Exportar, "Exportar IFC 4.3 (objetos, classes IFC; grava so o .ifc)."),
        ("IFC", C3DCommands.Ifc.ExportarLinha, "Exportar IFC pela linha de comando, sem janela."),
        ("IFC", C3DCommands.Ifc.Relatorio, "Depois da exportacao: nome do projeto/local no .ifc, conferencia e resumo."),
        ("IFC", C3DCommands.Ifc.CodeSet, "Acertar o Code Set Style do corredor para o IFC (meio-fio, sarjeta, pavimento)."),
        ("Feature line", C3DCommands.FeatureLines.CopiarGeometria, "Feature line de meio-fio/ilha copia a geometria de uma polilinha (mesmo objeto)."),
        ("Feature line", C3DCommands.FeatureLines.Origem, "Nova origem da feature line fechada, levando regioes e rebaixos junto."),
        ("Feature line", C3DCommands.FeatureLines.Fechadas, "Achar e fechar feature lines que parecem fechadas mas estao abertas."),
        ("Ajuda", C3DCommands.PassagemRampa.Help, "Mostra esta lista de novo."),
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
        ed.WriteMessage($"\n C3DPassagemRampa v{C3DVersions.PassagemRampa}  (rebaixo de meio-fio, travessias e IFC)");
        ed.WriteMessage($"\n MFREBAIXO v{C3DVersions.MfRebaixo} | IFC v{C3DVersions.Ifc}");
        ed.WriteMessage("\n============================================================");
        ed.WriteMessage($"\n DLL carregada: {AssemblyPath()}");
        string grupo = "";
        foreach ((string g, string nome, string descricao) in Comandos)
        {
            if (g != grupo)
            {
                ed.WriteMessage($"\n [{g}]");
                grupo = g;
            }
            ed.WriteMessage($"\n  {nome,-26} {descricao}");
        }
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
