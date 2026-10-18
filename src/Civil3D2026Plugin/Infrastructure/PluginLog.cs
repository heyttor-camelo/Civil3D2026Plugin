using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3D2026Plugin.Infrastructure;

internal enum LogNivel
{
    Info,
    Aviso,
    Erro,
    Tempo,
    Diag
}

internal sealed record LogEntrada(DateTime Quando, LogNivel Nivel, string Modulo, string Texto)
{
    public string Rotulo => Nivel switch
    {
        LogNivel.Aviso => "AVISO",
        LogNivel.Erro => "ERRO",
        LogNivel.Tempo => "TEMPO",
        LogNivel.Diag => "DIAG",
        _ => "INFO"
    };

    /// <summary>Linha para copiar/gravar; linhas seguintes de um texto longo vao indentadas.</summary>
    public string Linha => $"{Quando:HH:mm:ss.fff} {Rotulo,-5} [{Modulo}] {Texto.Replace("\n", "\n                         ")}";
}

/// <summary>
/// Log comum a todos os plugins da DLL: memoria (ultimas entradas, para a janela C3DLOG e a aba
/// escondida dos paineis) e arquivo diario em %LOCALAPPDATA%\Civil3D2026Plugin\logs. Registra
/// tambem os comandos do plugin (inicio, fim, falha, tempo), erros nao tratados das janelas e,
/// com o diagnostico detalhado ligado, excecoes tratadas dentro do codigo do plugin.
/// </summary>
internal static class PluginLog
{
    private const int Maximo = 8000;
    private const int DiasGuardados = 14;
    private static readonly object Trava = new();
    private static readonly Queue<LogEntrada> Fila = new();
    private static readonly Dictionary<string, int> Repetidas = new();
    private static readonly Dictionary<Document, Stopwatch> Comandos = new();
    private static readonly HashSet<Document> DocsLigados = new();
    private static HashSet<string>? _nomesComandos;
    private static StreamWriter? _arquivo;
    private static string _dia = "";
    private static bool _iniciado;

    [ThreadStatic]
    private static bool _dentro;

    /// <summary>Nova entrada (pode vir de qualquer thread).</summary>
    public static event Action<LogEntrada>? Novo;

    /// <summary>Registra excecoes tratadas que passam pelo codigo do plugin (para achar falhas silenciosas).</summary>
    public static bool Detalhado { get; set; }

