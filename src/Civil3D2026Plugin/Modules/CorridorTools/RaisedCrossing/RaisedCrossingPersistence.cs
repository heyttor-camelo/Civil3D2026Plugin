using System;
using System.Collections.Generic;
using System.Globalization;

namespace Civil3D2026Plugin.Modules.CorridorTools.RaisedCrossing;

internal static class RaisedCrossingPersistence
{
    private const string RootDictionaryName = "C3D_RAISED_CROSSING_V1";
    private const string SettingsKey = "SETTINGS";
    private const string SetKeyPrefix = "PASSAGE_";
    private const string SolidTagKey = "C3D_RAISED_CROSSING_TAG";
    private const string RecordSignature = "C3D_RAISED_CROSSING_V1";

    public static RaisedCrossingSettings LoadSettings(
        AcadDb.Transaction tr,
        AcadDb.Database db)
    {
        AcadDb.DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            AcadDb.OpenMode.ForRead);

        if (!root.Contains(SettingsKey))
            return new RaisedCrossingSettings();

        var record = tr.GetObject(
            root.GetAt(SettingsKey),
            AcadDb.OpenMode.ForRead) as AcadDb.Xrecord;

        AcadDb.TypedValue[] values =
            record?.Data?.AsArray() ?? Array.Empty<AcadDb.TypedValue>();

        var settings = new RaisedCrossingSettings();

        if (values.Length >= 1)
            settings.DefaultOverhang = ToDouble(values[0].Value, 1.00);
        if (values.Length >= 2)
            settings.SampleStep = ToDouble(values[1].Value, 0.50);
        if (values.Length >= 3)
            settings.LastAnchorMode = (CrossingAnchorMode)ToInt(values[2].Value, 0);
        if (values.Length >= 4)
            settings.LastLengthInputMode = (CrossingLengthInputMode)ToInt(values[3].Value, 0);
        if (values.Length >= 5)
            settings.DefaultRampLength = ToDouble(values[4].Value, 1.00);
        if (values.Length >= 6)
            settings.DefaultHeight = ToDouble(values[5].Value, 0.15);

