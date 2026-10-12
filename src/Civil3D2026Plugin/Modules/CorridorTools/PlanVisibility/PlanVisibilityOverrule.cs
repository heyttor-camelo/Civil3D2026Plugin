using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using AcGi = Autodesk.AutoCAD.GraphicsInterface;

// Autodesk.AutoCAD.Runtime tambem define Registry/RegistryKey: use aliases explicitos (CS0104).
using WinRegistry = Microsoft.Win32.Registry;
using WinRegistryKey = Microsoft.Win32.RegistryKey;
using WinRegistryValueKind = Microsoft.Win32.RegistryValueKind;

namespace Civil3D2026Plugin.Modules.CorridorTools.PlanVisibility;

/// <summary>Como os solidos-alvo aparecem nas vistas de planta.</summary>
internal enum PlanDisplayMode
{
    /// <summary>Nao sao desenhados em planta.</summary>
    Ocultar,

    /// <summary>Desenhados so com as arestas visiveis de cima (linhas ocultas removidas).</summary>
    Exato
}

/// <summary>
/// Configuracao do "solidos so no 3D", gravada no registro do usuario
/// (HKCU\Software\Civil3D2026Plugin\PlanVisibility) para valer em todos os DWGs.
/// </summary>
internal static class PlanVisibilitySettings
{
    private const string RegistryPath = @"Software\Civil3D2026Plugin\PlanVisibility";

    public static bool Enabled { get; set; }

    public static PlanDisplayMode Mode { get; set; } = PlanDisplayMode.Ocultar;

    /// <summary>No modo Exato, recalcula sozinho os solidos alterados ao ficar ocioso.</summary>
    public static bool AutoRefresh { get; set; } = true;

    /// <summary>Prefixo especial: todos os solidos e bodies.</summary>
    public const string AllPrefix = "*";

    public static IReadOnlyList<string> Prefixes { get; private set; } = Array.Empty<string>();

    public static string PrefixesText => string.Join(";", Prefixes);

    /// <summary>True se o filtro inclui todos os solidos (prefixo *).</summary>
    public static bool MatchesAll => Prefixes.Contains(AllPrefix);

    public static void SetPrefixes(string text)
    {
        Prefixes = text
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void Load()
    {
        try
        {
            using WinRegistryKey? key = WinRegistry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null)
                return;

            Enabled = Convert.ToInt32(key.GetValue("Enabled", 0)) != 0;
            SetPrefixes(Convert.ToString(key.GetValue("Prefixes", string.Empty)) ?? string.Empty);
            AutoRefresh = Convert.ToInt32(key.GetValue("AutoRefresh", 1)) != 0;
            Mode = Enum.TryParse(Convert.ToString(key.GetValue("Mode", "Ocultar")), true, out PlanDisplayMode mode)
                ? mode
                : PlanDisplayMode.Ocultar;
        }
        catch
        {
            // Sem acesso ao registro: segue com o padrao (desligado).
        }
    }

    public static void Save()
    {
        try
        {
            using WinRegistryKey key = WinRegistry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("Enabled", Enabled ? 1 : 0, WinRegistryValueKind.DWord);
            key.SetValue("Prefixes", PrefixesText, WinRegistryValueKind.String);
            key.SetValue("Mode", Mode.ToString(), WinRegistryValueKind.String);
            key.SetValue("AutoRefresh", AutoRefresh ? 1 : 0, WinRegistryValueKind.DWord);
        }
        catch
        {
            // A configuracao continua valendo na sessao atual.
        }
    }

