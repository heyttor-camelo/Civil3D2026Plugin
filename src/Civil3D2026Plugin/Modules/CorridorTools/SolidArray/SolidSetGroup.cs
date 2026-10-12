using System.Collections.Generic;

namespace Civil3D2026Plugin.Modules.CorridorTools.SolidArray;

/// <summary>
/// Mantem um grupo nativo do AutoCAD (GROUP) por conjunto de solidos
/// (C3DSOLIDARRAY e C3DSOLIDLIGAR).
///
/// - nome fixo derivado do ID do conjunto, para ser reencontrado a cada atualizacao;
/// - membros = solidos atualmente associados ao conjunto (apagados e
///   desassociados saem do grupo);
/// - grupo selecionavel: com PICKSTYLE ligado, um clique seleciona o conjunto
///   inteiro; Ctrl+H alterna para selecionar solidos individuais;
/// - se o usuario desfizer o grupo (UNGROUP), ele e recriado na proxima
///   atualizacao/edicao do conjunto.
/// </summary>
internal static class SolidSetGroup
{
    public static string NameFor(string prefix, string groupId) =>
        prefix + (groupId.Length <= 8 ? groupId : groupId[..8]).ToUpperInvariant();

    /// <summary>
    /// Sincroniza o grupo com os solidos do conjunto. Nunca interrompe a operacao
    /// principal: em falha, retorna false (o conjunto continua valido, so sem grupo).
    /// </summary>
    public static bool Sync(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        string name,
        string description,
        IEnumerable<string> handles)
    {
        try
        {
            SyncCore(tr, db, name, description, handles);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SyncCore(
        AcadDb.Transaction tr,
        AcadDb.Database db,
        string name,
        string description,
        IEnumerable<string> handles)
    {
        AcadDb.ObjectIdCollection members = new();

        foreach (string handle in handles)
        {
            AcadDb.ObjectId id = ResolveHandle(db, handle);
            if (!id.IsNull && !id.IsErased)
                members.Add(id);
        }

        AcadDb.DBDictionary groups = (AcadDb.DBDictionary)tr.GetObject(db.GroupDictionaryId, AcadDb.OpenMode.ForRead);
        AcadDb.Group group;

        if (groups.Contains(name))
        {
            group = (AcadDb.Group)tr.GetObject(groups.GetAt(name), AcadDb.OpenMode.ForWrite);
            group.Description = description;
            group.Clear();
        }
        else
        {
            if (members.Count == 0)
                return;

            groups.UpgradeOpen();
            group = new AcadDb.Group(description, true);
            groups.SetAt(name, group);
            tr.AddNewlyCreatedDBObject(group, true);
        }

        if (members.Count > 0)
            group.Append(members);
    }

    private static AcadDb.ObjectId ResolveHandle(AcadDb.Database db, string handleText)
    {
        if (string.IsNullOrWhiteSpace(handleText))
            return AcadDb.ObjectId.Null;

        try
        {
            long value = System.Convert.ToInt64(handleText, 16);
            return db.GetObjectId(false, new AcadDb.Handle(value), 0);
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }
}
