using System;
using System.Collections.Concurrent;
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
/// Indice dos layers-alvo por ObjectId, por desenho. Montado e mantido fora
/// da regeneracao (thread principal); consultado durante o desenho sem abrir
/// nenhum objeto do DWG (entity.Layer abriria a tabela de layers, o que nao e
/// seguro nas threads de graficos, ex.: durante a orbita 3D).
/// </summary>
internal static class PlanLayerIndex
{
    private static readonly object Sync = new();

    // Copy-on-write: leitores (qualquer thread) nunca veem um conjunto em alteracao.
    private static Dictionary<AcadDb.Database, HashSet<AcadDb.ObjectId>> _map = new();

    /// <summary>Seguro em qualquer thread.</summary>
    public static bool Contains(AcadDb.ObjectId layerId)
    {
        if (layerId.IsNull)
            return false;

        if (PlanVisibilitySettings.MatchesAll)
            return true;

        AcadDb.Database? db = layerId.Database;
        Dictionary<AcadDb.Database, HashSet<AcadDb.ObjectId>> map = Volatile.Read(ref _map);

        return db is not null && map.TryGetValue(db, out HashSet<AcadDb.ObjectId>? set) && set.Contains(layerId);
    }

    /// <summary>Remonta o indice de um desenho (thread principal, fora da regeneracao).</summary>
    public static void Rebuild(AcadDb.Database db)
    {
        HashSet<AcadDb.ObjectId> set = new();

        try
        {
            using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            AcadDb.LayerTable table = (AcadDb.LayerTable)tr.GetObject(db.LayerTableId, AcadDb.OpenMode.ForRead);

            foreach (AcadDb.ObjectId id in table)
            {
                if (id.IsErased)
                    continue;

                if (tr.GetObject(id, AcadDb.OpenMode.ForRead, false) is AcadDb.LayerTableRecord layer &&
                    PlanVisibilitySettings.Matches(layer.Name))
                {
                    set.Add(id);
                }
            }
        }
        catch
        {
            // Indice vazio: nada e tratado neste desenho ate a proxima remontagem.
        }

        Replace(db, set);
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
            HashSet<AcadDb.ObjectId> current = _map.TryGetValue(db, out HashSet<AcadDb.ObjectId>? existing)
                ? existing
                : new HashSet<AcadDb.ObjectId>();

            if (current.Contains(id) == matches)
                return;

            HashSet<AcadDb.ObjectId> copy = new(current);
            if (matches)
                copy.Add(id);
            else
                copy.Remove(id);

            ReplaceLocked(db, copy);
        }
    }

    public static void Remove(AcadDb.Database db)
    {
        lock (Sync)
        {
            if (!_map.ContainsKey(db))
                return;

            Dictionary<AcadDb.Database, HashSet<AcadDb.ObjectId>> copy = new(_map);
            copy.Remove(db);
            Volatile.Write(ref _map, copy);
        }
    }

    private static void Replace(AcadDb.Database db, HashSet<AcadDb.ObjectId> set)
    {
        lock (Sync)
            ReplaceLocked(db, set);
    }

    private static void ReplaceLocked(AcadDb.Database db, HashSet<AcadDb.ObjectId> set)
    {
        Dictionary<AcadDb.Database, HashSet<AcadDb.ObjectId>> copy = new(_map)
        {
            [db] = set
        };

        Volatile.Write(ref _map, copy);
    }
}

/// <summary>
/// DrawableOverrule para Solid3d, Body e insercoes de bloco: nos layers
/// configurados, em viewports que olham de cima (planta) o objeto nao e
/// desenhado (modo Ocultar) ou e desenhado so com as arestas visiveis
/// pre-calculadas (modo Exato, mesmo em 2D Wireframe e na plotagem); em vistas
/// 3D e desenhado normalmente. Funciona por viewport. Nao altera o DWG.
///
/// Regras de seguranca (o overrule roda dentro do motor grafico nativo):
/// - nenhuma excecao pode escapar: um erro vira "nao desenhar", nunca um crash;
/// - nada do DWG e aberto durante o desenho: layers por ObjectId (PlanLayerIndex)
///   e representacoes pelo cache em memoria (PlanRepresentationStore);
/// - o "desenhar normalmente" redesenha o proprio objeto; a trava contra
///   recursao e por objeto nativo e vale entre threads.
/// </summary>
internal sealed class PlanVisibilityOverrule : AcGi.DrawableOverrule
{
    /// <summary>Cosseno da tolerancia angular para considerar a vista "de cima" (~1 grau).</summary>
    private const double PlanCosTolerance = 0.99985;

    // Objetos nativos que estamos redesenhando "normalmente" agora (qualquer thread).
    private static readonly ConcurrentDictionary<IntPtr, int> SelfDrawing = new();