    public static bool Matches(string? layer)
    {
        if (string.IsNullOrEmpty(layer))
            return false;

        foreach (string prefix in Prefixes)
        {
            if (prefix == AllPrefix || layer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Indice por desenho, montado e mantido fora da regeneracao (thread principal)
/// e consultado durante o desenho sem abrir nenhum objeto do DWG:
/// - layers-alvo (por ObjectId; entity.Layer abriria a tabela de layers);
/// - definicoes de bloco inseridas em layer-alvo (seus solidos sao alvos);
/// - blocos de layout (model/paper space), para distinguir solidos soltos de
///   solidos dentro de blocos.
/// </summary>
internal static class PlanLayerIndex
{
    private sealed class Snapshot
    {
        public HashSet<AcadDb.ObjectId> Layers { get; init; } = new();
        public HashSet<AcadDb.ObjectId> MatchedBlocks { get; init; } = new();
        public HashSet<AcadDb.ObjectId> Layouts { get; init; } = new();
    }

    private static readonly object Sync = new();

    // Copy-on-write: leitores (qualquer thread) nunca veem um conjunto em alteracao.
    private static Dictionary<AcadDb.Database, Snapshot> _map = new();

    private static Snapshot? Get(AcadDb.ObjectId anyId)
    {
        AcadDb.Database? db = anyId.Database;
        if (db is null)
            return null;

        return Volatile.Read(ref _map).TryGetValue(db, out Snapshot? snapshot) ? snapshot : null;
    }

    /// <summary>Layer casa com os prefixos. Seguro em qualquer thread.</summary>
    public static bool Contains(AcadDb.ObjectId layerId)
    {
        if (layerId.IsNull)
            return false;

        if (PlanVisibilitySettings.MatchesAll)
            return true;

        return Get(layerId)?.Layers.Contains(layerId) == true;
    }

    /// <summary>A definicao de bloco dona do objeto esta inserida em layer-alvo.</summary>
    public static bool IsMatchedBlock(AcadDb.ObjectId ownerId) =>
        !ownerId.IsNull && Get(ownerId)?.MatchedBlocks.Contains(ownerId) == true;

    /// <summary>True se o objeto esta dentro de um bloco (dono nao e model/paper space).</summary>
    public static bool IsNested(AcadDb.ObjectId ownerId)
    {
        Snapshot? snapshot = ownerId.IsNull ? null : Get(ownerId);
        return snapshot is not null && snapshot.Layouts.Count > 0 && !snapshot.Layouts.Contains(ownerId);
    }

    /// <summary>Remonta o indice de um desenho (thread principal, fora da regeneracao).</summary>
    public static void Rebuild(AcadDb.Database db)
    {
        Snapshot snapshot = new();

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

            AcadDb.LayerTable layers = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);
            foreach (AcadDb.ObjectId id in layers)
            {
                if (!id.IsErased &&
                    tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.LayerTableRecord layer &&
                    PlanVisibilitySettings.Matches(layer.Name))
                {
                    snapshot.Layers.Add(id);
                }
            }

            AcadDb.BlockTable blocks = (AcadDb.BlockTable)tr.GetObject(db.BlockTableId, AcadDb.OpenMode.ForRead);
            foreach (AcadDb.ObjectId id in blocks)
            {
                if (!id.IsErased &&
                    tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.BlockTableRecord btr &&
                    btr.IsLayout)
                {
                    snapshot.Layouts.Add(id);
                }
            }

            AcadDb.BlockTableRecord modelSpace = (AcadDb.BlockTableRecord)tr.GetObject(
                blocks[AcadDb.BlockTableRecord.ModelSpace],
                AcadDb.OpenMode.ForRead);

            foreach (AcadDb.ObjectId id in modelSpace)
            {
                if (id.IsErased ||
                    !id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(AcadDb.BlockReference))) ||
                    tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockReference reference ||
                    !PlanVisibilitySettings.Matches(reference.Layer))
                {
                    continue;
                }

                AddBlockRecursive(tr, reference.BlockTableRecord, snapshot.MatchedBlocks, 0);
            }
        }
        catch
        {
            // Indice parcial: o que nao foi lido simplesmente nao e tratado.
        }

        lock (Sync)
            Publish(db, snapshot);
    }

