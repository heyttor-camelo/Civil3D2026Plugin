using Autodesk.AutoCAD.Runtime;

// Registro central das classes que expõem comandos ao AutoCAD/Civil 3D.
// Ao criar uma NOVA classe de comandos, adicione uma única linha aqui.
[assembly: CommandClass(typeof(Civil3D2026Plugin.Infrastructure.PluginApplication))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Drenagem.DrenExcelCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Drenagem.DrenNumCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.QTO.QtoCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.MFRebaixo.MFRebaixoCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.CorridorSplit.CorridorSplitCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.SolidArray.C3DSolidArrayCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing.RaisedCrossingCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.FeatureLines.FeatureLineElevationCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.FeatureLines.FeatureLineVertexCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.FeatureLines.FeatureLineRenameCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Surfaces.SurfaceColorAnalysisCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Surfaces.SurfaceSlopeCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Surfaces.TrimSurfaceLinesCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Surfaces.SurfaceGapCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Geometry.PointNormalCommands))]
[assembly: CommandClass(typeof(Civil3D2026Plugin.Diagnostics.PluginDiagnosticsCommands))]
