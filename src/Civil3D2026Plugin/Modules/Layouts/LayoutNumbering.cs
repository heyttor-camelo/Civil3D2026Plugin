using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>Um texto encontrado nos layouts (TEXT, MTEXT ou atributo de bloco).</summary>
internal sealed record SheetText2(
    ObjectId Id,
    string Layout,
    int Ordem,
    string Tipo,       // "Texto", "MTexto", "Atributo"
    string Texto,
    string Layer,
    double Altura,
    string Bloco,      // nome do bloco (atributo) ou ""
    string Tag,        // tag do atributo ou ""
    string Papel)      // "Numero", "Total" ou ""
{
    public int Caracteres => Texto.Length;
}

/// <summary>Filtro da busca de textos.</summary>
internal sealed class TextFilter
{
    public string Layer { get; set; } = "";      // curinga, vazio = todas
    public string Contem { get; set; } = "";     // texto ou regex (Regex=true)
    public bool Regex { get; set; }
    public double? AlturaMin { get; set; }
    public double? AlturaMax { get; set; }
    public int? CaracteresMin { get; set; }
    public int? CaracteresMax { get; set; }
    public bool SoNumericos { get; set; }        // "01", "12/30", "3 de 10"
    public bool Textos { get; set; } = true;
    public bool MTextos { get; set; } = true;
    public bool Atributos { get; set; } = true;
    public bool SoMarcados { get; set; }
}

/// <summary>Regra de numeracao.</summary>
internal sealed class NumberingRule
{
    public string Prefixo { get; set; } = "";
    public string Sufixo { get; set; } = "";
    public int Inicio { get; set; } = 1;
    public int Digitos { get; set; } = 2;
    public int? Total { get; set; }              // null = quantidade de layouts
    public string FormatoTotal { get; set; } = "/{0}";   // "/{0}", "{0}", "de {0}"
    public bool SoLayoutsMarcados { get; set; }
    public IReadOnlyList<string> Marcados { get; set; } = Array.Empty<string>();

    public string Numero(int n) => Prefixo + n.ToString().PadLeft(Math.Max(1, Digitos), '0') + Sufixo;

    public string TotalTexto(int total) => string.Format(CultureInfo.InvariantCulture, FormatoTotal, total.ToString().PadLeft(Math.Max(1, Digitos), '0'));
}