    private static void AddBlockRecursive(
        AcadDb.Transaction tr,
        AcadDb.ObjectId blockId,
        HashSet<AcadDb.ObjectId> result,
        int depth)
    {
        if (depth > 16 || !result.Add(blockId))
            return;

        if (tr.GetObject(blockId, AcadDb.OpenMode.ForRead, false) is not AcadDb.BlockTableRecord definition)
            return;

        foreach (AcadDb.ObjectId id in definition)
        {
            if (!id.IsErased &&
                id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(AcadDb.BlockReference))) &&
                tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.BlockReference inner)
            {
                AddBlockRecursive(tr, inner.BlockTableRecord, result, depth + 1);
            }
        }
    }

    /// <summary>Atualiza um layer criado/renomeado/apagado (eventos do banco, thread principal).</summary>
    public static void Update(AcadDb.LayerTableRecord layer, bool erased)
    {
        AcadDb.Database? db = layer.Database;
        if (db is null)
            return;

        AcadDb.ObjectId id = layer.ObjectId;
        bool matches = !erased && PlanVisibilitySettings.Matches(layer.Name);

        lock (Sync)
        {
            Snapshot current = _map.TryGetValue(db, out Snapshot? existing) ? existing : new Snapshot();
            if (current.Layers.Contains(id) == matches)
                return;

            HashSet<AcadDb.ObjectId> copy = new(current.Layers);
            if (matches)
                copy.Add(id);
            else
                copy.Remove(id);

            Publish(db, new Snapshot { Layers = copy, MatchedBlocks = current.MatchedBlocks, Layouts = current.Layouts });
        }
    }

    /// <summary>Uma insercao em layer-alvo foi criada/alterada: seus solidos passam a ser alvos.</summary>
    public static void AddMatchedBlock(AcadDb.Database db, AcadDb.ObjectId blockId)
    {
        lock (Sync)
        {
            Snapshot current = _map.TryGetValue(db, out Snapshot? existing) ? existing : new Snapshot();
            if (current.MatchedBlocks.Contains(blockId))
                return;

            HashSet<AcadDb.ObjectId> copy = new(current.MatchedBlocks) { blockId };
            Publish(db, new Snapshot { Layers = current.Layers, MatchedBlocks = copy, Layouts = current.Layouts });
        }
    }

    public static void Remove(AcadDb.Database db)
    {
        lock (Sync)
        {
            if (!_map.ContainsKey(db))
                return;

            Dictionary<AcadDb.Database, Snapshot> copy = new(_map);
            copy.Remove(db);
            Volatile.Write(ref _map, copy);
        }
    }

    private static void Publish(AcadDb.Database db, Snapshot snapshot)
    {
        Dictionary<AcadDb.Database, Snapshot> copy = new(_map)
        {
            [db] = snapshot
        };

        Volatile.Write(ref _map, copy);
    }
}

/// <summary>
/// DrawableOverrule para Solid3d, Body e insercoes de bloco.
///
/// - Estilos visuais 3D (qualquer regeneracao que nao seja 2D Wireframe):
///   o plugin NAO interfere; o AutoCAD desenha o solido nativamente.
/// - 2D Wireframe, vista de planta: oculto (Ocultar) ou so as arestas visiveis
///   pre-calculadas (Exato).
/// - 2D Wireframe, vista nao-planta (ex.: orbita em 2D Wireframe): as arestas
///   do solido sao desenhadas a partir de um cache (wireframe equivalente).
///
/// Regras de seguranca (o overrule roda dentro do motor grafico nativo):
/// - nenhum objeto e redesenhado de dentro do proprio desenho (causa de crash
///   na orbita em versoes anteriores);
/// - nada do DWG e aberto durante o desenho: indices e caches em memoria,
///   preenchidos no Idle;
/// - nenhuma excecao escapa para o codigo nativo.
/// </summary>
internal sealed class PlanVisibilityOverrule : AcGi.DrawableOverrule
{
    /// <summary>Cosseno da tolerancia angular para considerar a vista "de cima" (~1 grau).</summary>
    private const double PlanCosTolerance = 0.99985;

    public override int SetAttributes(AcGi.Drawable drawable, AcGi.DrawableTraits traits)
    {
        int flags = 0;

        try
        {
            flags = base.SetAttributes(drawable, traits);

            if (IsTarget(drawable))
                flags |= (int)AcGi.DrawableAttributes.ViewDependentViewportDraw;
        }
        catch
        {
            // Mantem os atributos obtidos ate aqui.
        }

        return flags;
    }

    public override bool WorldDraw(AcGi.Drawable drawable, AcGi.WorldDraw wd)
    {
        try
        {
            if (!IsTarget(drawable) || wd.RegenType != AcGi.RegenType.StandardDisplay)
                return base.WorldDraw(drawable, wd);

            if (drawable is AcadDb.BlockReference)
            {
                // Conteudo do bloco normalmente; false pede o ViewportDraw, onde
                // entram as linhas de planta e as partes dependentes da vista.
                base.WorldDraw(drawable, wd);
                return false;
            }

            // Solido em 2D Wireframe: o ViewportDraw decide conforme a vista.
            return false;
        }
        catch
        {
            return true; // em erro, nao desenha (nunca derruba o AutoCAD)
        }
    }

