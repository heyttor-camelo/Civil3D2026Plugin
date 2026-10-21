using Civil3D2026Plugin.Modules.Hydro.Calc;
using Xunit;

namespace Civil3D2026Plugin.Modules.Hydro.Calc.Tests;

public class RationalTests
{
    [Fact]
    public void Vazao_em_m3s_com_i_em_mmh_e_area_em_ha()
    {
        // C = 0,8; i = 120 mm/h; A = 3 ha -> Q = 0,8 * 120 * 3 / 360 = 0,8 m3/s.
        Assert.Equal(0.8, Rational.Flow(0.8, 120, 3), 6);
    }

    [Fact]
    public void Area_ou_intensidade_zero_dao_vazao_zero()
    {
        Assert.Equal(0, Rational.Flow(0.8, 0, 3));
        Assert.Equal(0, Rational.Flow(0.8, 120, 0));
    }

    [Fact]
    public void C_medio_ponderado_pela_area()
    {
        // 2 ha com C 0,9 e 2 ha com C 0,3 -> 0,6.
        Assert.Equal(0.6, Rational.WeightedC((0.9 * 2) + (0.3 * 2), 4), 9);
    }
}

public class IdfTests
{
    [Fact]
    public void Formula_K_Tr_a_sobre_t_mais_b_c()
    {
        IdfEquation e = new("teste", 1000, 0.2, 10, 0.8);
        // t = 20 min, Tr = 10: 1000 * 10^0.2 / 30^0.8.
        double esperado = 1000 * Math.Pow(10, 0.2) / Math.Pow(30, 0.8);
        Assert.Equal(esperado, e.Intensity(20, 10), 6);
    }

    [Fact]
    public void Intensidade_cai_com_a_duracao_e_sobe_com_Tr()
    {
        IdfEquation e = HydroIdf.Library[0];
        Assert.True(e.Intensity(10, 10) > e.Intensity(60, 10));
        Assert.True(e.Intensity(10, 25) > e.Intensity(10, 10));
    }
}

public class TimeOfConcentrationTests
{
    [Fact]
    public void Kirpich_1km_50m()
    {
        // 57 * (1^3 / 50)^0.385 = 12,6 min.
        Assert.Equal(57 * Math.Pow(1.0 / 50, 0.385), TimeOfConcentration.Kirpich(1000, 50), 6);
        Assert.InRange(TimeOfConcentration.Kirpich(1000, 50), 12.5, 12.8);
    }

    [Fact]
    public void Minimo_de_projeto_prevalece()
    {
        Assert.Equal(10, TimeOfConcentration.WithMinimum(4.2, 10));
        Assert.Equal(15, TimeOfConcentration.WithMinimum(15, 10));
    }

    [Fact]
    public void Compute_usa_o_desnivel_como_declividade()
    {
        double s = TimeOfConcentration.Compute(TcMethod.VenTeChow, 500, 10);
        Assert.Equal(TimeOfConcentration.VenTeChow(500, 0.02), s, 9);
        Assert.True(TimeOfConcentration.Compute(TcMethod.Dooge, 500, 10, 20000) > 0);
        Assert.True(TimeOfConcentration.Compute(TcMethod.Kerby, 100, 2) > 0);
    }

    [Fact]
    public void Chaves_das_formulas_fazem_ida_e_volta()
    {
        foreach (TcMethod m in Enum.GetValues<TcMethod>())
        {
            Assert.True(TimeOfConcentration.TryParse(TimeOfConcentration.Key(m), out TcMethod back));
            Assert.Equal(m, back);
        }
    }
}

public class ManningTests
{
    [Fact]
    public void Vazao_plena_D600_i05_n0013()
    {
        // A = 0,28274 m2; R = D/4 = 0,15 m; Q = A R^(2/3) S^0.5 / n.
        double a = Math.PI * 0.36 / 4;
        double esperado = a * Math.Pow(0.15, 2.0 / 3.0) * Math.Sqrt(0.005) / 0.013;
        Assert.Equal(esperado, Manning.FullFlow(0.6, 0.005, 0.013), 6);
        Assert.InRange(esperado, 0.43, 0.44);
    }

    [Fact]
    public void Meia_secao_tem_metade_da_area_e_mesma_velocidade_da_plena()
    {
        double aMeia = Manning.Area(1.0, 0.5);
        Assert.Equal(Math.PI / 8, aMeia, 6);
        // Raio hidraulico igual ao da plena -> velocidade igual, vazao metade.
        Assert.Equal(Manning.FullFlow(1.0, 0.01, 0.013) / 2, Manning.Flow(1.0, 0.01, 0.013, 0.5), 6);
    }

    [Fact]
    public void Solve_devolve_a_lamina_que_reproduz_a_vazao()
    {
        double q = 0.25;
        ManningResult r = Manning.Solve(q, 0.6, 0.005, 0.013);
        Assert.True(r.Flows);
        Assert.InRange(r.DepthRatio, 0.3, 0.9);
        Assert.Equal(q, Manning.Flow(0.6, 0.005, 0.013, r.DepthRatio), 5);
        Assert.Equal(q / r.Area, r.Velocity, 9);
        Assert.Equal(100.0 / r.Velocity / 60.0, r.TravelTimeMinutes(100), 9);
    }

    [Fact]
    public void Vazao_acima_da_plena_nao_escoa()
    {
        ManningResult r = Manning.Solve(10, 0.3, 0.001, 0.013);
        Assert.False(r.Flows);
        Assert.Equal(1, r.DepthRatio);
    }

