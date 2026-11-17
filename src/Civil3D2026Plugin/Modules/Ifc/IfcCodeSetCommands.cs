using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Color = Autodesk.AutoCAD.Colors.Color;
using Civil3D2026Plugin.Infrastructure;

// Registro tambem aqui: a DLL avulsa C3DPassagemRampa compila este arquivo sem o CommandRegistration.
[assembly: CommandClass(typeof(Civil3D2026Plugin.Modules.Ifc.IfcCodeSetCommands))]

namespace Civil3D2026Plugin.Modules.Ifc;

/// <summary>
/// C3DCODESETIFC: acerta o Code Set Style usado pelos corredores para o IFC sair com cor, classe e
/// material no meio-fio (MFSARJETA) e no pavimento.
///
/// Como a extensao IFC 2026.1 le o corredor (medido no IFC da Waldir Soeiro, 13/11/2026):
///  - cada shape/link vira uma peca; o estilo (e a cor) vem do item do Code Set Style do PRIMEIRO code
///    da shape/link (ex. "GUIA, MF_T30" -> item GUIA; "Sarjeta, SARJETA" -> item Sarjeta). Sem item:
///    peca sem cor e sem classe;
///  - cor = cor do "AreaFill" de Modelo do estilo de shape (link: cor de Modelo do estilo de link);
///  - classe IFC = planilha de mapeamento pelo NOME do estilo (C3DIFC: SARJETA -> IfcKerb, BASICO -> nao exporta);
///  - material IFC = Render Material do item do Code Set.
/// O comando so mexe no que falta ou esta diferente, mostra a lista antes e grava numa transacao so (U desfaz).
/// </summary>
public sealed class IfcCodeSetCommands
{
    private const string Modulo = "IFC";
    internal const string SarjetaShapeStyle = "SARJETA";
    internal const string LinkStyleBasico = "BASICO";

    private static readonly string[] ConcretoPreferidos =
        { "Concrete.Cast-In-Place.Flat.Grey.1", "Concrete.Cast-In-Place.Flat.Grey", "Concrete.Precast Structural Concrete.Smooth" };
    private static readonly string[] AsfaltoPreferidos = { "Sitework.Paving - Surfacing.Asphalt" };
    internal const string ConcretoNovo = "C3D Concreto";
    internal const string AsfaltoNovo = "C3D Asfalto";

    private enum Mat { Nenhum, Concreto, Asfalto }

    private sealed class Mudanca
    {
        public string Estilo = "";
        public string Tipo = "";
        public string Code = "";
        public string Hoje = "";
        public string Fica = "";
        public Action<AcadDb.Transaction>? Aplicar;
    }

