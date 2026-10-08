// ============================================================================
// Civil 3D 2026 - Importação de dimensionamento de drenagem a partir do Excel
//
// OBJETIVO
// -------
// Ler a planilha de dimensionamento e atualizar SOMENTE:
//   1) diâmetro interno do tubo de saída (coluna N);
//   2) invert de MONTANTE do tubo de saída (coluna J);
//   3) declividade do tubo de saída (coluna L);
//   4) cota de fundo do PV (Structure.SumpElevation), calculada DEPOIS
//      dos tubos como o MENOR invert dos tubos conectados à estrutura.
//
// NÃO ALTERA:
//   - cota de pavimento / RimElevation;
//   - ajuste automático do topo;
//   - família/material (o tamanho/diâmetro pode mudar dentro da família atual);
//   - estilo;
//   - rede;
//   - posição X/Y;
//   - nome;
//   - End Invert diretamente.
//
// REGRA DO TUBO
// -------------
// - A cota da coluna J é aplicada no endpoint do tubo conectado ao PV montante.
// - Em redes orientadas normalmente, isso corresponde ao Start Invert Elevation.
// - Se o Pipe estiver internamente invertido (PV montante no EndStructure),
//   a rotina usa o End como ponto mantido para não editar a estrutura errada.
// - Depois aplica diretamente a declividade da coluna L usando:
//       SetSlopeHoldStart(...) OU SetSlopeHoldEnd(...)
//   mantendo o invert de montante e deixando o Civil calcular o outro invert.
// - A coluna K (geratriz jusante) NÃO é aplicada.
// - A coluna N é aplicada como diâmetro interno EXATO dentro da família atual.
// - A rotina NÃO troca a família para conseguir outro diâmetro.
//
// IDENTIFICAÇÃO DOS TUBOS
// -----------------------
// - A rede é escolhida UMA ÚNICA VEZ selecionando qualquer tubo ou estrutura.
// - Toda a rede/planilha é processada em uma única execução.
// - Uma estrutura pode ter vários tubos conectados, porém a rotina procura
//   SOMENTE o tubo cujo FLUXO SAI da estrutura atual.
// - A propriedade Pipe.FlowDirection é usada como regra principal:
//       StartToEnd -> sai pela StartStructure;
//       EndToStart -> sai pela EndStructure.
// - Se FlowDirection estiver Bidirectional, a rotina usa a queda atual do
//   invert apenas como fallback para descobrir o sentido hidráulico.
// - Quando existe um próximo PV na planilha, ele é usado como conferência:
//   o único tubo de saída deve chegar exatamente nessa estrutura de jusante.
// - Toda a rede/planilha continua sendo processada em UMA ÚNICA execução.
//
// PLANILHA ESPERADA (conforme imagem fornecida)
// ---------------------------------------------
// Dados a partir da linha 5:
//   A = Trecho
//   B = PV
//   C = Distância entre PVs
//   D = Cota do pavimento
//   J = Cota da geratriz - montante
//   K = Cota da geratriz - jusante (LIDA, MAS NÃO APLICADA)
//   L = Declividade do tubo (decimal: 0.0145 = 1.45%)
//   M = Profundidade do PV
//   N = Diâmetro interno do tubo em mm (APLICADO)
//
// INTERPRETAÇÃO
// -------------
// - "Fundo do PV" = Cota do pavimento - Profundidade do PV.
// - Cota de pavimento e profundidade NÃO controlam mais o SumpElevation.
// - O fundo do PV é função geométrica dos tubos conectados:
//       SumpElevation = menor invert conectado após atualizar todos os tubos.
// - RimElevation não é alterado.
// - Coluna J = geratriz inferior INTERNA/invert de montante.
// - Coluna L = slope que será aplicado diretamente ao Pipe.
// - O tubo de cada linha é o único tubo cujo fluxo sai do PV dessa linha.
//
// COMANDOS
// --------
// DRENEXCEL        -> lê, mostra prévia e aplica após confirmação.
// DRENEXCELPREVIEW -> lê e mostra prévia, sem alterar o desenho.
// DRENEXCELHELP    -> mostra ajuda.
//
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;


using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using Autodesk.Civil.DatabaseServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;


namespace Civil3D2026Plugin.Modules.Drenagem
{
    public class DrenExcelCommands
    {
        // A declividade da planilha é tratada como conferência.
        // As cotas de montante/jusante são prioritárias, pois elas determinam
        // geometricamente a declividade real do tubo no Civil 3D.
        //
        // 0.00050 = 0.05 ponto percentual.
        // Diferenças acima disso aparecem como AVISO, mas não impedem a gravação.
        private const double ToleranciaDeclividade = 0.00050;

        // Tolerância de conferência das cotas.
        private const double ToleranciaCota = 0.002; // 2 mm em desenho em metros
        private const string Versao = C3DVersions.DrenExcel;

        // O método ResizeByInnerDiameterOrWidth recebe unidade do catálogo.
        // Para catálogo métrico padrão do Civil 3D, os tamanhos são normalmente em mm.
        private const double FatorDiametroExcelParaCatalogo = 1.0;

        [CommandMethod(C3DCommands.Drenagem.Excel)]
        public void DrenExcel()
        {
            Executar(aplicar: true);
        }

        [CommandMethod(C3DCommands.Drenagem.ExcelPreview)]
        public void DrenExcelPreview()
        {
            Executar(aplicar: false);
        }

        [CommandMethod(C3DCommands.Drenagem.ExcelHelp)]
        public void DrenExcelHelp()
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Editor ed = doc.Editor;

