using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3D2026Plugin.Modules.Viario;

/// <summary>Tabela de consulta (resumo dos manuais do DNIT), aberta a partir da fonte de um aviso.</summary>
internal sealed class RefTable
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>Manual, item e pagina impressa (no PDF: pagina impressa + 2).</summary>
    public string Source { get; init; } = "";

    /// <summary>Trechos de fonte dos avisos que abrem esta tabela.</summary>
    public string[] Keys { get; init; } = Array.Empty<string>();

    public string[] Columns { get; init; } = Array.Empty<string>();
    public string[][] Rows { get; init; } = Array.Empty<string[]>();
    public string Note { get; init; } = "";
}

/// <summary>
/// Resumo das tabelas do IPR-718 (Manual de Projeto de Intersecoes, DNIT 2005) e
/// IPR-740 (Manual de Projetos Geometricos de Travessias Urbanas, DNIT 2010).
/// Para consulta rapida; o manual e a referencia.
/// </summary>
internal static class ViarioReference
{
    private static string[] R(params string[] cells) => cells;

    public static readonly RefTable[] All =
    {
        new()
        {
            Id = "faixa", Title = "Largura de faixa de rolamento (urbano)", Source = "IPR-740 Tab.70 e Tabs.77-83, p.308 e p.373-379",
            Keys = new[] { "IPR-740 Tab.70", "IPR-740 Tab.77-83", "IPR-740 Tab.70/77-83" },
            Columns = new[] { "Classe / velocidade", "Desejavel (m)", "Minimo (m)", "Velocidade de projeto (km/h)" },
            Rows = new[]
            {
                R("Via expressa", "3,60", "3,50", "110 / 80 (primaria); 90 / 60 (secundaria)"),
                R("Arterial 60-80 km/h", "3,60", "3,50", "80-60 (70) / 50"),
                R("Arterial 50-60 km/h", "3,50", "3,30", "50"),
                R("Coletora", "3,50", "3,00", "60 / 50"),
                R("Local", "3,30", "3,00", "40 / 30"),
                R("Faixa de 3,00 m", "-", "aceitavel", "areas restritas, pouco trafego pesado (p.306)")
            },
            Note = "Sem contar a sarjeta. Perda de velocidade (HCM, Tab.69): 3,50 m -1,0 km/h; 3,30 m -3,1 km/h; 3,00 m -10,6 km/h. " +
                   "Estacionamento: 2,50 m (min. 2,20); 3,00 m com veiculos comerciais."
        },
        new()
        {
            Id = "bordos", Title = "Sarjeta, meio-fio e passeio", Source = "IPR-740 p.99-104, p.306, p.316",
            Keys = new[] { "IPR-740 p.306", "IPR-740 p.100", "IPR-740 p.102" },
            Columns = new[] { "Elemento", "Valor", "Condicao" },
            Rows = new[]
            {
                R("Afastamento da faixa ao meio-fio (sarjeta)", ">= 0,30 m", "p.306"),
                R("Meio-fio lado direito (arteriais)", "0,15 - 0,20 m de altura", "intransponivel, p.316"),
                R("Meio-fio continuo de canteiro/ilha", ">= 0,30 m (pref. 0,60) da faixa", "p.316"),
                R("Passeio - faixa livre", "min. desejavel 1,20 m", "abaixo de 1,50 m prever bolsoes de 1,50 m (p.100)"),
                R("Passeio residencial com meio-fio", ">= 1,80 m", "p.102"),
                R("Passeio comercial ou arterial", ">= 2,40 m", "p.102"),
                R("Passeio area comercial intensa", "3,00 - 4,50 m", "p.100-101"),
                R("Declividade transversal do passeio", "max. 2%", "Tab.13, p.103")
            }
        },
        new()
        {
            Id = "raio", Title = "Raio de bordo em esquinas - giro minimo (<= 15 km/h)", Source = "IPR-718 Tab.39, p.236",
            Keys = new[] { "IPR-718 Tab.39", "IPR-740 4.4.5" },
            Columns = new[] { "Angulo de giro", "Carro (VP)", "Onibus/caminhao (CO)", "Semirreboque (SR)" },
            Rows = new[]
            {
                R("30", "simples 18", "simples 30", "simples 60"),
                R("45", "15", "23", "53; 60-30-60 / 1,0"),
                R("60", "12", "18", "45; 60-23-60 / 1,7"),
                R("75", "11; 30-8-30 / 0,6", "17; 36-14-36 / 0,6", "45-15-45 / 2,0"),
                R("90", "9; 30-6-30 / 0,8", "15; 36-12-36 / 0,6", "55-18-55 / 2,0"),
                R("105", "30-6-30 / 0,8", "30-11-30 / 1,0", "55-14-55 / 2,5"),
                R("120", "30-6-30 / 0,6", "30-9-30 / 1,0", "55-12-55 / 2,6"),
                R("135", "30-6-30 / 0,5", "30-9-30 / 1,2", "48-11-48 / 2,7"),
                R("150", "23-6-23 / 0,6", "30-9-30 / 1,2", "48-11-48 / 2,1")
            },
            Note = "Curva de 3 centros: R1-R2-R1 / afastamento (m). Use o maior veiculo com participacao apreciavel no giro. " +
                   "Em area urbana os raios devem ser os minimos necessarios: raios grandes alongam a travessia de pedestres (IPR-740 4.4.5)."
        },
        new()
        {
            Id = "conversao", Title = "Faixa de giro a esquerda", Source = "IPR-718 8.5.5.4, p.275-281",
            Keys = new[] { "IPR-718 p.276", "IPR-718 p.277", "IPR-718 p.281", "IPR-718 p.276/281" },
            Columns = new[] { "Item", "Valor" },
            Rows = new[]
            {
                R("Largura", ">= 3,00 m; desejavel = faixa direta; excepcional 2,70 m"),
                R("Faixa deslocada com ilhas", ">= 3,60 m (pref. 4,20)"),
                R("Giro duplo semaforizado", "6,60 - 7,20 m"),
                R("Sobra do separador", ">= 1,20 m (pref. 1,80); minimo absoluto 0,60 m"),
                R("Taper urbano", "30 - 54 m (8:1 a 15:1 para faixa de 3,60 m)"),
                R("Razao do taper", "8:1 ate 50 km/h; 15:1 ate 80 km/h"),
                R("Taper reto curto", "nao usar com meio-fio; curva reversa >= 30 m"),
                R("Canteiro 3,00 - 3,60 m", "nariz de so 0,60 m ou pintura"),
                R("Canteiro > 5,40 m", "deslocar a faixa"),
                R("Nariz de aproximacao", "0,60 - 1,80 m das faixas diretas")
            }
        },
        new()
        {
            Id = "armazenamento", Title = "Armazenamento e desaceleracao", Source = "IPR-718 Tabs.52 e 53, p.279-281",
            Keys = new[] { "IPR-718 Tab.53" },
            Columns = new[] { "Situacao", "Valor" },
            Rows = new[]
            {
                R("Nao semaforizado <= 60 veic/h", "15 m"),
                R("100 veic/h", "30 m"),
                R("200 veic/h", "50 m"),
                R("300 veic/h", "75 m"),
                R("Minimo", "2 carros, ou 1 carro + 1 caminhao se pesados > 10%"),
                R("Semaforizado", "1,5 a 2 x media de veiculos por ciclo"),
                R("Desaceleracao sem taper (Tab.52)", "50 -> 70; 60 -> 100; 70 -> 130; 80 -> 165 m")
            }
        },
        new()
        {
            Id = "taper", Title = "Faixas de mudanca de velocidade - taper", Source = "IPR-718 Tabs.47 e 48, p.259-262",
            Keys = new[] { "IPR-718 Tab.47", "IPR-718 Tab.48" },
            Columns = new[] { "Velocidade (km/h)", "Taper (m)", "Desaceleracao total (m)", "Aceleracao total (m)" },
            Rows = new[]
            {
                R("40", "40", "60", "60"),
                R("50", "45", "75", "90"),
                R("60", "55", "95", "130"),
                R("70", "60", "110", "180"),
                R("80", "70", "130", "230")
            },
            Note = "Totais para parada/partida (curva a 0 km/h), greide <= 2%. Largura do trecho constante 3,50-3,60 m."
        },
        new()
        {
            Id = "ilhas", Title = "Ilhas canalizadoras e divisorias", Source = "IPR-718 8.7, p.354-361",
            Keys = new[] { "IPR-718 p.354", "IPR-718 p.354/361", "IPR-718 p.359", "IPR-718 p.360" },
            Columns = new[] { "Item", "Valor" },
            Rows = new[]
            {
                R("Area minima", "5,00 m2 urbana; 7,00 m2 rural (< 5 m2: so pintura)"),
                R("Lado minimo (triangular, apos arredondar)", "3,50 m (pref. 4,50)"),
                R("Afastamento do bordo", "0,60 - 1,00 m"),
                R("Nariz dianteiro (via principal)", "1,20 - 2,10 m"),
                R("Raio do nariz dianteiro", "0,60 - 1,00 m"),
                R("Raio da extremidade traseira", ">= 0,30 m"),
                R("Ilha divisoria (gota) - largura", ">= 1,00 m (restrito 0,50)"),
                R("Ilha divisoria - comprimento", "6 - 8 m (rural >= 30 m)"),
                R("Refugio de pedestres", ">= 2,50 m (nunca < 1,20); com ciclistas 1,80 m")
            }
        },
        new()
        {
            Id = "canteiro", Title = "Largura do canteiro central", Source = "IPR-718 Tab.64, p.377; IPR-740 Tab.73, p.331",
            Keys = new[] { "IPR-718 Tab.64", "IPR-740 p.328" },
            Columns = new[] { "Funcao", "Minimo (m)", "Desejado (m)" },
            Rows = new[]
            {
                R("Separacao", "1,20 (urbano 0,60)", "3,00"),
                R("Refugio de pedestres", "1,80 (urbano 1,20)", "4,20"),
                R("Giro a esquerda", "3,60 (urbano 3,00)", "6,00 (arterial 5,40)"),
                R("Protecao do veiculo que cruza", "6,00", "14,00"),
                R("Retorno em U", "9,00", "19,00")
            },
            Note = "Intersecao urbana nao semaforizada: acima de 15 m as colisoes aumentam; acima de 18 m e indesejavel (IPR-740 p.328)."
        },
        new()
        {
            Id = "abertura", Title = "Abertura de canteiro e retornos", Source = "IPR-718 Tabs.63 e 65, p.364-383",
            Keys = new[] { "IPR-718 p.364", "IPR-718 p.365", "IPR-718 Tab.65" },
            Columns = new[] { "Item", "VP", "CO", "O / SR" },
            Rows = new[]
            {
                R("Raio de controle (Tab.62)", "12 m", "15 m", "23 m"),
                R("Abertura, canteiro 3,0 m (circular/ogiva)", "21,0 / 14,1", "27,0 / 18,6", "43,0 / 31,5"),
                R("Abertura, canteiro 6,0 m", "18,0 / 12,0", "24,0 / 13,2", "39,0 / 25,5"),
                R("Retorno tipo A - canteiro W", "16 m", "26 m", "28 / 29 m"),
                R("Retorno tipo B", "10 m", "19 m", "21 / 22 m"),
                R("Retorno tipo C", "7 m", "16 m", "18 / 19 m")
            },
            Note = "Extensao da abertura >= largura da transversal, nunca < 12 m; evitar > 25 m. Ogiva preferivel com canteiro > 3,00 m."
        },
        new()
        {
            Id = "livre", Title = "Giro livre a direita - largura da pista de conversao", Source = "IPR-718 Tab.45, p.251; Tab.40, p.240",
            Keys = new[] { "IPR-718 Tab.45", "IPR-718 Tab.40" },
            Columns = new[] { "Raio interno", "Caso I (A/B/C)", "Caso II (A/B/C)", "Caso III (A/B/C)" },
            Rows = new[]
            {
                R("15 m", "5,4 / 5,5 / 7,0", "6,0 / 7,8 / 9,2", "9,4 / 11,0 / 13,6"),
                R("25 m", "4,8 / 5,0 / 5,8", "5,6 / 6,9 / 7,9", "8,6 / 9,7 / 11,1"),
                R("30 m", "4,5 / 4,9 / 5,5", "5,5 / 6,7 / 7,6", "8,4 / 9,4 / 10,6"),
                R("50 m", "4,2 / 4,6 / 5,0", "5,3 / 6,3 / 7,0", "7,9 / 8,8 / 9,5"),
                R("75 m", "3,9 / 4,5 / 4,8", "5,2 / 6,1 / 6,7", "7,7 / 8,5 / 8,9"),
                R("150 m", "3,6 / 4,5 / 4,5", "5,1 / 5,8 / 6,4", "7,5 / 8,2 / 8,4")
            },
            Note = "Caso I = 1 faixa; II = com passagem por veiculo parado; III = 2 faixas. A = VP; B = CO; C = SR. " +
                   "Meio-fio intransponivel nos dois lados: +0,60 m (caso I). Tab.40, 90 graus: 45-15-45 / 1,0 / 4,2 m / ilha 5,0 m2 (VP)."
        },
        new()
        {
            Id = "angulo", Title = "Angulo, espacamento e greide em intersecoes", Source = "IPR-718 8.4.1, p.104, p.203-207",
            Keys = new[] { "IPR-718 8.4.1", "IPR-718 p.104" },
            Columns = new[] { "Item", "Valor" },
            Rows = new[]
            {
                R("Angulo de cruzamento", "ideal 75 - 90 graus; 60 aceitavel; < 60 realinhar"),
                R("Intersecoes deslocadas (esquerda primeiro)", ">= 50 m entre ramos"),
                R("Deslocadas com armazenagem", ">= 100 m"),
                R("Greide na chegada", "evitar > 3%; max. 6%"),
                R("Triangulo de visibilidade sem controle (a = b)", "30 km/h 25 m; 40 35 m; 50 45 m; 60 55 m")
            }
        },
        new()
        {
            Id = "rotula", Title = "Rotatorias (rotulas)", Source = "IPR-718 p.180-190",
            Keys = new[] { "IPR-718 p.183-189" },
            Columns = new[] { "Item", "Valor" },
            Rows = new[]
            {
                R("Mini-rotula", "circulo central ~15 m de diametro (CET-SP: raio 1 a 8 m)"),
                R("Rotula compacta", "diametro 30 - 35 m"),
                R("Diametro externo minimo (onibus, caminhoes)", "~30 - 40 m"),
                R("Diametro > 75 m", "seguranca decresce"),
                R("Faixas de entrada e saida", "3,50 - 4,00 m"),
                R("Pista circulatoria", ">= maior entrada; inclinacao para fora 1,5 - 2,5%"),
                R("Travessia de pedestres", ">= 20 m antes da linha de 'De a preferencia'")
            }
        },
        new()
        {
            Id = "sinal", Title = "Sinalizacao horizontal (MBST Vol. IV)", Source = "Manual Brasileiro de Sinalizacao de Transito, Vol. IV (CONTRAN)",
            Keys = new[] { "MBST" },
            Columns = new[] { "Marca", "Valor" },
            Rows = new[]
            {
                R("LFO-2 / LMS-2, v < 60 km/h", "largura 0,10; traco 2 / espaco 4 m (1:2)"),
                R("60 <= v < 80 km/h", "0,10; 3 / 6 m"),
                R("v >= 80 km/h", "0,15; 4 / 12 m"),
                R("LBO (bordo)", "0,10 (0,15 se >= 80 km/h), a 0,10 - 0,20 m da borda"),
                R("LRE (retencao)", "0,30 - 0,60 m; >= 1,60 m antes da faixa de pedestres"),
                R("FTP-1 (zebrada)", "listras 0,30 - 0,40; espaco 0,30 - 0,80; extensao >= 3,00 m (rec. 4,00)")
            }
        }
    };

    public static RefTable? Find(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        return All
            .SelectMany(t => t.Keys.Select(k => (Table: t, Key: k)))
            .Where(x => source.Contains(x.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Key.Length)
            .Select(x => x.Table)
            .FirstOrDefault();
    }
}