    /// <summary>Material desejado para o code de uma shape (Nenhum = nao mexe).</summary>
    private static Mat MaterialDaShape(string code)
    {
        string c = code.Trim();
        if (c.Equals("GUIA", StringComparison.OrdinalIgnoreCase) || c.Equals("Curb", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)
            || c.Equals("Sarjeta", StringComparison.OrdinalIgnoreCase)
            || c.Equals("Calcada", StringComparison.OrdinalIgnoreCase) || c.Equals("Passeio", StringComparison.OrdinalIgnoreCase)
            || c.Equals("PasseioConcreto", StringComparison.OrdinalIgnoreCase))
            return Mat.Concreto;
        if (c.StartsWith("PAVIMENTO", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Pave", StringComparison.OrdinalIgnoreCase))
            return Mat.Asfalto;
        return Mat.Nenhum;
    }

    [CommandMethod(C3DCommands.Ifc.CodeSet, CommandFlags.Modal)]
    public void AcertarCodeSet()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null)
            return;
        Editor ed = doc.Editor;
        AcadDb.Database db = doc.Database;
        try
        {
            CivilDocument civ = CivilApplication.ActiveDocument;
            using AcadDb.Transaction tr = db.TransactionManager.StartTransaction();

            // Code Set Styles usados pelos corredores
            var usados = new Dictionary<AcadDb.ObjectId, List<string>>();
            foreach (AcadDb.ObjectId cid in civ.CorridorCollection)
            {
                if (tr.GetObject(cid, AcadDb.OpenMode.ForRead) is not CivilDb.Corridor cor)
                    continue;
                AcadDb.ObjectId css = cor.CodeSetStyleId;
                if (css.IsNull)
                    continue;
                if (!usados.TryGetValue(css, out var nomes))
                    usados[css] = nomes = new List<string>();
                nomes.Add(cor.Name);
            }

            if (usados.Count == 0)
            {
                ed.WriteMessage("\nNenhum corredor com Code Set Style neste desenho. Nada a fazer.\n");
                return;
            }

            // materiais e estilos que existem hoje
            Dictionary<string, AcadDb.ObjectId> materiais = Materiais(tr, db);
            string concreto = Escolher(materiais, ConcretoPreferidos, "Concrete", "Concreto") ?? ConcretoNovo;
            string asfalto = Escolher(materiais, AsfaltoPreferidos, "Asphalt", "Asfalto") ?? AsfaltoNovo;
            AcadDb.ObjectId shapeSarjeta = Estilo(tr, civ.Styles.ShapeStyles, SarjetaShapeStyle);
            AcadDb.ObjectId linkBasico = Estilo(tr, civ.Styles.LinkStyles, LinkStyleBasico);
            var nomesCss = new Dictionary<AcadDb.ObjectId, string>();

            var lista = new List<Mudanca>();
            var criar = new List<string>();
            var manual = new List<string>();
            if (!materiais.ContainsKey(concreto))
                criar.Add($"material \"{concreto}\" (concreto cinza simples; nao havia material de concreto no desenho)");
            if (!materiais.ContainsKey(asfalto))
                criar.Add($"material \"{asfalto}\" (asfalto cinza escuro simples; nao havia material de asfalto no desenho)");
            if (shapeSarjeta.IsNull)
                criar.Add($"estilo de shape \"{SarjetaShapeStyle}\" (cinza concreto 160,160,160 no modelo e na secao)");
            if (linkBasico.IsNull)
                criar.Add($"estilo de link \"{LinkStyleBasico}\" (linha preta no modelo)");

            foreach ((AcadDb.ObjectId cssId, List<string> corredores) in usados)
            {
                var css = (CodeSetStyle)tr.GetObject(cssId, AcadDb.OpenMode.ForRead);
                string cssNome = css.Name;
                nomesCss[cssId] = cssNome;

                // --- shapes
                css.UpgradeOpen();
                css.SubentityStyleType = SubassemblySubentityStyleType.ShapeType;
                var shapes = Itens(css);
                foreach (string code in new[] { "Sarjeta", "SARJETA" })
                {
                    ItemInfo? it = shapes.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.Ordinal));
                    string c = code;
                    if (it is null)
                    {
                        lista.Add(new Mudanca
                        {
                            Estilo = cssNome, Tipo = "shape", Code = c, Hoje = "sem item (peca sem cor e sem classe no IFC)",
                            Fica = $"estilo {SarjetaShapeStyle}",
                            Aplicar = t => NovoItem(t, cssId, SubassemblySubentityStyleType.ShapeType, c, () => shapeSarjeta, null, db),
                        });
                    }
                    else if (!string.Equals(it.Estilo, SarjetaShapeStyle, StringComparison.OrdinalIgnoreCase))
                    {
                        lista.Add(new Mudanca
                        {
                            Estilo = cssNome, Tipo = "shape", Code = c, Hoje = $"estilo {Vazio(it.Estilo)}",
                            Fica = $"estilo {SarjetaShapeStyle}",
                            Aplicar = t => ComItem(t, cssId, SubassemblySubentityStyleType.ShapeType, c,
                                i => i.CodeStyleId = shapeSarjeta),
                        });
                    }
                }

                var codesShape = shapes.Select(i => i.Code).Concat(new[] { "Sarjeta", "SARJETA" }).Distinct(StringComparer.Ordinal);
                foreach (string code in codesShape)
                {
                    Mat m = MaterialDaShape(code);
                    if (m == Mat.Nenhum)
                        continue;
                    string alvo = m == Mat.Concreto ? concreto : asfalto;
                    ItemInfo? it = shapes.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.Ordinal));
                    string? hoje = it?.Material;
                    if (hoje is null)
                    {
                        // Item novo ou API que nao le/grava Render Material em shape (Civil 3D 2026): passo manual.
                        manual.Add($"[{cssNome}] shape {code}: Render Material = {alvo}");
                        continue;
                    }

