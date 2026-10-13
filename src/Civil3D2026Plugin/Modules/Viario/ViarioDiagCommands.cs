using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>
/// C3DVIARIODIAG: lista por reflexao o que a API do Civil 3D instalado expoe
/// para alinhamentos, perfis (offset?), intersecoes e curb returns.
/// Somente leitura: nao altera o desenho. Gera um .txt na pasta temporaria.
/// </summary>
public sealed class ViarioDiagCommands
{
    private static readonly string[] MainTypes =
    {
        "Autodesk.Civil.DatabaseServices.Profile",
        "Autodesk.Civil.DatabaseServices.Alignment",
        "Autodesk.Civil.DatabaseServices.OffsetAlignmentInfo",
        "Autodesk.Civil.DatabaseServices.Intersection",
        "Autodesk.Civil.DatabaseServices.ProfilePVICollection",
        "Autodesk.Civil.DatabaseServices.ProfileEntityCollection",
        "Autodesk.Civil.DatabaseServices.AlignmentEntityCollection",
        "Autodesk.Civil.DatabaseServices.FeatureLine",
        "Autodesk.Civil.DatabaseServices.Site",
        "Autodesk.Civil.DatabaseServices.Profile+OffsetProfileParameters",
        "Autodesk.Civil.DatabaseServices.Profile+OffsetProfileParametersStation",
        "Autodesk.Civil.DatabaseServices.ProfilePVI",
        "Autodesk.Civil.DatabaseServices.ConnectedAlignmentParams",
        "Autodesk.Civil.DatabaseServices.ConnectedAlignmentInfo",
        "Autodesk.Civil.DatabaseServices.AlignmentRegion",
        "Autodesk.Civil.DatabaseServices.AlignmentRegionCollection",
        "Autodesk.Civil.DatabaseServices.AlignmentTransition",
        "Autodesk.Civil.DatabaseServices.AlignmentTransitionCollection",
        "Autodesk.Civil.DatabaseServices.TransitionDescription",
        "Autodesk.Civil.DatabaseServices.OffsetAlignmentWideningCriteria",
        "Autodesk.Civil.DatabaseServices.IntersectionRoad",
        "Autodesk.Civil.DatabaseServices.IntersectionRegion",
        "Autodesk.Civil.DatabaseServices.IntersectionRoadCollection",
        "Autodesk.Civil.DatabaseServices.IntersectionRegionCollection",
        "Autodesk.Civil.Settings.SettingsCmdCreateIntersection+SettingsCmdCurbReturnParameters",
        "Autodesk.Civil.Settings.SettingsCmdCreateIntersection+SettingsCmdCurbReturnProfileRules",
    };

    private static readonly string[] Keywords =
    {
        "Offset", "CurbReturn", "Intersection", "Superimpos", "Widening", "ProfileType", "AlignmentType", "Linked", "Link", "Connect", "CrossSlope"
    };

    [CommandMethod(C3DCommands.Viario.Diag, CommandFlags.Modal)]
    public void Diag()
    {
        Document? doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;

        StringBuilder sb = new();
        try
        {
            Build(sb);
        }
        catch (System.Exception ex)
        {
            sb.AppendLine("ERRO: " + ex);
        }

        string path = Path.Combine(Path.GetTempPath(), "C3DVIARIODIAG.txt");
        try
        {
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\n  Nao consegui gravar o arquivo: " + ex.Message);
            path = string.Empty;
        }

        doc.Editor.WriteMessage("\n" + sb);
        if (path.Length > 0)
            doc.Editor.WriteMessage($"\n  Diagnostico salvo em: {path}\n  Envie esse arquivo (ou o texto acima).");
    }

