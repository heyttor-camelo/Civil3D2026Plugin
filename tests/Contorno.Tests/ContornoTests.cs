using Civil3D2026Plugin.Modules.Utilitarios.Contorno;
using Xunit;

namespace Civil3D2026Plugin.Modules.Utilitarios.Contorno.Tests;

public class ContornoUniaoTests
{
    private const double Tol = 0.001;

    private static Laco Ret(double x0, double y0, double x1, double y1, bool horario = false)
    {
        var l = new Laco(new[]
        {
            (new P2(x0, y0), 0.0), (new P2(x1, y0), 0.0), (new P2(x1, y1), 0.0), (new P2(x0, y1), 0.0)
        });
        return horario ? l.Invertido() : l;
    }

    private static IReadOnlyList<Laco> Peca(params Laco[] lacos) => lacos;

    [Fact]
    public void Dois_quadrados_lado_a_lado_viram_um_retangulo()
    {
        var r = ContornoUniao.Unir(new[] { Peca(Ret(0, 0, 1, 1)), Peca(Ret(1, 0, 2, 1)) }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
        Assert.Empty(i.Furos);
        Assert.Equal(2.0, i.Externo.AreaComSinal(), 9);
        Assert.Equal(r.AreaEntrada, r.AreaSaida, 9);
    }

    [Fact]
    public void Grade_3x3_com_sentidos_misturados_vira_um_quadrado()
    {
        var pecas = new List<IReadOnlyList<Laco>>();
        for (int x = 0; x < 3; x++)
            for (int y = 0; y < 3; y++)
                pecas.Add(Peca(Ret(x, y, x + 1, y + 1, horario: ((x + y) % 2) == 1)));
        var r = ContornoUniao.Unir(pecas, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
        Assert.Equal(9.0, r.AreaSaida, 9);
        Assert.Equal(0, r.TrechosAbertos);
    }

    [Fact]
    public void Juncao_em_T_e_quebrada()
    {
        var r = ContornoUniao.Unir(new[]
        {
            Peca(Ret(0, 0, 2, 1)), Peca(Ret(0, 1, 1, 2)), Peca(Ret(1, 1, 2, 2))
        }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
        Assert.Equal(4.0, r.AreaSaida, 9);
    }

    [Fact]
    public void Anel_de_pecas_gera_furo()
    {
        var pecas = new List<IReadOnlyList<Laco>>();
        for (int x = 0; x < 3; x++)
            for (int y = 0; y < 3; y++)
                if (x != 1 || y != 1)
                    pecas.Add(Peca(Ret(x, y, x + 1, y + 1)));
        var r = ContornoUniao.Unir(pecas, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Laco furo = Assert.Single(i.Furos);
        Assert.Equal(4, furo.Count);
        Assert.Equal(-1.0, furo.AreaComSinal(), 9);
        Assert.Equal(8.0, r.AreaSaida, 9);
    }

    [Fact]
    public void Furo_da_propria_hachura_e_respeitado_mesmo_com_sentido_errado()
    {
        // Externo horario e furo anti-horario: a orientacao vem da profundidade.
        var r = ContornoUniao.Unir(new[]
        {
            Peca(Ret(0, 0, 4, 4, horario: true), Ret(1, 1, 2, 2)),
            Peca(Ret(4, 0, 6, 4))
        }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Single(i.Furos);
        Assert.Equal(16.0 - 1.0 + 8.0, r.AreaSaida, 9);
    }

    [Fact]
    public void Pecas_encostadas_so_no_canto_ficam_separadas()
    {
        var r = ContornoUniao.Unir(new[] { Peca(Ret(0, 0, 1, 1)), Peca(Ret(1, 1, 2, 2)) }, Tol);
        Assert.Equal(2, r.Ilhas.Count);
        Assert.All(r.Ilhas, i => Assert.Equal(4, i.Externo.Count));
    }

    [Fact]
    public void Pecas_separadas_continuam_separadas()
    {
        var r = ContornoUniao.Unir(new[] { Peca(Ret(0, 0, 1, 1)), Peca(Ret(3, 0, 4, 1)) }, Tol);
        Assert.Equal(2, r.Ilhas.Count);
    }

    [Fact]
    public void Folga_menor_que_a_tolerancia_e_fechada()
    {
        var r = ContornoUniao.Unir(new[] { Peca(Ret(0, 0, 1, 1)), Peca(Ret(1.0004, 0.0003, 2, 1)) }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
        Assert.Equal(2.0, r.AreaSaida, 2);
    }

    [Fact]
    public void Peca_repetida_nao_duplica_o_contorno()
    {
        var r = ContornoUniao.Unir(new[] { Peca(Ret(0, 0, 1, 1)), Peca(Ret(0, 0, 1, 1)), Peca(Ret(1, 0, 2, 1)) }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
    }

    [Fact]
    public void Dois_setores_de_arco_viram_meio_circulo()
    {
        double q = Math.Tan(Math.PI / 8); // quarto de circulo
        var a = new Laco(new[] { (new P2(0, 0), 0.0), (new P2(1, 0), q), (new P2(0, 1), 0.0) });
        var b = new Laco(new[] { (new P2(0, 0), 0.0), (new P2(0, 1), q), (new P2(-1, 0), 0.0) });
        var r = ContornoUniao.Unir(new[] { Peca(a), Peca(b) }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(2, i.Externo.Count);
        Assert.Contains(i.Externo.Bulges, x => Math.Abs(x - 1.0) < 1e-9);
        Assert.Equal(Math.PI / 2, r.AreaSaida, 9);
    }

    [Fact]
    public void Arco_quebrado_por_vertice_de_outra_peca()
    {
        // Meio disco inteiro e, encostadas na reta de baixo, duas pecas retangulares.
        var disco = new Laco(new[] { (new P2(1, 0), 1.0), (new P2(-1, 0), 0.0) });
        var r = ContornoUniao.Unir(new[] { Peca(disco), Peca(Ret(-1, -1, 0, 0)), Peca(Ret(0, -1, 1, 0)) }, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(Math.PI / 2 + 2.0, r.AreaSaida, 9);
        Assert.Equal(4, i.Externo.Count);
    }

    [Fact]
    public void Faixas_desencontradas_com_muitas_juncoes_em_T()
    {
        // Como o Revit pica uma regiao: linhas com cortes em posicoes diferentes.
        var rnd = new Random(7);
        var pecas = new List<IReadOnlyList<Laco>>();
        for (int y = 0; y < 6; y++)
        {
            double x = 0;
            while (x < 10)
            {
                double w = Math.Min(10 - x, 0.3 + rnd.NextDouble() * 2);
                pecas.Add(Peca(Ret(x, y, x + w, y + 1, horario: rnd.Next(2) == 0)));
                x += w;
            }
        }
        var r = ContornoUniao.Unir(pecas, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
        Assert.Equal(60.0, r.AreaSaida, 6);
        Assert.Equal(0, r.TrechosAbertos);
    }

    [Fact]
    public void Quadrado_em_triangulos()
    {
        P2 c = new(0.5, 0.5);
        P2[] v = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        var pecas = new List<IReadOnlyList<Laco>>();
        for (int k = 0; k < 4; k++)
            pecas.Add(Peca(new Laco(new[] { (v[k], 0.0), (v[(k + 1) % 4], 0.0), (c, 0.0) })));
        var r = ContornoUniao.Unir(pecas, Tol);
        Ilha i = Assert.Single(r.Ilhas);
        Assert.Equal(4, i.Externo.Count);
        Assert.Equal(1.0, r.AreaSaida, 9);
    }
}