/// <summary>
/// Numeracao abrangente de pranchas. Qualquer texto, MTEXT ou atributo (em qualquer layout,
/// dentro ou fora do carimbo) pode ser marcado como "numero" ou "total". A marca fica em
/// XData (C3D_PRANCHA) e, opcionalmente, na layer PRANCHA_NUMERO/PRANCHA_TOTAL, que o NP
/// antigo tambem reconhece.
/// </summary>
internal static class LayoutNumbering
{
    public const string App = "C3D_PRANCHA";
    private static readonly Regex Numerico = new(@"^\s*[A-Za-z]{0,4}[-_.\s]?\d{1,4}\s*(?:[/\\|]|de|DE)?\s*\d{0,4}\s*$", RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // Busca
    // ------------------------------------------------------------------

    public static List<SheetText2> Buscar(Database db, TextFilter f)
    {
        var lista = new List<SheetText2>();
        Regex? rx = null;
        if (f.Contem.Length > 0 && f.Regex)
        {
            try
            {
                rx = new Regex(f.Contem, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                rx = null;
            }
        }

        using Transaction tr = db.TransactionManager.StartTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType)
                continue;
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                switch (obj)
                {
                    case AttributeDefinition:
                        break;
                    case DBText t when f.Textos:
                        Add(lista, f, rx, new SheetText2(id, lay.LayoutName, lay.TabOrder, "Texto", t.TextString, t.Layer, t.Height, "", "", Papel(t)));
                        break;
                    case MText m when f.MTextos:
                        Add(lista, f, rx, new SheetText2(id, lay.LayoutName, lay.TabOrder, "MTexto", m.Text, m.Layer, m.TextHeight, "", "", Papel(m)));
                        break;
                    case BlockReference br when f.Atributos:
                    {
                        string nome = NomeBloco(tr, br);
                        foreach (ObjectId aid in br.AttributeCollection)
                            if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference a && !a.IsConstant)
                                Add(lista, f, rx, new SheetText2(aid, lay.LayoutName, lay.TabOrder, "Atributo", a.TextString, a.Layer, a.Height, nome, a.Tag, Papel(a)));
                        break;
                    }
                }
            }
        }
        tr.Commit();
        return lista.OrderBy(t => t.Ordem).ThenBy(t => t.Tipo).ThenBy(t => t.Texto, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Add(List<SheetText2> lista, TextFilter f, Regex? rx, SheetText2 t)
    {
        if (f.SoMarcados && t.Papel.Length == 0)
            return;
        if (f.Layer.Length > 0 && !Civil3D2026Plugin.Modules.Utilitarios.UtilHelpers.WildcardMatch(t.Layer, f.Layer))
            return;
        if (f.AlturaMin is double a0 && t.Altura < a0 - 1e-9)
            return;
        if (f.AlturaMax is double a1 && t.Altura > a1 + 1e-9)
            return;
        if (f.CaracteresMin is int c0 && t.Caracteres < c0)
            return;
        if (f.CaracteresMax is int c1 && t.Caracteres > c1)
            return;
        if (f.SoNumericos && !Numerico.IsMatch(t.Texto))
            return;
        if (f.Contem.Length > 0)
        {
            if (rx is not null ? !rx.IsMatch(t.Texto) : !t.Texto.Contains(f.Contem, StringComparison.OrdinalIgnoreCase))
                return;
        }
        lista.Add(t);
    }

    private static string NomeBloco(Transaction tr, BlockReference br)
    {
        ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
    }

    /// <summary>"Numero", "Total" ou "" pela XData ou pela layer (compatibilidade com o NP).</summary>
    public static string Papel(Entity ent)
    {
        using ResultBuffer? rb = ent.GetXDataForApplication(App);
        if (rb is not null)
            foreach (TypedValue tv in rb)
                if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString && tv.Value is string s && (s == "Numero" || s == "Total"))
                    return s;
        if (ent.Layer.Equals(LayoutOps.LayerNumero, StringComparison.OrdinalIgnoreCase))
            return "Numero";
        if (ent.Layer.Equals(LayoutOps.LayerTotal, StringComparison.OrdinalIgnoreCase))
            return "Total";
        return "";
    }

    // ------------------------------------------------------------------
    // Marcar / editar
    // ------------------------------------------------------------------

    /// <summary>papel = "Numero", "Total" ou "" (desmarcar). moverLayer poe o texto na layer PRANCHA_*.</summary>
    public static OpResult Marcar(Document doc, IEnumerable<ObjectId> ids, string papel, bool moverLayer)
    {
        int n = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            GarantirApp(tr, db);
            foreach (ObjectId id in ids)
            {
                if (tr.GetObject(id, OpenMode.ForWrite) is not Entity ent)
                    continue;
                ent.XData = papel.Length == 0
                    ? new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App))
                    : new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App), new TypedValue((int)DxfCode.ExtendedDataAsciiString, papel));

                if (papel.Length == 0)
                {
                    // Sai das layers do NP para nao continuar sendo numerado.
                    if (ent.Layer.Equals(LayoutOps.LayerNumero, StringComparison.OrdinalIgnoreCase) || ent.Layer.Equals(LayoutOps.LayerTotal, StringComparison.OrdinalIgnoreCase))
                        ent.Layer = "0";
                }
                else if (moverLayer)
                {
                    string layer = papel == "Numero" ? LayoutOps.LayerNumero : LayoutOps.LayerTotal;
                    GarantirLayer(tr, db, layer);
                    ent.Layer = layer;
                }
                n++;
            }
            tr.Commit();
        }
        return new OpResult(n, 0, papel.Length == 0 ? $"{n} texto(s) desmarcado(s)." : $"{n} texto(s) marcado(s) como {papel.ToLowerInvariant()}.");
    }

    public static OpResult EditarTexto(Document doc, ObjectId id, string novo)
    {
        using DocumentLock _ = doc.LockDocument();
        using Transaction tr = doc.Database.TransactionManager.StartTransaction();
        DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
        switch (obj)
        {
            case AttributeReference a: a.TextString = novo; break;
            case DBText t: t.TextString = novo; break;
            case MText m: m.Contents = novo; break;
            default: return new OpResult(0, 1, "O objeto nao e texto.");
        }
        tr.Commit();
        return new OpResult(1, 0, "Texto alterado.");
    }

    // ------------------------------------------------------------------
    // Numerar
    // ------------------------------------------------------------------

    /// <summary>
    /// Escreve numero e total nos textos marcados de cada layout, na ordem das abas.
    /// Layouts sem texto marcado sao contados (entram no total) mas nao recebem nada.
    /// </summary>
    public static OpResult Numerar(Document doc, NumberingRule regra)
    {
        int textos = 0, layoutsComTexto = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            List<string> ordem = LayoutOps.OrdemAtual(db);
            List<string> alvo = regra.SoLayoutsMarcados
                ? ordem.Where(n => regra.Marcados.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList()
                : ordem;
            int total = regra.Total ?? alvo.Count;
            string totalTexto = regra.TotalTexto(total);
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

            for (int i = 0; i < alvo.Count; i++)
            {
                if (!dic.Contains(alvo[i]))
                    continue;
                var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(alvo[i]), OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                string numero = regra.Numero(regra.Inicio + i);
                int n = 0;

                foreach (ObjectId id in btr)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    switch (obj)
                    {
                        case AttributeDefinition:
                            break;
                        case DBText or MText:
                            n += Escrever((Entity)obj, numero, totalTexto);
                            break;
                        case BlockReference br:
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference att)
                                    n += Escrever(att, numero, totalTexto);
                            break;
                    }
                }
                textos += n;
                if (n > 0)
                    layoutsComTexto++;
            }
            tr.Commit();
        }

        return new OpResult(textos, 0,
            textos > 0
                ? $"{textos} texto(s) atualizado(s) em {layoutsComTexto} layout(s)."
                : "Nenhum texto marcado como numero/total. Use a busca para localizar os textos e marque-os.");
    }

    private static int Escrever(Entity ent, string numero, string total)
    {
        string papel = Papel(ent);
        if (papel.Length == 0)
            return 0;
        string valor = papel == "Numero" ? numero : total;
        ent.UpgradeOpen();
        switch (ent)
        {
            case AttributeReference a: a.TextString = valor; break;
            case DBText t: t.TextString = valor; break;
            case MText m: m.Contents = valor; break;
            default: return 0;
        }
        return 1;
    }

    /// <summary>Numero e total atuais de um layout (textos marcados), para o mapa-chave e a lista.</summary>
    public static (string? Numero, string? Total) LerNumeracao(Transaction tr, Autodesk.AutoCAD.DatabaseServices.Layout layout)
    {
        string? numero = null, total = null;
        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        foreach (ObjectId id in btr)
        {
            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
            IEnumerable<Entity> textos = obj switch
            {
                DBText or MText => new[] { (Entity)obj },
                BlockReference br => br.AttributeCollection.Cast<ObjectId>().Select(a => tr.GetObject(a, OpenMode.ForRead)).OfType<AttributeReference>(),
                _ => Enumerable.Empty<Entity>()
            };
            foreach (Entity e in textos)
            {
                string p = Papel(e);
                if (p.Length == 0)
                    continue;
                string v = e switch { DBText t => t.TextString, MText m => m.Text, _ => "" };
                if (p == "Numero") numero ??= v; else total ??= v;
            }
        }
        return (numero, total);
    }

    private static void GarantirApp(Transaction tr, Database db)
    {
        var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (rat.Has(App))
            return;
        rat.UpgradeOpen();
        var r = new RegAppTableRecord { Name = App };
        rat.Add(r);
        tr.AddNewlyCreatedDBObject(r, true);
    }

    private static void GarantirLayer(Transaction tr, Database db, string nome)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(nome))
            return;
        lt.UpgradeOpen();
        var ltr = new LayerTableRecord { Name = nome };
        lt.Add(ltr);
        tr.AddNewlyCreatedDBObject(ltr, true);
    }
}
