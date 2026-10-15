using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

// =============================================================================
// Elementos de intersecao ligados aos eixos (sem dependencia do AutoCAD).
//
// Os elementos ficam gravados no proprio eixo (XData) e sao ancorados no no
// mais proximo da estaca gravada: se o eixo ou o cruzamento se move, o
// elemento acompanha.
// =============================================================================

internal enum AxisEventKind
{
    /// <summary>Alargamento de pista (faixa de conversao a direita/esquerda) com taper.</summary>
    Alargamento,

    /// <summary>Ilha em gota na aproximacao do no (pista alarga dos dois lados).</summary>
    Gota,

    /// <summary>Faixa de conversao a esquerda tirada do canteiro central.</summary>
    Baia,

    /// <summary>Abertura do canteiro central (retorno), no meio da quadra.</summary>
    Abertura
}

internal sealed class AxisEvent
{
    public AxisEventKind Kind { get; set; }

    /// <summary>Estaca de ancoragem (no mais proximo; na Abertura, o centro).</summary>
    public double S { get; set; }

    /// <summary>Lado do no em que o elemento fica: -1 = estacas menores, +1 = maiores.</summary>
    public int Dir { get; set; } = -1;

    /// <summary>Lado para quem chega no no: +1 direita, -1 esquerda, 0 ambos.</summary>
    public int Side { get; set; } = 1;

    public double Width { get; set; } = 3.0;

    /// <summary>Trecho com largura total (armazenamento / comprimento da ilha).</summary>
    public double Length { get; set; } = 30.0;

    /// <summary>Taper de saida (longe do no): a largura volta a zero ao fim do armazenamento.</summary>
    public double Taper { get; set; } = 20.0;

    /// <summary>
    /// Taper de entrada (junto ao no): a largura cresce de zero, a partir da pista
    /// transversal, ate a largura total. 0 = comeca ja com a largura total (padrao).
    /// Com Taper = 0 e TaperIn > 0 o alargamento comeca na intersecao e segue ate o fim.
    /// </summary>
    public double TaperIn { get; set; }

    /// <summary>Recuo da ponta da gota em relacao a pista transversal.</summary>
    public double Gap { get; set; } = 1.5;

    /// <summary>Taper em curva suave (reversa) em vez de reto.</summary>
    public bool Smooth { get; set; } = true;

    public AxisEvent Clone() => (AxisEvent)MemberwiseClone();

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public string Serialize() =>
        string.Join(";", new[]
        {
            "EV1", Kind.ToString(), N(S), Dir.ToString(CultureInfo.InvariantCulture),
            Side.ToString(CultureInfo.InvariantCulture), N(Width), N(Length), N(Taper), N(Gap), Smooth ? "1" : "0", N(TaperIn)
        });

    public static AxisEvent? Parse(string text)
    {
        string[] p = text.Split(';');
        if (p.Length < 10 || p[0] != "EV1" || !Enum.TryParse(p[1], out AxisEventKind kind))
            return null;

        static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        return new AxisEvent
        {
            Kind = kind,
            S = D(p[2]),
            Dir = I(p[3]) >= 0 ? 1 : -1,
            Side = Math.Sign(I(p[4])),
            Width = D(p[5]),
            Length = D(p[6]),
            Taper = D(p[7]),
            Gap = D(p[8]),
            Smooth = p[9] == "1",
            TaperIn = p.Length > 10 ? D(p[10]) : 0.0
        };
    }

    /// <summary>Onde fica o taper: longe do no (saida), junto ao no (entrada) ou nos dois.</summary>
    public string TaperMode => TaperIn > 1e-9 ? (Taper > 1e-9 ? "ambos" : "junto") : "longe";

    private string Tapers() =>
        (TaperIn > 1e-9 ? $" + taper de entrada {N(TaperIn)} m" : "") + (Taper > 1e-9 ? $" + taper {N(Taper)} m" : "");

    public string Describe() => Kind switch
    {
        AxisEventKind.Alargamento =>
            $"alargamento {(Side > 0 ? "a direita" : Side < 0 ? "a esquerda" : "dos dois lados")} {N(Width)} m, {N(Length)} m{Tapers()}",
        AxisEventKind.Gota => $"gota {N(Width)} x {N(Length)} m, recuo {N(Gap)} m, taper {N(Taper)} m",
        AxisEventKind.Baia => $"baia no canteiro {N(Width)} m, {N(Length)} m{Tapers()}",
        _ => $"abertura do canteiro {N(Length)} m"
    };
}

/// <summary>Esquina com faixa livre de conversao a direita e ilha triangular.</summary>
internal sealed class SlipLane
{
    /// <summary>Raio do meio-fio externo da faixa livre (face).</summary>
    public double Radius { get; set; } = 25.0;

    /// <summary>Largura da faixa livre.</summary>
    public double Width { get; set; } = 5.0;

    /// <summary>Afastamento da ilha em relacao aos bordos das vias.</summary>
    public double Setback { get; set; } = 0.6;

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public string Serialize() => string.Join(";", N(Radius), N(Width), N(Setback));

    public static SlipLane? Parse(string text)
    {
        string[] p = text.Split(';');
        if (p.Length < 3)
            return null;

        static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        return new SlipLane { Radius = D(p[0]), Width = D(p[1]), Setback = D(p[2]) };
    }
}
