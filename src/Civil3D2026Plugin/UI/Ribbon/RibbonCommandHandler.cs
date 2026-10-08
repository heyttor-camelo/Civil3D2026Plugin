using System;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.UI.Ribbon;

/// <summary>
/// Encaminha o clique da Ribbon para o mecanismo normal de comandos do AutoCAD.
/// A Ribbon nao chama diretamente a logica dos modulos: ela executa o mesmo
/// comando publico que o usuario poderia digitar na linha de comando.
/// </summary>
internal sealed class RibbonCommandHandler : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        string? command = parameter switch
        {
            RibbonCommandItem item => item.CommandParameter?.ToString(),
            string text => text,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(command))
            return;

        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        doc.SendStringToExecute(command.Trim() + " ", true, false, true);
    }
}