            ed.WriteMessage("\n");
            ed.WriteMessage($"\n================ {C3DCommands.Drenagem.Excel} HELP ================");
            ed.WriteMessage($"\n{C3DCommands.Drenagem.ExcelPreview}");
            ed.WriteMessage("\n  Lê a planilha e mostra exatamente o que seria alterado.");
            ed.WriteMessage("\n  Não modifica o DWG.");
            ed.WriteMessage("\n");
            ed.WriteMessage($"\n{C3DCommands.Drenagem.Excel}");
            ed.WriteMessage("\n  Lê a planilha, mostra uma prévia e pede confirmação.");
            ed.WriteMessage("\n  Atualiza primeiro TODOS os tubos de saída:");
            ed.WriteMessage("\n  diâmetro, invert de montante e declividade.");
            ed.WriteMessage("\n  Depois calcula o SumpElevation de cada PV pelo MENOR");
            ed.WriteMessage("\n  invert dos tubos conectados à estrutura.");
            ed.WriteMessage("\n  Não altera topo nem End Invert diretamente.");
            ed.WriteMessage("\n");
            ed.WriteMessage("\nFluxo:");
            ed.WriteMessage("\n  1. Escolher o arquivo .xlsx.");
            ed.WriteMessage("\n  2. Selecionar UMA VEZ qualquer tubo ou estrutura da rede alvo.");
            ed.WriteMessage("\n  3. A rotina processa toda a rede/planilha de uma vez.");
            ed.WriteMessage("\n  4. Os PVs são encontrados pelo nome da coluna B.");
            ed.WriteMessage("\n  5. Em cada PV, a rotina escolhe somente o tubo cujo fluxo SAI da estrutura.");
            ed.WriteMessage("\n  6. A prévia é exibida.");
            ed.WriteMessage("\n  7. Somente após confirmação os dados são gravados.");
            ed.WriteMessage("\n");
            ed.WriteMessage("\nRegra de segurança:");
            ed.WriteMessage("\n  Qualquer erro cancela a transação inteira.");
            ed.WriteMessage("\n  A rotina não troca família, material, estilo, nome ou rede.");
            ed.WriteMessage("\n=================================================");
        }

