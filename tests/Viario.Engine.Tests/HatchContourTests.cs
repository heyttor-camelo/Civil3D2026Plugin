using System.Globalization;
using Civil3D2026Plugin.Modules.Viario;
using Xunit;
using Xunit.Abstractions;

namespace Civil3D2026Plugin.Modules.Viario.Tests;

/// <summary>
/// Contorno da hachura do pavimento (ViarioHatch.Build) sobre redes calculadas pelo motor:
/// nenhum laco pode ter uma reta longa que nao seja borda de bloco (as retas legitimas sao
/// tampas de ponta, ate ~8 m, e emendas de folga, ate 2 m). Uma reta longa fora da grade e
/// uma "cunha": hachura vazando para fora do pavimento.
/// </summary>
public class HatchContourTests
{
    private readonly ITestOutputHelper _out;

    public HatchContourTests(ITestOutputHelper output) => _out = output;

    private static readonly RoadSection Local = new()
    {
        Id = "TIPO", Name = "Local", Pista = 7.0, Sarjeta = 0.35, MeioFio = 0.15, Calcada = 0.0, Raio = 6.0, Faixas = 2
    };

    private static AxisInput Line(string key, double x0, double y0, double x1, double y1, V2 shift) =>
        new(key, new GPath(new[] { Seg.Line(new V2(x0, y0) + shift, new V2(x1, y1) + shift) }), Local);

    private static AxisInput Circle(string key, double cx, double cy, double r, V2 shift)
    {
        V2 c = new V2(cx, cy) + shift;
        V2 e = new(c.X + r, c.Y), n = new(c.X, c.Y + r), w = new(c.X - r, c.Y), s = new(c.X, c.Y - r);
        double b = Math.Tan(Math.PI / 8.0);
        return new AxisInput(key, new GPath(new[] { new Seg(e, n, b), new Seg(n, w, b), new Seg(w, s, b), new Seg(s, e, b) }), Local);
    }

    /// <summary>Rede do tests/viario/teste_rede.scr: cruzamento, T, eixo curvo e rotatoria com ramos.</summary>
    private static List<AxisInput> Network(V2 shift)
    {
        double b = -Math.Tan(Math.PI / 8.0); // quarto de circulo horario (60,40)->(30,70), centro (60,70)
        GPath curved = new(new[] { Seg.Line(new V2(100, 40) + shift, new V2(60, 40) + shift), new Seg(new V2(60, 40) + shift, new V2(30, 70) + shift, b) });
        return new List<AxisInput>
        {
            Line("A", 0, 0, 200, 0, shift),
            Line("B", 100, -80, 100, 80, shift),
            Line("C", 175, 0, 175, -80, shift),
            new("D", curved, Local),
            Circle("R", 300, 0, 15, shift),
            Line("E", 200, 0, 285, 0, shift),
            Line("F", 300, 15, 300, 90, shift)
        };
    }

    /// <summary>
    /// Retas de laco que nao vem de lugar nenhum: nao estao sobre uma linha da rede (face de
    /// meio-fio, ilha, canteiro), nao sao borda de bloco e sao compridas demais para tampa de
    /// ponta ou emenda de folga. E o retrato de uma cunha de hachura vazando.
    /// </summary>
    internal static List<string> Suspicious(ViarioHatch.Plan plan, ViarioResult result, double maxStraight = 9.0)
    {
        List<string> issues = new();
        double tile = plan.TileSize;
        bool OnGrid(V2 a, V2 b)
        {
            if (tile <= 0)
                return false;
            bool SameLine(double u, double v)
            {
                if (Math.Abs(u - v) > 1e-6)
                    return false;
                double k = (u - ViarioHatch.TileOrigin) / tile;
                return Math.Abs(k - Math.Round(k)) < 1e-6;
            }

            return SameLine(a.X, b.X) || SameLine(a.Y, b.Y);
        }

        List<Seg> network = result.Lines
            .Where(l => l.Level is EdgeLevel.FaceMF or EdgeLevel.Ilha or EdgeLevel.Canteiro)
            .SelectMany(l => l.Segs)
            .ToList();
        bool OnNetwork(Seg s)
        {
            // Tres pontos da reta sobre alguma linha da rede (ate 2 mm).
            foreach (double t in new[] { 0.1, 0.5, 0.9 })
            {
                V2 p = s.PointAt(t);
                if (!network.Any(n => n.Closest(p).Dist < 0.002))
                    return false;
            }

            return true;
        }

        for (int i = 0; i < plan.Loops.Count; i++)
        {
            ViarioHatch.Loop loop = plan.Loops[i];
            foreach (Seg s in loop.Segs)
            {
                if (s.IsArc || s.Length <= maxStraight || OnGrid(s.A, s.B) || OnNetwork(s))
                    continue;
                issues.Add($"laco {i} ({(loop.Outer ? "externo" : "furo")}, bloco {ViarioHatch.TileKey(loop.Tile)}, area {loop.Area:0.#} m2): " +
                           $"reta de {s.Length:0.##} m de {Fmt(s.A)} a {Fmt(s.B)}");
            }
        }

        return issues;
    }

