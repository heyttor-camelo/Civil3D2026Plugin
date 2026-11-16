using System;
using System.Globalization;
using System.IO;

using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.QTO
{
    /// <summary>
    /// Opcoes do QTO guardadas por usuario em %APPDATA%\Civil3D2026Plugin\qto.txt
    /// (linhas chave=valor). Valem para o painel (C3DQTO) e para os comandos.
    /// </summary>
    internal sealed class QtoConfig
    {
        public const double IntervaloPadrao = 20.0;
        public const double ToleranciaPadrao = 5.0;

        private static QtoConfig? _atual;

        /// <summary>Intervalo entre estacas, em metros.</summary>
        public double Intervalo { get; set; } = IntervaloPadrao;

        /// <summary>Diferenca maxima (em %) entre a soma dos trechos e o total do Civil para sair "OK".</summary>
        public double Tolerancia { get; set; } = ToleranciaPadrao;

        /// <summary>Abre a pasta de saida no fim (usado pelo painel; a linha de comando pergunta).</summary>
        public bool AbrirPasta { get; set; } = true;

        public static QtoConfig Atual => _atual ??= Ler();

        private static string Arquivo =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Civil3D2026Plugin",
                "qto.txt");

        private static QtoConfig Ler()
        {
            QtoConfig cfg = new QtoConfig();

            try
            {
                if (!File.Exists(Arquivo))
                    return cfg;

                foreach (string linha in File.ReadAllLines(Arquivo))
                {
                    int p = linha.IndexOf('=');
                    if (p <= 0)
                        continue;

                    string chave = linha.Substring(0, p).Trim().ToLowerInvariant();
                    string valor = linha.Substring(p + 1).Trim();

                    switch (chave)
                    {
                        case "intervalo":
                            if (LerNumero(valor, out double intervalo) && IntervaloValido(intervalo))
                                cfg.Intervalo = intervalo;
                            break;

                        case "tolerancia":
                            if (LerNumero(valor, out double tolerancia) && ToleranciaValida(tolerancia))
                                cfg.Tolerancia = tolerancia;
                            break;

                        case "abrirpasta":
                            cfg.AbrirPasta = valor != "0";
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Aviso("QTO", "Nao foi possivel ler as opcoes do QTO: " + ex.Message);
            }

            return cfg;
        }

        public void Salvar()
        {
            try
            {
                string? pasta = Path.GetDirectoryName(Arquivo);
                if (!string.IsNullOrWhiteSpace(pasta))
                    Directory.CreateDirectory(pasta);

                File.WriteAllLines(
                    Arquivo,
                    new[]
                    {
                        "intervalo=" + Intervalo.ToString("0.######", CultureInfo.InvariantCulture),
                        "tolerancia=" + Tolerancia.ToString("0.######", CultureInfo.InvariantCulture),
                        "abrirpasta=" + (AbrirPasta ? "1" : "0")
                    });
            }
            catch (Exception ex)
            {
                PluginLog.Aviso("QTO", "Nao foi possivel gravar as opcoes do QTO: " + ex.Message);
            }
        }

        public static bool IntervaloValido(double valor) =>
            !double.IsNaN(valor) && valor >= 0.5 && valor <= 10000.0;

        public static bool ToleranciaValida(double valor) =>
            !double.IsNaN(valor) && valor >= 0.0 && valor <= 100.0;

        /// <summary>Aceita virgula ou ponto como separador decimal.</summary>
        public static bool LerNumero(string texto, out double valor) =>
            double.TryParse(
                (texto ?? string.Empty).Trim().Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out valor);
    }
}