        private static void Executar(bool aplicar)
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            try
            {
                ed.WriteMessage($"\n{C3DCommands.Drenagem.Excel} v{Versao}");

                string? arquivo = SelecionarExcel(ed);
                if (arquivo == null)
                    return;

                List<LinhaPlanilha> linhas = LerPlanilha(arquivo);

                if (linhas.Count == 0)
                {
                    ed.WriteMessage("\nNenhuma linha de PV válida foi encontrada na planilha.");
                    return;
                }

                ObjectId networkId = SelecionarRede(db, ed);
                if (networkId.IsNull)
                    return;

                List<ItemPlano> plano = MontarPlano(db, networkId, linhas);

                MostrarPreview(ed, plano);

                if (!aplicar)
                {
                    ed.WriteMessage("\n\nPREVIEW concluído. Nenhum objeto foi alterado.");
                    return;
                }

                PromptKeywordOptions pko =
                    new PromptKeywordOptions("\nAplicar exatamente estas alterações? [Sim/Nao] <Nao>: ");

                pko.Keywords.Add("Sim");
                pko.Keywords.Add("Nao");
                pko.Keywords.Default = "Nao";
                pko.AllowNone = true;

                PromptResult pr = ed.GetKeywords(pko);

                if (pr.Status != PromptStatus.OK || pr.StringResult != "Sim")
                {
                    ed.WriteMessage("\nOperação cancelada. Nenhum objeto foi alterado.");
                    return;
                }

                AplicarPlano(db, ed, plano);

                ed.WriteMessage($"\n\n{C3DCommands.Drenagem.Excel} concluído com sucesso.");
                ed.WriteMessage($"\nPVs processados: {plano.Count}");
                ed.WriteMessage("\nSomente as propriedades previstas pela rotina foram gravadas.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n\n[{C3DCommands.Drenagem.Excel} - ERRO]");
                ed.WriteMessage($"\n{ex.Message}");
                ed.WriteMessage("\nNenhuma alteração foi confirmada se o erro ocorreu durante a aplicação.");
            }
        }

        private static string? SelecionarExcel(Editor ed)
        {
            PromptOpenFileOptions pfo =
                new PromptOpenFileOptions("\nSelecione a planilha Excel (.xlsx): ");

            pfo.Filter = "Arquivos Excel (*.xlsx)|*.xlsx";

            PromptFileNameResult pfr = ed.GetFileNameForOpen(pfo);

            if (pfr.Status != PromptStatus.OK)
                return null;

            return pfr.StringResult;
        }

        private static ObjectId SelecionarRede(Database db, Editor ed)
        {
            PromptEntityOptions peo =
                new PromptEntityOptions(
                    "\nSelecione UMA VEZ qualquer TUBO ou ESTRUTURA para identificar a rede inteira: ");

            PromptEntityResult per = ed.GetEntity(peo);

            if (per.Status != PromptStatus.OK)
                return ObjectId.Null;

            using Transaction tr = db.TransactionManager.StartTransaction();

            Autodesk.AutoCAD.DatabaseServices.DBObject obj =
                tr.GetObject(per.ObjectId, OpenMode.ForRead);

            if (obj is Pipe pipe)
                return pipe.NetworkId;

            if (obj is Structure structure)
                return structure.NetworkId;

            ed.WriteMessage("\nO objeto selecionado não é um tubo nem uma estrutura de Pipe Network.");
            return ObjectId.Null;
        }

        private static List<LinhaPlanilha> LerPlanilha(string caminho)
        {
            // Faz uma cópia temporária abrindo o arquivo com compartilhamento.
            // Isso permite ler a planilha mesmo quando ela está aberta no Excel
            // ou sendo acompanhada por Google Drive/Drive for Desktop, desde que
            // o outro processo permita leitura compartilhada.
            string temporario = CopiarExcelParaTemporario(caminho);

            try
            {
                return LerPlanilhaXlsxSemBibliotecaExterna(temporario);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporario))
                        File.Delete(temporario);
                }
                catch
                {
                    // Não impede a rotina por falha ao limpar arquivo temporário.
                }
            }
        }

        private static string CopiarExcelParaTemporario(string caminho)
        {
            if (!File.Exists(caminho))
                throw new FileNotFoundException("A planilha selecionada não foi encontrada.", caminho);

            string temporario = Path.Combine(
                Path.GetTempPath(),
                $"DRENEXCEL_{Guid.NewGuid():N}.xlsx");

            try
            {
                using FileStream origem = new FileStream(
                    caminho,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using FileStream destino = new FileStream(
                    temporario,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);

                origem.CopyTo(destino);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    "Não foi possível ler a planilha porque outro processo está bloqueando o arquivo. " +
                    "Feche a planilha no Excel e, se ela estiver no Google Drive, aguarde a sincronização " +
                    "terminar e tente novamente. Detalhe: " + ex.Message,
                    ex);
            }

            return temporario;
        }

        private static List<LinhaPlanilha> LerPlanilhaXlsxSemBibliotecaExterna(string caminho)
        {
            using FileStream fs = new FileStream(
                caminho,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            using ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            List<string> sharedStrings = LerSharedStrings(zip);

            ZipArchiveEntry? worksheetEntry = ObterPrimeiraWorksheet(zip);

            if (worksheetEntry == null)
                throw new InvalidOperationException("Não foi possível localizar a primeira aba da planilha XLSX.");

            XDocument sheetDoc;

            using (Stream stream = worksheetEntry.Open())
                sheetDoc = XDocument.Load(stream);

            XNamespace ns =
                "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            Dictionary<string, string> celulas =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (XElement cell in sheetDoc.Descendants(ns + "c"))
            {
                string? referencia = (string?)cell.Attribute("r");

                if (string.IsNullOrWhiteSpace(referencia))
                    continue;

                celulas[referencia] = ObterValorCelula(cell, ns, sharedStrings);
            }

            int ultimaLinha = celulas.Keys
                .Select(ExtrairNumeroLinha)
                .DefaultIfEmpty(0)
                .Max();

            if (ultimaLinha < 5)
                throw new InvalidOperationException("A planilha não possui dados a partir da linha 5.");

            List<LinhaPlanilha> resultado = new List<LinhaPlanilha>();

            string trechoAtual = string.Empty;

            for (int r = 5; r <= ultimaLinha; r++)
            {
                string trechoNaLinha = ObterCelula(celulas, "A", r).Trim();

                if (!string.IsNullOrWhiteSpace(trechoNaLinha))
                    trechoAtual = trechoNaLinha;

                string pv = ObterCelula(celulas, "B", r).Trim();

                if (string.IsNullOrWhiteSpace(pv))
                    continue;

                double distancia = LerNumero(
                    ObterCelula(celulas, "C", r),
                    r,
                    "C - Distância entre PVs");

                double cotaPavimento = LerNumero(
                    ObterCelula(celulas, "D", r),
                    r,
                    "D - Cota do pavimento");

                double geratrizMontante = LerNumero(
                    ObterCelula(celulas, "J", r),
                    r,
                    "J - Geratriz montante");

                double geratrizJusante = LerNumero(
                    ObterCelula(celulas, "K", r),
                    r,
                    "K - Geratriz jusante");

                double declividade = LerNumero(
                    ObterCelula(celulas, "L", r),
                    r,
                    "L - Declividade");

                double profundidade = LerNumero(
                    ObterCelula(celulas, "M", r),
                    r,
                    "M - Profundidade do PV");

                double diametroMm = LerNumero(
                    ObterCelula(celulas, "N", r),
                    r,
                    "N - Diâmetro");

                if (diametroMm <= 0)
                    throw new InvalidOperationException(
                        $"Linha {r}: o diâmetro precisa ser maior que zero.");

                if (profundidade < 0)
                    throw new InvalidOperationException(
                        $"Linha {r}: a profundidade do PV não pode ser negativa.");

                resultado.Add(new LinhaPlanilha
                {
                    LinhaExcel = r,
                    Trecho = trechoAtual,
                    Pv = pv,
                    DistanciaEntrePvs = distancia,
                    CotaPavimento = cotaPavimento,
                    GeratrizMontante = geratrizMontante,
                    GeratrizJusante = geratrizJusante,
                    Declividade = declividade,
                    ProfundidadePv = profundidade,
                    DiametroMm = diametroMm
                });
            }

            return resultado;
        }

        private static ZipArchiveEntry? ObterPrimeiraWorksheet(ZipArchive zip)
        {
            XNamespace ns =
                "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            XNamespace relNs =
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            XNamespace pkgRelNs =
                "http://schemas.openxmlformats.org/package/2006/relationships";

            ZipArchiveEntry? workbookEntry = zip.GetEntry("xl/workbook.xml");
            ZipArchiveEntry? relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");

            if (workbookEntry == null || relsEntry == null)
                return zip.GetEntry("xl/worksheets/sheet1.xml");

            XDocument workbookDoc;
            XDocument relsDoc;

            using (Stream s = workbookEntry.Open())
                workbookDoc = XDocument.Load(s);

            using (Stream s = relsEntry.Open())
                relsDoc = XDocument.Load(s);

            XElement? primeiraSheet =
                workbookDoc
                    .Descendants(ns + "sheet")
                    .FirstOrDefault();

            if (primeiraSheet == null)
                return null;

            string? relId = (string?)primeiraSheet.Attribute(relNs + "id");

            if (string.IsNullOrWhiteSpace(relId))
                return null;

            XElement? relationship =
                relsDoc
                    .Descendants(pkgRelNs + "Relationship")
                    .FirstOrDefault(x =>
                        string.Equals(
                            (string?)x.Attribute("Id"),
                            relId,
                            StringComparison.Ordinal));

            string? target = (string?)relationship?.Attribute("Target");

            if (string.IsNullOrWhiteSpace(target))
                return null;

            target = target.Replace('\\', '/');

            string entryPath;

            if (target.StartsWith("/", StringComparison.Ordinal))
            {
                entryPath = target.TrimStart('/');
            }
            else
            {
                entryPath = "xl/" + target.TrimStart('/');
            }

            return zip.GetEntry(entryPath)
                ?? zip.GetEntry("xl/worksheets/sheet1.xml");
        }

        private static List<string> LerSharedStrings(ZipArchive zip)
        {
            List<string> resultado = new List<string>();

            ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");

            if (entry == null)
                return resultado;

            XDocument doc;

            using (Stream s = entry.Open())
                doc = XDocument.Load(s);

            XNamespace ns =
                "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            foreach (XElement si in doc.Descendants(ns + "si"))
            {
                string texto = string.Concat(
                    si.Descendants(ns + "t")
                      .Select(x => x.Value));

                resultado.Add(texto);
            }

            return resultado;
        }

        private static string ObterValorCelula(
            XElement cell,
            XNamespace ns,
            List<string> sharedStrings)
        {
            string tipo = ((string?)cell.Attribute("t") ?? string.Empty).Trim();

            if (tipo == "inlineStr")
            {
                return string.Concat(
                    cell.Descendants(ns + "t")
                        .Select(x => x.Value));
            }

            string valor = cell.Element(ns + "v")?.Value ?? string.Empty;

            if (tipo == "s")
            {
                if (int.TryParse(
                        valor,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int indice)
                    &&
                    indice >= 0 &&
                    indice < sharedStrings.Count)
                {
                    return sharedStrings[indice];
                }

                return string.Empty;
            }

            if (tipo == "b")
                return valor == "1" ? "TRUE" : "FALSE";

            return valor;
        }

        private static string ObterCelula(
            Dictionary<string, string> celulas,
            string coluna,
            int linha)
        {
            return celulas.TryGetValue(
                coluna + linha.ToString(CultureInfo.InvariantCulture),
                out string? valor)
                ? valor
                : string.Empty;
        }

        private static int ExtrairNumeroLinha(string referencia)
        {
            string digitos = new string(
                referencia
                    .Where(char.IsDigit)
                    .ToArray());

            if (int.TryParse(
                    digitos,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int linha))
            {
                return linha;
            }

            return 0;
        }

        private static double LerNumero(
            string texto,
            int linha,
            string campo)
        {
            texto = (texto ?? string.Empty).Trim();

            // O conteúdo numérico bruto de um XLSX é gravado com ponto decimal,
            // independentemente do idioma do Excel.
            if (double.TryParse(
                    texto,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double valor))
            {
                return valor;
            }

            // Fallback para alguma planilha que tenha número armazenado como texto.
            if (double.TryParse(
                    texto,
                    NumberStyles.Any,
                    CultureInfo.GetCultureInfo("pt-BR"),
                    out valor))
            {
                return valor;
            }

            throw new InvalidOperationException(
                $"Linha {linha}: não foi possível ler '{campo}'. Valor encontrado: '{texto}'.");
        }

        private static List<ItemPlano> MontarPlano(
            Database db,
            ObjectId networkId,
            List<LinhaPlanilha> linhas)
        {
            List<ItemPlano> plano = new List<ItemPlano>();

            using Transaction tr = db.TransactionManager.StartTransaction();

            Network network = (Network)tr.GetObject(networkId, OpenMode.ForRead);

            Dictionary<string, ObjectId> estruturas =
                new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in network.GetStructureIds())
            {
                Structure s = (Structure)tr.GetObject(id, OpenMode.ForRead);

                if (estruturas.ContainsKey(s.Name))
                {
                    throw new InvalidOperationException(
                        $"Existem duas estruturas chamadas '{s.Name}' na rede selecionada. " +
                        "A rotina não pode decidir qual deve ser usada.");
                }

                estruturas.Add(s.Name, id);
            }

            List<ObjectId> pipeIds = network.GetPipeIds().Cast<ObjectId>().ToList();

            HashSet<ObjectId> tubosJaUsados = new HashSet<ObjectId>();

            for (int i = 0; i < linhas.Count; i++)
            {
                LinhaPlanilha dados = linhas[i];

                if (!estruturas.TryGetValue(dados.Pv, out ObjectId estruturaMontanteId))
                {
                    throw new InvalidOperationException(
                        $"Linha {dados.LinhaExcel}: não foi encontrada, na rede selecionada, " +
                        $"uma estrutura com nome exatamente igual a '{dados.Pv}'.");
                }

                Structure estruturaMontante =
                    (Structure)tr.GetObject(estruturaMontanteId, OpenMode.ForRead);

                if (estruturaMontante.IsReferenceObject)
                {
                    throw new InvalidOperationException(
                        $"PV '{dados.Pv}' é um objeto de referência e não pode ser editado.");
                }

                ObjectId estruturaJusanteEsperadaId = ObjectId.Null;

                // Quando existe próxima linha no MESMO TRECHO, ela é a estrutura
                // de jusante esperada. A identificação do tubo passa a ser pelo
                // par exato de estruturas, e não por direção Start/End do Pipe.
                if (i + 1 < linhas.Count && MesmoTrecho(dados, linhas[i + 1]))
                {
                    string nomeJusanteEsperado = linhas[i + 1].Pv;

                    if (!estruturas.TryGetValue(nomeJusanteEsperado, out estruturaJusanteEsperadaId))
                    {
                        throw new InvalidOperationException(
                            $"Linha {dados.LinhaExcel}: a estrutura de jusante esperada " +
                            $"'{nomeJusanteEsperado}' não foi encontrada na rede selecionada.");
                    }
                }


                // A estrutura pode ter vários tubos conectados.
                // A regra correta é: existe somente UM tubo cujo fluxo SAI
                // desta estrutura. O próximo PV da planilha é usado apenas
                // como conferência da estrutura de jusante.
                ObjectId tuboId = EncontrarUnicoTuboQueSaiDaEstrutura(
                    tr,
                    pipeIds,
                    estruturaMontanteId,
                    estruturaJusanteEsperadaId,
                    dados);

                Pipe tuboSelecionado =
                    (Pipe)tr.GetObject(tuboId, OpenMode.ForRead);

                ObjectId estruturaJusanteRealId =
                    ObterOutraEstrutura(tuboSelecionado, estruturaMontanteId);

                if (!tubosJaUsados.Add(tuboId))
                {
                    throw new InvalidOperationException(
                        $"O mesmo tubo foi identificado para mais de uma linha da planilha. " +
                        $"Problema detectado na linha {dados.LinhaExcel} ({dados.Pv}).");
                }

                Pipe tubo = (Pipe)tr.GetObject(tuboId, OpenMode.ForRead);

                if (tubo.IsReferenceObject)
                {
                    throw new InvalidOperationException(
                        $"O tubo de saída do PV '{dados.Pv}' é um objeto de referência e não pode ser editado.");
                }

                bool pvMontanteNoStart;

                if (tubo.StartStructureId == estruturaMontanteId)
                    pvMontanteNoStart = true;
                else if (tubo.EndStructureId == estruturaMontanteId)
                    pvMontanteNoStart = false;
                else
                    throw new InvalidOperationException(
                        $"Erro interno: o tubo '{tubo.Name}' não está conectado ao PV '{dados.Pv}'.");

                string nomeJusante = "(sem estrutura)";

                if (!estruturaJusanteRealId.IsNull)
                {
                    Structure estruturaJusante =
                        (Structure)tr.GetObject(estruturaJusanteRealId, OpenMode.ForRead);

                    nomeJusante = estruturaJusante.Name;
                }

                double fundoCalculado =
                    dados.CotaPavimento - dados.ProfundidadePv;

                plano.Add(new ItemPlano
                {
                    Dados = dados,

                    EstruturaId = estruturaMontanteId,
                    EstruturaJusanteId = estruturaJusanteRealId,
                    EstruturaJusanteNome = nomeJusante,

                    TuboId = tuboId,
                    TuboNome = tubo.Name,

                    PvNoStartDoTubo = pvMontanteNoStart,

                    FundoPv = fundoCalculado,
                    FundoMenosGeratrizMontante =
                        fundoCalculado - dados.GeratrizMontante,

                    // Estado original usado para garantir que a rotina não
                    // troque topologia, família, rede ou posição em planta.
                    TuboStartOriginal = tubo.StartPoint,
                    TuboEndOriginal = tubo.EndPoint,
                    TuboStartStructureOriginal = tubo.StartStructureId,
                    TuboEndStructureOriginal = tubo.EndStructureId,
                    TuboPartFamilyOriginal = tubo.PartFamilyId,
                    TuboNetworkOriginal = tubo.NetworkId,

                    EstruturaPosicaoOriginal = estruturaMontante.Position,
                    EstruturaPartFamilyOriginal = estruturaMontante.PartFamilyId,
                    EstruturaNetworkOriginal = estruturaMontante.NetworkId,

                    EstruturaAutomaticRimOriginal =
                        estruturaMontante.AutomaticRimSurfaceAdjustment,

                    EstruturaSurfaceAdjustmentOriginal =
                        estruturaMontante.SurfaceAdjustmentValue,

                    EstruturaRefSurfaceOriginal =
                        estruturaMontante.RefSurfaceId,

                    EstruturaControlSumpOriginal =
                        estruturaMontante.ControlSumpBy,

                    EstruturaRimOriginal =
                        estruturaMontante.RimElevation,

                    EstruturaSumpOriginal =
                        estruturaMontante.SumpElevation,

                    ComprimentoExcel =
                        dados.DistanciaEntrePvs,

                    ComprimentoCivilCentroACentro =
                        tubo.Length2DCenterToCenter,

                    DeclividadePrevistaPelasCotas =
                        tubo.Length2DCenterToCenter > 1e-9
                            ? Math.Abs(
                                dados.GeratrizMontante -
                                dados.GeratrizJusante)
                              / tubo.Length2DCenterToCenter
                            : 0.0
                });
            }

            return plano;
        }

        private static bool MesmoTrecho(LinhaPlanilha a, LinhaPlanilha b)
        {
            return string.Equals(
                a.Trecho ?? string.Empty,
                b.Trecho ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        private static ObjectId EncontrarUnicoTuboQueSaiDaEstrutura(
            Transaction tr,
            List<ObjectId> pipeIds,
            ObjectId estruturaAtualId,
            ObjectId estruturaJusanteEsperadaId,
            LinhaPlanilha dados)
        {
            List<ObjectId> conectados = new List<ObjectId>();
            List<ObjectId> saidas = new List<ObjectId>();

            foreach (ObjectId pipeId in pipeIds)
            {
                Pipe p = (Pipe)tr.GetObject(pipeId, OpenMode.ForRead);

                bool conectado =
                    p.StartStructureId == estruturaAtualId ||
                    p.EndStructureId == estruturaAtualId;

                if (!conectado)
                    continue;

                conectados.Add(pipeId);

                if (TuboSaiDaEstrutura(p, estruturaAtualId))
                    saidas.Add(pipeId);
            }

            if (conectados.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Linha {dados.LinhaExcel}: o PV '{dados.Pv}' não possui tubos conectados.");
            }

            if (saidas.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Linha {dados.LinhaExcel} - PV '{dados.Pv}': existem " +
                    $"{conectados.Count} tubos conectados, porém nenhum foi identificado " +
                    "como tubo de SAÍDA pelo FlowDirection do Civil 3D. " +
                    "Nenhuma alteração foi feita.");
            }

            if (saidas.Count > 1)
            {
                string detalhes = string.Join(
                    "; ",
                    saidas.Select(id =>
                    {
                        Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForRead);
                        ObjectId outra = ObterOutraEstrutura(p, estruturaAtualId);
                        string nomeOutra = ObterNomeEstrutura(tr, outra);
                        return $"{p.Name} -> {nomeOutra} [{p.FlowDirection}]";
                    }));

                throw new InvalidOperationException(
                    $"Linha {dados.LinhaExcel} - PV '{dados.Pv}': foram encontrados " +
                    $"{saidas.Count} tubos cujo fluxo SAI da estrutura, mas a regra da rotina " +
                    $"exige apenas um. Saídas encontradas: {detalhes}. " +
                    "Nenhuma alteração foi feita.");
            }

            ObjectId tuboId = saidas[0];
            Pipe tubo = (Pipe)tr.GetObject(tuboId, OpenMode.ForRead);

            // Quando a planilha possui um próximo PV no mesmo trecho,
            // a saída hidráulica encontrada precisa chegar exatamente nele.
            if (!estruturaJusanteEsperadaId.IsNull)
            {
                ObjectId jusanteRealId =
                    ObterOutraEstrutura(tubo, estruturaAtualId);

                if (jusanteRealId != estruturaJusanteEsperadaId)
                {
                    string nomeJusanteEsperado =
                        ObterNomeEstrutura(tr, estruturaJusanteEsperadaId);

                    string nomeJusanteReal =
                        ObterNomeEstrutura(tr, jusanteRealId);

                    throw new InvalidOperationException(
                        $"Linha {dados.LinhaExcel} - PV '{dados.Pv}': o único tubo de saída " +
                        $"é '{tubo.Name}' e vai para '{nomeJusanteReal}', porém a próxima " +
                        $"estrutura da planilha é '{nomeJusanteEsperado}'. " +
                        "A rotina não irá adivinhar nem trocar conexões.");
                }
            }

            return tuboId;
        }

        private static bool TuboSaiDaEstrutura(
            Pipe pipe,
            ObjectId estruturaAtualId)
        {
            bool estruturaNoStart =
                pipe.StartStructureId == estruturaAtualId;

            bool estruturaNoEnd =
                pipe.EndStructureId == estruturaAtualId;

            if (!estruturaNoStart && !estruturaNoEnd)
                return false;

            // Regra principal: direção de fluxo armazenada pelo próprio Civil 3D.
            if (pipe.FlowDirection == FlowDirectionType.StartToEnd)
                return estruturaNoStart;

            if (pipe.FlowDirection == FlowDirectionType.EndToStart)
                return estruturaNoEnd;

            // Fallback somente para FlowDirection == Bidirectional:
            // considera que o tubo sai da estrutura que está no invert mais alto.
            if (pipe.FlowDirection == FlowDirectionType.Bidirectional)
            {
                double meiaAlturaInterna = pipe.InnerHeight * 0.5;

                double invertStart =
                    pipe.StartPoint.Z - meiaAlturaInterna;

                double invertEnd =
                    pipe.EndPoint.Z - meiaAlturaInterna;

                if (estruturaNoStart)
                    return invertStart > invertEnd + ToleranciaCota;

                if (estruturaNoEnd)
                    return invertEnd > invertStart + ToleranciaCota;
            }

            return false;
        }

        private static string ObterNomeEstrutura(
            Transaction tr,
            ObjectId estruturaId)
        {
            if (estruturaId.IsNull)
                return "(sem estrutura)";

            Structure s =
                (Structure)tr.GetObject(estruturaId, OpenMode.ForRead);

            return s.Name;
        }

        private static ObjectId ObterOutraEstrutura(
            Pipe pipe,
            ObjectId umaEstrutura)
        {
            if (pipe.StartStructureId == umaEstrutura)
                return pipe.EndStructureId;

            if (pipe.EndStructureId == umaEstrutura)
                return pipe.StartStructureId;

            return ObjectId.Null;
        }

        private static void MostrarPreview(Editor ed, List<ItemPlano> plano)
        {
            ed.WriteMessage("\n");
            ed.WriteMessage("\n================ DRENEXCEL - PRÉVIA ================");
            ed.WriteMessage($"\nPVs/tubos que serão processados de uma vez: {plano.Count}");
            ed.WriteMessage("\nOrdem: TODOS OS TUBOS -> depois SumpElevation dos PVs.");
            ed.WriteMessage("\nSump final = menor invert conectado após a atualização dos tubos.");

            foreach (ItemPlano item in plano)
            {
                LinhaPlanilha d = item.Dados;

                ed.WriteMessage($"\n\nLinha {d.LinhaExcel} | {d.Pv}");
                ed.WriteMessage(
                    $"\n  Saída hidráulica:      {d.Pv} -> {item.EstruturaJusanteNome}");

                ed.WriteMessage($"\n  Tubo:                  {item.TuboNome}");
                ed.WriteMessage(
                    $"\n  Fundo da planilha ref.: {item.FundoPv:F3} (apenas referência)");
                ed.WriteMessage(
                    "\n  Sump final:             será calculado pelo menor invert conectado");

                if (item.PvNoStartDoTubo)
                {
                    ed.WriteMessage(
                        $"\n  Start Invert desej.:   {d.GeratrizMontante:F3}");
                    ed.WriteMessage(
                        $"\n  Slope será aplicado:   {Math.Abs(d.Declividade):F5} " +
                        $"({Math.Abs(d.Declividade) * 100.0:F3}%) mantendo START.");
                }
                else
                {
                    ed.WriteMessage(
                        $"\n  [ORIENTAÇÃO] O PV montante está no END do objeto Pipe.");
                    ed.WriteMessage(
                        $"\n  End Invert montante:   {d.GeratrizMontante:F3}");
                    ed.WriteMessage(
                        $"\n  Slope será aplicado:   {Math.Abs(d.Declividade):F5} " +
                        $"({Math.Abs(d.Declividade) * 100.0:F3}%) mantendo END.");
                }

                ed.WriteMessage(
                    $"\n  Coluna K ({d.GeratrizJusante:F3}) será IGNORADA; " +
                    "o outro invert será calculado pelo Civil.");

                ed.WriteMessage(
                    $"\n  Diâmetro interno:      {d.DiametroMm:F0} mm (será aplicado exatamente)");
            }

            ed.WriteMessage("\n\n=====================================================");
        }

        private static void AplicarPlano(
            Database db,
            Editor ed,
            List<ItemPlano> plano)
        {
            using Transaction tr = db.TransactionManager.StartTransaction();

            // ================================================================
            // 1) TUBOS
            // ================================================================
            //
            // Primeiro aplica o diâmetro, depois fixa o invert no PV montante
            // e por fim aplica a declividade informada na planilha.
            // O endpoint oposto fica livre para o Civil calcular.
            //
            // ORDEM IMPORTANTE:
            //   DIÂMETRO -> INVERT MONTANTE -> SLOPE
            //
            // O resize muda a altura interna do tubo; portanto o invert só é
            // calculado depois que o tamanho correto já estiver aplicado.
            foreach (ItemPlano item in plano)
            {
                LinhaPlanilha d = item.Dados;

                Pipe pipe =
                    (Pipe)tr.GetObject(item.TuboId, OpenMode.ForWrite);

                // ------------------------------------------------------------
                // DIÂMETRO
                // ------------------------------------------------------------
                // ResizeByInnerDiameterOrWidth recebe a unidade do catálogo.
                // Em catálogo métrico padrão do Civil 3D, os tamanhos são em mm.
                // useClosestSize=false: exige exatamente o diâmetro da planilha.
                // Não troca família/material.
                try
                {
                    pipe.ResizeByInnerDiameterOrWidth(
                        d.DiametroMm * FatorDiametroExcelParaCatalogo,
                        useClosestSize: false);
                }
                catch (System.Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Tubo '{pipe.Name}' / PV '{d.Pv}': não foi possível aplicar " +
                        $"o diâmetro exato de {d.DiametroMm:F0} mm dentro da família atual. " +
                        "A rotina não trocará família/material. " +
                        $"Detalhe: {ex.Message}");
                }

                // O diâmetro altera a altura interna do tubo. Por isso a meia
                // altura precisa ser obtida DEPOIS do resize, antes de calcular
                // a elevação do eixo correspondente ao invert.
                double meiaAlturaInterna = pipe.InnerHeight * 0.5;

                if (meiaAlturaInterna <= 0)
                {
                    throw new InvalidOperationException(
                        $"Tubo '{pipe.Name}': altura interna inválida.");
                }

                double invertMontanteDesejado =
                    d.GeratrizMontante;

                double slopeDesejado =
                    Math.Abs(d.Declividade);

                if (slopeDesejado < 0)
                    slopeDesejado = -slopeDesejado;

                Point3d sp = pipe.StartPoint;
                Point3d ep = pipe.EndPoint;

                if (item.PvNoStartDoTubo)
                {
                    // START INVERT = eixo Start.Z - InnerHeight/2
                    pipe.StartPoint = new Point3d(
                        sp.X,
                        sp.Y,
                        invertMontanteDesejado + meiaAlturaInterna);

                    // A declividade da tabela representa queda no sentido do
                    // fluxo. Partindo do START montante, a inclinação geométrica
                    // start->end é negativa.
                    pipe.SetSlopeHoldStart(-slopeDesejado);
                }
                else
                {
                    // Caso o objeto Pipe esteja internamente orientado ao
                    // contrário, o PV montante está no EndStructure.
                    // Mantemos o endpoint hidráulico de montante, para não
                    // aplicar a cota na estrutura errada.
                    pipe.EndPoint = new Point3d(
                        ep.X,
                        ep.Y,
                        invertMontanteDesejado + meiaAlturaInterna);

                    // A inclinação é aplicada "away from End", portanto também
                    // negativa para descer a partir do PV montante.
                    pipe.SetSlopeHoldEnd(-slopeDesejado);
                }

                // ------------------------------------------------------------
                // CONFERÊNCIA DO INVERT DE MONTANTE
                // ------------------------------------------------------------
                double invertMontanteObtido =
                    ObterInvertNoPv(pipe, item.PvNoStartDoTubo);

                if (Math.Abs(
                        invertMontanteObtido -
                        invertMontanteDesejado) > ToleranciaCota)
                {
                    throw new InvalidOperationException(
                        $"Tubo '{pipe.Name}' / PV '{d.Pv}': o invert de montante " +
                        $"não ficou na cota solicitada. " +
                        $"Solicitado={invertMontanteDesejado:F3}; " +
                        $"obtido={invertMontanteObtido:F3}.");
                }

                // ------------------------------------------------------------
                // CONFERÊNCIA DA DECLIVIDADE
                // ------------------------------------------------------------
                double slopeObtido =
                    Math.Abs(pipe.Slope);

                if (Math.Abs(slopeObtido - slopeDesejado) > 0.000001)
                {
                    throw new InvalidOperationException(
                        $"Tubo '{pipe.Name}' / PV '{d.Pv}': a declividade não ficou " +
                        $"no valor solicitado. " +
                        $"Solicitado={slopeDesejado:F6}; obtido={slopeObtido:F6}.");
                }

                // ------------------------------------------------------------
                // NÃO ALTERAR MAIS NADA
                // ------------------------------------------------------------
                if (pipe.NetworkId != item.TuboNetworkOriginal ||
                    pipe.PartFamilyId != item.TuboPartFamilyOriginal ||
                    pipe.StartStructureId != item.TuboStartStructureOriginal ||
                    pipe.EndStructureId != item.TuboEndStructureOriginal)
                {
                    throw new InvalidOperationException(
                        $"Tubo '{pipe.Name}': rede, família ou conexões mudaram " +
                        "durante a edição. A operação inteira foi cancelada.");
                }

                if (Math.Abs(pipe.StartPoint.X - item.TuboStartOriginal.X) > 1e-9 ||
                    Math.Abs(pipe.StartPoint.Y - item.TuboStartOriginal.Y) > 1e-9 ||
                    Math.Abs(pipe.EndPoint.X - item.TuboEndOriginal.X) > 1e-9 ||
                    Math.Abs(pipe.EndPoint.Y - item.TuboEndOriginal.Y) > 1e-9)
                {
                    throw new InvalidOperationException(
                        $"Tubo '{pipe.Name}': a posição X/Y mudou. " +
                        "A operação inteira foi cancelada.");
                }

                double invertOposto =
                    ObterInvertNoPv(pipe, !item.PvNoStartDoTubo);

                ed.WriteMessage(
                    $"\n{d.Pv}: diâmetro aplicado={d.DiametroMm:F0} mm; " +
                    $"invert montante={invertMontanteObtido:F3}; " +
                    $"slope={slopeObtido:F5}; " +
                    $"invert oposto calculado={invertOposto:F3}.");
            }

            // ================================================================
            // 2) ESTRUTURAS - SUMP ELEVATION COMO FUNÇÃO DOS TUBOS
            // ================================================================
            //
            // IMPORTANTE:
            // TODOS os tubos já foram redimensionados e reposicionados acima.
            // Só agora calculamos o fundo de cada PV.
            //
            // Regra:
            //   SumpElevation = MENOR invert entre TODOS os tubos conectados.
            //
            // Isso evita exatamente o caso em que o Excel usa comprimento
            // arredondado e o End Invert calculado pelo Civil fica alguns
            // milímetros abaixo do Start Invert do tubo seguinte.
            ObjectId networkId =
                plano.Count > 0
                    ? plano[0].EstruturaNetworkOriginal
                    : ObjectId.Null;

            List<ObjectId> todosTubosDaRede = new List<ObjectId>();

            if (!networkId.IsNull)
            {
                Network network =
                    (Network)tr.GetObject(networkId, OpenMode.ForRead);

                todosTubosDaRede =
                    network.GetPipeIds().Cast<ObjectId>().ToList();
            }

            foreach (ItemPlano item in plano)
            {
                LinhaPlanilha d = item.Dados;

                Structure s =
                    (Structure)tr.GetObject(item.EstruturaId, OpenMode.ForWrite);

                double menorInvert =
                    ObterMenorInvertConectado(
                        tr,
                        todosTubosDaRede,
                        item.EstruturaId);

                if (double.IsNaN(menorInvert) ||
                    double.IsInfinity(menorInvert))
                {
                    throw new InvalidOperationException(
                        $"PV '{d.Pv}': não foi possível calcular o menor invert " +
                        "dos tubos conectados.");
                }

                if (s.ControlSumpBy != StructureControlSumpType.ByElevation)
                    s.ControlSumpBy = StructureControlSumpType.ByElevation;

                s.SumpElevation = menorInvert;

                double sumpObtido =
                    s.SumpElevation;

                if (Math.Abs(sumpObtido - menorInvert) > ToleranciaCota)
                {
                    throw new InvalidOperationException(
                        $"PV '{d.Pv}': SumpElevation não acompanhou o menor invert conectado. " +
                        $"Menor invert={menorInvert:F3}; Sump obtido={sumpObtido:F3}.");
                }

                // A cota calculada pela planilha fica apenas como diagnóstico.
                double diferencaPlanilha =
                    sumpObtido - item.FundoPv;

                if (Math.Abs(diferencaPlanilha) > ToleranciaCota)
                {
                    ed.WriteMessage(
                        $"\n[INFO] {d.Pv}: fundo planilha={item.FundoPv:F3}; " +
                        $"menor invert real={sumpObtido:F3}; " +
                        $"diferença={diferencaPlanilha:+0.000;-0.000;0.000} m. " +
                        "Prevaleceu a geometria real dos tubos.");
                }

                // Não mexer em topo, rede, família ou posição X/Y.
                if (s.NetworkId != item.EstruturaNetworkOriginal ||
                    s.PartFamilyId != item.EstruturaPartFamilyOriginal)
                {
                    throw new InvalidOperationException(
                        $"PV '{d.Pv}': rede ou família da estrutura mudou. " +
                        "A operação inteira foi cancelada.");
                }

                Point3d posAtual = s.Position;

                if (Math.Abs(posAtual.X - item.EstruturaPosicaoOriginal.X) > 1e-9 ||
                    Math.Abs(posAtual.Y - item.EstruturaPosicaoOriginal.Y) > 1e-9)
                {
                    throw new InvalidOperationException(
                        $"PV '{d.Pv}': a posição X/Y da estrutura mudou. " +
                        "A operação inteira foi cancelada.");
                }

                if (Math.Abs(s.RimElevation - item.EstruturaRimOriginal) > ToleranciaCota)
                {
                    throw new InvalidOperationException(
                        $"PV '{d.Pv}': a cota de topo foi alterada pelo Civil. " +
                        "A operação inteira foi cancelada.");
                }

                ed.WriteMessage(
                    $"\n{d.Pv}: SumpElevation={sumpObtido:F3} " +
                    "(menor invert conectado).");
            }

            tr.Commit();
        }

        private static double ObterMenorInvertConectado(
            Transaction tr,
            List<ObjectId> pipeIds,
            ObjectId estruturaId)
        {
            double menor = double.PositiveInfinity;
            bool encontrou = false;

            foreach (ObjectId pipeId in pipeIds)
            {
                Pipe pipe =
                    (Pipe)tr.GetObject(pipeId, OpenMode.ForRead);

                if (pipe.StartStructureId == estruturaId)
                {
                    double inv =
                        ObterInvertNoPv(pipe, endpointStart: true);

                    if (inv < menor)
                        menor = inv;

                    encontrou = true;
                }

                if (pipe.EndStructureId == estruturaId)
                {
                    double inv =
                        ObterInvertNoPv(pipe, endpointStart: false);

                    if (inv < menor)
                        menor = inv;

                    encontrou = true;
                }
            }

            return encontrou
                ? menor
                : double.NaN;
        }

        private static double ObterInvertNoPv(Pipe pipe, bool endpointStart)
        {
            double meiaAlturaInterna = pipe.InnerHeight * 0.5;

            return endpointStart
                ? pipe.StartPoint.Z - meiaAlturaInterna
                : pipe.EndPoint.Z - meiaAlturaInterna;
        }

        private sealed class LinhaPlanilha
        {
            public int LinhaExcel { get; set; }
            public string Trecho { get; set; } = string.Empty;
            public string Pv { get; set; } = string.Empty;

            public double DistanciaEntrePvs { get; set; }
            public double CotaPavimento { get; set; }
            public double GeratrizMontante { get; set; }
            public double GeratrizJusante { get; set; }
            public double Declividade { get; set; }
            public double ProfundidadePv { get; set; }
            public double DiametroMm { get; set; }
        }

        private sealed class ItemPlano
        {
            public LinhaPlanilha Dados { get; set; } = null!;

            public ObjectId EstruturaId { get; set; }
            public ObjectId EstruturaJusanteId { get; set; }
            public string EstruturaJusanteNome { get; set; } = string.Empty;

            public ObjectId TuboId { get; set; }

            public string TuboNome { get; set; } = string.Empty;

            public bool PvNoStartDoTubo { get; set; }

            public double FundoPv { get; set; }

            public double FundoMenosGeratrizMontante { get; set; }

            // Estado original para validação de "não alterar mais nada".
            public Point3d TuboStartOriginal { get; set; }
            public Point3d TuboEndOriginal { get; set; }
            public ObjectId TuboStartStructureOriginal { get; set; }
            public ObjectId TuboEndStructureOriginal { get; set; }
            public ObjectId TuboPartFamilyOriginal { get; set; }
            public ObjectId TuboNetworkOriginal { get; set; }

            public Point3d EstruturaPosicaoOriginal { get; set; }
            public ObjectId EstruturaPartFamilyOriginal { get; set; }
            public ObjectId EstruturaNetworkOriginal { get; set; }

            public bool EstruturaAutomaticRimOriginal { get; set; }
            public double EstruturaSurfaceAdjustmentOriginal { get; set; }
            public ObjectId EstruturaRefSurfaceOriginal { get; set; }
            public StructureControlSumpType EstruturaControlSumpOriginal { get; set; }
            public double EstruturaRimOriginal { get; set; }
            public double EstruturaSumpOriginal { get; set; }

            // Diagnóstico Excel x geometria real da rede.
            public double ComprimentoExcel { get; set; }
            public double ComprimentoCivilCentroACentro { get; set; }
            public double DeclividadePrevistaPelasCotas { get; set; }
        }
    }
}
