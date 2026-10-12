using System;
using System.Collections.Generic;
using System.Linq;
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
/// DrawableOverrule para Solid3d e Body: nos layers configurados, em viewports
/// que olham de cima (planta) o objeto nao e desenhado (modo Ocultar) ou e
/// desenhado so com as arestas visiveis pre-calculadas (modo Exato, mesmo em
/// 2D Wireframe e na plotagem); em vistas 3D e desenhado normalmente. Funciona por viewport, inclusive em
/// viewports lado a lado no model space. Nao altera o DWG.
///
/// Mecanica:
/// - SetAttributes marca o objeto como dependente da vista;
/// - WorldDraw devolve false (sem desenhar) para forcar o ViewportDraw;
/// - ViewportDraw verifica a direcao da camera: em planta nao desenha nada;
///   nas demais vistas desenha o proprio objeto via Geometry.Draw, com um
///   contador de reentrada para que esse desenho aninhado use o WorldDraw
///   original do AutoCAD.
/// </summary>
internal sealed class PlanVisibilityOverrule : AcGi.DrawableOverrule
{
    /// <summary>Cosseno da tolerancia angular para considerar a vista "de cima" (~1 grau).</summary>
    private const double PlanCosTolerance = 0.99985;

    [ThreadStatic]
    private static int _nesting;

    public override int SetAttributes(AcGi.Drawable drawable, AcGi.DrawableTraits traits)
    {
        int flags = base.SetAttributes(drawable, traits);

        if (_nesting == 0 && IsTarget(drawable))
            flags |= (int)AcGi.DrawableAttributes.ViewDependentViewportDraw;

        return flags;
    }

    public override bool WorldDraw(AcGi.Drawable drawable, AcGi.WorldDraw wd)
    {
        if (_nesting > 0 || !IsTarget(drawable))
            return base.WorldDraw(drawable, wd);

        // false: o AutoCAD chama ViewportDraw, onde a vista e conhecida.
        return false;
    }

    public override void ViewportDraw(AcGi.Drawable drawable, AcGi.ViewportDraw vd)
    {
        if (_nesting > 0 || !IsTarget(drawable))
        {
            base.ViewportDraw(drawable, vd);
            return;
        }

        if (IsPlanView(vd))
        {
            if (PlanVisibilitySettings.Mode == PlanDisplayMode.Ocultar)
                return;

            // Exato: linhas visiveis pre-calculadas. Sem representacao (ou
            // pendente de atualizacao), desenha o solido normalmente.
            if (drawable is AcadDb.Entity entity &&
                PlanRepresentationStore.TryGet(entity) is List<Point3d[]> lines)
            {
                foreach (Point3d[] line in lines)
                    vd.Geometry.Polyline(new Point3dCollection(line), Vector3d.ZAxis, IntPtr.Zero);

                return;
            }
        }

        _nesting++;
        try
        {
            vd.Geometry.Draw(drawable);
        }
        finally
        {
            _nesting--;
        }
    }

    private static bool IsTarget(AcGi.Drawable drawable)
    {
        try
        {
            return PlanVisibilitySettings.Enabled &&
                   drawable is AcadDb.Entity entity &&
                   PlanVisibilitySettings.Matches(entity.Layer);
        }
        catch
        {
            return false;
        }
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
        typeof(AcadDb.Body) // links extraidos do corredor saem como Body
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