    public override void ViewportDraw(AcGi.Drawable drawable, AcGi.ViewportDraw vd)
    {
        try
        {
            if (!IsTarget(drawable) || vd.RegenType != AcGi.RegenType.StandardDisplay)
            {
                base.ViewportDraw(drawable, vd);
                return;
            }

            bool plan = IsPlanView(vd);

            if (drawable is AcadDb.BlockReference reference)
            {
                // Partes dependentes da vista do conteudo (inclui os solidos internos,
                // que se tratam sozinhos neste mesmo overrule).
                base.ViewportDraw(drawable, vd);

                if (plan && PlanRepresentationStore.TryGet(reference) is List<Point3d[]> blockLines)
                    DrawLines(vd, blockLines);

                return;
            }

            if (drawable is not AcadDb.Entity entity)
                return;

            if (plan)
            {
                if (PlanVisibilitySettings.Mode == PlanDisplayMode.Ocultar)
                    return;

                // Exato: solido dentro de bloco e representado pela insercao.
                if (PlanLayerIndex.IsNested(entity.OwnerId))
                    return;

                if (PlanRepresentationStore.TryGet(entity) is List<Point3d[]> lines)
                {
                    DrawLines(vd, lines);
                    return;
                }

                // Sem representacao (pendente): aparece completo, em arestas.
            }

            if (PlanRepresentationStore.TryGetWire(entity) is List<Point3d[]> wire)
                DrawLines(vd, wire);
        }
        catch
        {
            // Em erro, nao desenha este objeto nesta vista (nunca derruba o AutoCAD).
        }
    }

    private static void DrawLines(AcGi.ViewportDraw vd, List<Point3d[]> lines)
    {
        AcGi.Geometry geometry = vd.Geometry;

        foreach (Point3d[] line in lines)
        {
            for (int i = 0; i + 1 < line.Length; i++)
                geometry.WorldLine(line[i], line[i + 1]);
        }
    }

    private static bool IsTarget(AcGi.Drawable drawable)
    {
        if (!PlanVisibilitySettings.Enabled || drawable is not AcadDb.Entity entity)
            return false;

        if (entity is AcadDb.BlockReference reference)
        {
            // So insercoes com linhas de planta calculadas (modo Exato).
            return PlanVisibilitySettings.Mode == PlanDisplayMode.Exato &&
                   PlanRepresentationStore.TryGet(reference) is not null;
        }

        return (entity is AcadDb.Solid3d || entity is AcadDb.Body) &&
               (PlanLayerIndex.Contains(entity.LayerId) || PlanLayerIndex.IsMatchedBlock(entity.OwnerId));
    }

    private static bool IsPlanView(AcGi.ViewportDraw vd)
    {
        try
        {
            Vector3d toEye = vd.Viewport.CameraLocation - vd.Viewport.CameraTarget;
            if (toEye.Length <= 1e-12)
                return false;

            return toEye.GetNormal().DotProduct(Vector3d.ZAxis) >= PlanCosTolerance;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Registra/remove o overrule conforme a configuracao.</summary>
internal static class PlanVisibilityManager
{
    private static PlanVisibilityOverrule? _overrule;

    private static readonly Type[] TargetTypes =
    {
        typeof(AcadDb.Solid3d),
        typeof(AcadDb.Body), // links extraidos do corredor saem como Body
        typeof(AcadDb.BlockReference) // solidos dentro de blocos
    };

    public static bool IsActive => _overrule is not null;

    public static void Initialize()
    {
        PlanVisibilitySettings.Load();
        PlanRepresentationStore.Initialize();
        Apply();
    }

    public static void Terminate()
    {
        PlanRepresentationStore.Terminate();
        Unregister();
    }

    /// <summary>Liga ou desliga o overrule de acordo com Enabled e Prefixes.</summary>
    public static void Apply()
    {
        bool shouldRun = PlanVisibilitySettings.Enabled && PlanVisibilitySettings.Prefixes.Count > 0;

        // Prefixos podem ter mudado: remonta o indice de layers de todos os desenhos.
        try
        {
            foreach (Autodesk.AutoCAD.ApplicationServices.Document doc in Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager)
                PlanLayerIndex.Rebuild(doc.Database);
        }
        catch
        {
            // Sem documentos acessiveis agora.
        }

        if (shouldRun && _overrule is null)
            Register();
        else if (!shouldRun && _overrule is not null)
            Unregister();
    }

    private static void Register()
    {
        _overrule = new PlanVisibilityOverrule();

        foreach (Type type in TargetTypes)
            Overrule.AddOverrule(RXObject.GetClass(type), _overrule, false);

        Overrule.Overruling = true;
    }

    private static void Unregister()
    {
        if (_overrule is null)
            return;

        foreach (Type type in TargetTypes)
        {
            try
            {
                Overrule.RemoveOverrule(RXObject.GetClass(type), _overrule);
            }
            catch
            {
                // Ja removido.
            }
        }

        _overrule = null;
    }
}