                    if (string.Equals(hoje, alvo, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string c = code;
                    lista.Add(new Mudanca
                    {
                        Estilo = cssNome, Tipo = "shape", Code = c, Hoje = $"material {Vazio(hoje)}", Fica = $"material {alvo}",
                        Aplicar = t => ComItem(t, cssId, SubassemblySubentityStyleType.ShapeType, c, i => DefinirMaterial(i, alvo, t, db)),
                    });
                }

                // --- links
                css.SubentityStyleType = SubassemblySubentityStyleType.LinkType;
                var links = Itens(css);
                {
                    const string c = "Sarjeta";
                    ItemInfo? it = links.FirstOrDefault(i => string.Equals(i.Code, c, StringComparison.Ordinal));
                    if (it is null)
                    {
                        lista.Add(new Mudanca
                        {
                            Estilo = cssNome, Tipo = "link", Code = c, Hoje = "sem item (superficie solta sem cor no IFC)",
                            Fica = $"estilo {LinkStyleBasico} (nao exportado pelo C3DIFC)",
                            Aplicar = t => NovoItem(t, cssId, SubassemblySubentityStyleType.LinkType, c, () => linkBasico, null, db),
                        });
                    }
                    else if (!string.Equals(it.Estilo, LinkStyleBasico, StringComparison.OrdinalIgnoreCase))
                    {
                        lista.Add(new Mudanca
                        {
                            Estilo = cssNome, Tipo = "link", Code = c, Hoje = $"estilo {Vazio(it.Estilo)}", Fica = $"estilo {LinkStyleBasico}",
                            Aplicar = t => ComItem(t, cssId, SubassemblySubentityStyleType.LinkType, c,
                                i => i.CodeStyleId = linkBasico),
                        });
                    }
                }

                css.SubentityStyleType = SubassemblySubentityStyleType.MarkerType;
            }

            if (lista.Count == 0)
            {
                tr.Abort();
                ed.WriteMessage($"\n{C3DCommands.Ifc.CodeSet}: estilos e itens do Code Set Style dos corredores ({string.Join(", ", usados.Keys.Select(k => nomesCss[k]))}) ja estao certos para o IFC. Nada mudou.");
                ed.WriteMessage(TextoManual(manual) + "\n");
                return;
            }

            // mostra o que vai mudar
            var sb = new StringBuilder();
            sb.Append($"\n{C3DCommands.Ifc.CodeSet}: mudancas no Code Set Style usado pelos corredores ({string.Join("; ", usados.Select(u => nomesCss[u.Key] + ": " + string.Join(", ", u.Value)))}):");
            foreach (string c in criar)
                sb.Append($"\n  criar {c}");
            foreach (Mudanca m in lista)
                sb.Append($"\n  [{m.Estilo}] {m.Tipo} {m.Code}: hoje {m.Hoje} -> fica {m.Fica}");
            sb.Append("\n  Itens que ja estao certos nao mudam. Tudo numa operacao so: U desfaz.");
            if (manual.Count > 0)
                sb.Append($"\n  (Render Material dos {manual.Count} itens de shape fica como passo manual: a API do Civil 3D 2026 nao grava; lista no fim.)");
            ed.WriteMessage(sb.ToString());

            var ko = new PromptKeywordOptions($"\nAplicar estas {lista.Count} mudancas no Code Set Style? [Aplicar/Cancelar]: ")
            {
                AllowNone = false,
                AppendKeywordsToMessage = false,
            };
            ko.Keywords.Add("Aplicar");
            ko.Keywords.Add("Cancelar");
            PromptResult kr = ed.GetKeywords(ko);
            if (kr.Status != PromptStatus.OK || kr.StringResult != "Aplicar")
            {
                tr.Abort();
                ed.WriteMessage("\nCancelado. Nada mudou.\n");
                return;
            }

            // cria o que falta e aplica
            if (!materiais.ContainsKey(concreto))
                NovoMaterial(tr, db, concreto, 165, 165, 160);
            if (!materiais.ContainsKey(asfalto))
                NovoMaterial(tr, db, asfalto, 55, 55, 55);
            if (shapeSarjeta.IsNull)
                shapeSarjeta = NovoShapeStyle(tr, civ);
            if (linkBasico.IsNull)
                linkBasico = NovoLinkStyle(tr, civ);

            var falhas = new List<string>();
            int ok = 0;
            foreach (Mudanca m in lista)
            {
                try
                {
                    m.Aplicar!(tr);
                    ok++;
                }
                catch (System.Exception ex)
                {
                    falhas.Add($"{m.Tipo} {m.Code} ({m.Fica}): {ex.Message}");
                }
            }

            foreach (AcadDb.ObjectId cssId in usados.Keys)
            {
                var css = (CodeSetStyle)tr.GetObject(cssId, AcadDb.OpenMode.ForWrite);
                css.SubentityStyleType = SubassemblySubentityStyleType.MarkerType;
            }

            tr.Commit();

