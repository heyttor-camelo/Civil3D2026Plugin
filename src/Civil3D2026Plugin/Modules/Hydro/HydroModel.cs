using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>Ponto em planta (E, N).</summary>
internal readonly record struct P2(double X, double Y)
{
    public static double Dist(P2 a, P2 b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}

/// <summary>Caixa envolvente em planta.</summary>
internal readonly struct Bounds
{
    public readonly double MinX, MinY, MaxX, MaxY;
    public bool IsEmpty => MinX > MaxX;
    public static readonly Bounds Empty = new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public Bounds(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public Bounds Add(P2 p) => new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public Bounds Union(Bounds b) => b.IsEmpty ? this : IsEmpty ? b : new(Math.Min(MinX, b.MinX), Math.Min(MinY, b.MinY), Math.Max(MaxX, b.MaxX), Math.Max(MaxY, b.MaxY));

    public Bounds Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public P2 Center => new((MinX + MaxX) / 2.0, (MinY + MaxY) / 2.0);
}

/// <summary>Uma Pipe Network do desenho, lida de uma vez (sem objetos do AutoCAD).</summary>
internal sealed class NetworkInfo
{
    public string Handle { get; init; } = "";
    public string Name { get; init; } = "";
    public string PartsList { get; init; } = "";
    public string ReferenceSurface { get; init; } = "";
    public string ReferenceAlignment { get; init; } = "";
    public List<StructureInfo> Structures { get; } = new();
    public List<PipeInfo> Pipes { get; } = new();
    public Dictionary<string, StructureInfo> StructureByHandle { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PipeInfo> PipeByHandle { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double Length2D => Pipes.Sum(p => p.Length2D);

    public Bounds Bounds
    {
        get
        {
            Bounds b = Bounds.Empty;
            foreach (StructureInfo s in Structures)
                b = b.Add(s.Point);
            foreach (PipeInfo p in Pipes)
                b = b.Add(p.Start).Add(p.End);
            return b;
        }
    }

    /// <summary>Estruturas na ordem de montante para jusante (ordem topologica pelo fluxo; empates por nome).</summary>
    public List<StructureInfo> Ordered()
    {
        Dictionary<string, int> pending = Structures.ToDictionary(s => s.Handle, s => s.PipesIn.Count, StringComparer.OrdinalIgnoreCase);
        List<StructureInfo> result = new();
        HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
        while (result.Count < Structures.Count)
        {
            StructureInfo? next = Structures.Where(s => !done.Contains(s.Handle) && pending[s.Handle] <= 0)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (next is null)
            {
                // Ciclo ou tubo sem sentido: entra o que sobrou, por nome.
                next = Structures.Where(s => !done.Contains(s.Handle)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).First();
            }

            done.Add(next.Handle);
            result.Add(next);
            foreach (string pipeHandle in next.PipesOut)
            {
                if (PipeByHandle.TryGetValue(pipeHandle, out PipeInfo? p) && p.ToHandle is string to && pending.ContainsKey(to))
                    pending[to]--;
            }
        }

        return result;
    }

    /// <summary>Tubos na ordem das estruturas de montante.</summary>
    public List<PipeInfo> OrderedPipes()
    {
        List<PipeInfo> result = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (StructureInfo s in Ordered())
        {
            foreach (string h in s.PipesOut)
            {
                if (seen.Add(h) && PipeByHandle.TryGetValue(h, out PipeInfo? p))
                    result.Add(p);
            }
        }

        foreach (PipeInfo p in Pipes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(p.Handle))
                result.Add(p);
        }

        return result;
    }

    /// <summary>Caminho de montante para jusante a partir de uma estrutura (segue o primeiro tubo de saida).</summary>
    public List<PipeInfo> PathDownstream(string structureHandle)
    {
        List<PipeInfo> path = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string? current = structureHandle;
        while (current is not null && seen.Add(current) && StructureByHandle.TryGetValue(current, out StructureInfo? s))
        {
            PipeInfo? outPipe = s.PipesOut.Select(h => PipeByHandle.GetValueOrDefault(h)).FirstOrDefault(p => p is not null);
            if (outPipe is null)
                break;
            path.Add(outPipe);
            current = outPipe.ToHandle;
        }

        return path;
    }

    /// <summary>Estruturas a montante (inclusive a propria).</summary>
    public HashSet<string> Upstream(string structureHandle)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> stack = new();
        stack.Push(structureHandle);
        while (stack.Count > 0)
        {
            string h = stack.Pop();
            if (!set.Add(h) || !StructureByHandle.TryGetValue(h, out StructureInfo? s))
                continue;
            foreach (string pipeHandle in s.PipesIn)
            {
                if (PipeByHandle.TryGetValue(pipeHandle, out PipeInfo? p) && p.FromHandle is string from)
                    stack.Push(from);
            }
        }

        return set;
    }
}

/// <summary>Estrutura (PV, caixa, boca de lobo) da rede.</summary>
internal sealed class StructureInfo
{
    public string Handle { get; init; } = "";
    public string Name { get; set; } = "";
    public string NetworkHandle { get; init; } = "";
    public string Family { get; set; } = "";
    public string Size { get; set; } = "";
    public string Type { get; set; } = "";
    public P2 Point { get; set; }
    public double Rotation { get; set; }
    public double Rim { get; set; }
    public double Sump { get; set; }
    public double SumpDepth { get; set; }
    public double SurfaceElevation { get; set; } = double.NaN;
    public double InnerDiameter { get; set; }
    public string ControlSumpBy { get; set; } = "";
    public string RuleSet { get; set; } = "";
    public string RefSurface { get; set; } = "";
    public double Hgl { get; set; } = double.NaN;
    public double Egl { get; set; } = double.NaN;
    public double KnownFlow { get; set; }

    /// <summary>Tubos que chegam (handles) e que saem, pelo FlowDirection.</summary>
    public List<string> PipesIn { get; } = new();
    public List<string> PipesOut { get; } = new();
    public List<string> PipesUndefined { get; } = new();
    public List<string> Catchments { get; } = new();
    public List<string> Warnings { get; } = new();

    public double Depth => Rim - Sump;
    public int PipeCount => PipesIn.Count + PipesOut.Count + PipesUndefined.Count;
}

/// <summary>Tubo da rede, com montante/jusante pelo FlowDirection.</summary>
internal sealed class PipeInfo
{
    public string Handle { get; init; } = "";
    public string Name { get; set; } = "";
    public string NetworkHandle { get; init; } = "";
    public string Family { get; set; } = "";
    public string Size { get; set; } = "";
    public string StartHandle { get; set; } = "";
    public string EndHandle { get; set; } = "";
    public string FlowDirection { get; set; } = "";
    public P2 Start { get; set; }
    public P2 End { get; set; }

    /// <summary>Elevacao do eixo nas pontas (objeto Pipe).</summary>
    public double StartZ { get; set; }
    public double EndZ { get; set; }
    public double InnerDiameter { get; set; }
    public double OuterDiameter { get; set; }
    public double InnerHeight { get; set; }
    public double Length2D { get; set; }
    public double Length3D { get; set; }
    public double CoverStart { get; set; } = double.NaN;
    public double CoverEnd { get; set; } = double.NaN;
    public double MinimumCover { get; set; } = double.NaN;
    public double MaximumCover { get; set; } = double.NaN;
    public double Manning { get; set; } = double.NaN;
    public double FlowRate { get; set; } = double.NaN;
    public double HglUp { get; set; } = double.NaN;
    public double HglDown { get; set; } = double.NaN;
    public double EglUp { get; set; } = double.NaN;
    public double EglDown { get; set; } = double.NaN;
    public string RuleSet { get; set; } = "";
    public List<string> Warnings { get; } = new();

    /// <summary>Estrutura de montante/jusante pelo fluxo (nulas se o tubo e bidirecional ou solto).</summary>
    public string? FromHandle { get; set; }
    public string? ToHandle { get; set; }
    public bool Defined => FromHandle is not null && ToHandle is not null;

    /// <summary>Se a ponta Start do objeto e a montante.</summary>
    public bool StartIsUpstream => FlowDirection != "EndToStart";

    public double StartInvert => StartZ - (InnerHeight / 2.0);
    public double EndInvert => EndZ - (InnerHeight / 2.0);
    public double UpInvert => StartIsUpstream ? StartInvert : EndInvert;
    public double DownInvert => StartIsUpstream ? EndInvert : StartInvert;
    public P2 UpPoint => StartIsUpstream ? Start : End;
    public P2 DownPoint => StartIsUpstream ? End : Start;

    /// <summary>Declividade no sentido do fluxo (positiva descendo), m/m.</summary>
    public double Slope => Length2D > 1e-9 ? (UpInvert - DownInvert) / Length2D : 0;
    public double CoverUp => StartIsUpstream ? CoverStart : CoverEnd;
    public double CoverDown => StartIsUpstream ? CoverEnd : CoverStart;
    public double MinCoverActual => Math.Min(CoverStart, CoverEnd);
}

/// <summary>Bacia (Catchment) do desenho.</summary>
internal sealed class CatchmentInfo
{
    public string Handle { get; init; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";

    /// <summary>Area em m2.</summary>
    public double Area { get; set; }
    public double C { get; set; }

    /// <summary>Tempo de concentracao em minutos.</summary>
    public double Tc { get; set; }
    public string StructureHandle { get; set; } = "";
    public string StructureName { get; set; } = "";
    public string NetworkName { get; set; } = "";
    public string TcMethod { get; set; } = "";
    public double FlowLength { get; set; }
    public List<P2> Boundary { get; } = new();
    public P2 Centroid { get; set; }
    public P2 Discharge { get; set; }
    public double AreaHa => Area / 10000.0;
}

/// <summary>Tudo que o painel le do desenho de uma vez.</summary>
internal sealed class HydroSnapshot
{
    public List<NetworkInfo> Networks { get; } = new();
    public List<CatchmentInfo> Catchments { get; } = new();
    public List<string> Groups { get; } = new();
    public string Drawing { get; init; } = "";

    public NetworkInfo? Find(string? handle) => Networks.FirstOrDefault(n => string.Equals(n.Handle, handle, StringComparison.OrdinalIgnoreCase));
}
