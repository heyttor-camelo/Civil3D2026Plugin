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

/// <summary>
/// Regra de numeracao. O texto vem de um modelo: {000} e o numero da prancha com 3 algarismos
/// (zeros a esquerda), {T000} o total com 3 algarismos; {0} e {T} sem zeros. Ex.:
/// "PRANCHA: {000}/{T000}" vira "PRANCHA: 008/012".
/// </summary>
internal sealed class NumberingRule
{
    /// <summary>Modelo dos textos de numero (e dos textos escolhidos na busca).</summary>
    public string Modelo { get; set; } = "{00}";
    /// <summary>Modelo dos textos gravados como TOTAL (total num texto separado).</summary>
    public string ModeloTotal { get; set; } = "/{T00}";
    public int Inicio { get; set; } = 1;
    public int? Total { get; set; }              // null = quantidade de layouts numerados
    public bool SoLayoutsMarcados { get; set; }
    public IReadOnlyList<string> Marcados { get; set; } = Array.Empty<string>();
    /// <summary>Textos escolhidos na busca: so eles recebem (e ficam gravados como NUMERO). null = os ja gravados como NUMERO/TOTAL.</summary>
    public IReadOnlyCollection<ObjectId>? Textos { get; set; }

    private static readonly Regex Campo = new(@"\{([NnTt]?)(0*)\}", RegexOptions.Compiled);

