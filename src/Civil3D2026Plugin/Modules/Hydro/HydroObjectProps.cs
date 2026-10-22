using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Civil3D2026Plugin.Infrastructure;
using Civil3D2026Plugin.Modules.Hydro.Calc;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Uma propriedade do objeto nativo (estrutura ou tubo), lida por reflexao.</summary>
internal sealed class ObjProp
{
    public string Key { get; init; } = "";
    public string Group { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";

    /// <summary>num, ang, int, bool, text, enum, ref, point ou ro (so leitura).</summary>
    public string Kind { get; init; } = "ro";
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
}

/// <summary>
/// "Todas as propriedades" de uma peca da rede: le por reflexao toda propriedade publica do
/// objeto Civil (Structure/Pipe/Part e a base Civil) mais camada/cor/tipo de linha do AutoCAD,
/// e grava qualquer uma que tenha setter. Numeros aceitam valor, +0,10/-0,30 e expressao;
/// angulos em graus; referencias (estilo, superficie, alinhamento) pelo nome.
/// Leitura sem LockDocument (OpenClose); escrita so dentro de HYAPLICAR.
/// </summary>
internal static class HydroObjectProps
{
    private const string Module = "Hydro";

    private static readonly HashSet<string> AcadWhitelist = new(StringComparer.Ordinal)
    {
        "Layer", "ColorIndex", "Linetype", "LinetypeScale", "LineWeight", "Visible", "Material"
    };

    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal)
    {
        "PartData", "Handle", "ObjectId", "OwnerId", "Id", "Database", "ExtensionDictionary", "UnmanagedObject",
        "AutoDelete", "IsDisposed", "ClassID", "BlockId", "BlockName", "Bounds", "GeometricExtents", "Ecs",
        "IsWriteEnabled", "IsReadEnabled", "IsNotifyEnabled", "IsModified", "IsNewObject", "IsErased", "IsUndoing",
        "IsTransactionResident", "IsPersistent", "IsAProxy", "IsCancelling", "IsEraseStatusToggled", "IsModifiedXData",
        "IsModifiedGraphics", "IsNotifying", "IsReallyClosing", "HasSaveVersionOverride", "MergeStyle", "Drawable",
        "DrawableType", "Annotative", "HasFields", "XData", "AcadObject", "ReferencedObjectIds"
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Name"] = "Nome", ["Description"] = "Descricao", ["StyleId"] = "Estilo", ["StyleName"] = "Estilo (nome)",
        ["Layer"] = "Camada", ["ColorIndex"] = "Cor (indice ACI)", ["Linetype"] = "Tipo de linha", ["LinetypeScale"] = "Escala do tipo de linha",
        ["LineWeight"] = "Espessura de linha", ["Visible"] = "Visivel", ["Material"] = "Material",
        ["Rotation"] = "Rotacao [graus]", ["Position"] = "Posicao (X; Y; Z)", ["Location"] = "Posicao (X; Y; Z)",
        ["RimElevation"] = "Cota da tampa", ["SumpElevation"] = "Cota do fundo", ["SumpDepth"] = "Profundidade do poco (sump)",
        ["ControlSumpBy"] = "Controle do fundo", ["RefSurfaceId"] = "Superficie de referencia", ["RefAlignmentId"] = "Alinhamento de referencia",
        ["RuleSetStyleId"] = "Rule set", ["RuleSetStyleName"] = "Rule set (nome)", ["PartSizeName"] = "Tamanho", ["PartFamilyName"] = "Familia",
        ["StartPoint"] = "Ponta inicial (X; Y; Z)", ["EndPoint"] = "Ponta final (X; Y; Z)", ["FlowDirection"] = "Sentido do fluxo",
        ["FlowDirectionMethod"] = "Metodo do sentido", ["ManningCoefficient"] = "Manning", ["FlowRate"] = "Vazao [m3/s]",
        ["InnerDiameterOrWidth"] = "Diametro / largura interna", ["OuterDiameterOrWidth"] = "Diametro / largura externa",
        ["InnerHeight"] = "Altura interna", ["OuterHeight"] = "Altura externa", ["WallThickness"] = "Espessura da parede",
        ["FloorThickness"] = "Espessura do fundo", ["Height"] = "Altura", ["Length2D"] = "Comprimento 2D", ["Length3D"] = "Comprimento 3D",
        ["MinimumCover"] = "Recobrimento minimo", ["MaximumCover"] = "Recobrimento maximo", ["KnownFlow"] = "Vazao conhecida",
        ["StructureType"] = "Tipo da estrutura", ["BoundingShape"] = "Forma", ["SurfaceAdjustmentValue"] = "Ajuste sobre a superficie",
        ["AutomaticSurfaceAdjustment"] = "Ajustar a superficie automaticamente", ["HydraulicGradeLine"] = "HGL", ["EnergyGradeLine"] = "EGL"
    };