    [Fact]
    public void Menor_diametro_que_atende()
    {
        double[] d = { 0.3, 0.4, 0.5, 0.6, 0.8, 1.0 };
        Criteria c = new(MaxDepthRatio: 0.85, MinVelocity: 0.75, MaxVelocity: 5);
        double? menor = Manning.SmallestDiameter(0.25, 0.005, 0.013, d, c);
        Assert.NotNull(menor);
        // Deve ser o primeiro que escoa com y/D <= 0,85 e V >= 0,75.
        ManningResult r = Manning.Solve(0.25, menor!.Value, 0.005, 0.013);
        Assert.True(r.Flows && r.DepthRatio <= 0.85 && r.Velocity >= 0.75);
        double anterior = d.Where(x => x < menor.Value).DefaultIfEmpty(0).Max();
        if (anterior > 0)
        {
            ManningResult ra = Manning.Solve(0.25, anterior, 0.005, 0.013);
            Assert.True(!ra.Flows || ra.DepthRatio > 0.85 || ra.Velocity < 0.75 || ra.Velocity > 5);
        }

        Assert.Null(Manning.SmallestDiameter(50, 0.005, 0.013, d, c));
    }
}

public class NetworkCalcTests
{
    private static readonly IdfEquation Idf = new("teste", 1000, 0.2, 10, 0.8);
    private static readonly Criteria Crit = new(MinTcMinutes: 10);

    [Fact]
    public void Acumula_area_e_C_de_montante_para_jusante()
    {
        // PV1 -(T1)-> PV2 -(T2)-> PV3. Bacias: 1 ha C 0,8 em PV1; 2 ha C 0,5 em PV2.
        List<NodeInput> nodes = new() { new("PV1"), new("PV2"), new("PV3") };
        List<PipeInput> pipes = new()
        {
            new("T1", "PV1", "PV2", 0.6, 0.01, 60, 0.013),
            new("T2", "PV2", "PV3", 0.8, 0.008, 80, 0.013)
        };
        List<CatchmentInput> bacias = new() { new("B1", "PV1", 1, 0.8, 12), new("B2", "PV2", 2, 0.5, 8) };

        NetworkResult r = NetworkCalc.Run(nodes, pipes, bacias, Idf, 10, Crit);

        ReachResult t1 = r.Pipes["T1"];
        Assert.Equal(1, t1.AreaHa, 9);
        Assert.Equal(0.8, t1.C, 9);
        Assert.Equal(12, t1.TcMinutes, 9);
        Assert.Equal(Rational.Flow(0.8, Idf.Intensity(12, 10), 1), t1.Flow, 9);
        Assert.True(t1.Flows && t1.Velocity > 0 && t1.TravelTimeMinutes > 0);

        ReachResult t2 = r.Pipes["T2"];
        Assert.Equal(3, t2.AreaHa, 9);
        Assert.Equal(((0.8 * 1) + (0.5 * 2)) / 3, t2.C, 9);
        // Tc no PV2 = max(8, 12 + percurso em T1).
        Assert.Equal(12 + t1.TravelTimeMinutes, t2.TcMinutes, 9);
        Assert.Equal(Rational.Flow(t2.C, Idf.Intensity(t2.TcMinutes, 10), 3), t2.Flow, 9);
        Assert.True(t2.Flow > t1.Flow);

        // O no final recebe tudo.
        Assert.Equal(3, r.Nodes["PV3"].AreaHa, 9);
        Assert.Empty(r.Messages);
    }

    [Fact]
    public void Tc_minimo_vale_para_bacias_rapidas()
    {
        List<NodeInput> nodes = new() { new("A"), new("B") };
        List<PipeInput> pipes = new() { new("T", "A", "B", 0.4, 0.01, 30, 0.013) };
        List<CatchmentInput> bacias = new() { new("B1", "A", 0.5, 0.9, 3) };
        NetworkResult r = NetworkCalc.Run(nodes, pipes, bacias, Idf, 10, Crit);
        Assert.Equal(10, r.Pipes["T"].TcMinutes, 9);
    }

    [Fact]
    public void Vazao_conhecida_na_estrutura_entra_na_soma()
    {
        List<NodeInput> nodes = new() { new("A", 0.05), new("B") };
        List<PipeInput> pipes = new() { new("T", "A", "B", 0.4, 0.01, 30, 0.013) };
        NetworkResult r = NetworkCalc.Run(nodes, pipes, new List<CatchmentInput>(), Idf, 10, Crit);
        Assert.Equal(0.05, r.Pipes["T"].Flow, 9);
        Assert.Equal(0.05, r.Nodes["B"].Flow, 9);
    }

    [Fact]
    public void Trecho_sem_estrutura_e_bacia_fora_da_rede_viram_avisos()
    {
        List<NodeInput> nodes = new() { new("A") };
        List<PipeInput> pipes = new() { new("T", "A", "X", 0.4, 0.01, 30, 0.013) };
        List<CatchmentInput> bacias = new() { new("B1", "Z", 1, 0.8, 10) };
        NetworkResult r = NetworkCalc.Run(nodes, pipes, bacias, Idf, 10, Crit);
        Assert.Equal(2, r.Messages.Count);
        Assert.Empty(r.Pipes);
    }

    [Fact]
    public void Ciclo_nao_trava_e_avisa()
    {
        List<NodeInput> nodes = new() { new("A"), new("B") };
        List<PipeInput> pipes = new() { new("T1", "A", "B", 0.4, 0.01, 30, 0.013), new("T2", "B", "A", 0.4, 0.01, 30, 0.013) };
        NetworkResult r = NetworkCalc.Run(nodes, pipes, new List<CatchmentInput>(), Idf, 10, Crit);
        Assert.Contains(r.Messages, m => m.Contains("Ciclo"));
        Assert.Equal(2, r.Pipes.Count);
    }
}
