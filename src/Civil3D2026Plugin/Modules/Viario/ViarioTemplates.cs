using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

// =============================================================================
// Modelos de intersecao parametrizados (sem dependencia do AutoCAD).
// Cada modelo gera eixos em coordenadas locais (no no centro, via principal
// no eixo X), com os elementos (gotas, baias, alargamentos) e as faixas livres.
// =============================================================================

internal enum TemplateRole
{
    Principal,
    Secundaria,
    Anel
}

internal sealed class TemplateParam
{
    public TemplateParam(string key, string label, double value, double min = 0.0, double max = 1e6)
    {
        Key = key;
        Label = label;
        Value = value;
        Min = min;
        Max = max;
    }

    public string Key { get; }
    public string Label { get; }
    public double Value { get; set; }
    public double Min { get; }
    public double Max { get; }
}

internal sealed class TemplateAxis
{
    public TemplateRole Role { get; init; }
    public List<V2> Points { get; init; } = new();
    public List<double> Bulges { get; init; } = new();
    public bool IsCircle { get; init; }
    public V2 Center { get; init; }
    public double Radius { get; init; }
    public List<AxisEvent> Events { get; init; } = new();

    public GPath ToPath()
    {
        if (IsCircle)
        {
            return new GPath(new[]
            {
                Seg.FromArc(Center, Radius, 0.0, Math.PI),
                Seg.FromArc(Center, Radius, Math.PI, Math.PI)
            });
        }

        List<Seg> segs = new();
        for (int i = 1; i < Points.Count; i++)
            segs.Add(new Seg(Points[i - 1], Points[i], i - 1 < Bulges.Count ? Bulges[i - 1] : 0.0));
        return new GPath(segs);
    }

    public TemplateAxis Transform(V2 origin, double angle) => new()
    {
        Role = Role,
        Points = Points.Select(p => ViarioTemplates.Place(p, origin, angle)).ToList(),
        Bulges = new List<double>(Bulges),
        IsCircle = IsCircle,
        Center = ViarioTemplates.Place(Center, origin, angle),
        Radius = Radius,
        Events = Events.Select(e => e.Clone()).ToList()
    };
}

internal sealed class TemplateSlip
{
    public V2 Hint { get; init; }
    public SlipLane Slip { get; init; } = new();
}

internal sealed class TemplateBuild
{
    public List<TemplateAxis> Axes { get; } = new();
    public List<TemplateSlip> Slips { get; } = new();
    public List<string> Notes { get; } = new();
}

internal sealed class TemplateSections
{
    public RoadSection Principal { get; init; } = new();
    public RoadSection Secundaria { get; init; } = new();
    public RoadSection Anel { get; init; } = new();

    public RoadSection For(TemplateRole role) => role switch
    {
        TemplateRole.Principal => Principal,
        TemplateRole.Secundaria => Secundaria,
        _ => Anel
    };
}

internal sealed class IntersectionTemplate
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool UsesRing { get; init; }
    public List<TemplateParam> Params { get; init; } = new();
    public Func<Func<string, double>, TemplateSections, TemplateBuild> Builder { get; init; } = (_, _) => new TemplateBuild();

    public TemplateBuild Build(TemplateSections sections)
    {
        Dictionary<string, double> values = Params.ToDictionary(p => p.Key, p => Math.Clamp(p.Value, p.Min, p.Max));
        return Builder(k => values.TryGetValue(k, out double v) ? v : 0.0, sections);
    }
}

internal static class ViarioTemplates
{
    public static V2 Place(V2 p, V2 origin, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new V2(origin.X + (p.X * c) - (p.Y * s), origin.Y + (p.X * s) + (p.Y * c));
    }

    private static V2 Dir(double degrees) => V2.FromAngle(degrees * Math.PI / 180.0);

    private static TemplateAxis Line(TemplateRole role, V2 a, V2 b, params AxisEvent[] events) =>
        new() { Role = role, Points = new List<V2> { a, b }, Events = events.ToList() };

    private static AxisEvent Gota(double s, int dir, Func<string, double> p) => new()
    {
        Kind = AxisEventKind.Gota, S = s, Dir = dir, Side = 0,
        Width = p("gw"), Length = p("gl"), Gap = p("gr"), Taper = p("gt"), Smooth = true
    };