    private static string GroupOf(Type declaring) => declaring.Name switch
    {
        "Structure" => "Estrutura",
        "Pipe" => "Tubo",
        "Part" => "Peca da rede",
        _ when declaring.Namespace?.StartsWith("Autodesk.Civil", StringComparison.Ordinal) == true => "Objeto Civil",
        _ => "Desenho (camada, cor, linha)"
    };

    private static bool IsAngle(PropertyInfo p) =>
        p.PropertyType == typeof(double) && (p.Name.Contains("Rotation", StringComparison.Ordinal) || p.Name.Contains("Angle", StringComparison.Ordinal));

    private static IEnumerable<PropertyInfo> Candidates(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true } && !Skip.Contains(p.Name))
        .Where(p => p.DeclaringType?.Namespace?.StartsWith("Autodesk.Civil", StringComparison.Ordinal) == true || AcadWhitelist.Contains(p.Name))
        .GroupBy(p => p.Name).Select(g => g.First());

    private static bool Supported(Type t) =>
        t == typeof(double) || t == typeof(int) || t == typeof(short) || t == typeof(long) || t == typeof(bool) || t == typeof(string) ||
        t.IsEnum || t == typeof(AcadDb.ObjectId) || t == typeof(Point3d) || t == typeof(Point2d) || t.IsValueType;

    // ------------------------------------------------------------------
    // Leitura
    // ------------------------------------------------------------------

    public static List<ObjProp> Read(AcadDb.Database db, string handle)
    {
        List<ObjProp> list = new();
        AcadDb.ObjectId? id = HydroReader.Id(db, handle);
        if (id is null)
            return list;
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(id.Value, AcadDb.OpenMode.ForRead, false) is not AcadDb.DBObject obj)
            return list;
        RefSource refs = new(db, tr);
        foreach (PropertyInfo p in Candidates(obj.GetType()))
        {
            Type t = p.PropertyType;
            if (!Supported(t))
                continue;
            object? value;
            try
            {
                value = p.GetValue(obj);
            }
            catch
            {
                // Propriedade que nao se aplica a esta peca (o Civil lanca excecao).
                continue;
            }

            bool writable = p.SetMethod is { IsPublic: true };
            string kind = !writable ? "ro"
                : IsAngle(p) ? "ang"
                : t == typeof(double) ? "num"
                : t == typeof(int) || t == typeof(short) || t == typeof(long) ? "int"
                : t == typeof(bool) ? "bool"
                : t == typeof(string) ? "text"
                : t.IsEnum ? "enum"
                : t == typeof(AcadDb.ObjectId) ? "ref"
                : t == typeof(Point3d) ? "point"
                : "ro";
            IReadOnlyList<string> choices = Array.Empty<string>();
            if (kind == "enum")
                choices = Enum.GetNames(t);
            else if (kind == "bool")
                choices = new[] { "Sim", "Nao" };
            else if (kind == "ref")
            {
                choices = refs.For(p.Name, value is AcadDb.ObjectId rid ? rid : AcadDb.ObjectId.Null).Select(c => c.Name).ToList();
                if (choices.Count == 0)
                    kind = "ro";
            }

            list.Add(new ObjProp
            {
                Key = p.Name,
                Group = GroupOf(p.DeclaringType!),
                Label = Labels.TryGetValue(p.Name, out string? l) ? $"{l}  ({p.Name})" : p.Name,
                Value = Format(value, IsAngle(p), tr),
                Kind = kind,
                Choices = choices
            });
        }

        string[] order = { "Estrutura", "Tubo", "Peca da rede", "Objeto Civil", "Desenho (camada, cor, linha)" };
        return list.OrderBy(x => Array.IndexOf(order, x.Group)).ThenBy(x => x.Kind == "ro" ? 1 : 0).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Medidas (PartData) de uma peca qualquer, lidas na hora (tubos nao vem no retrato).</summary>
    public static List<PartParam> ReadPartParams(AcadDb.Database db, string handle)
    {
        AcadDb.ObjectId? id = HydroReader.Id(db, handle);
        if (id is null)
            return new List<PartParam>();
        using AcadDb.Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        return tr.GetObject(id.Value, AcadDb.OpenMode.ForRead, false) is CivilDb.Part part ? HydroReader.ReadParameters(part) : new List<PartParam>();
    }

    private static string Format(object? v, bool angle, AcadDb.Transaction tr) => v switch
    {
        null => "",
        double d when angle => (d * 180 / Math.PI).ToString("0.####", CultureInfo.InvariantCulture),
        double d => double.IsNaN(d) ? "-" : d.ToString("0.####", CultureInfo.InvariantCulture),
        bool b => b ? "Sim" : "Nao",
        Point3d p => string.Format(CultureInfo.InvariantCulture, "{0:0.###}; {1:0.###}; {2:0.###}", p.X, p.Y, p.Z),
        Point2d p => string.Format(CultureInfo.InvariantCulture, "{0:0.###}; {1:0.###}", p.X, p.Y),
        AcadDb.ObjectId id => NameOf(id, tr),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    private static string NameOf(AcadDb.ObjectId id, AcadDb.Transaction tr)
    {
        if (id.IsNull || !id.IsValid || id.IsErased)
            return "(nenhum)";
        try
        {
            AcadDb.DBObject o = tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
            if (o.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) is string n && n.Length > 0)
                return n;
            return o.GetType().Name + " " + id.Handle;
        }
        catch
        {
            return id.Handle.ToString();
        }
    }

    // ------------------------------------------------------------------
    // Escrita
    // ------------------------------------------------------------------

    /// <summary>Grava a propriedade em cada peca; devolve o resumo (gravadas e recusadas com o motivo).</summary>
    public static string Write(AcadDb.Database db, IEnumerable<string> handles, string key, string text)
    {
        int ok = 0;
        List<string> errors = new();
        foreach (string h in handles)
        {
            try
            {
                WriteOne(db, h, key, text);
                ok++;
            }
            catch (Exception ex)
            {
                errors.Add(ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException.Message : ex.Message);
            }
        }

        string msg = $"{key}: {ok} peca(s) gravada(s)" + (errors.Count > 0 ? $"; {errors.Count} recusada(s): " + string.Join("; ", errors.Distinct().Take(4)) : "");
        PluginLog.Info(Module, msg);
        return msg;
    }

    private static void WriteOne(AcadDb.Database db, string handle, string key, string text)
    {
        AcadDb.ObjectId id = HydroReader.Id(db, handle) ?? throw new InvalidOperationException("peca nao encontrada");
        using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();
        AcadDb.DBObject obj = tr.GetObject(id, AcadDb.OpenMode.ForWrite, false);
        PropertyInfo p = Candidates(obj.GetType()).FirstOrDefault(x => x.Name == key)
            ?? throw new InvalidOperationException($"{key} nao existe neste objeto");
        if (p.SetMethod is not { IsPublic: true })
            throw new InvalidOperationException($"{key} e somente leitura");
        object? current = p.GetValue(obj);
        object value = Parse(p, text.Trim(), current, db, tr);
        p.SetValue(obj, value);
        tr.Commit();
    }

    private static object Parse(PropertyInfo p, string text, object? current, AcadDb.Database db, AcadDb.Transaction tr)
    {
        Type t = p.PropertyType;
        if (t == typeof(string))
            return text;
        if (t == typeof(double))
        {
            bool angle = IsAngle(p);
            double now = current is double d ? (angle ? d * 180 / Math.PI : d) : 0;
            double v = HydroRuleEngine.EditValue(text, now) ?? throw new InvalidOperationException($"{p.Name}: valor vazio");
            return angle ? v * Math.PI / 180 : v;
        }

        if (t == typeof(int) || t == typeof(short) || t == typeof(long))
        {
            double v = HydroRuleEngine.EditValue(text, Convert.ToDouble(current ?? 0, CultureInfo.InvariantCulture)) ?? 0;
            return Convert.ChangeType(Math.Round(v), t, CultureInfo.InvariantCulture);
        }

        if (t == typeof(bool))
            return text.Trim().ToLowerInvariant() is "sim" or "s" or "true" or "1" or "yes";
        if (t.IsEnum)
            return Enum.Parse(t, text, true);
        if (t == typeof(Point3d))
        {
            double[] n = text.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => double.Parse(x.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            Point3d c = current is Point3d cp ? cp : Point3d.Origin;
            return n.Length switch
            {
                2 => new Point3d(n[0], n[1], c.Z),
                3 => new Point3d(n[0], n[1], n[2]),
                _ => throw new InvalidOperationException("use X; Y; Z")
            };
        }

        if (t == typeof(AcadDb.ObjectId))
        {
            if (text.Equals("(nenhum)", StringComparison.OrdinalIgnoreCase))
                return AcadDb.ObjectId.Null;
            RefSource refs = new(db, tr);
            foreach ((string name, AcadDb.ObjectId oid) in refs.For(p.Name, current is AcadDb.ObjectId cid ? cid : AcadDb.ObjectId.Null))
            {
                if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
                    return oid;
            }

            throw new InvalidOperationException($"'{text}' nao encontrado para {p.Name}");
        }

        throw new InvalidOperationException($"{p.Name}: tipo {t.Name} nao editavel");
    }

    // ------------------------------------------------------------------
    // Opcoes das referencias (estilos, superficies, alinhamentos)
    // ------------------------------------------------------------------

    /// <summary>Lista os objetos que podem ir numa propriedade ObjectId: os do mesmo tipo do atual.</summary>
    private sealed class RefSource
    {
        private readonly AcadDb.Database _db;
        private readonly AcadDb.Transaction _tr;
        private List<AcadDb.ObjectId>? _styles;

        public RefSource(AcadDb.Database db, AcadDb.Transaction tr)
        {
            _db = db;
            _tr = tr;
        }

        public List<(string Name, AcadDb.ObjectId Id)> For(string property, AcadDb.ObjectId current)
        {
            IEnumerable<AcadDb.ObjectId> pool;
            Type? type = null;
            if (!current.IsNull && current.IsValid && !current.IsErased)
            {
                try
                {
                    type = _tr.GetObject(current, AcadDb.OpenMode.ForRead, false).GetType();
                }
                catch
                {
                    type = null;
                }
            }

            CivilDocument cdoc = CivilDocument.GetCivilDocument(_db);
            if (property.Contains("Surface", StringComparison.Ordinal))
                pool = Ids(() => cdoc.GetSurfaceIds());
            else if (property.Contains("Alignment", StringComparison.Ordinal))
                pool = Ids(() => cdoc.GetAlignmentIds());
            else if (property.Contains("Style", StringComparison.Ordinal) || property.Contains("RuleSet", StringComparison.Ordinal))
                pool = Styles(cdoc);
            else
                return new List<(string, AcadDb.ObjectId)>();

            List<(string, AcadDb.ObjectId)> list = new();
            foreach (AcadDb.ObjectId id in pool)
            {
                try
                {
                    AcadDb.DBObject o = _tr.GetObject(id, AcadDb.OpenMode.ForRead, false);
                    if (type is not null && o.GetType() != type)
                        continue;
                    if (type is null && !property.Contains("Surface", StringComparison.Ordinal) && !property.Contains("Alignment", StringComparison.Ordinal))
                        continue;
                    list.Add((NameOf(id, _tr), id));
                }
                catch
                {
                    // Objeto ilegivel: fica fora da lista.
                }
            }

            if (property.Contains("Surface", StringComparison.Ordinal) || property.Contains("Alignment", StringComparison.Ordinal))
                list.Insert(0, ("(nenhum)", AcadDb.ObjectId.Null));
            return list.OrderBy(x => x.Item1 == "(nenhum)" ? 0 : 1).ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<AcadDb.ObjectId> Ids(Func<AcadDb.ObjectIdCollection> get)
        {
            try
            {
                return get().Cast<AcadDb.ObjectId>().ToList();
            }
            catch
            {
                return Array.Empty<AcadDb.ObjectId>();
            }
        }

        /// <summary>Ids de todas as colecoes de estilo do primeiro nivel de CivilDocument.Styles.</summary>
        private List<AcadDb.ObjectId> Styles(CivilDocument cdoc)
        {
            if (_styles is not null)
                return _styles;
            _styles = new List<AcadDb.ObjectId>();
            object root = cdoc.Styles;
            foreach (PropertyInfo p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !typeof(IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string))
                    continue;
                try
                {
                    if (p.GetValue(root) is IEnumerable items)
                        _styles.AddRange(items.OfType<AcadDb.ObjectId>());
                }
                catch
                {
                    // Colecao que nao se aplica.
                }
            }

            return _styles;
        }
    }
}
