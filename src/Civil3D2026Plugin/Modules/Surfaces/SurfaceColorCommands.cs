using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.Colors;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;

using CivSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace Civil3D2026Plugin.Modules.Surfaces
{
    public class SurfaceColorAnalysisCommands
    {
        private static double _intervaloCorte = 1.0;
        private static double _intervaloAterro = 1.0;
        private static int _intensidadeLuminance = 5;

        private static int _corteHue = 345;
        private static int _corteSaturation = 100;
        private static int _corteLuminance = 65;

        private static int _aterroHue = 210;
        private static int _aterroSaturation = 100;
        private static int _aterroLuminance = 50;

        [CommandMethod(C3DCommands.Superficies.ColorAuto)]
        public void SurfaceColorAuto()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            try
            {
                if (!PerguntarConfiguracoes(ed))
                    return;

                var peo = new PromptEntityOptions("\nSelecione a Surface (TIN ou Volume): ");
                peo.SetRejectMessage("\nSelecione uma surface do Civil 3D.");
                peo.AddAllowedClass(typeof(TinSurface), true);
                peo.AddAllowedClass(typeof(TinVolumeSurface), true);

                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                    return;

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var obj = tr.GetObject(per.ObjectId, OpenMode.ForWrite);

                    CivSurface? surface = obj as CivSurface;
                    if (surface == null)
                    {
                        ed.WriteMessage("\nObjeto selecionado não é uma Surface válida.");
                        return;
                    }

                    var props = surface.GetGeneralProperties();
                    double min = props.MinimumElevation;
                    double max = props.MaximumElevation;

                    var data = BuildElevationRanges(
                        min,
                        max,
                        _intervaloCorte,
                        _intervaloAterro,
                        _intensidadeLuminance,
                        _corteHue,
                        _corteSaturation,
                        _corteLuminance,
                        _aterroHue,
                        _aterroSaturation,
                        _aterroLuminance);

                    surface.Analysis.SetElevationData(data.ToArray());

                    tr.Commit();

                    ed.WriteMessage(
                        $"\nColor scheme aplicado com sucesso. Faixas: {data.Count}. Min={min:F3}, Max={max:F3}");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nErro: " + ex.Message);
            }
        }

        private static bool PerguntarConfiguracoes(Editor ed)
        {
            ed.WriteMessage(
                $"\nAtual: Corte={_intervaloCorte:F3}m HSL({_corteHue},{_corteSaturation},{_corteLuminance}) | " +
                $"Aterro={_intervaloAterro:F3}m HSL({_aterroHue},{_aterroSaturation},{_aterroLuminance}) | " +
                $"Luminance-por-faixa={_intensidadeLuminance}");

            var pko = new PromptKeywordOptions("\nConfigurações [Manter/Editar] <Manter>: ", "Manter Editar");
            pko.AllowNone = true;

            var pkr = ed.GetKeywords(pko);
            if (pkr.Status == PromptStatus.Cancel)
                return false;

            bool editar = pkr.Status == PromptStatus.OK &&
                          string.Equals(pkr.StringResult, "Editar", StringComparison.OrdinalIgnoreCase);

            if (!editar)
                return true;

            var prCorte = LerDouble(ed, "\nIntervalo de corte, em metros", _intervaloCorte);
            if (prCorte == null)
                return false;
            _intervaloCorte = prCorte.Value;

            var prAterro = LerDouble(ed, "\nIntervalo de aterro, em metros", _intervaloAterro);
            if (prAterro == null)
                return false;
            _intervaloAterro = prAterro.Value;

            var prLum = LerInt(ed, "\nRedução da luminance por faixa", _intensidadeLuminance, true);
            if (prLum == null)
                return false;
            _intensidadeLuminance = prLum.Value;

            if (!LerHsl(ed, "\nCorte", ref _corteHue, ref _corteSaturation, ref _corteLuminance))
                return false;

            if (!LerHsl(ed, "\nAterro", ref _aterroHue, ref _aterroSaturation, ref _aterroLuminance))
                return false;

            return true;
        }

        private static double? LerDouble(Editor ed, string mensagem, double padrao)
        {
            var pdo = new PromptDoubleOptions($"{mensagem} <{padrao}>: ")
            {
                DefaultValue = padrao,
                UseDefaultValue = true,
                AllowNegative = false,
                AllowZero = false,
                AllowNone = false
            };

            var pr = ed.GetDouble(pdo);
            if (pr.Status != PromptStatus.OK)
                return null;

            return pr.Value;
        }

        private static int? LerInt(Editor ed, string mensagem, int padrao, bool permiteZero)
        {
            var pio = new PromptIntegerOptions($"{mensagem} <{padrao}>: ")
            {
                DefaultValue = padrao,
                UseDefaultValue = true,
                AllowNegative = false,
                AllowZero = permiteZero,
                AllowNone = false
            };

            var pr = ed.GetInteger(pio);
            if (pr.Status != PromptStatus.OK)
                return null;

            return pr.Value;
        }

        private static bool LerHsl(Editor ed, string titulo, ref int h, ref int s, ref int l)
        {
            ed.WriteMessage($"\n{titulo} atual: HSL({h},{s},{l})");

            var nh = LerInt(ed, $"{titulo} - Hue", h, true);
            if (nh == null)
                return false;

            var ns = LerInt(ed, $"{titulo} - Saturation", s, true);
            if (ns == null)
                return false;

            var nl = LerInt(ed, $"{titulo} - Luminance", l, true);
            if (nl == null)
                return false;

            h = Clamp(nh.Value, 0, 360);
            s = Clamp(ns.Value, 0, 100);
            l = Clamp(nl.Value, 0, 100);

            return true;
        }

        private static List<SurfaceAnalysisElevationData> BuildElevationRanges(
            double min,
            double max,
            double intervaloCorte,
            double intervaloAterro,
            int intensidadeLuminance,
            int corteHue,
            int corteSaturation,
            int corteLuminance,
            int aterroHue,
            int aterroSaturation,
            int aterroLuminance)
        {
            var list = new List<SurfaceAnalysisElevationData>();

            if (min < 0.0)
            {
                int stepsNeg = (int)Math.Ceiling(Math.Abs(min) / intervaloCorte);

                for (int i = stepsNeg; i >= 1; i--)
                {
                    double bandMin = -i * intervaloCorte;
                    double bandMax = -(i - 1) * intervaloCorte;

                    if (bandMin < min)
                        bandMin = min;
                    if (bandMax > 0.0)
                        bandMax = 0.0;

                    int step = i - 1;
                    int lum = Clamp(corteLuminance - intensidadeLuminance * step, 0, 100);
                    var color = CreateColorFromHsl(corteHue, corteSaturation, lum);

                    list.Add(new SurfaceAnalysisElevationData(bandMin, bandMax, color));
                }
            }

            if (max > 0.0)
            {
                int stepsPos = (int)Math.Ceiling(max / intervaloAterro);

                for (int i = 0; i < stepsPos; i++)
                {
                    double bandMin = i * intervaloAterro;
                    double bandMax = (i + 1) * intervaloAterro;

                    if (bandMin < 0.0)
                        bandMin = 0.0;
                    if (bandMax > max)
                        bandMax = max;

                    int step = i;
                    int lum = Clamp(aterroLuminance - intensidadeLuminance * step, 0, 100);
                    var color = CreateColorFromHsl(aterroHue, aterroSaturation, lum);

                    list.Add(new SurfaceAnalysisElevationData(bandMin, bandMax, color));
                }
            }

            if (list.Count == 0)
            {
                double fim = intervaloAterro > 0 ? intervaloAterro : 1.0;
                var color = CreateColorFromHsl(aterroHue, aterroSaturation, aterroLuminance);
                list.Add(new SurfaceAnalysisElevationData(0.0, fim, color));
            }

            return list;
        }

        private static AcadColor CreateColorFromHsl(int hue, int saturation, int luminance)
        {
            double h = hue / 360.0;
            double s = saturation / 100.0;
            double l = luminance / 100.0;

            double r, g, b;

            if (s == 0.0)
            {
                r = l;
                g = l;
                b = l;
            }
            else
            {
                double q = l < 0.5
                    ? l * (1.0 + s)
                    : l + s - l * s;

                double p = 2.0 * l - q;

                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }

            return AcadColor.FromRgb(
                (byte)Clamp((int)Math.Round(r * 255.0), 0, 255),
                (byte)Clamp((int)Math.Round(g * 255.0), 0, 255),
                (byte)Clamp((int)Math.Round(b * 255.0), 0, 255));
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;

            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;

            return p;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // =====================================================================
        // SURFREADSCHEME
        // =====================================================================
        // OBJETIVO
        // -------
        // Ler o esquema de análise de elevação existente de uma Surface e,
        // opcionalmente, converter SOMENTE cores ACI (1..255) para o RGB
        // equivalente da própria paleta do AutoCAD.
        //
        // DIFERENÇA EM RELAÇÃO AO SURFCOLORAUTO
        // --------------------------------------
        // SURFREADSCHEME preserva:
        //   - MinimumElevation;
        //   - MaximumElevation;
        //   - ordem das faixas;
        //   - quantidade de faixas.
        // Ele não recria o esquema. Apenas corrige ACI -> True Color RGB.
        //
        // SURFCOLORAUTO continua disponível para quando a intenção for GERAR
        // novamente as faixas e cores com base em intervalos/HSL definidos.
        // =====================================================================

        [CommandMethod(C3DCommands.Superficies.ReadScheme)]
        public void SurfReadScheme()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            var peo = new PromptEntityOptions(
                "\nSelecione a Surface para leitura do esquema: ");

            peo.SetRejectMessage(
                "\nSelecione uma Surface do Civil 3D.");

            peo.AddAllowedClass(typeof(TinSurface), true);
            peo.AddAllowedClass(typeof(TinVolumeSurface), true);

            PromptEntityResult per = ed.GetEntity(peo);

            if (per.Status != PromptStatus.OK)
                return;

            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var obj = tr.GetObject(
                    per.ObjectId,
                    OpenMode.ForRead);

                var surface =
                    obj as Autodesk.Civil.DatabaseServices.Surface;

                if (surface == null)
                {
                    ed.WriteMessage(
                        "\nObjeto selecionado não é uma Surface válida.");

                    return;
                }

                SurfaceAnalysisElevationData[] data;

                try
                {
                    data = surface.Analysis.GetElevationData();
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage(
                        "\nErro ao ler análise de elevação: " +
                        ex.Message);

                    return;
                }

                if (data == null || data.Length == 0)
                {
                    ed.WriteMessage(
                        "\nA Surface não possui dados de análise " +
                        "de elevação configurados.");

                    return;
                }

                ed.WriteMessage(
                    $"\n\nFaixas existentes: {data.Length}");

                ed.WriteMessage(
                    "\n--------------------------------------------------");

                int quantidadeAci = 0;
                int quantidadeRgb = 0;
                int quantidadeOutros = 0;

                for (int i = 0; i < data.Length; i++)
                {
                    SurfaceAnalysisElevationData faixa = data[i];
                    AcadColor cor = faixa.Scheme;

                    string descricao;

                    if (TryGetAciRgb(
                        cor,
                        out short aci,
                        out byte r,
                        out byte g,
                        out byte b))
                    {
                        quantidadeAci++;

                        descricao =
                            $"ACI {aci} -> RGB({r}, {g}, {b})";
                    }
                    else if (cor != null &&
                             cor.ColorMethod == ColorMethod.ByColor)
                    {
                        quantidadeRgb++;

                        descricao =
                            $"RGB({cor.Red}, {cor.Green}, {cor.Blue}) " +
                            "[já é True Color]";
                    }
                    else
                    {
                        quantidadeOutros++;

                        descricao =
                            cor == null
                                ? "(sem cor)"
                                : $"Método: {cor.ColorMethod}";
                    }

                    ed.WriteMessage(
                        $"\n{i + 1}: " +
                        $"{faixa.MinimumElevation:F3} - " +
                        $"{faixa.MaximumElevation:F3} | " +
                        descricao);
                }

                ed.WriteMessage(
                    "\n--------------------------------------------------");

                ed.WriteMessage(
                    $"\nACI encontradas: {quantidadeAci}");

                ed.WriteMessage(
                    $"\nTrue Color/RGB existentes: {quantidadeRgb}");

                if (quantidadeOutros > 0)
                {
                    ed.WriteMessage(
                        $"\nOutros métodos de cor: {quantidadeOutros}");
                }

                if (quantidadeAci == 0)
                {
                    ed.WriteMessage(
                        "\n\nNenhuma cor ACI precisa ser convertida.");

                    tr.Commit();
                    return;
                }

                var pko = new PromptKeywordOptions(
                    "\n\nPreservar as faixas atuais e converter " +
                    "somente as cores ACI para RGB? [Sim/Nao] <Nao>: ");

                pko.AllowNone = true;
                pko.Keywords.Add("Sim");
                pko.Keywords.Add("Nao");

                PromptResult resposta = ed.GetKeywords(pko);

                if (resposta.Status == PromptStatus.Cancel)
                    return;

                bool converter =
                    resposta.Status == PromptStatus.OK &&
                    resposta.StringResult == "Sim";

                if (!converter)
                {
                    ed.WriteMessage(
                        "\nNenhuma alteração realizada.");

                    tr.Commit();
                    return;
                }

                int convertidas = 0;

                for (int i = 0; i < data.Length; i++)
                {
                    AcadColor corOriginal = data[i].Scheme;

                    if (!TryGetAciRgb(
                        corOriginal,
                        out short aci,
                        out byte r,
                        out byte g,
                        out byte b))
                    {
                        // Já é RGB ou outro tipo: não alterar.
                        continue;
                    }

                    AcadColor novaCor = AcadColor.FromRgb(r, g, b);

                    // IMPORTANTE:
                    // altera SOMENTE Scheme. Faixas, ordem e quantidade
                    // permanecem exatamente como estavam.
                    data[i].Scheme = novaCor;

                    convertidas++;

                    ed.WriteMessage(
                        $"\nFaixa {i + 1}: " +
                        $"ACI {aci} -> RGB({r}, {g}, {b})");
                }

                if (convertidas > 0)
                {
                    surface.UpgradeOpen();
                    surface.Analysis.SetElevationData(data);
                }

                tr.Commit();
                ed.Regen();

                ed.WriteMessage(
                    "\n\nConversão concluída.");

                ed.WriteMessage(
                    $"\n{convertidas} cor(es) convertida(s) " +
                    "de ACI para True Color RGB.");

                ed.WriteMessage(
                    "\nAs faixas de elevação foram preservadas.");
            }
        }

        // =====================================================================
        // ACI -> RGB usando a tabela interna do AutoCAD
        // =====================================================================

        private static bool TryGetAciRgb(
            AcadColor color,
            out short aci,
            out byte r,
            out byte g,
            out byte b)
        {
            aci = 0;
            r = 0;
            g = 0;
            b = 0;

            if (color == null)
                return false;

            if (color.ColorMethod != ColorMethod.ByAci)
                return false;

            aci = color.ColorIndex;

            // 0 = ByBlock; 256 = ByLayer. Converte somente ACI fixo 1..255.
            if (aci < 1 || aci > 255)
                return false;

            int rgb = EntityColor.LookUpRgb((byte)aci);

            r = (byte)((rgb >> 16) & 0xFF);
            g = (byte)((rgb >> 8) & 0xFF);
            b = (byte)(rgb & 0xFF);

            return true;
        }

    }
}