    private static void Build(StringBuilder sb)
    {
        sb.AppendLine("=== C3DVIARIODIAG ===");
        sb.AppendLine("Data: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        List<Assembly> civil = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => IsCivil(a.GetName().Name))
            .OrderBy(a => a.GetName().Name)
            .ToList();

        sb.AppendLine();
        sb.AppendLine("--- Assemblies Civil carregados ---");
        foreach (Assembly a in civil)
            sb.AppendLine($"{a.GetName().Name} {a.GetName().Version} | {SafeLocation(a)}");

        List<Type> all = civil.SelectMany(SafeTypes).Where(t => t.IsPublic || t.IsNestedPublic).ToList();

        // Tipos principais em detalhe.
        foreach (string name in MainTypes)
        {
            Type? t = all.FirstOrDefault(x => x.FullName == name)
                ?? all.FirstOrDefault(x => x.FullName is not null && x.FullName.EndsWith("." + name.Split('.').Last()));
            sb.AppendLine();
            sb.AppendLine($"--- {name} ---");
            if (t is null)
            {
                sb.AppendLine("  (nao encontrado)");
                continue;
            }

            DumpType(sb, t);
        }

        // Enums relevantes completos.
        sb.AppendLine();
        sb.AppendLine("--- Enums relevantes ---");
        foreach (Type e in all.Where(t => t.IsEnum && (t.Name.Contains("Profile") || t.Name.Contains("Alignment") || t.Name.Contains("Intersection") || t.Name.Contains("Offset") || t.Name.Contains("CurbReturn") || t.Name.Contains("Connect") || t.Name.Contains("Transition") || t.Name.Contains("Region") || t.Name.Contains("Slope"))).OrderBy(t => t.FullName))
            sb.AppendLine($"{e.FullName}: {string.Join(", ", Enum.GetNames(e))}");

        // Qualquer tipo com nome-chave.
        sb.AppendLine();
        sb.AppendLine("--- Tipos com nomes-chave (Offset, CurbReturn, Intersection, Superimposed, Widening, Link) ---");
        foreach (Type t in all.Where(t => Keywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.FullName))
            sb.AppendLine($"{Kind(t)} {t.FullName}");

        // Membros com nome-chave em qualquer tipo de DatabaseServices (para achar metodos escondidos).
        sb.AppendLine();
        sb.AppendLine("--- Membros com nomes-chave em Autodesk.Civil.DatabaseServices ---");
        foreach (Type t in all.Where(t => t.Namespace == "Autodesk.Civil.DatabaseServices").OrderBy(t => t.Name))
        {
            foreach (MemberInfo m in SafeMembers(t))
            {
                if (m is MethodInfo mi && mi.IsSpecialName)
                    continue;
                if (!Keywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;
                sb.AppendLine($"{t.Name}.{Describe(m)}");
            }
        }

        // Interop COM (se carregado).
        sb.AppendLine();
        sb.AppendLine("--- COM interop (AeccX*) carregados ---");
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? string.Empty).StartsWith("Autodesk.AECC.Interop", StringComparison.OrdinalIgnoreCase) || (a.GetName().Name ?? string.Empty).Contains("AeccX", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine($"{a.GetName().Name} {a.GetName().Version}");
            foreach (Type t in SafeTypes(a).Where(t => Keywords.Any(k => t.Name.Contains(k, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.FullName))
                sb.AppendLine($"  {Kind(t)} {t.FullName}");
        }
    }

    private static void DumpType(StringBuilder sb, Type t)
    {
        sb.AppendLine($"  Base: {t.BaseType?.FullName}");

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (ConstructorInfo c in t.GetConstructors())
            sb.AppendLine("  ctor(" + Params(c.GetParameters()) + ")");

        foreach (MethodInfo m in t.GetMethods(Flags).Where(m => !m.IsSpecialName).OrderBy(m => !m.IsStatic).ThenBy(m => m.Name))
            sb.AppendLine("  " + Describe(m));

        foreach (PropertyInfo p in t.GetProperties(Flags).OrderBy(p => p.Name))
            sb.AppendLine("  " + Describe(p));
    }

    private static string Describe(MemberInfo m) => m switch
    {
        MethodInfo mi => $"{(mi.IsStatic ? "static " : string.Empty)}{Short(mi.ReturnType)} {mi.Name}({Params(mi.GetParameters())})",
        PropertyInfo pi => $"prop {Short(pi.PropertyType)} {pi.Name} {{{(pi.CanRead ? " get;" : string.Empty)}{(pi.CanWrite && pi.SetMethod?.IsPublic == true ? " set;" : string.Empty)} }}",
        FieldInfo fi => $"field {Short(fi.FieldType)} {fi.Name}",
        EventInfo ei => $"event {ei.Name}",
        _ => m.Name
    };

    private static string Params(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p => $"{(p.IsOut ? "out " : string.Empty)}{Short(p.ParameterType)} {p.Name}"));

    private static string Short(Type t)
    {
        if (t.IsGenericType)
            return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
        return t.Name;
    }

    private static string Kind(Type t) => t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";

    private static bool IsCivil(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return name.StartsWith("AeccDb", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AecBase", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Autodesk.Civil", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AeccPressurePipes", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AeccDbMgd", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeLocation(Assembly a)
    {
        try { return a.Location; } catch { return "?"; }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
        catch { return Array.Empty<Type>(); }
    }

    private static IEnumerable<MemberInfo> SafeMembers(Type t)
    {
        try { return t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly); }
        catch { return Array.Empty<MemberInfo>(); }
    }
}
