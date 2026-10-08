// Aliases globais padronizados do projeto.
//
// REGRA PARA NOVOS MODULOS:
// - quando um arquivo precisar de AutoCAD.DatabaseServices E Civil.DatabaseServices,
//   prefira AcadDb.<tipo> e CivilDb.<tipo> em vez de importar os dois namespaces
//   amplamente. Isso evita colisões como Entity/DBObject.
global using AcadDb = Autodesk.AutoCAD.DatabaseServices;
global using CivilDb = Autodesk.Civil.DatabaseServices;

global using C3DCommands = Civil3D2026Plugin.Infrastructure.CommandNames;
global using C3DVersions = Civil3D2026Plugin.Infrastructure.PluginVersions;
