using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Pontos de restauracao do painel de layouts: antes de cada acao que mexe no desenho (e antes da
/// atualizacao automatica das pranchas do eixo) guarda a ordem e o nome das abas e a vista de
/// todas as viewports (centro, alvo, direcao, giro, escala, posicao/tamanho no papel, travada,
/// ligada). Restaurar devolve tudo isso, sem mexer no resto do desenho; layouts criados depois
/// podem ser excluidos. Ficam os ultimos 30 por desenho, em memoria e em
/// %AppData%\Civil3D2026Plugin\restauracao (sobrevivem a fechar o Civil).
/// </summary>
internal static class LayoutRestauracao
{
    private const string Mod = "Layouts";
    private const int Maximo = 30;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal sealed record LayoutEstado(long Handle, string Nome, int Ordem);

    internal sealed record VpEstado(
        long Handle, Point3d Centro, double Largura, double Altura,
        Point2d ViewCenter, Point3d ViewTarget, Vector3d ViewDirection, double Twist, double Escala,
        bool Travada, bool Ligada);

    internal sealed class Ponto
    {
        public DateTime Quando { get; init; }
        public string Acao { get; init; } = "";
        public string Arquivo { get; set; } = "";
        public List<LayoutEstado> Layouts { get; } = new();
        public List<VpEstado> Viewports { get; } = new();

        public string Rotulo => $"{Quando:dd/MM HH:mm:ss}  antes de: {Acao}   ({Layouts.Count} layouts, {Viewports.Count} viewports)";
    }

    /// <summary>Guarda o estado atual. Nunca derruba a acao: falha so vai para o log.</summary>
    public static void Capturar(Database db, string acao)
    {
        try
        {
            Ponto p = Ler(db, acao);
            if (p.Layouts.Count == 0)
                return;
            string pasta = Pasta(db);
            Directory.CreateDirectory(pasta);
            p.Arquivo = Path.Combine(pasta, p.Quando.ToString("yyyyMMdd-HHmmss-fff", Inv) + ".txt");
            File.WriteAllText(p.Arquivo, Escrever(p), Encoding.UTF8);
            foreach (string velho in Directory.GetFiles(pasta, "*.txt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Maximo))
                File.Delete(velho);
            PluginLog.Diag(Mod, $"Ponto de restauracao: {p.Rotulo}");
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso(Mod, "Ponto de restauracao nao gravado: " + ex.Message);
        }
    }

    /// <summary>Pontos deste desenho, do mais novo para o mais velho.</summary>
    public static List<Ponto> Listar(Database db)
    {
        var lista = new List<Ponto>();
        try
        {
            string pasta = Pasta(db);
            if (!Directory.Exists(pasta))
                return lista;
            foreach (string f in Directory.GetFiles(pasta, "*.txt").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                try
                {
                    Ponto p = LerArquivo(f);
                    p.Arquivo = f;
                    lista.Add(p);
                }
                catch (System.Exception ex)
                {
                    PluginLog.Diag(Mod, $"Ponto de restauracao ilegivel ({Path.GetFileName(f)}): {ex.Message}");
                }
            }
        }
        catch (System.Exception ex)
        {
            PluginLog.Aviso(Mod, "Pontos de restauracao: " + ex.Message);
        }
        return lista;
    }

    /// <summary>O que mudaria ao restaurar: viewports com vista diferente, abas fora de ordem, renomeados, layouts novos e apagados.</summary>
    public static (int Vistas, bool Ordem, int Renomeados, List<string> Novos, List<string> Sumiram) Diferencas(Database db, Ponto p)
    {
        Ponto agora = Ler(db, "");
        var vpsAgora = agora.Viewports.ToDictionary(v => v.Handle);
        int vistas = p.Viewports.Count(v => vpsAgora.TryGetValue(v.Handle, out VpEstado? a) && !Igual(a, v));
        var layAgora = agora.Layouts.ToDictionary(l => l.Handle);
        var layAntes = p.Layouts.ToDictionary(l => l.Handle);
        int renomeados = p.Layouts.Count(l => layAgora.TryGetValue(l.Handle, out LayoutEstado? a) && !a.Nome.Equals(l.Nome, StringComparison.Ordinal));
        List<string> novos = agora.Layouts.Where(l => !layAntes.ContainsKey(l.Handle)).OrderBy(l => l.Ordem).Select(l => l.Nome).ToList();
        List<string> sumiram = p.Layouts.Where(l => !layAgora.ContainsKey(l.Handle)).OrderBy(l => l.Ordem).Select(l => l.Nome).ToList();
        List<long> ordemAntes = p.Layouts.OrderBy(l => l.Ordem).Select(l => l.Handle).Where(layAgora.ContainsKey).ToList();
        List<long> ordemAgora = agora.Layouts.OrderBy(l => l.Ordem).Select(l => l.Handle).Where(layAntes.ContainsKey).ToList();
        return (vistas, !ordemAntes.SequenceEqual(ordemAgora), renomeados, novos, sumiram);
    }

    /// <summary>
    /// Devolve vistas, nomes e ordem do ponto; excluirNovos apaga os layouts criados depois dele.
    /// Quem chama guarda antes um ponto do estado atual (o painel faz isso no Executar).
    /// </summary>
    public static OpResult Restaurar(Document doc, Ponto p, bool excluirNovos)
    {
        Database db = doc.Database;
        var avisos = new List<string>();
        int vistas = 0, renomeados = 0, excluidos = 0;
        bool ordemOk;
        using (SheetSeriesSync.Suspender())
        using (doc.LockDocument())
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (VpEstado v in p.Viewports)
                {
                    if (!db.TryGetObjectId(new Handle(v.Handle), out ObjectId id) || id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                        continue;
                    if (Igual(Estado(vp), v))
                        continue;
                    try
                    {
                        vp.UpgradeOpen();
                        vp.Locked = false;
                        vp.ViewDirection = v.ViewDirection;
                        vp.ViewTarget = v.ViewTarget;
                        vp.TwistAngle = v.Twist;
                        vp.ViewCenter = v.ViewCenter;
                        if (v.Escala > 0)
                            vp.CustomScale = v.Escala;
                        vp.CenterPoint = v.Centro;
                        vp.Width = v.Largura;
                        vp.Height = v.Altura;
                        if (vp.On != v.Ligada)
                            vp.On = v.Ligada;
                        vp.Locked = v.Travada;
                        vistas++;
                    }
                    catch (System.Exception ex)
                    {
                        avisos.Add($"viewport {id.Handle}: {ex.Message}");
                    }
                }
                tr.Commit();
            }

            // Nomes: so volta o nome antigo se ninguem estiver usando.
            LayoutManager lm = LayoutManager.Current;
            Dictionary<long, string> nomesAgora = Ler(db, "").Layouts.ToDictionary(l => l.Handle, l => l.Nome);
            foreach (LayoutEstado l in p.Layouts)
            {
                if (!nomesAgora.TryGetValue(l.Handle, out string? atual) || atual.Equals(l.Nome, StringComparison.Ordinal))
                    continue;
                if (nomesAgora.Values.Any(n => n.Equals(l.Nome, StringComparison.OrdinalIgnoreCase)))
                {
                    avisos.Add($"\"{atual}\" nao voltou a se chamar \"{l.Nome}\": ja existe um layout com esse nome");
                    continue;
                }
                try
                {
                    lm.RenameLayout(atual, l.Nome);
                    nomesAgora[l.Handle] = l.Nome;
                    renomeados++;
                }
                catch (System.Exception ex)
                {
                    avisos.Add($"renomear \"{atual}\" para \"{l.Nome}\": {ex.Message}");
                }
            }

            List<string> novos = Diferencas(db, p).Novos;
            if (excluirNovos && novos.Count > 0)
            {
                OpResult r = LayoutOps.Excluir(doc, novos);
                excluidos = r.Ok;
                if (r.Falhas > 0)
                    avisos.Add(r.Mensagem);
            }

            // Ordem: a do ponto; layouts novos (se ficaram) vao para o fim, na ordem em que estao.
            List<string> ordem = p.Layouts.OrderBy(l => l.Ordem)
                .Select(l => nomesAgora.TryGetValue(l.Handle, out string? n) ? n : null)
                .Where(n => n is not null).Select(n => n!).ToList();
            ordemOk = LayoutOps.GravarOrdem(db, ordem);
            if (!ordemOk)
                avisos.Add("a ordem das abas nao ficou exatamente a do ponto (detalhes no log)");
        }

        foreach (string a in avisos)
            PluginLog.Aviso(Mod, "Restaurar: " + a);
        var partes = new List<string> { $"{vistas} viewport(s) com a vista de volta" };
        if (renomeados > 0)
            partes.Add($"{renomeados} layout(s) com o nome de volta");
        partes.Add(ordemOk ? "ordem das abas restaurada" : "ordem das abas com aviso");
        if (excluidos > 0)
            partes.Add($"{excluidos} layout(s) criado(s) depois excluido(s)");
        return new OpResult(vistas + renomeados + excluidos, avisos.Count,
            $"Restaurado o ponto de {p.Quando:dd/MM HH:mm:ss} (antes de: {p.Acao}): {string.Join(", ", partes)}" +
            (avisos.Count > 0 ? $"; {avisos.Count} aviso(s) no log (Ctrl+Shift+L)." : ". Se nao gostou, ha um ponto de antes desta restauracao na lista."));
    }

    // ------------------------------------------------------------------

    private static Ponto Ler(Database db, string acao)
    {
        var p = new Ponto { Quando = DateTime.Now, Acao = acao };
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var dic = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in dic)
        {
            var lay = (Autodesk.AutoCAD.DatabaseServices.Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            if (lay.ModelType)
                continue;
            p.Layouts.Add(new LayoutEstado(lay.Handle.Value, lay.LayoutName, lay.TabOrder));
            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            // A viewport do proprio papel (zoom do layout) fica de fora.
            HashSet<ObjectId> papel = LayoutReader.ViewportsDoPapel(tr, lay, btr);
            foreach (ObjectId id in btr)
            {
                if (papel.Contains(id) || id.ObjectClass.DxfName != "VIEWPORT" || tr.GetObject(id, OpenMode.ForRead) is not Viewport vp)
                    continue;
                p.Viewports.Add(Estado(vp));
            }
        }
        tr.Commit();
        return p;
    }

    private static VpEstado Estado(Viewport vp) => new(
        vp.Handle.Value, vp.CenterPoint, vp.Width, vp.Height,
        vp.ViewCenter, vp.ViewTarget, vp.ViewDirection, vp.TwistAngle, vp.CustomScale,
        vp.Locked, vp.On);

    private static bool Igual(VpEstado a, VpEstado b)
    {
        static bool N(double x, double y) => Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        return N(a.ViewCenter.X, b.ViewCenter.X) && N(a.ViewCenter.Y, b.ViewCenter.Y) &&
               a.ViewTarget.IsEqualTo(b.ViewTarget) && a.ViewDirection.IsEqualTo(b.ViewDirection) &&
               N(a.Twist, b.Twist) && N(a.Escala, b.Escala) &&
               a.Centro.IsEqualTo(b.Centro) && N(a.Largura, b.Largura) && N(a.Altura, b.Altura) &&
               a.Travada == b.Travada && a.Ligada == b.Ligada;
    }

    /// <summary>Uma pasta por desenho: nome do arquivo + hash do caminho completo.</summary>
    private static string Pasta(Database db)
    {
        string caminho = string.IsNullOrEmpty(db.Filename) ? "sem-nome" : Path.GetFullPath(db.Filename).ToUpperInvariant();
        uint h = 2166136261;
        foreach (char c in caminho)
            h = (h ^ c) * 16777619;
        string nome = Path.GetFileNameWithoutExtension(caminho);
        foreach (char c in Path.GetInvalidFileNameChars())
            nome = nome.Replace(c, '_');
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Civil3D2026Plugin", "restauracao", $"{nome}_{h:X8}");
    }

    private static string D(double v) => v.ToString("R", Inv);

    private static string Escrever(Ponto p)
    {
        var sb = new StringBuilder();
        sb.Append("Q\t").Append(p.Quando.ToString("o", Inv)).Append('\n');
        sb.Append("A\t").Append(p.Acao.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
        foreach (LayoutEstado l in p.Layouts)
            sb.Append("L\t").Append(l.Handle.ToString(Inv)).Append('\t').Append(l.Ordem.ToString(Inv)).Append('\t').Append(l.Nome).Append('\n');
        foreach (VpEstado v in p.Viewports)
            sb.Append(string.Join("\t", "V", v.Handle.ToString(Inv),
                D(v.Centro.X), D(v.Centro.Y), D(v.Largura), D(v.Altura),
                D(v.ViewCenter.X), D(v.ViewCenter.Y),
                D(v.ViewTarget.X), D(v.ViewTarget.Y), D(v.ViewTarget.Z),
                D(v.ViewDirection.X), D(v.ViewDirection.Y), D(v.ViewDirection.Z),
                D(v.Twist), D(v.Escala), v.Travada ? "1" : "0", v.Ligada ? "1" : "0")).Append('\n');
        return sb.ToString();
    }

    private static Ponto LerArquivo(string arquivo)
    {
        DateTime quando = File.GetLastWriteTime(arquivo);
        string acao = "";
        var layouts = new List<LayoutEstado>();
        var vps = new List<VpEstado>();
        foreach (string linha in File.ReadAllLines(arquivo, Encoding.UTF8))
        {
            string[] c = linha.Split('\t');
            double N(int i) => double.Parse(c[i], Inv);
            switch (c[0])
            {
                case "Q":
                    quando = DateTime.Parse(c[1], Inv, DateTimeStyles.RoundtripKind);
                    break;
                case "A":
                    acao = c.Length > 1 ? c[1] : "";
                    break;
                case "L" when c.Length >= 4:
                    layouts.Add(new LayoutEstado(long.Parse(c[1], Inv), string.Join("\t", c.Skip(3)), int.Parse(c[2], Inv)));
                    break;
                case "V" when c.Length >= 18:
                    vps.Add(new VpEstado(long.Parse(c[1], Inv), new Point3d(N(2), N(3), 0), N(4), N(5),
                        new Point2d(N(6), N(7)), new Point3d(N(8), N(9), N(10)), new Vector3d(N(11), N(12), N(13)),
                        N(14), N(15), c[16] == "1", c[17] == "1"));
                    break;
            }
        }
        var p = new Ponto { Quando = quando, Acao = acao };
        p.Layouts.AddRange(layouts);
        p.Viewports.AddRange(vps);
        return p;
    }
}