    // Enquanto um bloco desenha o proprio conteudo em planta, os solidos internos
    // ficam suprimidos (ja estao representados pelas linhas visiveis do bloco).
    [ThreadStatic]
    private static int _suppressDepth;

    [ThreadStatic]
    private static bool _suppressAll;

    public override int SetAttributes(AcGi.Drawable drawable, AcGi.DrawableTraits traits)
    {
        int flags = 0;

        try
        {
            flags = base.SetAttributes(drawable, traits);

            if (!IsSelfDrawing(drawable) && IsTarget(drawable))
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
            if (IsSuppressedNestedSolid(drawable))
                return true; // nada a desenhar

            if (IsSelfDrawing(drawable) || !IsTarget(drawable))
                return base.WorldDraw(drawable, wd);

            // false: o AutoCAD chama ViewportDraw, onde a vista e conhecida.
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
            if (IsSuppressedNestedSolid(drawable))
                return;

            if (IsSelfDrawing(drawable) || !IsTarget(drawable))
            {
                base.ViewportDraw(drawable, vd);
                return;
            }

            if (IsPlanView(vd))
            {
                if (drawable is AcadDb.BlockReference reference)
                {
                    DrawBlockInPlan(reference, vd);
                    return;
                }

                if (PlanVisibilitySettings.Mode == PlanDisplayMode.Ocultar)
                    return;

                // Exato: linhas visiveis pre-calculadas. Sem representacao (ou
                // pendente de atualizacao), desenha o solido normalmente.
                if (drawable is AcadDb.Entity entity &&
                    PlanRepresentationStore.TryGet(entity) is List<Point3d[]> lines)
                {
                    DrawLines(vd, lines);
                    return;
                }
            }

            DrawNormally(drawable, vd);
        }
        catch
        {
            // Em erro, nao desenha este objeto nesta vista (nunca derruba o AutoCAD).
        }
    }

    /// <summary>
    /// Bloco em planta: linhas visiveis dos solidos internos (modo Exato) e o
    /// restante do conteudo do bloco normalmente, com os solidos suprimidos.
    /// </summary>
    private static void DrawBlockInPlan(AcadDb.BlockReference reference, AcGi.ViewportDraw vd)
    {
        if (PlanVisibilitySettings.Mode == PlanDisplayMode.Exato)
        {
            if (PlanRepresentationStore.TryGet(reference) is not List<Point3d[]> lines)
            {
                DrawNormally(reference, vd);
                return;
            }

            DrawLines(vd, lines);
        }

        bool previousAll = _suppressAll;
        _suppressAll = PlanLayerIndex.Contains(reference.LayerId);
        _suppressDepth++;

        try
        {
            DrawNormally(reference, vd);
        }
        finally
        {
            _suppressDepth--;
            _suppressAll = previousAll;
        }
    }

    private static void DrawNormally(AcGi.Drawable drawable, AcGi.ViewportDraw vd)
    {
        IntPtr key = drawable.UnmanagedObject;
        SelfDrawing.AddOrUpdate(key, 1, (_, count) => count + 1);

        try
        {
            vd.Geometry.Draw(drawable);
        }
        finally
        {
            if (SelfDrawing.AddOrUpdate(key, 0, (_, count) => count - 1) <= 0)
                SelfDrawing.TryRemove(key, out _);
        }
    }

    private static bool IsSelfDrawing(AcGi.Drawable drawable) =>
        SelfDrawing.TryGetValue(drawable.UnmanagedObject, out int count) && count > 0;

    private static void DrawLines(AcGi.ViewportDraw vd, List<Point3d[]> lines)
    {
        AcGi.Geometry geometry = vd.Geometry;

        foreach (Point3d[] line in lines)
        {
            for (int i = 0; i + 1 < line.Length; i++)
                geometry.WorldLine(line[i], line[i + 1]);
        }
    }

    private static bool IsSuppressedNestedSolid(AcGi.Drawable drawable)
    {
        if (_suppressDepth == 0)
            return false;

        return drawable is AcadDb.Entity entity &&
               (entity is AcadDb.Solid3d || entity is AcadDb.Body) &&
               (_suppressAll || PlanLayerIndex.Contains(entity.LayerId));
    }

    private static bool IsTarget(AcGi.Drawable drawable)
    {
        if (!PlanVisibilitySettings.Enabled || drawable is not AcadDb.Entity entity)
            return false;

        if (entity is AcadDb.BlockReference reference)
        {
            // Exato: so blocos com representacao calculada (contem solidos).
            // Ocultar: blocos cujo layer casa; com "*", os solidos internos
            // ja se ocultam sozinhos e nao e preciso tocar em todos os blocos.
            return PlanVisibilitySettings.Mode == PlanDisplayMode.Exato
                ? PlanRepresentationStore.TryGet(reference) is not null
                : !PlanVisibilitySettings.MatchesAll && PlanLayerIndex.Contains(reference.LayerId);
        }

        return PlanLayerIndex.Contains(entity.LayerId);
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
