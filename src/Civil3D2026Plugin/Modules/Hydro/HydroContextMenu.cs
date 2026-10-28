using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Civil3D2026Plugin.Infrastructure;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
// No .NET 10 o WinForms voltou a ter System.Windows.Forms.MenuItem (using global): fixa o do AutoCAD.
using MenuItem = Autodesk.AutoCAD.Windows.MenuItem;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Menu de contexto HYDRO no botao direito sobre tubos e estruturas (ContextMenuExtension por
/// classe). Cada item manda o comando correspondente; a selecao clicada vira pickfirst do comando.
/// </summary>
internal static class HydroContextMenu
{
    private static ContextMenuExtension? _pipeMenu;
    private static ContextMenuExtension? _structureMenu;

    public static void Initialize()
    {
        if (_pipeMenu is not null)
            return;
        try
        {
            _pipeMenu = Build(true);
            _structureMenu = Build(false);
            AcApp.AddObjectContextMenuExtension(RXObject.GetClass(typeof(CivilDb.Pipe)), _pipeMenu);
            AcApp.AddObjectContextMenuExtension(RXObject.GetClass(typeof(CivilDb.Structure)), _structureMenu);
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso("Hydro", "Menu de contexto nao registrado: " + ex.Message);
            _pipeMenu = null;
            _structureMenu = null;
        }
    }

    public static void Terminate()
    {
        try
        {
            if (_pipeMenu is not null)
                AcApp.RemoveObjectContextMenuExtension(RXObject.GetClass(typeof(CivilDb.Pipe)), _pipeMenu);
            if (_structureMenu is not null)
                AcApp.RemoveObjectContextMenuExtension(RXObject.GetClass(typeof(CivilDb.Structure)), _structureMenu);
        }
        catch
        {
            // Encerramento.
        }

        _pipeMenu = null;
        _structureMenu = null;
    }

    private static ContextMenuExtension Build(bool pipe)
    {
        ContextMenuExtension menu = new() { Title = "HYDRO" };
        if (pipe)
        {
            Add(menu, "Declividade (segurar montante/jusante)...", C3DCommands.Hydro.Decliv);
            Add(menu, "Trocar familia / tamanho...", C3DCommands.Hydro.Trocar);
            Add(menu, "Inverter sentido", C3DCommands.Hydro.Inverter);
            Add(menu, "Conectar / desconectar ponta...", C3DCommands.Hydro.Conectar);
            Add(menu, "Inserir estrutura no tubo...", C3DCommands.Hydro.Inserir);
            Add(menu, "Igualar propriedades a partir deste...", C3DCommands.Hydro.Igualar);
        }
        else
        {
            Add(menu, "Editar medidas (comprimento, largura)...", C3DCommands.Hydro.Medidas);
            Add(menu, "Editar fundo...", C3DCommands.Hydro.Fundo);
            Add(menu, "Editar tampa...", C3DCommands.Hydro.Tampa);
            Add(menu, "Regra de medida (comprimento, largura, tampa, fundo)...", C3DCommands.Hydro.Regra);
            Add(menu, "Atualizar regras das medidas", C3DCommands.Hydro.Regras);
            Add(menu, "Trocar tipo / tamanho...", C3DCommands.Hydro.Trocar);
            Add(menu, "Mover (tubos acompanham)...", C3DCommands.Hydro.Mover);
            Add(menu, "Remover e emendar os tubos", C3DCommands.Hydro.Remover);
            Add(menu, "Ligar bacia a esta estrutura...", C3DCommands.Hydro.LigarBacia);
            Add(menu, "Igualar propriedades a partir desta...", C3DCommands.Hydro.Igualar);
        }

        menu.MenuItems.Add(new MenuItem(""));
        Add(menu, "Calcular a rede", C3DCommands.Hydro.Calcular);
        Add(menu, "Mostrar no painel HYDRO", pipe ? C3DCommands.Hydro.Tubos : C3DCommands.Hydro.Estruturas);
        return menu;
    }

    private static void Add(ContextMenuExtension menu, string text, string command)
    {
        MenuItem item = new(text);
        item.Click += (_, _) =>
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            // Sem o cancel duplo: a selecao clicada tem de continuar valendo como pickfirst.
            doc?.SendStringToExecute(command + " ", true, false, false);
        };
        menu.MenuItems.Add(item);
    }
}