        return settings;
    }

    public static void SaveSettings(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        RaisedCrossingSettings settings)
    {
        AcadDb.DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            AcadDb.OpenMode.ForWrite);

        using var buffer = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, settings.DefaultOverhang),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, settings.SampleStep),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, (int)settings.LastAnchorMode),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, (int)settings.LastLengthInputMode),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, settings.DefaultRampLength),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, settings.DefaultHeight));

        SaveRecord(tr, root, SettingsKey, buffer);
    }

    public static void SavePassage(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        RaisedCrossingData data)
    {
        AcadDb.DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            AcadDb.OpenMode.ForWrite);

        using AcadDb.ResultBuffer buffer = BuildPassageBuffer(data);
        SaveRecord(tr, root, SetKeyPrefix + data.GroupId, buffer);
    }

    public static RaisedCrossingData? LoadPassage(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        AcadDb.DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            AcadDb.OpenMode.ForRead);

        string key = SetKeyPrefix + groupId;
        if (!root.Contains(key))
            return null;

        var record = tr.GetObject(
            root.GetAt(key),
            AcadDb.OpenMode.ForRead,
            false) as AcadDb.Xrecord;

        return record is null ? null : ParsePassage(record);
    }

    public static List<RaisedCrossingData> LoadAllPassages(
        AcadDb.Transaction tr,
        AcadDb.Database db)
    {
        AcadDb.DBDictionary root = GetOrCreateRootDictionary(
            tr,
            db,
            AcadDb.OpenMode.ForRead);

        var result = new List<RaisedCrossingData>();

        foreach (AcadDb.DBDictionaryEntry entry in root)
        {
            if (!entry.Key.StartsWith(SetKeyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var record = tr.GetObject(
                entry.Value,
                AcadDb.OpenMode.ForRead,
                false) as AcadDb.Xrecord;

            if (record is null)
                continue;

            RaisedCrossingData? data = ParsePassage(record);
            if (data is not null)
                result.Add(data);
        }

        return result;
    }

    public static void TagSolid(
        AcadDb.Transaction tr,
        AcadDb.Entity entity,
        string groupId)
    {
        if (entity.ExtensionDictionary.IsNull)
            entity.CreateExtensionDictionary();

        var dictionary = (AcadDb.DBDictionary)tr.GetObject(
            entity.ExtensionDictionary,
            AcadDb.OpenMode.ForWrite);

        using var buffer = new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, RecordSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, groupId));

        SaveRecord(tr, dictionary, SolidTagKey, buffer);
    }

    public static string? TryReadSolidGroupId(
        AcadDb.Transaction tr,
        AcadDb.DBObject entity)
    {
        if (entity.ExtensionDictionary.IsNull)
            return null;

        var dictionary = tr.GetObject(
            entity.ExtensionDictionary,
            AcadDb.OpenMode.ForRead,
            false) as AcadDb.DBDictionary;

        if (dictionary is null || !dictionary.Contains(SolidTagKey))
            return null;

        var record = tr.GetObject(
            dictionary.GetAt(SolidTagKey),
            AcadDb.OpenMode.ForRead,
            false) as AcadDb.Xrecord;

        AcadDb.TypedValue[] values =
            record?.Data?.AsArray() ?? Array.Empty<AcadDb.TypedValue>();

        if (values.Length < 2)
            return null;

        string signature = ToText(values[0].Value);
        if (!string.Equals(signature, RecordSignature, StringComparison.Ordinal))
            return null;

        string groupId = ToText(values[1].Value);
        return string.IsNullOrWhiteSpace(groupId) ? null : groupId;
    }

    public static AcadDb.ObjectId ResolveHandle(
        AcadDb.Database db,
        string handleText)
    {
        if (string.IsNullOrWhiteSpace(handleText))
            return AcadDb.ObjectId.Null;

        try
        {
            long value = Convert.ToInt64(handleText, 16);
            return db.GetObjectId(false, new AcadDb.Handle(value), 0);
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    private static AcadDb.ResultBuffer BuildPassageBuffer(RaisedCrossingData data)
    {
        return new AcadDb.ResultBuffer(
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, RecordSignature),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.GroupId),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.FeatureLine1Handle),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.FeatureLine2Handle),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.SolidHandle),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, (int)data.AnchorMode),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.ReferenceFraction),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.Length),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.Overhang),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.SampleStep),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int32, (int)data.HeightMode),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.Height),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.CorridorName ?? string.Empty),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.BaselineName ?? string.Empty),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.RegionName ?? string.Empty),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.SubassemblyName ?? string.Empty),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.CgStation),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Int16, data.HasReferenceStation ? 1 : 0),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.ReferenceStation),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.LastReferencePoint.X),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.LastReferencePoint.Y),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.LastReferencePoint.Z),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Real, data.RampLength),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.FeatureLine3Handle),
            new AcadDb.TypedValue((int)AcadDb.DxfCode.Text, data.FeatureLine4Handle));
    }

    private static RaisedCrossingData? ParsePassage(AcadDb.Xrecord record)
    {
        AcadDb.TypedValue[] values =
            record.Data?.AsArray() ?? Array.Empty<AcadDb.TypedValue>();

        if (values.Length < 22)
            return null;

        if (!string.Equals(ToText(values[0].Value), RecordSignature, StringComparison.Ordinal))
            return null;

        return new RaisedCrossingData
        {
            GroupId = ToText(values[1].Value),
            FeatureLine1Handle = ToText(values[2].Value),
            FeatureLine2Handle = ToText(values[3].Value),
            SolidHandle = ToText(values[4].Value),
            AnchorMode = (CrossingAnchorMode)ToInt(values[5].Value, 0),
            ReferenceFraction = ToDouble(values[6].Value, 0.0),
            Length = ToDouble(values[7].Value, 0.0),
            Overhang = ToDouble(values[8].Value, 1.0),
            SampleStep = ToDouble(values[9].Value, 0.50),
            HeightMode = (CrossingHeightMode)ToInt(values[10].Value, 0),
            Height = ToDouble(values[11].Value, 0.0),
            CorridorName = ToText(values[12].Value),
            BaselineName = ToText(values[13].Value),
            RegionName = ToText(values[14].Value),
            SubassemblyName = ToText(values[15].Value),
            CgStation = ToDouble(values[16].Value, 0.0),
            HasReferenceStation = ToInt(values[17].Value, 0) != 0,
            ReferenceStation = ToDouble(values[18].Value, 0.0),
            LastReferencePoint = new Autodesk.AutoCAD.Geometry.Point3d(
                ToDouble(values[19].Value, 0.0),
                ToDouble(values[20].Value, 0.0),
                ToDouble(values[21].Value, 0.0)),
            // Retrocompativel com PASSAGEM v1.0.0 (22 campos) e v1.0.1 (23 campos).
            RampLength = values.Length >= 23
                ? ToDouble(values[22].Value, 1.00)
                : 1.00,
            FeatureLine3Handle = values.Length >= 24
                ? ToText(values[23].Value)
                : string.Empty,
            FeatureLine4Handle = values.Length >= 25
                ? ToText(values[24].Value)
                : string.Empty
        };
    }

    private static void SaveRecord(
        AcadDb.Transaction tr,
        AcadDb.DBDictionary dictionary,
        string key,
        AcadDb.ResultBuffer data)
    {
        if (dictionary.Contains(key))
        {
            var existing = (AcadDb.Xrecord)tr.GetObject(
                dictionary.GetAt(key),
                AcadDb.OpenMode.ForWrite);
            existing.Data = data;
            return;
        }

        var record = new AcadDb.Xrecord
        {
            Data = data
        };

        dictionary.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    private static AcadDb.DBDictionary GetOrCreateRootDictionary(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        AcadDb.OpenMode requestedMode)
    {
        var nod = (AcadDb.DBDictionary)tr.GetObject(
            db.NamedObjectsDictionaryId,
            AcadDb.OpenMode.ForRead);

        if (nod.Contains(RootDictionaryName))
        {
            return (AcadDb.DBDictionary)tr.GetObject(
                nod.GetAt(RootDictionaryName),
                requestedMode);
        }

        nod.UpgradeOpen();
        var root = new AcadDb.DBDictionary();
        nod.SetAt(RootDictionaryName, root);
        tr.AddNewlyCreatedDBObject(root, true);
        return root;
    }

    private static string ToText(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static double ToDouble(object? value, double fallback)
    {
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static int ToInt(object? value, int fallback)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }
}