    /// <summary>Modelo com {0...} e {T0...} trocados pelo numero e pelo total.</summary>
    public static string Formatar(string modelo, int numero, int total) =>
        Campo.Replace(modelo ?? "", m =>
        {
            string qual = m.Groups[1].Value, zeros = m.Groups[2].Value;
            if (qual.Length == 0 && zeros.Length == 0)
                return m.Value; // "{}" fica como esta
            int v = qual is "T" or "t" ? total : numero;
            return v.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, zeros.Length), '0');
        });

    /// <summary>O modelo tem algum campo de numero ou de total.</summary>
    public static bool TemCampo(string modelo) =>
        Campo.Matches(modelo ?? "").Any(m => m.Groups[1].Length > 0 || m.Groups[2].Length > 0);

    public string Numero(int n, int total) => Formatar(Modelo, n, total);

    public string TotalTexto(int n, int total) => Formatar(ModeloTotal, n, total);

    /// <summary>Equivalente ao formato antigo (prefixo, digitos e "/total").</summary>
    public static NumberingRule Padrao(int digitos)
    {
        string z = new('0', Math.Clamp(digitos, 1, 6));
        return new NumberingRule { Modelo = "{" + z + "}", ModeloTotal = "/{T" + z + "}" };
    }
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
                if (tr.GetObject(id, OpenMode.ForWrite, false, true) is not Entity ent)
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
        DBObject obj = tr.GetObject(id, OpenMode.ForWrite, false, true);
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
    /// Escreve numero e total na ordem das abas. Com regra.Textos, so nesses textos (os escolhidos
    /// na busca), e o total e a quantidade de layouts que eles ocupam; sem, nos textos gravados
    /// como NUMERO/TOTAL (layouts sem texto contam no total, mas nao recebem nada). O formato fica
    /// gravado no desenho para o auto-update das sheets e o NP usarem o mesmo.
    /// </summary>
    public static OpResult Numerar(Document doc, NumberingRule regra)
    {
        int textos = 0, layoutsComTexto = 0, iguais = 0;
        using DocumentLock _ = doc.LockDocument();
        Database db = doc.Database;
        HashSet<ObjectId>? escolhidos = regra.Textos is null ? null : new HashSet<ObjectId>(regra.Textos);
        int total;
        string primeiro = "", ultimo = "";
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            GarantirApp(tr, db);
            List<string> ordem = LayoutOps.OrdemAtual(db);
            var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            List<string> alvo = regra.SoLayoutsMarcados
                ? ordem.Where(n => regra.Marcados.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList()
                : ordem;

            // Textos de cada layout que vao receber o numero/total.
            var porLayout = new List<(string Layout, List<Entity> Textos)>();
            foreach (string nome in alvo)
            {
                if (!dic.Contains(nome))
                    continue;
                var layout = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(dic.GetAt(nome), OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var lista = new List<Entity>();
                foreach (ObjectId id in btr)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    switch (obj)
                    {
                        case AttributeDefinition:
                            break;
                        case DBText or MText:
                            if (Entra((Entity)obj, escolhidos))
                                lista.Add((Entity)obj);
                            break;
                        case BlockReference br:
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference att && Entra(att, escolhidos))
                                    lista.Add(att);
                            break;
                    }
                }
                // Escolhidos na busca: so os layouts com texto escolhido entram na sequencia.
                if (escolhidos is null || lista.Count > 0)
                    porLayout.Add((nome, lista));
            }

            total = regra.Total ?? porLayout.Count;
            for (int i = 0; i < porLayout.Count; i++)
            {
                int n = regra.Inicio + i;
                string numero = regra.Numero(n, total), totalTexto = regra.TotalTexto(n, total);
                if (i == 0)
                    primeiro = numero;
                ultimo = numero;
                int k = 0;
                foreach (Entity ent in porLayout[i].Textos)
                {
                    // Escolhido na busca sem papel gravado vira NUMERO.
                    string papel = Papel(ent) is { Length: > 0 } p ? p : "Numero";
                    switch (Escrever(tr, ent, papel, papel == "Total" ? totalTexto : numero, n, total))
                    {
                        case true: k++; break;
                        case false: iguais++; break;
                    }
                }
                textos += k;
                if (porLayout[i].Textos.Count > 0)
                    layoutsComTexto++;
            }
            GravarFormato(tr, db, regra);
            tr.Commit();
        }

        if (textos + iguais == 0)
            return new OpResult(0, 0, escolhidos is null
                ? "Nenhum texto marcado como numero/total. Na Busca, selecione os textos da prancha e marque a caixinha."
                : "Os textos escolhidos nao estao nos layouts a numerar.") { Mudou = false };
        string faixa = primeiro.Length > 0 ? $" ({primeiro} a {ultimo}, total {total})" : "";
        return new OpResult(textos, 0,
            $"{textos} texto(s) atualizado(s) em {layoutsComTexto} layout(s){faixa}" + (iguais > 0 ? $"; {iguais} ja estavam certos." : "."))
        { Mudou = textos > 0 };
    }

    private static bool Entra(Entity ent, HashSet<ObjectId>? escolhidos) =>
        escolhidos is null ? Papel(ent).Length > 0 : escolhidos.Contains(ent.ObjectId);

    /// <summary>
    /// Grava o texto e a marca (papel, numero e total em XData, para o mapa-chave ler so o numero).
    /// true = escreveu, false = ja estava igual, null = nao e texto.
    /// </summary>
    private static bool? Escrever(Transaction tr, Entity ent, string papel, string valor, int numero, int total)
    {
        string atual = ent switch { AttributeReference a => a.TextString, DBText t => t.TextString, MText m => m.Text, _ => "" };
        bool marcado = Papel(ent) == papel && ValorGravado(ent, "N=") == numero.ToString(CultureInfo.InvariantCulture) && ValorGravado(ent, "T=") == total.ToString(CultureInfo.InvariantCulture);
        if (atual == valor && marcado)
            return false;
        // Mesmo em layer travada (o carimbo costuma ficar travado).
        DBObject w = tr.GetObject(ent.ObjectId, OpenMode.ForWrite, false, true);
        switch (w)
        {
            case AttributeReference a: if (a.TextString != valor) a.TextString = valor; break;
            case DBText t: if (t.TextString != valor) t.TextString = valor; break;
            case MText m:
                if (m.Text != valor)
                {
                    // Com formatacao (cor, fonte) em volta do texto: troca so o texto, mantendo os codigos.
                    // O valor entra escapado (\\, { e } do modelo viram texto, nao codigo de formatacao).
                    string c = m.Contents, novo = EscaparMText(valor), velho = EscaparMText(m.Text);
                    int i = velho.Length > 0 ? c.IndexOf(velho, StringComparison.Ordinal) : -1;
                    m.Contents = i >= 0 && c.IndexOf(velho, i + 1, StringComparison.Ordinal) < 0
                        ? c[..i] + novo + c[(i + velho.Length)..]
                        : novo;
                }
                break;
            default: return null;
        }
        ((Entity)w).XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, papel),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "N=" + numero.ToString(CultureInfo.InvariantCulture)),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "T=" + total.ToString(CultureInfo.InvariantCulture)));
        return true;
    }

    private static string EscaparMText(string s) =>
        s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r\n", "\\P").Replace("\n", "\\P");

    private static string? ValorGravado(Entity ent, string chave)
    {
        using ResultBuffer? rb = ent.GetXDataForApplication(App);
        if (rb is null)
            return null;
        foreach (TypedValue tv in rb)
            if (tv.Value is string s && s.StartsWith(chave, StringComparison.Ordinal))
                return s[chave.Length..];
        return null;
    }

    // ------------------------------------------------------------------
    // Formato gravado no desenho
    // ------------------------------------------------------------------

    private const string DicFormato = "C3D_NUMERACAO";

    /// <summary>Formato da ultima numeracao feita neste desenho (o auto-update das sheets e o NP usam o mesmo). null = nunca numerado pelo painel.</summary>
    public static NumberingRule? FormatoGravado(Database db)
    {
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            NumberingRule? r = null;
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (nod.Contains(DicFormato) && tr.GetObject(nod.GetAt(DicFormato), OpenMode.ForRead) is Xrecord { Data: not null } xr)
            {
                TypedValue[] v = xr.Data.AsArray();
                if (v.Length >= 2 && v[0].Value is string a && v[1].Value is string b)
                    r = new NumberingRule { Modelo = a, ModeloTotal = b };
            }
            tr.Commit();
            return r;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void GravarFormato(Transaction tr, Database db, NumberingRule regra)
    {
        var dados = new ResultBuffer(new TypedValue((int)DxfCode.Text, regra.Modelo), new TypedValue((int)DxfCode.Text, regra.ModeloTotal));
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (nod.Contains(DicFormato))
        {
            var xr = (Xrecord)tr.GetObject(nod.GetAt(DicFormato), OpenMode.ForRead);
            TypedValue[]? atual = xr.Data?.AsArray();
            if (atual is { Length: >= 2 } && Equals(atual[0].Value, regra.Modelo) && Equals(atual[1].Value, regra.ModeloTotal))
                return;
            xr.UpgradeOpen();
            xr.Data = dados;
            return;
        }
        nod.UpgradeOpen();
        var novo = new Xrecord { Data = dados };
        nod.SetAt(DicFormato, novo);
        tr.AddNewlyCreatedDBObject(novo, true);
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
                // Numero e total puros (gravados ao numerar); senao, o texto como esta.
                string v = e switch { AttributeReference a => a.TextString, DBText t => t.TextString, MText m => m.Text, _ => "" };
                string? n = ValorGravado(e, "N="), tt = ValorGravado(e, "T=");
                if (p == "Numero")
                {
                    numero ??= n is not null ? Digitos(n, v) : v;
                    if (tt is not null && v.Contains(tt, StringComparison.Ordinal))
                        total ??= Digitos(tt, v);
                }
                else
                {
                    total ??= tt is not null ? Digitos(tt, v) : v;
                }
            }
        }
        return (numero, total);
    }

    /// <summary>O numero com os zeros com que aparece no texto (008 em "PRANCHA: 008/012").</summary>
    private static string Digitos(string n, string texto)
    {
        foreach (Match m in Regex.Matches(texto, @"\d+"))
            if (int.TryParse(m.Value, out int a) && int.TryParse(n, out int b) && a == b)
                return m.Value;
        return n;
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