            string resumo = $"{C3DCommands.Ifc.CodeSet}: {ok} de {lista.Count} mudancas gravadas" +
                            (criar.Count > 0 ? $"; criados: {string.Join("; ", criar)}" : "") + ". U desfaz.";
            ed.WriteMessage("\n" + resumo);
            PluginLog.Info(Modulo, resumo + " Lista: " + string.Join(" | ", lista.Select(m => $"{m.Tipo} {m.Code}: {m.Hoje} -> {m.Fica}")));
            if (falhas.Count > 0)
            {
                string f = "Nao gravou: " + string.Join("; ", falhas);
                ed.WriteMessage("\n" + f);
                PluginLog.Aviso(Modulo, f);
            }

            ed.WriteMessage(TextoManual(manual));
            if (manual.Count > 0)
                PluginLog.Info(Modulo, "Passo manual (Render Material de shape): " + string.Join(" | ", manual));
            ed.WriteMessage("\nOs corredores ficam desatualizados ate o Rebuild; rode o Rebuild antes do C3DIFC.\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n{C3DCommands.Ifc.CodeSet}: erro - {ex.Message}\n");
            PluginLog.Erro(Modulo, C3DCommands.Ifc.CodeSet + ": " + ex.Message, ex);
        }
    }

    // ------------------------------------------------------------------ apoio

    private static string Vazio(string? s) => string.IsNullOrWhiteSpace(s) ? "(nenhum)" : s!;

    private static string TextoManual(List<string> manual)
    {
        if (manual.Count == 0)
            return "";
        var sb = new StringBuilder();
        sb.Append("\nPasso manual (a API do Civil 3D 2026 nao le nem grava o Render Material de itens de SHAPE):");
        sb.Append("\n  Toolspace > Settings > General > Multipurpose Styles > Code Set Styles > estilo > Edit Code Set Style > aba Codes, grupo Shape, coluna Render Material:");
        foreach (string m in manual)
            sb.Append("\n  " + m);
        return sb.ToString();
    }

    /// <summary>Dados de um item do Code Set (copiados; o item nativo e descartado na hora).</summary>
    private sealed record ItemInfo(string Code, string Estilo, string? Material);

    // Os CodeSetStyleItem sao wrappers de memoria que pertence ao Code Set: Dispose() neles derrubou o Civil no
    // Commit e o finalizador (coletor de lixo) derrubou depois, ao ler o Code Set de novo (testes de 17/11/2026).
    // Por isso: nunca Dispose, e SuppressFinalize em todo item que passar por aqui (o Code Set cuida da memoria).
    private static CodeSetStyleItem Segurar(CodeSetStyleItem it)
    {
        GC.SuppressFinalize(it);
        return it;
    }

    private static List<ItemInfo> Itens(CodeSetStyle css)
    {
        var l = new List<ItemInfo>();
        foreach (CodeSetStyleItem item in css)
        {
            CodeSetStyleItem it = Segurar(item);
            l.Add(new ItemInfo(it.Code ?? "", it.CodeStyleName ?? "", RenderMaterial(it)));
        }

        return l;
    }

    private static void ComItem(AcadDb.Transaction tr, AcadDb.ObjectId cssId, SubassemblySubentityStyleType tipo, string code, Action<CodeSetStyleItem> fazer)
    {
        var css = (CodeSetStyle)tr.GetObject(cssId, AcadDb.OpenMode.ForWrite);
        css.SubentityStyleType = tipo;
        bool achou = false;
        foreach (CodeSetStyleItem item in css)
        {
            CodeSetStyleItem it = Segurar(item);
            if (achou || !string.Equals(it.Code, code, StringComparison.Ordinal))
                continue;
            achou = true;
            fazer(it);
        }

        if (!achou)
            throw new InvalidOperationException($"item {code} nao encontrado");
    }

    private static void NovoItem(AcadDb.Transaction tr, AcadDb.ObjectId cssId, SubassemblySubentityStyleType tipo, string code,
                                 Func<AcadDb.ObjectId> estilo, string? material, AcadDb.Database db)
    {
        var css = (CodeSetStyle)tr.GetObject(cssId, AcadDb.OpenMode.ForWrite);
        css.SubentityStyleType = tipo;
        AcadDb.ObjectId sid = estilo();
        if (sid.IsNull)
            throw new InvalidOperationException("estilo nao encontrado");
        CodeSetStyleItem it = Segurar(css.Add(code, sid));
        if (material is not null)
            DefinirMaterial(it, material, tr, db);
    }

    /// <summary>
    /// Render Material do item ("" = nenhum). null = a API recusa ("Operation is not valid..."):
    /// no Civil 3D 2026 isso acontece em todos os itens de SHAPE (teste de 17/11/2026), so os de link funcionam.
    /// </summary>
    private static string? RenderMaterial(CodeSetStyleItem it)
    {
        try
        {
            return it.RenderMaterialStyleName ?? "";
        }
        catch
        {
            return null;
        }
    }

    private static void DefinirMaterial(CodeSetStyleItem it, string nome, AcadDb.Transaction tr, AcadDb.Database db)
    {
        Dictionary<string, AcadDb.ObjectId> mats = Materiais(tr, db);
        if (!mats.TryGetValue(nome, out AcadDb.ObjectId mid))
            throw new InvalidOperationException($"material {nome} nao encontrado");
        try
        {
            it.RenderMaterialStyleId = mid;
        }
        catch
        {
            it.RenderMaterialStyleName = nome;
        }
    }

    private static Dictionary<string, AcadDb.ObjectId> Materiais(AcadDb.Transaction tr, AcadDb.Database db)
    {
        var d = new Dictionary<string, AcadDb.ObjectId>(StringComparer.OrdinalIgnoreCase);
        var md = (AcadDb.DBDictionary)tr.GetObject(db.MaterialDictionaryId, AcadDb.OpenMode.ForRead);
        foreach (AcadDb.DBDictionaryEntry e in md)
            d[e.Key] = e.Value;
        return d;
    }

    private static string? Escolher(Dictionary<string, AcadDb.ObjectId> mats, string[] preferidos, params string[] contem)
    {
        foreach (string p in preferidos)
            if (mats.ContainsKey(p))
                return mats.Keys.First(k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
        foreach (string c in contem)
        {
            string? k = mats.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (k is not null)
                return k;
        }

        return null;
    }

    private static AcadDb.ObjectId Estilo(AcadDb.Transaction tr, StyleCollectionBase col, string nome)
    {
        foreach (AcadDb.ObjectId id in col)
        {
            if (tr.GetObject(id, AcadDb.OpenMode.ForRead) is StyleBase s && string.Equals(s.Name, nome, StringComparison.OrdinalIgnoreCase))
                return id;
        }

        return AcadDb.ObjectId.Null;
    }

    private static void NovoMaterial(AcadDb.Transaction tr, AcadDb.Database db, string nome, byte r, byte g, byte b)
    {
        var md = (AcadDb.DBDictionary)tr.GetObject(db.MaterialDictionaryId, AcadDb.OpenMode.ForWrite);
        var m = new AcadDb.Material { Name = nome, Description = "Criado pelo C3DCODESETIFC (cor para o IFC)" };
        var cor = new MaterialColor(Method.Override, 1.0, new EntityColor(r, g, b));
        m.Diffuse = new MaterialDiffuseComponent(cor, new MaterialMap());
        m.Ambient = cor;
        md.SetAt(nome, m);
        tr.AddNewlyCreatedDBObject(m, true);
    }

    private static AcadDb.ObjectId NovoShapeStyle(AcadDb.Transaction tr, CivilDocument civ)
    {
        AcadDb.ObjectId id = civ.Styles.ShapeStyles.Add(SarjetaShapeStyle);
        var st = (ShapeStyle)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
        if (!string.Equals(st.Name, SarjetaShapeStyle, StringComparison.Ordinal))
            st.Name = SarjetaShapeStyle;
        st.Description = "Criado pelo C3DCODESETIFC: sarjeta da MFSARJETA (cor no IFC)";
        Color concreto = Color.FromRgb(160, 160, 160);
        Color borda = Color.FromColorIndex(ColorMethod.ByAci, 8);
        foreach (ShapeDisplayStyleType t in new[] { ShapeDisplayStyleType.Border, ShapeDisplayStyleType.AreaFill })
        {
            DisplayStyle mo = st.GetDisplayStyleModel(t);
            mo.Visible = true;
            mo.Color = t == ShapeDisplayStyleType.AreaFill ? concreto : borda;
            DisplayStyle se = st.GetDisplayStyleSection(t);
            se.Visible = true;
            se.Color = t == ShapeDisplayStyleType.AreaFill ? concreto : borda;
            st.GetDisplayStylePlan(t).Visible = false;
        }

        return id;
    }

    private static AcadDb.ObjectId NovoLinkStyle(AcadDb.Transaction tr, CivilDocument civ)
    {
        AcadDb.ObjectId id = civ.Styles.LinkStyles.Add(LinkStyleBasico);
        var st = (LinkStyle)tr.GetObject(id, AcadDb.OpenMode.ForWrite);
        if (!string.Equals(st.Name, LinkStyleBasico, StringComparison.Ordinal))
            st.Name = LinkStyleBasico;
        st.LinkDisplayStyleModel.Visible = true;
        st.LinkDisplayStyleModel.Color = Color.FromColorIndex(ColorMethod.ByAci, 7);
        return id;
    }
}