    public static string Pasta => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3D2026Plugin", "logs");

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    public static void Iniciar()
    {
        if (_iniciado)
            return;
        _iniciado = true;
        LimparAntigos();
        Registrar(LogNivel.Info, "Plugin", "Inicio da sessao. " + Ambiente());
        AppDomain.CurrentDomain.UnhandledException += OnNaoTratada;
        AppDomain.CurrentDomain.FirstChanceException += OnPrimeiraChance;
        try
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += OnJanela;
        }
        catch (Exception)
        {
            // Sem dispatcher WPF: segue sem esse gancho.
        }
        try
        {
            AcApp.DocumentManager.DocumentCreated += (_, e) => LigarDocumento(e.Document);
            foreach (Document d in AcApp.DocumentManager)
                LigarDocumento(d);
        }
        catch (Exception)
        {
        }
    }

    public static void Encerrar()
    {
        if (!_iniciado)
            return;
        _iniciado = false;
        AppDomain.CurrentDomain.UnhandledException -= OnNaoTratada;
        AppDomain.CurrentDomain.FirstChanceException -= OnPrimeiraChance;
        lock (Trava)
        {
            try
            {
                _arquivo?.Dispose();
            }
            catch (Exception)
            {
            }
            _arquivo = null;
        }
    }

    // ------------------------------------------------------------------
    // Registro
    // ------------------------------------------------------------------

    public static void Info(string modulo, string texto) => Registrar(LogNivel.Info, modulo, texto);

    public static void Aviso(string modulo, string texto) => Registrar(LogNivel.Aviso, modulo, texto);

    public static void Erro(string modulo, string texto, Exception? ex = null) =>
        Registrar(LogNivel.Erro, modulo, ex is null ? texto : texto + "\n" + Descrever(ex));

    public static void Diag(string modulo, string texto) => Registrar(LogNivel.Diag, modulo, texto);

    /// <summary>Tempo ja medido (somas de varias etapas, por exemplo).</summary>
    public static void Tempo(string modulo, string texto) => Registrar(LogNivel.Tempo, modulo, texto);

    /// <summary>Mede uma etapa: ao descartar, registra o tempo gasto.</summary>
    public static IDisposable Medir(string modulo, string etapa) => new Cronometro(modulo, etapa);

    private sealed class Cronometro : IDisposable
    {
        private readonly string _modulo, _etapa;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _feito;

        public Cronometro(string modulo, string etapa)
        {
            _modulo = modulo;
            _etapa = etapa;
        }

        public void Dispose()
        {
            if (_feito)
                return;
            _feito = true;
            Registrar(LogNivel.Tempo, _modulo, $"{_etapa}: {_sw.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
        }
    }

    public static List<LogEntrada> Copia()
    {
        lock (Trava)
            return Fila.ToList();
    }

    public static List<string> Modulos() =>
        Copia().Select(e => e.Modulo).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

    public static void Limpar()
    {
        lock (Trava)
        {
            Fila.Clear();
            Repetidas.Clear();
        }
    }

    /// <summary>Texto para copiar e mandar: cabecalho com o ambiente e as linhas.</summary>
    public static string Texto(IEnumerable<LogEntrada> entradas)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Civil3D2026Plugin - log ===");
        sb.AppendLine(Ambiente());
        sb.AppendLine();
        foreach (LogEntrada e in entradas)
            sb.AppendLine(e.Linha);
        return sb.ToString();
    }

    internal static string Ambiente()
    {
        var p = new List<string> { $"plugin {C3DVersions.Plugin}" };
        try
        {
            p.Add($"AutoCAD {AcApp.GetSystemVariable("ACADVER")}");
        }
        catch (Exception)
        {
        }
        try
        {
            Version? civil = typeof(Autodesk.Civil.ApplicationServices.CivilDocument).Assembly.GetName().Version;
            p.Add($"Civil {civil}");
        }
        catch (Exception)
        {
        }
        p.Add($".NET {Environment.Version}");
        p.Add(Environment.OSVersion.VersionString);
        try
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is not null)
                p.Add($"desenho {Path.GetFileName(doc.Name)} (INSUNITS {doc.Database.Insunits})");
        }
        catch (Exception)
        {
        }
        return string.Join(" | ", p);
    }

    private static string Descrever(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (!ReferenceEquals(e, ex))
                sb.Append("  -> ");
            sb.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
        }
        string pilha = ex.StackTrace ?? "";
        IEnumerable<string> linhas = pilha.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(25);
        sb.Append(string.Join("\n", linhas));
        return sb.ToString().TrimEnd();
    }

    private static void Registrar(LogNivel nivel, string modulo, string texto)
    {
        var e = new LogEntrada(DateTime.Now, nivel, modulo, (texto ?? "").TrimEnd());
        lock (Trava)
        {
            Fila.Enqueue(e);
            while (Fila.Count > Maximo)
                Fila.Dequeue();
            Gravar(e);
        }
        try
        {
            Novo?.Invoke(e);
        }
        catch (Exception)
        {
            // Quem escuta nao derruba o log.
        }
    }

    private static void Gravar(LogEntrada e)
    {
        try
        {
            string dia = e.Quando.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_arquivo is null || dia != _dia)
            {
                _arquivo?.Dispose();
                Directory.CreateDirectory(Pasta);
                // Um arquivo por dia e por sessao do AutoCAD: duas sessoes no mesmo arquivo
                // sobrescreveriam as linhas uma da outra (Append nao e atomico entre processos).
                var fs = new FileStream(Path.Combine(Pasta, $"plugin-{dia}-{Environment.ProcessId}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _arquivo = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                _dia = dia;
            }
            _arquivo.WriteLine(e.Linha);
        }
        catch (Exception)
        {
            // Sem disco: fica so na memoria.
            _arquivo = null;
        }
    }

    private static void LimparAntigos()
    {
        try
        {
            if (!Directory.Exists(Pasta))
                return;
            foreach (string f in Directory.GetFiles(Pasta, "plugin-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-DiasGuardados))
                    File.Delete(f);
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------
    // Comandos do plugin (inicio, fim, falha) em todos os desenhos
    // ------------------------------------------------------------------

    private static HashSet<string> NomesComandos()
    {
        if (_nomesComandos is not null)
            return _nomesComandos;
        var set = new HashSet<string>(ModulosDosComandos().Keys, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (CommandCatalog.ModuleDefinition m in CommandCatalog.Modules)
                foreach (CommandCatalog.CommandDefinition c in m.Commands)
                    set.Add(c.Name);
        }
        catch (Exception)
        {
        }
        return _nomesComandos = set;
    }

    private static Dictionary<string, string>? _modulosDosComandos;

    /// <summary>
    /// Comandos da DLL ([CommandMethod]) e o modulo de cada um pelo namespace da classe, o mesmo
    /// nome usado pelas janelas (Layouts, Viario, Secao...), para o filtro da aba Log juntar tudo.
    /// </summary>
    private static Dictionary<string, string> ModulosDosComandos()
    {
        if (_modulosDosComandos is not null)
            return _modulosDosComandos;
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Type?[] tipos;
            try
            {
                tipos = typeof(PluginLog).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                tipos = ex.Types;
            }
            foreach (Type? t in tipos)
            {
                if (t is null)
                    continue;
                MethodInfo[] metodos;
                try
                {
                    metodos = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (MethodInfo m in metodos)
                {
                    try
                    {
                        foreach (Autodesk.AutoCAD.Runtime.CommandMethodAttribute a in m.GetCustomAttributes<Autodesk.AutoCAD.Runtime.CommandMethodAttribute>(false))
                            if (!string.IsNullOrEmpty(a.GlobalName))
                                mapa[a.GlobalName] = ModuloDoNamespace(t.Namespace ?? "");
                    }
                    catch (Exception)
                    {
                        // Atributo ilegivel: o comando fica de fora do log.
                    }
                }
            }
        }
        catch (Exception)
        {
        }
        return _modulosDosComandos = mapa;
    }

    private static void LigarDocumento(Document? doc)
    {
        if (doc is null || !DocsLigados.Add(doc))
            return;
        doc.CommandWillStart += (_, e) =>
        {
            if (!NomesComandos().Contains(e.GlobalCommandName))
                return;
            lock (Trava)
                Comandos[doc] = Stopwatch.StartNew();
            Registrar(LogNivel.Info, ModuloDoComando(e.GlobalCommandName), $"Comando {e.GlobalCommandName} iniciado.");
        };
        doc.CommandEnded += (_, e) => FimComando(doc, e.GlobalCommandName, "terminado");
        doc.CommandCancelled += (_, e) => FimComando(doc, e.GlobalCommandName, "cancelado");
        doc.CommandFailed += (_, e) => FimComando(doc, e.GlobalCommandName, "FALHOU");
        doc.BeginDocumentClose += (_, _) =>
        {
            DocsLigados.Remove(doc);
            lock (Trava)
                Comandos.Remove(doc);
        };
    }

    private static void FimComando(Document doc, string nome, string como)
    {
        if (!NomesComandos().Contains(nome))
            return;
        string tempo = "";
        lock (Trava)
        {
            if (Comandos.Remove(doc, out Stopwatch? sw))
                tempo = $" ({sw.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s)";
        }
        Registrar(como == "FALHOU" ? LogNivel.Erro : LogNivel.Info, ModuloDoComando(nome), $"Comando {nome} {como}{tempo}.");
    }

    private static string ModuloDoComando(string nome) =>
        ModulosDosComandos().TryGetValue(nome, out string? modulo) ? modulo : "Plugin";

    // ------------------------------------------------------------------
    // Excecoes
    // ------------------------------------------------------------------

    private static void OnNaoTratada(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex && ex.ToString().Contains("Civil3D2026Plugin", StringComparison.Ordinal))
            Erro("Plugin", e.IsTerminating ? "Erro fatal (o AutoCAD vai fechar)" : "Erro nao tratado", ex);
    }

    /// <summary>
    /// Erro numa janela do plugin: registra e segura (Handled) para nao derrubar o AutoCAD.
    /// Erros de outras partes do AutoCAD passam direto.
    /// </summary>
    private static void OnJanela(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        string texto = e.Exception.ToString();
        if (!texto.Contains("Civil3D2026Plugin", StringComparison.Ordinal))
            return;
        Erro(ModuloDaPilha(e.Exception.StackTrace ?? ""), "Erro nao tratado numa janela do plugin (a janela continua aberta)", e.Exception);
        e.Handled = true;
        try
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                $"\n[Plugin] Erro na janela: {e.Exception.Message}. Detalhes: {C3DCommands.Core.Log} (ou Ctrl+Shift+L na janela).");
        }
        catch (Exception)
        {
        }
    }

    private static void OnPrimeiraChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (!Detalhado || _dentro)
            return;
        _dentro = true;
        try
        {
            // A mesma excecao em laco (amostras de cota num trecho sem dados, por exemplo): depois
            // de algumas, nem monta a pilha (e cara).
            string previa = e.Exception.GetType().Name + "|" + e.Exception.Message;
            int vistas;
            lock (Trava)
            {
                // Cada mensagem diferente vira uma chave: limpa de vez em quando.
                if (Repetidas.Count > 4000)
                    Repetidas.Clear();
                Repetidas.TryGetValue(previa, out vistas);
                Repetidas[previa] = vistas + 1;
            }
            if (vistas >= 20)
                return;
            StackFrame[] quadros = new StackTrace(1, false).GetFrames();
            string? modulo = null, metodo = null;
            foreach (StackFrame f in quadros)
            {
                Type? t = f.GetMethod()?.DeclaringType;
                string ns = t?.Namespace ?? "";
                if (!ns.StartsWith("Civil3D2026Plugin", StringComparison.Ordinal) || t == typeof(PluginLog))
                    continue;
                modulo = ModuloDoNamespace(ns);
                metodo = $"{t!.Name}.{f.GetMethod()!.Name}";
                break;
            }
            if (modulo is null)
                return;
            string chave = $"{e.Exception.GetType().Name}|{e.Exception.Message}|{metodo}";
            int n;
            lock (Trava)
            {
                Repetidas.TryGetValue(chave, out n);
                Repetidas[chave] = n + 1;
            }
            if (n >= 3)
                return;
            Registrar(LogNivel.Diag, modulo, $"excecao {e.Exception.GetType().Name} em {metodo}: {e.Exception.Message}" +
                                             (n == 2 ? "  (repeticoes seguintes omitidas)" : ""));
        }
        catch (Exception)
        {
        }
        finally
        {
            _dentro = false;
        }
    }

    private static string ModuloDaPilha(string pilha)
    {
        foreach (string linha in pilha.Split('\n'))
        {
            int i = linha.IndexOf("Civil3D2026Plugin.", StringComparison.Ordinal);
            if (i < 0)
                continue;
            string resto = linha[(i + "Civil3D2026Plugin.".Length)..];
            int fim = resto.IndexOf('(');
            string caminho = fim > 0 ? resto[..fim] : resto;
            string[] partes = caminho.Split('.');
            // Namespace.Tipo.Metodo: o modulo e o penultimo segmento do namespace.
            return partes.Length >= 3 ? ModuloDoNamespace("Civil3D2026Plugin." + string.Join(".", partes[..^2])) : "Plugin";
        }
        return "Plugin";
    }

    /// <summary>Civil3D2026Plugin.Modules.[...].Layouts -> "Layouts"; fora de Modules -> "Plugin".</summary>
    private static string ModuloDoNamespace(string ns)
    {
        string[] p = ns.Split('.');
        int i = Array.IndexOf(p, "Modules");
        return i >= 0 && i + 1 < p.Length ? p[^1] : "Plugin";
    }
}
