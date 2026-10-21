using Civil3D2026Plugin.Modules.Hydro.Calc;
using Xunit;

namespace Civil3D2026Plugin.Modules.Hydro.Calc.Tests;

public class ExprTests
{
    private static readonly Dictionary<string, double> Vars = new()
    {
        ["dmax"] = 0.6, ["tampa"] = 102.5, ["fundo"] = 100.0, ["ac"] = 0.8, ["sil"] = 1.2, ["fundo_min"] = 100.12
    };

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("-2 + 5", 3)]
    [InlineData("0,15 * 2", 0.3)]
    [InlineData("0.15 * 2", 0.3)]
    [InlineData("=dmax + 2 * 0,15", 0.9)]
    [InlineData("DMAX + 2 × 0,15", 0.9)]
    [InlineData("max(dmax; 0,8)", 0.8)]
    [InlineData("min(1; 2; -3)", -3)]
    [InlineData("tampa − fundo − ac", 1.7)]
    [InlineData("fundo_min - 0,05", 100.07)]
    [InlineData("teto(0,61 + 0,3; 0,2)", 1.0)]
    [InlineData("teto(0,8; 0,2)", 0.8)]
    [InlineData("piso(1,19; 0,2)", 1.0)]
    [InlineData("arred(1,2345; 2)", 1.23)]
    [InlineData("se(dmax > 0,5; 1,5; 1,2)", 1.5)]
    [InlineData("se(dmax <= 0,5; 1,5; 1,2)", 1.2)]
    [InlineData("limitar(2,7; 1; 2,5)", 2.5)]
    [InlineData("$SIL * 2", 2.4)]
    public void Avalia(string text, double expected)
    {
        Assert.Equal(expected, HydroExpr.Parse(text).Evaluate(Vars), 9);
    }

    [Theory]
    [InlineData("dmax +", "fim inesperado")]
    [InlineData("max(dmax, 0,8", "falta ')'")]
    [InlineData("largura * 2", "'largura' nao existe")]
    [InlineData("foo(1)", "funcao 'foo' desconhecida")]
    [InlineData("1 / 0", "divisao por zero")]
    [InlineData("", "vazia")]
    public void Erros_claros(string text, string piece)
    {
        HydroExprException ex = Assert.Throws<HydroExprException>(() => HydroExpr.Parse(text).Evaluate(Vars));
        Assert.Contains(piece, ex.Message);
    }

    [Fact]
    public void Variaveis_usadas_em_minusculas()
    {
        HydroExpr e = HydroExpr.Parse("max(DMAX; Sil) + ac");
        Assert.Equal(new[] { "ac", "dmax", "sil" }, e.Variables.OrderBy(x => x));
    }
}

public class RuleEngineTests
{
    private static readonly Dictionary<string, double> Vars = new()
    {
        ["dmax"] = 0.6, ["tampa"] = 102.5, ["fundo"] = 100.0, ["siw"] = 1.0, ["sil"] = 1.0
    };

    [Fact]
    public void Regra_usa_resultado_de_outra_em_qualquer_ordem()
    {
        Dictionary<string, string> rules = new() { ["SIL"] = "siw", ["SIW"] = "teto(dmax + 2 * 0,15; 0,2)" };
        HydroRuleResult r = HydroRuleEngine.Evaluate(rules, Vars);
        Assert.Empty(r.Errors);
        Assert.Equal(1.0, r.Values["siw"], 9);
        Assert.Equal(1.0, r.Values["sil"], 9);
    }

    [Fact]
    public void Ciclo_vira_erro_sem_derrubar_as_outras()
    {
        Dictionary<string, string> rules = new() { ["sil"] = "siw + 0,1", ["siw"] = "sil", ["fundo"] = "tampa - 2" };
        HydroRuleResult r = HydroRuleEngine.Evaluate(rules, Vars);
        Assert.Contains("ciclo", r.Errors["sil"]);
        Assert.Contains("siw", r.Errors.Keys);
        Assert.Equal(100.5, r.Values["fundo"], 9);
    }

    [Fact]
    public void Erro_de_sintaxe_fica_so_na_regra_dela()
    {
        Dictionary<string, string> rules = new() { ["sil"] = "dmax +", ["siw"] = "dmax * 2" };
        HydroRuleResult r = HydroRuleEngine.Evaluate(rules, Vars);
        Assert.True(r.Errors.ContainsKey("sil"));
        Assert.Equal(1.2, r.Values["siw"], 9);
    }

    [Fact]
    public void Regra_vazia_e_valor_fixo()
    {
        HydroRuleResult r = HydroRuleEngine.Evaluate(new Dictionary<string, string> { ["sil"] = " " }, Vars);
        Assert.Empty(r.Values);
        Assert.Empty(r.Errors);
    }

    [Theory]
    [InlineData("", 1.0, null)]
    [InlineData("+0,10", 1.0, 1.1)]
    [InlineData("-0,30", 1.0, 0.7)]
    [InlineData("1,5", 1.0, 1.5)]
    [InlineData("=2 * 0,4", 1.0, 0.8)]
    public void Edicao_absoluta_relativa_ou_expressao(string text, double current, double? expected)
    {
        double? v = HydroRuleEngine.EditValue(text, current);
        if (expected is null)
            Assert.Null(v);
        else
            Assert.Equal(expected.Value, v!.Value, 9);
    }

    [Fact]
    public void Lista_fechada_arredonda_para_cima()
    {
        double[] list = { 1.0, 1.2, 1.4, 1.6 };
        Assert.Equal(1.2, HydroRuleEngine.SnapUp(1.05, list, out bool o1), 9);
        Assert.False(o1);
        Assert.Equal(1.4, HydroRuleEngine.SnapUp(1.4, list, out _), 9);
        Assert.Equal(1.6, HydroRuleEngine.SnapUp(2.0, list, out bool o2), 9);
        Assert.True(o2);
    }
}