    private static string Fmt(V2 p) => "(" + p.X.ToString("0.###", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0.###", CultureInfo.InvariantCulture) + ")";

    private void Report(string title, ViarioResult result, ViarioHatch.Plan plan, List<string> issues)
    {
        _out.WriteLine($"{title}: {result.Nodes.Count} no(s), {result.Lines.Count} linha(s), {result.Corners.Count} esquina(s); " +
                       $"hachura: {plan.Loops.Count} laco(s) ({plan.Loops.Count(l => l.Outer)} externos), {plan.Caps} tampa(s), bloco {plan.TileSize}");
        foreach (string w in result.Warnings)
            _out.WriteLine("  [rede] " + w);
        foreach (string w in plan.Warnings)
            _out.WriteLine("  [hachura] " + w);
        foreach (string w in issues)
            _out.WriteLine("  [CUNHA] " + w);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.0, 0.0, 500.0)]
    [InlineData(0.0, 0.0, 100.0)]
    [InlineData(272000.0, 7445000.0, 0.0)]
    [InlineData(272000.0, 7445000.0, 500.0)]
    [InlineData(272000.0, 7445000.0, 100.0)]
    public void RedeDeTeste_SemCunhas(double dx, double dy, double tile)
    {
        List<AxisInput> axes = Network(new V2(dx, dy));
        ViarioResult result = ViarioEngine.Compute(axes, new EngineSettings());
        ViarioHatch.Plan plan = ViarioHatch.Build(axes, result, tile);
        List<string> issues = Suspicious(plan, result);
        Report($"rede de teste +({dx},{dy}) bloco {tile}", result, plan, issues);

        Assert.True(plan.Loops.Count(l => l.Outer) >= 1, "sem laco externo");
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("aberto", StringComparison.Ordinal));
        Assert.Empty(issues);
    }

    /// <summary>
    /// Refaz o calculo de um retrato gravado pelo plugin (VIARIO_DUMP=caminho do viario-*.json).
    /// Sem a variavel, nao faz nada. Lista os lacos, avisos e cunhas na saida do teste.
    /// </summary>
    [Fact]
    public void RetratoDoDesenho()
    {
        string? path = Environment.GetEnvironmentVariable("VIARIO_DUMP");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _out.WriteLine("VIARIO_DUMP nao definido: nada a refazer.");
            return;
        }

        ViarioDump.Content c = ViarioDump.Deserialize(File.ReadAllText(path));
        _out.WriteLine($"Desenho {c.Drawing}: {c.Axes.Count} eixo(s), {c.Settings.CornerRadius.Count} raio(s), bloco {c.TileSize}");
        ViarioResult result = ViarioEngine.Compute(c.Axes, c.Settings);

        foreach (double tile in new[] { c.TileSize, 0.0 }.Distinct())
        {
            ViarioHatch.Plan plan = ViarioHatch.Build(c.Axes, result, tile);
            List<string> issues = Suspicious(plan, result);
            Report($"retrato bloco {tile}", result, plan, issues);
        }

        string? expect = Environment.GetEnvironmentVariable("VIARIO_DUMP_ASSERT");
        if (expect == "1")
            Assert.Empty(Suspicious(ViarioHatch.Build(c.Axes, result, c.TileSize), result));
    }
}