    // Parametros comuns.
    private static TemplateParam Ramo => new("L", "Comprimento dos ramos (m)", 60, 15, 1000);
    private static TemplateParam Angulo(double v = 90) => new("ang", "Angulo entre as vias (graus)", v, 20, 160);
    private static IEnumerable<TemplateParam> GotaParams() => new[]
    {
        new TemplateParam("gw", "Gota: largura (m)", 2.5, 0.5, 20),
        new TemplateParam("gl", "Gota: comprimento (m)", 15, 3, 200),
        new TemplateParam("gr", "Gota: recuo da ponta (m)", 1.5, 0, 20),
        new TemplateParam("gt", "Gota: taper do alargamento (m)", 20, 0, 200)
    };
    private static IEnumerable<TemplateParam> LivreParams() => new[]
    {
        new TemplateParam("lr", "Faixa livre: raio do meio-fio externo (m)", 25, 5, 200),
        new TemplateParam("lw", "Faixa livre: largura (m)", 5, 2, 15),
        new TemplateParam("ls", "Faixa livre: afastamento da ilha (m)", 0.6, 0, 5)
    };
    private static IEnumerable<TemplateParam> ConversaoParams() => new[]
    {
        new TemplateParam("cw", "Conversao: largura da faixa (m)", 3.0, 1, 10),
        new TemplateParam("cl", "Conversao: armazenamento (m)", 30, 1, 500),
        new TemplateParam("ct", "Conversao: taper (m)", 20, 0, 200)
    };

    private static SlipLane Livre(Func<string, double> p) =>
        new() { Radius = p("lr"), Width = p("lw"), Setback = p("ls") };

    /// <summary>Ponto de referencia da esquina entre duas direcoes (bissetriz).</summary>
    private static V2 CornerHint(V2 a, V2 b, double distance) => (a.Unit() + b.Unit()).Unit() * distance;

