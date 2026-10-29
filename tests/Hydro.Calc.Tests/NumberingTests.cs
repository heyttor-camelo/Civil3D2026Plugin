using Civil3D2026Plugin.Modules.Hydro.Calc;
using Xunit;

namespace Civil3D2026Plugin.Modules.Hydro.Calc.Tests;

public class NumberingTests
{
    // A(PV) -> B(CX) -> D(PV) <- C(PV), saida em D.
    private static (List<NumNode>, List<NumEdge>) Net()
    {
        List<NumNode> n = new()
        {
            new("A", "PV", "x1", 0, 10), new("B", "CX", "x2", 10, 10), new("C", "PV", "x3", 20, 0), new("D", "PV", "x4", 20, 10)
        };
        List<NumEdge> e = new()
        {
            new("t1", "p1", "A", "B", "A", "B", 10), new("t2", "p2", "B", "D", "B", "D", 10), new("t3", "p3", "C", "D", "C", "D", 10)
        };
        return (n, e);
    }

    [Fact]
    public void SamePrefixDifferentTypesShareSequence()
    {
        (List<NumNode> n, List<NumEdge> e) = Net();
        NumOptions o = new();
        o.Groups["PV"] = new(true, "PV-");
        o.Groups["CX"] = new(true, "PV-");
        List<NumResult> r = Numbering.Plan(n, e, o, out List<string> w);
        Dictionary<string, string> names = r.Where(x => !x.IsPipe).ToDictionary(x => x.Id, x => x.NewName);
        Assert.Equal("PV-01", names["A"]);
        Assert.Equal("PV-02", names["B"]);
        Assert.Equal("PV-03", names["C"]);
        Assert.Equal("PV-04", names["D"]);
        Assert.Empty(w);
        Assert.Equal(new[] { "T-01", "T-02", "T-03" }, r.Where(x => x.IsPipe).OrderBy(x => x.Id).Select(x => x.NewName));
    }

    [Fact]
    public void OnlyOneTypeAndSkipsUsedNames()
    {
        (List<NumNode> n, List<NumEdge> e) = Net();
        NumOptions o = new();
        o.Groups["PV"] = new(false, "PV-");
        o.Groups["CX"] = new(true, "CX-");
        o.Pipes = new(false, "T-");
        o.UsedOutside.Add("CX-1");
        o.StartByPrefix["CX-"] = 1;
        List<NumResult> r = Numbering.Plan(n, e, o, out _);
        Assert.Single(r);
        Assert.Equal("CX-02", r[0].NewName);
    }

    [Fact]
    public void SuggestStartContinuesAfterUsed()
    {
        Assert.Equal(8, Numbering.SuggestStart("PV-", new[] { "PV-07", "PV-3", "T-20", "PVX-50" }));
        Assert.Equal("PV-", Numbering.PrefixOf("PV-12"));
        Assert.Null(Numbering.PrefixOf("PV"));
    }

    [Fact]
    public void ExcludedKeepsNameAndWarnsCollision()
    {
        (List<NumNode> n, List<NumEdge> e) = Net();
        n[3] = n[3] with { OldName = "PV-02" };
        NumOptions o = new();
        o.Groups["PV"] = new(true, "PV-");
        o.Groups["CX"] = new(true, "PV-");
        o.Excluded.Add("D");
        List<NumResult> r = Numbering.Plan(n, e, o, out List<string> w);
        Assert.DoesNotContain(r, x => x.Id == "D");
        Assert.Contains(w, x => x.Contains("PV-02"));
    }
}
