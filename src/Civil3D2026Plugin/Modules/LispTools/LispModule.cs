using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Modules.LispTools;

/// <summary>Uma LISP (ou VLX/FAS) distribuida dentro da DLL.</summary>
public sealed class LispInfo
{
    public string Arquivo { get; init; } = "";
    public string Titulo { get; init; } = "";
    public string Categoria { get; init; } = "";
    public string Versao { get; init; } = "";
    public string Descricao { get; init; } = "";
    public IReadOnlyList<string> Uso { get; init; } = Array.Empty<string>();

    /// <summary>Comandos exibidos na lista (cabecalho ;;@COMANDO ou, sem ele, os detectados).</summary>
    public IReadOnlyList<string> Comandos { get; init; } = Array.Empty<string>();

    /// <summary>Todos os (defun c:XXX) do arquivo; viram stubs de autoload.</summary>
    public IReadOnlyList<string> ComandosDetectados { get; init; } = Array.Empty<string>();

    /// <summary>VLX/FAS ou arquivo sem comando detectavel: carregado inteiro ao abrir o desenho.</summary>
    public bool CarregarInteiro { get; init; }

    public byte[] Conteudo { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// LispTools: LISPs embutidas na DLL (EmbeddedResource em Modules/LispTools/Lisp).
///
/// Na inicializacao os arquivos sao extraidos para
/// %LOCALAPPDATA%\Civil3D2026Plugin\Lisp e um carregador e gerado.
/// Em cada desenho o carregador registra os comandos com o autoload do AutoCAD:
/// a LISP so e carregada no primeiro uso do comando. Isso evita efeitos colaterais
/// de rotinas que executam codigo ao carregar (ex.: NP cria layers) e mensagens
/// de "carregado" em todo desenho aberto.
///
/// Para adicionar uma LISP: copie o arquivo para Modules/LispTools/Lisp, ponha o
/// cabecalho ;;@ (veja Lisp/README.md) e recompile.
/// </summary>
public static class LispModule
{
    private const string FlagCarregado = "Civil3D2026Plugin.LispTools.Carregado";
    private const string LoaderName = "_LispTools_carregar.lsp";

    private static readonly string[] Extensoes = { ".lsp", ".vlx", ".fas" };

    private static List<LispInfo>? _catalogo;
    private static string? _loader;
    private static bool _iniciado;

    /// <summary>Pasta onde as LISPs ficam no disco.</summary>
    public static string Pasta { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Civil3D2026Plugin", "Lisp");

    /// <summary>Todas as LISPs embutidas, por categoria e titulo.</summary>
    public static IReadOnlyList<LispInfo> Catalogo => _catalogo ??= LerCatalogo();

    // ------------------------------------------------------------------
    // Ciclo de vida (chamado por PluginApplication)
    // ------------------------------------------------------------------

    public static void Initialize()
    {
        if (_iniciado)
            return;
        _iniciado = true;

        try
        {
            ExtrairArquivos();
            // O carregamento acontece no Idle: durante o NETLOAD ha um comando em
            // andamento e o desenho nao aceita a string ainda.
            AcApp.Idle += OnIdle;
        }
        catch (System.Exception ex)
        {
            Escrever($"\n[LispTools] Falha ao iniciar: {ex.Message}");
        }
    }

    public static void Terminate()
    {
        if (!_iniciado)
            return;
        AcApp.Idle -= OnIdle;
        _iniciado = false;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        try
        {
            CarregarEm(AcApp.DocumentManager.MdiActiveDocument, forcar: false);
        }
        catch (System.Exception)
        {
            // Idle nao pode derrubar o AutoCAD; tenta de novo no proximo.
        }
    }

    // ------------------------------------------------------------------
    // Carregamento
    // ------------------------------------------------------------------

    /// <summary>
    /// Envia o carregador para o desenho. Sem forcar, so uma vez por desenho e
    /// somente quando o desenho estiver ocioso.
    /// </summary>
    internal static bool CarregarEm(Document? doc, bool forcar)
    {
        if (doc is null || _loader is null || Catalogo.Count == 0)
            return false;
        if (!forcar && doc.UserData.ContainsKey(FlagCarregado))
            return false;
        if (!forcar && !doc.Editor.IsQuiescent)
            return false;

        doc.UserData[FlagCarregado] = true;
        doc.SendStringToExecute($"(load \"{ParaLisp(_loader)}\") ", false, false, false);
        return true;
    }

    /// <summary>
    /// Grava as LISPs na pasta local (so as que mudaram) e gera o carregador.
    /// </summary>
    internal static void ExtrairArquivos()
    {
        Directory.CreateDirectory(Pasta);

        var sb = new StringBuilder();
        sb.AppendLine(";; Gerado automaticamente pelo Civil3D2026Plugin (LispTools). Nao editar.");
        sb.AppendLine("(vl-load-com)");
        sb.AppendLine("(defun lispTools:carregar (f cmds / r)");
        sb.AppendLine("  (setq r (vl-catch-all-apply");
        sb.AppendLine("            (if (and cmds autoload) 'autoload 'load)");
        sb.AppendLine("            (if (and cmds autoload) (list f cmds) (list f))))");
        sb.AppendLine("  (if (vl-catch-all-error-p r)");
        sb.AppendLine("    (princ (strcat \"\\n[LispTools] Erro ao carregar \" f \": \" (vl-catch-all-error-message r))))");
        sb.AppendLine("  (princ))");

        foreach (LispInfo lisp in Catalogo)
        {
            string destino = Path.Combine(Pasta, lisp.Arquivo);
            try
            {
                if (!File.Exists(destino) || !File.ReadAllBytes(destino).AsSpan().SequenceEqual(lisp.Conteudo))
                    File.WriteAllBytes(destino, lisp.Conteudo);
            }
            catch (IOException ex)
            {
                // Arquivo em uso: a copia que ja esta no disco continua valendo.
                Escrever($"\n[LispTools] Nao foi possivel atualizar {lisp.Arquivo}: {ex.Message}");
                if (!File.Exists(destino))
                    continue;
            }

            string cmds = lisp.CarregarInteiro || lisp.ComandosDetectados.Count == 0
                ? "nil"
                : "'(" + string.Join(" ", lisp.ComandosDetectados.Select(c => $"\"{c}\"")) + ")";
            sb.AppendLine($"(lispTools:carregar \"{ParaLisp(destino)}\" {cmds})");
        }

        sb.AppendLine("(princ)");

        _loader = Path.Combine(Pasta, LoaderName);
        File.WriteAllText(_loader, sb.ToString(), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------
    // Catalogo a partir dos recursos embutidos
    // ------------------------------------------------------------------

    private static readonly Regex ReDefun =
        new(@"\(\s*defun\s+c:([^\s()]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<LispInfo> LerCatalogo()
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string[] recursos = asm.GetManifestResourceNames();

        // Cabecalhos avulsos (.info) para arquivos compilados, que nao podem ser lidos.
        var infos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string nome in recursos.Where(n => n.EndsWith(".info", StringComparison.OrdinalIgnoreCase)))
            infos[NomeArquivo(nome)[..^5]] = Decodificar(LerRecurso(asm, nome));

        var lista = new List<LispInfo>();
        foreach (string nome in recursos)
        {
            string arquivo = NomeArquivo(nome);
            string ext = Path.GetExtension(arquivo);
            if (!Extensoes.Contains(ext, StringComparer.OrdinalIgnoreCase))
                continue;

            byte[] bytes = LerRecurso(asm, nome);
            bool compilado = !ext.Equals(".lsp", StringComparison.OrdinalIgnoreCase);
            string texto = compilado ? "" : Decodificar(bytes);
            string cabecalho = infos.TryGetValue(arquivo, out string? info) ? info : texto;

            string titulo = "", categoria = "", versao = "";
            var desc = new List<string>();
            var uso = new List<string>();
            var cmds = new List<string>();

            foreach (string bruta in cabecalho.Split('\n'))
            {
                string t = bruta.Trim();
                if (!t.StartsWith(";;@", StringComparison.Ordinal))
                    continue;

                int sp = t.IndexOf(' ');
                string tag = (sp < 0 ? t : t[..sp]).ToUpperInvariant();
                string val = sp < 0 ? "" : t[(sp + 1)..].Trim();
                switch (tag)
                {
                    case ";;@TITULO": titulo = val; break;
                    case ";;@CATEGORIA": categoria = val; break;
                    case ";;@VERSAO": versao = val; break;
                    case ";;@DESC": desc.Add(val); break;
                    case ";;@USO": uso.Add(val); break;
                    case ";;@COMANDO":
                        if (val.Length > 0)
                            cmds.Add(val.ToUpperInvariant());
                        break;
                }
            }

            List<string> detectados = ReDefun.Matches(texto)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .Distinct()
                .ToList();

            lista.Add(new LispInfo
            {
                Arquivo = arquivo,
                Titulo = titulo.Length > 0 ? titulo : Path.GetFileNameWithoutExtension(arquivo),
                Categoria = categoria.Length > 0 ? categoria : "Outras",
                Versao = versao,
                Descricao = desc.Count > 0 ? string.Join(" ", desc) : "(sem descricao no cabecalho)",
                Uso = uso,
                Comandos = cmds.Count > 0 ? cmds : detectados,
                ComandosDetectados = detectados,
                CarregarInteiro = compilado || detectados.Count == 0,
                Conteudo = bytes
            });
        }

        return lista
            .OrderBy(l => l.Categoria, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Titulo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static byte[] LerRecurso(Assembly asm, string nome)
    {
        using Stream? s = asm.GetManifestResourceStream(nome);
        if (s is null)
            return Array.Empty<byte>();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string NomeArquivo(string recurso)
    {
        // LogicalName "Lisp\X.lsp" (definido no .csproj) -> "X.lsp".
        int barra = recurso.LastIndexOfAny(new[] { '\\', '/' });
        return barra >= 0 ? recurso[(barra + 1)..] : recurso;
    }

    private static string Decodificar(byte[] b)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(b);
        }
    }

    private static string ParaLisp(string caminho) => caminho.Replace('\\', '/');

    // ------------------------------------------------------------------
    // Texto para a linha de comando (banner, C3DHELP e C3DLISPLIST)
    // ------------------------------------------------------------------

    /// <summary>Arvore das LISPs para o banner/C3DHELP, no mesmo formato do catalogo.</summary>
    public static void WriteTree(Editor ed, bool includeDescriptions)
    {
        ed.WriteMessage($"\n+ LISPs embutidas ({Catalogo.Count}) - {C3DCommands.LispTools.Painel} abre a lista");
        foreach (IGrouping<string, LispInfo> grupo in Catalogo.GroupBy(l => l.Categoria))
        {
            ed.WriteMessage($"\n  |-- {grupo.Key}");
            foreach (LispInfo l in grupo)
            {
                ed.WriteMessage($"\n  |   |-- {string.Join(", ", l.Comandos)}");
                if (includeDescriptions)
                    ed.WriteMessage($" - {l.Titulo}");
            }
        }
    }

    internal static void Escrever(string msg) =>
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(msg);
}