    public static List<IntersectionTemplate> All() => new()
    {
        new IntersectionTemplate
        {
            Name = "Cruzamento simples",
            Description = "Quatro ramos, esquinas concordadas (Tipo I).",
            Params = new() { Ramo, Angulo() },
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                V2 u = Dir(p("ang"));
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(p("L"), 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, u * -p("L"), u * p("L")));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "Intersecao em T",
            Description = "Secundaria chegando na principal (Tipo I).",
            Params = new() { Ramo, Angulo() },
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                V2 u = Dir(-p("ang"));
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(p("L"), 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * p("L")));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "Intersecao em Y",
            Description = "Bifurcacao: principal se divide em dois ramos.",
            Params = new() { Ramo, new TemplateParam("ang", "Abertura entre os ramos (graus)", 60, 20, 160) },
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                double half = p("ang") / 2.0;
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(0, 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), Dir(half) * p("L")));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), Dir(-half) * p("L")));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "T com gota (Tipo II)",
            Description = "Refugio em gota na via secundaria.",
            Params = new List<TemplateParam> { Ramo, Angulo() }.Concat(GotaParams()).ToList(),
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                V2 u = Dir(-p("ang"));
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(p("L"), 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * p("L"), Gota(0, 1, p)));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "Cruzamento com gotas (Tipo II)",
            Description = "Refugio em gota nos dois ramos da secundaria.",
            Params = new List<TemplateParam> { Ramo, Angulo() }.Concat(GotaParams()).ToList(),
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                V2 u = Dir(p("ang"));
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(p("L"), 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * p("L"), Gota(0, 1, p)));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * -p("L"), Gota(0, 1, p)));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "T com faixa livre a direita (Tipo III)",
            Description = "Faixa separada para quem dobra a direita, com ilha triangular.",
            Params = new List<TemplateParam> { Ramo, Angulo(), new("lados", "Esquinas com faixa livre (1 ou 2)", 2, 1, 2) }
                .Concat(LivreParams()).ToList(),
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                V2 u = Dir(-p("ang"));
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(p("L"), 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * p("L")));
                b.Slips.Add(new TemplateSlip { Hint = CornerHint(new V2(1, 0), u, 20), Slip = Livre(p) });
                if (p("lados") >= 1.5)
                    b.Slips.Add(new TemplateSlip { Hint = CornerHint(new V2(-1, 0), u, 20), Slip = Livre(p) });
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "Cruzamento com faixas livres",
            Description = "Faixa livre a direita nas quatro esquinas.",
            Params = new List<TemplateParam> { Ramo, Angulo() }.Concat(LivreParams()).ToList(),
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                V2 u = Dir(p("ang"));
                b.Axes.Add(Line(TemplateRole.Principal, new V2(-p("L"), 0), new V2(p("L"), 0)));
                b.Axes.Add(Line(TemplateRole.Secundaria, u * -p("L"), u * p("L")));
                foreach (V2 a in new[] { new V2(1, 0), new V2(-1, 0) })
                {
                    foreach (V2 c in new[] { u, -u })
                        b.Slips.Add(new TemplateSlip { Hint = CornerHint(a, c, 20), Slip = Livre(p) });
                }

                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "Cruzamento com conversao a esquerda (Tipo IV)",
            Description = "Faixas de conversao a esquerda na principal (no canteiro, se houver; senao alargando a pista) e gotas na secundaria.",
            Params = new List<TemplateParam> { Ramo, Angulo(), new("gotas", "Gotas na secundaria (0 ou 1)", 1, 0, 1) }
                .Concat(ConversaoParams()).Concat(GotaParams()).ToList(),
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                double l = p("L");
                V2 u = Dir(p("ang"));
                bool median = s.Principal.Canteiro > 1e-3;
                AxisEvent Conv(int dir) => new()
                {
                    Kind = median ? AxisEventKind.Baia : AxisEventKind.Alargamento,
                    S = l, Dir = dir, Side = -1, Width = p("cw"), Length = p("cl"), Taper = p("ct"), Smooth = true
                };

                b.Axes.Add(Line(TemplateRole.Principal, new V2(-l, 0), new V2(l, 0), Conv(-1), Conv(1)));
                if (!median)
                    b.Notes.Add("A secao principal nao tem canteiro: as faixas de conversao alargam a pista.");

                bool gotas = p("gotas") >= 0.5 && s.Secundaria.Canteiro <= 1e-3;
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * l, gotas ? new[] { Gota(0, 1, p) } : Array.Empty<AxisEvent>()));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * -l, gotas ? new[] { Gota(0, 1, p) } : Array.Empty<AxisEvent>()));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "T com faixas de conversao",
            Description = "Alargamento na principal: conversao a direita e a esquerda para a secundaria.",
            Params = new List<TemplateParam>
            {
                Ramo, Angulo(),
                new("dir", "Conversao a direita (0 ou 1)", 1, 0, 1),
                new("esq", "Conversao a esquerda (0 ou 1)", 1, 0, 1)
            }.Concat(ConversaoParams()).ToList(),
            Builder = (p, s) =>
            {
                TemplateBuild b = new();
                double l = p("L");
                V2 u = Dir(-p("ang"));
                List<AxisEvent> events = new();
                if (p("dir") >= 0.5)
                {
                    events.Add(new AxisEvent
                    {
                        Kind = AxisEventKind.Alargamento, S = l, Dir = -1, Side = 1,
                        Width = p("cw"), Length = p("cl"), Taper = p("ct"), Smooth = true
                    });
                }

                if (p("esq") >= 0.5)
                {
                    events.Add(new AxisEvent
                    {
                        Kind = s.Principal.Canteiro > 1e-3 ? AxisEventKind.Baia : AxisEventKind.Alargamento,
                        S = l, Dir = 1, Side = -1, Width = p("cw"), Length = p("cl"), Taper = p("ct"), Smooth = true
                    });
                }

                b.Axes.Add(Line(TemplateRole.Principal, new V2(-l, 0), new V2(l, 0), events.ToArray()));
                b.Axes.Add(Line(TemplateRole.Secundaria, new V2(0, 0), u * l));
                return b;
            }
        },
        new IntersectionTemplate
        {
            Name = "Rotatoria de 4 ramos",
            Description = "Anel com quatro entradas e gotas divisoras (rotatoria canalizada).",
            UsesRing = true,
            Params = new List<TemplateParam>
            {
                Ramo,
                new("ri", "Raio da ilha central, na face do meio-fio (m)", 15, 2, 200),
                new("gotas", "Gotas nas entradas (0 ou 1)", 1, 0, 1)
            }.Concat(GotaParams()).ToList(),
            Builder = (p, s) => Roundabout(p, s, new[] { 0.0, 90.0, 180.0, 270.0 })
        },
        new IntersectionTemplate
        {
            Name = "Rotatoria de 3 ramos",
            Description = "Anel com tres entradas (em T) e gotas divisoras.",
            UsesRing = true,
            Params = new List<TemplateParam>
            {
                Ramo,
                new("ri", "Raio da ilha central, na face do meio-fio (m)", 15, 2, 200),
                new("gotas", "Gotas nas entradas (0 ou 1)", 1, 0, 1)
            }.Concat(GotaParams()).ToList(),
            Builder = (p, s) => Roundabout(p, s, new[] { 0.0, 180.0, 270.0 })
        },
        new IntersectionTemplate
        {
            Name = "Rotatoria de 5 ramos",
            Description = "Anel com cinco entradas igualmente espacadas.",
            UsesRing = true,
            Params = new List<TemplateParam>
            {
                Ramo,
                new("ri", "Raio da ilha central, na face do meio-fio (m)", 20, 2, 200),
                new("gotas", "Gotas nas entradas (0 ou 1)", 1, 0, 1)
            }.Concat(GotaParams()).ToList(),
            Builder = (p, s) => Roundabout(p, s, new[] { 0.0, 72.0, 144.0, 216.0, 288.0 })
        }
    };

    private static TemplateBuild Roundabout(Func<string, double> p, TemplateSections s, double[] angles)
    {
        TemplateBuild b = new();
        double ring = p("ri") + s.Anel.EdgeOffset(EdgeLevel.FaceMF);
        b.Axes.Add(new TemplateAxis { Role = TemplateRole.Anel, IsCircle = true, Center = new V2(0, 0), Radius = ring });

        bool gotas = p("gotas") >= 0.5;
        for (int k = 0; k < angles.Length; k++)
        {
            V2 u = Dir(angles[k]);
            TemplateRole role = k % 2 == 0 ? TemplateRole.Principal : TemplateRole.Secundaria;
            bool hasMedian = s.For(role).Canteiro > 1e-3;
            AxisEvent[] events = gotas && !hasMedian ? new[] { Gota(0, 1, p) } : Array.Empty<AxisEvent>();
            b.Axes.Add(Line(role, u * ring, u * (ring + p("L")), events));
        }

        return b;
    }

    /// <summary>
    /// Calcula a rede com o modelo posicionado (origem e angulo), incluindo
    /// as faixas livres (associadas as esquinas pelos pontos de referencia).
    /// </summary>
    public static ViarioResult Compute(
        TemplateBuild build, TemplateSections sections, IReadOnlyList<AxisInput> existing,
        EngineSettings baseSettings, V2 origin, double angle, out List<AxisInput> placed)
    {
        placed = new List<AxisInput>();
        for (int i = 0; i < build.Axes.Count; i++)
        {
            TemplateAxis axis = build.Axes[i].Transform(origin, angle);
            placed.Add(new AxisInput("__T" + i.ToString(CultureInfo.InvariantCulture), axis.ToPath(), sections.For(axis.Role), null, axis.Events));
        }

        List<AxisInput> all = new(existing);
        all.AddRange(placed);

        ViarioResult result = ViarioEngine.Compute(all, baseSettings);
        if (build.Slips.Count == 0)
            return result;

        EngineSettings settings = baseSettings.Clone();
        foreach (TemplateSlip slip in build.Slips)
        {
            string? key = NearestCorner(result, Place(slip.Hint, origin, angle), 40.0);
            if (key is not null)
                settings.SlipLanes[key] = slip.Slip;
        }

        return ViarioEngine.Compute(all, settings);
    }

    public static string? NearestCorner(ViarioResult result, V2 hint, double maxDistance) =>
        result.Corners
            .Where(c => V2.Dist(c.Corner, hint) <= maxDistance)
            .OrderBy(c => V2.Dist(c.Corner, hint))
            .Select(c => c.Key)
            .FirstOrDefault();
}
